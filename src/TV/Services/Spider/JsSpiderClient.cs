using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Vod;
using TV.Services.Vod.Native;

namespace TV.Services.Spider;

/// <summary>
/// Universal TVBox JavaScript spider engine powered by Jint (BSD-2-Clause) + AngleSharp.
/// Automatically executes both:
/// 1. drpy2 declarative rule scripts (`var rule = { title, host, url, 推荐, 一级, 二级, 搜索, ... }`)
/// 2. CatVod-JS / T3 module spiders (`export function home/category/detail/search/play` or `__JS_SPIDER__`)
/// </summary>
public sealed partial class JsSpiderClient
{
    private sealed record CachedScript(string Source, string Hash, DateTimeOffset CachedAt);
    private sealed record DrpyRule(
        string Title,
        string Host,
        string Url,
        string HomeUrl,
        string DetailUrl,
        string SearchUrl,
        string ClassName,
        string ClassUrl,
        string ClassParse,
        string Recommend,
        string FirstLevel,
        string SecondLevelRaw,
        JsonElement SecondLevelObj,
        string SearchRule,
        bool DoubleMode,
        bool PlayParse,
        string Lazy,
        string PreProcess,
        Dictionary<string, string> Headers);

    private static readonly string[] BuiltInJavaDrpyScripts = ["虎牙.js", "斗鱼直播.js", "兔小贝.js"];
    private static readonly TimeSpan ScriptCacheTtl = TimeSpan.FromHours(6);

    private readonly SourceHttpClient _http;
    private readonly ConcurrentDictionary<string, CachedScript> _scriptCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _localStorage = new(StringComparer.Ordinal);
    private readonly object _localSync = new();
    private bool _localLoaded;

    public JsSpiderClient(SourceHttpClient http) => _http = http;

