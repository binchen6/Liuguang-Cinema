using TV.Models;
using TV.Services.Cloud;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native handler for 🛴手机┃推送 (csp_Push / csp_PushGuard) and empty-ext placeholder notices (e.g. cc / csp_XPathGuard without ext).
/// Eliminates noisy InvalidDataException/InvalidOperationException on catalog probes while supporting direct URL & cloud-share playback.
/// </summary>
public sealed class PushVodProvider(CloudDriveService cloud) : INativePlaybackProvider
{
    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_PushGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Push", StringComparison.OrdinalIgnoreCase) ||
         (site.Api.Equals("csp_XPathGuard", StringComparison.OrdinalIgnoreCase) || site.Api.Equals("csp_XPath", StringComparison.OrdinalIgnoreCase)) &&
         (site.Ext.ValueKind == System.Text.Json.JsonValueKind.Undefined ||
          site.Ext.ValueKind == System.Text.Json.JsonValueKind.Null ||
          site.Ext.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrWhiteSpace(site.Ext.GetString())));

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse { Page = 1, PageCount = 1 });

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        if (CloudShareLink.TryParse(id, out _))
            return CloudSearchDetail.ReadAsync(cloud, site, id, id, null, cancellationToken);

        var url = id.Trim();
        var item = new VodItem
        {
            Id = url,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = url.Length > 0 ? url : "推送播放",
            PlayFrom = "直连$$$嗅探",
            PlayUrl = $"播放${url}$$$播放${url}"
        };
        return Task.FromResult(item);
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parse = flag.Contains("嗅探", StringComparison.Ordinal) ? 1 : 0;
        return Task.FromResult(new SpiderPlayResult(id, new Dictionary<string, string>(), parse, 0));
    }
}
