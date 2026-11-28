using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Configuration;
using TV.Services.Http;
using TV.Services.Live;
using TV.Services.Packs;
using TV.Services.Persistence;
using TV.Services.Spider;
using TV.Services.Vod;
using TV.Services.Vod.Native;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: site-probe <config path or -> <probe profile> [site keys] [--import=<url>] [--live-channel=<name>] [--vod-id=<id>] [--decode-sample]");
    return 2;
}

Console.OutputEncoding = new UTF8Encoding(false);
Environment.SetEnvironmentVariable("TV_DATA_ROOT", Path.GetFullPath(args[1]));
AppPaths.EnsureCreated();
var database = new DatabaseService();
await database.SetSettingAsync("source.autoRefresh", "false");
var dns = new DnsResolverService(database);
var http = new SourceHttpClient(dns);
using var config = new SourceConfigService(http, database);
using var repoPacks = new RepoPackService(http, dns);
if (args[0] != "-")
    File.Copy(args[0], AppPaths.Config);

const string ImportOption = "--import=";
const string DecodeOption = "--decode-sample";
const string LiveChannelOption = "--live-channel=";
const string VodIdOption = "--vod-id=";
foreach (var option in args.Skip(2).Where(value => value.StartsWith(ImportOption, StringComparison.Ordinal)))
{
    var importUrl = option[ImportOption.Length..];
    await config.ImportAsync(importUrl);
    var autoPack = await repoPacks.AutoInstallForRepositoryAsync(importUrl, config.Current);
    if (autoPack.Matched)
        Console.Error.WriteLine($"RepoPack: {autoPack.Message}");
}
var document = await config.LoadAsync();
var decode = args.Contains(DecodeOption, StringComparer.Ordinal);
var liveChannel = args.Skip(2).FirstOrDefault(value => value.StartsWith(LiveChannelOption, StringComparison.Ordinal))?[LiveChannelOption.Length..];
var vodId = args.Skip(2).FirstOrDefault(value => value.StartsWith(VodIdOption, StringComparison.Ordinal))?[VodIdOption.Length..];
var selected = args.Skip(2).Where(value => !value.StartsWith("--", StringComparison.Ordinal))
    .ToHashSet(StringComparer.Ordinal);
if (decode && selected.Count == 0)
{
    Console.Error.WriteLine("Choose explicit site keys for sample decoding.");
    return 2;
}
if (decode) Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
using var decoder = decode ? new LibVLC("--quiet", "--vout=dummy", "--aout=dummy", "--network-caching=1000") : null;
var sites = document.Sites.Where(site => selected.Count == 0 || selected.Contains(site.Key)).ToArray();
var lives = document.Lives.Select((source, index) => (Source: source, Key: $"live:{index + 1}"))
    .Where(entry => selected.Count == 0 || selected.Contains(entry.Key)).ToArray();
if (liveChannel is not null && (sites.Length != 0 || lives.Length != 1))
{
    Console.Error.WriteLine("Choose one live source key when selecting a channel by name.");
    return 2;
}
if (vodId is not null && (sites.Length != 1 || lives.Length != 0))
{
    Console.Error.WriteLine("Choose one VOD site key when supplying a sample ID.");
    return 2;
}
Console.Error.WriteLine($"Config {config.ActiveUrl}: {document.Sites.Count} sites, {document.Lives.Count} live sources");
if (sites.Length + lives.Length == 0) return 3;

var writeLock = new object();
void Write(Row row)
{
    lock (writeLock) Console.WriteLine(JsonSerializer.Serialize(row));
}

await Parallel.ForEachAsync(sites, new ParallelOptions { MaxDegreeOfParallelism = decode ? 1 : 3 },
    async (site, _) => Write(await ProbeSiteAsync(site)));
foreach (var entry in lives) Write(await ProbeLiveAsync(entry.Source, entry.Key));
return 0;