    public static bool Supports(SourceSite site)
    {
        if (site.Type != 3) return false;
        var api = site.Api.Trim();
        var ext = ExtractExtString(site.Ext);
        if (IsLegacyBuiltInJavaDrpy(api, ext)) return false;

        if (api.Contains("drpy", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(ext);
        if (IsJsAddress(api))
            return true;
        return false;
    }

    private static bool IsLegacyBuiltInJavaDrpy(string api, string ext)
    {
        if (!api.Contains("drpy2", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ext)) return false;
        var unescaped = Uri.UnescapeDataString(ext);
        return BuiltInJavaDrpyScripts.Any(name => unescaped.Contains(name, StringComparison.Ordinal));
    }

    private static bool IsJsAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var clean = value.Split('?', 2)[0].Split('#', 2)[0].Trim();
        return clean.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (scriptUrl, source) = await LoadScriptAsync(site, cancellationToken).ConfigureAwait(false);
        if (LooksLikeDrpyRule(source))
        {
            var rule = ParseDrpyRule(source, site);
            return await ExecuteDrpyHomeAsync(site, rule, cancellationToken).ConfigureAwait(false);
        }
        var raw = await InvokeCatVodJsAsync(site, scriptUrl, source, "home", [true], cancellationToken).ConfigureAwait(false);
        var response = ParseCmsResponse(raw, site);
        if (response.List.Count == 0)
        {
            try
            {
                var homeVod = await InvokeCatVodJsAsync(site, scriptUrl, source, "homeVod", [], cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(homeVod))
                {
                    var extra = ParseCmsResponse(homeVod, site);
                    if (extra.List.Count > 0) response.List = extra.List;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (response.List.Count == 0 && response.Categories.Count > 0)
        {
            try
            {
                var firstCat = await GetCategoryAsync(site, response.Categories[0].Id, 1, null, cancellationToken).ConfigureAwait(false);
                if (firstCat.List.Count > 0) response.List = firstCat.List;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return response;
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken = default)
    {
        var (scriptUrl, source) = await LoadScriptAsync(site, cancellationToken).ConfigureAwait(false);
        if (LooksLikeDrpyRule(source))
        {
            var rule = ParseDrpyRule(source, site);
            return await ExecuteDrpyCategoryAsync(site, rule, categoryId, Math.Max(1, page), cancellationToken).ConfigureAwait(false);
        }
        var filterObj = filters is null ? new Dictionary<string, string>() : new Dictionary<string, string>(filters);
        var raw = await InvokeCatVodJsAsync(site, scriptUrl, source, "category",
            [categoryId, Math.Max(1, page).ToString(CultureInfo.InvariantCulture), true, filterObj], cancellationToken).ConfigureAwait(false);
        return ParseCmsResponse(raw, site);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page = 1, CancellationToken cancellationToken = default)
    {
        var (scriptUrl, source) = await LoadScriptAsync(site, cancellationToken).ConfigureAwait(false);
        if (LooksLikeDrpyRule(source))
        {
            var rule = ParseDrpyRule(source, site);
            return await ExecuteDrpySearchAsync(site, rule, keyword, Math.Max(1, page), cancellationToken).ConfigureAwait(false);
        }
        var raw = await InvokeCatVodJsAsync(site, scriptUrl, source, "search",
            [keyword, false, Math.Max(1, page).ToString(CultureInfo.InvariantCulture)], cancellationToken).ConfigureAwait(false);
        return ParseCmsResponse(raw, site);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var (scriptUrl, source) = await LoadScriptAsync(site, cancellationToken).ConfigureAwait(false);
        if (LooksLikeDrpyRule(source))
        {
            var rule = ParseDrpyRule(source, site);
            return await ExecuteDrpyDetailAsync(site, rule, id, cancellationToken).ConfigureAwait(false);
        }
        var raw = await InvokeCatVodJsAsync(site, scriptUrl, source, "detail", [id], cancellationToken).ConfigureAwait(false);
        var response = ParseCmsResponse(raw, site);
        return response.List.FirstOrDefault() ?? throw new InvalidDataException("JS 站点未返回视频详情。");
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken cancellationToken = default)
    {
        var (scriptUrl, source) = await LoadScriptAsync(site, cancellationToken).ConfigureAwait(false);
        if (LooksLikeDrpyRule(source))
        {
            var rule = ParseDrpyRule(source, site);
            return await ExecuteDrpyPlayAsync(site, rule, flag, id, cancellationToken).ConfigureAwait(false);
        }
        var raw = await InvokeCatVodJsAsync(site, scriptUrl, source, "play", [flag, id, Array.Empty<string>()], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            var direct = PlayUrlResolver.IsDirectMediaUrl(id);
            return new SpiderPlayResult(id, NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders()), direct ? 0 : 1, 0);
        }
        using var doc = JsonDocument.Parse(raw);
        return SpiderVodClient.ReadPlayResult(doc.RootElement);
    }

    #region Script Loading & Caching

    private async Task<(string Key, string Source)> LoadScriptAsync(SourceSite site, CancellationToken token)
    {
        var ext = ExtractExtString(site.Ext);
        var target = site.Api.Contains("drpy", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ext)
            ? ext
            : site.Api.Trim();

        // Inline rule script directly in ext
        if (target.Contains("var rule", StringComparison.Ordinal) || target.TrimStart().StartsWith('{'))
            return (site.Key, target);

        if (_scriptCache.TryGetValue(target, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < ScriptCacheTtl)
            return (target, cached.Source);

        string text;
        if (target.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(target).LocalPath;
            text = await File.ReadAllTextAsync(localPath, token).ConfigureAwait(false);
        }
        else if (target.StartsWith("assets://", StringComparison.OrdinalIgnoreCase) ||
                 target.StartsWith("clan://", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"该站点引用了 Android 离线包脚本（{target}），请改用 HTTP/HTTPS 在线仓库。");
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var cacheFile = GetScriptCacheFile(target);
            if (File.Exists(cacheFile) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < ScriptCacheTtl)
            {
                text = await File.ReadAllTextAsync(cacheFile, token).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    text = await DownloadScriptWithMirrorsAsync(uri.AbsoluteUri, token).ConfigureAwait(false);
                    ValidateScript(text, target);
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    await File.WriteAllTextAsync(cacheFile, text, Encoding.UTF8, token).ConfigureAwait(false);
                }
                catch when (File.Exists(cacheFile))
                {
                    text = await File.ReadAllTextAsync(cacheFile, token).ConfigureAwait(false);
                }
            }
        }
        else
        {
            throw new InvalidDataException($"不支持的 JS 爬虫地址：{target}");
        }

        ValidateScript(text, target);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        _scriptCache[target] = new CachedScript(text, hash, DateTimeOffset.UtcNow);
        return (target, text);
    }

    private async Task<string> DownloadScriptWithMirrorsAsync(string url, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = "okhttp/4.12.0" };
        var (content, _) = await GitHubMirrorService.GetTextWithMirrorsAsync(_http, url, headers, cancellationToken: token).ConfigureAwait(false);
        return content;
    }

    private static void ValidateScript(string raw, string source)
    {
        var text = raw.TrimStart('\uFEFF').Trim();
        if (text.Length == 0) throw new InvalidDataException($"JS 爬虫脚本为空：{source}");
        if (text.StartsWith("//bb", StringComparison.Ordinal))
            throw new NotSupportedException("该站点使用加密 QuickJS 字节码（//bb），暂不支持桌面直接执行。");
        if (text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"JS 爬虫地址返回了网页内容：{source}");
    }

    private static string GetScriptCacheFile(string url)
    {
        var dir = Path.Combine(AppPaths.Root, "spider", "js-cache");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        return Path.Combine(dir, key + ".js");
    }

    private static bool LooksLikeDrpyRule(string source) =>
        DrpyRuleDeclRegex().IsMatch(source) || source.TrimStart().StartsWith('{');

    #endregion

    #region drpy2 Rule Interpreter

    private DrpyRule ParseDrpyRule(string source, SourceSite site)
    {
        string json;
        var trimmed = source.TrimStart('\uFEFF').Trim();
        if (trimmed.StartsWith('{'))
        {
            json = trimmed;
        }
        else
        {
            using var engine = CreateSandboxedEngine(site, CancellationToken.None);
            engine.Execute(StripJsImports(trimmed));
            var ruleVal = engine.Evaluate("typeof rule !== 'undefined' ? JSON.stringify(rule) : ''");
            json = ruleVal.IsString() ? ruleVal.AsString() : string.Empty;
        }
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("drpy2 脚本未导出 rule 对象。");

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (r.TryGetProperty("headers", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hdrEl.EnumerateObject())
                headers[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
        }
        headers.TryAdd("User-Agent", NativeSiteToolkit.BrowserUserAgent);

        var secondRaw = string.Empty;
        JsonElement secondObj = default;
        if (r.TryGetProperty("二级", out var secEl))
        {
            if (secEl.ValueKind == JsonValueKind.String) secondRaw = secEl.GetString() ?? string.Empty;
            else if (secEl.ValueKind == JsonValueKind.Object) secondObj = secEl.Clone();
        }

        return new DrpyRule(
            Title: NativeSiteToolkit.ReadJsonString(r, "title"),
            Host: NativeSiteToolkit.ReadJsonString(r, "host").TrimEnd('/'),
            Url: NativeSiteToolkit.ReadJsonString(r, "url"),
            HomeUrl: NativeSiteToolkit.ReadJsonString(r, "homeUrl"),
            DetailUrl: NativeSiteToolkit.ReadJsonString(r, "detailUrl"),
            SearchUrl: NativeSiteToolkit.ReadJsonString(r, "searchUrl"),
            ClassName: NativeSiteToolkit.ReadJsonString(r, "class_name"),
            ClassUrl: NativeSiteToolkit.ReadJsonString(r, "class_url"),
            ClassParse: NativeSiteToolkit.ReadJsonString(r, "class_parse"),
            Recommend: NativeSiteToolkit.ReadJsonString(r, "推荐"),
            FirstLevel: NativeSiteToolkit.ReadJsonString(r, "一级"),
            SecondLevelRaw: secondRaw,
            SecondLevelObj: secondObj,
            SearchRule: NativeSiteToolkit.ReadJsonString(r, "搜索"),
            DoubleMode: r.TryGetProperty("double", out var dbl) && dbl.ValueKind == JsonValueKind.True,
            PlayParse: r.TryGetProperty("play_parse", out var pp) && pp.ValueKind == JsonValueKind.True,
            Lazy: NativeSiteToolkit.ReadJsonString(r, "lazy"),
            PreProcess: NativeSiteToolkit.ReadJsonString(r, "预处理"),
            Headers: headers);
    }

    private async Task<CmsResponse> ExecuteDrpyHomeAsync(SourceSite site, DrpyRule rule, CancellationToken token)
    {
        var categories = new List<VodCategory>();
        if (!string.IsNullOrWhiteSpace(rule.ClassName) && !string.IsNullOrWhiteSpace(rule.ClassUrl))
        {
            var names = rule.ClassName.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var urls = rule.ClassUrl.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < Math.Min(names.Length, urls.Length); i++)
                categories.Add(new VodCategory { Id = urls[i], Name = names[i] });
        }

        var homeTarget = string.IsNullOrWhiteSpace(rule.HomeUrl)
            ? rule.Host + "/"
            : ResolveTemplateUrl(rule.Host, rule.HomeUrl);
        var (html, pageUri) = await FetchDrpyPageAsync(homeTarget, rule.Headers, token).ConfigureAwait(false);

        if (categories.Count == 0 && !string.IsNullOrWhiteSpace(rule.ClassParse))
        {
            var cp = rule.ClassParse.Split(';');
            if (cp.Length >= 3)
            {
                var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
                var nodes = QueryDrpyList(doc, cp[0]);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var regexPattern = cp.Length >= 4 && !string.IsNullOrWhiteSpace(cp[3]) ? cp[3] : null;
                foreach (var node in nodes)
                {
                    var name = QueryDrpyText(node, cp[1], pageUri, isUrl: false);
                    var id = QueryDrpyText(node, cp[2], pageUri, isUrl: false);
                    if (regexPattern is not null)
                    {
                        var m = Regex.Match(id, regexPattern);
                        if (m.Success) id = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                    }
                    if (name.Length > 0 && id.Length > 0 && seen.Add(id))
                        categories.Add(new VodCategory { Id = id, Name = name });
                }
            }
        }

        var recRule = rule.Recommend == "*" ? rule.FirstLevel : rule.Recommend;
        var items = await ParseDrpyListRuleAsync(html, pageUri, site, rule, recRule, rule.DoubleMode, token).ConfigureAwait(false);
        if (items.Count == 0 && categories.Count > 0)
        {
            var catResp = await ExecuteDrpyCategoryAsync(site, rule, categories[0].Id, 1, token).ConfigureAwait(false);
            items = catResp.List;
        }
        return new CmsResponse { Categories = categories, List = items, Page = 1, PageCount = 1, Total = items.Count };
    }

    private async Task<CmsResponse> ExecuteDrpyCategoryAsync(SourceSite site, DrpyRule rule, string tid, int page, CancellationToken token)
    {
        var path = rule.Url
            .Replace("fyclass", tid, StringComparison.Ordinal)
            .Replace("fypage", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("fyfilter", "", StringComparison.Ordinal);
        var target = ResolveTemplateUrl(rule.Host, path);
        var (html, pageUri) = await FetchDrpyPageAsync(target, rule.Headers, token).ConfigureAwait(false);
        var items = await ParseDrpyListRuleAsync(html, pageUri, site, rule, rule.FirstLevel, doubleMode: false, token).ConfigureAwait(false);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    private async Task<CmsResponse> ExecuteDrpySearchAsync(SourceSite site, DrpyRule rule, string keyword, int page, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(rule.SearchUrl)) return new CmsResponse { Page = page, PageCount = 1 };
        var path = rule.SearchUrl
            .Replace("**", Uri.EscapeDataString(keyword), StringComparison.Ordinal)
            .Replace("fypage", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var target = ResolveTemplateUrl(rule.Host, path);
        var (html, pageUri) = await FetchDrpyPageAsync(target, rule.Headers, token).ConfigureAwait(false);
        var searchRule = rule.SearchRule == "*" || string.IsNullOrWhiteSpace(rule.SearchRule) ? rule.FirstLevel : rule.SearchRule;
        var items = await ParseDrpyListRuleAsync(html, pageUri, site, rule, searchRule, doubleMode: false, token).ConfigureAwait(false);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    private async Task<VodItem> ExecuteDrpyDetailAsync(SourceSite site, DrpyRule rule, string id, CancellationToken token)
    {
        var target = id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? id
            : (!string.IsNullOrWhiteSpace(rule.DetailUrl)
                ? ResolveTemplateUrl(rule.Host, rule.DetailUrl.Replace("fyid", id, StringComparison.Ordinal))
                : ResolveTemplateUrl(rule.Host, id));
        var (html, pageUri) = await FetchDrpyPageAsync(target, rule.Headers, token).ConfigureAwait(false);

        if (rule.SecondLevelRaw.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
        {
            var jsDetail = await ExecuteDrpyJsDetailAsync(site, rule, html, pageUri, id, rule.SecondLevelRaw[3..], token).ConfigureAwait(false);
            if (jsDetail is not null) return jsDetail;
        }

        var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);

        if (rule.SecondLevelObj.ValueKind == JsonValueKind.Object)
        {
            var s = rule.SecondLevelObj;
            var titleSel = NativeSiteToolkit.ReadJsonString(s, "title").Split(';');
            var title = titleSel.Length > 0 ? QueryDrpyText(doc, titleSel[0], pageUri, false) : string.Empty;
            var typeName = titleSel.Length > 1 ? QueryDrpyText(doc, titleSel[1], pageUri, false) : string.Empty;
            var img = QueryDrpyText(doc, NativeSiteToolkit.ReadJsonString(s, "img"), pageUri, true);
            var descParts = NativeSiteToolkit.ReadJsonString(s, "desc").Split(';');
            var remarks = descParts.Length > 0 ? QueryDrpyText(doc, descParts[0], pageUri, false) : string.Empty;
            var year = descParts.Length > 1 ? QueryDrpyText(doc, descParts[1], pageUri, false) : string.Empty;
            var area = descParts.Length > 2 ? QueryDrpyText(doc, descParts[2], pageUri, false) : string.Empty;
            var actor = descParts.Length > 3 ? QueryDrpyText(doc, descParts[3], pageUri, false) : string.Empty;
            var director = descParts.Length > 4 ? QueryDrpyText(doc, descParts[4], pageUri, false) : string.Empty;
            var content = QueryDrpyText(doc, NativeSiteToolkit.ReadJsonString(s, "content"), pageUri, false);

            var tabsSel = NativeSiteToolkit.ReadJsonString(s, "tabs");
            var listsSel = NativeSiteToolkit.ReadJsonString(s, "lists");
            var tabNames = !string.IsNullOrWhiteSpace(tabsSel)
                ? QueryDrpyList(doc, tabsSel).Select(e => NativeSiteToolkit.SanitizeLabel(e.TextContent)).Where(t => t.Length > 0).ToList()
                : new List<string>();
            var playFrom = new List<string>();
            var playUrl = new List<string>();
            if (!string.IsNullOrWhiteSpace(listsSel))
            {
                var lists = listsSel.Contains(":eq(#id)", StringComparison.Ordinal)
                    ? ExtractIndexedDrpyLists(doc, listsSel, Math.Max(tabNames.Count, 1), pageUri)
                    : ExtractDirectDrpyLists(doc, listsSel, pageUri);
                for (var i = 0; i < lists.Count; i++)
                {
                    if (lists[i].Count == 0) continue;
                    playFrom.Add(i < tabNames.Count ? tabNames[i] : $"线路{playFrom.Count + 1}");
                    playUrl.Add(string.Join('#', lists[i]));
                }
            }
            if (playFrom.Count == 0)
            {
                (playFrom, playUrl) = NativeSiteToolkit.ExtractStuiPlaylists(
                    doc, pageUri, ".stui-pannel__head h3, .nav-tabs li a, .module-tab-item", ".stui-content__playlist, .module-play-list-content");
            }
            return new VodItem
            {
                Id = id, SiteKey = site.Key, SiteName = site.Name,
                Name = NativeSiteToolkit.FirstNonEmpty(title, doc.QuerySelector("h1")?.TextContent),
                Picture = img, TypeName = typeName, Remarks = remarks, Year = year, Area = area,
                Actor = actor, Director = director, Content = content,
                PlayFrom = string.Join("$$$", playFrom), PlayUrl = string.Join("$$$", playUrl)
            };
        }

        // Fallback MacCMS / Stui detail extraction when 二级 is '*' or unspecified
        var fallbackTitle = NativeSiteToolkit.FirstNonEmpty(doc.QuerySelector("h1.title, .page-title, h1")?.TextContent, doc.Title);
        var fallbackPic = NativeSiteToolkit.ExtractImageUrl(doc.QuerySelector(".stui-content__thumb img, .module-item-pic img, .lazyload"), pageUri);
        var fallbackDesc = NativeSiteToolkit.CollapseSpaces(doc.QuerySelector(".detail-content, .stui-content__desc, .video-info-content")?.TextContent);
        var (names, urls) = NativeSiteToolkit.ExtractStuiPlaylists(
            doc, pageUri, ".stui-pannel__head h3, .stui-vodlist__head h3, .nav-tabs li a, .module-tab-item",
            ".stui-content__playlist, .module-play-list-content, ul.play-list");
        if (names.Count == 0)
        {
            var shareEps = new List<string>();
            var seenShare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var href = NativeSiteToolkit.ResolveUrl(pageUri, a.GetAttribute("href"));
                if (href.Length == 0 || !seenShare.Add(href)) continue;
                var isCloudOrMedia =
                    href.Contains("pan.quark.cn/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("drive.uc.cn/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("alipan.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("aliyundrive.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("pan.baidu.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("123pan.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("123684.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("123865.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("123912.com/s/", StringComparison.OrdinalIgnoreCase) ||
                    PlayUrlResolver.IsDirectMediaUrl(href);
                if (!isCloudOrMedia) continue;
                var label = NativeSiteToolkit.SanitizeLabel(a.TextContent);
                if (label.Length == 0) label = $"资源{shareEps.Count + 1}";
                shareEps.Add($"{label}${href}");
            }
            if (shareEps.Count > 0)
            {
                names.Add("网盘/直链资源");
                urls.Add(string.Join('#', shareEps));
            }
        }
        return new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name,
            Name = fallbackTitle, Picture = fallbackPic, Content = fallbackDesc,
            PlayFrom = string.Join("$$$", names), PlayUrl = string.Join("$$$", urls)
        };
    }

    private async Task<SpiderPlayResult> ExecuteDrpyPlayAsync(SourceSite site, DrpyRule rule, string flag, string id, CancellationToken token)
    {
        var target = id.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? id : ResolveTemplateUrl(rule.Host, id);
        var headers = new Dictionary<string, string>(rule.Headers, StringComparer.OrdinalIgnoreCase);

        if (rule.Lazy.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
        {
            var lazyResult = await ExecuteDrpyJsLazyAsync(site, rule, target, flag, rule.Lazy[3..], token).ConfigureAwait(false);
            if (lazyResult is not null) return lazyResult;
        }

        if (PlayUrlResolver.IsDirectMediaUrl(target))
            return new SpiderPlayResult(target, headers, 0, 0);

        // Try direct MacCMS player_aaaa extraction first (instant, zero WebView2 needed!)
        try
        {
            var (html, finalUri) = await FetchDrpyPageAsync(target, headers, token).ConfigureAwait(false);
            if (NativeSiteToolkit.TryParsePlayerAaaa(html, out var player) &&
                player.DecodedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                PlayUrlResolver.IsDirectMediaUrl(player.DecodedUrl))
            {
                headers["Referer"] = finalUri.AbsoluteUri;
                return new SpiderPlayResult(player.DecodedUrl, headers, 0, 0);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        headers["Referer"] = rule.Host + "/";
        return new SpiderPlayResult(target, headers, 1, 0);
    }

    private async Task<List<VodItem>> ParseDrpyListRuleAsync(
        string html, Uri pageUri, SourceSite site, DrpyRule rule, string ruleExpr, bool doubleMode, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ruleExpr)) return [];
        if (ruleExpr.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
            return await ExecuteDrpyJsListAsync(site, rule, html, pageUri, ruleExpr[3..], token).ConfigureAwait(false);

        var parts = ruleExpr.Split(';');
        if (parts.Length < 5) return [];
        var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (doubleMode && parts.Length >= 6)
        {
            foreach (var group in QueryDrpyList(doc, parts[0]))
            {
                foreach (var card in QueryDrpyList(group, parts[1]))
                    AddDrpyCard(items, seen, card, parts[2], parts[3], parts[4], parts[5], pageUri, site);
            }
        }
        else
        {
            foreach (var card in QueryDrpyList(doc, parts[0]))
                AddDrpyCard(items, seen, card, parts[1], parts[2], parts[3], parts[4], pageUri, site);
        }
        return items;
    }

    private Task<List<VodItem>> ExecuteDrpyJsListAsync(SourceSite site, DrpyRule rule, string html, Uri pageUri, string jsBody, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var engine = CreateSandboxedEngine(site, token);
            BindDrpyJsGlobals(engine, rule, html, pageUri.AbsoluteUri);
            engine.Execute("var VODS = []; var d = []; function setResult(arr) { if (Array.isArray(arr)) VODS = arr; }");
            engine.Execute(jsBody);
            var json = engine.Evaluate("""
                (function() {
                    var src = (Array.isArray(VODS) && VODS.length > 0) ? VODS : (Array.isArray(d) ? d : []);
                    return JSON.stringify(src.map(function(x) {
                        return {
                            vod_id: String(x.vod_id || x.url || ''),
                            vod_name: String(x.vod_name || x.title || ''),
                            vod_pic: String(x.vod_pic || x.img || x.pic_url || ''),
                            vod_remarks: String(x.vod_remarks || x.desc || x.content || '')
                        };
                    }));
                })();
                """).AsString();
            var list = new List<VodItem>();
            using var doc = JsonDocument.Parse(json);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = NativeSiteToolkit.ReadJsonString(el, "vod_id");
                var name = NativeSiteToolkit.ReadJsonString(el, "vod_name");
                if (id.Length == 0 || name.Length == 0 || !seen.Add(id)) continue;
                list.Add(new VodItem
                {
                    Id = id, SiteKey = site.Key, SiteName = site.Name, Name = name,
                    Picture = NativeSiteToolkit.ReadJsonString(el, "vod_pic"),
                    Remarks = NativeSiteToolkit.ReadJsonString(el, "vod_remarks")
                });
            }
            return list;
        }, token);
    }

    private Task<VodItem?> ExecuteDrpyJsDetailAsync(SourceSite site, DrpyRule rule, string html, Uri pageUri, string id, string jsBody, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var engine = CreateSandboxedEngine(site, token);
            BindDrpyJsGlobals(engine, rule, html, pageUri.AbsoluteUri);
            engine.Execute("var VOD = {}; var LISTS = [];");
            engine.Execute(jsBody);
            var json = engine.Evaluate("JSON.stringify(VOD || {})").AsString();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var name = NativeSiteToolkit.ReadJsonString(r, "vod_name");
            var playUrl = NativeSiteToolkit.ReadJsonString(r, "vod_play_url");
            if (name.Length == 0 && playUrl.Length == 0) return null;
            return new VodItem
            {
                Id = id, SiteKey = site.Key, SiteName = site.Name,
                Name = name.Length > 0 ? name : id,
                Picture = NativeSiteToolkit.ReadJsonString(r, "vod_pic"),
                TypeName = NativeSiteToolkit.ReadJsonString(r, "type_name"),
                Remarks = NativeSiteToolkit.ReadJsonString(r, "vod_remarks"),
                Year = NativeSiteToolkit.ReadJsonString(r, "vod_year"),
                Area = NativeSiteToolkit.ReadJsonString(r, "vod_area"),
                Actor = NativeSiteToolkit.ReadJsonString(r, "vod_actor"),
                Director = NativeSiteToolkit.ReadJsonString(r, "vod_director"),
                Content = PlaybackParsing.StripHtml(NativeSiteToolkit.ReadJsonString(r, "vod_content")),
                PlayFrom = NativeSiteToolkit.FirstNonEmpty(NativeSiteToolkit.ReadJsonString(r, "vod_play_from"), "在线播放"),
                PlayUrl = playUrl
            };
        }, token);
    }

    private Task<SpiderPlayResult?> ExecuteDrpyJsLazyAsync(SourceSite site, DrpyRule rule, string target, string flag, string jsBody, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var engine = CreateSandboxedEngine(site, token);
            BindDrpyJsGlobals(engine, rule, string.Empty, target);
            engine.SetValue("flag", flag);
            engine.Execute(jsBody);
            var raw = engine.Evaluate("typeof input === 'string' ? JSON.stringify({ url: input }) : JSON.stringify(input || {})").AsString();
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var url = NativeSiteToolkit.ReadJsonString(root, "url");
            if (string.IsNullOrWhiteSpace(url)) return null;
            var parse = NativeSiteToolkit.ReadJsonInt(root, "parse", PlayUrlResolver.IsDirectMediaUrl(url) ? 0 : 1);
            var headers = new Dictionary<string, string>(rule.Headers, StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("header", out var hdr) && hdr.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in hdr.EnumerateObject())
                    headers[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
            }
            return new SpiderPlayResult(url, headers, parse, 0);
        }, token);
    }

    private static void BindDrpyJsGlobals(Engine engine, DrpyRule rule, string html, string url)
    {
        engine.SetValue("html", html);
        engine.SetValue("MY_URL", url);
        engine.SetValue("input", url);
        engine.SetValue("HOST", rule.Host);
        engine.SetValue("rule_fetch_params", new { headers = rule.Headers });
        engine.Execute("""
            var jsp = { pdfh: pdfh, pdfa: pdfa, pd: function(h, expr, base) { return pd(h, expr, base || MY_URL); } };
            function joinUrl(base, rel) { return pd('<a href="' + String(rel || '') + '"></a>', 'a&&href', String(base || MY_URL)); }
            var urljoin = joinUrl;
            var urljoin2 = joinUrl;
            function post(u, opts) { opts = opts || {}; opts.method = 'POST'; return request(u, opts); }
            function pdfl(h, listExpr, urlExpr, base) {
                return pdfa(h, listExpr).map(function(it) { return pd(it, urlExpr, base || MY_URL); });
            }
            """);
    }

    private static void AddDrpyCard(
        List<VodItem> items, HashSet<string> seen, IElement card,
        string titleRule, string imgRule, string remarkRule, string linkRule,
        Uri pageUri, SourceSite site)
    {
        var name = QueryDrpyText(card, titleRule, pageUri, false);
        var href = QueryDrpyText(card, linkRule, pageUri, true);
        if (name.Length == 0 || href.Length == 0 || !seen.Add(href)) return;
        items.Add(new VodItem
        {
            Id = href,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = name,
            Picture = QueryDrpyText(card, imgRule, pageUri, true),
            Remarks = QueryDrpyText(card, remarkRule, pageUri, false)
        });
    }

    private static List<List<string>> ExtractIndexedDrpyLists(IDocument doc, string listsSel, int tabCount, Uri pageUri)
    {
        var result = new List<List<string>>();
        for (var i = 0; i < tabCount; i++)
        {
            var resolved = listsSel.Replace("#id", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var eps = new List<string>();
            foreach (var a in QueryDrpyList(doc, resolved))
            {
                var label = NativeSiteToolkit.SanitizeLabel(a.TextContent);
                var href = NativeSiteToolkit.ResolveUrl(pageUri, a.GetAttribute("href"));
                if (label.Length > 0 && href.Length > 0) eps.Add($"{label}${href}");
            }
            result.Add(eps);
        }
        return result;
    }

    private static List<List<string>> ExtractDirectDrpyLists(IDocument doc, string listsSel, Uri pageUri)
    {
        var result = new List<List<string>>();
        var containers = QueryDrpyList(doc, listsSel);
        if (containers.Count > 0 && containers.All(c => c.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)))
        {
            var eps = new List<string>();
            foreach (var a in containers)
            {
                var label = NativeSiteToolkit.SanitizeLabel(a.TextContent);
                var href = NativeSiteToolkit.ResolveUrl(pageUri, a.GetAttribute("href"));
                if (label.Length > 0 && href.Length > 0) eps.Add($"{label}${href}");
            }
            if (eps.Count > 0) result.Add(eps);
            return result;
        }
        foreach (var container in containers)
        {
            var eps = new List<string>();
            var links = container.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)
                ? [container]
                : container.QuerySelectorAll("a[href]").ToArray();
            foreach (var a in links)
            {
                var label = NativeSiteToolkit.SanitizeLabel(a.TextContent);
                var href = NativeSiteToolkit.ResolveUrl(pageUri, a.GetAttribute("href"));
                if (label.Length > 0 && href.Length > 0) eps.Add($"{label}${href}");
            }
            if (eps.Count > 0) result.Add(eps);
        }
        return result;
    }

    private async Task<(string Html, Uri FinalUri)> FetchDrpyPageAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken token)
    {
        var exchange = await _http.GetExchangeAsync(url, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        return (SourceHttpClient.DecodeText(exchange.Payload.Content), exchange.Payload.FinalUri);
    }

    private static string ResolveTemplateUrl(string host, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return host + "/";
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        return host.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    #endregion

    #region drpy2 CSS / pdfh / pdfa / pd Selector Engine (AngleSharp)

    internal static List<IElement> QueryDrpyList(IParentNode root, string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return [];
        foreach (var branch in expr.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var list = QueryDrpyListSingle(root, branch);
            if (list.Count > 0) return list;
        }
        return [];
    }

    private static List<IElement> QueryDrpyListSingle(IParentNode root, string expr)
    {
        var clean = expr.Split("--", 2)[0].Trim();
        var segments = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<IParentNode> current = [root];
        var buffer = new StringBuilder();

        var isFirstFlush = true;
        void FlushBuffer()
        {
            var sel = buffer.ToString().Trim();
            buffer.Clear();
            if (sel.Length == 0) return;
            var next = new List<IParentNode>();
            foreach (var node in current)
            {
                try
                {
                    var found = node.QuerySelectorAll(sel);
                    if (found.Length > 0)
                        next.AddRange(found);
                    else if (isFirstFlush && node is IElement selfEl && selfEl.Matches(sel))
                        next.Add(selfEl);
                }
                catch { }
            }
            isFirstFlush = false;
            current = next;
        }

        foreach (var seg in segments)
        {
            var eqMatch = EqSelectorRegex().Match(seg);
            if (eqMatch.Success)
            {
                var prefix = eqMatch.Groups[1].Value;
                var idx = int.Parse(eqMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var suffix = eqMatch.Groups[3].Value;
                if (prefix.Length > 0)
                {
                    if (buffer.Length > 0) buffer.Append(' ');
                    buffer.Append(prefix);
                }
                FlushBuffer();
                if (idx < 0) idx = current.Count + idx;
                current = idx >= 0 && idx < current.Count ? [current[idx]] : [];
                if (suffix.Length > 0) buffer.Append(suffix);
            }
            else
            {
                if (buffer.Length > 0) buffer.Append(' ');
                buffer.Append(seg);
            }
        }
        FlushBuffer();
        return current.OfType<IElement>().ToList();
    }

    internal static string QueryDrpyText(IParentNode root, string expr, Uri? baseUri, bool isUrl)
    {
        if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
        foreach (var branch in expr.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = QueryDrpyTextSingle(root, branch, baseUri, isUrl);
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static string QueryDrpyTextSingle(IParentNode root, string expr, Uri? baseUri, bool isUrl)
    {
        var parts = expr.Split("&&", StringSplitOptions.TrimEntries);
        var selector = parts[0];
        var attr = parts.Length > 1 ? parts[^1] : "Text";
        var excluded = string.Empty;
        if (selector.Contains("--", StringComparison.Ordinal))
        {
            var split = selector.Split("--", 2);
            selector = split[0].Trim();
            excluded = split[1].Trim();
        }

        IElement? target;
        if (selector.Length == 0 || selector == "this")
        {
            target = root as IElement;
        }
        else
        {
            var chain = parts.Length > 2 ? string.Join(' ', parts[..^1]) : selector;
            target = QueryDrpyListSingle(root, chain).FirstOrDefault();
        }
        if (target is null) return string.Empty;

        string raw;
        if (attr.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            if (excluded.Length > 0)
            {
                var clone = (IElement)target.Clone();
                try { foreach (var rm in clone.QuerySelectorAll(excluded)) rm.Remove(); } catch { }
                raw = NativeSiteToolkit.CollapseSpaces(clone.TextContent);
            }
            else
            {
                raw = NativeSiteToolkit.CollapseSpaces(target.TextContent);
            }
        }
        else if (attr.Equals("Html", StringComparison.OrdinalIgnoreCase))
        {
            raw = target.InnerHtml;
        }
        else
        {
            raw = target.GetAttribute(attr)?.Trim() ?? string.Empty;
        }

        if (isUrl && baseUri is not null && raw.Length > 0)
            return NativeSiteToolkit.ResolveUrl(baseUri, raw);
        return raw;
    }

    #endregion

    #region CatVod-JS / T3 Jint Executor

    private Task<string> InvokeCatVodJsAsync(
        SourceSite site, string scriptKey, string source, string action, object?[] args, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var engine = CreateSandboxedEngine(site, token);
            var prepared = PreprocessEsModule(source);
            engine.Execute(prepared);

            var ext = ExtractExtString(site.Ext);
            engine.SetValue("__tv_ext", ext);
            engine.SetValue("__tv_action", action);
            engine.SetValue("__tv_args_json", JsonSerializer.Serialize(args));

            var runner = """
                (function() {
                    var mod = (typeof __JS_SPIDER__ !== 'undefined' && __JS_SPIDER__)
                        || (typeof module !== 'undefined' && module.exports && (module.exports.default || module.exports))
                        || (typeof exports !== 'undefined' && (exports.default || exports))
                        || globalThis;
                    if (typeof mod === 'function') { try { mod = mod(); } catch (_) {} }
                    if (mod && typeof mod.init === 'function') {
                        try { mod.init({ ext: __tv_ext, stype: 3, skey: globalThis.__tv_skey || '' }); }
                        catch (_) { try { mod.init(__tv_ext); } catch (__) {} }
                    }
                    var fn = mod && mod[__tv_action];
                    if (typeof fn !== 'function') {
                        var aliases = { home: 'homeContent', homeVod: 'homeVideoContent', category: 'categoryContent', detail: 'detailContent', search: 'searchContent', play: 'playerContent' };
                        fn = mod && mod[aliases[__tv_action]];
                    }
                    if (typeof fn !== 'function') return '';
                    var parsedArgs = JSON.parse(__tv_args_json);
                    var res = fn.apply(mod, parsedArgs);
                    if (res === null || res === undefined) return '';
                    if (typeof res === 'string') return res;
                    return JSON.stringify(res);
                })();
                """;
            var val = engine.Evaluate(runner).UnwrapIfPromise();
            if (val.IsNull() || val.IsUndefined()) return string.Empty;
            if (val.IsString()) return val.AsString();
            return engine.Invoke("JSON.stringify", val).AsString();
        }, token);
    }

    public Task<CmsResponse> ExecuteInlineHomeAsync(SourceSite site, string scriptSource, CancellationToken token = default)
    {
        if (LooksLikeDrpyRule(scriptSource))
            return ExecuteDrpyHomeAsync(site, ParseDrpyRule(scriptSource, site), token);
        return InvokeInlineCatVodHomeAsync(site, scriptSource, token);
    }

    public Task<CmsResponse> ExecuteInlineCategoryAsync(SourceSite site, string scriptSource, string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken token = default)
    {
        if (LooksLikeDrpyRule(scriptSource))
            return ExecuteDrpyCategoryAsync(site, ParseDrpyRule(scriptSource, site), categoryId, Math.Max(1, page), token);
        return InvokeInlineCatVodCategoryAsync(site, scriptSource, categoryId, page, filters, token);
    }

    public Task<CmsResponse> ExecuteInlineSearchAsync(SourceSite site, string scriptSource, string keyword, int page, CancellationToken token = default)
    {
        if (LooksLikeDrpyRule(scriptSource))
            return ExecuteDrpySearchAsync(site, ParseDrpyRule(scriptSource, site), keyword, Math.Max(1, page), token);
        return InvokeInlineCatVodSearchAsync(site, scriptSource, keyword, page, token);
    }

    public Task<VodItem> ExecuteInlineDetailAsync(SourceSite site, string scriptSource, string id, CancellationToken token = default)
    {
        if (LooksLikeDrpyRule(scriptSource))
            return ExecuteDrpyDetailAsync(site, ParseDrpyRule(scriptSource, site), id, token);
        return InvokeInlineCatVodDetailAsync(site, scriptSource, id, token);
    }

    public Task<SpiderPlayResult> ExecuteInlinePlayAsync(SourceSite site, string scriptSource, string flag, string id, CancellationToken token = default)
    {
        if (LooksLikeDrpyRule(scriptSource))
            return ExecuteDrpyPlayAsync(site, ParseDrpyRule(scriptSource, site), flag, id, token);
        return InvokeInlineCatVodPlayAsync(site, scriptSource, flag, id, token);
    }

    private async Task<CmsResponse> InvokeInlineCatVodHomeAsync(SourceSite site, string source, CancellationToken token)
    {
        var raw = await InvokeCatVodJsAsync(site, site.Key, source, "home", [true], token).ConfigureAwait(false);
        var resp = ParseCmsResponse(raw, site);
        if (resp.List.Count == 0)
        {
            try
            {
                var homeVod = await InvokeCatVodJsAsync(site, site.Key, source, "homeVod", [], token).ConfigureAwait(false);
                var extra = ParseCmsResponse(homeVod, site);
                if (extra.List.Count > 0) resp.List = extra.List;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return resp;
    }

    private async Task<CmsResponse> InvokeInlineCatVodCategoryAsync(SourceSite site, string source, string tid, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken token)
    {
        var filterObj = filters is null ? new Dictionary<string, string>() : new Dictionary<string, string>(filters);
        var raw = await InvokeCatVodJsAsync(site, site.Key, source, "category", [tid, Math.Max(1, page).ToString(CultureInfo.InvariantCulture), true, filterObj], token).ConfigureAwait(false);
        return ParseCmsResponse(raw, site);
    }

    private async Task<CmsResponse> InvokeInlineCatVodSearchAsync(SourceSite site, string source, string keyword, int page, CancellationToken token)
    {
        var raw = await InvokeCatVodJsAsync(site, site.Key, source, "search", [keyword, false, Math.Max(1, page).ToString(CultureInfo.InvariantCulture)], token).ConfigureAwait(false);
        return ParseCmsResponse(raw, site);
    }

    private async Task<VodItem> InvokeInlineCatVodDetailAsync(SourceSite site, string source, string id, CancellationToken token)
    {
        var raw = await InvokeCatVodJsAsync(site, site.Key, source, "detail", [id], token).ConfigureAwait(false);
        return ParseCmsResponse(raw, site).List.FirstOrDefault() ?? throw new InvalidDataException("JS 规则未返回视频详情。");
    }

    private async Task<SpiderPlayResult> InvokeInlineCatVodPlayAsync(SourceSite site, string source, string flag, string id, CancellationToken token)
    {
        var raw = await InvokeCatVodJsAsync(site, site.Key, source, "play", [flag, id, Array.Empty<string>()], token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new SpiderPlayResult(id, NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders()), PlayUrlResolver.IsDirectMediaUrl(id) ? 0 : 1, 0);
        using var doc = JsonDocument.Parse(raw);
        return SpiderVodClient.ReadPlayResult(doc.RootElement);
    }

    private Engine CreateSandboxedEngine(SourceSite site, CancellationToken token)
    {
        EnsureLocalStorageLoaded();
        var engine = new Engine(opts => opts
            .TimeoutInterval(TimeSpan.FromSeconds(12))
            .LimitMemory(64 * 1024 * 1024)
            .MaxStatements(500_000)
            .LimitRecursion(128)
            .CancellationToken(token));

        engine.SetValue("__tv_skey", site.Key);
        engine.SetValue("log", new Action<object?>(msg => Diagnostics.Log($"[JS:{site.Key}] {msg}")));
        engine.SetValue("console", new { log = new Action<object?>(msg => Diagnostics.Log($"[JS:{site.Key}] {msg}")), error = new Action<object?>(msg => Diagnostics.Log($"[JS:{site.Key}:ERR] {msg}")) });

        engine.SetValue("base64Encode", new Func<string, string>(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""))));
        engine.SetValue("base64Decode", new Func<string, string>(s =>
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var cleaned = s.Replace('-', '+').Replace('_', '/').Trim();
            while (cleaned.Length % 4 != 0) cleaned += '=';
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(cleaned)); }
            catch { return string.Empty; }
        }));
        engine.SetValue("btoa", new Func<string, string>(s => Convert.ToBase64String(Encoding.Latin1.GetBytes(s ?? ""))));
        engine.SetValue("atob", new Func<string, string>(s =>
        {
            try { return Encoding.Latin1.GetString(Convert.FromBase64String((s ?? "").Trim())); }
            catch { return string.Empty; }
        }));
        engine.SetValue("md5", new Func<string, string>(s => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant()));
        engine.SetValue("sha1", new Func<string, string>(s => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant()));
        engine.SetValue("sha256", new Func<string, string>(s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant()));

        // Synchronous HTTP bridge matching CatVod-JS `req(url, options)` / drpy `request(url, options)`
        Func<string, JsValue?, object> reqBridge = (url, optVal) =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = NativeSiteToolkit.BrowserUserAgent };
            var method = "GET";
            string? body = null;
            var postType = "";
            if (optVal is ObjectInstance obj)
            {
                var m = obj.Get("method");
                if (m.IsString() && !string.IsNullOrWhiteSpace(m.AsString())) method = m.AsString().ToUpperInvariant();
                var pt = obj.Get("postType");
                if (pt.IsString()) postType = pt.AsString();
                var hdr = obj.Get("headers");
                if (hdr is ObjectInstance hdrObj)
                {
                    foreach (var pair in hdrObj.GetOwnProperties())
                        if (pair.Value.Value is { } v && !v.IsUndefined() && !v.IsNull())
                            headers[pair.Key.AsString()] = v.ToString();
                }
                var data = obj.Get("data");
                if (data.IsUndefined() || data.IsNull()) data = obj.Get("body");
                if (!data.IsUndefined() && !data.IsNull())
                {
                    body = data.IsString() ? data.AsString() : JsonSerializer.Serialize(data.ToObject());
                }
            }
            try
            {
                HttpExchangeResult ex;
                if (method == "POST")
                {
                    if (postType.Equals("form", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(body) && !body.TrimStart().StartsWith('{'))
                    {
                        var pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv =>
                        {
                            var idx = kv.IndexOf('=');
                            return idx >= 0
                                ? new KeyValuePair<string, string>(Uri.UnescapeDataString(kv[..idx]), Uri.UnescapeDataString(kv[(idx + 1)..]))
                                : new KeyValuePair<string, string>(Uri.UnescapeDataString(kv), "");
                        });
                        ex = _http.PostFormAsync(url, pairs, headers, cancellationToken: token).GetAwaiter().GetResult();
                    }
                    else
                    {
                        ex = _http.PostJsonAsync(url, body ?? "{}", headers, cancellationToken: token).GetAwaiter().GetResult();
                    }
                }
                else
                {
                    ex = _http.GetExchangeAsync(url, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).GetAwaiter().GetResult();
                }
                var content = SourceHttpClient.DecodeText(ex.Payload.Content);
                var respHeaders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (ex.SetCookies.Count > 0) respHeaders["set-cookie"] = ex.SetCookies.ToArray();
                return new { code = (int)ex.StatusCode, content, headers = respHeaders };
            }
            catch (Exception error)
            {
                return new { code = 500, content = "", headers = new Dictionary<string, object>(), error = error.Message };
            }
        };

        engine.SetValue("req", reqBridge);
        engine.SetValue("request", new Func<string, JsValue?, string>((url, opts) =>
        {
            dynamic res = reqBridge(url, opts);
            return (string)res.content;
        }));
        engine.SetValue("fetch", new Func<string, JsValue?, string>((url, opts) =>
        {
            dynamic res = reqBridge(url, opts);
            return (string)res.content;
        }));

        // drpy2 DOM selector helpers exposed to JS scripts
        engine.SetValue("pdfh", new Func<string, string, string>((html, expr) =>
        {
            var doc = new HtmlParser().ParseDocument(html ?? "");
            return QueryDrpyText(doc, expr, null, false);
        }));
        engine.SetValue("pdfa", new Func<string, string, string[]>((html, expr) =>
        {
            var doc = new HtmlParser().ParseDocument(html ?? "");
            return QueryDrpyList(doc, expr).Select(e => e.OuterHtml).ToArray();
        }));
        engine.SetValue("pd", new Func<string, string, string, string>((html, expr, baseUrl) =>
        {
            var doc = new HtmlParser().ParseDocument(html ?? "");
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri);
            return QueryDrpyText(doc, expr, uri, true);
        }));

