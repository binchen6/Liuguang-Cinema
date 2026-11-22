using TV.Models;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public interface INativeVodProvider
{
    bool CanHandle(SourceSite site);
    Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default);
    Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default);
    Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default);
    Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default);
}

public interface INativePlaybackProvider : INativeVodProvider
{
    Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token);
}

public interface ICategoryFilterVodProvider : INativeVodProvider
{
    Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken);
}

public interface IQualityVodProvider : INativePlaybackProvider
{
    Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, QualityOption quality, bool forDownload, CancellationToken token);
}

public sealed class NativeVodProviderRouter
{
    private readonly IReadOnlyList<INativeVodProvider> _providers;
    public NativeVodProviderRouter(IEnumerable<INativeVodProvider> providers) => _providers = providers.ToArray();

    public static NativeVodProviderRouter CreateDefault(
        Http.SourceHttpClient http,
        Cloud.CloudDriveService cloud,
        BilibiliVodProvider bilibili) =>
        new([
            new WoggNativeProvider(http, cloud),
            new HttpSpiderProvider(http),
            new HtmlVodProvider(http),
            bilibili,
            new ChangZhangVodProvider(http),
            new DoubanVodProvider(http),
            new YgpVodProvider(http),
            new Xb6vVodProvider(http),
            new LibvioVodProvider(http),
            new JpysVodProvider(http),
            new GuaziVodProvider(http),
            new GimyVodProvider(http),
            new HanjuVodProvider(http),
            new ReboVodProvider(http, cloud),
            new SeedhubVodProvider(http, cloud),
            new PanSouVodProvider(http, cloud),
            new PanTaVodProvider(http, cloud),
            new PanSearchVodProvider(http, cloud),
            new KanqiuVodProvider(http),
            new PushVodProvider(cloud),
            new MyDriveVodProvider(cloud)
        ]);

    public static bool IsSearchOnly(SourceSite site) =>
        PanSouVodProvider.Supports(site) || PanTaVodProvider.Supports(site) || PanSearchVodProvider.Supports(site);
    public bool TryGet(SourceSite site, out INativeVodProvider provider)
    {
        provider = _providers.FirstOrDefault(item => item.CanHandle(site))!;
        return provider is not null;
    }
}