async Task<Row> ProbeSiteAsync(SourceSite site)
{
    using var cloud = new CloudDriveService(database, dns);
    using var bilibili = new BilibiliAccountService(database, dns);
    using var runtime = new SpiderRuntime(database);
    var bilibiliProvider = new BilibiliVodProvider(http, cloud, bilibili);
    var native = NativeVodProviderRouter.CreateDefault(http, cloud, bilibiliProvider);
    var cms = new CmsClient(http);
    var jsSpider = new JsSpiderClient(http);
    var pySpider = new PySpiderClient(http);
    var autoHtml = new UniversalAutoHtmlVodProvider(http, cloud);
    var repoPackVod = new RepoPackVodProvider(repoPacks, native, bilibiliProvider, jsSpider, cms);
    var router = new VodClientRouter(cms, new SpiderVodClient(runtime), native, jsSpider, pySpider, autoHtml, repoPackVod);
    var implementation = native.TryGet(site, out var provider)
            ? (provider is UniversalProtocolVodProvider && repoPackVod.CanOverrideUniversal(site) ? "RepoPackVodProvider" : provider.GetType().Name)
        : JsSpiderClient.Supports(site) ? "JsSpiderClient"
        : PySpiderClient.Supports(site) ? "PySpiderClient"
        : repoPackVod.CanOverrideUniversal(site) ? "RepoPackVodProvider"
        : autoHtml.CanHandle(site) ? "UniversalAutoHtmlVodProvider"
        : repoPackVod.CanHandle(site) ? "RepoPackVodProvider"
        : !site.IsSupported ? "无公开实现" : site.Type == 3 ? "Java CatVod" : "CMS";
    var row = new Row(site.Key, site.Name, site.Type.ToString(), PublicAddress(site.Api), implementation);
    if (!site.IsSupported)
    {
        row.Home = row.Category = row.Search = row.Detail = row.Play = new("BLOCKED", "无可用适配器");
        return row;
    }

    var searchOnly = NativeVodProviderRouter.IsSearchOnly(site);
    var (home, homeError) = searchOnly ? (null, null) : await TryAsync(ct => router.GetHomeAsync(site, ct));
    row.Home = searchOnly ? Unavailable("搜索专用站") : homeError ??
        Sample($"{home!.Categories.Count} 个分类，{home.List.Count} 条推荐",
            new { Fields = Fields(home.List), Categories = home.Categories.Select(c => c.Name), Filters = FilterCount(home),
                Cards = home.List.Take(3).Select(item => new { item.Id, item.Name, item.Remarks, item.Rating, item.Year }) });
    var candidate = FirstItem(home);
    var isBilibili = provider is BilibiliVodProvider;
    var category = home?.Categories.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Id) &&
        (!isBilibili || !BilibiliVodProvider.IsLoginCategory(item.Id)));
    row.Category = searchOnly ? Unavailable("搜索专用站") : new("PENDING", "首页未返回可抽样分类");
    if (category is not null)
    {
        var (first, firstError) = await TryAsync(ct => router.GetCategoryAsync(site, category.Id, 1, ct));
        var (second, secondError) = first is null || first.PageCount == 1
            ? (null, null) : await TryAsync(ct => router.GetCategoryAsync(site, category.Id, 2, ct));
        var (last, lastError) = first is { PageCount: > 2 }
            ? await TryAsync(ct => router.GetCategoryAsync(site, category.Id, first.PageCount, ct)) : (null, null);
        var repeated = first?.List.Count > 0 && second?.List.Count > 0 &&
            first.List.Select(item => item.Id).SequenceEqual(second.List.Select(item => item.Id));
        var evidence = new { category.Name, PageCount = first?.PageCount, Total = first?.Total,
            Page1 = first?.List.Count, Page2 = second?.List.Count, LastPage = last?.List.Count, SecondError = secondError, LastError = lastError };
        row.Category = firstError ?? (repeated ? new("FAIL", "第 2 页 ID 列表与第 1 页相同", evidence) :
            secondError is not null || lastError is not null ? new("BLOCKED", "部分分页请求失败", evidence) :
            Sample($"{category.Name}，第 1 页 {first!.List.Count} 条，第 2 页 {second?.List.Count.ToString() ?? "未请求"}", evidence));
        if (FirstItem(first) is { } categoryHit) candidate = categoryHit;
    }

    if (site.Searchable == 0) row.Search = Unavailable("配置关闭搜索");
    else
    {
        var queries = new List<Evidence>();
        VodItem? firstSearchHit = null;
        foreach (var keyword in new[] { "山海", "庆余年", "三体", "繁花", "流浪地球" })
        {
            var (hits, failure) = await TryAsync(ct => router.SearchAsync(site, keyword, 1, ct));
            queries.Add(failure is not null ? failure with { Summary = $"{keyword}：{failure.Summary}" } :
                Sample($"{keyword}：{hits!.List.Count} 条", Fields(hits.List)));
            firstSearchHit ??= FirstItem(hits);
        }
        var samples = queries.Count(query => query.Status == "SAMPLE");
        row.Search = new(samples == 5 ? "SAMPLE" : "BLOCKED",
            string.Join("；", queries.Select(query => query.Summary)), queries);
        candidate = firstSearchHit ?? candidate;
    }

    var sampleId = vodId ?? candidate?.Id;
    if (string.IsNullOrWhiteSpace(sampleId)) return row;
    var (detail, detailError) = await TryAsync(ct => router.GetDetailAsync(site, sampleId, ct));
    if (detail is null)
    {
        row.Detail = detailError!;
        return row;
    }
    var lines = PlaybackParsing.ParseLines(detail);
    row.Detail = Sample($"{detail.Name}，{lines.Count} 条线路，{lines.Sum(line => line.Episodes.Count)} 集",
        new { ItemId = sampleId, Fields = Fields([detail]),
            Metadata = new { detail.Year, detail.Area, detail.TypeName, detail.Remarks, detail.Rating, detail.Actor, detail.Director },
            Lines = lines.Select(line => new { line.Name, Count = line.Episodes.Count, Names = line.Episodes.Take(3).Select(episode => episode.Name) }) });
    var resolved = new List<Evidence>();
    Evidence? playbackProof = null;
    foreach (var line in lines.Where(line => line.Episodes.Count > 0).Take(3))
    {
        var address = line.Episodes[0].Url;
        if (IsCloudAddress(address))
        {
            resolved.Add(new("BLOCKED", $"{line.Name} 需网盘会话"));
            continue;
        }
        if (site.Type is not (3 or 4))
        {
            resolved.Add(Sample($"{line.Name}：{AddressType(address)}"));
            continue;
        }
        var (play, failure) = await TryAsync(ct => router.ResolvePlayAsync(site, line.Name, address, detail.CloudProvider, ct));
        Evidence? decodeResult = null;
        if (decoder is not null && playbackProof?.Status != "SAMPLE" && play is { Parse: 0, Jx: 0 } && PlayUrlResolver.IsDirectMediaUrl(play.Url))
        {
            var audioOnly = play.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
                Path.GetExtension(new Uri(play.Url).AbsolutePath).ToLowerInvariant() is ".mp3" or ".m4a" or ".flac" or ".aac" or ".wav" or ".opus";
            decodeResult = play.UseHttpProxy
                ? await DecodeAsync(decoder, cloud.ProxyMedia(new CloudPlayResult(play.Url, play.Headers, play.ContentType)
                    { UnwrapPngHlsSegments = play.UnwrapPngHlsSegments }),
                    new Dictionary<string, string>(), audioTracks: play.AudioTracks, audioOnly: audioOnly)
                : await DecodeAsync(decoder, play.Url, play.Headers, audioTracks: play.AudioTracks, audioOnly: audioOnly);
            playbackProof = decodeResult;
        }
        resolved.Add(failure is not null ? failure with { Summary = $"{line.Name}：{failure.Summary}" } :
            Sample($"{line.Name}：{AddressType(play!.Url)}，parse={play.Parse}，jx={play.Jx}" +
                (decodeResult is null ? string.Empty : $"，{decodeResult.Summary}"),
                new { HeaderNames = play.Headers.Keys, Subtitles = play.Subtitles?.Count, AudioTracks = play.AudioTracks?.Count,
                    Heights = play.ProviderVideoHeights?.OrderDescending(), Decode = decodeResult }));
    }
    row.Play = new(playbackProof is { Status: "BLOCKED" } ? "BLOCKED" :
        resolved.Any(x => x.Status == "SAMPLE") ? "SAMPLE" : resolved.Count == 0 ? "PENDING" : "BLOCKED",
        resolved.Count == 0 ? "详情未返回可抽样剧集" : string.Join("；", resolved.Select(x => x.Summary)), resolved);
    return row;
}

