using TV.Services.Cloud;
using TV.Services.Configuration;
using TV.Services.Http;
using TV.Services.Imaging;
using TV.Services.Live;
using TV.Services.Library;
using TV.Services.Maintenance;
using TV.Services.Metadata;
using TV.Services.Persistence;
using TV.Services.Playback;
using TV.Services.Spider;
using TV.Services.Vod;
using TV.Services.Vod.Native;

namespace TV.Services;

public sealed class AppServices : IDisposable
{
    private readonly Lazy<PlayerService> _player;

    public AppServices(bool playerOnly = false)
    {
        PlayerOnly = playerOnly;
        AppPaths.EnsureCreated();
        Database = new DatabaseService();
        Dns = new DnsResolverService(Database);
        Http = new SourceHttpClient(Dns);
        if (playerOnly)
        {
            BilibiliAccount = new BilibiliAccountService(Database, Dns);
            Bilibili = new BilibiliVodProvider(Http, null, BilibiliAccount);
            Playback = new PlaybackResolver(this);
            Danmaku = new DanmakuService(Http);
            OnlineMedia = new OnlineMediaService(Http, Bilibili);
            _player = new Lazy<PlayerService>(() => new PlayerService());
            return;
        }
        Config = new SourceConfigService(Http, Database);
        Cms = new CmsClient(Http);
        SpiderRuntime = new SpiderRuntime(Database);
        Cloud = new CloudDriveService(Database, Dns);
        BilibiliAccount = new BilibiliAccountService(Database, Dns);
        CloudLogin = new CloudLoginCoordinator(Cloud, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        Bilibili = new BilibiliVodProvider(Http, Cloud, BilibiliAccount);
        NativeVod = new NativeVodProviderRouter([new WoggNativeProvider(Http, Cloud), new HttpSpiderProvider(Http),
            new HtmlVodProvider(Http), Bilibili, new ChangZhangVodProvider(Http),
            new PanSouVodProvider(Http, Cloud), new PanTaVodProvider(Http, Cloud), new KanqiuVodProvider(Http), new MyDriveVodProvider(Cloud)]);
        Vod = new VodClientRouter(Cms, new SpiderVodClient(SpiderRuntime), NativeVod);
        Live = new LiveService(Http);
        Epg = new EpgService(Http);
        Douban = new DoubanService(Http, Database);
        Images = new ImageCacheService(Http);
        Resolver = new PlayUrlResolver(Http);
        Sniffer = new SniffService(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        Playback = new PlaybackResolver(this);
        SourceRecovery = new SourceRecoveryService(this);
        Downloads = new VideoDownloadService(Dns);
        Danmaku = new DanmakuService(Http);
        LocalMedia = new LocalMediaService(Database);
        Cleanup = new CleanupService(Database);
        OnlineMedia = new OnlineMediaService(Http, Bilibili);
        _player = new Lazy<PlayerService>(() => new PlayerService());
    }

    public bool PlayerOnly { get; }
    public SourceHttpClient Http { get; }
    public DatabaseService Database { get; }
    public DnsResolverService Dns { get; }
    public SourceConfigService Config { get; } = null!;
    public CmsClient Cms { get; } = null!;
    public VodClientRouter Vod { get; } = null!;
    public SpiderRuntime SpiderRuntime { get; } = null!;
    public CloudDriveService Cloud { get; } = null!;
    public BilibiliAccountService BilibiliAccount { get; }
    public CloudLoginCoordinator CloudLogin { get; } = null!;
    public TV.Services.Vod.Native.NativeVodProviderRouter NativeVod { get; } = null!;
    public LiveService Live { get; } = null!;
    public EpgService Epg { get; } = null!;
    public DoubanService Douban { get; } = null!;
    public ImageCacheService Images { get; } = null!;
    public PlayUrlResolver Resolver { get; } = null!;
    public SniffService Sniffer { get; } = null!;
    public PlaybackResolver Playback { get; }
    public SourceRecoveryService SourceRecovery { get; } = null!;
    public VideoDownloadService Downloads { get; } = null!;
    public DanmakuService Danmaku { get; }
    public BilibiliVodProvider Bilibili { get; }
    public OnlineMediaService OnlineMedia { get; }
    public LocalMediaService LocalMedia { get; } = null!;
    public CleanupService Cleanup { get; } = null!;
    public PlayerService Player => _player.Value;
    public Task StopPlayerAsync() => _player.IsValueCreated ? _player.Value.StopAsync() : Task.CompletedTask;

    public void Dispose()
    {
        if (_player.IsValueCreated) _player.Value.Dispose();
        if (PlayerOnly)
        {
            BilibiliAccount.Dispose();
            return;
        }
        Downloads.Dispose();
        LocalMedia.Dispose();
        CloudLogin.Dispose();
        Cloud.Dispose();
        BilibiliAccount.Dispose();
        Config.Dispose();
        SpiderRuntime.Dispose();
    }
}
