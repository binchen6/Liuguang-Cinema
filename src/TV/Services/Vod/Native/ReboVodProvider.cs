using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
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
        var (doc, pageUri) = await FetchHtmlAsync(site, "/", cancellationToken).ConfigureAwait(false);
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
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
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
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = id.StartsWith('/') ? id : $"/detail/{id}.html";
        var (doc, pageUri) = await FetchHtmlAsync(site, path, cancellationToken).ConfigureAwait(false);
        var title = doc.QuerySelector("h1.title, .stui-content__detail h1, h1")?.TextContent.Trim() ?? string.Empty;
        var pic = NativeSiteToolkit.ExtractImageUrl(doc.QuerySelector(".stui-content__thumb img, .stui-content__thumb .lazyload"), pageUri);
        var desc = NativeSiteToolkit.CollapseSpaces(doc.QuerySelector(".detail-content, .stui-content__desc, .detail-sketch")?.TextContent);

        var (lineNames, lineUrls) = NativeSiteToolkit.ExtractStuiPlaylists(
            doc, pageUri, ".stui-pannel__head h3, .stui-vodlist__head h3, .nav-tabs li a", ".stui-content__playlist");
        await NativeSiteToolkit.AppendCloudShareLinesAsync(cloud, doc, lineNames, lineUrls, cancellationToken, allowMultiplePerProvider: false).ConfigureAwait(false);

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

        var headers = NativeSiteToolkit.CreateBrowserHeaders();
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            id = new Uri(Origins[0], id).AbsoluteUri;

        try
        {
            var exchange = await http.GetExchangeAsync(id, headers, maxBytes: 2 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
            var html = Encoding.UTF8.GetString(exchange.Payload.Content);
            if (NativeSiteToolkit.TryParsePlayerAaaa(html, out var player))
            {
                if (await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, player.DecodedUrl, token, "热播夸克分享中没有可播放文件。").ConfigureAwait(false) is { } cloudDirect)
                    return cloudDirect;

                if (player.DecodedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    PlayUrlResolver.IsDirectMediaUrl(player.DecodedUrl))
                {
                    return new SpiderPlayResult(player.DecodedUrl, headers, 0, 0);
                }

                if (player.From.Equals("rrmj", StringComparison.OrdinalIgnoreCase) && player.RawUrl.Length > 0)
                {
                    var artUrl = new Uri(exchange.Payload.FinalUri, $"/static/player/artplayer/?url={Uri.EscapeDataString(player.RawUrl)}").AbsoluteUri;
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

    private Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(SourceSite site, string relativePath, CancellationToken token) =>
        NativeSiteToolkit.FetchHtmlAsync(http, site, Origins, relativePath, "热播", token, detectChallenge: false, minHtmlLength: 400);

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
            var name = NativeSiteToolkit.FirstNonEmpty(a.GetAttribute("title"), NativeSiteToolkit.CollapseSpaces(a.TextContent));
            if (name.Length == 0 || !seen.Add(vodId)) continue;
            var pic = NativeSiteToolkit.ExtractImageUrl(a, pageUri);
            var remark = NativeSiteToolkit.CollapseSpaces(a.QuerySelector(".pic-text")?.TextContent);
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

    [GeneratedRegex(@"/detail/(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailIdRegex();
}
