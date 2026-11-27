using System.Text.RegularExpressions;
using AngleSharp.Dom;
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
        var pic = NativeSiteToolkit.ExtractImageUrl(doc.QuerySelector(".stui-content__thumb img, .stui-content__thumb .lazyload, .lazyload"), pageUri);
        var desc = NativeSiteToolkit.CollapseSpaces(doc.QuerySelector(".detail-content, .stui-content__desc")?.TextContent);

        var (lineNames, lineUrls) = NativeSiteToolkit.ExtractStuiPlaylists(
            doc, pageUri, ".stui-vodlist__head h3, .nav-tabs li a", ".stui-content__playlist");

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
        return Task.FromResult(new SpiderPlayResult(id, NativeSiteToolkit.CreateBrowserHeaders(), 1, 0));
    }

    private Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(SourceSite site, string relativePath, CancellationToken token) =>
        NativeSiteToolkit.FetchHtmlAsync(http, site, NativeSiteToolkit.MergeConfiguredOrigins(site, FallbackOrigins), relativePath, "立播", token, maxBytes: 2 * 1024 * 1024, minHtmlLength: 400);

    private static List<VodItem> ParseCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a.stui-vodlist__thumb[href*='/detail/'], a[href*='/detail/']"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var name = NativeSiteToolkit.FirstNonEmpty(a.GetAttribute("title"), a.TextContent);
            if (href.Length == 0 || name.Length == 0 || !seen.Add(href)) continue;
            var pic = NativeSiteToolkit.ExtractImageUrl(a, pageUri);
            var remark = a.QuerySelector(".pic-text")?.TextContent.Trim() ?? string.Empty;
            items.Add(new VodItem { Id = href, SiteKey = site.Key, SiteName = site.Name, Name = name, Picture = pic, Remarks = remark });
        }
        return items;
    }

    [GeneratedRegex(@"/type/(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypePathRegex();
}