async Task<Row> ProbeLiveAsync(LiveSource source, string key)
{
    var row = new Row(key, source.Name, "live", PublicAddress(source.Url), "LiveService")
    {
        Search = Unavailable("直播列表协议无搜索接口")
    };
    var (channels, error) = await TryAsync(ct => new LiveService(http).LoadAsync([source], ct));
    if (channels is null)
    {
        row.Home = error!;
        return row;
    }
    var groups = channels.GroupBy(c => c.Group).ToArray();
    row.Home = Sample($"{channels.Count} 条频道线路");
    row.Category = Sample($"{groups.Length} 个分组", groups.Select(g => new { Name = g.Key, Count = g.Count() }));
    var channelsWithoutUpdate = channels.Where(channel => channel.Group != "列表更新时间");
    var sample = liveChannel is null
        ? channelsWithoutUpdate.FirstOrDefault(channel => !channel.Name.EndsWith("测试", StringComparison.Ordinal))
            ?? channelsWithoutUpdate.FirstOrDefault() ?? channels[0]
        : channels.FirstOrDefault(channel => string.Equals(channel.Name, liveChannel, StringComparison.OrdinalIgnoreCase));
    if (sample is null)
    {
        row.Detail = new("PENDING", $"未找到指定频道 {liveChannel}");
        return row;
    }
    var (program, epgFailure) = decode && sample.EpgUrl.Length > 0
        ? await TryAsync(ct => new EpgService(http).GetCurrentProgramAsync(sample, ct)) : (null, null);
    row.Detail = Sample($"抽样频道 {sample.Name}", new { sample.Group, HasLogo = sample.Logo.Length > 0,
        HasEpg = sample.EpgUrl.Length > 0, ProgramMatched = program is not null, EpgFailure = epgFailure, HeaderNames = sample.Headers.Keys });
    Evidence? playback = null;
    if (decoder is not null)
    {
        using var cloud = new CloudDriveService(database, dns);
        playback = Uri.TryCreate(sample.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !uri.IsLoopback
            ? await DecodeAsync(decoder, cloud.ProxyMedia(new CloudPlayResult(sample.Url, sample.Headers)),
                new Dictionary<string, string>(), live: true)
            : await DecodeAsync(decoder, sample.Url, sample.Headers, live: true);
    }
    row.Play = playback ?? Sample($"抽样线路 {AddressType(sample.Url)}，待客户端实播");
    return row;
}

static async Task<Evidence> DecodeAsync(LibVLC decoder, string address, IReadOnlyDictionary<string, string> headers,
    bool live = false, IReadOnlyList<Uri>? audioTracks = null, bool audioOnly = false)
{
    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        return Unavailable("后台解码抽样仅支持 HTTP(S) 媒体");
    using var player = new MediaPlayer(decoder);
    using var media = new Media(decoder, uri);
    media.AddOption(":http-reconnect=true");
    if (audioOnly) media.AddOption(":no-video");
    if (headers.TryGetValue("User-Agent", out var agent)) media.AddOption(":http-user-agent=" + agent);
    if (headers.TryGetValue("Referer", out var referer)) media.AddOption(":http-referrer=" + referer);
    foreach (var track in audioTracks ?? []) media.AddSlave(MediaSlaveType.Audio, 3, track);
    var elapsed = Stopwatch.StartNew();
    if (!player.Play(media)) return new("BLOCKED", "LibVLC 未接受媒体");
    long? firstFrameAt = null;
    long frameSpan = 0;
    var decodedVideo = 0;
    var playedAudio = 0;
    var verified = false;
    var timeLimit = TimeSpan.FromSeconds(live ? 22 : 14);
    while (elapsed.Elapsed < timeLimit && player.State is not (VLCState.Error or VLCState.Ended))
    {
        var stats = media.Statistics;
        if (stats.DecodedVideo > decodedVideo)
        {
            var now = elapsed.ElapsedMilliseconds;
            firstFrameAt ??= now;
            frameSpan = now - firstFrameAt.Value;
            decodedVideo = stats.DecodedVideo;
        }
        playedAudio = Math.Max(playedAudio, stats.PlayedAudioBuffers);
        // Some live FLV streams leave the reported time at zero while frames continue to decode.
        verified = audioOnly ? player.Time >= 3500 && (playedAudio > 0 || player.AudioTrack >= 0) :
            decodedVideo > 0 && (player.Time >= 3500 || live && frameSpan >= 3500);
        if (verified) break;
        await Task.Delay(150);
    }
    var metrics = new { TimeMs = player.Time, VideoTrack = player.VideoTrack, AudioTrack = player.AudioTrack,
        DecodedVideo = decodedVideo, PlayedAudioBuffers = playedAudio, FirstFrameMs = firstFrameAt,
        FrameSpanMs = frameSpan, State = player.State.ToString() };
    player.Stop();
    var summary = audioOnly ? $"音频时钟 {metrics.TimeMs}ms，{playedAudio} 音频缓冲" :
        $"后台解码 {metrics.TimeMs}ms，{decodedVideo} 视频帧";
    return verified ? Sample(summary, metrics) : new("BLOCKED", $"{summary}，{metrics.State}", metrics);
}

static VodItem? FirstItem(CmsResponse? response) =>
    response?.List.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name));

