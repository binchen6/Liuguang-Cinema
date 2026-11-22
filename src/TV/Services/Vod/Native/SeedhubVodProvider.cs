using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
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

    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

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
        var pic = ResolveUrl(pageUri,
            doc.QuerySelector(".movie-poster img, img.poster, .cover img")?.GetAttribute("data-src")
            ?? doc.QuerySelector(".movie-poster img, img.poster, .cover img")?.GetAttribute("src"));
        var desc = CollapseSpaces(doc.QuerySelector(".movie-intro, .intro, .summary, article p")?.TextContent ?? string.Empty);

        var lineNames = new List<string>();
        var lineUrls = new List<string>();
        var seenShares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            if (!CloudShareLink.TryParse(href, out var shareLink)) continue;
            var shareUrl = shareLink.ToShareUrl();
            if (!seenShares.Add(shareUrl)) continue;
            var providerName = CloudSearchDetail.ProviderName(shareLink.Provider);
            var lineTitle = lineNames.Contains(providerName) ? $"{providerName}#{lineNames.Count + 1}" : providerName;
            try
            {
                var share = await cloud.GetShareAsync(shareUrl, cancellationToken).ConfigureAwait(false);
                if (share.Files.Count > 0)
                {
                    lineNames.Add(lineTitle);
                    lineUrls.Add(string.Join('#', share.Files.Select(CloudDriveService.FormatEpisode)));
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lineNames.Add(lineTitle);
                lineUrls.Add($"登录后加载分享${shareUrl}");
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
        if (CloudShareLink.TryParse(id, out var shareLink))
        {
            var share = await cloud.GetShareAsync(shareLink.ToShareUrl(), token).ConfigureAwait(false);
            if (share.Files.Count == 0) throw new InvalidDataException("网盘分享中没有可播放的媒体文件。");
            var resolved = await cloud.ResolveAsync(CloudDriveService.GetPlaybackId(share.Files[0]), new QualityOption("原画", 0) { IsOriginal = true }, token).ConfigureAwait(false);
            return new SpiderPlayResult(resolved.Url, new Dictionary<string, string>(resolved.Headers, StringComparer.OrdinalIgnoreCase), 0, 0);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = BrowserUa };
        return new SpiderPlayResult(id, headers, 1, 0);
    }

    private async Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(SourceSite site, string relativePath, CancellationToken token)
    {
        Uri? challengedOrigin = null;
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
                if (exchange.StatusCode == HttpStatusCode.Forbidden ||
                    exchange.HasCloudflareChallenge ||
                    html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                {
                    challengedOrigin ??= target;
                    continue;
                }
                if ((int)exchange.StatusCode is >= 200 and < 300 && html.Length > 400)
                {
                    var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
                    return (doc, exchange.Payload.FinalUri);
                }
                lastError = new HttpRequestException($"聚剧站点返回 HTTP {(int)exchange.StatusCode}");
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
                "聚剧（SeedHub）站点需要完成浏览器安全验证后访问。");
        }
        throw lastError ?? new HttpRequestException("聚剧站点当前无法访问。");
    }

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
            var name = a.GetAttribute("title")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = CollapseSpaces(a.QuerySelector("img")?.GetAttribute("alt") ?? a.TextContent);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(vodId)) continue;
            var pic = ResolveUrl(pageUri,
                a.QuerySelector("img")?.GetAttribute("data-src")
                ?? a.QuerySelector("img")?.GetAttribute("data-original")
                ?? a.QuerySelector("img")?.GetAttribute("src"));
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

    private static string ResolveUrl(Uri pageUri, string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(pageUri, raw.Trim(), out var uri) ? uri.AbsoluteUri : string.Empty;

    private static string CollapseSpaces(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"/movies/(\d+)/?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovieIdRegex();
}
