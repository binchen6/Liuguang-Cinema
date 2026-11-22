using System.Collections.Concurrent;
using System.Globalization;
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
/// Native provider for 🧲新6V┃磁力 (csp_SixV / csp_SixVGuard / csp_Xb6v).
/// Fixes broken /e/search/index.php (now dynamically discovers /e/search/so.php) and safe category pagination.
/// </summary>
public sealed partial class Xb6vVodProvider(SourceHttpClient http) : ICategoryFilterVodProvider, INativePlaybackProvider
{
    private static readonly Uri DefaultOrigin = new("https://www.xb6v.com/");
    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private readonly ConcurrentDictionary<string, string> _searchActionByOrigin = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _searchIdByKey = new(StringComparer.Ordinal);

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_SixVGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_SixV", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Xb6v", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var origin = Origin(site);
        var (doc, pageUri) = await ReadPageAsync(origin, origin, cancellationToken).ConfigureAwait(false);
        CacheSearchAction(origin, doc);

        var categories = new List<VodCategory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filters = new Dictionary<string, object>();

        var menuLinks = doc.QuerySelectorAll("#menus > li > a").ToArray();
        for (var i = 0; i < menuLinks.Length; i++)
        {
            var link = menuLinks[i];
            var href = link.GetAttribute("href")?.Trim() ?? string.Empty;
            var name = link.TextContent.Trim();
            if (href.Length == 0 || name.Length == 0 || href == "/" || href.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!href.StartsWith('/'))
            {
                if (Uri.TryCreate(pageUri, href, out var abs) && abs.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase))
                    href = abs.AbsolutePath;
                else continue;
            }
            if (!href.EndsWith('/')) href += "/";
            if (!seen.Add(href)) continue;

