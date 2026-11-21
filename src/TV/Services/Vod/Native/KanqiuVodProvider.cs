using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public sealed partial class KanqiuVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private static readonly Uri DefaultOrigin = new("https://www.88kanqiu.one/");
    private const string PlayerIdPrefix = "kanqiu:";
    private const string ReplayCategory = "replay";
    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36";

    public bool CanHandle(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_Kanqiu", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_KanqiuGuard", StringComparison.OrdinalIgnoreCase));

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (html, page) = await ReadPageAsync(site, Origin(site), cancellationToken).ConfigureAwait(false);
        var response = ReadSchedule(site, html, page);
        response.HomeCategoryName = "全部直播";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in html.QuerySelectorAll("a[href]"))
        {
            if (!TrySitePage(page, link.GetAttribute("href"), out var address)) continue;
            var id = address.AbsolutePath == "/replay" && address.Query.Length == 0 ? ReplayCategory :
                CategoryPath().Match(address.AbsolutePath).Groups[1].Value;
            var name = link.TextContent.Trim();
            if (id.Length > 0 && name.Length > 0 && seen.Add(id))
                response.Categories.Add(new VodCategory { Id = id, Name = name });
        }
        return response;
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (categoryId == ReplayCategory) return await ReadReplayPageAsync(site, page, cancellationToken).ConfigureAwait(false);
        if (page > 1) return new CmsResponse { Page = page, PageCount = 1 };
        if (categoryId.Length == 0) return await GetHomeAsync(site, cancellationToken).ConfigureAwait(false);
        if (!NumericId().IsMatch(categoryId)) return new CmsResponse { Page = page, PageCount = 1 };
        var (html, address) = await ReadPageAsync(site, new Uri(Origin(site), $"match/{categoryId}/live"), cancellationToken)
            .ConfigureAwait(false);
        return ReadSchedule(site, html, address);
    }

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var origin = Origin(site);
        if (!TrySitePage(origin, id, out var target)) throw new InvalidDataException("赛事地址无效。");
        var replay = ReplayPath().IsMatch(target.AbsolutePath);
        if (!replay && !MatchPath().IsMatch(target.AbsolutePath)) throw new InvalidDataException("赛事地址无效。");
        var (html, page) = await ReadPageAsync(site, target, cancellationToken).ConfigureAwait(false);
        if (replay) return ReadReplay(site, html, page, id);
        var info = html.QuerySelector(".game-info-container") ?? throw new InvalidDataException("原站未返回赛事详情。");
        var title = TeamNames(info);
        if (title.Length == 0) title = html.Title ?? string.Empty;
        if (title.Length == 0) throw new InvalidDataException("原站未返回赛事名称。");
        var item = new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name, Name = title,
            Picture = Picture(info.QuerySelector("img.team-logo, img"), page),
            TypeName = Text(info, ".game-name"), Remarks = JoinText(Text(info, ".game-time"), Text(info, ".game-status")),
            Content = JoinText(title, Text(info, ".game-name"), Text(info, ".game-time"), Text(info, ".game-status"))
        };
        try
        {
            var lines = await ReadLinesAsync(site, page, cancellationToken).ConfigureAwait(false);
            item.PlayFrom = string.Join("$$$", lines.Select(line => line.Name));
            item.PlayUrl = string.Join("$$$", lines.Select(line => "直播$" + PlayerId(line.Address)));
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or FormatException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Content += "\n原站线路暂时无法读取。";
        }
        return item;
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var address = id.StartsWith(PlayerIdPrefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(id[PlayerIdPrefix.Length..]))
            : id.Replace("***", "#", StringComparison.Ordinal);
        var origin = Origin(site);
        if (!TryHttpPage(origin, address, out var uri)) throw new InvalidDataException("赛事播放地址无效。");

        var headers = new Dictionary<string, string>(MediaRequestHeaders.ForTarget(Headers(site), origin, uri), StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("Referer", origin.GetLeftPart(UriPartial.Authority) + "/");
        var parse = PlayUrlResolver.IsDirectMediaUrl(uri.AbsoluteUri) ? 0 : 1;
        return Task.FromResult(new SpiderPlayResult(uri.AbsoluteUri, headers, parse, 0, ReplaceHeaders: true));
    }

    private static CmsResponse ReadSchedule(SourceSite site, IDocument html, Uri page)
    {
        var schedule = html.QuerySelector(".content-container") ?? html.QuerySelector("h1.module-title")?.Closest(".col-md-9")
            ?? throw new InvalidDataException("原站未返回赛事目录。");
        var matches = new List<VodItem>();
        foreach (var list in schedule.QuerySelectorAll(".list-group"))
        {
            var date = string.Empty;
            foreach (var row in list.Children)
            {
                if (!row.ClassList.Contains("group-game-item"))
                {
                    var label = row.TextContent.Trim();
                    if (DateLabel().IsMatch(label)) date = label;
                    continue;
                }

                var name = TeamNames(row);
                var link = row.QuerySelector("a[href*='/play']")?.GetAttribute("href");
                var id = row.QuerySelector(".pay-btn[data-id]")?.GetAttribute("data-id");
                if (link is null && id is not null && NumericId().IsMatch(id)) link = $"/live/{id}/play";
                if (name.Length == 0 || !TrySitePage(page, link, out var address) || !MatchPath().IsMatch(address.AbsolutePath)) continue;

                var league = Text(row, ".game-type, .game-name");
                if (league.Length == 0)
                    league = row.QuerySelector(".game-time")?.PreviousElementSibling?.TextContent.Trim() ?? string.Empty;
                matches.Add(new VodItem
                {
                    Id = address.PathAndQuery, Name = name, SiteKey = site.Key, SiteName = site.Name,
                    Picture = Picture(row.QuerySelector("img"), page),
                    TypeName = league,
                    Remarks = JoinText(date, Text(row, ".game-time, .category-game-time"), Text(row, ".pay-btn .btn"))
                });
            }
        }
        return new CmsResponse { List = matches, Page = 1, PageCount = 1, Total = matches.Count, Limit = matches.Count };
    }

    private async Task<CmsResponse> ReadReplayPageAsync(SourceSite site, int number, CancellationToken token)
    {
        var pageNumber = Math.Max(1, number);
        var path = pageNumber == 1 ? "replay" : "replay?page=" + pageNumber.ToString(CultureInfo.InvariantCulture);
        var (html, page) = await ReadPageAsync(site, new Uri(Origin(site), path), token).ConfigureAwait(false);
        var items = new List<VodItem>();
        foreach (var link in html.QuerySelectorAll(".list-group .media-heading a[href]"))
        {
            var name = link.TextContent.Trim();
            if (name.Length == 0 || !TrySitePage(page, link.GetAttribute("href"), out var target) || !ReplayPath().IsMatch(target.AbsolutePath)) continue;
            var card = link.Closest(".media") ?? link;
            items.Add(new VodItem
            {
                Id = target.PathAndQuery, Name = name, SiteKey = site.Key, SiteName = site.Name,
                Picture = Picture(card.QuerySelector("img.media-object"), page),
                Remarks = Text(card, ".media-body > span"), Blurb = Text(card, ".playback-desc")
            });
        }

        var lastPage = 1;
        foreach (var link in html.QuerySelectorAll(".pagination a[href]"))
        {
            if (TrySitePage(page, link.GetAttribute("href"), out var target) && target.AbsolutePath == "/replay" &&
                target.Query.StartsWith("?page=", StringComparison.Ordinal) &&
                int.TryParse(target.Query.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                lastPage = Math.Max(lastPage, index);
        }
        return new CmsResponse { List = items, Page = pageNumber, PageCount = lastPage, Limit = items.Count };
    }

    private static VodItem ReadReplay(SourceSite site, IDocument html, Uri page, string id)
    {
        var panel = html.QuerySelector(".playback-detail-container") ?? throw new InvalidDataException("原站未返回录像详情。");
        var episodes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var link in panel.QuerySelectorAll("a[href]"))
        {
            var label = link.TextContent.Trim();
            if (label.Length == 0 || !TryHttpPage(page, link.GetAttribute("href"), out var address)) continue;
            var marker = label.IndexOf(']');
            var line = label.StartsWith('[') && marker > 1 ? label[1..marker].Trim() : "回放";
            var name = label.StartsWith('[') && marker > 1 && marker + 1 < label.Length ? label[(marker + 1)..].Trim() : label;
            line = line.Replace("$", "＄", StringComparison.Ordinal);
            name = name.Replace("$", "＄", StringComparison.Ordinal).Replace("#", "＃", StringComparison.Ordinal);
            if (!episodes.TryGetValue(line, out var group)) episodes.Add(line, group = []);
            group.Add(name + "$" + PlayerId(address));
        }
        return new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name, Name = Text(panel, ".playback-detail-title"),
            Picture = Picture(panel.QuerySelector("img"), page), Content = panel.TextContent.Trim(),
            PlayFrom = string.Join("$$$", episodes.Keys),
            PlayUrl = string.Join("$$$", episodes.Values.Select(group => string.Join('#', group)))
        };
    }

    private static string PlayerId(Uri address) => PlayerIdPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(address.AbsoluteUri));

    private async Task<(IDocument Html, Uri Page)> ReadPageAsync(SourceSite site, Uri address, CancellationToken token)
    {
        var result = await http.GetBytesAsync(address.AbsoluteUri, Headers(site), maxBytes: 2 * 1024 * 1024,
            cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
        var html = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(result.Content), token).ConfigureAwait(false);
        return (html, result.FinalUri);
    }

    private async Task<List<PlayableLine>> ReadLinesAsync(SourceSite site, Uri page, CancellationToken token)
    {
        var match = MatchPath().Match(page.AbsolutePath);
        if (!match.Success) throw new InvalidDataException("原站赛事地址无效。");
        var api = new Uri(page, $"/live/{match.Groups[1].Value}/source");
        var headers = new Dictionary<string, string>(MediaRequestHeaders.ForTarget(Headers(site), Origin(site), api), StringComparer.OrdinalIgnoreCase);
        headers["Referer"] = page.AbsoluteUri;
        headers["X-Requested-With"] = "XMLHttpRequest";

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var response = await http.GetBytesAsync(api.AbsoluteUri, headers, maxBytes: 256 * 1024,
            cancellationToken: deadline.Token, scopeRedirectHeaders: true).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<SourceEnvelope>(response.Content);
        if (string.IsNullOrWhiteSpace(envelope?.Data)) return [];
        if (envelope.Data.Length <= 8) throw new InvalidDataException("原站返回的线路数据不完整。");

        // The public endpoint wraps the same Base64-encoded JSON used by the site's player.
        var data = JsonSerializer.Deserialize<SourceList>(Convert.FromBase64String(envelope.Data[6..^2]));
        var lines = new List<PlayableLine>();
        if (data is not { ShareStatus: 0, ShareStatusCheck: 0, Links: { } links }) return lines;
        foreach (var link in links)
        {
            if (link is not { ShareLock: 0 }) continue;
            var name = link.Name?.Replace("$", "＄", StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(name) || !TryHttpPage(page, link.Url, out var address)) continue;
            lines.Add(new PlayableLine(name, address));
        }
        return lines;
    }

    private static Uri Origin(SourceSite site) =>
        site.Ext.ValueKind == JsonValueKind.String && Uri.TryCreate(site.Ext.GetString(), UriKind.Absolute, out var address) &&
        address.Scheme is "http" or "https" && address.UserInfo.Length == 0
            ? new Uri(address.GetLeftPart(UriPartial.Authority) + "/") : DefaultOrigin;

    private static Dictionary<string, string> Headers(SourceSite site)
    {
        var headers = new Dictionary<string, string>(site.GetHeaders(), StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("User-Agent", BrowserAgent);
        return headers;
    }

    private static string TeamNames(IElement node) =>
        string.Join(" vs ", node.QuerySelectorAll(".team-name").Select(team => team.TextContent.Trim())
            .Where(team => team.Length > 0).Distinct(StringComparer.Ordinal));

    private static string Text(IElement node, string selector) => node.QuerySelector(selector)?.TextContent.Trim() ?? string.Empty;

    private static string JoinText(params string[] values) => string.Join(" · ", values.Where(value => value.Length > 0));

    private static string Picture(IElement? image, Uri origin)
    {
        var link = image?.GetAttribute("data-src") ?? image?.GetAttribute("src");
        return TryHttpPage(origin, link, out var address) ? address.AbsoluteUri : string.Empty;
    }

    private static bool TrySitePage(Uri origin, string? reference, out Uri address)
    {
        if (!TryHttpPage(origin, reference, out address) || address.IdnHost != origin.IdnHost) return false;
        // IDs saved by the previous adapter may still use the HTTP form of this match page.
        if (address.Scheme == "http" && origin.Scheme == "https" && address.IsDefaultPort)
            address = new Uri(origin, address.PathAndQuery);
        return MediaRequestHeaders.SameOrigin(origin, address);
    }

    private static bool TryHttpPage(Uri origin, string? reference, out Uri address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(reference) || !Uri.TryCreate(origin, reference.Trim(), out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
        address = uri;
        return true;
    }

    private sealed record SourceEnvelope([property: JsonPropertyName("data")] string? Data);
    private sealed record SourceList([property: JsonPropertyName("shareStatus")] int ShareStatus,
        [property: JsonPropertyName("shareStatusCheck")] int ShareStatusCheck,
        [property: JsonPropertyName("links")] List<SourceLine?>? Links);
    private sealed record SourceLine([property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url, [property: JsonPropertyName("shareLock")] int ShareLock);
    private sealed record PlayableLine(string Name, Uri Address);

    [GeneratedRegex(@"^/match/([0-9]+)/live/?$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryPath();
    [GeneratedRegex(@"^/live/([0-9]+)/play/?$", RegexOptions.CultureInvariant)]
    private static partial Regex MatchPath();
    [GeneratedRegex(@"^/replay/[0-9]+/?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReplayPath();
    [GeneratedRegex(@"^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericId();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateLabel();
}
