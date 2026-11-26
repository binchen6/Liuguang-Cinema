using System.Text;
using TV.Models;
using TV.Services.Http;
using TV.Services.Cloud;
using TV.Services.Vod;

namespace TV.Services.Playback;

public sealed class PlaybackResolver(AppServices services)
{
    public Task<PlaybackRequest> ResolveAsync(PlaybackRequest source, QualityOption quality, CancellationToken token) =>
        ResolveAsync(source, quality, getHdrOutput: null, token);

    internal Task<PlaybackRequest> ResolveForDownloadAsync(PlaybackRequest source, CancellationToken token) =>
        ResolveAsync(source, new QualityOption("原画", -1), getHdrOutput: null, token, forDownload: true);

    internal async Task<PlaybackRequest> ResolveAsync(PlaybackRequest source, QualityOption quality,
        Func<CancellationToken, Task<bool>>? getHdrOutput, CancellationToken token, bool forDownload = false)
    {
        var request = source.Copy();
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var input) && input.IsFile)
        {
            request.HasFixedVideoTrack = true;
            return request;
        }
        var config = services.Config.Current;
        var site = config?.Sites.FirstOrDefault(item => item.Key == request.SiteKey);
        var flag = request.Lines.ElementAtOrDefault(request.LineIndex)?.Name ?? string.Empty;
        string? contentType = null;
        var useHttpProxy = false;
        var unwrapPngHlsSegments = false;
        if (CloudShareLink.TryParse(request.Url, out _))
        {
            var share = await services.Cloud.GetShareAsync(request.Url, token);
            var file = share.Files.FirstOrDefault() ?? throw new InvalidDataException("分享中没有可播放的媒体文件。");
            request.Url = CloudDriveService.GetPlaybackId(file);
            input = new Uri(request.Url);
        }
        if (request.Url.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
        {
            var originalLine = flag.Contains("原画", StringComparison.Ordinal) || flag.StartsWith("夸父原", StringComparison.Ordinal);
            var cloudQuality = quality.Height == 0 && originalLine ? new QualityOption("原画", -1) : quality;
            if (cloudQuality.IsSpecific && !cloudQuality.RequestsOriginal && input?.Host is "ali" or "baidu")
                throw new NotSupportedException($"该网盘未提供 {quality.Name}。");
            CloudPlayResult cloud;
            try
            {
                cloud = forDownload ? await services.Cloud.ResolveAsync(request.Url, "原画", token)
                    : await services.Cloud.ResolveAsync(request.Url, cloudQuality, token);
            }
            catch (InvalidOperationException ex) when (!forDownload && input?.Host == "quark" && quality.Name == "自动" && cloudQuality.RequestsOriginal &&
                ex.Message.StartsWith("原画地址获取失败", StringComparison.Ordinal))
            {
                cloudQuality = quality;
                cloud = await services.Cloud.ResolveAsync(request.Url, cloudQuality, token);
            }
            request.Url = cloud.Url;
            request.CloudSubtitles = cloud.SubtitleTracks ?? [];
            request.AvailableQualities = cloud.AvailableQualities;
            request.ResolvedQuality = cloud.ResolvedQuality;
            request.Notice = cloud.Notice;
            request.HasFixedVideoTrack = (cloud.ResolvedQuality?.RequestsOriginal == true ||
                cloudQuality.RequestsOriginal && cloud.AvailableVideoHeights is not { Count: > 0 } &&
                cloud.ResolvedQuality is null)
                || input?.Host is "ali" or "baidu";
            if (cloud.AvailableVideoHeights is { Count: > 0 } heights)
            {
                request.AvailableVideoHeights = heights;
                request.UsesProviderVideoHeights = true;
            }
            // The default MP4 and adaptive demuxers can fault when stopping a retimed seek on Quark/UC media.
            request.UseAvformatDemuxer = input?.Host is "quark" or "uc" &&
                (cloud.ContentType == "video/mp4" || HlsManifest.IsPlaylist(cloud.Url, cloud.ContentType));
            if (input?.Host == "ali") request.AvailableVideoHeights = new HashSet<int>();
            request.Headers.Clear();
            return request;
        }

        if (site?.Type is 3 or 4)
        {
            var result = await services.Vod.ResolvePlayAsync(site, flag, request.Url, quality, request.Item?.CloudProvider, forDownload, token);
            if (result.Subtitles is not null) request.Subtitles = request.Subtitles.Concat(result.Subtitles).Distinct().ToList();
            if (result.AudioTracks is not null) request.AudioTracks = request.AudioTracks.Concat(result.AudioTracks).Distinct().ToList();
            request.Danmaku = result.Danmaku ?? request.Danmaku;
            if (result.ReplaceHeaders) request.Headers.Clear();
            foreach (var header in result.Headers) request.Headers[header.Key] = header.Value;
            request.Url = result.Url;
            contentType = result.ContentType;
            useHttpProxy = result.UseHttpProxy;
            unwrapPngHlsSegments = result.UnwrapPngHlsSegments;
            request.SeekOnAudioTrackChange = result.SeekOnAudioTrackChange;
            if (result.ProviderQualities is { } options)
            {
                request.AvailableQualities = options;
                request.ResolvedQuality = result.ResolvedQuality;
                request.UsesProviderVideoHeights = true;
            }
            if (result.ProviderVideoHeights is { } heights)
            {
                request.AvailableVideoHeights = heights;
                request.UsesProviderVideoHeights = true;
            }
            if ((result.Parse == 1 || result.Jx == 1) && !PlayUrlResolver.IsDirectMediaUrl(request.Url))
            {
                var userAgent = result.BrowserFailureMessage is null ? request.Headers.GetValueOrDefault("User-Agent") : null;
                var sniffed = result.Jx == 1 ? null : await services.Sniffer.SniffAsync(request.Url, userAgent, token, result.BrowserMediaFilter);
                if (sniffed is not null)
                {
                    if (Uri.TryCreate(result.Url, UriKind.Absolute, out var page))
                    {
                        var scoped = MediaRequestHeaders.ForTarget(request.Headers, page, new Uri(sniffed.Url));
                        if (!ReferenceEquals(scoped, request.Headers))
                        {
                            request.Headers.Clear();
                            foreach (var (name, value) in scoped) request.Headers[name] = value;
                        }
                    }
                    foreach (var (name, value) in sniffed.Headers) request.Headers[name] = value;
                    request.Url = sniffed.Url;
                    contentType = sniffed.ContentType;
                }
                else
                {
                    if (result.BrowserFailureMessage is { } message) throw new NotSupportedException(message);
                    request.Url = await services.Resolver.ResolveAsync(result.Url, config!.Parses, request.Headers, token);
                    if (!PlayUrlResolver.IsDirectMediaUrl(request.Url) && !IsLoopback(request.Url))
                        throw new InvalidDataException("没有解析到视频地址，请切换线路。");
                    request.Headers.TryAdd("Referer", result.Url);
                    contentType = null;
                }
            }
        }
        else if (request.Kind == PlaybackKind.Vod && config is not null)
        {
            request.Url = await services.Resolver.ResolveAsync(request.Url, config.Parses, request.Headers, token);
        }

        if (!request.UsesProviderVideoHeights)
        {
            if (request.Kind == PlaybackKind.Vod && string.IsNullOrEmpty(contentType) && !PlayUrlResolver.IsDirectMediaUrl(request.Url) &&
                Uri.TryCreate(request.Url, UriKind.Absolute, out var probe) && probe.Scheme is "http" or "https")
                contentType = await services.Http.ProbeMediaTypeAsync(probe, request.Headers, token);
            if (HlsManifest.IsPlaylist(request.Url, contentType))
            {
                var payload = await ReadPlaylistAsync(request, !quality.IsSpecific && !quality.RequestsOriginal, token);
                if (payload is not null)
                {
                    var master = Encoding.UTF8.GetString(payload.Content);
                    if (!HlsManifest.IsManifest(master) && (quality.IsSpecific || quality.RequestsOriginal))
                        throw new InvalidDataException("片源未返回有效的 HLS 播放列表。");
                    bool? hdrOutput = getHdrOutput is not null && HlsManifest.IsManifest(master) ? await getHdrOutput(token) : null;
                    if (HlsManifest.SelectQualities(master, quality, hdrOutput) is { } selection)
                    {
                        if (selection.RequiresDolbyVision)
                            throw new NotSupportedException("此线路仅提供杜比视界 Profile 5，请切换线路。");
                        if (selection.Heights.Count > 0) request.AvailableVideoHeights = selection.Heights;
                        if (selection.QualityOptions.Count > 0) request.AvailableQualities = selection.QualityOptions;
                        request.ResolvedQuality = selection.SelectedQuality;
                        if (quality.IsSpecific && selection.VariantCount == 0)
                            throw new NotSupportedException($"该线路未提供 {quality.Name}。");
                        if (hdrOutput.HasValue)
                            Diagnostics.Log($"HLS selected: {quality.Name}, {selection.Range}, {selection.VariantCount} variants, HDR output={hdrOutput.Value}");
                        if (selection.Playlist is { } selected)
                        {
                            var origin = new Uri(request.Url);
                            var headers = new Dictionary<string, string>(MediaRequestHeaders.ForTarget(request.Headers, origin, payload.FinalUri), StringComparer.OrdinalIgnoreCase);
                            ProxyTracks(request, origin);
                            request.Url = services.Cloud.ProxyMedia(new CloudPlayResult(payload.FinalUri.AbsoluteUri, headers, contentType)
                            {
                                UnwrapPngHlsSegments = unwrapPngHlsSegments
                            }, selected);
                            request.Headers.Clear();
                            return request;
                        }
                    }
                    else if (request.Kind == PlaybackKind.Vod && HlsManifest.IsMediaPlaylist(master))
                    {
                        request.HasFixedVideoTrack = true;
                    }
                }
            }
            else if (!forDownload && DashManifest.IsPlaylist(request.Url, contentType) && !MediaRequestHeaders.RequiresProxy(request.Headers))
            {
                var payload = await ReadPlaylistAsync(request, !quality.IsSpecific && !quality.RequestsOriginal, token);
                if (payload is not null && DashManifest.SelectQualities(Encoding.UTF8.GetString(payload.Content), payload.FinalUri, quality) is { } selection)
                {
                    if (selection.Options.Count > 0) request.AvailableQualities = selection.Options;
                    request.ResolvedQuality = selection.SelectedQuality;
                    if (quality.IsSpecific && selection.SelectedQuality is null)
                        throw new NotSupportedException($"该线路未提供 {quality.Name}。");
                    if (selection.Manifest is { } selected)
                    {
                        var origin = new Uri(request.Url);
                        var headers = MediaRequestHeaders.ForTarget(request.Headers, origin, payload.FinalUri);
                        ProxyTracks(request, origin);
                        request.Url = services.Cloud.ProxyMedia(new CloudPlayResult(payload.FinalUri.AbsoluteUri, headers,
                            DashManifest.ContentType), selected);
                        request.Headers.Clear();
                        foreach (var (name, value) in headers) request.Headers[name] = value;
                        return request;
                    }
                }
            }
            if (quality.IsSpecific && request.ResolvedQuality is null && !flag.Contains(quality.Name, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"该线路未提供 {quality.Name}。");
        }

        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var mediaUri) && mediaUri.Scheme is "https" or "http")
        {
            request.HasFixedVideoTrack |= !request.UsesProviderVideoHeights && IsSingleVideoFile(mediaUri, contentType);
            ProxyTracks(request, mediaUri);
            // Use Windows' HTTPS transport for live feeds as well as providers that require it.
            var liveHttps = request.Kind == PlaybackKind.Live && mediaUri.Scheme == "https" && !mediaUri.IsLoopback;
            if (useHttpProxy || liveHttps || MediaRequestHeaders.RequiresProxy(request.Headers) ||
                HlsManifest.IsPlaylist(request.Url, contentType) && !HlsManifest.IsPlaylist(request.Url, null))
            {
                request.Url = services.Cloud.ProxyMedia(new CloudPlayResult(request.Url, new Dictionary<string, string>(request.Headers), contentType)
                {
                    UnwrapPngHlsSegments = unwrapPngHlsSegments
                });
                request.Headers.Clear();
            }
        }
        return request;
    }

    private async Task<HttpPayload?> ReadPlaylistAsync(PlaybackRequest request, bool automatic, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (automatic) deadline.CancelAfter(TimeSpan.FromMilliseconds(1600));
        try
        {
            return await services.Http.GetBytesAsync(request.Url, request.Headers, maxBytes: 1024 * 1024,
                cancellationToken: deadline.Token, scopeRedirectHeaders: true);
        }
        catch (Exception ex) when (automatic && !token.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static bool IsSingleVideoFile(Uri uri, string? contentType) =>
        contentType is "video/mp4" or "video/webm" or "video/x-matroska" or "video/quicktime" or "video/x-flv" ||
        Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".mp4" or ".m4v" or ".webm" or ".mkv" or ".mov" or ".avi" or ".flv";

    private static bool IsLoopback(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private void ProxyTracks(PlaybackRequest request, Uri mediaUri)
    {
        if (request.Headers.Count == 0) return;
        Uri ResolveTrack(Uri uri)
        {
            if (uri.Scheme is not ("http" or "https")) return uri;
            var headers = MediaRequestHeaders.ForTarget(request.Headers, mediaUri, uri);
            return headers.Count > 0 ? new Uri(services.Cloud.ProxyMedia(new CloudPlayResult(uri.AbsoluteUri, headers))) : uri;
        }
        request.Subtitles = request.Subtitles.Select(ResolveTrack).ToList();
        request.AudioTracks = request.AudioTracks.Select(ResolveTrack).ToList();
    }
}
