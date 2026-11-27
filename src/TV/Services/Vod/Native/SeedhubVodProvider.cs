using System.Text.RegularExpressions;
using AngleSharp.Dom;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 💡聚剧┃四盘 (csp_Seedhub / csp_SeedhubGuard) backed by seedhub.cc.
/// Surfaces Cloudflare browser challenges cleanly via SiteVerificationRequiredException for user verification,
/// and resolves Quark/UC/Ali/Baidu share links natively via CloudDriveService.
/// </summary>
public sealed partial class SeedhubVodProvider(SourceHttpClient http, CloudDriveService cloud) : INativePlaybackProvider
{
    private static readonly Uri[] Origins =
    [
        new("https://www.seedhub.cc/")
    ];

    private static readonly VodCategory[] DefaultCategories =
    [
        new() { Id = "1", Name = "电影" },
        new() { Id = "2", Name = "剧集" },
        new() { Id = "3", Name = "动漫" }
    ];

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_SeedhubGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Seedhub", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (doc, pageUri) = await FetchHtmlAsync(site, "/", cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            HomeCategoryName = "最新资源",
            Categories = [.. DefaultCategories],
            List = ParseCards(doc, pageUri, site),
            Page = 1,
            PageCount = 1
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var path = $"/categories/{Uri.EscapeDataString(categoryId)}/movies/?page={page}";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var path = $"/?s={Uri.EscapeDataString(keyword)}&page={page}";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = id.StartsWith('/') ? id : $"/movies/{id}/";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var title = doc.QuerySelector("h1")?.TextContent.Trim() ?? string.Empty;
        var pic = NativeSiteToolkit.ExtractImageUrl(doc.QuerySelector(".movie-poster img, img.poster, .cover img"), pageUri);
        var desc = NativeSiteToolkit.CollapseSpaces(doc.QuerySelector(".movie-intro, .intro, .summary, article p")?.TextContent);

        var lineNames = new List<string>();
        var lineUrls = new List<string>();
        await NativeSiteToolkit.AppendCloudShareLinesAsync(cloud, doc, lineNames, lineUrls, cancellationToken).ConfigureAwait(false);

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
        if (await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, id, token).ConfigureAwait(false) is { } direct)
            return direct;
        return new SpiderPlayResult(id, NativeSiteToolkit.CreateBrowserHeaders(), 1, 0);
    }

    private Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(SourceSite site, string relativePath, CancellationToken token) =>
        NativeSiteToolkit.FetchHtmlAsync(http, site, NativeSiteToolkit.MergeConfiguredOrigins(site, Origins), relativePath, "聚剧（SeedHub）", token, minHtmlLength: 400);

    private static List<VodItem> ParseCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a[href*='/movies/']"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var idMatch = MovieIdRegex().Match(href);
            if (!idMatch.Success) continue;
            var vodId = idMatch.Groups[1].Value;
            var name = NativeSiteToolkit.FirstNonEmpty(a.GetAttribute("title"), a.QuerySelector("img")?.GetAttribute("alt"), NativeSiteToolkit.CollapseSpaces(a.TextContent));
            if (name.Length == 0 || !seen.Add(vodId)) continue;
            var pic = NativeSiteToolkit.ExtractImageUrl(a, pageUri);
            items.Add(new VodItem
            {
                Id = vodId,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Picture = pic
            });
        }
        return items;
    }

    [GeneratedRegex(@"/movies/(\d+)/?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovieIdRegex();
}