static object Fields(IEnumerable<VodItem> items) => new
{
    Poster = items.Count(i => !string.IsNullOrWhiteSpace(i.Picture)),
    Title = items.Count(i => !string.IsNullOrWhiteSpace(i.Name)),
    Remarks = items.Count(i => !string.IsNullOrWhiteSpace(i.Remarks)),
    Rating = items.Count(i => !string.IsNullOrWhiteSpace(i.Rating)),
    Year = items.Count(i => !string.IsNullOrWhiteSpace(i.Year)),
    Area = items.Count(i => !string.IsNullOrWhiteSpace(i.Area)),
    Type = items.Count(i => !string.IsNullOrWhiteSpace(i.TypeName)),
    Actor = items.Count(i => !string.IsNullOrWhiteSpace(i.Actor)),
    Director = items.Count(i => !string.IsNullOrWhiteSpace(i.Director)),
    Plot = items.Count(i => !string.IsNullOrWhiteSpace(i.Description))
};

static int FilterCount(CmsResponse payload) =>
    payload.Filters.ValueKind == JsonValueKind.Object ? payload.Filters.EnumerateObject().Count() : 0;

static string PublicAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
    ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
    : value;

static string AddressType(string value) => PlayUrlResolver.IsDirectMediaUrl(value) ? "直链" :
    Uri.TryCreate(value, UriKind.Absolute, out var uri) ? $"{uri.Scheme} 地址" : "站点内部 ID";

