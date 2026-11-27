using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;

namespace TV.Services.Vod.Native;

public sealed partial class WoggNativeProvider : ICategoryFilterVodProvider
{
    private sealed record FilterField(string Key, string Name, int Slot);
    private static readonly FilterField[] FilterFields =
    [
        new("class", "剧情", 3), new("area", "地区", 1), new("year", "时间", 11),
        new("letter", "字母", 5), new("by", "排序", 2)
    ];
    private readonly SourceHttpClient _http;
    private readonly CloudDriveService _cloud;
    private readonly ConcurrentDictionary<string, Uri> _workingOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _slashMacCmsStyle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _forbiddenUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _requestGate = new(3, 3);
    private readonly SemaphoreSlim _searchGate = new(1);
    private long _lastSearchEnd;
    private static readonly TimeSpan SearchInterval = TimeSpan.FromMilliseconds(3100);
    private static readonly Uri LegacyOrigin = new("https://www.wogg.net/");
    private static readonly Uri[] PublicOrigins = [new("https://woggpan.888484.xyz/"), LegacyOrigin];
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private static readonly HashSet<string> CloudCmsApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "csp_FishCloud", "csp_FishCloudGuard",
        "csp_Muou", "csp_MuouGuard",
        "csp_Duoduo", "csp_DuoduoGuard",
        "csp_Ouge", "csp_OugeGuard",
        "csp_Wax", "csp_WaxGuard",
        "csp_Mogg", "csp_MoggGuard",
        "csp_Huban", "csp_HubanGuard",
        "csp_Huajuan", "csp_HuajuanGuard",
        "csp_Shayang", "csp_ShayangGuard",
        "csp_ErXiao", "csp_ErXiaoGuard",
        "csp_XiaoMi", "csp_XiaoMiGuard"
    };

    public WoggNativeProvider(SourceHttpClient http, CloudDriveService cloud)
    {
        _http = http;
        _cloud = cloud;
    }

    private static bool IsWoggApi(string api) =>
        api.Equals("csp_WoGGGuard", StringComparison.OrdinalIgnoreCase) ||
        api.Equals("csp_Wogg", StringComparison.OrdinalIgnoreCase);

    public static bool Supports(SourceSite site) =>
        site.Type == 3 && (IsWoggApi(site.Api) || (CloudCmsApis.Contains(site.Api) && ConfiguredOrigins(site.Ext).Any()));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var document = await OpenAsync(site, "/", cancellationToken).ConfigureAwait(false);
        var categories = new List<VodCategory>();
        var seenCat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usesSlashStyle = false;
        foreach (var link in document.QuerySelectorAll(".nav-link, .nav-menu-item a[href], header a[href*='/vod']"))
        {
            var href = link.GetAttribute("href") ?? string.Empty;
            var match = CategoryRegex().Match(href);
            if (!match.Success)
            {
                match = SlashCategoryRegex().Match(href);
                if (match.Success) usesSlashStyle = true;
            }
            var catName = link.TextContent.Trim();
            if (match.Success && catName.Length > 0 && seenCat.Add(match.Groups[1].Value))
                categories.Add(new VodCategory { Id = match.Groups[1].Value, Name = catName });
        }
        var visible = document.QuerySelectorAll(".module-item").Where(card => card.Closest(".tab-list:not(.selected)") is null).ToArray();
        var items = ParseCards(visible);
        if (items.Any(it => it.Id.Contains("/vod/detail/", StringComparison.OrdinalIgnoreCase)))
            usesSlashStyle = true;
        _slashMacCmsStyle[site.Key] = usesSlashStyle;
        return Prepare(new CmsResponse
        {
            Categories = categories, List = items,
            HomeCategoryName = Text(document.QuerySelector("#index-main .module-heading .module-title, .module-heading .module-title"))
        }, site, document.Url);
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken)
    {
        string path;
        if (_slashMacCmsStyle.TryGetValue(site.Key, out var slash) && slash)
        {
            var segs = new List<string> { "id", Uri.EscapeDataString(categoryId) };
            if (filters is not null)
            {
                foreach (var field in FilterFields)
                    if (filters.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        segs.Add(field.Key);
                        segs.Add(Uri.EscapeDataString(value));
                    }
            }
            if (page > 1)
            {
                segs.Add("page");
                segs.Add(page.ToString(CultureInfo.InvariantCulture));
            }
            path = $"/index.php/vod/show/{string.Join('/', segs)}.html";
        }
        else
        {
            var parts = new string[12];
            parts[0] = categoryId;
            parts[8] = page.ToString(CultureInfo.InvariantCulture);
            if (filters is not null)
                foreach (var field in FilterFields)
                    if (filters.TryGetValue(field.Key, out var value)) parts[field.Slot] = Uri.EscapeDataString(value);
            path = $"/index.php/vodshow/{string.Join('-', parts)}.html";
        }
        var document = await OpenAsync(site, path, cancellationToken).ConfigureAwait(false);
        var response = ReadPage(document, ParseCards(document.QuerySelectorAll(".module-item")), page);
        if (page == 1 && filters is not { Count: > 0 }) response.Filters = ReadCategoryFilters(document, categoryId);
        return Prepare(response, site, document.Url);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var slash = _slashMacCmsStyle.TryGetValue(site.Key, out var isSlash) && isSlash;
        var path = slash
            ? FormattableString.Invariant($"/index.php/vod/search/page/{page}/wd/{Uri.EscapeDataString(keyword)}.html")
            : FormattableString.Invariant($"/index.php/vodsearch/----------{page}---.html?wd={Uri.EscapeDataString(keyword)}");
        IDocument document;
        await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastSearchEnd != 0)
            {
                var wait = SearchInterval - Stopwatch.GetElapsedTime(_lastSearchEnd);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            try { document = await OpenAsync(site, path, cancellationToken).ConfigureAwait(false); }
            finally { _lastSearchEnd = Stopwatch.GetTimestamp(); }
        }
        finally { _searchGate.Release(); }
        var items = new List<VodItem>();
        foreach (var card in document.QuerySelectorAll(".module-search-item"))
        {
            var link = card.QuerySelector(".video-serial") ?? card.QuerySelector(".video-info-header h3 a, .module-item-pic a[href]");
            var name = link?.GetAttribute("title")?.Trim() ?? card.QuerySelector(".video-info-header h3")?.TextContent.Trim() ?? string.Empty;
            if (link is null || name.Length == 0) continue;
            var item = new VodItem
            {
                Id = link.GetAttribute("href") ?? string.Empty, Name = name,
                Picture = Image(card.QuerySelector(".module-item-pic img"), document.Url),
                Remarks = Text(card.QuerySelector(".video-serial"))
            };
            FillInfo(item, card, fullPlot: false);
            items.Add(item);
        }
        if (items.Count == 0)
            items = ParseCards(document.QuerySelectorAll(".module-item"));
        return Prepare(ReadPage(document, items, page), site, document.Url);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = Uri.TryCreate(id, UriKind.Absolute, out var saved) && saved.Scheme is "http" or "https"
            ? saved.PathAndQuery : id;
        if (!path.Contains("/voddetail/", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/vod/detail/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("云盘站点详情地址无效。", nameof(id));
        var document = await OpenAsync(site, path, cancellationToken).ConfigureAwait(false);
        var url = document.Url;
        var item = new VodItem
        {
            Id = id,
            Name = document.QuerySelector(".video-info-header > .page-title, .video-info-header h1, h1")?.TextContent.Trim() ?? string.Empty,
            Picture = Image(document.QuerySelector(".module-item-pic img"), url)
        };
        FillInfo(item, document, fullPlot: true);

        var rows = document.QuerySelectorAll("[data-clipboard-text]").Select(node => node.GetAttribute("data-clipboard-text"))
            .Concat(document.QuerySelectorAll("a[href]").Select(node => node.GetAttribute("href"))
                .Where(href => href is not null && CloudShareLink.TryParse(href, out _)));
        var seen = new HashSet<Uri>();
        var counts = new Dictionary<string, int>();
        var shares = new List<(Uri Address, string Label, int Index, bool Supported)>();
        foreach (var row in rows)
        {
            if (!Uri.TryCreate(row?.Trim(), UriKind.Absolute, out var address) ||
                address.Scheme is not ("https" or "http") || address.UserInfo.Length != 0 || !seen.Add(address)) continue;
            var supported = CloudShareLink.TryParse(address.AbsoluteUri, out var parsed);
            var label = supported ? parsed.Provider switch
            {
                CloudProviderKind.Quark => "夸克", CloudProviderKind.UC => "UC", CloudProviderKind.Baidu => "百度", _ => "阿里"
            } : address.IdnHost switch { "pan.baidu.com" => "百度", "pan.xunlei.com" => "迅雷", _ => address.IdnHost };
            var number = counts.GetValueOrDefault(label) + 1;
            counts[label] = number;
            shares.Add((address, label, number, supported));
        }

        var lines = new List<PlaybackLine>();
        using var limiter = new SemaphoreSlim(2);
        var results = await Task.WhenAll(shares.Select(async source =>
        {
            if (!source.Supported) return (Share: (CloudShare?)null, Status: "在网盘打开");
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var share = await _cloud.GetShareAsync(source.Address.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                return (Share: (CloudShare?)share, Status: $"{share.Files.Count} 个文件");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Diagnostics.Log($"玩偶{source.Label}{source.Index}读取失败: {ex.GetType().Name} - {ex.Message}");
                var status = ex is CloudAuthenticationException ? "登录后读取" :
                    ex.Message.Contains("已失效", StringComparison.Ordinal) ? "分享已失效" :
                    ex.Message.Contains("提取码", StringComparison.Ordinal) ? "需要提取码" :
                    ex.Message.Contains("没有可播放", StringComparison.Ordinal) ? "未找到媒体" : "目录读取失败";
                return (Share: (CloudShare?)null, Status: status);
            }
            finally { limiter.Release(); }
        })).ConfigureAwait(false);
        for (var i = 0; i < shares.Count; i++)
        {
            var source = shares[i];
            item.SourceShares.Add(new SourceShareEntry(source.Label + " " + source.Index, source.Address, results[i].Status));
            if (results[i].Share is not { } share)
            {
                if (results[i].Status == "登录后读取")
                    lines.Add(new PlaybackLine
                    {
                        Name = $"{source.Label}分享{source.Index}",
                        Episodes = [new Episode { Name = "登录后加载分享", Url = source.Address.AbsoluteUri }]
                    });
                continue;
            }
            var episodes = share.Files.Select(file => new Episode { Name = CloudDriveService.SafeEpisodeName(file.Name), Url = CloudId(file) }).ToList();
            if (episodes.Count == 0) continue;
            var originalName = share.Provider == CloudProviderKind.Quark ? $"夸父原{source.Index}" : $"{source.Label}原画{source.Index}";
            lines.Add(new PlaybackLine { Name = originalName, Episodes = episodes });
            if (share.Provider is CloudProviderKind.Quark or CloudProviderKind.UC)
                lines.Add(new PlaybackLine { Name = $"{source.Label}高清{source.Index}", Episodes = episodes });
        }
        item.PlayFrom = string.Join("$$$", lines.Select(line => line.Name));
        item.PlayUrl = string.Join("$$$", lines.Select(line => string.Join('#', line.Episodes.Select(episode => $"{episode.Name}${episode.Url}"))));
        if (string.IsNullOrWhiteSpace(item.Name)) item.Name = results.Select(result => result.Share).FirstOrDefault(share => share is not null)?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(item.Picture)) item.Picture = results.Select(result => result.Share).FirstOrDefault(share => share is not null)?.Picture ?? string.Empty;
        item.SiteKey = site.Key;
        item.SiteName = site.Name;
        item.Content = PlaybackParsing.StripHtml(item.Content);
        return item;
    }

    private async Task<IDocument> OpenAsync(SourceSite site, string path, CancellationToken cancellationToken)
    {
        HttpRequestException? accessError = null;
        Uri? challengeTarget = null;
        var isWogg = IsWoggApi(site.Api);
        var configured = ConfiguredOrigins(site.Ext).ToArray();
        var customConfigured = configured.Where(origin => !MediaRequestHeaders.SameOrigin(origin, LegacyOrigin)).ToArray();
        var ordered = new List<Uri>();
        if (_workingOrigins.TryGetValue(site.Key, out var working) &&
            (configured.Contains(working) || (isWogg && PublicOrigins.Contains(working))))
            ordered.Add(working);
        ordered.AddRange(customConfigured);
        if (isWogg) ordered.AddRange(PublicOrigins);
        ordered.AddRange(configured);

        var candidates = ordered.Distinct().Take(6).ToArray();
        var now = DateTimeOffset.UtcNow;
        var preferred = candidates.Where(origin => !IsInCooldown(origin, now)).ToArray();
        var roots = preferred.Length > 0 ? preferred : candidates;

        var detail = path.Contains("/voddetail/", StringComparison.OrdinalIgnoreCase) || path.Contains("/vod/detail/", StringComparison.OrdinalIgnoreCase);
        var search = path.Contains("/vodsearch/", StringComparison.OrdinalIgnoreCase) || path.Contains("/vod/search/", StringComparison.OrdinalIgnoreCase);
        var selector = detail ? ".video-info-header h1, .module-row-text, [data-clipboard-text]" : search ? ".search-stat, .module-search-item, .module-item" : ".module-item, .library-stat, .nav-link";

        foreach (var root in roots)
        {
            var maxAttempts = MediaRequestHeaders.SameOrigin(root, LegacyOrigin) ? 1 : 2;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var (document, statusCode, isChallenge) = await ReadDocumentAsync(root, path, cancellationToken).ConfigureAwait(false);
                    if (statusCode == HttpStatusCode.Forbidden || isChallenge)
                    {
                        var originKey = SiteSessionStore.NormalizeOrigin(root);
                        _forbiddenUntil[originKey] = DateTimeOffset.UtcNow.AddMinutes(10);
                        if (_http.Sessions is not null)
                            await _http.Sessions.ClearAsync(root, cancellationToken).ConfigureAwait(false);
                        challengeTarget ??= new Uri(root, "./" + path.TrimStart('/'));
                        accessError = new HttpRequestException($"玩偶入口 {root.IdnHost} 拒绝访问（HTTP {(int)statusCode}）。", null, HttpStatusCode.Forbidden);
                        Diagnostics.Log($"玩偶入口 {root.IdnHost}：{(int)statusCode} (challenge={isChallenge})");
                        break;
                    }
                    if ((int)statusCode >= 500 && attempt + 1 < maxAttempts)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if ((int)statusCode >= 400)
                    {
                        accessError ??= new HttpRequestException($"玩偶入口 {root.IdnHost} 返回 HTTP {(int)statusCode}。", null, statusCode);
                        Diagnostics.Log($"玩偶入口 {root.IdnHost}：{(int)statusCode}");
                        break;
                    }
                    if (search && ReadSearchDelay(document) is { } delay)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        (document, statusCode, isChallenge) = await ReadDocumentAsync(root, path, cancellationToken).ConfigureAwait(false);
                        if (statusCode == HttpStatusCode.Forbidden || isChallenge)
                        {
                            challengeTarget ??= new Uri(root, "./" + path.TrimStart('/'));
                            accessError = new HttpRequestException($"玩偶入口 {root.IdnHost} 拒绝访问（HTTP {(int)statusCode}）。", null, HttpStatusCode.Forbidden);
                            break;
                        }
                        if (ReadSearchDelay(document) is not null)
                            throw new HttpRequestException("玩偶哥哥搜索间隔未结束，请稍后重试。", null, HttpStatusCode.TooManyRequests);
                    }
                    if (document.QuerySelector(selector) is null) break;
                    _forbiddenUntil.TryRemove(SiteSessionStore.NormalizeOrigin(root), out _);
                    _workingOrigins[site.Key] = root;
                    return document;
                }
                catch (HttpRequestException ex) when (ex.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    if (accessError is null || ex.StatusCode == HttpStatusCode.Forbidden) accessError = ex;
                    Diagnostics.Log($"玩偶入口 {root.IdnHost}：{(int?)ex.StatusCode ?? 0}");
                    if (ex.StatusCode == HttpStatusCode.Forbidden) break;
                    if (attempt + 1 < maxAttempts) await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Diagnostics.Log($"玩偶入口 {root.IdnHost}：超时 (attempt {attempt + 1})");
                    if (attempt + 1 < maxAttempts) await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (challengeTarget is not null)
            throw new SiteVerificationRequiredException(site.Key, site.Name, challengeTarget,
                SiteVerificationKind.BrowserChallenge, "玩偶哥哥站点需要完成浏览器安全验证后访问。");
        throw accessError ?? new HttpRequestException("玩偶入口未返回片库页面。");
    }

    private bool IsInCooldown(Uri root, DateTimeOffset now)
    {
        var key = SiteSessionStore.NormalizeOrigin(root);
        if (!_forbiddenUntil.TryGetValue(key, out var until) || until <= now) return false;
        // If user just completed verification and saved a session, bypass cooldown immediately
        var session = _http.Sessions?.TryGetCached(root);
        if (session is not null && session.SavedAt > until.AddMinutes(-10))
        {
            _forbiddenUntil.TryRemove(key, out _);
            return false;
        }
        return true;
    }

    private async Task<(IDocument Document, HttpStatusCode StatusCode, bool IsChallenge)> ReadDocumentAsync(Uri root, string path, CancellationToken token)
    {
        await _requestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var requestUri = new Uri(root, "./" + path.TrimStart('/'));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = BrowserUserAgent,
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8",
                ["Referer"] = root.AbsoluteUri
            };
            var exchange = await _http.GetExchangeAsync(requestUri.AbsoluteUri, headers, cancellationToken: deadline.Token).ConfigureAwait(false);
            var html = SourceHttpClient.DecodeText(exchange.Payload.Content);
            var isChallenge = exchange.HasCloudflareChallenge ||
                (exchange.StatusCode == HttpStatusCode.Forbidden &&
                 (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                  html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                  html.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
                  html.Contains("正在验证您的浏览器", StringComparison.Ordinal)));
            var context = BrowsingContext.New(AngleSharp.Configuration.Default);
            var document = await context.OpenAsync(request => request.Content(html).Address(exchange.Payload.FinalUri.AbsoluteUri), deadline.Token).ConfigureAwait(false);
            return (document, exchange.StatusCode, isChallenge);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static TimeSpan? ReadSearchDelay(IDocument document)
    {
        if (document.QuerySelector("#wait") is not { } wait || document.Body?.TextContent.Contains("搜索时间间隔", StringComparison.Ordinal) != true)
            return null;
        return int.TryParse(Text(wait), out var seconds) && seconds is > 0 and <= 15
            ? TimeSpan.FromSeconds(seconds + 0.1) : SearchInterval;
    }

    private static IEnumerable<Uri> ConfiguredOrigins(JsonElement extension)
    {
        if (TryOrigin(extension) is { } direct) yield return direct;
        if (extension.ValueKind != JsonValueKind.Object) yield break;
        foreach (var key in new[] { "siteUrl", "site" })
        {
            if (!extension.TryGetProperty(key, out var value)) continue;
            if (TryOrigin(value) is { } uri) yield return uri;
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    if (TryOrigin(item) is { } candidate) yield return candidate;
        }
    }

    private static Uri? TryOrigin(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0) return null;
        return new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" }.Uri;
    }

    private static List<VodItem> ParseCards(IEnumerable<IElement> cards)
    {
        var result = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            var link = card.QuerySelector(".video-name a") ?? card.QuerySelector(".module-item-pic a[href]") ?? card.QuerySelector("a[href*='/vod']");
            var href = link?.GetAttribute("href")?.Trim() ?? string.Empty;
            var name = NativeSiteToolkit.FirstNonEmpty(
                card.QuerySelector(".video-name")?.TextContent,
                link?.GetAttribute("title"),
                card.QuerySelector(".module-item-pic img")?.GetAttribute("alt"));
            if (link is null || href.Length == 0 || name.Length == 0 || !seen.Add(href)) continue;
            var caption = card.QuerySelector(".module-item-caption .video-class");
            result.Add(new VodItem
            {
                Id = href, Name = name,
                Picture = ImageSource(card.QuerySelector(".module-item-pic img")),
                Remarks = Text(card.QuerySelector(".module-item-text")),
                Year = Text(caption?.PreviousElementSibling), TypeName = Text(caption), Area = Text(caption?.NextElementSibling),
                Actor = Links(card.QuerySelector(".module-item-content .video-tag")),
                Blurb = Text(card.QuerySelector(".module-item-content .video-text"))
            });
        }
        return result;
    }

    private static void FillInfo(VodItem item, IParentNode view, bool fullPlot)
    {
        var aux = view.QuerySelector(".video-info-header .video-info-aux");
        if (aux is not null)
        {
            var type = Text(aux.QuerySelector(".video-tag-icon"));
            var genres = aux.QuerySelectorAll("div.tag-link a[href*='/vodshow/']").Select(Text);
            item.TypeName = string.Join(" / ", genres.Prepend(type).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
            var tags = aux.Children.Where(tag => tag.ClassList.Contains("tag-link") &&
                tag.QuerySelector(".video-tag-icon, .slash") is null).Select(Text).Where(value => value.Length > 0).ToArray();
            item.Year = tags.FirstOrDefault(IsYear) ?? string.Empty;
            item.Area = tags.LastOrDefault(value => !IsYear(value)) ?? string.Empty;
        }
        foreach (var row in view.QuerySelectorAll(".video-info-main .video-info-item"))
        {
            var label = Text(row.PreviousElementSibling).TrimEnd('：', ':');
            switch (label)
            {
                case "导演": item.Director = Links(row); break;
                case "主演": item.Actor = Links(row); break;
                case "年代": if (Text(row) is { Length: > 0 } year) item.Year = year; break;
                case "连载":
                case "备注": item.Remarks = Text(row); break;
                case "剧情":
                    var content = fullPlot ? row.QuerySelector(".sqjj_a") ?? row : row;
                    item.Content = Text(content).Replace("[收起部分]", string.Empty, StringComparison.Ordinal)
                        .Replace("[展开全部]", string.Empty, StringComparison.Ordinal).Trim();
                    break;
            }
        }
    }

    private static CmsResponse ReadPage(IDocument document, List<VodItem> items, int page)
    {
        var total = 0;
        foreach (var script in document.QuerySelectorAll("script:not([src])"))
        {
            if (PageTotalRegex().Match(script.TextContent) is not { Success: true } match ||
                !int.TryParse(match.Groups[1].Value, out total)) continue;
            break;
        }
        var last = page;
        foreach (var link in document.QuerySelectorAll("a.page-number[href]"))
        {
            if (PageLinkRegex().Match(link.GetAttribute("href")!) is { Success: true } match &&
                int.TryParse(match.Groups[1].Value, out var value)) last = Math.Max(last, value);
        }
        return new CmsResponse { List = items, Page = page, Limit = items.Count, Total = total, PageCount = last };
    }

    private static JsonElement ReadCategoryFilters(IDocument document, string categoryId)
    {
        var definitions = new List<object>();
        foreach (var group in document.QuerySelectorAll(".library-box:not(.library-box-first) .library-list"))
        {
            var links = group.QuerySelectorAll("a.library-item[href]")
                .Select(link => (Name: Text(link), Path: FilterLinkRegex().Match(link.GetAttribute("href")!)))
                .Where(option => option.Name.Length > 0 && option.Path.Success && option.Path.Groups["category"].Value == categoryId)
                .ToArray();
            var field = FilterFields.FirstOrDefault(field => links.Any(option => option.Path.Groups[field.Key].Length > 0));
            if (field is null) continue;
            var choices = links.Select(option => new { n = option.Name, v = Uri.UnescapeDataString(option.Path.Groups[field.Key].Value) })
                .DistinctBy(option => option.v, StringComparer.Ordinal).ToArray();
            if (choices.Length > 1) definitions.Add(new { key = field.Key, name = field.Name, value = choices });
        }
        return definitions.Count == 0 ? default : JsonSerializer.SerializeToElement(new Dictionary<string, List<object>> { [categoryId] = definitions });
    }

    private static CmsResponse Prepare(CmsResponse response, SourceSite site, string responseUrl)
    {
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Id = Absolute(item.Id, responseUrl);
            if (Uri.TryCreate(item.Id, UriKind.Absolute, out var detail) && PublicOrigins.Any(origin => MediaRequestHeaders.SameOrigin(origin, detail)))
                item.Id = new Uri(LegacyOrigin, detail.PathAndQuery).AbsoluteUri;
            item.Picture = Absolute(item.Picture, responseUrl);
        }
        return response;
    }

    private static bool IsYear(string value) => value.Length == 4 && value.All(char.IsAsciiDigit);
    private static string Text(IElement? element) => element?.TextContent.Trim() ?? string.Empty;
    private static string Links(IElement? row) => row is null ? string.Empty : row.QuerySelectorAll("a") is { Length: > 0 } links
        ? string.Join(" / ", links.Select(Text).Where(text => text.Length > 0)) : Text(row);
    private static string ImageSource(IElement? image) => image?.GetAttribute("data-src") ?? image?.GetAttribute("src") ?? string.Empty;
    private static string Image(IElement? image, string baseUrl) => Absolute(ImageSource(image), baseUrl);
    private static string Absolute(string value, string baseUrl) => string.IsNullOrWhiteSpace(value) ? string.Empty : Uri.TryCreate(value, UriKind.Absolute, out _) ? value : new Uri(new Uri(baseUrl), value).ToString();
    private static string CloudId(CloudFile file) => CloudDriveService.GetPlaybackId(file);

    [GeneratedRegex(@"/vodtype/(\w+)\.html", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryRegex();
    [GeneratedRegex(@"/vod/type/id/(\w+)\.html", RegexOptions.IgnoreCase)]
    private static partial Regex SlashCategoryRegex();
    [GeneratedRegex(@"mac_total.*?text\('(\d+)'\)", RegexOptions.IgnoreCase)]
    private static partial Regex PageTotalRegex();
    [GeneratedRegex(@"-(\d+)-(?:[^-/]*-){2}[^-/]*\.html(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageLinkRegex();
    [GeneratedRegex(@"/vodshow/(?<category>\d+)-(?<area>[^/-]*)-(?<by>[^/-]*)-(?<class>[^/-]*)-[^/-]*-(?<letter>0-9|[^/-]*)-(?:[^/-]*-){5}(?<year>[^/-]*)\.html(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterLinkRegex();
}
