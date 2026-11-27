using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Universal auto-adapter for arbitrary Type-3 TVBox sites (csp_*) that supply plaintext HTTP/HTTPS URLs
/// in `ext` (string, comma/$$$-separated URLs, or `{ site/url/host: ... }`).
/// Automatically probes:
///   1. MacCMS JSON API (`/api.php/provide/vod`) if exposed by the host
///   2. MacCMS / Stui / MXPro / Panlian / MyTheme / generic HTML video & cloud-share pages
///   3. Direct cloud-share expansion (`CloudDriveService`), `player_aaaa` decoding, `<video>` extraction,
///      and WebView2 sniff fallback.
/// </summary>
public sealed partial class UniversalAutoHtmlVodProvider(SourceHttpClient http, CloudDriveService? cloud = null) : INativePlaybackProvider
{
    private sealed record SiteDiscovery(
        bool UsesMacCmsJson,
        string? MacCmsEndpoint,
        string? SearchActionTemplate,
        string? CategoryUrlPattern,
        Uri WorkingOrigin);

    private readonly ConcurrentDictionary<string, SiteDiscovery> _discoveries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".py", ".json", ".txt", ".m3u", ".m3u8", ".jar", ".zip", ".apk"
    };

    private static readonly HashSet<string> NonGenericApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "csp_AList", "csp_AListGuard", "csp_AListV3",
        "csp_Market", "csp_Local", "csp_FishDrive", "csp_FishConfig", "csp_Wallpaper",
        "csp_Push", "csp_PushAgent", "csp_Notice",
        "csp_T4", "csp_T4Guard",
        "csp_XPath", "csp_XPathFilter", "csp_XPathMac", "csp_XPathMacFilter",
        "csp_XYQHiker", "csp_XYQBiu"
    };

    public static bool Supports(SourceSite site)
    {
        if (site.Type != 3 || !site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)) return false;
        if (NonGenericApis.Contains(site.Api)) return false;
        var origins = GetUsableOrigins(site);
        return origins.Count > 0;
    }

    public bool CanHandle(SourceSite site) => Supports(site);

    private static IReadOnlyList<Uri> GetUsableOrigins(SourceSite site)
    {
        var rawCandidates = new List<string>();
        if (site.Ext.ValueKind == JsonValueKind.String)
        {
            var raw = site.Ext.GetString()?.Trim() ?? string.Empty;
            if (raw.Length == 0) return [];
            rawCandidates.AddRange(raw.Split(["$$$", ",", ";", "|"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (site.Ext.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in new[] { "site", "siteUrl", "url", "host", "sites", "urls" })
            {
                if (!site.Ext.TryGetProperty(prop, out var el)) continue;
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                    rawCandidates.Add(s.Trim());
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } a)
                            rawCandidates.Add(a.Trim());
                }
            }
        }

        var origins = new List<Uri>();
        foreach (var cand in rawCandidates)
        {
            if (!Uri.TryCreate(cand, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                continue;
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(ext) && ExcludedExtensions.Contains(ext))
                continue;
            var root = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
            if (!origins.Any(u => u.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase) && u.Port == root.Port))
                origins.Add(root);
        }
        return origins;
    }

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var origins = GetUsableOrigins(site);
        if (origins.Count == 0) throw new InvalidDataException("站点未配置可访问的地址。");

        // Stage 1: fast MacCMS JSON API probe on first origin if already discovered or quick-check
        if (_discoveries.TryGetValue(site.Key, out var cached) && cached.UsesMacCmsJson && cached.MacCmsEndpoint is not null)
            return await FetchMacCmsJsonHomeAsync(site, cached.MacCmsEndpoint, cancellationToken).ConfigureAwait(false);

        var (doc, pageUri) = await NativeSiteToolkit.FetchHtmlAsync(
            http, site, origins, "/", site.Name, cancellationToken, minHtmlLength: 250).ConfigureAwait(false);

        var categories = ExtractCategories(doc, pageUri, out var catPattern);
        var items = ExtractCards(doc, pageUri, site);
        var searchTemplate = DiscoverSearchTemplate(doc, pageUri);

        // If HTML had no parseable video cards, probe MacCMS JSON endpoint as fallback
        if (items.Count == 0)
        {
            var apiUrl = new Uri(new Uri(pageUri.GetLeftPart(UriPartial.Authority) + "/"), "/api.php/provide/vod/").AbsoluteUri;
            try
            {
                var jsonResp = await FetchMacCmsJsonHomeAsync(site, apiUrl, cancellationToken).ConfigureAwait(false);
                if (jsonResp.List.Count > 0 || jsonResp.Categories.Count > 0)
                {
                    _discoveries[site.Key] = new SiteDiscovery(true, apiUrl, null, null, new Uri(pageUri.GetLeftPart(UriPartial.Authority) + "/"));
                    return jsonResp;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        _discoveries[site.Key] = new SiteDiscovery(
            UsesMacCmsJson: false,
            MacCmsEndpoint: null,
            SearchActionTemplate: searchTemplate,
            CategoryUrlPattern: catPattern,
            WorkingOrigin: new Uri(pageUri.GetLeftPart(UriPartial.Authority) + "/"));

        return new CmsResponse
        {
            HomeCategoryName = "最新推荐",
            Categories = categories,
            List = items,
            Page = 1,
            PageCount = 1,
            Total = items.Count
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        if (_discoveries.TryGetValue(site.Key, out var disc) && disc.UsesMacCmsJson && disc.MacCmsEndpoint is not null)
        {
            var raw = await http.GetStringAsync($"{disc.MacCmsEndpoint}?ac=detail&t={Uri.EscapeDataString(categoryId)}&pg={page}",
                NativeSiteToolkit.CreateBrowserHeaders(disc.WorkingOrigin, site.GetHeaders()), cancellationToken).ConfigureAwait(false);
            return ParseMacCmsJson(raw, site, page);
        }

        var origins = OrderedOrigins(site);
        var path = BuildCategoryPath(categoryId, page, disc?.CategoryUrlPattern);
        var (doc, pageUri) = await NativeSiteToolkit.FetchHtmlAsync(
            http, site, origins, path, site.Name, cancellationToken, minHtmlLength: 250).ConfigureAwait(false);
        var items = ExtractCards(doc, pageUri, site);
        return new CmsResponse
        {
            List = items,
            Page = page,
            PageCount = items.Count >= 8 ? page + 1 : page,
            Total = items.Count
        };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);

        if (_discoveries.TryGetValue(site.Key, out var disc) && disc.UsesMacCmsJson && disc.MacCmsEndpoint is not null)
        {
            var raw = await http.GetStringAsync($"{disc.MacCmsEndpoint}?ac=detail&wd={Uri.EscapeDataString(keyword)}&pg={page}",
                NativeSiteToolkit.CreateBrowserHeaders(disc.WorkingOrigin, site.GetHeaders()), cancellationToken).ConfigureAwait(false);
            return ParseMacCmsJson(raw, site, page);
        }

        var origins = OrderedOrigins(site);
        var encoded = Uri.EscapeDataString(keyword);
        var candidatePaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(disc?.SearchActionTemplate))
            candidatePaths.Add(disc.SearchActionTemplate.Replace("{wd}", encoded, StringComparison.Ordinal).Replace("{pg}", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        candidatePaths.Add($"/vodsearch/-------------/?wd={encoded}&page={page}");
        candidatePaths.Add($"/index.php/vod/search/page/{page}/wd/{encoded}.html");
        candidatePaths.Add($"/vodsearch/{encoded}----------{page}---.html");
        candidatePaths.Add($"/search/{encoded}----------{page}---.html");
        candidatePaths.Add($"/?s={encoded}&page={page}");

        Exception? lastErr = null;
        foreach (var path in candidatePaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var (doc, pageUri) = await NativeSiteToolkit.FetchHtmlAsync(
                    http, site, origins, path, site.Name, cancellationToken, minHtmlLength: 250).ConfigureAwait(false);
                var items = ExtractCards(doc, pageUri, site);
                if (items.Count > 0)
                    return new CmsResponse { List = items, Page = page, PageCount = items.Count >= 8 ? page + 1 : page, Total = items.Count };
            }
            catch (SiteVerificationRequiredException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException) { lastErr = ex; }
        }
        if (lastErr is not null) Diagnostics.Log($"[AutoHtml:{site.Key}] search fallback empty: {lastErr.Message}");
        return new CmsResponse { Page = page, PageCount = 1 };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        if (_discoveries.TryGetValue(site.Key, out var disc) && disc.UsesMacCmsJson && disc.MacCmsEndpoint is not null)
        {
            var raw = await http.GetStringAsync($"{disc.MacCmsEndpoint}?ac=detail&ids={Uri.EscapeDataString(id)}",
                NativeSiteToolkit.CreateBrowserHeaders(disc.WorkingOrigin, site.GetHeaders()), cancellationToken).ConfigureAwait(false);
            var parsed = ParseMacCmsJson(raw, site, 1);
            return parsed.List.FirstOrDefault() ?? throw new InvalidDataException("站点未返回视频详情。");
        }

        var origins = OrderedOrigins(site);
        var (doc, pageUri) = await NativeSiteToolkit.FetchHtmlAsync(
            http, site, origins, id, site.Name, cancellationToken, minHtmlLength: 250).ConfigureAwait(false);

        var title = NativeSiteToolkit.FirstNonEmpty(
            doc.QuerySelector("h1.premium-title, h1.page-title, h1.title, .stui-content__detail h1, .video-info-header h1, h1")?.TextContent,
            doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content"),
            doc.Title?.Split(['-', '_', '|'], 2)[0]);
        var pic = NativeSiteToolkit.ExtractImageUrl(
            doc.QuerySelector(".premium-poster img, .stui-content__thumb img, .module-item-pic img, .detail-poster img, .poster img, .lazyload"), pageUri);
        if (pic.Length == 0)
            pic = NativeSiteToolkit.ResolveUrl(pageUri, doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content"));
        var desc = NativeSiteToolkit.FirstNonEmpty(
            NativeSiteToolkit.CollapseSpaces(doc.QuerySelector("#detailDescText, .detail-desc-text, .detail-content, .stui-content__desc, .video-info-content, .intro, .summary")?.TextContent),
            doc.QuerySelector("meta[name='description']")?.GetAttribute("content"));

        var (lineNames, lineUrls) = NativeSiteToolkit.ExtractStuiPlaylists(
            doc, pageUri,
            ".stui-pannel__head h3, .stui-vodlist__head h3, .nav-tabs li a, .module-tab-item, .play-tabs a, .playlist-tab a",
            ".stui-content__playlist, .module-play-list-content, ul.play-list, .playlist ul, .plist");

        // Generic fallback: direct play links on detail page
        if (lineNames.Count == 0)
        {
            var eps = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in doc.QuerySelectorAll("a[href*='/vodplay/'], a[href*='/play/'], a[href*='/vod/play/'], a[href*='/vplay/'], a[href$='.m3u8'], a[href$='.mp4'], a[href$='.mp3']"))
            {
                var href = NativeSiteToolkit.ResolveUrl(pageUri, a.GetAttribute("href"));
                var label = NativeSiteToolkit.SanitizeLabel(a.TextContent);
                if (href.Length > 0 && label.Length > 0 && seen.Add(href))
                    eps.Add($"{label}${href}");
            }
            if (eps.Count > 0)
            {
                lineNames.Add("在线播放");
                lineUrls.Add(string.Join('#', eps));
            }
        }

        if (cloud is not null)
            await NativeSiteToolkit.AppendCloudShareLinesAsync(cloud, doc, lineNames, lineUrls, cancellationToken).ConfigureAwait(false);

        if (lineNames.Count == 0 && doc.QuerySelector(".pan-lock-box") is not null)
        {
            desc = (string.IsNullOrWhiteSpace(desc) ? string.Empty : desc + "\n\n") +
                   "提示：原站网盘链接需登录后查看，可点击「查找换源」搜索其它可直连播放源。";
        }

        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title.Length > 0 ? title : id,
            Picture = pic,
            Content = PlaybackParsing.StripHtml(desc),
            PlayFrom = string.Join("$$$", lineNames),
            PlayUrl = string.Join("$$$", lineUrls)
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        if (cloud is not null && await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, id, token).ConfigureAwait(false) is { } cloudDirect)
            return cloudDirect;

        var origins = OrderedOrigins(site);
        var target = id.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || id.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? id
            : (origins.Count > 0 ? new Uri(origins[0], id).AbsoluteUri : id);
        var headers = NativeSiteToolkit.CreateBrowserHeaders(origins.FirstOrDefault(), site.GetHeaders());

        if (PlayUrlResolver.IsDirectMediaUrl(target))
            return new SpiderPlayResult(target, headers, 0, 0);

        try
        {
            var exchange = await http.GetExchangeAsync(target, headers, maxBytes: 2 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
            var html = SourceHttpClient.DecodeText(exchange.Payload.Content);
            headers["Referer"] = exchange.Payload.FinalUri.AbsoluteUri;

            if (NativeSiteToolkit.TryParsePlayerAaaa(html, out var player) && player.DecodedUrl.Length > 0)
            {
                if (cloud is not null && await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, player.DecodedUrl, token).ConfigureAwait(false) is { } innerCloud)
                    return innerCloud;
                if (player.DecodedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && PlayUrlResolver.IsDirectMediaUrl(player.DecodedUrl))
                    return new SpiderPlayResult(player.DecodedUrl, headers, 0, 0);
            }

            var directMatch = DirectStreamRegex().Match(html);
            if (directMatch.Success)
            {
                var found = directMatch.Value.Replace("\\/", "/", StringComparison.Ordinal);
                if (Uri.TryCreate(found, UriKind.Absolute, out _))
                    return new SpiderPlayResult(found, headers, 0, 0);
            }
        }
        catch (CloudAuthenticationException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        return new SpiderPlayResult(target, headers, 1, 0);
    }

    private IReadOnlyList<Uri> OrderedOrigins(SourceSite site)
    {
        var list = GetUsableOrigins(site).ToList();
        if (_discoveries.TryGetValue(site.Key, out var disc))
        {
            list.RemoveAll(u => u.Host.Equals(disc.WorkingOrigin.Host, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, disc.WorkingOrigin);
        }
        return list;
    }

    private async Task<CmsResponse> FetchMacCmsJsonHomeAsync(SourceSite site, string apiUrl, CancellationToken token)
    {
        var headers = NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var raw = await http.GetStringAsync(apiUrl + "?ac=list", headers, cts.Token).ConfigureAwait(false);
        var resp = ParseMacCmsJson(raw, site, 1);
        if (resp.List.Count == 0 && resp.Categories.Count > 0)
        {
            var detailRaw = await http.GetStringAsync(apiUrl + "?ac=detail&pg=1", headers, cts.Token).ConfigureAwait(false);
            var detailResp = ParseMacCmsJson(detailRaw, site, 1);
            resp.List = detailResp.List;
        }
        return resp;
    }

    private static CmsResponse ParseMacCmsJson(string raw, SourceSite site, int page)
    {
        var resp = JsonSerializer.Deserialize<CmsResponse>(raw.TrimStart('\uFEFF'), JsonUtil.Options) ?? new CmsResponse();
        resp.Categories ??= [];
        resp.List ??= [];
        resp.Categories.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name) || c.Name.Contains("伦理", StringComparison.Ordinal));
        resp.List.RemoveAll(v => v is null || string.IsNullOrWhiteSpace(v.Id));
        foreach (var item in resp.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Content = PlaybackParsing.StripHtml(item.Content);
        }
        resp.Page = page;
        resp.PageCount = Math.Max(resp.PageCount, resp.List.Count > 0 ? page + 1 : page);
        return resp;
    }

    private static List<VodCategory> ExtractCategories(IDocument doc, Uri pageUri, out string? pattern)
    {
        pattern = null;
        var categories = new List<VodCategory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in doc.QuerySelectorAll("nav a[href], header a[href], .nav-links a[href], .nav-link[href], .stui-header__menu a[href], .top-nav a[href]"))
        {
            var rawHref = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var name = NativeSiteToolkit.CollapseSpaces(a.TextContent);
            if (rawHref.Length == 0 || name.Length is 0 or > 14) continue;
            if (name is "首页" or "主页" or "登录" or "注册" or "求片" or "留言" or "留言板" or "APP" or "下载" or "更多" or "更多↓" ||
                name.Contains("伦理", StringComparison.Ordinal) || name.Contains("福利", StringComparison.Ordinal))
                continue;

            var abs = NativeSiteToolkit.ResolveUrl(pageUri, rawHref);
            if (abs.Length == 0 || !Uri.TryCreate(abs, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            var path = uri.PathAndQuery;
            if (path is "/" or "" || path.Contains("/user/", StringComparison.OrdinalIgnoreCase) || path.Contains("/gbook", StringComparison.OrdinalIgnoreCase))
                continue;

            if (path.Contains("/vodtype/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/vod/type/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/type/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/show/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/category/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/list/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/categories/", StringComparison.OrdinalIgnoreCase) ||
                (uri.AbsolutePath.Count(c => c == '/') == 2 && uri.AbsolutePath.EndsWith('/')))
            {
                if (seen.Add(path))
                {
                    categories.Add(new VodCategory { Id = path, Name = name });
                    pattern ??= path;
                }
            }
        }
        return categories;
    }

    private static string BuildCategoryPath(string categoryId, int page, string? _)
    {
        if (page <= 1) return categoryId;
        var clean = categoryId.Trim();
        if (clean.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var stem = clean[..^5];
            if (stem.Contains("/vod/type/id/", StringComparison.OrdinalIgnoreCase))
                return $"{stem}/page/{page}.html";
            if (stem.Contains("/vodtype/", StringComparison.OrdinalIgnoreCase))
                return $"{stem}-{page}.html";
            if (stem.Contains("/type/", StringComparison.OrdinalIgnoreCase))
                return $"{stem}-{page}.html";
            return $"{stem}-{page}.html";
        }
        if (clean.Contains('?')) return $"{clean}&page={page}";
        if (clean.EndsWith('/')) return $"{clean}page/{page}/";
        return $"{clean}?page={page}";
    }

    private static string? DiscoverSearchTemplate(IDocument doc, Uri pageUri)
    {
        foreach (var form in doc.QuerySelectorAll("form"))
        {
            var input = form.QuerySelector("input[name='wd'], input[name='q'], input[name='s'], input[name='keyword'], input[name='searchword'], input[type='search'], input[type='text']");
            var inputName = input?.GetAttribute("name")?.Trim();
            var action = form.GetAttribute("action")?.Trim();
            if (string.IsNullOrWhiteSpace(inputName) || string.IsNullOrWhiteSpace(action)) continue;
            var resolved = NativeSiteToolkit.ResolveUrl(pageUri, action);
            if (!Uri.TryCreate(resolved, UriKind.Absolute, out var actionUri)) continue;
            var sep = actionUri.PathAndQuery.Contains('?') ? "&" : "?";
            return $"{actionUri.PathAndQuery}{sep}{Uri.EscapeDataString(inputName)}={{wd}}";
        }
        return null;
    }

    private static List<VodItem> ExtractCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(IElement card, IElement? link)
        {
            if (link is null) return;
            var href = link.GetAttribute("href")?.Trim() ?? string.Empty;
            if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href == "#") return;
            var resolved = NativeSiteToolkit.ResolveUrl(pageUri, href);
            if (!Uri.TryCreate(resolved, UriKind.Absolute, out var uri)) return;
            if (!uri.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase)) return;
            var path = uri.PathAndQuery;
            if (path is "/" or "" || path.Contains("/user/", StringComparison.OrdinalIgnoreCase) || path.Contains("/login", StringComparison.OrdinalIgnoreCase))
                return;

            var name = NativeSiteToolkit.FirstNonEmpty(
                card.QuerySelector(".video-title, .video-name, .module-item-title, .title, h3, h4, h2")?.TextContent,
                link.GetAttribute("title"),
                card.QuerySelector("img")?.GetAttribute("alt"),
                NativeSiteToolkit.CollapseSpaces(link.TextContent));
            if (name.Length is 0 or > 120 || !seen.Add(path)) return;

            var pic = NativeSiteToolkit.ExtractImageUrl(card.QuerySelector("img") ?? link, pageUri);
            var remark = NativeSiteToolkit.FirstNonEmpty(
                card.QuerySelector(".video-episode, .module-item-note, .module-item-text, .pic-text, .remarks, .badge, .tag")?.TextContent,
                card.QuerySelector(".video-score, .score")?.TextContent);

            items.Add(new VodItem
            {
                Id = path,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Picture = pic,
                Remarks = NativeSiteToolkit.CollapseSpaces(remark)
            });
        }

        // Priority 1: Panlian / Modern card grids (e.g. wn4k.com .video-card)
        foreach (var card in doc.QuerySelectorAll("a.video-card[href], .video-card"))
            TryAdd(card, card.TagName.Equals("A", StringComparison.OrdinalIgnoreCase) ? card : card.QuerySelector("a[href]"));

        // Priority 2: MXPro / Wogg style .module-item / .module-card-item
        if (items.Count == 0)
        {
            foreach (var card in doc.QuerySelectorAll(".module-item, .module-card-item, .module-search-item"))
                TryAdd(card, card.QuerySelector("a[href*='/vod'], a[href*='/detail'], a[href]"));
        }

        // Priority 3: Stui / MyTheme / DYXS cards
        if (items.Count == 0)
        {
            foreach (var card in doc.QuerySelectorAll(".stui-vodlist__box, .myui-vodlist__box, .fed-list-item, .vodlist_item, .movie-item, .thread-item"))
                TryAdd(card, card.QuerySelector("a.stui-vodlist__thumb[href], a[href*='/detail'], a[href*='/vod'], a[href]"));
        }

        // Priority 4: Semantic detail links anywhere on page
        if (items.Count == 0)
        {
            foreach (var a in doc.QuerySelectorAll("a[href*='/voddetail/'], a[href*='/vod/detail/'], a[href*='/detail/'], a[href*='/movie/'], a[href*='/movies/'], a[href*='/video/'], a[href*='/thread-']"))
                TryAdd(a.Closest("li, article, .card, .item, dl") ?? a, a);
        }

        return items;
    }

    [GeneratedRegex(@"https?:\\?/\\?/[^\s""'<>\\]+?\.(?:m3u8|mp4|mp3|m4a|flv)(?:\?[^\s""'<>\\]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectStreamRegex();
}
