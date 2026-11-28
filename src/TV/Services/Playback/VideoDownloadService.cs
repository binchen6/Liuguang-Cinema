using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Playback;

public sealed partial class VideoDownloadService : IDisposable
{
    private readonly HttpClient _client;

    public VideoDownloadService(DnsResolverService dns) => _client = new HttpClient(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(12),
        ConnectCallback = dns.ConnectAsync,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = 6
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string> GetExtensionAsync(PlaybackRequest request, CancellationToken token)
    {
        if (request.Magnet is { } magnet) return Path.GetExtension(magnet.FileName).ToLowerInvariant();
        using var response = await OpenAsync(new Uri(request.Url), request.Headers, token).ConfigureAwait(false);
        var uri = response.RequestMessage!.RequestUri!;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (HlsManifest.IsPlaylist(uri.AbsoluteUri, contentType)) return ".m3u8";
        var filename = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName ?? uri.AbsolutePath;
        var extension = Path.GetExtension(filename.Trim('"')).ToLowerInvariant();
        return extension switch
        {
            ".mp4" or ".mkv" or ".webm" or ".mov" or ".ts" or ".avi" or ".mp3" or ".m4a" or ".flv" => extension,
            _ => contentType switch
            {
                "video/mp4" => ".mp4", "video/webm" => ".webm", "video/x-matroska" => ".mkv",
                "video/quicktime" => ".mov", "video/mp2t" => ".ts", "audio/mpeg" => ".mp3",
                _ => ".media"
            }
        };
    }

    public async Task SaveAsync(PlaybackRequest request, string destination, IProgress<double> progress, CancellationToken token)
    {
        var uri = new Uri(request.Url);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        string? assets = null;
        var committed = false;
        try
        {
            if (request.Magnet is { } magnet)
            {
                await magnet.SaveSelectedFileAsync(temporary, progress, token).ConfigureAwait(false);
            }
            else
            {
                using var response = await OpenAsync(uri, request.Headers, token).ConfigureAwait(false);
                if (HlsManifest.IsPlaylist(response.RequestMessage!.RequestUri!.AbsoluteUri, response.Content.Headers.ContentType?.MediaType))
                {
                    assets = Path.Combine(Path.GetDirectoryName(destination)!, "media-" + Guid.NewGuid().ToString("N")[..12]);
                    Directory.CreateDirectory(assets);
                    var files = new Dictionary<Uri, string>();
                    var lines = await ReadPlaylistAsync(response, token).ConfigureAwait(false);
                    var updated = await MaterializeAsync(lines, response.RequestMessage.RequestUri, uri, assets, files, request.Headers, progress, token, 0, true).ConfigureAwait(false);
                    await File.WriteAllLinesAsync(temporary, updated, new UTF8Encoding(false), token).ConfigureAwait(false);
                }
                else await CopyAsync(response, temporary, progress, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            committed = true;
            progress.Report(100);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Log($"下载临时文件清理失败: {ex.GetType().Name}"); }
            try { if (!committed && assets is not null && Directory.Exists(assets)) Directory.Delete(assets, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Log($"下载资源目录清理失败: {ex.GetType().Name}"); }
        }
    }

    private async Task<string[]> MaterializeAsync(string[] lines, Uri baseUri, Uri origin, string assets, Dictionary<Uri, string> files,
        IReadOnlyDictionary<string, string> headers, IProgress<double> progress, CancellationToken token, int depth, bool root)
    {
        if (depth > 4 || lines.Length > 40_000) throw new InvalidDataException("播放列表超出离线下载范围。");
        var master = lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
        if (master)
        {
            var selected = HlsManifest.SelectVariant(string.Join('\n', lines), -1)
                ?? throw new InvalidDataException("未识别到可下载的画质。");
            lines = selected.Split('\n');
        }
        else if (!lines.Contains("#EXT-X-ENDLIST", StringComparer.Ordinal))
            throw new NotSupportedException("当前直播或仍在更新的视频无法下载。");

        var prefix = root ? Path.GetFileName(assets) + "/" : "";
        var resourceCount = Math.Max(1, lines.Count(line => line.Length > 0 && (!line.StartsWith('#') || UriAttribute().IsMatch(line))));
        var completedResources = 0;
        void ReportProgress()
        {
            if (!master) progress.Report(Math.Min(99, ++completedResources * 100d / resourceCount));
        }
        var segmentExtension = lines.Any(line => line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)) ? ".m4s" : ".ts";
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0) continue;
            if (!line.StartsWith('#'))
            {
                lines[index] = prefix + await SaveResourceAsync(line, master ? ".m3u8" : segmentExtension,
                    baseUri, origin, assets, files, headers, progress, token, depth).ConfigureAwait(false);
                ReportProgress();
                continue;
            }
            var uriAttribute = UriAttribute().Match(line);
            if (!uriAttribute.Success) continue;
            var uri = uriAttribute.Groups[1].Value;
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var extension = line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) ? ".m3u8"
                : line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ? ".mp4" : ".key";
            var local = await SaveResourceAsync(uri, extension, baseUri, origin, assets, files, headers, progress, token, depth).ConfigureAwait(false);
            lines[index] = line.Remove(uriAttribute.Groups[1].Index, uri.Length).Insert(uriAttribute.Groups[1].Index, prefix + local);
            ReportProgress();
        }
        return lines;
    }

    private async Task<string> SaveResourceAsync(string value, string defaultExtension, Uri baseUri, Uri origin, string assets, Dictionary<Uri, string> files,
        IReadOnlyDictionary<string, string> headers, IProgress<double> progress, CancellationToken token, int depth)
    {
        var uri = new Uri(baseUri, value);
        if (files.TryGetValue(uri, out var cached)) return cached;
        if (files.Count >= 20_000) throw new InvalidDataException("播放列表包含过多文件。");
        using var response = await OpenAsync(uri, headers, token, origin).ConfigureAwait(false);
        var effective = response.RequestMessage!.RequestUri!;
        var playlist = defaultExtension == ".m3u8" || HlsManifest.IsPlaylist(effective.AbsoluteUri, response.Content.Headers.ContentType?.MediaType);
        var extension = playlist ? ".m3u8" : Path.GetExtension(effective.AbsolutePath);
        if (extension.Length is 0 or > 8 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = defaultExtension;
        var filename = $"{files.Count:D6}{extension}";
        files.Add(uri, filename);
        var path = Path.Combine(assets, filename);
        if (playlist)
        {
            var lines = await ReadPlaylistAsync(response, token).ConfigureAwait(false);
            var updated = await MaterializeAsync(lines, effective, origin, assets, files, headers, progress, token, depth + 1, false).ConfigureAwait(false);
            await File.WriteAllLinesAsync(path, updated, new UTF8Encoding(false), token).ConfigureAwait(false);
        }
        else await CopyAsync(response, path, null, token).ConfigureAwait(false);
        return filename;
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token, Uri? origin = null)
    {
        origin ??= uri;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new NotSupportedException("当前地址不支持另存。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var target = uri;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var (key, value) in MediaRequestHeaders.ForTarget(headers, origin, target))
                if (!key.Equals("Host", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(key, value);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is not null)
            {
                using (response)
                {
                    if (redirects >= 7 || !Uri.TryCreate(target, response.Headers.Location, out var next) ||
                        next.Scheme is not ("http" or "https") || next.UserInfo.Length > 0 ||
                        target.Scheme == "https" && next.Scheme != "https")
                        throw new InvalidDataException("下载重定向无效。");
                    target = next;
                }
                continue;
            }
            if (response.StatusCode == HttpStatusCode.PartialContent && !CoversEntireMedia(response))
            {
                response.Dispose();
                throw new EndOfStreamException("服务器只返回了部分媒体内容，无法保存完整文件。");
            }
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/json"))
                return response;
            using (response)
            {
                response.EnsureSuccessStatusCode();
                throw new InvalidDataException("服务器返回的内容不是媒体文件。");
            }
        }
    }

    private static bool CoversEntireMedia(HttpResponseMessage response) =>
        response.Content.Headers.ContentRange is { From: 0, To: { } end, Length: { } length } range &&
        string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) && length > 0 && end == length - 1 &&
        (response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength == length);

    private static async Task<string[]> ReadPlaylistAsync(HttpResponseMessage response, CancellationToken token)
    {
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token).ConfigureAwait(false);
        var text = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).TrimStart('\uFEFF');
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new InvalidDataException("无效的 HLS 播放列表。");
        return text.Split('\n', StringSplitOptions.TrimEntries);
    }

    private static async Task CopyAsync(HttpResponseMessage response, string destination, IProgress<double>? progress, CancellationToken token)
    {
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var expected = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
        var buffer = new byte[128 * 1024];
        var lastUpdate = Environment.TickCount64;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (progress is not null && expected > 0 && Environment.TickCount64 - lastUpdate >= 200)
            {
                progress.Report(Math.Clamp(output.Length * 100d / expected.Value, 0, 99));
                lastUpdate = Environment.TickCount64;
            }
        }
        if (output.Length == 0 || expected is > 0 && output.Length != expected.Value) throw new EndOfStreamException("媒体文件下载不完整。");
    }

    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();

    public void Dispose() => _client.Dispose();
}
