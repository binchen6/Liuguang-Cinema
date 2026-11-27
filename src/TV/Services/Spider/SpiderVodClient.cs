using System.Collections.Concurrent;
using System.Text.Json;
using TV.Models;
using TV.Services.Cloud;

namespace TV.Services.Spider;

public sealed record SpiderPlayResult(string Url, IReadOnlyDictionary<string, string> Headers, int Parse, int Jx,
    IReadOnlyList<Uri>? Subtitles = null, Uri? Danmaku = null, string? ContentType = null, IReadOnlyList<Uri>? AudioTracks = null,
    bool ReplaceHeaders = false)
{
    public IReadOnlySet<int>? ProviderVideoHeights { get; init; }
    public IReadOnlyList<QualityOption>? ProviderQualities { get; init; }
    public QualityOption? ResolvedQuality { get; init; }
    public bool UseHttpProxy { get; init; }
    public bool UnwrapPngHlsSegments { get; init; }
    internal bool SeekOnAudioTrackChange { get; init; }
    internal string? BrowserFailureMessage { get; init; }
    internal Func<Uri, bool>? BrowserMediaFilter { get; init; }
}

public sealed class SpiderVodClient
{
    private sealed record CachedPlay(SpiderPlayResult Result, DateTimeOffset ExpiresAt);
    private readonly SpiderRuntime _runtime;
    private readonly ConcurrentDictionary<string, CachedPlay> _playCache = new();

    public SpiderVodClient(SpiderRuntime runtime) => _runtime = runtime;

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        SendAsync(site, "home", new(), cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    internal Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken) =>
        SendAsync(site, "category", new() { ["tid"] = categoryId, ["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture), ["extend"] = filters }, cancellationToken);

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page = 1, CancellationToken cancellationToken = default) =>
        SendAsync(site, "search", new() { ["keyword"] = keyword, ["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var routed = RouteForId(site, id, out var provider);
        var response = await SendAsync(routed, "detail", new() { ["id"] = id }, cancellationToken).ConfigureAwait(false);
        var item = response.List.FirstOrDefault() ?? throw new InvalidDataException("爬虫没有返回资源详情。");
        item.CloudProvider = provider;
        return item;
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, string? cloudProvider = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cloudProvider)) RouteForId(site, id, out cloudProvider);
        if (!string.IsNullOrWhiteSpace(cloudProvider)) site = WithApi(site, cloudProvider);
        var cacheKey = $"{site.Key}\n{site.Api}\n{flag}\n{id}";
        if (_playCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Result;

        var data = await _runtime.SendAsync(site, "play", new() { ["flag"] = flag, ["id"] = id }, cancellationToken).ConfigureAwait(false);
        var result = ReadPlayResult(data);
        if (RequiresWindowsMediaTransport(site, result.Url))
            result = result with { UseHttpProxy = true };
        _playCache[cacheKey] = new CachedPlay(result, DateTimeOffset.UtcNow.AddMinutes(5));
        if (_playCache.Count > 256)
        {
            foreach (var pair in _playCache.Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)) _playCache.TryRemove(pair.Key, out _);
            if (_playCache.Count > 256)
                foreach (var pair in _playCache.OrderBy(pair => pair.Value.ExpiresAt).Take(_playCache.Count - 256)) _playCache.TryRemove(pair.Key, out _);
        }
        return result;
    }

    private static bool RequiresWindowsMediaTransport(SourceSite site, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var media) || media.Scheme != "https") return false;
        // These CDNs need Windows HTTPS for reliable byte-range reads or certificate validation.
        return media.IdnHost == "resource-cdn.tuxiaobei.com" || media.IdnHost == "vod.youlai.cn" &&
            (site.Api.Equals("csp_FirstAid", StringComparison.OrdinalIgnoreCase) ||
             site.Api.Equals("csp_FirstAidGuard", StringComparison.OrdinalIgnoreCase));
    }

