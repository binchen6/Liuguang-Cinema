using System.Globalization;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;
using TV.Services.Vod;
using TV.Services.Vod.Native;

namespace TV.Services.Packs;

/// <summary>
/// Executes site adaptation rules installed from Repository Resource Packs (`RepoPackService`).
/// Supports 5 safe, zero-native-code rule kinds:
///   - `alias`: delegates to an existing built-in Native C# provider with remapped API/Ext
///   - `js`: runs a bundled drpy2/CatVod-JS script inside our sandboxed Jint `JsSpiderClient`
///   - `cms`: queries a live MacCMS JSON/XML endpoint with optional category filter & line preference
///   - `bili-preset`: presents curated Bilibili topic/music/audiobook/kids channels via `BilibiliVodProvider`
///   - `info`: shows clean explanatory cards for non-playable utility / Android-ARM-only `.so` entries
/// </summary>
public sealed class RepoPackVodProvider : ICategoryFilterVodProvider, INativePlaybackProvider
{
    private readonly RepoPackService _packs;
    private readonly NativeVodProviderRouter _native;
    private readonly BilibiliVodProvider _bilibili;
    private readonly JsSpiderClient _js;
    private readonly CmsClient _cms;

    public RepoPackVodProvider(
        RepoPackService packs,
        NativeVodProviderRouter native,
        BilibiliVodProvider bilibili,
        JsSpiderClient js,
        CmsClient cms)
    {
        _packs = packs;
        _native = native;
        _bilibili = bilibili;
        _js = js;
        _cms = cms;
    }

    public bool CanHandle(SourceSite site) => _packs.TryFindRule(site, overrideUniversalOnly: false, out _, out _);

