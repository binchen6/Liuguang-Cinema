using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 📺热播┃多线 (csp_AppTT / csp_AppTTGuard) backed by rebozj.cc / rebozj.pro.
/// Supports direct MacCMS playback, Quark/cloud share playback via CloudDriveService, and WebView2 sniff fallback.
/// </summary>
public sealed partial class ReboVodProvider(SourceHttpClient http, CloudDriveService cloud) : INativePlaybackProvider
{
    private static readonly Uri[] Origins =
    [
        new("https://www.rebozj.cc/"),
        new("https://www.rebozj.pro/")
    ];

    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private static readonly VodCategory[] DefaultCategories =
    [
        new() { Id = "1", Name = "电影" },
        new() { Id = "2", Name = "电视剧" },
        new() { Id = "4", Name = "动漫" },
        new() { Id = "5", Name = "综艺" },
        new() { Id = "3", Name = "纪录片" }
    ];

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_AppTTGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_AppTT", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (doc, pageUri) = await FetchHtmlAsync("/", cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            HomeCategoryName = "最新热播",
            Categories = [.. DefaultCategories],
            List = ParseCards(doc, pageUri, site),
            Page = 1,
            PageCount = 1
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var path = page == 1
            ? $"/show/{Uri.EscapeDataString(categoryId)}-----------.html"
            : $"/show/{Uri.EscapeDataString(categoryId)}--------{page}---.html";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var path = page == 1
            ? $"/search/{Uri.EscapeDataString(keyword)}-------------.html"
            : $"/search/{Uri.EscapeDataString(keyword)}----------{page}---.html";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = id.StartsWith('/') ? id : $"/detail/{id}.html";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var title = doc.QuerySelector("h1.title, .stui-content__detail h1, h1")?.TextContent.Trim() ?? string.Empty;
        var pic = ResolveUrl(pageUri,
            doc.QuerySelector(".stui-content__thumb img, .stui-content__thumb .lazyload")?.GetAttribute("data-original")
            ?? doc.QuerySelector(".stui-content__thumb img")?.GetAttribute("src"));
        var desc = CollapseSpaces(
            doc.QuerySelector(".detail-content, .stui-content__desc, .detail-sketch")?.TextContent ?? string.Empty);

        var lineNames = new List<string>();
        var lineUrls = new List<string>();
        var tabs = doc.QuerySelectorAll(".stui-pannel__head h3, .stui-vodlist__head h3, .nav-tabs li a")
            .Select(e => CollapseSpaces(e.TextContent))
            .Where(s => s.Length > 0 && !s.Contains("简介", StringComparison.Ordinal) && !s.Contains("猜你喜欢", StringComparison.Ordinal) && !s.Contains("推荐", StringComparison.Ordinal))
            .ToList();

