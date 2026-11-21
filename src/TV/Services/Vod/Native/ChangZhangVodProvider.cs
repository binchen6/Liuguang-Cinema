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

public sealed class ChangZhangVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    // The publisher lists this address at https://www.czzy.site/.
    private static readonly Uri Origin = new("https://www.4kcz.com/");
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";

    public bool CanHandle(SourceSite site) => site.Type == 3 &&
        site.Api.Equals("csp_NewCzGuard", StringComparison.OrdinalIgnoreCase) &&
        (site.Ext.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
         site.Ext.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(site.Ext.GetString()) ||
             ResolvePage(site.Ext.GetString()) is not null));

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (document, effective) = await ReadPageAsync(site, Origin, cancellationToken).ConfigureAwait(false);
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
        var (document, effective) = await ReadPageAsync(site, address, cancellationToken).ConfigureAwait(false);
        return Catalog(site, document, effective, page);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var path = page <= 1 ? "/nimasile" : FormattableString.Invariant($"/nimasile/page/{page}/");
        var address = new Uri(Origin, path + "?q=" + Uri.EscapeDataString(keyword));
        var (document, effective) = await ReadPageAsync(site, address, cancellationToken).ConfigureAwait(false);
        if (Header(document).QuerySelector("form.w-search-form input[name=q]") is null)
            throw new InvalidDataException("厂长原站未返回站内搜索页。");
        return Catalog(site, document, effective, page);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var (document, page) = await ReadPageAsync(site, RequirePage(id), cancellationToken).ConfigureAwait(false);
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
        var (document, page) = await ReadPageAsync(site, RequirePage(id), token).ConfigureAwait(false);
        var frame = HttpLink(page, document.QuerySelector(".videoplay iframe.viframe[src]")?.GetAttribute("src"));
        var video = HtmlPageMedia.ReadVideoElement(document, page);
        Uri? frameOrigin = null;
        if (video is null && frame is not null && frame.IdnHost == "plaa.py1080p.com" &&
            frame.AbsolutePath == "/player/py.php")
        {
            var published = HttpUtility.ParseQueryString(frame.Query)["url"];
            // The player can publish an HTTPS playlist on port 907.
            if (Uri.TryCreate(published, UriKind.Absolute, out var media) && media.Scheme == "https" &&
                media.UserInfo.Length == 0 && (media.IsDefaultPort || media.Port == 907) &&
                media.IdnHost.EndsWith(".py1080p.com", StringComparison.OrdinalIgnoreCase) &&
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
            : new SpiderPlayResult(page.AbsoluteUri, headers, 1, 0, ReplaceHeaders: true);
    }

    private async Task<(IDocument Document, Uri Effective)> ReadPageAsync(SourceSite site, Uri address, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(site.GetHeaders(), StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("User-Agent", UserAgent);
        headers.TryAdd("Referer", Origin.AbsoluteUri);
        var payload = await http.GetBytesAsync(address.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024,
            cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
        if (!MediaRequestHeaders.SameOrigin(Origin, payload.FinalUri)) throw new InvalidDataException("厂长入口已跳转到其他域名。");
        var document = await new HtmlParser().ParseDocumentAsync(SourceHttpClient.DecodeText(payload.Content), token).ConfigureAwait(false);
        return (document, payload.FinalUri);
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

    private static string Text(IElement? element) => element?.TextContent.Trim() ?? string.Empty;
    private static string CleanValue(string value) => value.Trim() == "false" ? string.Empty : value.Trim();
    private static string Field(Dictionary<string, string> fields, string key) => CleanValue(fields.GetValueOrDefault(key, string.Empty));
    private static string EscapeLabel(string name) => name.Replace('$', '＄').Replace('#', '＃');
}
