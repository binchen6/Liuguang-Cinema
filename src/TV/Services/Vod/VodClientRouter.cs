using TV.Models;
using TV.Services.Spider;
using TV.Services.Vod.Native;

namespace TV.Services.Vod;

public sealed class VodClientRouter
{
    private readonly CmsClient _cms;
    private readonly SpiderVodClient _spider;
    private readonly NativeVodProviderRouter _native;
    private readonly JsSpiderClient? _js;
    private readonly PySpiderClient? _py;

    public VodClientRouter(CmsClient cms, SpiderVodClient spider, NativeVodProviderRouter native,
        JsSpiderClient? js = null, PySpiderClient? py = null)
    {
        _cms = cms;
        _spider = spider;
        _native = native;
        _js = js;
        _py = py;
    }

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        _native.TryGet(site, out var native) ? native.GetHomeAsync(site, cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.GetHomeAsync(site, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.GetHomeAsync(site, cancellationToken)
            : site.Type == 3 ? _spider.GetHomeAsync(site, cancellationToken)
            : _cms.GetHomeAsync(site, cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken) =>
        _native.TryGet(site, out var native)
            ? native is ICategoryFilterVodProvider filtered
                ? filtered.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
                : native.GetCategoryAsync(site, categoryId, page, cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
            : site.Type == 3 ? _spider.GetCategoryAsync(site, categoryId, page, filters, cancellationToken)
            : _cms.GetCategoryAsync(site, categoryId, page, cancellationToken);

    internal bool CanFilterCategory(SourceSite site) => _native.TryGet(site, out var native)
        ? native is ICategoryFilterVodProvider : site.Type == 3;

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page = 1, CancellationToken cancellationToken = default) =>
        _native.TryGet(site, out var native) ? native.SearchAsync(site, keyword, page, cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.SearchAsync(site, keyword, page, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.SearchAsync(site, keyword, page, cancellationToken)
            : site.Type == 3 ? _spider.SearchAsync(site, keyword, page, cancellationToken)
            : _cms.SearchAsync(site, keyword, page, cancellationToken);

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default) =>
        _native.TryGet(site, out var native) ? native.GetDetailAsync(site, id, cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.GetDetailAsync(site, id, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.GetDetailAsync(site, id, cancellationToken)
            : site.Type == 3 ? _spider.GetDetailAsync(site, id, cancellationToken)
            : _cms.GetDetailAsync(site, id, cancellationToken);

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, string? cloudProvider = null, CancellationToken cancellationToken = default) =>
        _native.TryGet(site, out var native) && native is INativePlaybackProvider playback
            ? playback.ResolvePlayAsync(site, flag, id, cancellationToken)
            : _js is not null && JsSpiderClient.Supports(site) ? _js.ResolvePlayAsync(site, flag, id, cancellationToken)
            : _py is not null && PySpiderClient.Supports(site) ? _py.ResolvePlayAsync(site, flag, id, cancellationToken)
            : _spider.ResolvePlayAsync(site, flag, id, cloudProvider, cancellationToken);

    internal Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, QualityOption quality,
        string? cloudProvider, bool forDownload, CancellationToken cancellationToken) =>
        _native.TryGet(site, out var native) && native is IQualityVodProvider selectable
            ? selectable.ResolvePlayAsync(site, flag, id, quality, forDownload, cancellationToken)
            : ResolvePlayAsync(site, flag, id, cloudProvider, cancellationToken);
}