static bool IsCloudAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
    (uri.Scheme == "cloud" || uri.IdnHost is "pan.quark.cn" or "drive.uc.cn" or
        "alipan.com" or "www.alipan.com" or "aliyundrive.com" or "www.aliyundrive.com");

static async Task<(T?, Evidence?)> TryAsync<T>(Func<CancellationToken, Task<T>> operation) where T : class?
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { return (await operation(deadline.Token), null); }
    catch (Exception ex)
    {
        var status = Regex.Match(ex.Message, @"\b[45]\d{2}\b");
        var label = ex switch
        {
            OperationCanceledException or TimeoutException => "超时",
            HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
            HttpRequestException { InnerException: SocketException socket } => $"网络 {socket.SocketErrorCode}",
            _ when ex.Message.Contains("验证码", StringComparison.Ordinal) => "需要验证码",
            _ when ex.Message.Contains("登录", StringComparison.Ordinal) => "需要登录",
            _ when status.Success => $"上游状态 {status.Value}",
            _ => ex.GetType().Name
        };
        return (null, new("BLOCKED", label));
    }
}

static Evidence Sample(string summary, object? data = null) => new("SAMPLE", summary, data);
static Evidence Unavailable(string reason) => new("N/A", reason);

sealed record Evidence(string Status, string Summary, object? Data = null);

sealed record Row(string Key, string Name, string Type, string Api, string Implementation)
{
    public Evidence Home { get; set; } = new("PENDING", "待抽样");
    public Evidence Category { get; set; } = new("PENDING", "待抽样");
    public Evidence Search { get; set; } = new("PENDING", "待抽样");
    public Evidence Detail { get; set; } = new("PENDING", "缺少可抽样片目");
    public Evidence Play { get; set; } = new("PENDING", "待解析和实播");
}
