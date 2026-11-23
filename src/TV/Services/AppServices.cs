using TV.Services.AI;
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
using TV.Services.Updates;

namespace TV.Services;

public sealed class AppServices : IDisposable
{
    private readonly Lazy<PlayerService> _player;
    private readonly Lazy<DnsResolverService> _dns;
    private readonly Lazy<SiteSessionStore> _siteSessions;
    private readonly Lazy<SourceHttpClient> _http;
    private readonly Lazy<BilibiliAccountService> _bilibiliAccount;
    private readonly Lazy<BilibiliVodProvider> _bilibili;
    private readonly Lazy<DanmakuService> _danmaku;
    private readonly Lazy<DanDanPlayService> _danDanPlay;
    private readonly Lazy<OnlineMediaService> _onlineMedia;
    private readonly Lazy<BailianSubtitleService> _bailianSubtitle;
    private readonly Lazy<RealtimeSubtitleService> _realtimeSubtitle;

    public AppServices(bool playerOnly = false)
    {
        PlayerOnly = playerOnly;
        AppPaths.EnsureCreated();
        Database = new DatabaseService();
        _dns = new Lazy<DnsResolverService>(() => new DnsResolverService(Database));
        _siteSessions = new Lazy<SiteSessionStore>(() => new SiteSessionStore(Database));
        _http = new Lazy<SourceHttpClient>(() => new SourceHttpClient(_dns.Value, _siteSessions.Value));
        _bilibiliAccount = new Lazy<BilibiliAccountService>(() => new BilibiliAccountService(Database, _dns.Value));
        _danmaku = new Lazy<DanmakuService>(() => new DanmakuService(_http.Value));
        _danDanPlay = new Lazy<DanDanPlayService>(() => new DanDanPlayService(_http.Value, Database));
        _bailianSubtitle = new Lazy<BailianSubtitleService>(() => new BailianSubtitleService(Database, _dns.Value));
        _realtimeSubtitle = new Lazy<RealtimeSubtitleService>(() => new RealtimeSubtitleService(_bailianSubtitle.Value));
        _player = new Lazy<PlayerService>(() => new PlayerService());
        if (playerOnly)
        {
            _bilibili = new Lazy<BilibiliVodProvider>(() => new BilibiliVodProvider(_http.Value, null, _bilibiliAccount.Value));
            _onlineMedia = new Lazy<OnlineMediaService>(() => new OnlineMediaService(_http.Value, _bilibili.Value));
            Playback = new PlaybackResolver(this);
            return;
        }
        var dns = _dns.Value;
        var http = _http.Value;
        var bilibiliAccount = _bilibiliAccount.Value;
        Config = new SourceConfigService(http, Database);
        Cms = new CmsClient(http);
        SpiderRuntime = new SpiderRuntime(Database);
        Cloud = new CloudDriveService(Database, dns);
        CloudLogin = new CloudLoginCoordinator(Cloud, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        var bilibili = new BilibiliVodProvider(http, Cloud, bilibiliAccount);
        _bilibili = new Lazy<BilibiliVodProvider>(() => bilibili);
        NativeVod = NativeVodProviderRouter.CreateDefault(http, Cloud, bilibili);
        Vod = new VodClientRouter(Cms, new SpiderVodClient(SpiderRuntime), NativeVod);
        Live = new LiveService(http);
        Epg = new EpgService(http);
        Douban = new DoubanService(http, Database);
        Images = new ImageCacheService(http, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), dns);
        Updates = new AppUpdateService(http, dns, Database);
        Resolver = new PlayUrlResolver(http);
        Sniffer = new SniffService(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        Playback = new PlaybackResolver(this);
        SourceRecovery = new SourceRecoveryService(this);
        Downloads = new VideoDownloadService(dns);
        _onlineMedia = new Lazy<OnlineMediaService>(() => new OnlineMediaService(http, bilibili));
        LocalMedia = new LocalMediaService(Database);
        Cleanup = new CleanupService(Database);
    }

    public bool PlayerOnly { get; }
    public SourceHttpClient Http => _http.Value;
    public SiteSessionStore SiteSessions => _siteSessions.Value;
    public DatabaseService Database { get; }
    public DnsResolverService Dns => _dns.Value;
    public SourceConfigService Config { get; } = null!;
    public CmsClient Cms { get; } = null!;
    public VodClientRouter Vod { get; } = null!;
    public SpiderRuntime SpiderRuntime { get; } = null!;
    public CloudDriveService Cloud { get; } = null!;
    public BilibiliAccountService BilibiliAccount => _bilibiliAccount.Value;
    public CloudLoginCoordinator CloudLogin { get; } = null!;
    public TV.Services.Vod.Native.NativeVodProviderRouter NativeVod { get; } = null!;
    public LiveService Live { get; } = null!;
    public EpgService Epg { get; } = null!;
    public DoubanService Douban { get; } = null!;
    public ImageCacheService Images { get; } = null!;
    public AppUpdateService Updates { get; } = null!;
    public PlayUrlResolver Resolver { get; } = null!;
    public SniffService Sniffer { get; } = null!;
    public PlaybackResolver Playback { get; }
    public SourceRecoveryService SourceRecovery { get; } = null!;
    public VideoDownloadService Downloads { get; } = null!;
    public DanmakuService Danmaku => _danmaku.Value;
    public DanDanPlayService DanDanPlay => _danDanPlay.Value;
    public BailianSubtitleService BailianSubtitle => _bailianSubtitle.Value;
    public RealtimeSubtitleService RealtimeSubtitle => _realtimeSubtitle.Value;
    public BilibiliVodProvider Bilibili => _bilibili.Value;
    public OnlineMediaService OnlineMedia => _onlineMedia.Value;
    public LocalMediaService LocalMedia { get; } = null!;
    public CleanupService Cleanup { get; } = null!;
    public PlayerService Player => _player.Value;
    public Task StopPlayerAsync() => _player.IsValueCreated ? _player.Value.StopAsync() : Task.CompletedTask;

    public void Dispose()
    {
        if (_realtimeSubtitle.IsValueCreated) _realtimeSubtitle.Value.Dispose();
        if (_bailianSubtitle.IsValueCreated) _bailianSubtitle.Value.Dispose();
        if (_player.IsValueCreated) _player.Value.Dispose();
        if (!PlayerOnly)
        {
            Images.Dispose();
            Updates.Dispose();
            Downloads.Dispose();
            LocalMedia.Dispose();
            CloudLogin.Dispose();
            Cloud.Dispose();
            Config.Dispose();
            SpiderRuntime.Dispose();
        }
        if (_bilibiliAccount.IsValueCreated) _bilibiliAccount.Value.Dispose();
        if (_http.IsValueCreated) _http.Value.Dispose();
        if (_dns.IsValueCreated) _dns.Value.Dispose();
    }
}
