using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Dom;
using TV.Models;
using TV.Services.Http;
using TV.Services.Playback;

namespace TV.Services.Vod.Native;

public sealed partial class UniversalAutoHtmlVodProvider
{
    private static CmsResponse? TryParseCms(string raw, SourceSite site, Uri source, int page)
    {
        var text = raw.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        CmsResponse? result;
        try
        {
            if (text.StartsWith('{'))
            {
                using var json = JsonDocument.Parse(text);
                var content = json.RootElement;
                if (content.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    content = data;
                if (!content.EnumerateObject().Any(property =>
                    (property.Name.Equals("list", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("class", StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.Array)) return null;
                result = content.Deserialize<CmsResponse>(JsonUtil.Options);
            }
            else if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<rss", StringComparison.OrdinalIgnoreCase))
                result = CmsClient.ParseXml(text);
            else return null;
        }
        catch (Exception ex) when (ex is JsonException or XmlException) { return null; }
        if (result is null) return null;
        result.List ??= [];
        result.Categories ??= [];
        result.List.RemoveAll(item => item is null || string.IsNullOrWhiteSpace(item.Id));
        result.Categories.RemoveAll(category => category is null || string.IsNullOrWhiteSpace(category.Id) ||
            string.IsNullOrWhiteSpace(category.Name) || category.Name.Contains("伦理", StringComparison.Ordinal));
        foreach (var item in result.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Picture = NativeSiteToolkit.ResolveUrl(source, item.Picture);
            item.Content = PlaybackParsing.StripHtml(item.Content);
        }
        result.Page = page;
        if (result.PageCount <= 0 && result.Total > 0 && result.Limit > 0)
            result.PageCount = (int)Math.Ceiling((double)result.Total / result.Limit);
        result.PageCount = Math.Max(page, result.PageCount);
        return result;
    }

    private static Uri WithQuery(Uri address, params (string Key, string Value)[] query)
    {
        var builder = new UriBuilder(address);
        var keys = query.Select(static part => part.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !keys.Contains(Uri.UnescapeDataString(part.Split('=', 2)[0])));
        builder.Query = string.Join('&', existing.Concat(query.Select(part => Uri.EscapeDataString(part.Key) + "=" + Uri.EscapeDataString(part.Value))));
        return builder.Uri;
    }

    private static Uri? ResolveAddress(Uri page, string? value, bool sameOrigin) =>
        !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith('#') && Uri.TryCreate(page, value.Trim(), out var uri) &&
        MediaRequestHeaders.IsHttpAddress(uri) && (!sameOrigin || MediaRequestHeaders.SameOrigin(page, uri))
            ? new UriBuilder(uri) { Fragment = "" }.Uri : null;

    private static Uri? FindNextPage(IDocument doc, Uri current)
    {
        var links = doc.QuerySelectorAll("link[rel~='next'][href], a[rel~='next'][href], .pagination a[href], .page a[href], .pages a[href], .stui-page a[href]");
        foreach (var link in links)
        {
            var text = NativeSiteToolkit.CollapseSpaces(link.TextContent);
            var rel = link.GetAttribute("rel");
            if (rel?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("next", StringComparer.OrdinalIgnoreCase) != true &&
                !link.ClassList.Contains("next") && text is not ("下一页" or "下页" or "Next" or "›" or "»")) continue;
            if (ResolveAddress(current, link.GetAttribute("href"), sameOrigin: true) is { } next && next != current) return next;
        }
        return null;
    }

    private static SearchForm? DiscoverSearchForm(IDocument doc, Uri current)
    {
        foreach (var form in doc.QuerySelectorAll("form"))
        {
            if (form.GetAttribute("method") is { Length: > 0 } method && !method.Equals("get", StringComparison.OrdinalIgnoreCase)) continue;
            var field = form.QuerySelector("input[name='wd'], input[name='q'], input[name='s'], input[name='keyword'], input[name='searchword'], input[type='search'][name]")?
                .GetAttribute("name")?.Trim();
            var action = form.GetAttribute("action");
            if (!string.IsNullOrWhiteSpace(field) && ResolveAddress(current, string.IsNullOrWhiteSpace(action) ? current.AbsoluteUri : action, sameOrigin: true) is { } target)
                return new SearchForm(target, field);
        }
        return null;
    }

    private static List<VodCategory> ExtractCategories(IDocument doc, Uri page)
    {
        var categories = new List<VodCategory>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in doc.QuerySelectorAll("nav a[href], header a[href], .nav-links a[href], .nav-link[href], .stui-header__menu a[href], .top-nav a[href]"))
        {
            var name = NativeSiteToolkit.CollapseSpaces(link.TextContent);
            if (name.Length is 0 or > 18 || name is "首页" or "主页" or "登录" or "注册" or "下载" or "APP" or "更多" ||
                name.Contains("伦理", StringComparison.Ordinal) || name.Contains("福利", StringComparison.Ordinal)) continue;
            if (ResolveAddress(page, link.GetAttribute("href"), sameOrigin: true) is { } uri && uri != page &&
                CategoryPath().IsMatch(uri.AbsolutePath) && seen.Add(uri.PathAndQuery))
                categories.Add(new VodCategory { Id = uri.PathAndQuery, Name = name });
        }
        return categories;
    }

    private static List<VodItem> ExtractCards(IDocument doc, Uri page, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var known = doc.QuerySelectorAll("a.video-card[href], .video-card, .module-item, .module-card-item, .module-search-item, .stui-vodlist__box, .myui-vodlist__box, .fed-list-item, .vodlist_item, .movie-item, .thread-item");
        foreach (var card in known)
        {
            var link = card.HasAttribute("href") ? card : card.QuerySelector("a[href]");
            if (link is not null) Add(card, link, requireDetailsPath: false);
            if (items.Count >= 300) return items;
        }
        if (items.Count > 0) return items;
        foreach (var link in doc.QuerySelectorAll("main a[href], article a[href], a[href*='/voddetail/'], a[href*='/vod/detail/'], a[href*='/detail/'], a[href*='/movie/'], a[href*='/video/'], a[href*='/thread-']"))
        {
            Add(link.Closest("li, article, .card, .item, dl") ?? link, link, requireDetailsPath: true);
            if (items.Count >= 300) break;
        }
        return items;

        void Add(IElement card, IElement link, bool requireDetailsPath)
        {
            var target = ResolveAddress(page, link.GetAttribute("href"), sameOrigin: true);
            if (target is null || target == page || target.AbsolutePath == "/" ||
                target.AbsolutePath.Contains("/user/", StringComparison.OrdinalIgnoreCase) ||
                target.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                requireDetailsPath && !DetailPath().IsMatch(target.AbsolutePath)) return;
            var image = card.QuerySelector("img");
            var name = NativeSiteToolkit.CollapseSpaces(NativeSiteToolkit.FirstNonEmpty(
                card.QuerySelector(".video-title, .video-name, .module-item-title, .title, h3, h4, h2")?.TextContent,
                link.GetAttribute("title"), image?.GetAttribute("alt"), link.TextContent));
            if (name.Length is 0 or > 120 || !seen.Add(target.PathAndQuery)) return;
            items.Add(new VodItem
            {
                Id = target.PathAndQuery, SiteKey = site.Key, SiteName = site.Name, Name = name,
                Picture = NativeSiteToolkit.ExtractImageUrl(image ?? link, page),
                Remarks = NativeSiteToolkit.CollapseSpaces(card.QuerySelector(".video-episode, .module-item-note, .module-item-text, .pic-text, .remarks, .badge, .tag")?.TextContent)
            });
        }
    }

    private static (Uri Address, string? ContentType)? FindMediaElement(IDocument doc, Uri page)
    {
        foreach (var element in doc.QuerySelectorAll("video[src], video[data-src], source[src], audio[src], audio[data-src]"))
        {
            if (element.LocalName == "source" && element.ParentElement?.LocalName is not ("audio" or "video")) continue;
            var value = element.GetAttribute("src") ?? element.GetAttribute("data-src");
            if (ResolveAddress(page, value, sameOrigin: false) is not { } target) continue;
            var type = element.GetAttribute("type")?.Split(';', 2)[0].Trim();
            if (PlayUrlResolver.IsDirectMediaUrl(target.AbsoluteUri) || type?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
                type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true || HlsManifest.IsPlaylist(target.AbsoluteUri, type))
                return (target, type);
        }
        return null;
    }

    private static string PageNumber(int page) => page.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\$\$\$|[,;|]\s*(?=https?://)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressSeparator();

    [GeneratedRegex(@"/(?:vodtype|vod/type|vodshow|type|show|category|categories|collection|list)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CategoryPath();

    [GeneratedRegex(@"/(?:voddetail/|vod/detail/|detail/|movies?/|video/|thread-)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailPath();

    [GeneratedRegex(@"/(?:vodplay|vod/play|play|vplay|watch)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerPath();

    [GeneratedRegex(@"https?:\\?/\\?/[^\s""'<>\\]+?\.(?:m3u8|mp4|mp3|m4a|flv)(?:\?[^\s""'<>\\]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectStreamPattern();
}
