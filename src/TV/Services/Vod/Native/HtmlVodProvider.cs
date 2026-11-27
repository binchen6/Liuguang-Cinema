using System.Collections.Concurrent;
using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public sealed class HtmlVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private const string BrowserUserAgent = NativeSiteToolkit.BrowserUserAgent;
    private readonly ConcurrentDictionary<string, string> _discoveredSearchPaths = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Category(string Id, string Name);

    private sealed record Layout(Uri Base, Category[] Categories, string Cards, string SearchPath,
        string Title, string Poster, string Groups, string GroupName, string EpisodeLinks,
        Func<IDocument, Uri, Uri?>? ReadPlayerData = null, Func<IDocument, Uri, Uri?>? ReadDanmaku = null);

    private static readonly Layout Auete = new(new Uri("https://www.aeete.com/"),
        [new("Movie", "电影"), new("Tv", "电视剧"), new("Dm", "动漫"), new("Zy", "综艺"), new("qita", "其他")],
        ".threadlist a.pic, .threadlist a[title][href], a.carousel-link", "/so.php?searchword=",
        "h1.detail-title", ".detail-poster img", ".play-card", ".card-header b", "a[href*='/play-']", HtmlPageMedia.ReadBase64Player);

    private static readonly Layout Bttwoo = new(new Uri("https://www.bttwoo.com/"),
        [new("movie", "电影"), new("tv", "电视剧"), new("anime", "动漫")],
        "a[href^='/play/']:not(.episode-link)", "/search?q=",
        "h1", "", "", "", "a.episode-link[href]", ReadDanmaku: HtmlPageMedia.ReadEpisodeDanmaku);

    public static bool Supports(SourceSite site) => site.Type == 3 && FindLayout(site.Api) is not null;
    public bool CanHandle(SourceSite site) => Supports(site);

    private static Layout? FindLayout(string api) => api.ToLowerInvariant() switch
    {
        "csp_auete" or "csp_aueteguard" => Auete,
        "csp_bttwoo" or "csp_bttwooguard" => Bttwoo,
        _ => null
    };

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var layout = FindLayout(site.Api)!;
        var response = await ReadListAsync(site, layout, "/", 1, cancellationToken).ConfigureAwait(false);
        response.Categories = layout.Categories.Select(category => new VodCategory { Id = category.Id, Name = category.Name }).ToList();
        return response;
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        var layout = FindLayout(site.Api)!;
        if (layout == Bttwoo && page > 1) return Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });
        if (categoryId.Length == 0) return GetHomeAsync(site, cancellationToken);
        if (!layout.Categories.Any(category => category.Id == categoryId)) return Task.FromResult(new CmsResponse());
        var suffix = page > 1 ? page.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var path = layout == Auete ? $"/{categoryId}/index{suffix}.html" : $"/{categoryId}";
        return ReadListAsync(site, layout, path, page, cancellationToken);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var layout = FindLayout(site.Api)!;
        var origin = ResolveOrigin(site, layout);
        var originKey = SiteSessionStore.NormalizeOrigin(origin);
        var searchPath = layout == Auete && _discoveredSearchPaths.TryGetValue(originKey, out var cached)
            ? cached : layout.SearchPath;
        try
        {
            return await ReadListAsync(site, layout, searchPath + Uri.EscapeDataString(keyword), page, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (layout == Auete && ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Site may rotate search PHP path (/so.php, /aeete5so.php, etc.); probe homepage form + known fallbacks.
            var candidates = new List<string>();
            try
            {
                var (homeHtml, homeRoot, _, _) = await ReadPageWithExchangeAsync(site, layout, "/", cancellationToken).ConfigureAwait(false);
                var freshPath = DiscoverAueteSearchPath(homeHtml, homeRoot);
                if (!string.IsNullOrEmpty(freshPath)) candidates.Add(freshPath);
            }
            catch { }
            candidates.Add("/so.php?searchword=");
            candidates.Add("/aeete5so.php?searchword=");

            foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (string.Equals(candidate, searchPath, StringComparison.Ordinal)) continue;
                try
                {
                    var result = await ReadListAsync(site, layout, candidate + Uri.EscapeDataString(keyword), page, cancellationToken).ConfigureAwait(false);
                    _discoveredSearchPaths[originKey] = candidate;
                    return result;
                }
                catch (HttpRequestException nextEx) when (nextEx.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }
            }
            throw;
        }
    }

    private async Task<CmsResponse> ReadListAsync(SourceSite site, Layout layout, string path, int page, CancellationToken token)
    {
        var (html, root, _, setCookies) = await ReadPageWithExchangeAsync(site, layout, path, token).ConfigureAwait(false);
        if (html.QuerySelector("#f_login img[src*='vdimgck']") is { } captchaImg)
        {
            if (http.Sessions is not null)
                await http.Sessions.ClearAsync(root, token).ConfigureAwait(false);
            var challenge = BuildImageCaptchaChallenge(html, captchaImg, root, setCookies, Headers(site));
            throw new SiteVerificationRequiredException(site.Key, site.Name, root, SiteVerificationKind.ImageCaptcha,
                "站点搜索需要验证码。", challenge);
        }
        var list = new Dictionary<string, VodItem>(StringComparer.Ordinal);
        foreach (var card in html.QuerySelectorAll(layout.Cards))
        {
            if (!TryPageLink(card.GetAttribute("href"), root, out var pageUri)) continue;
            var image = card.QuerySelector("img") ?? card.ParentElement?.QuerySelector("img");
            var name = NonEmpty(card.GetAttribute("title"), image?.GetAttribute("alt"), card.QuerySelector("h2,h3")?.TextContent);
            if (name.Length == 0) continue;
            var picture = ImageLink(image, root);
            var key = pageUri.PathAndQuery;
            if (!list.TryGetValue(key, out var item))
            {
                item = new VodItem { Id = key, Name = name, Picture = picture, SiteKey = site.Key, SiteName = site.Name };
                if (layout == Auete)
                {
                    var row = card.Closest("li") ?? card;
                    item.Remarks = Text(row, ".hdtag");
                    item.SiteRating = ReadVisibleScore(row, ".rating");
                }
                list.Add(key, item);
            }
            else if (item.Picture.Length == 0) item.Picture = picture;
            if (layout == Bttwoo) ReadBttCardMetadata(card, item);
        }
        var isSearch = path.Contains("searchword=", StringComparison.OrdinalIgnoreCase) || path.StartsWith(layout.SearchPath, StringComparison.Ordinal);
        var pageCount = layout == Auete && !isSearch
            ? ReadAuetePageCount(html, root, page) : page;
        return new CmsResponse { List = list.Values.ToList(), Page = page, PageCount = pageCount };
    }

    private static string? DiscoverAueteSearchPath(IDocument html, Uri root)
    {
        var form = html.QuerySelector("form#search_form")
            ?? html.QuerySelector("form input[name='searchword']")?.Closest("form");
        var action = form?.GetAttribute("action")?.Trim();
        if (string.IsNullOrWhiteSpace(action) || !TryPageLink(action, root, out var uri)) return null;
        return uri.AbsolutePath + "?searchword=";
    }

    private static ImageCaptchaChallenge? BuildImageCaptchaChallenge(
        IDocument html, IElement captchaImg, Uri pageUri, IReadOnlyList<string> setCookies, IReadOnlyDictionary<string, string> requestHeaders)
    {
        var form = html.QuerySelector("form#f_login") ?? captchaImg.Closest("form");
        var actionAttr = form?.GetAttribute("action") ?? string.Empty;
        var imgSrc = captchaImg.GetAttribute("src") ?? "include/vdimgck.php";
        if (!Uri.TryCreate(pageUri, string.IsNullOrWhiteSpace(actionAttr) ? pageUri.PathAndQuery : actionAttr, out var actionUri) ||
            !Uri.TryCreate(pageUri, imgSrc, out var imageUri))
            return null;
        var codeInput = form?.QuerySelector("input[name='validate'], input#vdcode, input[type='text']");
        var codeField = codeInput?.GetAttribute("name") is { Length: > 0 } name ? name : "validate";
        var hidden = new List<KeyValuePair<string, string>>();
        if (form is not null)
        {
            foreach (var input in form.QuerySelectorAll("input[type='hidden'][name]"))
            {
                var fieldName = input.GetAttribute("name");
                if (!string.IsNullOrWhiteSpace(fieldName))
                    hidden.Add(new KeyValuePair<string, string>(fieldName, input.GetAttribute("value") ?? string.Empty));
            }
        }
        var seedCookies = SiteSessionStore.MergeCookies(string.Empty, setCookies);
        var ua = requestHeaders.GetValueOrDefault("User-Agent") ?? BrowserUserAgent;
        return new ImageCaptchaChallenge(actionUri, imageUri, codeField, hidden, seedCookies, pageUri, ua);
    }

    private static int ReadAuetePageCount(IDocument html, Uri root, int page)
    {
        var directory = new Uri(root, ".").AbsolutePath;
        foreach (var link in html.QuerySelectorAll(".pagination a"))
        {
            if (!TryPageLink(link.GetAttribute("href"), root, out var uri) || new Uri(uri, ".").AbsolutePath != directory)
                continue;
            var file = uri.AbsolutePath[directory.Length..];
            if (file.StartsWith("index", StringComparison.Ordinal) && file.EndsWith(".html", StringComparison.Ordinal) &&
                file.Length > 10 && int.TryParse(file.AsSpan(5, file.Length - 10), out var number))
                page = Math.Max(page, number);
        }
        return page;
    }

    private static void ReadBttCardMetadata(IElement card, VodItem item)
    {
        var tile = card.Closest(".movie-card");
        item.Year = NonEmpty(item.Year, tile?.QuerySelectorAll("span.text-gray-400")
            .Select(span => span.TextContent.Trim()).FirstOrDefault(IsYear));
        item.Remarks = NonEmpty(item.Remarks, Text(card, "span.bg-gradient-to-t"),
            tile?.QuerySelectorAll("span").Select(span => span.TextContent.Trim()).FirstOrDefault(IsEpisodeRemark));
        item.SiteRating = NonEmpty(item.SiteRating, card.QuerySelector("[data-rating]")?.GetAttribute("data-rating"), ReadBttScore(card));
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var layout = FindLayout(site.Api)!;
        var (html, root, _) = await ReadPageAsync(site, layout, id, cancellationToken).ConfigureAwait(false);
        var groups = layout == Auete
            ? html.QuerySelectorAll(layout.Groups).Select(group => (Name: NonEmpty(group.QuerySelector(layout.GroupName)?.TextContent, "在线"),
                Links: group.QuerySelectorAll(layout.EpisodeLinks).AsEnumerable()))
            : html.QuerySelectorAll(layout.EpisodeLinks).GroupBy(link => link.GetAttribute("data-line"))
                .Select((group, index) => (Name: $"线路 {index + 1}", Links: group.AsEnumerable()));
        var lines = groups.Select(group => (group.Name, Episodes: group.Links.Select(link => ReadEpisode(link, root)).OfType<Episode>()
            .DistinctBy(episode => episode.Url).ToArray())).Where(group => group.Episodes.Length > 0).ToArray();
        if (lines.Length == 0 && layout == Bttwoo && root.AbsolutePath.StartsWith("/play/", StringComparison.Ordinal))
            lines = [("在线", [new Episode { Name = "播放", Url = root.PathAndQuery }])];

        var title = NonEmpty(html.QuerySelector(layout.Title)?.TextContent, Meta(html, "title")).Trim('《', '》');
        if (title.Length == 0) throw new InvalidDataException("站点未返回有效详情。");
        var item = new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name, Name = title, Content = Meta(html, "description"),
            Picture = layout == Auete ? ImageLink(html.QuerySelector(layout.Poster), root) : Absolute(root, Meta(html, "image")),
            PlayFrom = string.Join("$$$", lines.Select(group => SafeLabel(group.Name))),
            PlayUrl = string.Join("$$$", lines.Select(group => string.Join('#', group.Episodes.Select(episode => $"{episode.Name}${episode.Url}"))))
        };
        if (layout == Auete)
        {
            item.Year = Text(html, ".badge-time");
            item.Area = NonEmpty(Meta(html, "video:area"), Text(html, ".badge-area"));
            item.TypeName = NonEmpty(Meta(html, "video:class"), Text(html, ".badge-type"));
            item.Director = Meta(html, "video:director");
            item.Actor = Meta(html, "video:actor");
            item.Remarks = Text(html, ".detail-status .detail-status-item:first-child .value");
            item.SiteRating = ReadVisibleScore(html, ".detail-rating");
            item.Content = NonEmpty(Text(html, ".detail-des"), item.Content);
        }
        else if (layout == Bttwoo)
        {
            var fields = html.QuerySelectorAll(".col-span-1.text-gray-500");
            string Field(string label) => fields.FirstOrDefault(field => field.TextContent.Trim() == label)
                ?.NextElementSibling?.TextContent.Trim() ?? string.Empty;
            var release = Field("上映");
            if (release.Length >= 4 && IsYear(release[..4])) item.Year = release[..4];
            item.Area = Field("地区");
            item.TypeName = Field("类型");
            item.Actor = Field("主演");
            item.Director = Field("导演");
            item.Remarks = html.QuerySelectorAll("[x-data^='episodeManager('] p").Select(text => text.TextContent.Trim())
                .FirstOrDefault(IsEpisodeRemark) ?? string.Empty;
            item.SiteRating = ReadBttScore(html);
        }
        return item;
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var layout = FindLayout(site.Api)!;
        var (html, page, requested) = await ReadPageAsync(site, layout, id, token).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(MediaRequestHeaders.ForTarget(Headers(site), requested, page), StringComparer.OrdinalIgnoreCase);
        var media = HtmlPageMedia.ReadVideoElement(html, page) ?? layout.ReadPlayerData?.Invoke(html, page);
        var danmaku = layout.ReadDanmaku?.Invoke(html, page);
        if (media is not null)
        {
            var scoped = new Dictionary<string, string>(MediaRequestHeaders.ForTarget(headers, page, media), StringComparer.OrdinalIgnoreCase);
            scoped.TryAdd("Referer", MediaRequestHeaders.SameOrigin(page, media) ? page.AbsoluteUri : page.GetLeftPart(UriPartial.Authority) + "/");
            return new SpiderPlayResult(media.AbsoluteUri, scoped, 0, 0, Danmaku: danmaku, ReplaceHeaders: true)
            {
                UseHttpProxy = layout == Auete && media.Scheme == Uri.UriSchemeHttps
            };
        }
        headers.TryAdd("Referer", page.AbsoluteUri);
        return new SpiderPlayResult(page.AbsoluteUri, headers, 1, 0, Danmaku: danmaku, ReplaceHeaders: true);
    }

    private async Task<(IDocument Html, Uri Uri, Uri Requested)> ReadPageAsync(SourceSite site, Layout layout, string path, CancellationToken token)
    {
        var (html, uri, requested, _) = await ReadPageWithExchangeAsync(site, layout, path, token).ConfigureAwait(false);
        return (html, uri, requested);
    }

    private async Task<(IDocument Html, Uri Uri, Uri Requested, IReadOnlyList<string> SetCookies)> ReadPageWithExchangeAsync(
        SourceSite site, Layout layout, string path, CancellationToken token)
    {
        var origin = ResolveOrigin(site, layout);
        if (!TryPageLink(path, origin, out var uri)) throw new InvalidDataException("站点页面地址无效。");
        var exchange = await http.GetExchangeAsync(uri.AbsoluteUri, Headers(site), maxBytes: 4 * 1024 * 1024, cancellationToken: token,
            scopeRedirectHeaders: true).ConfigureAwait(false);
        if ((int)exchange.StatusCode >= 400)
        {
            if (exchange.StatusCode == HttpStatusCode.Forbidden && exchange.HasCloudflareChallenge)
                throw new SiteVerificationRequiredException(site.Key, site.Name, uri, SiteVerificationKind.BrowserChallenge,
                    $"{site.Name} 需要完成浏览器安全验证。");
            throw new HttpRequestException($"站点请求失败（HTTP {(int)exchange.StatusCode}）。", null, exchange.StatusCode);
        }
        var html = await new HtmlParser().ParseDocumentAsync(SourceHttpClient.DecodeText(exchange.Payload.Content), token).ConfigureAwait(false);
        return (html, exchange.Payload.FinalUri, uri, exchange.SetCookies);
    }

    private static Uri ResolveOrigin(SourceSite site, Layout layout) =>
        Uri.TryCreate(site.Ext.ValueKind == System.Text.Json.JsonValueKind.String ? site.Ext.GetString() : null,
            UriKind.Absolute, out var custom) && custom.Scheme is "http" or "https" ? custom : layout.Base;

    private static Dictionary<string, string> Headers(SourceSite site)
    {
        var headers = new Dictionary<string, string>(site.GetHeaders(), StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("User-Agent", BrowserUserAgent);
        headers.TryAdd("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return headers;
    }

    private static bool TryPageLink(string? path, Uri origin, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(origin, path, out var result) ||
            result.Scheme is not ("http" or "https") || result.Host != origin.Host) return false;
        uri = result;
        return true;
    }

    private static Episode? ReadEpisode(IElement link, Uri root) => TryPageLink(link.GetAttribute("href"), root, out var episode)
        ? new Episode { Name = SafeLabel(NonEmpty(link.GetAttribute("title"), link.TextContent, "播放")), Url = episode.PathAndQuery }
        : null;

    private static string SafeLabel(string value) => value.Trim().Replace('$', '＄').Replace('#', '＃');
    private static string Text(IParentNode root, string selector) => root.QuerySelector(selector)?.TextContent.Trim() ?? string.Empty;
    private static string ReadVisibleScore(IParentNode root, string selector) => Text(root, selector).TrimEnd('分').TrimEnd();
    private static bool IsYear(string value) => value.Length == 4 && value.All(char.IsAsciiDigit);
    private static bool IsEpisodeRemark(string value) => value.Length <= 24 && value.EndsWith('集') &&
        (value.StartsWith("更新至", StringComparison.Ordinal) || value.StartsWith('全'));
    private static string ReadBttScore(IParentNode root) => root.QuerySelectorAll("use[href$='#ri-star-fill']")
        .Select(icon => icon.Closest("span")?.TextContent.Trim())
        .FirstOrDefault(text => decimal.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var score) && score is > 0 and <= 10)
        ?? string.Empty;
    private static string Meta(IDocument html, string name) =>
        NonEmpty(html.QuerySelector($"meta[property='og:{name}']")?.GetAttribute("content"),
                 html.QuerySelector($"meta[name='{name}']")?.GetAttribute("content"));
    private static string Absolute(Uri root, string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(root, value, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri : string.Empty;
    private static string ImageLink(IElement? image, Uri root) => Absolute(root,
        NonEmpty(image?.GetAttribute("data-src"), image?.GetAttribute("data-original"), image?.GetAttribute("src")));
    private static string NonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
