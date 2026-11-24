using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native cloud-share search provider backed by public PanSearch.me Next.js JSON index
/// with shared caching, gentle rate pacing, and PanSo.vip fallback on HTTP 429/5xx.
/// Powers 🐌盘她┃夸父 (csp_YpanSoGuard), 🌶️抠抠┃搜搜 (csp_KkSsGuard), and 🌈优汐┃搜搜 (csp_UuSsGuard).
/// </summary>
public sealed partial class PanSearchVodProvider(SourceHttpClient http, CloudDriveService cloud) : INativeVodProvider
{
    private sealed record RawHit(string ShareUrl, CloudProviderKind Provider, string Title, string Content);
    private sealed record CachedRawSearch(List<RawHit> Hits, int Total, DateTimeOffset ExpiresAt);

    private const string BaseUrl = "https://www.pansearch.me";
    private const string FallbackOrigin = "https://panso.vip";
    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private readonly SemaphoreSlim _buildIdGate = new(1, 1);
    private readonly SemaphoreSlim _paceGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedRawSearch> _rawCache = new(StringComparer.Ordinal);
    private string? _buildId;
    private DateTimeOffset _buildIdExpiresAt;
    private DateTimeOffset _nextPanSearchAllowedAt;
    private DateTimeOffset _panSearchCooldownUntil;

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_YpanSoGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_YpanSo", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_KkSsGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_KkSs", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_UuSsGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_UuSs", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_PanSearch", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse());

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetHomeAsync(site, cancellationToken);

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse();
        page = Math.Max(1, page);
        var preferred = PreferredKind(site);

        var raw = await GetRawSearchAsync(keyword, page, cancellationToken).ConfigureAwait(false);
        if (raw is not null)
        {
            var filtered = preferred is null
                ? raw.Hits
                : raw.Hits.Where(h => h.Provider == preferred.Value).ToList();
            if (filtered.Count == 0 && raw.Hits.Count > 0) filtered = raw.Hits;

            var items = filtered.Select(h => new VodItem
            {
                Id = h.ShareUrl,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = h.Title,
                Remarks = CloudSearchDetail.ProviderName(h.Provider),
                Content = h.Content
            }).OrderByDescending(item => SearchScoring.Score(keyword, item)).ToList();

            if (items.Count > 0)
            {
                return new CmsResponse
                {
                    List = items,
                    Page = page,
                    PageCount = Math.Max(1, Math.Min(10, (int)Math.Ceiling(raw.Total / 10d))),
                    Limit = 10,
                    Total = raw.Total
                };
            }
        }

        // Automatic fallback to public panso.vip search when pansearch.me rate-limits (429/5xx)
        return await SearchFallbackPanSoAsync(site, keyword, page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        if (CloudShareLink.TryParse(id, out _))
            return await CloudSearchDetail.ReadAsync(cloud, site, id, id, null, cancellationToken).ConfigureAwait(false);

        // Handle fallback panso.vip /doc/ detail pages
        if (Uri.TryCreate(id, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("panso.vip", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/doc/", StringComparison.Ordinal))
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = BrowserUa };
            var payload = await http.GetBytesAsync(uri.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: cancellationToken).ConfigureAwait(false);
            var doc = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(payload.Content), cancellationToken).ConfigureAwait(false);
            var title = doc.QuerySelector("h1")?.TextContent.Trim();
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                if (Uri.TryCreate(payload.FinalUri, anchor.GetAttribute("href"), out var target) &&
                    CloudShareLink.TryParse(target.AbsoluteUri, out _))
                {
                    return await CloudSearchDetail.ReadAsync(cloud, site, id, target.AbsoluteUri, title, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        throw new InvalidDataException("网盘分享地址无效。");
    }

    private async Task<CachedRawSearch?> GetRawSearchAsync(string keyword, int page, CancellationToken token)
    {
        var key = $"{keyword}\n{page}";
        var now = DateTimeOffset.UtcNow;
        if (_rawCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached;

        if (now < _panSearchCooldownUntil)
            return null;

        await _paceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_rawCache.TryGetValue(key, out cached) && cached.ExpiresAt > now)
                return cached;
            if (now < _panSearchCooldownUntil)
                return null;

            var delay = _nextPanSearchAllowedAt - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);
            _nextPanSearchAllowedAt = DateTimeOffset.UtcNow.AddMilliseconds(450);

            var buildId = await GetBuildIdAsync(forceRefresh: false, token).ConfigureAwait(false);
            var (payload, status) = await FetchSearchJsonAsync(buildId, keyword, (page - 1) * 10, token).ConfigureAwait(false);
            if (status == System.Net.HttpStatusCode.NotFound)
            {
                buildId = await GetBuildIdAsync(forceRefresh: true, token).ConfigureAwait(false);
                (payload, status) = await FetchSearchJsonAsync(buildId, keyword, (page - 1) * 10, token).ConfigureAwait(false);
            }

            if (status == System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500 || payload is null)
            {
                _panSearchCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(45);
                return null;
            }

            using var doc = JsonDocument.Parse(payload);
            var hits = ParseRawHits(doc.RootElement, out var total);
            var entry = new CachedRawSearch(hits, total, DateTimeOffset.UtcNow.AddMinutes(3));
            _rawCache[key] = entry;
            return entry;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            if (ex is OperationCanceledException or TimeoutException)
                _panSearchCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(45);
            return null;
        }
        finally
        {
            _paceGate.Release();
        }
    }

    private async Task<CmsResponse> SearchFallbackPanSoAsync(SourceSite site, string keyword, int page, CancellationToken token)
    {
        var url = $"{FallbackOrigin}/search?exact=false&format=video&page={page}&q={Uri.EscapeDataString(keyword)}&share_time=&type=&user=";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = BrowserUa };
        var payload = await http.GetBytesAsync(url, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        var doc = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(payload.Content), token).ConfigureAwait(false);
        var response = new CmsResponse { Page = page, PageCount = page };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in doc.QuerySelectorAll("a[href*='/doc/']"))
        {
            if (!Uri.TryCreate(payload.FinalUri, anchor.GetAttribute("href"), out var detail)) continue;
            if (!detail.AbsolutePath.StartsWith("/doc/", StringComparison.Ordinal)) continue;
            var name = anchor.GetAttribute("title")?.Trim();
            if (string.IsNullOrEmpty(name)) name = anchor.TextContent.Trim();
            if (name.Length == 0 || name == "详情页" || !seen.Add(detail.AbsoluteUri)) continue;
            response.List.Add(new VodItem
            {
                Id = detail.AbsoluteUri,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Remarks = "网盘分享"
            });
        }
        response.Total = response.List.Count;
        return response;
    }

    private async Task<string> GetBuildIdAsync(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_buildId) && DateTimeOffset.UtcNow < _buildIdExpiresAt)
            return _buildId!;

        await _buildIdGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_buildId) && DateTimeOffset.UtcNow < _buildIdExpiresAt)
                return _buildId!;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = BrowserUa };
            var page = await http.GetBytesAsync(BaseUrl + "/", headers, maxBytes: 2 * 1024 * 1024, cancellationToken: token, requestTimeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var html = Encoding.UTF8.GetString(page.Content);
            var match = BuildIdRegex().Match(html);
            if (!match.Success) throw new InvalidDataException("网盘搜索索引暂时不可用。");
            _buildId = match.Groups[1].Value;
            _buildIdExpiresAt = DateTimeOffset.UtcNow.AddHours(2);
            return _buildId;
        }
        finally
        {
            _buildIdGate.Release();
        }
    }

    private async Task<(byte[]? Content, System.Net.HttpStatusCode Status)> FetchSearchJsonAsync(string buildId, string keyword, int offset, CancellationToken token)
    {
        var url = $"{BaseUrl}/_next/data/{Uri.EscapeDataString(buildId)}/search.json?keyword={Uri.EscapeDataString(keyword)}&offset={offset}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Referer"] = BaseUrl + "/"
        };
        var exchange = await http.GetExchangeAsync(url, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token, requestTimeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return ((int)exchange.StatusCode is >= 200 and < 300 ? exchange.Payload.Content : null, exchange.StatusCode);
    }

    private static List<RawHit> ParseRawHits(JsonElement root, out int total)
    {
        total = 0;
        var results = new List<RawHit>();
        if (!root.TryGetProperty("pageProps", out var pageProps) ||
            !pageProps.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("data", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return results;

        if (data.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt32(out var tv)) total = tv;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in array.EnumerateArray())
        {
            var rawContent = entry.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;
            if (rawContent.Length == 0) continue;

            var plain = PlaybackParsing.StripHtml(rawContent);
            foreach (Match linkMatch in UrlRegex().Matches(rawContent))
            {
                var candidate = linkMatch.Value.TrimEnd('"', '\'', '<', '>', ';', ',', ')', ']', '。', '，');
                if (!CloudShareLink.TryParse(candidate, out var link)) continue;
                var shareUrl = link.ToShareUrl();
                if (!seen.Add(shareUrl)) continue;

                var providerName = CloudSearchDetail.ProviderName(link.Provider);
                var title = ExtractTitle(plain, candidate, providerName);
                results.Add(new RawHit(shareUrl, link.Provider, title, plain.Length > 480 ? plain[..480] : plain));
            }
        }
        return results;
    }

    private static string ExtractTitle(string plain, string url, string providerName)
    {
        var lines = plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.Contains(url, StringComparison.OrdinalIgnoreCase))
            {
                var before = line[..line.IndexOf(url, StringComparison.OrdinalIgnoreCase)].Trim(' ', ':', '：', '-', '—');
                if (before.Length >= 2) return TrimLength(before);
            }
        }
        var first = lines.FirstOrDefault(l => !l.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(first) ? providerName + "分享" : TrimLength(first);
    }

    private static string TrimLength(string value) => value.Length <= 90 ? value : value[..90].TrimEnd();

    private static CloudProviderKind? PreferredKind(SourceSite site)
    {
        if (site.Api.Contains("YpanSo", StringComparison.OrdinalIgnoreCase)) return CloudProviderKind.Quark;
        if (site.Api.Contains("UuSs", StringComparison.OrdinalIgnoreCase)) return CloudProviderKind.UC;
        return null;
    }

    [GeneratedRegex(@"""buildId""\s*:\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdRegex();
    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
