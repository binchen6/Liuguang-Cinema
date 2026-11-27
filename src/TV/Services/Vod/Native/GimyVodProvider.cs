using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 🍊橘猫┃多线 / 剧迷 (csp_JumiTv / csp_JumiTvGuard) backed by gimytw.cc.
/// </summary>
public sealed partial class GimyVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private static readonly Uri Origin = new("https://gimytw.cc/");
    private const string BrowserUa = NativeSiteToolkit.BrowserUserAgent;

    private static readonly VodCategory[] DefaultCategories =
    [
        new() { Id = "drama", Name = "电视剧" },
        new() { Id = "movie", Name = "电影" },
        new() { Id = "variety", Name = "综艺" },
        new() { Id = "anime", Name = "动漫" },
        new() { Id = "drama?country=cn", Name = "陆剧" },
        new() { Id = "drama?country=kr", Name = "韩剧" },
        new() { Id = "drama?country=us", Name = "美剧" },
        new() { Id = "drama?country=jp", Name = "日剧" },
        new() { Id = "drama?country=tw", Name = "台剧" }
    ];

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_JumiTvGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_JumiTv", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (doc, pageUri) = await FetchHtmlAsync("/", cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            HomeCategoryName = "热门影视",
            Categories = [.. DefaultCategories],
            List = ParseCards(doc, pageUri, site),
            Page = 1,
            PageCount = 1
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var path = categoryId.Contains('?', StringComparison.Ordinal)
            ? $"/{categoryId.TrimStart('/')}&page={page}"
            : $"/{categoryId.Trim('/')}/?page={page}";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse
        {
            List = items,
            Page = page,
            PageCount = items.Count > 0 ? page + 1 : page,
            Total = items.Count
        };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0 || page > 1) return new CmsResponse { Page = page, PageCount = 1 };
        var path = $"/search?q={Uri.EscapeDataString(keyword)}";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse { List = items, Page = 1, PageCount = 1, Total = items.Count };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = id.StartsWith('/') ? id : $"/voddetail2/{id}.html";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var vodId = DetailIdRegex().Match(path) is { Success: true } dm ? dm.Groups[1].Value : id;
        var title = doc.QuerySelector(".details-info h1, h1")?.TextContent.Trim() ?? string.Empty;
        var pic = ResolveUrl(pageUri,
            doc.QuerySelector(".details-pic a.video-pic")?.GetAttribute("data-original")
            ?? doc.QuerySelector(".details-pic img")?.GetAttribute("data-original")
            ?? doc.QuerySelector(".details-pic img")?.GetAttribute("src")
            ?? doc.QuerySelector("img[src*='/poster/'], [data-original*='/poster/']")?.GetAttribute("data-original")
            ?? doc.QuerySelector("img[src*='/poster/']")?.GetAttribute("src")
            ?? (vodId.All(char.IsDigit) && vodId.Length > 0 ? $"/poster/{vodId}.jpg" : null));

        string remarks = string.Empty, typeName = string.Empty, actor = string.Empty, director = string.Empty, year = string.Empty, area = string.Empty;
        foreach (var li in doc.QuerySelectorAll(".details-info ul.info li"))
        {
            var text = CollapseSpaces(li.TextContent);
            if (text.StartsWith("狀態：", StringComparison.Ordinal) || text.StartsWith("状态：", StringComparison.Ordinal))
                remarks = text[3..].Trim();
            else if (text.StartsWith("類別：", StringComparison.Ordinal) || text.StartsWith("类别：", StringComparison.Ordinal))
                typeName = text[3..].Trim();
            else if (text.StartsWith("主演：", StringComparison.Ordinal))
                actor = text[3..].Trim();
            else if (text.StartsWith("導演：", StringComparison.Ordinal) || text.StartsWith("导演：", StringComparison.Ordinal))
                director = text[3..].Trim();
            else if (text.StartsWith("地區：", StringComparison.Ordinal) || text.StartsWith("地区：", StringComparison.Ordinal))
                area = text[3..].Trim();
            else if (text.StartsWith("年份：", StringComparison.Ordinal))
                year = text[3..].Trim();
        }
        var content = CollapseSpaces(
            doc.QuerySelector(".details-content-all")?.TextContent
            ?? doc.QuerySelector(".details-content-default")?.TextContent
            ?? string.Empty);

        var lineNames = new List<string>();
        var lineUrls = new List<string>();
        var tabs = doc.QuerySelectorAll(".details-play-title .nav-tabs li a")
            .Select(e => CollapseSpaces(e.TextContent))
            .Where(s => s.Length > 0)
            .ToList();
        var playlists = doc.QuerySelectorAll("ul.playlistlink-1, .details-play-list ul");
        for (var i = 0; i < playlists.Length; i++)
        {
            var eps = ExtractEpisodes(playlists[i]);
            if (eps.Count == 0) continue;
            lineNames.Add(i < tabs.Count ? tabs[i] : $"线路{lineNames.Count + 1}");
            lineUrls.Add(string.Join('#', eps));
        }

        if (lineUrls.Count == 0)
        {
            var fallbackEps = ExtractEpisodes(doc);
            if (fallbackEps.Count > 0)
            {
                lineNames.Add("在线播放");
                lineUrls.Add(string.Join('#', fallbackEps));
            }
        }

        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title,
            Picture = pic,
            TypeName = typeName,
            Remarks = remarks,
            Year = year,
            Area = area,
            Actor = actor,
            Director = director,
            Content = content,
            PlayFrom = string.Join("$$$", lineNames),
            PlayUrl = string.Join("$$$", lineUrls)
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var epPath = id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(id).PathAndQuery
            : id;
        var (epHtml, epUri) = await FetchTextAsync(epPath, Origin.AbsoluteUri, token).ConfigureAwait(false);

        var watchPaths = new List<string>();
        var iframeMatch = WatchIframeRegex().Match(epHtml);
        if (iframeMatch.Success) watchPaths.Add(iframeMatch.Groups[1].Value);
        foreach (Match m in WatchPathRegex().Matches(epHtml))
        {
            var wp = m.Groups[1].Value;
            if (!watchPaths.Contains(wp, StringComparer.OrdinalIgnoreCase))
                watchPaths.Add(wp);
        }
        if (watchPaths.Count == 0)
            throw new InvalidDataException("剧迷播放页未找到视频播放框架。");

        string? firstStream = null;
        foreach (var watchPath in watchPaths.Take(4))
        {
            var (watchHtml, _) = await FetchTextAsync(watchPath, epUri.AbsoluteUri, token).ConfigureAwait(false);
            var urlMatch = StreamUrlRegex().Match(watchHtml);
            if (!urlMatch.Success) continue;
            var candidate = urlMatch.Groups[1].Value.Trim();
            firstStream ??= candidate;
            if (watchPaths.Count == 1) break;
            try
            {
                var probe = await http.GetExchangeAsync(candidate, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = BrowserUa }, maxBytes: 16 * 1024, cancellationToken: token).ConfigureAwait(false);
                if ((int)probe.StatusCode is >= 200 and < 300)
                {
                    firstStream = candidate;
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (string.IsNullOrWhiteSpace(firstStream))
            throw new InvalidDataException("剧迷播放框架未返回有效视频流地址。");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa
        };
        return new SpiderPlayResult(firstStream, headers, 0, 0);
    }

    private async Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(string relativePath, CancellationToken token)
    {
        var (html, finalUri) = await FetchTextAsync(relativePath, Origin.AbsoluteUri, token).ConfigureAwait(false);
        var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
        return (doc, finalUri);
    }

    private async Task<(string Html, Uri FinalUri)> FetchTextAsync(string relativePath, string referer, CancellationToken token)
    {
        var target = new Uri(Origin, relativePath);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Referer"] = referer
        };
        var exchange = await http.GetExchangeAsync(target.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        if ((int)exchange.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"剧迷站点返回 HTTP {(int)exchange.StatusCode}");
        return (Encoding.UTF8.GetString(exchange.Payload.Content), exchange.Payload.FinalUri);
    }

    private static List<VodItem> ParseCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = doc.QuerySelectorAll("a.video-pic[href*='/voddetail2/']");
        if (nodes.Length == 0) nodes = doc.QuerySelectorAll("a[href*='/voddetail2/']");
        foreach (var a in nodes)
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
            var remark = CollapseSpaces(a.QuerySelector(".note, .text-bg-r")?.TextContent ?? string.Empty);
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

    private static List<string> ExtractEpisodes(IParentNode container)
    {
        var eps = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in container.QuerySelectorAll("a[href*='/eps/']"))
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var label = CollapseSpaces(a.TextContent);
            if (href.Length == 0 || label.Length == 0 || !seen.Add(href)) continue;
            eps.Add($"{label.Replace("$", "＄").Replace("#", "＃")}${href}");
        }
        return eps;
    }

    private static string ResolveUrl(Uri pageUri, string? raw) => NativeSiteToolkit.ResolveUrl(pageUri, raw);

    private static string CollapseSpaces(string value) => NativeSiteToolkit.CollapseSpaces(value);

    [GeneratedRegex(@"/voddetail2/(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailIdRegex();

    [GeneratedRegex(@"<iframe[^>]+src=[""'](/_watch/\d+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WatchIframeRegex();

    [GeneratedRegex(@"(/_watch/\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WatchPathRegex();

    [GeneratedRegex(@"var\s+url\s*=\s*['""](https?://[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreamUrlRegex();
}