            categories.Add(new VodCategory { Id = href, Name = name });
            if (link.ParentElement?.QuerySelectorAll("ul a[href]") is { Length: > 0 } subLinks)
            {
                var values = new List<object> { new { n = "不限", v = "" } };
                foreach (var sub in subLinks)
                {
                    var subHref = sub.GetAttribute("href")?.Trim() ?? string.Empty;
                    var subName = sub.TextContent.Trim();
                    if (subHref.StartsWith(href, StringComparison.OrdinalIgnoreCase) && subName.Length > 0)
                        values.Add(new { n = subName, v = subHref[href.Length..] });
                }
                if (values.Count > 1)
                    filters[href] = new[] { new { key = "cateId", name = "类型", value = values } };
            }
        }

        var response = new CmsResponse
        {
            HomeCategoryName = "最新更新",
            Categories = categories,
            List = ParseVodList(doc, pageUri, site),
            Page = 1,
            PageCount = 1,
            Limit = 18
        };
        if (filters.Count > 0)
            response.Filters = JsonSerializer.SerializeToElement(filters);
        return response;
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        var origin = Origin(site);
        var sub = filters is not null && filters.TryGetValue("cateId", out var cateId) ? cateId.Trim() : string.Empty;
        var basePath = categoryId.Trim();
        if (!basePath.StartsWith('/')) basePath = "/" + basePath;
        if (!basePath.EndsWith('/')) basePath += "/";
        var path = basePath + sub;
        if (page > 1) path += $"index_{page}.html";

        var target = new Uri(origin, path);
        IDocument doc;
        Uri finalUri;
        try
        {
            (doc, finalUri) = await ReadPageAsync(target, origin, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (page > 1 && ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new CmsResponse { Page = page, PageCount = page - 1, Limit = 18 };
        }

        var items = ParseVodList(doc, finalUri, site);
        var pageCount = ParsePageCount(doc, page);
        return new CmsResponse
        {
            List = items,
            Page = page,
            PageCount = pageCount,
            Limit = 18,
            Total = page == pageCount ? (page - 1) * 18 + items.Count : pageCount * 18
        };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var origin = Origin(site);
        var cacheKey = $"{origin.AbsoluteUri}\n{keyword}";

        if (page > 1 && _searchIdByKey.TryGetValue(cacheKey, out var cachedSearchId))
        {
            var pagedUri = new Uri(origin, $"/e/search/result/index.php?page={page - 1}&searchid={Uri.EscapeDataString(cachedSearchId)}");
            var (pagedDoc, pagedFinal) = await ReadPageAsync(pagedUri, origin, cancellationToken).ConfigureAwait(false);
            var pagedItems = ParseVodList(pagedDoc, pagedFinal, site);
            return new CmsResponse { List = pagedItems, Page = page, PageCount = pagedItems.Count > 0 ? page + 1 : page };
        }

        var actionPath = _searchActionByOrigin.GetValueOrDefault(origin.AbsoluteUri, "/e/search/so.php");
        var form = new Dictionary<string, string>
        {
            ["show"] = "title",
            ["tempid"] = "1",
            ["tbname"] = "article",
            ["mid"] = "1",
            ["dopost"] = "search",
            ["submit"] = "",
            ["keyboard"] = keyword
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Origin"] = origin.GetLeftPart(UriPartial.Authority),
            ["Referer"] = origin.AbsoluteUri
        };

        var exchange = await http.PostFormAsync(new Uri(origin, actionPath).AbsoluteUri, form, headers,
            maxBytes: 2 * 1024 * 1024, cancellationToken: cancellationToken, followRedirects: true).ConfigureAwait(false);
        if (exchange.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var (homeDoc, _) = await ReadPageAsync(origin, origin, cancellationToken).ConfigureAwait(false);
            actionPath = CacheSearchAction(origin, homeDoc);
            exchange = await http.PostFormAsync(new Uri(origin, actionPath).AbsoluteUri, form, headers,
                maxBytes: 2 * 1024 * 1024, cancellationToken: cancellationToken, followRedirects: true).ConfigureAwait(false);
        }

        var html = Encoding.UTF8.GetString(exchange.Payload.Content);
        var doc = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var items = ParseVodList(doc, exchange.Payload.FinalUri, site);

        var searchIdMatch = SearchIdRegex().Match(exchange.Payload.FinalUri.Query);
        if (searchIdMatch.Success)
            _searchIdByKey[cacheKey] = searchIdMatch.Groups[1].Value;

        var hasNext = doc.QuerySelectorAll("a[href*='searchid=']").Any(a => a.TextContent.Contains("下一页", StringComparison.Ordinal));
        return new CmsResponse
        {
            List = items,
            Page = 1,
            PageCount = hasNext ? 2 : 1,
            Total = items.Count
        };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var origin = Origin(site);
        if (!Uri.TryCreate(origin, id, out var target))
            throw new InvalidDataException("新6V 详情地址无效。");

        var (doc, pageUri) = await ReadPageAsync(target, origin, cancellationToken).ConfigureAwait(false);
        var contentEl = doc.QuerySelector("#post_content");
        var episodes = new List<string>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (contentEl is not null)
        {
            var anchors = contentEl.QuerySelectorAll("table a[href]").ToList();
            if (anchors.Count == 0) anchors = contentEl.QuerySelectorAll("a[href]").ToList();
            foreach (var a in anchors)
            {
                var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
                if (!href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
                    !href.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase) &&
                    !href.StartsWith("thunder:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seenLinks.Add(href)) continue;
                var label = a.TextContent.Trim();
                if (label.Length == 0) label = $"磁力 {episodes.Count + 1}";
                label = label.Replace("$", "＄", StringComparison.Ordinal).Replace("#", "＃", StringComparison.Ordinal);
                episodes.Add($"{label}${href}");
            }
        }

        var partHtml = doc.QuerySelector(".context")?.InnerHtml ?? contentEl?.InnerHtml ?? string.Empty;
        var title = doc.QuerySelector(".article_container > h1")?.TextContent.Trim()
            ?? doc.QuerySelector(".article_container h1")?.TextContent.Trim()
            ?? doc.QuerySelector("h1")?.TextContent.Trim()
            ?? string.Empty;
        var pic = ResolveUrl(pageUri, contentEl?.QuerySelector("img")?.GetAttribute("src"));

        var typeName = MatchGroup(TypeRegex(), partHtml);
        if (typeName.Length == 0) typeName = doc.QuerySelector("[rel='category tag']")?.TextContent.Trim() ?? string.Empty;
        var year = MatchGroup(YearRegex(), partHtml);
        if (year.Length == 0) year = MatchGroup(YearFallbackRegex(), partHtml);
        var area = MatchGroup(AreaRegex(), partHtml);
        if (area.Length == 0) area = MatchGroup(AreaFallbackRegex(), partHtml);
        var director = CleanMeta(MatchGroup(DirectorRegex(), partHtml));
        var actor = CleanMeta(MatchGroup(ActorRegex(), partHtml));
        var desc = CleanDescription(MatchGroup(DescRegex(), partHtml));

        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title,
            Picture = pic,
            TypeName = CleanMeta(typeName),
            Year = CleanMeta(year),
            Area = CleanMeta(area),
            Director = director,
            Actor = actor,
            Remarks = episodes.Count > 0 ? $"磁力 {episodes.Count} 条" : string.Empty,
            Content = desc,
            PlayFrom = episodes.Count > 0 ? "磁力线路1" : string.Empty,
            PlayUrl = episodes.Count > 0 ? string.Join('#', episodes) : string.Empty
        };
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new SpiderPlayResult(id, new Dictionary<string, string>(), 0, 0));
    }

    private async Task<(IDocument Document, Uri FinalUri)> ReadPageAsync(Uri target, Uri origin, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Referer"] = origin.AbsoluteUri
        };
        var payload = await http.GetBytesAsync(target.AbsoluteUri, headers, maxBytes: 2 * 1024 * 1024,
            cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
        var doc = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(payload.Content), token).ConfigureAwait(false);
        return (doc, payload.FinalUri);
    }

    private string CacheSearchAction(Uri origin, IDocument doc)
    {
        var action = doc.QuerySelector("#searchform, form[action*='search']")?.GetAttribute("action")?.Trim();
        if (string.IsNullOrWhiteSpace(action)) action = "/e/search/so.php";
        _searchActionByOrigin[origin.AbsoluteUri] = action;
        return action;
    }

    private static List<VodItem> ParseVodList(IDocument doc, Uri pageUri, SourceSite site)
    {
        var list = new List<VodItem>();
        foreach (var item in doc.QuerySelectorAll("#post_container .post_hover"))
        {
            var anchor = item.QuerySelector("a.zoom") ?? item.QuerySelector("a[href]");
            if (anchor is null) continue;
            var href = anchor.GetAttribute("href")?.Trim() ?? string.Empty;
            if (href.Length == 0) continue;
            var rawTitle = anchor.GetAttribute("title") ?? anchor.TextContent;
            var name = HtmlTagRegex().Replace(rawTitle ?? string.Empty, string.Empty).Trim();
            if (name.Length == 0) continue;
            var pic = ResolveUrl(pageUri, anchor.QuerySelector("img")?.GetAttribute("src") ?? item.QuerySelector("img")?.GetAttribute("src"));
            var remark = item.QuerySelector("[rel='category tag']")?.TextContent.Trim() ?? string.Empty;
            list.Add(new VodItem
            {
                Id = href,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Picture = pic,
                Remarks = remark
            });
        }
        return list;
    }

    private static int ParsePageCount(IDocument doc, int currentPage)
    {
        var max = currentPage;
        foreach (var a in doc.QuerySelectorAll("a[href*='index_']"))
        {
            var href = a.GetAttribute("href") ?? string.Empty;
            var match = IndexPageRegex().Match(href);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
                max = Math.Max(max, num);
        }
        return Math.Max(1, max);
    }

    private static Uri Origin(SourceSite site) =>
        site.Ext.ValueKind == JsonValueKind.String && Uri.TryCreate(site.Ext.GetString(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/")
            : DefaultOrigin;

    private static string ResolveUrl(Uri pageUri, string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(pageUri, raw.Trim(), out var resolved)
            ? resolved.AbsoluteUri
            : string.Empty;

    private static string MatchGroup(Regex regex, string input)
    {
        var m = regex.Match(input);
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    private static string CleanMeta(string value) =>
        HtmlTagRegex().Replace(value, " ").Replace("&nbsp;", " ").Replace("\u3000", " ").Trim();

    private static string CleanDescription(string value) =>
        HtmlTagRegex().Replace(value, string.Empty).Replace("&nbsp;", " ").Replace("\r", "").Replace("\n", " ").Trim();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();
    [GeneratedRegex(@"index_(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexPageRegex();
    [GeneratedRegex(@"(?:^\?|&)searchid=([^&]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchIdRegex();
    [GeneratedRegex(@"◎类\s*别\s*(.*?)<br", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeRegex();
    [GeneratedRegex(@"◎年\s*代\s*(.*?)<br", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();
    [GeneratedRegex(@"首播[:：]\s*(.*?)<br", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearFallbackRegex();
    [GeneratedRegex(@"◎产\s*地\s*(.*?)<br", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AreaRegex();
    [GeneratedRegex(@"地区[:：]\s*(.*?)<br", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AreaFallbackRegex();
    [GeneratedRegex(@"◎导\s*演\s*(.*?)<br", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectorRegex();
    [GeneratedRegex(@"◎(?:演\s*员|主\s*演)\s*(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ActorRegex();
    [GeneratedRegex(@"◎简\s*介(.*?)(?:<hr|</p>)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DescRegex();
}