        // Persistent local storage matching CatVod-JS `local.get/set/delete`
        engine.SetValue("local", new
        {
            get = new Func<string, string, string>((rkey, key) => _localStorage.GetValueOrDefault($"{rkey}:{key}", "")),
            set = new Action<string, string, string>((rkey, key, val) =>
            {
                _localStorage[$"{rkey}:{key}"] = val ?? "";
                SaveLocalStorage();
            }),
            delete = new Action<string, string>((rkey, key) =>
            {
                _localStorage.TryRemove($"{rkey}:{key}", out _);
                SaveLocalStorage();
            })
        });

        engine.SetValue("__aesEncrypt", new Func<string, string, string, string, string, string>((plain, keyStr, ivStr, mode, pad) =>
        {
            using var aes = Aes.Create();
            aes.Key = NormalizeAesBytes(keyStr, 16);
            aes.Mode = mode.Equals("ECB", StringComparison.OrdinalIgnoreCase) ? CipherMode.ECB : CipherMode.CBC;
            aes.Padding = pad.Equals("ZeroPadding", StringComparison.OrdinalIgnoreCase) ? PaddingMode.Zeros : PaddingMode.PKCS7;
            if (aes.Mode == CipherMode.CBC) aes.IV = NormalizeAesBytes(ivStr, 16);
            var bytes = Encoding.UTF8.GetBytes(plain ?? string.Empty);
            var cipher = aes.CreateEncryptor().TransformFinalBlock(bytes, 0, bytes.Length);
            return Convert.ToBase64String(cipher);
        }));
        engine.SetValue("__aesDecrypt", new Func<string, string, string, string, string, string>((cipherText, keyStr, ivStr, mode, pad) =>
        {
            using var aes = Aes.Create();
            aes.Key = NormalizeAesBytes(keyStr, 16);
            aes.Mode = mode.Equals("ECB", StringComparison.OrdinalIgnoreCase) ? CipherMode.ECB : CipherMode.CBC;
            aes.Padding = pad.Equals("ZeroPadding", StringComparison.OrdinalIgnoreCase) ? PaddingMode.Zeros : PaddingMode.PKCS7;
            if (aes.Mode == CipherMode.CBC) aes.IV = NormalizeAesBytes(ivStr, 16);
            var raw = DecodeCipherBytes(cipherText ?? string.Empty);
            var plain = aes.CreateDecryptor().TransformFinalBlock(raw, 0, raw.Length);
            return Encoding.UTF8.GetString(plain).TrimEnd('\0');
        }));
        engine.SetValue("__hexEncode", new Func<string, string>(s => Convert.ToHexString(Encoding.UTF8.GetBytes(s ?? "")).ToLowerInvariant()));
        engine.SetValue("__hexDecode", new Func<string, string>(s =>
        {
            try { return Encoding.UTF8.GetString(Convert.FromHexString((s ?? "").Trim())); }
            catch { return string.Empty; }
        }));