    internal static SpiderPlayResult ReadPlayResult(JsonElement data)
    {
        data = ReadObject(data);
        var url = StringValue(data, "url");
        if (string.IsNullOrWhiteSpace(url) || url.Equals("null", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("爬虫没有返回可播放地址。");
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        var parse = IntValue(data, "parse");
        var jx = IntValue(data, "jx");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MergeHeaders(headers, data, "header");
        MergeHeaders(headers, data, "headers");
        var subtitles = ReadMediaLinks(data, url, "subs", "subtitles", "subtitle").Distinct().ToArray();
        var audio = ReadMediaLinks(data, url, "audioTracks", "audio_tracks", "audios").Distinct().ToArray();
        var danmaku = ReadMediaLinks(data, url, "danmaku", "danmu").FirstOrDefault();
        var contentType = StringValue(data, "contentType");
        if (contentType.Length == 0) contentType = StringValue(data, "content_type");
        return new SpiderPlayResult(url, headers, parse, jx, subtitles, danmaku, contentType.Split(';', 2)[0].Trim().ToLowerInvariant(), audio);
    }

    private static IEnumerable<Uri> ReadMediaLinks(JsonElement data, string videoUrl, params string[] names)
    {
        Uri.TryCreate(videoUrl, UriKind.Absolute, out var baseUri);
        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value)) continue;
            var candidates = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
            foreach (var candidate in candidates)
            {
                var link = candidate.ValueKind switch
                {
                    JsonValueKind.String => candidate.GetString(),
                    JsonValueKind.Object => FirstNonEmpty(StringValue(candidate, "url"), StringValue(candidate, "src")),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(link)) continue;
                var resolved = baseUri?.Scheme is "http" or "https"
                    ? Uri.TryCreate(baseUri, link, out var uri) : Uri.TryCreate(link, UriKind.Absolute, out uri);
                if (resolved && uri!.Scheme is ("http" or "https") && uri.UserInfo.Length == 0) yield return uri;
            }
        }
    }

    private static string FirstNonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static void MergeHeaders(Dictionary<string, string> target, JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var header)) return;
        if (header.ValueKind == JsonValueKind.String)
        {
            // 部分爬虫把 header 序列化成 JSON 字符串返回
            try
            {
                using var document = JsonDocument.Parse(header.GetString()!);
                if (document.RootElement.ValueKind == JsonValueKind.Object) MergeHeaders(target, document.RootElement);
            }
            catch (JsonException) { }
            return;
        }
        if (header.ValueKind == JsonValueKind.Object) MergeHeaders(target, header);
    }

    private static void MergeHeaders(Dictionary<string, string> target, JsonElement header)
    {
        foreach (var property in header.EnumerateObject())
            target[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
    }

    private static int IntValue(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return 0;
    }

    private async Task<CmsResponse> SendAsync(SourceSite site, string action, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var data = await _runtime.SendAsync(site, action, parameters, cancellationToken).ConfigureAwait(false);
        data = ReadObject(data);
        if (!data.TryGetProperty("list", out _) && (action != "home" || !data.TryGetProperty("class", out _)))
            throw new InvalidDataException("站点响应格式无法识别。");
        var response = data.Deserialize<CmsResponse>(JsonUtil.Options)!;
        // CatVod's Result.page uses Integer.MAX_VALUE when a pagination value is unknown.
        response.Page = NormalizePaginationValue(response.Page);
        response.PageCount = NormalizePaginationValue(response.PageCount);
        response.Limit = NormalizePaginationValue(response.Limit);
        response.Total = NormalizePaginationValue(response.Total);
        response.Categories ??= [];
        response.List ??= [];
        response.List.RemoveAll(static item => item is null);
        response.Categories.RemoveAll(static item => item is null);
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Content = PlaybackParsing.StripHtml(item.Content);
            item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
        }
        return response;
    }

    private static int NormalizePaginationValue(int value) => value == int.MaxValue ? 0 : value;

    private static JsonElement ReadObject(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.String)
        {
            var value = data.GetString();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("站点响应为空。");
            try
            {
                using var document = JsonDocument.Parse(value);
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new InvalidDataException("站点响应格式无法识别。");
            }
        }
        if (data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new InvalidDataException("站点响应为空。");
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("站点响应格式无法识别。");
        return data;
    }

    private static SourceSite RouteForId(SourceSite site, string id, out string? provider)
    {
        provider = CloudShareLink.TryParse(id, out var link) ? link.Provider switch
        {
            CloudProviderKind.Quark => "csp_Quark",
            CloudProviderKind.UC => "csp_UC",
            CloudProviderKind.Ali => "csp_Ali",
            _ => null
        } : null;
        return provider is null ? site : WithApi(site, provider);
    }

    private static SourceSite WithApi(SourceSite site, string api) => new()
    {
        Key = site.Key, Name = site.Name, Type = 3, Api = api, Ext = site.Ext, Jar = site.Jar, RepositorySpider = site.RepositorySpider,
        Header = site.Header, Searchable = site.Searchable, Changeable = site.Changeable, QuickSearch = site.QuickSearch, Timeout = site.Timeout
    };

    private static string StringValue(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var values = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText()).Where(item => item.Length > 0).ToArray();
            return values.Length > 1 ? values[1] : values.FirstOrDefault() ?? string.Empty;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText().Trim('"');
    }
}