        var lists = doc.QuerySelectorAll(".stui-content__playlist");
        for (var i = 0; i < lists.Length; i++)
        {
            var eps = new List<string>();
            foreach (var a in lists[i].QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
                var label = CollapseSpaces(a.TextContent);
                if (href.Length == 0 || label.Length == 0) continue;
                var fullUri = new Uri(pageUri, href).AbsoluteUri;
                eps.Add($"{label.Replace("$", "＄").Replace("#", "＃")}${fullUri}");
            }
            if (eps.Count == 0) continue;
            var lineName = i < tabs.Count ? tabs[i] : $"线路{lineNames.Count + 1}";
            lineNames.Add(lineName);
            lineUrls.Add(string.Join('#', eps));
        }

        // Also scan any embedded cloud share links on the detail page
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            if (!CloudShareLink.TryParse(href, out var shareLink)) continue;
            var providerName = CloudSearchDetail.ProviderName(shareLink.Provider);
            if (lineNames.Any(n => n.Contains(providerName, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var share = await cloud.GetShareAsync(shareLink.ToShareUrl(), cancellationToken).ConfigureAwait(false);
                if (share.Files.Count > 0)
                {
                    lineNames.Add(providerName);
                    lineUrls.Add(string.Join('#', share.Files.Select(CloudDriveService.FormatEpisode)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lineNames.Add(providerName);
                lineUrls.Add($"登录后加载分享${shareLink.ToShareUrl()}");
            }
        }

        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title,
            Picture = pic,
            Content = desc,
            PlayFrom = string.Join("$$$", lineNames),
            PlayUrl = string.Join("$$$", lineUrls)
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        if (CloudShareLink.TryParse(id, out var directShare))
        {
            var share = await cloud.GetShareAsync(directShare.ToShareUrl(), token).ConfigureAwait(false);
            if (share.Files.Count == 0) throw new InvalidDataException("网盘分享中没有可播放的媒体文件。");
            var resolved = await cloud.ResolveAsync(CloudDriveService.GetPlaybackId(share.Files[0]), new QualityOption("原画", 0) { IsOriginal = true }, token).ConfigureAwait(false);
            return new SpiderPlayResult(resolved.Url, new Dictionary<string, string>(resolved.Headers, StringComparer.OrdinalIgnoreCase), 0, 0);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa
        };
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            id = new Uri(Origins[0], id).AbsoluteUri;

        try
        {
            var exchange = await http.GetExchangeAsync(id, headers, maxBytes: 2 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
            var html = Encoding.UTF8.GetString(exchange.Payload.Content);
            var match = PlayerAaaaRegex().Match(html);
            if (match.Success)
            {
                using var doc = JsonDocument.Parse(match.Groups[1].Value);
                var root = doc.RootElement;
                var rawUrl = root.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? string.Empty : string.Empty;
                var from = root.TryGetProperty("from", out var fEl) ? fEl.GetString() ?? string.Empty : string.Empty;
                var encrypt = root.TryGetProperty("encrypt", out var eEl) && eEl.TryGetInt32(out var enc) ? enc : 0;

                var decodedUrl = DecodeMacUrl(rawUrl, encrypt);
                if (CloudShareLink.TryParse(decodedUrl, out var shareLink))
                {
                    var share = await cloud.GetShareAsync(shareLink.ToShareUrl(), token).ConfigureAwait(false);
                    if (share.Files.Count == 0) throw new InvalidDataException("热播夸克分享中没有可播放文件。");
                    var resolved = await cloud.ResolveAsync(CloudDriveService.GetPlaybackId(share.Files[0]), new QualityOption("原画", 0) { IsOriginal = true }, token).ConfigureAwait(false);
                    return new SpiderPlayResult(resolved.Url, new Dictionary<string, string>(resolved.Headers, StringComparer.OrdinalIgnoreCase), 0, 0);
                }

                if (decodedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    (decodedUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || decodedUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase)))
                {
                    return new SpiderPlayResult(decodedUrl, headers, 0, 0);
                }

                if (from.Equals("rrmj", StringComparison.OrdinalIgnoreCase) && rawUrl.Length > 0)
                {
                    var artUrl = new Uri(exchange.Payload.FinalUri, $"/static/player/artplayer/?url={Uri.EscapeDataString(rawUrl)}").AbsoluteUri;
                    headers["Referer"] = exchange.Payload.FinalUri.AbsoluteUri;
                    return new SpiderPlayResult(artUrl, headers, 1, 0);
                }
            }
        }
        catch (CloudAuthenticationException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to WebView2 sniff on play page
        }

        return new SpiderPlayResult(id, headers, 1, 0);
    }

    private async Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(string relativePath, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var origin in Origins)
        {
            var target = new Uri(origin, relativePath);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = BrowserUa,
                ["Referer"] = origin.AbsoluteUri
            };
            try
            {
                var exchange = await http.GetExchangeAsync(target.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
                var html = Encoding.UTF8.GetString(exchange.Payload.Content);
                if ((int)exchange.StatusCode is >= 200 and < 300 && html.Length > 400)
                {
                    var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
                    return (doc, exchange.Payload.FinalUri);
                }
                lastError = new HttpRequestException($"热播站点返回 HTTP {(int)exchange.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException("热播站点当前无法访问。");
    }

    private static List<VodItem> ParseCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a.stui-vodlist__thumb[href*='/detail/'], a[href*='/detail/']"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var idMatch = DetailIdRegex().Match(href);
            if (!idMatch.Success) continue;
            var vodId = idMatch.Groups[1].Value;
            var name = a.GetAttribute("title")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = CollapseSpaces(a.TextContent);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(vodId)) continue;
            var pic = ResolveUrl(pageUri,
                a.GetAttribute("data-original")
                ?? a.QuerySelector("img")?.GetAttribute("data-original")
                ?? a.QuerySelector("img")?.GetAttribute("src"));
            var remark = CollapseSpaces(a.QuerySelector(".pic-text")?.TextContent ?? string.Empty);
            items.Add(new VodItem
            {
                Id = vodId,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Picture = pic,
                Remarks = remark
            });
        }
        return items;
    }

    private static string DecodeMacUrl(string raw, int encrypt)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            return encrypt switch
            {
                1 => Uri.UnescapeDataString(raw),
                2 => Uri.UnescapeDataString(Encoding.UTF8.GetString(Convert.FromBase64String(raw))),
                _ => raw
            };
        }
        catch { return raw; }
    }

    private static string ResolveUrl(Uri pageUri, string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(pageUri, raw.Trim(), out var uri) ? uri.AbsoluteUri : string.Empty;

    private static string CollapseSpaces(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"/detail/(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailIdRegex();

    [GeneratedRegex(@"var\s+player_aaaa\s*=\s*(\{[\s\S]*?\})\s*;?\s*</script>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerAaaaRegex();
}