        // CryptoJS (MD5/SHA1/SHA256/AES-CBC/AES-ECB/Utf8/Base64/Hex) + CommonJS module shims
        engine.Execute("""
            var module = { exports: {} };
            var exports = module.exports;
            function __wordVal(v) { return (v && typeof v === 'object' && v.__v !== undefined) ? String(v.__v) : String(v || ''); }
            var CryptoJS = {
                MD5: function(s) { var h = md5(__wordVal(s)); return { __v: h, toString: function() { return h; } }; },
                SHA1: function(s) { var h = sha1(__wordVal(s)); return { __v: h, toString: function() { return h; } }; },
                SHA256: function(s) { var h = sha256(__wordVal(s)); return { __v: h, toString: function() { return h; } }; },
                mode: { CBC: 'CBC', ECB: 'ECB' },
                pad: { Pkcs7: 'Pkcs7', ZeroPadding: 'ZeroPadding' },
                enc: {
                    Utf8: {
                        parse: function(s) { return { __v: String(s || ''), toString: function() { return String(s || ''); } }; },
                        stringify: function(w) { return __wordVal(w); }
                    },
                    Base64: {
                        parse: function(s) { var d = base64Decode(String(s || '')); return { __v: d, toString: function() { return d; } }; },
                        stringify: function(w) { return base64Encode(__wordVal(w)); }
                    },
                    Hex: {
                        parse: function(s) { var d = __hexDecode(String(s || '')); return { __v: d, toString: function() { return d; } }; },
                        stringify: function(w) { return __hexEncode(__wordVal(w)); }
                    }
                },
                AES: {
                    encrypt: function(data, key, cfg) {
                        cfg = cfg || {};
                        var out = __aesEncrypt(__wordVal(data), __wordVal(key), __wordVal(cfg.iv || ''), String(cfg.mode || 'CBC'), String(cfg.padding || 'Pkcs7'));
                        return { __v: out, ciphertext: { __v: out }, toString: function() { return out; } };
                    },
                    decrypt: function(data, key, cfg) {
                        cfg = cfg || {};
                        var raw = (data && data.ciphertext) ? __wordVal(data.ciphertext) : __wordVal(data);
                        var out = __aesDecrypt(raw, __wordVal(key), __wordVal(cfg.iv || ''), String(cfg.mode || 'CBC'), String(cfg.padding || 'Pkcs7'));
                        return { __v: out, toString: function() { return out; } };
                    }
                }
            };
            function require(name) { return {}; }
            """);

