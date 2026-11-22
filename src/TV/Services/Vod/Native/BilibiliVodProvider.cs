using System.Globalization;
using System.Net;
using System.Text.Json;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public sealed partial class BilibiliVodProvider(SourceHttpClient http, CloudDriveService? cloud, BilibiliAccountService? account = null) :
    ICategoryFilterVodProvider, IQualityVodProvider
{
    private const string Api = BilibiliAccountService.Api;
    private const string SiteUrl = BilibiliAccountService.Site;
    private const int HighestQuality = 127;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";
    private static readonly IReadOnlyDictionary<string, string> PublicHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = UserAgent,
        ["Referer"] = SiteUrl
    };
    private static readonly Dictionary<int, (string Label, int Height)> QualityFormats = new()
    {
        [6] = ("240P 极速", 240), [15] = ("360P 流畅", 360), [16] = ("360P 流畅", 360),
        [32] = ("480P 清晰", 480), [48] = ("720P 高清", 720), [64] = ("720P 高清", 720), [74] = ("720P 60帧", 720),
        [80] = ("1080P 高清", 1080), [100] = ("智能修复", 0), [112] = ("1080P 高码率", 1080), [116] = ("1080P 60帧", 1080),
        [120] = ("4K 超高清", 2160), [125] = ("HDR 真彩", 0), [126] = ("杜比视界", 0), [127] = ("8K 超高清", 4320)
    };
    private static readonly Dictionary<int, int> DefaultQualityIds = new()
    {
        [240] = 6, [360] = 16, [480] = 32, [720] = 64, [1080] = 80, [2160] = 120, [4320] = 127
    };

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        site.Api.Equals("csp_BiliGuard", StringComparison.OrdinalIgnoreCase) && ChannelConfig(site) is not null;

    public static bool IsLoginCategory(string id) => id.Equals("peizhi", StringComparison.OrdinalIgnoreCase);

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var address = ChannelConfig(site) ?? throw new InvalidDataException("哔哩合集缺少分类配置地址。");
        var payload = await http.GetBytesAsync(address.AbsoluteUri, PublicHeaders, maxBytes: 2 * 1024 * 1024,
            cancellationToken: cancellationToken, scopeRedirectHeaders: true).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CmsResponse>(SourceHttpClient.DecodeText(payload.Content).TrimStart('\uFEFF'), JsonUtil.Options)
            ?? throw new InvalidDataException("哔哩合集没有返回分类配置。");
        response.Categories ??= [];
        response.List ??= [];
        response.Categories.RemoveAll(static category => category is null);
        response.List.RemoveAll(static item => item is null);
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
        }

        // The Android catalog supplies this feed when the collection defines no curated list.
        if (response.List.Count == 0)
        {
            try
            {
                var popular = await ReadApiAsync(site, "/x/web-interface/popular", cancellationToken, ("ps", "20"), ("pn", "1"))
                    .ConfigureAwait(false);
                response.List = Videos(site, popular, "list");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or JsonException)
            {
                Diagnostics.Log($"哔哩推荐暂不可用 ({site.Key}): {ex.GetType().Name}");
            }
        }
        response.Page = 1;
        response.PageCount = Math.Max(response.PageCount, 1);
        return response;
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken)
    {
        if (categoryId.Length == 0) return GetHomeAsync(site, cancellationToken);
        if (IsLoginCategory(categoryId)) throw new InvalidOperationException("请通过点播页的登录配置管理 B 站账号。");
        if (categoryId.EndsWith("/{pg}", StringComparison.Ordinal) && NumericId(categoryId[..^5]))
            return ReadUploaderAsync(site, categoryId[..^5], page, cancellationToken);
        var topic = filters?.GetValueOrDefault("tid");
        var keyword = string.IsNullOrWhiteSpace(topic) ? categoryId : $"{categoryId} {topic}";
        return ReadSearchAsync(site, keyword, page, filters?.GetValueOrDefault("order") ?? "totalrank",
            filters?.GetValueOrDefault("duration") ?? "0", cancellationToken);
    }

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        ReadSearchAsync(site, keyword, page, "totalrank", "0", cancellationToken);

    private async Task<CmsResponse> ReadSearchAsync(SourceSite site, string keyword, int page, string order, string duration, CancellationToken token)
    {
        var data = await ReadApiAsync(site, "/x/web-interface/wbi/search/type", token, ("search_type", "video"),
            ("keyword", keyword), ("page", page.ToString(CultureInfo.InvariantCulture)), ("order", order), ("duration", duration))
            .ConfigureAwait(false);
        var items = Videos(site, data, "result");
        return new CmsResponse { List = items, Page = page, PageCount = Int(data, "numPages"), Total = Int(data, "numResults"), Limit = Int(data, "pagesize") };
    }

    private async Task<CmsResponse> ReadUploaderAsync(SourceSite site, string uploader, int page, CancellationToken token)
    {
        const int pageSize = 30;
        var nav = await ReadApiAsync(site, "/x/web-interface/nav", token).ConfigureAwait(false);
        var images = Value(nav, "wbi_img");
        var query = BilibiliWebQuery.Create(Text(images, "img_url"), Text(images, "sub_url"),
            ("mid", uploader), ("pn", page.ToString(CultureInfo.InvariantCulture)), ("ps", pageSize.ToString(CultureInfo.InvariantCulture)));
        var data = await ReadApiQueryAsync(site, "/x/space/wbi/arc/search", query, token).ConfigureAwait(false);
        var total = Int(Value(data, "page"), "count");
        return new CmsResponse { List = Videos(site, Value(data, "list"), "vlist"), Page = page, PageCount = (int)Math.Ceiling(total / (double)pageSize),
            Total = total, Limit = pageSize };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var keys = id.Split('@', 2);
        var useBvid = IsBvid(keys[0]);
        var submittedId = useBvid ? keys[0] : keys[^1];
        if (!useBvid && !NumericId(submittedId)) throw new InvalidDataException("哔哩视频编号无效。");
        var data = await ReadApiAsync(site, "/x/web-interface/view", cancellationToken,
            (useBvid ? "bvid" : "aid", submittedId)).ConfigureAwait(false);
        var item = Item(site, data) ?? throw new InvalidDataException("哔哩哔哩没有返回视频详情。");
        item.Id = id;
        var aid = Text(data, "aid");
        var episodes = Array(data, "pages").Select(part => EpisodeEntry(aid, Text(part, "cid"), Text(part, "part")))
            .Where(entry => entry is not null).ToList();
        if (episodes.Count == 0 && EpisodeEntry(aid, Text(data, "cid"), "正片") is { } episode) episodes.Add(episode);
        if (episodes.Count == 0) return item;
        item.PlayFrom = "B站";
        item.PlayUrl = string.Join('#', episodes);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1400));
        try
        {
            var related = await ReadApiAsync(site, "/x/web-interface/archive/related", budget.Token, ("bvid", Text(data, "bvid")))
                .ConfigureAwait(false);
            if (related.ValueKind == JsonValueKind.Array)
            {
                var entries = related.EnumerateArray().Select(video => EpisodeEntry(Text(video, "aid"), Text(video, "cid"), Text(video, "title")))
                    .OfType<string>().ToArray();
                if (entries.Length > 0)
                {
                    item.PlayFrom += "$$$相关";
                    item.PlayUrl += "$$$" + string.Join('#', entries);
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException)
        {
            Diagnostics.Log($"哔哩相关推荐暂不可用 ({site.Key}): {ex.GetType().Name}");
        }
        return item;
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token) =>
        ResolvePlayAsync(site, flag, id, new QualityOption("自动", 0), token);

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, QualityOption quality, CancellationToken token) =>
        ResolvePlayAsync(site, flag, id, quality, forDownload: false, token);

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, QualityOption quality, bool forDownload, CancellationToken token)
    {
        if (cloud is null) throw new NotSupportedException("直达播放模式不支持在线媒体代理，请在完整应用中打开此内容。");
        var ids = id.Split('+', 3);
        if (ids.Length < 2 || !NumericId(ids[0]) || !NumericId(ids[1])) throw new InvalidDataException("哔哩分集编号无效。");
        var requested = quality.ProviderId ?? (quality.Height > 0 ? DefaultQualityIds.GetValueOrDefault(quality.Height) : HighestQuality);
        if (requested <= 0) throw new NotSupportedException($"该线路未提供 {quality.Name}。");
        var cookie = account is null ? null : await account.GetPlaybackCookieAsync(token).ConfigureAwait(false);
        var danmaku = new Uri($"{Api}/x/v1/dm/list.so?oid={ids[1]}");
        var headers = new Dictionary<string, string>(PublicHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = $"{SiteUrl}video/av{ids[0]}/"
        };
        Task<JsonElement> ReadPlaybackAsync(int qn, string format, CancellationToken cancellationToken) =>
            ReadApiQueryAsync(site, "/x/player/playurl", FormattableString.Invariant($"avid={ids[0]}&cid={ids[1]}&qn={qn}&{format}"),
                cancellationToken, cookie);

        const string progressiveFormat = "fnval=1&platform=html5&high_quality=1";
        var directRequest = ReadPlaybackAsync(forDownload ? requested : HighestQuality, progressiveFormat, token);
        if (forDownload)
        {
            var data = await directRequest.ConfigureAwait(false);
            var idForDownload = Int(data, "quality");
            var option = ReadQuality(idForDownload, ReadFormats(data).GetValueOrDefault(idForDownload), default, true);
            if (quality.IsSpecific && idForDownload != requested) throw UnavailableQuality(quality);
            return CreateProgressive(data, headers, danmaku, [option], option);
        }

        using var dashDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        dashDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        var directAttempt = ReadPlaybackResultAsync(directRequest, token);
        var dashAttempt = ReadPlaybackResultAsync(ReadPlaybackAsync(HighestQuality, "fnval=4048&fourk=1", dashDeadline.Token), token);
        await Task.WhenAll(directAttempt, dashAttempt).ConfigureAwait(false);
        var (direct, directError) = await directAttempt.ConfigureAwait(false);
        var (dashResponse, dashError) = await dashAttempt.ConfigureAwait(false);
        var dash = Value(dashResponse, "dash");
        var tracks = ReadVideoTracks(dash);
        var availableTracks = tracks.Values.Where(track => track.IsPlayable).ToArray();
        var directId = ProgressiveAddress(direct) is null ? 0 : Int(direct, "quality");
        var formats = ReadFormats(direct, dashResponse);
        if (quality.IsSpecific && directId > 0 && directId != requested && !availableTracks.Any(track => track.Id == requested) &&
            ReadFormats(direct).ContainsKey(requested))
        {
            var alternate = await ReadPlaybackResultAsync(ReadPlaybackAsync(requested, progressiveFormat, token), token).ConfigureAwait(false);
            if (ProgressiveAddress(alternate.Data) is not null && Int(alternate.Data, "quality") == requested)
                direct = alternate.Data;
        }
        var actualDirect = ProgressiveAddress(direct) is null ? 0 : Int(direct, "quality");
        var options = formats.Keys.Concat(tracks.Keys).Append(directId).Append(actualDirect).Where(id => id > 0).Distinct()
            .OrderDescending().Select(id =>
            {
                tracks.TryGetValue(id, out var track);
                return ReadQuality(id, formats.GetValueOrDefault(id), track?.Metadata ?? default,
                    id == directId || id == actualDirect || track?.IsPlayable == true);
            }).ToArray();
        var video = quality.IsSpecific ? availableTracks.FirstOrDefault(track => track.Id == requested)
            : availableTracks.Where(track => track.Id is not (125 or 126) && track.Id >= actualDirect)
                .OrderByDescending(track => track.Id).FirstOrDefault();
        if (video is null && !quality.IsSpecific && ProgressiveAddress(direct) is null)
            video = availableTracks.OrderByDescending(track => track.Id).FirstOrDefault();

        // Prefer a complete progressive file when it delivers the same tier as DASH.
        if (video?.Id == actualDirect) video = null;
        if (video?.Address is { } address)
        {
            var audio = Array(dash, "audio").OrderByDescending(track => Int(track, "bandwidth"))
                .Select(track => ReadTrackAddress(track, "audio/mp4")).OfType<Uri>().Distinct()
                .Select(track => new Uri(cloud.ProxyMedia(new CloudPlayResult(track.AbsoluteUri, headers, "audio/mp4", ".m4a"))))
                .ToArray();
            var local = cloud.ProxyMedia(new CloudPlayResult(address.AbsoluteUri, headers, "video/mp4", ".mp4")
            {
                UseBoundedRanges = video.Id >= 112
            });
            return new SpiderPlayResult(local, new Dictionary<string, string>(), 0, 0, Danmaku: danmaku,
                ContentType: "video/mp4", AudioTracks: audio, ReplaceHeaders: true)
            {
                ProviderVideoHeights = AvailableHeights(options),
                ProviderQualities = options,
                ResolvedQuality = options.First(option => option.ProviderId == video.Id),
                SeekOnAudioTrackChange = audio.Length > 0
            };
        }
        if (quality.IsSpecific && actualDirect != requested) throw UnavailableQuality(quality);
        if (directError is not null)
            throw new InvalidOperationException(directError.Message, directError);
        if (ProgressiveAddress(direct) is null && dashError is not null)
            throw new InvalidOperationException(dashError.Message, dashError);
        return CreateProgressive(direct, headers, danmaku, options, options.FirstOrDefault(option => option.ProviderId == actualDirect));
    }

    private static SpiderPlayResult CreateProgressive(JsonElement data, IReadOnlyDictionary<string, string> headers, Uri danmaku,
        IReadOnlyList<QualityOption> options, QualityOption? selected)
    {
        if (ProgressiveAddress(data) is not { } media)
            throw new NotSupportedException(Array(data, "durl").Length > 1
                ? "该视频没有可用的单文件媒体。" : "哔哩哔哩没有返回此视频的可用播放地址。");
        return new SpiderPlayResult(media.AbsoluteUri, headers, 0, 0, Danmaku: danmaku, ContentType: "video/mp4", ReplaceHeaders: true)
        {
            ProviderVideoHeights = AvailableHeights(options),
            ProviderQualities = options,
            ResolvedQuality = selected,
            UseHttpProxy = true
        };
    }

    private static Uri? ProgressiveAddress(JsonElement data)
    {
        var sources = Array(data, "durl");
        return sources.Length == 1 ? AsHttpUri(Text(sources[0], "url")) : null;
    }

    private static Dictionary<int, JsonElement> ReadFormats(params JsonElement[] responses)
    {
        var formats = new Dictionary<int, JsonElement>();
        foreach (var format in responses.SelectMany(response => Array(response, "support_formats")))
            if (Int(format, "quality") is > 0 and var id) formats[id] = format;
        return formats;
    }

    private static Dictionary<int, DashVideo> ReadVideoTracks(JsonElement dash) =>
        Array(dash, "video")
            .Select(track => new DashVideo(Int(track, "id"), ReadTrackAddress(track, "video/mp4"), Text(track, "codecs"), track))
            .Where(track => track.Id > 0 && track.Address is not null)
            .GroupBy(track => track.Id).ToDictionary(group => group.Key, group => group.OrderByDescending(track => track.IsPlayable)
                .ThenByDescending(track => track.Codec.StartsWith("avc1.", StringComparison.OrdinalIgnoreCase)).First());

    private static QualityOption ReadQuality(int id, JsonElement format, JsonElement track, bool available)
    {
        var (label, height) = QualityFormats.GetValueOrDefault(id);
        var name = Text(format, "new_description");
        if (name.Length == 0)
            name = string.Join(' ', new[] { Text(format, "display_desc"), Text(format, "superscript") }.Where(value => value.Length > 0));
        if (name.Length == 0) name = label ?? $"B 站画质 {id}";
        var fps = VideoFormat.ReadFrameRate(Text(track, "frameRate"));
        if (fps == 0) fps = VideoFormat.ReadFrameRate(Text(track, "frame_rate"));
        return new QualityOption(name, height > 0 ? height : Int(track, "height"))
        {
            FrameRate = fps,
            ProviderId = id,
            IsAvailable = available
        };
    }

    private static HashSet<int> AvailableHeights(IEnumerable<QualityOption> options) =>
        options.Where(option => option.IsAvailable && option.Height > 0).Select(option => option.Height).ToHashSet();

    private static NotSupportedException UnavailableQuality(QualityOption quality) =>
        new($"B 站未向当前会话提供「{quality.Name}」的可播流。");

    private sealed record DashVideo(int Id, Uri? Address, string Codec, JsonElement Metadata)
    {
        public bool IsPlayable => !Codec.StartsWith("dvh1.05.", StringComparison.OrdinalIgnoreCase) &&
            !Codec.StartsWith("dvhe.05.", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? ReadTrackAddress(JsonElement track, string mediaType)
    {
        var type = Text(track, "mime_type");
        if (!mediaType.Equals(type.Length > 0 ? type : Text(track, "mimeType"), StringComparison.OrdinalIgnoreCase)) return null;
        return AsHttpUri(Text(track, "base_url")) ?? AsHttpUri(Text(track, "baseUrl"));
    }

    private static async Task<(JsonElement Data, Exception? Error)> ReadPlaybackResultAsync(Task<JsonElement> request, CancellationToken token)
    {
        try { return (await request.ConfigureAwait(false), null); }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return (default, ex);
        }
    }

    private Task<JsonElement> ReadApiAsync(SourceSite site, string path, CancellationToken token, params (string Key, string Value)[] parameters) =>
        ReadApiQueryAsync(site, path, string.Join('&', parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")), token);

    private async Task<JsonElement> ReadApiQueryAsync(SourceSite site, string path, string query, CancellationToken token, string? session = null)
    {
        var headers = new Dictionary<string, string>(PublicHeaders, StringComparer.OrdinalIgnoreCase);
        var cookie = session ?? site.GetHeaders().GetValueOrDefault("Cookie") ?? Text(site.Ext, "cookie");
        if (!string.IsNullOrWhiteSpace(cookie) && !Uri.TryCreate(cookie, UriKind.Absolute, out _) && cookie.IndexOfAny(['\r', '\n']) < 0)
            headers["Cookie"] = cookie;
        var url = query.Length == 0 ? Api + path : Api + path + "?" + query;
        var payload = await http.GetBytesAsync(url, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token, scopeRedirectHeaders: true)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload.Content);
        var root = document.RootElement;
        var code = Value(root, "code");
        var data = Value(root, "data");
        if (data.ValueKind == JsonValueKind.Undefined && Value(root, "result").ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            data = Value(root, "result");
        if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var status))
            throw new InvalidDataException("哔哩哔哩响应格式无法识别。");
        if (status == 0 || path == "/x/web-interface/nav" && status == -101) return data.Clone();
        if (status == -101 && session is not null)
            return await ReadApiQueryAsync(site, path, query, token).ConfigureAwait(false);
        if (status == -412) throw new HttpRequestException("哔哩哔哩拒绝当前请求。", null, HttpStatusCode.PreconditionFailed);
        if (status == -352) throw new SiteVerificationRequiredException(site.Key, site.Name,
            new Uri("https://search.bilibili.com/"), SiteVerificationKind.BrowserChallenge, "站点搜索需要验证码。");
        if (status == -101) throw new InvalidOperationException("此哔哩哔哩视频需要登录。");
        throw new InvalidDataException($"哔哩哔哩接口返回 {status}。");
    }

    private static List<VodItem> Videos(SourceSite site, JsonElement data, string property) =>
        Array(data, property).Select(value => Item(site, value)).OfType<VodItem>().ToList();

    private static VodItem? Item(SourceSite site, JsonElement data)
    {
        var aid = Text(data, "aid");
        var bvid = Text(data, "bvid");
        var name = PlaybackParsing.StripHtml(Text(data, "title"));
        if ((!NumericId(aid) && !IsBvid(bvid)) || name.Length == 0) return null;
        var picture = Text(data, "pic");
        if (picture.StartsWith("//", StringComparison.Ordinal)) picture = "https:" + picture;
        var date = Text(data, "pubdate");
        var year = long.TryParse(date, NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) && stamp is > 0 and < 253402272000L
            ? DateTimeOffset.FromUnixTimeSeconds(stamp).ToOffset(TimeSpan.FromHours(8)).Year.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var duration = Text(data, "duration");
        if (duration.Length == 0) duration = Text(data, "length");
        if (int.TryParse(duration, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            duration = seconds < 3600 ? $"{seconds / 60}:{seconds % 60:00}" : $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        var author = Text(Value(data, "owner"), "name");
        return new VodItem
        {
            Id = IsBvid(bvid) ? $"{bvid}@{aid}" : aid, Name = name, Picture = AsHttpUri(picture)?.AbsoluteUri ?? string.Empty,
            Year = year, Remarks = duration, TypeName = Text(data, "tname"), Director = author.Length > 0 ? author : Text(data, "author"),
            Content = PlaybackParsing.StripHtml(Text(data, "desc")), SiteKey = site.Key, SiteName = site.Name
        };
    }

    private static string? EpisodeEntry(string aid, string cid, string name) => NumericId(aid) && NumericId(cid)
        ? $"{(name.Length > 0 ? name.Replace('$', '＄').Replace('#', '＃') : "正片")}${aid}+{cid}" : null;

    private static Uri? ChannelConfig(SourceSite site) => AsHttpUri(Text(site.Ext, "json"));
    private static Uri? AsHttpUri(string text) => Uri.TryCreate(text, UriKind.Absolute, out var result) &&
        result.Scheme is "http" or "https" && result.UserInfo.Length == 0 ? result : null;
    private static bool NumericId(string value) => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);
    private static bool IsBvid(string value) => value.Length == 12 && value.StartsWith("BV", StringComparison.Ordinal) && value.All(char.IsAsciiLetterOrDigit);
    private static JsonElement Value(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement root, string key)
    {
        var value = Value(root, key);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }
    private static int Int(JsonElement root, string key) => AsInt(Value(root, key));
    private static int AsInt(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static JsonElement[] Array(JsonElement root, string key) =>
        Value(root, key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToArray() : [];
}