    public bool CanOverrideUniversal(SourceSite site) => _packs.TryFindRule(site, overrideUniversalOnly: true, out _, out _);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (pack, rule) = RequireRule(site);
        return rule.Kind.ToLowerInvariant() switch
        {
            "alias" => await DispatchAliasHomeAsync(site, rule, cancellationToken).ConfigureAwait(false),
            "js" => await _js.ExecuteInlineHomeAsync(site, ReadBundledScript(pack, rule), cancellationToken).ConfigureAwait(false),
            "cms" => await DispatchCmsHomeAsync(site, rule, cancellationToken).ConfigureAwait(false),
            "bili-preset" => await DispatchBiliPresetHomeAsync(site, rule, cancellationToken).ConfigureAwait(false),
            "info" => CreateInfoResponse(site, rule),
            _ => throw new NotSupportedException($"资源包规则类型不受支持：{rule.Kind}")
        };
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken)
    {
        var (pack, rule) = RequireRule(site);
        return rule.Kind.ToLowerInvariant() switch
        {
            "alias" => await DispatchAliasCategoryAsync(site, rule, categoryId, page, filters, cancellationToken).ConfigureAwait(false),
            "js" => await _js.ExecuteInlineCategoryAsync(site, ReadBundledScript(pack, rule), categoryId, page, filters, cancellationToken).ConfigureAwait(false),
            "cms" => await _cms.GetCategoryAsync(CreateCmsAlias(site, rule), string.IsNullOrWhiteSpace(categoryId) ? rule.TypeFilter : categoryId, page, cancellationToken).ConfigureAwait(false),
            "bili-preset" => await _bilibili.GetCategoryAsync(site, ResolveBiliPresetTopic(site, rule, categoryId), page, filters, cancellationToken).ConfigureAwait(false),
            "info" => CreateInfoResponse(site, rule),
            _ => throw new NotSupportedException($"资源包规则类型不受支持：{rule.Kind}")
        };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var (pack, rule) = RequireRule(site);
        return rule.Kind.ToLowerInvariant() switch
        {
            "alias" => await DispatchAliasSearchAsync(site, rule, keyword, page, cancellationToken).ConfigureAwait(false),
            "js" => await _js.ExecuteInlineSearchAsync(site, ReadBundledScript(pack, rule), keyword, page, cancellationToken).ConfigureAwait(false),
            "cms" => await _cms.SearchAsync(CreateCmsAlias(site, rule), keyword, page, cancellationToken).ConfigureAwait(false),
            "bili-preset" => await _bilibili.SearchAsync(site, keyword, page, cancellationToken).ConfigureAwait(false),
            "info" => new CmsResponse { Page = page, PageCount = 1 },
            _ => new CmsResponse { Page = page, PageCount = 1 }
        };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var (pack, rule) = RequireRule(site);
        return rule.Kind.ToLowerInvariant() switch
        {
            "alias" => await DispatchAliasDetailAsync(site, rule, id, cancellationToken).ConfigureAwait(false),
            "js" => await _js.ExecuteInlineDetailAsync(site, ReadBundledScript(pack, rule), id, cancellationToken).ConfigureAwait(false),
            "cms" => await _cms.GetDetailAsync(CreateCmsAlias(site, rule), id, cancellationToken).ConfigureAwait(false),
            "bili-preset" => await _bilibili.GetDetailAsync(site, id, cancellationToken).ConfigureAwait(false),
            "info" => new VodItem
            {
                Id = id, SiteKey = site.Key, SiteName = site.Name,
                Name = string.IsNullOrWhiteSpace(rule.Title) ? site.Name : rule.Title,
                Content = rule.Message
            },
            _ => throw new NotSupportedException($"资源包规则类型不受支持：{rule.Kind}")
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var (pack, rule) = RequireRule(site);
        switch (rule.Kind.ToLowerInvariant())
        {
            case "alias":
                var aliased = CreateAliasedSite(site, rule);
                if (_native.TryGet(aliased, out var native) && native is INativePlaybackProvider playback)
                    return await playback.ResolvePlayAsync(aliased, flag, id, token).ConfigureAwait(false);
                return new SpiderPlayResult(id, NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders()), PlayUrlResolver.IsDirectMediaUrl(id) ? 0 : 1, 0);
            case "js":
                return await _js.ExecuteInlinePlayAsync(site, ReadBundledScript(pack, rule), flag, id, token).ConfigureAwait(false);
            case "cms":
                return new SpiderPlayResult(id, NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders()), PlayUrlResolver.IsDirectMediaUrl(id) ? 0 : 1, 0);
            case "bili-preset":
                return await _bilibili.ResolvePlayAsync(site, flag, id, token).ConfigureAwait(false);
            default:
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(rule.Message) ? "该条目无可播放流。" : rule.Message);
        }
    }

    private (InstalledRepoPack Pack, RepoPackSiteRule Rule) RequireRule(SourceSite site) =>
        _packs.TryFindRule(site, overrideUniversalOnly: false, out var pack, out var rule)
            ? (pack, rule)
            : throw new InvalidOperationException($"未找到站点 {site.Name} 的资源包规则。");

    private static string ReadBundledScript(InstalledRepoPack pack, RepoPackSiteRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ScriptFile))
            throw new InvalidDataException("资源包 JS 规则缺少 scriptFile。");
        var root = Path.GetFullPath(pack.DirectoryPath);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, rule.ScriptFile));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException("资源包脚本文件不存在或路径越界。", rule.ScriptFile);
        return File.ReadAllText(full, Encoding.UTF8);
    }

    private static SourceSite CreateAliasedSite(SourceSite original, RepoPackSiteRule rule) => new()
    {
        Key = original.Key,
        Name = original.Name,
        Type = 3,
        Api = rule.AliasApi,
        Ext = rule.AliasExt.ValueKind != JsonValueKind.Undefined ? rule.AliasExt : original.Ext,
        Header = original.Header,
        Timeout = original.Timeout,
        Searchable = original.Searchable,
        QuickSearch = original.QuickSearch,
        Filterable = original.Filterable
    };

    private static SourceSite CreateCmsAlias(SourceSite original, RepoPackSiteRule rule) => new()
    {
        Key = original.Key,
        Name = original.Name,
        Type = 1,
        Api = rule.Endpoint,
        Header = original.Header,
        Timeout = original.Timeout
    };

    private Task<CmsResponse> DispatchAliasHomeAsync(SourceSite site, RepoPackSiteRule rule, CancellationToken token)
    {
        var aliased = CreateAliasedSite(site, rule);
        return _native.TryGet(aliased, out var native)
            ? native.GetHomeAsync(aliased, token)
            : throw new InvalidOperationException($"资源包别名目标不可用：{rule.AliasApi}");
    }

    private Task<CmsResponse> DispatchAliasCategoryAsync(SourceSite site, RepoPackSiteRule rule, string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken token)
    {
        var aliased = CreateAliasedSite(site, rule);
        if (!_native.TryGet(aliased, out var native))
            throw new InvalidOperationException($"资源包别名目标不可用：{rule.AliasApi}");
        return native is ICategoryFilterVodProvider filtered
            ? filtered.GetCategoryAsync(aliased, categoryId, page, filters, token)
            : native.GetCategoryAsync(aliased, categoryId, page, token);
    }

    private Task<CmsResponse> DispatchAliasSearchAsync(SourceSite site, RepoPackSiteRule rule, string keyword, int page, CancellationToken token)
    {
        var aliased = CreateAliasedSite(site, rule);
        return _native.TryGet(aliased, out var native)
            ? native.SearchAsync(aliased, keyword, page, token)
            : Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });
    }

    private Task<VodItem> DispatchAliasDetailAsync(SourceSite site, RepoPackSiteRule rule, string id, CancellationToken token)
    {
        var aliased = CreateAliasedSite(site, rule);
        return _native.TryGet(aliased, out var native)
            ? native.GetDetailAsync(aliased, id, token)
            : throw new InvalidOperationException($"资源包别名目标不可用：{rule.AliasApi}");
    }

    private async Task<CmsResponse> DispatchCmsHomeAsync(SourceSite site, RepoPackSiteRule rule, CancellationToken token)
    {
        var cmsSite = CreateCmsAlias(site, rule);
        if (!string.IsNullOrWhiteSpace(rule.TypeFilter))
        {
            var resp = await _cms.GetCategoryAsync(cmsSite, rule.TypeFilter, 1, token).ConfigureAwait(false);
            if (rule.Categories.Count > 0)
                resp.Categories = rule.Categories.Select(c => new VodCategory { Id = c.Id, Name = c.Name }).ToList();
            return resp;
        }
        return await _cms.GetHomeAsync(cmsSite, token).ConfigureAwait(false);
    }

    private async Task<CmsResponse> DispatchBiliPresetHomeAsync(SourceSite site, RepoPackSiteRule rule, CancellationToken token)
    {
        var categories = BuildBiliPresetCategories(site, rule);
        var seedTopic = !string.IsNullOrWhiteSpace(rule.DefaultKeyword)
            ? rule.DefaultKeyword
            : (categories.Count > 0 ? categories[0].Id : site.Name);
        var resp = await _bilibili.GetCategoryAsync(site, seedTopic, 1, null, token).ConfigureAwait(false);
        resp.Categories = categories;
        resp.HomeCategoryName = categories.Count > 0 ? categories[0].Name : "精选推荐";
        return resp;
    }

    private static List<VodCategory> BuildBiliPresetCategories(SourceSite site, RepoPackSiteRule rule)
    {
        var list = new List<VodCategory>();
        // Respect site.Ext.categories (e.g. Music24bit's ["周杰伦", "林俊杰", ...]) when present!
        if (site.Ext.ValueKind == JsonValueKind.Object &&
            site.Ext.TryGetProperty("categories", out var extCats) && extCats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extCats.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                {
                    var kw = string.IsNullOrWhiteSpace(rule.DefaultKeyword) ? s.Trim() : $"{s.Trim()} {rule.DefaultKeyword}";
                    list.Add(new VodCategory { Id = kw, Name = s.Trim() });
                }
            }
        }
        foreach (var preset in rule.Categories)
        {
            if (!list.Any(c => c.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add(new VodCategory { Id = preset.Id, Name = preset.Name });
        }
        return list;
    }

    private static string ResolveBiliPresetTopic(SourceSite site, RepoPackSiteRule rule, string categoryId)
    {
        if (!string.IsNullOrWhiteSpace(categoryId)) return categoryId;
        var cats = BuildBiliPresetCategories(site, rule);
        return cats.Count > 0 ? cats[0].Id : (!string.IsNullOrWhiteSpace(rule.DefaultKeyword) ? rule.DefaultKeyword : site.Name);
    }

    private static CmsResponse CreateInfoResponse(SourceSite site, RepoPackSiteRule rule) => new()
    {
        HomeCategoryName = "站点说明",
        Categories = [new VodCategory { Id = "info", Name = "说明" }],
        List =
        [
            new VodItem
            {
                Id = "pack-info:" + site.Key,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = string.IsNullOrWhiteSpace(rule.Title) ? site.Name : rule.Title,
                Remarks = "桌面提示",
                Content = rule.Message
            }
        ],
        Page = 1,
        PageCount = 1,
        Total = 1
    };
}