        return engine;
    }

    private static byte[] NormalizeAesBytes(string value, int defaultLength)
    {
        var raw = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var targetLen = raw.Length <= 16 ? 16 : raw.Length <= 24 ? 24 : 32;
        if (raw.Length == targetLen) return raw;
        var padded = new byte[Math.Max(defaultLength, targetLen)];
        Array.Copy(raw, padded, Math.Min(raw.Length, padded.Length));
        return padded;
    }

    private static byte[] DecodeCipherBytes(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length >= 32 && trimmed.Length % 2 == 0 && trimmed.All(Uri.IsHexDigit))
            return Convert.FromHexString(trimmed);
        var b64 = trimmed.Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += '=';
        return Convert.FromBase64String(b64);
    }

    private static string StripJsImports(string source) =>
        EsImportRegex().Replace(source, "");

    private static string PreprocessEsModule(string source)
    {
        var text = EsImportRegex().Replace(source, "");
        text = EsExportDefaultRegex().Replace(text, "module.exports = ");
        text = EsExportFuncRegex().Replace(text, "exports.$1 = function $1");
        text = EsExportAsyncFuncRegex().Replace(text, "exports.$1 = function $1");
        text = EsExportVarRegex().Replace(text, "var ");
        return text;
    }

    private void EnsureLocalStorageLoaded()
    {
        if (_localLoaded) return;
        lock (_localSync)
        {
            if (_localLoaded) return;
            _localLoaded = true;
            try
            {
                var path = Path.Combine(AppPaths.Root, "spider", "js-local.json");
                if (File.Exists(path))
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (map is not null)
                        foreach (var (k, v) in map) _localStorage[k] = v;
                }
            }
            catch { }
        }
    }

    private void SaveLocalStorage()
    {
        lock (_localSync)
        {
            try
            {
                var dir = Path.Combine(AppPaths.Root, "spider");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "js-local.json");
                File.WriteAllText(path, JsonSerializer.Serialize(_localStorage), Encoding.UTF8);
            }
            catch { }
        }
    }

    private static CmsResponse ParseCmsResponse(string raw, SourceSite site)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CmsResponse();
        var response = JsonSerializer.Deserialize<CmsResponse>(raw, JsonUtil.Options) ?? new CmsResponse();
        response.Categories ??= [];
        response.List ??= [];
        response.List.RemoveAll(static item => item is null);
        response.Categories.RemoveAll(static item => item is null);
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Content = PlaybackParsing.StripHtml(item.Content);
            item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
        }
        return response;
    }

    private static string ExtractExtString(JsonElement ext) => ext.ValueKind switch
    {
        JsonValueKind.String => ext.GetString()?.Trim() ?? string.Empty,
        JsonValueKind.Object or JsonValueKind.Array => ext.GetRawText(),
        _ => string.Empty
    };

    [GeneratedRegex(@"(?:^|\n)\s*(?:var|let|const)\s+rule\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex DrpyRuleDeclRegex();

    [GeneratedRegex(@"^(.*?):eq\((-?\d+)\)(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EqSelectorRegex();

    [GeneratedRegex(@"^\s*import\s+[\s\S]*?from\s+['""][^'""]+['""]\s*;?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EsImportRegex();

    [GeneratedRegex(@"\bexport\s+default\s+", RegexOptions.CultureInvariant)]
    private static partial Regex EsExportDefaultRegex();

    [GeneratedRegex(@"\bexport\s+async\s+function\s+([A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex EsExportAsyncFuncRegex();

    [GeneratedRegex(@"\bexport\s+function\s+([A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex EsExportFuncRegex();

    [GeneratedRegex(@"\bexport\s+(?:const|let|var)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex EsExportVarRegex();

    #endregion
}
