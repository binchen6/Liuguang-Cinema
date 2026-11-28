using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Playback;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

internal sealed class SiteAdapterMismatchException(string message) : IOException(message);

/// <summary>Discovers public CMS endpoints and recognizable video pages for a configured TVBox site.</summary>
public sealed partial class UniversalAutoHtmlVodProvider(SourceHttpClient http, CloudDriveService? cloud = null) : INativePlaybackProvider
{
    private sealed record SiteAddress(Uri Entry, Uri Base, bool IsCmsEndpoint);
    private sealed record ConfiguredAddresses(JsonElement Ext, SiteAddress[] Entries);
    private sealed record SearchForm(Uri Action, string Field);
    private sealed record SiteDiscovery(Uri Base, Uri HomeUri, Uri? CmsEndpoint, SearchForm? Search, CmsResponse Home)
    {
        internal long ExpiresAt { get; } = Environment.TickCount64 + 120_000;
        internal ConcurrentDictionary<(string Query, int Page), Uri> NextPages { get; } = new();
    }

    private sealed class DiscoveryState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal volatile SiteDiscovery? Current;
    }

    private readonly ConditionalWeakTable<SourceSite, DiscoveryState> _discoveries = new();
    private static readonly ConditionalWeakTable<SourceSite, ConfiguredAddresses> Addresses = new();

    private static readonly HashSet<string> AddressFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "site", "sites", "siteUrl", "host", "hosts", "url", "urls", "baseUrl", "homeUrl", "endpoint", "api"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".py", ".txt", ".m3u", ".m3u8", ".jar", ".zip", ".apk", ".mp4", ".mp3", ".webm"
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

    public static bool Supports(SourceSite site) =>
        site.Type == 3 && site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) &&
        !NonGenericApis.Contains(site.Api) && GetAddresses(site).Length != 0;

    public bool CanHandle(SourceSite site) => Supports(site);

    private static SiteAddress[] GetAddresses(SourceSite site)
    {
        var ext = site.Ext;
        if (Addresses.TryGetValue(site, out var cached) && cached.Ext.Equals(ext)) return cached.Entries;
        var entries = GetAddressCandidates(ext).Take(8).ToArray();
        Addresses.AddOrUpdate(site, new ConfiguredAddresses(ext, entries));
        return entries;
    }

    private static IEnumerable<SiteAddress> GetAddressCandidates(JsonElement ext)
    {
        var seen = new HashSet<Uri>();
        foreach (var uri in ReadAddresses(ext, allowString: true, depth: 0))
        {
            if (!seen.Add(uri)) continue;
            var path = uri.AbsolutePath;
            var cms = path.Contains("/provide/vod", StringComparison.OrdinalIgnoreCase) ||
                      path.TrimEnd('/').EndsWith("/api/vod", StringComparison.OrdinalIgnoreCase);
            var extension = Path.GetExtension(path);
            if (ExcludedExtensions.Contains(extension) ||
                !cms && (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))) continue;
            var marker = cms ? path.IndexOf("/api", StringComparison.OrdinalIgnoreCase) : -1;
            var basePath = marker >= 0 ? path[..(marker + 1)] : GetBasePath(path);
            var siteBase = new UriBuilder(uri) { Path = basePath, Query = "", Fragment = "" }.Uri;
            yield return new SiteAddress(uri, siteBase, cms);
        }
    }

    private static string GetBasePath(string path) =>
        path.EndsWith('/') ? path : Path.HasExtension(path) ? path[..(path.LastIndexOf('/') + 1)] : path + "/";

    private static IEnumerable<Uri> ReadAddresses(JsonElement value, bool allowString, int depth)
    {
        if (depth >= 5) yield break;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var fieldMatch = AddressFields.Contains(property.Name);
                if (!fieldMatch && !property.Name.Equals("config", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var uri in ReadAddresses(property.Value, fieldMatch, depth + 1)) yield return uri;
            }
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array && allowString)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var uri in ReadAddresses(item, allowString: true, depth + 1)) yield return uri;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.String || !allowString) yield break;
        var text = value.GetString()?.Trim() ?? "";
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            using var nested = ReadEmbeddedConfig(text);
            if (nested is not null)
                foreach (var uri in ReadAddresses(nested.RootElement, allowString: true, depth + 1)) yield return uri;
            yield break;
        }

        foreach (var address in AddressSeparator().Split(text))
            if (Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && MediaRequestHeaders.IsHttpAddress(uri))
                yield return new UriBuilder(uri) { Fragment = "" }.Uri;
    }

    private static JsonDocument? ReadEmbeddedConfig(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        (await DiscoverAsync(site, cancellationToken).ConfigureAwait(false)).Home;

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        return WithDiscoveryAsync(site, async (route, token) =>
        {
            if (route.CmsEndpoint is { } cms)
                return await ReadCmsAsync(site, cms, route.HomeUri, page, token, ("ac", "detail"), ("t", categoryId),
                    ("pg", PageNumber(page))).ConfigureAwait(false);

            var key = ("category:" + categoryId, page);
            if (!route.NextPages.TryGetValue(key, out var target))
            {
                var category = ResolveAddress(route.HomeUri, categoryId, sameOrigin: true)
                    ?? throw new InvalidDataException("分类地址与站点不匹配。");
                target = page == 1 ? category : WithQuery(category, ("page", PageNumber(page)));
            }
            return await ReadListingAsync(site, route, target, key, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        page = Math.Max(1, page);
        if (keyword.Length == 0) return Task.FromResult(new CmsResponse { Page = page, PageCount = page });
        return WithDiscoveryAsync(site, async (route, token) =>
        {
            if (route.CmsEndpoint is { } cms)
                return await ReadCmsAsync(site, cms, route.HomeUri, page, token, ("ac", "detail"), ("wd", keyword),
                    ("pg", PageNumber(page))).ConfigureAwait(false);

            var key = ("search:" + keyword, page);
            if (route.NextPages.TryGetValue(key, out var next))
                return await ReadListingAsync(site, route, next, key, token).ConfigureAwait(false);
            if (route.Search is { } search)
            {
                var target = page == 1 ? WithQuery(search.Action, (search.Field, keyword))
                    : WithQuery(search.Action, (search.Field, keyword), ("page", PageNumber(page)));
                return await ReadListingAsync(site, route, target, key, token).ConfigureAwait(false);
            }

            var encoded = Uri.EscapeDataString(keyword);
            foreach (var path in new[] { $"vodsearch/{encoded}----------{page}---.html",
                $"index.php/vod/search/page/{page}/wd/{encoded}.html", $"vodsearch/-------------/?wd={encoded}&page={page}" })
            {
                try
                {
                    var response = await ReadListingAsync(site, route, new Uri(route.Base, path), key, token).ConfigureAwait(false);
                    if (response.List.Count > 0 && !response.List.Select(item => item.Id).SequenceEqual(route.Home.List.Select(item => item.Id)))
                        return response;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
            }
            return new CmsResponse { Page = page, PageCount = page };
        }, cancellationToken);
    }

    private async Task<CmsResponse> ReadListingAsync(SourceSite site, SiteDiscovery route, Uri target,
        (string Query, int Page) key, CancellationToken token)
    {
        var (doc, uri) = await ReadPageAsync(site, target, route.HomeUri, token).ConfigureAwait(false);
        var cards = ExtractCards(doc, uri, site);
        var next = cards.Count == 0 ? null : FindNextPage(doc, uri);
        if (next is not null)
        {
            if (route.NextPages.Count >= 128) route.NextPages.Clear();
            route.NextPages[(key.Query, key.Page + 1)] = next;
        }
        return new CmsResponse { List = cards, Page = key.Page, PageCount = key.Page + (next is null ? 0 : 1), Total = cards.Count };
    }

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default) =>
        WithDiscoveryAsync(site, async (route, token) =>
        {
            // HTML identifiers are paths. CMS identifiers are opaque.
            if (route.CmsEndpoint is { } cms && !id.StartsWith('/') &&
                !(Uri.TryCreate(id, UriKind.Absolute, out var absolute) && MediaRequestHeaders.IsHttpAddress(absolute)))
            {
                var response = await ReadCmsAsync(site, cms, route.HomeUri, 1, token, ("ac", "detail"), ("ids", id)).ConfigureAwait(false);
                return response.List.FirstOrDefault() ?? throw new InvalidDataException("站点未返回视频详情。");
            }

            var target = ResolveAddress(route.HomeUri, id, sameOrigin: true)
                ?? throw new InvalidDataException("视频详情地址与站点不匹配。");
            var (doc, pageUri) = await ReadPageAsync(site, target, route.HomeUri, token).ConfigureAwait(false);
            var title = NativeSiteToolkit.FirstNonEmpty(
                doc.QuerySelector("h1.premium-title, h1.page-title, h1.title, h1")?.TextContent,
                doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content"),
                doc.Title?.Split(['-', '_', '|'], 2)[0]);
            var pic = NativeSiteToolkit.FirstNonEmpty(
                NativeSiteToolkit.ExtractImageUrl(doc.QuerySelector(".premium-poster img, .stui-content__thumb img, .module-item-pic img, .detail-poster img, .poster img"), pageUri),
                NativeSiteToolkit.ResolveUrl(pageUri, doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content")));
            var desc = NativeSiteToolkit.FirstNonEmpty(
                NativeSiteToolkit.CollapseSpaces(doc.QuerySelector("#detailDescText, .detail-desc-text, .detail-content, .stui-content__desc, .video-info-content, .intro, .summary")?.TextContent),
                doc.QuerySelector("meta[name='description']")?.GetAttribute("content"));
            var (lineNames, lineUrls) = NativeSiteToolkit.ExtractStuiPlaylists(doc, pageUri,
                ".stui-pannel__head h3, .stui-vodlist__head h3, .nav-tabs li a, .module-tab-item, .play-tabs a, .playlist-tab a",
                ".stui-content__playlist, .module-play-list-content, ul.play-list, .playlist ul, .plist");

            if (lineNames.Count == 0)
            {
                var episodes = new List<string>();
                var seen = new HashSet<Uri>();
                foreach (var link in doc.QuerySelectorAll("main a[href], article a[href], a[href*='/vodplay/'], a[href*='/vplay/'], a[href*='/play/'], [itemprop='episode'] a[href]"))
                {
                    var candidate = ResolveAddress(pageUri, link.GetAttribute("href"), sameOrigin: false);
                    if (candidate is null || (!PlayUrlResolver.IsDirectMediaUrl(candidate.AbsoluteUri) &&
                        !(MediaRequestHeaders.SameOrigin(pageUri, candidate) && PlayerPath().IsMatch(candidate.AbsolutePath)))) continue;
                    var label = NativeSiteToolkit.SanitizeLabel(link.TextContent);
                    if (label.Length > 0 && seen.Add(candidate)) episodes.Add(string.Concat(label, "$", candidate.AbsoluteUri));
                    if (episodes.Count >= 500) break;
                }

                if (episodes.Count == 0 && FindMediaElement(doc, pageUri) is not null)
                    episodes.Add("正片$" + pageUri.AbsoluteUri);
                if (episodes.Count > 0)
                {
                    lineNames.Add("在线播放");
                    lineUrls.Add(string.Join('#', episodes));
                }
            }

            var magnets = doc.QuerySelectorAll("a[href^='magnet:' i]")
                .Select(element => (Url: element.GetAttribute("href")?.Trim(), Label: NativeSiteToolkit.SanitizeLabel(element.TextContent)))
                .Where(candidate => MagnetPlayback.TryParse(candidate.Url, out _)).DistinctBy(candidate => candidate.Url).Take(50)
                .Select((candidate, index) => $"{(candidate.Label.Length > 0 ? candidate.Label.Replace('$', '＄').Replace('#', '＃') : $"磁力资源 {index + 1}")}${candidate.Url}").ToArray();
            if (magnets.Length > 0)
            {
                lineNames.Add("磁力资源");
                lineUrls.Add(string.Join('#', magnets));
            }
            if (cloud is not null)
                await NativeSiteToolkit.AppendCloudShareLinesAsync(cloud, doc, lineNames, lineUrls, token).ConfigureAwait(false);

            if (lineNames.Count == 0 && doc.QuerySelector(".pan-lock-box") is not null)
                desc += "\n\n提示：原站网盘链接需要登录后查看。";

            return new VodItem
            {
                Id = id, SiteKey = site.Key, SiteName = site.Name,
                Name = NativeSiteToolkit.FirstNonEmpty(title, id), Picture = pic, Content = PlaybackParsing.StripHtml(desc),
                PlayFrom = string.Join("$$$", lineNames), PlayUrl = string.Join("$$$", lineUrls)
            };
        }, cancellationToken);

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (MagnetPlayback.TryParse(id, out _)) return new SpiderPlayResult(id, new Dictionary<string, string>(), 0, 0);
        if (cloud is not null && await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, id, token).ConfigureAwait(false) is { } cloudDirect)
            return cloudDirect;

        var configured = GetAddresses(site).FirstOrDefault()
            ?? throw new InvalidDataException("站点未配置可访问的地址。");
        var route = _discoveries.TryGetValue(site, out var state) ? state.Current : null;
        var referer = route?.HomeUri ?? configured.Entry;
        var target = ResolveAddress(route?.Base ?? configured.Base, id, sameOrigin: false)
            ?? throw new InvalidDataException("播放地址无效。");
        if (PlayUrlResolver.IsDirectMediaUrl(target.AbsoluteUri))
            return MediaResult(site, referer, target, parse: 0);

        try
        {
            var (body, pageUri, contentType) = await ReadAsync(site, target, referer, token, skipPlaybackBody: true).ConfigureAwait(false);
            if (IsPlaybackContentType(contentType) || HlsManifest.IsPlaylist(pageUri.AbsoluteUri, contentType) ||
                DashManifest.IsPlaylist(pageUri.AbsoluteUri, contentType))
                return MediaResult(site, referer, pageUri, parse: 0, contentType);

            if (NativeSiteToolkit.TryParsePlayerAaaa(body, out var player) && MagnetPlayback.TryParse(player.DecodedUrl, out _))
                return new SpiderPlayResult(player.DecodedUrl, new Dictionary<string, string>(), 0, 0);
            if (player is not null &&
                ResolveAddress(pageUri, player.DecodedUrl, sameOrigin: false) is { } playerUrl)
            {
                if (cloud is not null && await NativeSiteToolkit.TryResolveDirectCloudShareAsync(cloud, playerUrl.AbsoluteUri, token).ConfigureAwait(false) is { } innerCloud)
                    return innerCloud;
                if (PlayUrlResolver.IsDirectMediaUrl(playerUrl.AbsoluteUri))
                    return MediaResult(site, pageUri, playerUrl, parse: 0);
            }

            var document = await new HtmlParser().ParseDocumentAsync(body, token).ConfigureAwait(false);
            if (FindMediaElement(document, pageUri) is { } htmlMedia)
                return MediaResult(site, pageUri, htmlMedia.Address, parse: 0, htmlMedia.ContentType);
            var match = DirectStreamPattern().Match(body);
            if (match.Success && ResolveAddress(pageUri, match.Value.Replace("\\/", "/", StringComparison.Ordinal), sameOrigin: false) is { } stream)
                return MediaResult(site, pageUri, stream, parse: 0);

            return MediaResult(site, referer, pageUri, parse: 1);
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException { StatusCode: null })
        {
            return MediaResult(site, referer, target, parse: 1);
        }
    }

    private static SpiderPlayResult MediaResult(SourceSite site, Uri referer, Uri media, int parse, string? contentType = null)
    {
        var origin = GetAddresses(site).FirstOrDefault()?.Entry ?? referer;
        var headers = NativeSiteToolkit.CreateBrowserHeaders(referer, MediaRequestHeaders.ForTarget(site.GetHeaders(), origin, media));
        return new SpiderPlayResult(media.AbsoluteUri, headers, parse, 0) { ReplaceHeaders = true, ContentType = contentType };
    }

    private async Task<T> WithDiscoveryAsync<T>(SourceSite site, Func<SiteDiscovery, CancellationToken, Task<T>> action, CancellationToken token)
    {
        var route = await DiscoverAsync(site, token).ConfigureAwait(false);
        try { return await action(route, token).ConfigureAwait(false); }
        catch (Exception ex) when (!token.IsCancellationRequested && IsStaleRoute(ex))
        {
            var refreshed = await DiscoverAsync(site, token, route).ConfigureAwait(false);
            if (refreshed.CmsEndpoint == route.CmsEndpoint && refreshed.HomeUri == route.HomeUri) throw;
            return await action(refreshed, token).ConfigureAwait(false);
        }
    }

    private static bool IsStaleRoute(Exception error) => error is TimeoutException or SiteAdapterMismatchException ||
        error is HttpRequestException { StatusCode: null or HttpStatusCode.NotFound or HttpStatusCode.Gone or
            HttpStatusCode.PreconditionFailed or >= HttpStatusCode.InternalServerError };

    private async Task<SiteDiscovery> DiscoverAsync(SourceSite site, CancellationToken token, SiteDiscovery? stale = null)
    {
        token.ThrowIfCancellationRequested();
        var state = _discoveries.GetValue(site, static _ => new DiscoveryState());
        if (state.Current is { } quick && !ReferenceEquals(quick, stale) && Environment.TickCount64 < quick.ExpiresAt) return quick;
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (state.Current is { } current && !ReferenceEquals(current, stale) && Environment.TickCount64 < current.ExpiresAt) return current;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 7, 16)));
            try
            {
                state.Current = await DetectSiteAsync(site, timeout.Token).ConfigureAwait(false);
                return state.Current;
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("自动适配探测超时，请检查网络或站点地址。", ex);
            }
        }
        finally { state.Gate.Release(); }
    }

    private async Task<SiteDiscovery> DetectSiteAsync(SourceSite site, CancellationToken token)
    {
        Exception? lastError = null;
        HttpRequestException? preconditionFailure = null;
        SiteVerificationRequiredException? challenge = null;
        var reachedSite = false;
        var candidates = GetAddresses(site);
        if (candidates.Length == 0) throw new SiteAdapterMismatchException("站点未提供可探测的公开地址。");
        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.IsCmsEndpoint)
                {
                    if (await TryCmsHomeAsync(site, candidate.Entry, candidate.Base, candidate.Base, token).ConfigureAwait(false) is { } explicitCms)
                        return explicitCms;
                    reachedSite = true;
                    continue;
                }

                Uri probeBase = candidate.Base;
                try
                {
                    var (text, pageUri, _) = await ReadAsync(site, candidate.Entry, candidate.Base, token).ConfigureAwait(false);
                    reachedSite = true;
                    if (TryParseCms(text, site, pageUri, 1) is { } embedded && (embedded.List.Count > 0 || embedded.Categories.Count > 0))
                        return new SiteDiscovery(candidate.Base, pageUri, pageUri, null, embedded);
                    var doc = await new HtmlParser().ParseDocumentAsync(text, token).ConfigureAwait(false);
                    var cards = ExtractCards(doc, pageUri, site);
                    var categories = ExtractCategories(doc, pageUri);
                    var search = DiscoverSearchForm(doc, pageUri);
                    probeBase = new UriBuilder(pageUri) { Path = GetBasePath(pageUri.AbsolutePath), Query = "", Fragment = "" }.Uri;
                    if (cards.Count > 0 || categories.Count > 0 || search is not null)
                        return new SiteDiscovery(probeBase, pageUri, null, search,
                            new CmsResponse { HomeCategoryName = "最新推荐", Categories = categories, List = cards, Page = 1, PageCount = 1, Total = cards.Count });
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed) { preconditionFailure ??= ex; }
                catch (Exception ex) when (IsStaleRoute(ex)) { lastError = ex; }

                var conventional = new Uri(probeBase, "api.php/provide/vod/");
                if (await TryCmsHomeAsync(site, conventional, probeBase, probeBase, token).ConfigureAwait(false) is { } discoveredCms)
                    return discoveredCms;
            }
            catch (SiteVerificationRequiredException ex) { challenge ??= ex; }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed) { preconditionFailure ??= ex; }
            catch (Exception ex) when (IsStaleRoute(ex)) { lastError = ex; }
        }

        if (challenge is not null) throw challenge;
        if (preconditionFailure is not null) throw preconditionFailure;
        if (!reachedSite && lastError is not null && lastError is not SiteAdapterMismatchException &&
            lastError is not HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone }) throw lastError;
        throw new SiteAdapterMismatchException("未识别到公开的视频目录或通用播放接口。");
    }

    private async Task<SiteDiscovery?> TryCmsHomeAsync(SourceSite site, Uri endpoint, Uri siteBase, Uri referer, CancellationToken token)
    {
        var (raw, lastUri, _) = await ReadAsync(site, WithQuery(endpoint, ("ac", "list")), referer, token).ConfigureAwait(false);
        var home = TryParseCms(raw, site, lastUri, 1);
        if (home is null || home.List.Count == 0 && home.Categories.Count == 0) return null;
        if (home.List.Count == 0)
        {
            try { home.List = (await ReadCmsAsync(site, endpoint, referer, 1, token, ("ac", "detail"), ("pg", "1")).ConfigureAwait(false)).List; }
            catch (Exception ex) when (IsStaleRoute(ex)) { }
        }
        return new SiteDiscovery(siteBase, lastUri, endpoint, null, home);
    }

    private async Task<CmsResponse> ReadCmsAsync(SourceSite site, Uri endpoint, Uri referer, int page, CancellationToken token,
        params (string Key, string Value)[] query)
    {
        var (raw, finalUri, _) = await ReadAsync(site, WithQuery(endpoint, query), referer, token).ConfigureAwait(false);
        return TryParseCms(raw, site, finalUri, page) ?? throw new SiteAdapterMismatchException("站点返回的目录格式已变更。");
    }

    private async Task<(IDocument Document, Uri Uri)> ReadPageAsync(SourceSite site, Uri address, Uri referer, CancellationToken token)
    {
        var (text, uri, _) = await ReadAsync(site, address, referer, token).ConfigureAwait(false);
        if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('<'))
            throw new SiteAdapterMismatchException("站点未返回 HTML 页面。");
        return (await new HtmlParser().ParseDocumentAsync(text, token).ConfigureAwait(false), uri);
    }

    private static bool IsPlaybackContentType(string contentType) =>
        contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
        HlsManifest.IsPlaylist(string.Empty, contentType) || DashManifest.IsPlaylist(string.Empty, contentType);

    private async Task<(string Text, Uri Uri, string ContentType)> ReadAsync(SourceSite site, Uri address, Uri referer,
        CancellationToken token, bool skipPlaybackBody = false)
    {
        var configured = GetAddresses(site).FirstOrDefault()?.Entry ?? referer;
        var headers = NativeSiteToolkit.CreateBrowserHeaders(referer, MediaRequestHeaders.ForTarget(site.GetHeaders(), configured, address));
        try
        {
            var exchange = await http.GetExchangeAsync(address.AbsoluteUri, headers, maxBytes: 3 * 1024 * 1024,
                cancellationToken: token, scopeRedirectHeaders: true, requestTimeout: TimeSpan.FromSeconds(4),
                skipBodyForContentType: skipPlaybackBody ? IsPlaybackContentType : null).ConfigureAwait(false);
            var text = SourceHttpClient.DecodeText(exchange.Payload.Content);
            if (NativeSiteToolkit.IsBrowserChallenge(exchange, text))
                throw new SiteVerificationRequiredException(site.Key, site.Name, exchange.Payload.FinalUri, SiteVerificationKind.BrowserChallenge,
                    "站点需要在浏览器中完成验证。");
            if ((int)exchange.StatusCode is < 200 or >= 300)
                throw new HttpRequestException($"站点返回 HTTP {(int)exchange.StatusCode}。", null, exchange.StatusCode);
            return (text, exchange.Payload.FinalUri, exchange.Payload.ContentType);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("站点响应超时。", ex);
        }
    }

}
