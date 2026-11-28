using System.Runtime.CompilerServices;
using TV.Models;
using TV.Services.Packs;
using TV.Services.Spider;
using TV.Services.Vod.Native;

namespace TV.Services.Vod;

/// <summary>
/// Strict priority routing hierarchy:
///   1. Native C# Providers + Universal Protocols (`_native`: Native > AppYs/AList/360Kan/CloudCatalog)
///   2. Universal Script Runners (`_js`, `_py`)
///   3. RepoPack Explicit Override Rules (`_repoPack` when `overrideUniversal == true`, e.g. custom DOM like m.ting15.com)
///   4. Universal Auto-HTML / MacCMS Fallback (`_autoHtml` for any csp_* with plaintext HTTP URLs)
///   5. RepoPack Dedicated Rules (`_repoPack` for repo-specific dedicated adaptation)
///   6. Java CatVod Engine / External JAR (`_spider`) or Standard CMS (`_cms`)
/// </summary>
public sealed class VodClientRouter
{
    private readonly CmsClient _cms;
    private readonly SpiderVodClient _spider;
    private readonly NativeVodProviderRouter _native;
    private readonly JsSpiderClient? _js;
    private readonly PySpiderClient? _py;
    private readonly UniversalAutoHtmlVodProvider? _autoHtml;
    private readonly RepoPackVodProvider? _repoPack;
    private readonly ConditionalWeakTable<SourceSite, RepoPackVodProvider> _packFallbacks = new();

    public VodClientRouter(
        CmsClient cms,
        SpiderVodClient spider,
        NativeVodProviderRouter native,
        JsSpiderClient? js = null,
        PySpiderClient? py = null,
        UniversalAutoHtmlVodProvider? autoHtml = null,
        RepoPackVodProvider? repoPack = null)
    {
        _cms = cms;
        _spider = spider;
        _native = native;
        _js = js;
        _py = py;
        _autoHtml = autoHtml;
        _repoPack = repoPack;
    }

    private bool TryResolveProvider(SourceSite site, out INativeVodProvider provider)
    {
        // Priority 1: Native C# & Universal Protocol Providers (except when a RepoPack explicitly overrides a dead endpoint)
        if (_native.TryGet(site, out var native))
        {
            if (native is UniversalProtocolVodProvider && _repoPack is not null && _repoPack.CanOverrideUniversal(site))
            {
                provider = _repoPack;
                return true;
            }
            provider = native;
            return true;
        }
        // Priority 2: Script runners are handled directly in dispatch methods before autoHtml/repoPack
        if ((_js is not null && JsSpiderClient.Supports(site)) || (_py is not null && PySpiderClient.Supports(site)))
        {
            provider = null!;
            return false;
        }
        // Priority 3: RepoPack explicit override for non-standard DOM sites
        if (_repoPack is not null && _repoPack.CanOverrideUniversal(site))
        {
            provider = _repoPack;
            return true;
        }
        if (_packFallbacks.TryGetValue(site, out var previousFallback) && previousFallback.CanHandle(site))
        {
            provider = previousFallback;
            return true;
        }
        // Priority 4: Universal Auto-HTML / MacCMS adapter (works across all repos without any pack)
        if (_autoHtml is not null && _autoHtml.CanHandle(site))
        {
            provider = _autoHtml;
            return true;
        }
        // Priority 5: RepoPack dedicated adaptation rules
        if (_repoPack is not null && _repoPack.CanHandle(site))
        {
            provider = _repoPack;
            return true;
        }
        provider = null!;
        return false;
    }

    private async Task<T> DispatchAsync<T>(SourceSite site, INativeVodProvider provider,
        Func<INativeVodProvider, Task<T>> operation, CancellationToken token)
    {
        try { return await operation(provider).ConfigureAwait(false); }
        catch (SiteAdapterMismatchException) when (!token.IsCancellationRequested &&
            ReferenceEquals(provider, _autoHtml) && _repoPack is not null && _repoPack.CanHandle(site))
        {
            var result = await operation(_repoPack).ConfigureAwait(false);
            _packFallbacks.AddOrUpdate(site, _repoPack);
            return result;
        }
    }

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        TryResolveProvider(site, out var native) ? DispatchAsync(site, native, p => p.GetHomeAsync(site, cancellationToken), cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.GetHomeAsync(site, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.GetHomeAsync(site, cancellationToken)
            : site.Type == 3 ? _spider.GetHomeAsync(site, cancellationToken)
            : _cms.GetHomeAsync(site, cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken) =>
        TryResolveProvider(site, out var native)
            ? DispatchAsync(site, native, p => p is ICategoryFilterVodProvider filtered
                ? filtered.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
                : p.GetCategoryAsync(site, categoryId, page, cancellationToken), cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
            : site.Type == 3 ? _spider.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
            : _cms.GetCategoryAsync(site, categoryId, page, cancellationToken);

    internal bool CanFilterCategory(SourceSite site) => TryResolveProvider(site, out var native)
        ? native is ICategoryFilterVodProvider : site.Type == 3;

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page = 1, CancellationToken cancellationToken = default) =>
        TryResolveProvider(site, out var native) ? DispatchAsync(site, native, p => p.SearchAsync(site, keyword, page, cancellationToken), cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.SearchAsync(site, keyword, page, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.SearchAsync(site, keyword, page, cancellationToken)
            : site.Type == 3 ? _spider.SearchAsync(site, keyword, page, cancellationToken)
            : _cms.SearchAsync(site, keyword, page, cancellationToken);

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default) =>
        TryResolveProvider(site, out var native) ? DispatchAsync(site, native, p => p.GetDetailAsync(site, id, cancellationToken), cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.GetDetailAsync(site, id, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.GetDetailAsync(site, id, cancellationToken)
            : site.Type == 3 ? _spider.GetDetailAsync(site, id, cancellationToken)
            : _cms.GetDetailAsync(site, id, cancellationToken);

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, string? cloudProvider = null, CancellationToken cancellationToken = default) =>
        TryResolveProvider(site, out var native) && native is INativePlaybackProvider playback
            ? playback.ResolvePlayAsync(site, flag, id, cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.ResolvePlayAsync(site, flag, id, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.ResolvePlayAsync(site, flag, id, cancellationToken)
            : _spider.ResolvePlayAsync(site, flag, id, cloudProvider, cancellationToken);

    internal Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, QualityOption quality,
        string? cloudProvider, bool forDownload, CancellationToken cancellationToken) =>
        TryResolveProvider(site, out var native) && native is IQualityVodProvider selectable
            ? selectable.ResolvePlayAsync(site, flag, id, quality, forDownload, cancellationToken)
            : ResolvePlayAsync(site, flag, id, cloudProvider, cancellationToken);
}
