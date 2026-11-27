using System.Globalization;
using System.Text.Json;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public sealed class ChangZhangVodProvider(SourceHttpClient http,
    Func<Uri, string, CancellationToken, Task<string?>>? readRenderedPage = null) : INativePlaybackProvider
{
    // The publisher lists this address at https://www.czzy.site/.
    private static readonly Uri Origin = new("https://www.4kcz.com/");
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";
    private const string SearchInput = "form.w-search-form input[name=q], form.w-search-form input[name=s]";
    private const string PlaybackFrame = ".videoplay iframe.viframe[src]";
    private const string PlaybackReady = PlaybackFrame + ", video[src], video source[src]";
    private long _browserModeUntil;

    internal static bool IsChangZhangApi(string api) =>
        api.Equals("csp_NewCzGuard", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_NewCz", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_CZ", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_CZGuard", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_Czzy", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_CzzyGuard", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_Czsapp", StringComparison.OrdinalIgnoreCase);

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        IsChangZhangApi(site.Api) &&
        (site.Ext.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object ||
         site.Ext.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(site.Ext.GetString()) ||
             ResolvePage(site.Ext.GetString()) is not null));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (document, effective) = await ReadPageAsync(site, Origin, ".dytit a[href]", cancellationToken).ConfigureAwait(false);
        var categories = new List<VodCategory>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var navigation = Header(document).QuerySelectorAll(".navlist > li > a[href]")
            .Concat(document.QuerySelectorAll(".submenu_mi > li > a[href]"));
        foreach (var anchor in navigation)
        {
            var link = ResolvePage(anchor.GetAttribute("href"));
            var name = Text(anchor);
            var path = link?.AbsolutePath.TrimEnd('/');
            if (link is not null && path is not (null or "" or "/gonggao" or "/wangzhanliuyan") &&
                name.Length > 0 && seen.Add(path))
                categories.Add(new VodCategory { Id = link.PathAndQuery, Name = name });
        }
        var result = Catalog(site, document, effective, 1);
        if (result.List.Count == 0 || categories.Count == 0) throw new InvalidDataException("厂长原站未返回可识别的首页目录。");
        result.Categories = categories;
        return result;
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        if (categoryId.Length == 0) return await GetHomeAsync(site, cancellationToken).ConfigureAwait(false);
        var category = RequirePage(categoryId);
        var address = page <= 1 ? category : new UriBuilder(category)
        {
            Path = category.AbsolutePath.TrimEnd('/') + FormattableString.Invariant($"/page/{page}/")
        }.Uri;
        var (document, effective) = await ReadPageAsync(site, address, ".bt_img", cancellationToken).ConfigureAwait(false);
        return Catalog(site, document, effective, page);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var path = page <= 1 ? "/nimasile" : FormattableString.Invariant($"/nimasile/page/{page}/");
        var address = new Uri(Origin, path + "?q=" + Uri.EscapeDataString(keyword));
        var (document, effective) = await ReadPageAsync(site, address, SearchInput, cancellationToken).ConfigureAwait(false);
        if (Header(document).QuerySelector(SearchInput) is null)
            throw new InvalidDataException("厂长原站未返回站内搜索页。");
        return Catalog(site, document, effective, page);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var (document, page) = await ReadPageAsync(site, RequirePage(id), ".dyxingq h1", cancellationToken).ConfigureAwait(false);
        var heading = document.QuerySelector(".dyxingq");
        var title = Text(heading?.QuerySelector("h1"));
        if (title.Length == 0) throw new InvalidDataException("厂长原站未返回该片详情。");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in heading!.QuerySelectorAll(".moviedteail_list > li"))
        {
            var text = Text(row);
            var colon = text.IndexOfAny(['：', ':']);
            if (colon < 1) continue;
            var values = row.QuerySelectorAll("a, span").Select(Text).Where(value => value.Length > 0).ToArray();
            fields.TryAdd(text[..colon].Trim(), values.Length > 0 ? string.Join(" / ", values) : text[(colon + 1)..].Trim());
        }
        var lines = new List<string>();
        var episodes = new List<string>();
        foreach (var group in document.QuerySelectorAll(".paly_list_btn"))
        {
            var entries = new List<string>();
            foreach (var link in group.QuerySelectorAll("a[href]"))
            {
                var episode = ResolvePage(link.GetAttribute("href"));
                var name = Text(link);
                if (episode is not null && episode.AbsolutePath.StartsWith("/v_play/", StringComparison.Ordinal) && name.Length > 0)
                    entries.Add(EscapeLabel(name) + "$" + episode.AbsoluteUri);
            }
            if (entries.Count == 0) continue;
            var lineName = Text(group.PreviousElementSibling?.QuerySelector("h2, h3, h4"));
            lines.Add(lineName.Length > 0 ? EscapeLabel(lineName) : "在线播放");
            episodes.Add(string.Join('#', entries));
        }
        return new VodItem
        {
            Id = page.AbsoluteUri, Name = title, Picture = ImageUrl(heading.QuerySelector(".dyimg img"), page),
            TypeName = Field(fields, "类型"), Area = Field(fields, "地区"), Year = Field(fields, "年份"),
            Actor = Field(fields, "主演"), Director = Field(fields, "导演"), SiteRating = Text(heading.QuerySelector(".rating")),
            Content = Text(document.QuerySelector(".yp_context")), PlayFrom = string.Join("$$$", lines),
            PlayUrl = string.Join("$$$", episodes), SiteKey = site.Key, SiteName = site.Name
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var (document, page) = await ReadPageAsync(site, RequirePage(id), PlaybackReady, token).ConfigureAwait(false);
        var frame = HttpLink(page, document.QuerySelector(PlaybackFrame)?.GetAttribute("src"));
        var video = HtmlPageMedia.ReadVideoElement(document, page);
        Uri? frameOrigin = null;
        if (video is null && frame is not null && frame.IdnHost == "plaa.py1080p.com" && (frame.IsDefaultPort || frame.Port == 8181) &&
            frame.AbsolutePath == "/player/py.php")
        {
            var published = HttpUtility.ParseQueryString(frame.Query)["url"];
            if (Uri.TryCreate(published, UriKind.Absolute, out var media) && IsPublishedMedia(media) &&
                PlayUrlResolver.IsDirectMediaUrl(media.AbsoluteUri))
            {
                video = media;
                frameOrigin = frame;
            }
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = (frameOrigin ?? page).GetLeftPart(UriPartial.Authority) + "/"
        };
        if (frameOrigin is not null) headers["Origin"] = frameOrigin.GetLeftPart(UriPartial.Authority);
        return video is not null
            ? new SpiderPlayResult(video.AbsoluteUri, headers, 0, 0, ReplaceHeaders: true)
            {
                UseHttpProxy = true,
                UnwrapPngHlsSegments = frameOrigin is not null && video.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            }
            : BrowserPlayback(page);
    }

    private static SpiderPlayResult BrowserPlayback(Uri page) =>
        new(page.AbsoluteUri, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Referer"] = Origin.AbsoluteUri },
            1, 0, ReplaceHeaders: true)
        {
            UseHttpProxy = true,
            UnwrapPngHlsSegments = true,
            BrowserMediaFilter = IsPublishedMedia,
            BrowserFailureMessage = "厂长站点未返回可用视频。请尝试其他线路，或在浏览器检查原站播放状态。"
        };

    private static bool IsPublishedMedia(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
        (uri.IsDefaultPort && (uri.IdnHost == Origin.IdnHost || uri.IdnHost == "v4-kling.kechuangai.com") ||
         (uri.IsDefaultPort || uri.Port == 907) && uri.IdnHost.EndsWith(".py1080p.com", StringComparison.OrdinalIgnoreCase));

    private bool UseBrowserMode => readRenderedPage is not null && Environment.TickCount64 < Interlocked.Read(ref _browserModeUntil);

    private async Task<(IDocument Document, Uri Effective)> ReadPageAsync(SourceSite site, Uri address, string readySelector, CancellationToken token)
    {
        if (UseBrowserMode)
            return (await ReadBrowserPageAsync(address, readySelector, token).ConfigureAwait(false), address);
        var headers = new Dictionary<string, string>(site.GetHeaders(), StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("User-Agent", UserAgent);
        headers.TryAdd("Referer", Origin.AbsoluteUri);
        HttpPayload payload;
        try
        {
            payload = await http.GetBytesAsync(address.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024,
                cancellationToken: token, scopeRedirectHeaders: true,
                requestTimeout: readRenderedPage is null ? null : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (readRenderedPage is not null && !token.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException)
        {
            return (await ReadBrowserPageAsync(address, readySelector, token).ConfigureAwait(false), address);
        }
        if (!MediaRequestHeaders.SameOrigin(Origin, payload.FinalUri)) throw new InvalidDataException("厂长入口已跳转到其他域名。");
        var parser = new HtmlParser();
        IDocument document = await parser.ParseDocumentAsync(SourceHttpClient.DecodeText(payload.Content), token).ConfigureAwait(false);
        if (document.QuerySelector("#slg-title") is not null)
        {
            Interlocked.Exchange(ref _browserModeUntil, Environment.TickCount64 + 15 * 60 * 1000);
            if (document.QuerySelector(readySelector) is null)
                document = await ReadBrowserPageAsync(payload.FinalUri, readySelector, token).ConfigureAwait(false);
        }
        return (document, payload.FinalUri);
    }

    private async Task<IDocument> ReadBrowserPageAsync(Uri address, string selector, CancellationToken token)
    {
        var rendered = readRenderedPage is null ? null : await readRenderedPage(address, selector, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rendered))
            throw new InvalidDataException("厂长站点需要浏览器渲染，目前未能读取页面。请稍后重试。");
        return await new HtmlParser().ParseDocumentAsync(rendered, token).ConfigureAwait(false);
    }

    private static CmsResponse Catalog(SourceSite site, IDocument document, Uri page, int pageNumber)
    {
        var cards = new List<VodItem>();
        foreach (var item in document.QuerySelectorAll(".bt_img.mi_ne_kd > ul > li"))
        {
            var title = item.QuerySelector(".dytit > a[href]");
            var link = ResolvePage(title?.GetAttribute("href"));
            var name = Text(title);
            if (link is null || !link.AbsolutePath.StartsWith("/movie/", StringComparison.Ordinal) || name.Length == 0) continue;
            var actor = Text(item.QuerySelector(".inzhuy"));
            cards.Add(new VodItem
            {
                Id = link.AbsoluteUri, Name = name, Picture = ImageUrl(item.QuerySelector("img"), page),
                Remarks = string.Join(" ", item.QuerySelectorAll(".jidi span, .hdinfo span").Select(Text).Where(text => text.Length > 0)),
                TypeName = Text(item.QuerySelector(".hdinfo .furk")),
                Actor = actor.StartsWith("主演：", StringComparison.Ordinal) ? CleanValue(actor["主演：".Length..]) : actor,
                SiteRating = Text(item.QuerySelector(".rating")), SiteKey = site.Key, SiteName = site.Name
            });
        }
        var lastPage = pageNumber;
        foreach (var link in document.QuerySelectorAll(".pagenavi_txt a[href]"))
        {
            var address = ResolvePage(link.GetAttribute("href"));
            var path = address?.AbsolutePath.TrimEnd('/');
            if (path is null) continue;
            var marker = path.LastIndexOf("/page/", StringComparison.Ordinal);
            if (marker >= 0 && int.TryParse(path.AsSpan(marker + "/page/".Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number))
                lastPage = Math.Max(lastPage, number);
        }
        return new CmsResponse { List = cards, Page = pageNumber, PageCount = lastPage, Limit = cards.Count };
    }

    private static Uri RequirePage(string address) => ResolvePage(address) ?? throw new InvalidDataException("厂长站点地址无效。");

    private static IParentNode Header(IDocument document) =>
        document.QuerySelector("header#header template") is IHtmlTemplateElement template ? template.Content : document;

    private static Uri? ResolvePage(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (Uri.TryCreate(Origin, address, out var result) && MediaRequestHeaders.SameOrigin(Origin, result) && result.UserInfo.Length == 0)
            return result;
        if (Uri.TryCreate(address, UriKind.Absolute, out var legacy) && legacy.Scheme is "https" or "http" &&
            legacy.UserInfo.Length == 0 && legacy.IsDefaultPort && legacy.IdnHost is "czys.pro" or "www.czys.pro" or "4kcz.com")
            return new Uri(Origin, legacy.PathAndQuery);
        return null;
    }

    private static Uri? HttpLink(Uri page, string? address) => string.IsNullOrWhiteSpace(address) ? null :
        Uri.TryCreate(page, address, out var url) && url.Scheme == "https" && url.UserInfo.Length == 0 ? url : null;

    private static string ImageUrl(IElement? image, Uri page) =>
        (HttpLink(page, image?.GetAttribute("data-original")) ?? HttpLink(page, image?.GetAttribute("src")))?.AbsoluteUri ?? string.Empty;

    private static string Text(IElement? element) => element?.TextContent.Replace("\u200b", string.Empty, StringComparison.Ordinal).Trim() ?? string.Empty;
    private static string CleanValue(string value) => value.Trim() == "false" ? string.Empty : value.Trim();
    private static string Field(Dictionary<string, string> fields, string key) => CleanValue(fields.GetValueOrDefault(key, string.Empty));
    private static string EscapeLabel(string name) => name.Replace('$', '＄').Replace('#', '＃');
}
