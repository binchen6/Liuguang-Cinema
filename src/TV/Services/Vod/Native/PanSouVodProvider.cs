using System.Collections.Concurrent;
using System.Text.Json;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Playback;

namespace TV.Services.Vod.Native;

public sealed class PanSouVodProvider(SourceHttpClient http, CloudDriveService cloud) : INativePlaybackProvider
{
    private sealed record CachedSearch(Lazy<Task<CmsResponse>> Request, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, CachedSearch> _cache = new(StringComparer.Ordinal);
    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_S_zpsGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_PanSou", StringComparison.OrdinalIgnoreCase)) &&
        GetEndpoints(site.Ext).Count > 0;

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse());

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetHomeAsync(site, cancellationToken);

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (page > 1 || string.IsNullOrWhiteSpace(keyword)) return new CmsResponse();
        var endpoints = GetEndpoints(site.Ext);
        if (endpoints.Count == 0) throw new InvalidDataException("盘搜接口地址无效。");
        var key = site.Key + "\n" + site.Name + "\n" + string.Join('\n', endpoints) + "\n" + keyword.Trim();
        var now = DateTimeOffset.UtcNow;
        if (_cache.Count >= 128)
            foreach (var pair in _cache.OrderBy(pair => pair.Value.ExpiresAt).Take(Math.Max(8, _cache.Count - 120))) _cache.TryRemove(pair.Key, out _);
        var candidate = new CachedSearch(new Lazy<Task<CmsResponse>>(() => ReadSearchAsync(endpoints, keyword, site)), now.AddMinutes(2));
        var entry = _cache.AddOrUpdate(key, candidate, (_, current) => current.ExpiresAt > now &&
            (!current.Request.IsValueCreated || !current.Request.Value.IsCompleted || current.Request.Value.IsCompletedSuccessfully) ? current : candidate);
        try { return await entry.Request.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            if (entry.Request.Value.IsFaulted || entry.Request.Value.IsCanceled) _cache.TryRemove(new KeyValuePair<string, CachedSearch>(key, entry));
            throw;
        }
    }

    private static readonly Dictionary<string, string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
        ["Accept"] = "application/json, text/plain, */*"
    };

    private async Task<CmsResponse> ReadSearchAsync(IReadOnlyList<Uri> endpoints, string keyword, SourceSite site)
    {
        var timeoutSpan = TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 5, 30));
        using var deadline = new CancellationTokenSource(timeoutSpan);
        var attempt = endpoints.Select((endpoint, index) => TryReadEndpointAsync(endpoint, keyword, site,
            TimeSpan.FromMilliseconds(index * 350), timeoutSpan, deadline.Token)).ToList();
        Exception? failure = null;
        CmsResponse? empty = null;
        while (attempt.Count > 0)
        {
            var completed = await Task.WhenAny(attempt).ConfigureAwait(false);
            attempt.Remove(completed);
            var outcome = await completed.ConfigureAwait(false);
            if (outcome.Response is { List.Count: > 0 })
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                return outcome.Response;
            }
            empty ??= outcome.Response;
            failure ??= outcome.Error;
        }
        return empty ?? throw failure ?? new TimeoutException("盘搜请求超时。");
    }

    private async Task<(CmsResponse? Response, Exception? Error)> TryReadEndpointAsync(Uri endpoint, string keyword, SourceSite site,
        TimeSpan delay, TimeSpan timeoutSpan, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
            var request = new UriBuilder(endpoint)
            {
                Query = "kw=" + Uri.EscapeDataString(keyword.Trim()) + "&res=merge&cloud_types=quark,uc,aliyun,baidu" +
                    (Property(site.Ext, "includeMagnet").ValueKind == JsonValueKind.True ? ",magnet" : string.Empty)
            };
            var responsePayload = await http.GetExchangeAsync(request.Uri.AbsoluteUri, RequestHeaders, maxBytes: 4 * 1024 * 1024,
                cancellationToken: token, requestTimeout: timeoutSpan, scopeRedirectHeaders: true).ConfigureAwait(false);
            var text = SourceHttpClient.DecodeText(responsePayload.Payload.Content);
            if (NativeSiteToolkit.IsBrowserChallenge(responsePayload, text))
                throw new SiteVerificationRequiredException(site.Key, site.Name, responsePayload.Payload.FinalUri,
                    SiteVerificationKind.BrowserChallenge, "搜索接口需要在浏览器中完成验证。");
            if ((int)responsePayload.StatusCode is < 200 or >= 300)
                throw new HttpRequestException($"搜索接口返回 HTTP {(int)responsePayload.StatusCode}。", null, responsePayload.StatusCode);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (Property(root, "code").ToString() != "0") throw new InvalidDataException("盘搜请求失败。");
            var data = Property(root, "data");
            var groups = Property(data, "merged_by_type");
            if (groups.ValueKind != JsonValueKind.Object)
            {
                if (Property(data, "total").ToString() == "0") return (new CmsResponse { Page = 1, PageCount = 1 }, null);
                throw new InvalidDataException("盘搜返回了无法识别的结果。");
            }

            var response = new CmsResponse { Page = 1, PageCount = 1 };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in group.Value.EnumerateArray())
                {
                    var address = ReadString(item, "url").Trim();
                    string shareUrl;
                    string provider;
                    if (CloudShareLink.TryParse(address, out var link))
                    {
                        shareUrl = link.ToShareUrl(ReadString(item, "password").Trim());
                        provider = CloudSearchDetail.ProviderName(link.Provider);
                    }
                    else if (MagnetPlayback.TryParse(address, out _)) { shareUrl = address; provider = "磁力"; }
                    else continue;
                    if (!seen.Add(shareUrl)) continue;
                    var name = PlaybackParsing.StripHtml(ReadString(item, "note"));
                    response.List.Add(new VodItem
                    {
                        Id = shareUrl, SiteKey = site.Key, SiteName = site.Name,
                        Name = Title(name, provider), Remarks = provider,
                        Content = name.Length > 480 ? name[..480] : name
                    });
                }
            }
            response.List = response.List.OrderByDescending(item => SearchScoring.Score(keyword, item)).ToList();
            response.Total = response.List.Count;
            return (response, null);
        }
        catch (OperationCanceledException ex)
        {
            return (null, new TimeoutException("盘搜请求超时。", ex));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or SiteVerificationRequiredException)
        { return (null, ex); }
    }

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        if (!MagnetPlayback.TryParse(id, out var magnet)) return CloudSearchDetail.ReadAsync(cloud, site, id, id, null, cancellationToken);
        var name = string.IsNullOrWhiteSpace(magnet.Name) ? "磁力资源" : magnet.Name;
        return Task.FromResult(new VodItem { Id = id, SiteKey = site.Key, SiteName = site.Name, Name = name,
            PlayFrom = "磁力资源", PlayUrl = "播放$" + id });
    }

    public async Task<Spider.SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        if (MagnetPlayback.TryParse(id, out _)) return new Spider.SpiderPlayResult(id, new Dictionary<string, string>(), 0, 0);
        return await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, id, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("搜索结果没有可播放的网盘地址。");
    }

    private static IReadOnlyList<Uri> GetEndpoints(JsonElement extension)
    {
        var values = new List<string?> { extension.ValueKind == JsonValueKind.String ? extension.GetString() : ReadString(extension, "siteUrl") };
        if (Property(extension, "siteUrls") is { ValueKind: JsonValueKind.Array } mirrors)
            values.AddRange(mirrors.EnumerateArray().Take(3).Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()));
        return values.Select(value =>
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !MediaRequestHeaders.IsHttpAddress(uri) ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith("/api/search", StringComparison.Ordinal)) path += "/api/search";
            return new UriBuilder(uri) { Path = path }.Uri;
        }).OfType<Uri>().Distinct().ToArray();
    }

    private static string Title(string note, string provider)
    {
        var line = note.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return provider + "分享";
        return line.Length <= 100 ? line : line[..100].TrimEnd();
    }

    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    private static string ReadString(JsonElement item, string name) =>
        Property(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;
}
