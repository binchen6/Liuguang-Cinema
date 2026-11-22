using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 🌟立播┃不卡 (csp_Libvio / csp_LibvioGuard).
/// Uses SourceHttpClient + SiteSessionStore so browser-verification 403 challenges surface
/// cleanly via SiteVerificationRequiredException for interactive user verification.
/// </summary>
public sealed partial class LibvioVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private static readonly Uri[] FallbackOrigins =
    [
        new("https://www.libvio.pw/"),
        new("https://www.libvio.to/"),
        new("https://www.libvio.io/")
    ];

    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_LibvioGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Libvio", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (doc, pageUri) = await FetchHtmlAsync(site, "/", cancellationToken).ConfigureAwait(false);
        var categories = new List<VodCategory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a[href*='/type/']"))
        {
            var m = TypePathRegex().Match(a.GetAttribute("href") ?? string.Empty);
            var name = a.TextContent.Trim();
            if (m.Success && name.Length > 0 && seen.Add(m.Groups[1].Value))
                categories.Add(new VodCategory { Id = m.Groups[1].Value, Name = name });
        }
        return new CmsResponse
        {
            HomeCategoryName = "最新推荐",
            Categories = categories,
            List = ParseCards(doc, pageUri, site),
            Page = 1,
            PageCount = 1
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var path = $"/show/{Uri.EscapeDataString(categoryId)}--------{page}---.html";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var path = $"/search/{Uri.EscapeDataString(keyword)}----------{page}---.html";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = id.StartsWith('/') ? id : "/detail/" + id + ".html";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var title = doc.QuerySelector("h1.title, h1")?.TextContent.Trim() ?? string.Empty;
        var pic = ResolveUrl(pageUri, doc.QuerySelector(".stui-content__thumb img, .lazyload")?.GetAttribute("data-original")
            ?? doc.QuerySelector(".stui-content__thumb img")?.GetAttribute("src"));
        var desc = doc.QuerySelector(".detail-content, .stui-content__desc")?.TextContent.Trim() ?? string.Empty;

        var lineNames = new List<string>();
        var lineUrls = new List<string>();
        var tabs = doc.QuerySelectorAll(".stui-vodlist__head h3, .nav-tabs li a").Select(e => e.TextContent.Trim()).Where(s => s.Length > 0).ToList();
        var lists = doc.QuerySelectorAll(".stui-content__playlist");
        for (var i = 0; i < lists.Length; i++)
        {
            var eps = new List<string>();
            foreach (var a in lists[i].QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
                var label = a.TextContent.Trim();
                if (href.Length > 0 && label.Length > 0)
                    eps.Add($"{label.Replace("$", "＄").Replace("#", "＃")}${new Uri(pageUri, href).AbsoluteUri}");
            }
            if (eps.Count == 0) continue;
            lineNames.Add(i < tabs.Count ? tabs[i] : $"线路{lineNames.Count + 1}");
            lineUrls.Add(string.Join('#', eps));
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

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = BrowserUa };
        return Task.FromResult(new SpiderPlayResult(id, headers, 1, 0));
    }

    private async Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(SourceSite site, string relativePath, CancellationToken token)
    {
        Uri? challengedOrigin = null;
        Exception? lastError = null;
        foreach (var origin in Origins(site))
        {
            var target = new Uri(origin, relativePath);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = BrowserUa,
                ["Referer"] = origin.AbsoluteUri
            };
            try
            {
                var exchange = await http.GetExchangeAsync(target.AbsoluteUri, headers, maxBytes: 2 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
                var html = Encoding.UTF8.GetString(exchange.Payload.Content);
                if (exchange.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    exchange.HasCloudflareChallenge ||
                    html.Contains("browser verification required", StringComparison.OrdinalIgnoreCase))
                {
                    challengedOrigin ??= target;
                    continue;
                }
                if ((int)exchange.StatusCode is >= 200 and < 300 && html.Length > 400)
                {
                    var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
                    return (doc, exchange.Payload.FinalUri);
                }
                lastError = new HttpRequestException($"立播返回 HTTP {(int)exchange.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        if (challengedOrigin is not null)
        {
            throw new SiteVerificationRequiredException(
                site.Key, site.Name, challengedOrigin, SiteVerificationKind.BrowserChallenge,
                "立播站点需要完成浏览器安全验证后访问。");
        }
        throw lastError ?? new HttpRequestException("立播站点当前无法访问。");
    }

    private static IEnumerable<Uri> Origins(SourceSite site)
    {
        var list = new List<Uri>();
        if (site.Ext.ValueKind == JsonValueKind.Object &&
            site.Ext.TryGetProperty("siteUrl", out var urlEl) &&
            urlEl.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var configured) &&
            configured.Scheme is "http" or "https")
        {
            list.Add(new Uri(configured.GetLeftPart(UriPartial.Authority) + "/"));
        }
        foreach (var fb in FallbackOrigins)
        {
            if (!list.Any(u => u.Host.Equals(fb.Host, StringComparison.OrdinalIgnoreCase)))
                list.Add(fb);
        }
        return list;
    }

    private static List<VodItem> ParseCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a.stui-vodlist__thumb[href*='/detail/'], a[href*='/detail/']"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var name = a.GetAttribute("title")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = a.TextContent.Trim();
            if (href.Length == 0 || string.IsNullOrWhiteSpace(name) || !seen.Add(href)) continue;
            var pic = ResolveUrl(pageUri, a.GetAttribute("data-original") ?? a.QuerySelector("img")?.GetAttribute("data-original") ?? a.QuerySelector("img")?.GetAttribute("src"));
            var remark = a.QuerySelector(".pic-text")?.TextContent.Trim() ?? string.Empty;
            items.Add(new VodItem { Id = href, SiteKey = site.Key, SiteName = site.Name, Name = name, Picture = pic, Remarks = remark });
        }
        return items;
    }

    private static string ResolveUrl(Uri pageUri, string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(pageUri, raw.Trim(), out var uri) ? uri.AbsoluteUri : string.Empty;

    [GeneratedRegex(@"/type/(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypePathRegex();
}
