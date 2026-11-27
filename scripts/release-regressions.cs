using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using TV.Models;
using TV.Services;
using TV.Services.Cloud;
using TV.Services.Configuration;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Playback;
using TV.Services.Spider;
using TV.Services.Vod.Native;

Environment.SetEnvironmentVariable("TV_DATA_ROOT", Path.Combine(args[0], "profile"));
AppPaths.EnsureCreated();
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
    checks++;
}
async Task CheckThrows<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, label); return; }
    throw new InvalidOperationException(label);
}

var database = new DatabaseService();
var fixture = new CloudFixture();
using var cloud = new CloudHttpClient(fixture, allowAutoRedirect: true);
var uc = new QuarkCloudProvider(cloud, CloudProviderKind.UC, _ => Task.FromResult("fixture=first"), database);
var original = new QualityOption("原画", -1) { IsOriginal = true };
var small = new CloudFile(CloudProviderKind.UC, "drive", "one", "", "sample.mp4", 7 * CloudFixture.MiB);
var normal = await uc.PlayAsync(small, original);
Check(normal.Url == "https://cdn.uc.cn/original.mp4" && normal.Notice is null, "UC serves an available original");
Check(fixture.LastDownloadCookie == "fixture=first" && normal.Headers["Cookie"] == "fixture=first", "Media uses its own request cookie");
Check(fixture.LastTranscodeCookie == "fixture=rotated", "Cookie rotation is used by the next API call");

fixture.Mode = "clip";
var smallWithPromo = small with { Size = 20 * CloudFixture.MiB };
var fallback = await uc.PlayAsync(smallWithPromo, original);
Check(fallback.ResolvedQuality?.Height == 1080 && fallback.Notice?.Contains("原画受限", StringComparison.Ordinal) == true,
    "UC detects the promo below the former file-size threshold and selects the best transcode");
Check(!fallback.Headers.ContainsKey("Cookie"), "UC cookie is withheld from a separate CDN");

var transcodeCalls = fixture.TranscodeCalls;
await CheckThrows<UcPlaybackRestrictionException>(() => uc.PlayAsync(smallWithPromo, "原画"), "Original download rejects the promo");
Check(transcodeCalls == fixture.TranscodeCalls, "Original download leaves the transcode endpoint unused");
var sameSize = await uc.PlayAsync(small with { Size = CloudFixture.PromoSize }, original);
Check(sameSize.Url.EndsWith("/clip.mp4", StringComparison.Ordinal) && sameSize.Notice is null, "Equal-size originals stay playable");

fixture.Mode = "explicit-title";
fallback = await uc.PlayAsync(small with { Size = 0 }, original);
Check(fallback.ResolvedQuality?.Height == 1080, "Explicit promo filename is detected when size is missing");
fixture.Mode = "html";
fallback = await uc.PlayAsync(small with { Size = 0 }, original);
Check(fallback.ResolvedQuality?.Height == 1080, "UC restriction in a media response is detected");
fixture.Mode = "denied";
fallback = await uc.PlayAsync(small, original);
Check(fallback.ResolvedQuality?.Height == 1080 && fallback.Notice is not null, "HTTP 403 from the original endpoint falls back");
fixture.Mode = "lower-quality";
fallback = await uc.PlayAsync(small, original);
Check(fallback.ResolvedQuality?.Height == 720 && fallback.Notice is not null, "UC tries a lower authorized transcode when the best one is unavailable");
fixture.Mode = "original-only";
fallback = await uc.PlayAsync(small, new QualityOption("自动", 0));
Check(fallback.ResolvedQuality?.IsOriginal == true, "Automatic UC playback uses the available original when no transcodes exist");
fixture.Mode = "business-denial";
fallback = await uc.PlayAsync(small, original);
Check(fallback.ResolvedQuality?.Height == 1080, "UC business restriction returned with HTTP 200 falls back");
fixture.Mode = "denied-all";
await CheckThrows<UcPlaybackRestrictionException>(() => uc.PlayAsync(small, original), "Total UC restriction yields a dedicated playback message");
fixture.Mode = "expired";
await CheckThrows<CloudAuthenticationException>(() => uc.PlayAsync(small, original), "Expired UC account asks for sign-in");
fixture.Mode = "normal";
await uc.SaveCredentialAsync("newaccount=only", CancellationToken.None);
await uc.PlayAsync(small, "原画");
Check(fixture.LastDownloadCookie == "newaccount=only", "Re-login clears the previous account cookies");

var credentials = new Dictionary<string, string> { ["Cookie"] = "fixture=one", ["Authorization"] = "Bearer fixture", ["User-Agent"] = "regression" };
await cloud.GetAsync("https://cdn.uc.cn/same-origin", credentials);
Check(fixture.SameOriginSawCredentials, "Same-origin redirect retains required credentials");
var across = await cloud.GetAsync("https://cdn.uc.cn/redirect", credentials);
Check(fixture.CrossOriginRedirectReceived && !fixture.CrossOriginSawCredentials && across.Cookies.Count == 0,
    "Cross-origin redirect preserves delivery while isolating credentials and cookies");
await CheckThrows<HttpRequestException>(
    () => cloud.PostJsonAsync("https://cdn.uc.cn/redirect-307", new { hello = "fixture" }, credentials),
    "Cross-origin redirect does not resend a request body");
var redacted = Diagnostics.Redact("Cookie: first=fixture-one; second=fixture-two\nhttps://cdn.uc.cn/file?token=fixture-three\n{\"Cookie\":\"other=fixture-four\"}");
Check(!redacted.Contains("fixture-", StringComparison.Ordinal), "Cookie pairs and signed media queries are redacted");

using var siteHttp = new SourceHttpClient();
var danmaku = new DanDanPlayService(siteHttp, database);
Check(!typeof(DanDanPlayService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .Any(attribute => attribute.Key.StartsWith("DanDanPlayApp", StringComparison.Ordinal)),
    "The desktop assembly has no built-in DanDanPlay credential metadata");
await danmaku.SaveCustomCredentialsAsync("fixture-app", "fixture-secret");
var protectedSecret = await database.GetSettingAsync(DanDanPlayService.SettingAppSecret);
Check(protectedSecret is not null && !protectedSecret.Contains("fixture-secret", StringComparison.Ordinal) &&
    SecretStore.Unprotect(protectedSecret) == "fixture-secret" && await danmaku.HasCustomCredentialsAsync(),
    "Custom DanDanPlay credentials round-trip through DPAPI");
await danmaku.ClearCustomCredentialsAsync();
Check(!await danmaku.HasCustomCredentialsAsync() && danmaku.HasDefaultService == await danmaku.HasCredentialsAsync(),
    "Clearing a custom credential restores the configured default");

// --- 1.9.7 Regression: DanDanPlay multi-relay failover, custom relay persistence, and direct China CDN delivery ---
var parsedRelays = DanDanPlayService.ParseRelayUrls("https://edgeone.example.com, https://abc.laf.run/dandanplay; https://esa.example.com/");
Check(parsedRelays.Count == 3 && parsedRelays[1].AbsoluteUri == "https://abc.laf.run/dandanplay/",
    "DanDanPlay ParseRelayUrls accepts multi-origin and path-prefixed HTTPS relays");
Check(DanDanPlayService.ParseRelayUrls("http://insecure.example.com", strict: true).Count == 0 &&
      DanDanPlayService.ParseRelayUrls("https://127.0.0.1/", strict: true).Count == 0 &&
      DanDanPlayService.ParseRelayUrls("https://relay.example.com/a?x=1", strict: true).Count == 0,
    "DanDanPlay ParseRelayUrls rejects non-HTTPS, loopback, and query/fragment URLs");
await danmaku.SaveCustomRelayUrlAsync("https://cn-relay.example.com/dandanplay");
Check(await danmaku.GetCustomRelayUrlAsync() == "https://cn-relay.example.com/dandanplay" && await danmaku.HasCustomRelayAsync(),
    "Custom DanDanPlay relay URL persists cleanly in SQLite");
await danmaku.ClearCustomRelayUrlAsync();
Check(!await danmaku.HasCustomRelayAsync(), "Clearing custom DanDanPlay relay URL works");

var relayFixture = new RelayAndUniversalFixture();
using var mockSiteHttp = new SourceHttpClient(relayFixture);
var failoverDanmaku = new DanDanPlayService(
    mockSiteHttp,
    database,
    [new Uri("https://dead-relay.example.invalid/"), new Uri("https://healthy-relay.example.invalid/dandanplay/")],
    commentDeliveryRewriter: uri => new Uri("https://cdn-fixture.example.invalid" + uri.PathAndQuery));
var failoverGroups = await failoverDanmaku.SearchEpisodesAsync("葬送的芙莉莲");
Check(failoverGroups.Count == 1 && failoverGroups[0].AnimeId == 2026,
    "DanDanPlay automatically fails over from a 502 primary relay to the healthy secondary relay");
var deadCallsBeforeSecond = relayFixture.DeadRelayCalls;
var secondGroups = await failoverDanmaku.SearchEpisodesAsync("药屋少女的呢喃");
Check(secondGroups.Count == 1 && relayFixture.DeadRelayCalls == deadCallsBeforeSecond,
    "DanDanPlay remembers the healthy relay and skips the failed primary on subsequent requests");
var directComments = await failoverDanmaku.GetCommentsAsync(98765, withRelated: true, chConvert: 1);
Check(directComments.Length == 1 && relayFixture.DirectDeliveryHeaderSeen && relayFixture.DirectCdnFetched && !relayFixture.DirectCdnSawRelayHeader,
    "DanDanPlay downloads comment payloads directly from validated cas2.dandanplay.net CDN without relay headers");
Check(!DanDanPlayService.TryExtractDirectCommentDeliveryUrl("""{"success":true,"deliveryUrl":"https://evil.example.invalid/api/comment/98765"}""", "/api/v2/comment/98765", out _),
    "DanDanPlay rejects forged non-cas2 deliveryUrl values");

// --- 1.9.7 Regression: TVBox config decoding (comments, ** Base64, AES-128-CBC) & relative path normalization ---
var lenientJson = """
{
  // single line comment
  "spider": "./spider.jar;md5;deadbeef",
  /* block comment */
  "sites": [
    { "key": "s1", "name": "Site1", "type": 3, "api": "csp_CustomGuard", "ext": "./rules/s1.json", },
  ],
}
""";
var decodedLenient = SourceConfigService.DecodeConfig(Encoding.UTF8.GetBytes(lenientJson));
var parsedLenient = SourceConfigService.Parse(decodedLenient);
Check(parsedLenient.Sites.Count == 1 && parsedLenient.Sites[0].Key == "s1", "SourceConfigService decodes JSON with comments and trailing commas");

var base64Payload = "IMGHEAD8**" + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sites":[{"key":"b64","name":"B64"}]}"""));
var decodedB64 = SourceConfigService.DecodeConfig(Encoding.UTF8.GetBytes(base64Payload));
Check(decodedB64.Contains("\"b64\"", StringComparison.Ordinal), "SourceConfigService decodes 8-char** image-prefixed Base64 configs");

var aesPlain = """{"spider":"./box.jar","sites":[{"key":"aes1","name":"AES Site","type":3,"api":"csp_AesGuard"}]}""";
var aesHexPayload = EncryptTvBoxAesHex(aesPlain, "tvboxsecret12345", "1700000000000");
var decodedAes = SourceConfigService.DecodeConfig(Encoding.UTF8.GetBytes(aesHexPayload));
Check(decodedAes.Contains("\"aes1\"", StringComparison.Ordinal), "SourceConfigService decodes AES-128-CBC ($#key#$ / 2423...2324) hex TVBox configs");

// --- 1.9.7 Regression: Repository switching & active-repo site scoping ---
var configService = new SourceConfigService(mockSiteHttp, database);
await configService.ImportAsync("https://repo-fixture.example.invalid/multi.json");
Check(configService.Repositories.Count >= 3, "Multi-repo import registers Built-in plus each sub-repository cleanly");
var repoA = configService.Repositories.First(r => r.Name == "仓库甲");
var repoB = configService.Repositories.First(r => r.Name == "仓库乙");
await configService.SelectRepositoryAsync(repoA.Id);
Check(configService.Current is not null && configService.Current.Sites.Count == 1 && configService.Current.Sites[0].Key == "repo_a_site",
    "Switching to Repo A scopes Current.Sites strictly to Repo A");
await configService.SelectRepositoryAsync(repoB.Id);
Check(configService.Current is not null && configService.Current.Sites.Count == 1 && configService.Current.Sites[0].Key == "repo_b_site" &&
      configService.Current.Sites[0].EffectiveJar.StartsWith("https://repo-fixture.example.invalid/jars/b.jar", StringComparison.Ordinal),
    "Switching to Repo B scopes Current.Sites strictly to Repo B and hydrates its relative spider JAR");
Check(configService.FindSite("repo_a_site") is { Key: "repo_a_site" },
    "FindSite still resolves inactive-repository sites so favorites and history remain playable");
var persistedRepo = await database.GetSettingAsync("source.activeRepo");
Check(persistedRepo == repoB.Id, "Active repository selection persists to SQLite");
await configService.SelectRepositoryAsync(SourceConfigService.BuiltInRepositoryId);
await configService.RemoveImportAsync("https://repo-fixture.example.invalid/multi.json");

// --- 1.9.7 Regression: Universal Android TVBox site adaptation (External JAR catalog, AppYsV2, drpy2 JS, CatVod-JS, NativeSiteToolkit) ---
Check(SpiderCatalog.IsAvailable("csp_AnyThirdPartyAndroidGuard", default, "https://repo-fixture.example.invalid/jars/b.jar;md5;1234"),
    "SpiderCatalog enables any third-party Android csp_* spider backed by an external JAR");
Check(!SpiderCatalog.IsAvailable("csp_Notice", default, "https://repo-fixture.example.invalid/jars/b.jar"),
    "SpiderCatalog excludes non-playable placeholder entries like csp_Notice");

var universalProvider = new UniversalProtocolVodProvider(mockSiteHttp);
var appYsSite = new SourceSite
{
    Key = "test_appys",
    Name = "AppYs测试",
    Type = 3,
    Api = "csp_AppYsV2",
    Ext = JsonDocument.Parse("\"https://appys.example.invalid/api.php/app/\"").RootElement
};
Check(UniversalProtocolVodProvider.Supports(appYsSite), "UniversalProtocolVodProvider matches csp_AppYsV2 sites");
var appYsHome = await universalProvider.GetHomeAsync(appYsSite, CancellationToken.None);
Check(appYsHome.Categories.Count >= 1 && appYsHome.List.Count >= 1 && appYsHome.List[0].Name == "星际穿越",
    "UniversalProtocolVodProvider parses AppYsV2 home categories and items");
var appYsDetail = await universalProvider.GetDetailAsync(appYsSite, "101", CancellationToken.None);
Check(appYsDetail.PlayUrl.Contains("https://cdn.example.invalid/interstellar.m3u8", StringComparison.Ordinal),
    "UniversalProtocolVodProvider parses AppYsV2 detail play lists");

var jsClient = new JsSpiderClient(mockSiteHttp);
var drpySite = new SourceSite
{
    Key = "test_drpy",
    Name = "Drpy测试",
    Type = 3,
    Api = "https://drpy.example.invalid/libs/drpy2.min.js",
    Ext = JsonDocument.Parse("\"https://drpy.example.invalid/js/sample.js\"").RootElement
};
Check(JsSpiderClient.Supports(drpySite), "JsSpiderClient detects drpy2 JS sites");
var drpyHome = await jsClient.GetHomeAsync(drpySite, CancellationToken.None);
Check(drpyHome.Categories.Count == 2 && drpyHome.List.Count == 1 && drpyHome.List[0].Name == "流光测试剧",
    "JsSpiderClient executes drpy2 rule scripts via Jint + AngleSharp selectors");
var drpyPlay = await jsClient.ResolvePlayAsync(drpySite, "线路1", "https://drpy.example.invalid/play/1-1-1.html", CancellationToken.None);
Check(drpyPlay.Url == "https://stream.example.invalid/ep1.m3u8" && drpyPlay.Parse == 0,
    "JsSpiderClient extracts MacCMS player_aaaa direct stream URLs");

var t3JsSite = new SourceSite
{
    Key = "test_t3js",
    Name = "T3JS测试",
    Type = 3,
    Api = "https://drpy.example.invalid/js/catvod_module.js",
    Ext = JsonDocument.Parse("\"\"").RootElement
};
var t3Home = await jsClient.GetHomeAsync(t3JsSite, CancellationToken.None);
Check(t3Home.Categories.Count == 1 && t3Home.List.Count == 1 && t3Home.List[0].Id == "js_vod_99",
    "JsSpiderClient executes CatVod-JS / T3 module spiders cleanly inside Jint sandbox");

var playerAaaaHtml = """<script>var player_aaaa={"flag":"play","encrypt":2,"url":"JTJGaGxzJTJGc2FtcGxlLm0zdTg="};</script>""";
Check(NativeSiteToolkit.TryParsePlayerAaaa(playerAaaaHtml, out var parsedAaaa) && parsedAaaa.DecodedUrl == "/hls/sample.m3u8",
    "NativeSiteToolkit decodes encrypt=2 MacCMS player_aaaa payloads");

// --- 1.9.7 Regression: PlaybackSeekCoordinator smooth seek & zero pause/play flicker ---
PlayerSnapshot Snap(VLCState state, bool playing, long timeMs, long lengthMs, long seekGen) =>
    new(state, playing, timeMs, lengthMs, false, 1f, [], [], -1, -1, null, 0f, 0, string.Empty, seekGen);

var seekCoord = new PlaybackSeekCoordinator();
seekCoord.Reset(nowMs: 1000);
var playingTick = seekCoord.EvaluateTick(Snap(VLCState.Playing, true, 100_000, 1_000_000, 0), 100_000, 1010, false);
Check(playingTick.EffectivePlaying && playingTick.ShouldUpdatePosition, "Seek coordinator tracks normal playback cleanly");
seekCoord.BeginDrag();
var dragTick = seekCoord.EvaluateTick(Snap(VLCState.Playing, true, 101_000, 1_000_000, 0), 250_000, 1050, false);
Check(!dragTick.ShouldUpdatePosition && dragTick.DisplayPositionMs == 250_000, "Seek coordinator suppresses engine ticks while user is dragging slider");
seekCoord.CommitSeek(targetMs: 420_000, durationMs: 1_000_000, seekGeneration: 5, nowMs: 1200);
var staleTick = seekCoord.EvaluateTick(Snap(VLCState.Playing, true, 100_000, 1_000_000, 4), 420_000, 1500, false);
Check(!staleTick.ShouldUpdatePosition && staleTick.DisplayPositionMs == 420_000,
    "Seek coordinator blocks stale pre-seek ticks so the slider thumb never snaps back");
var settledTick = seekCoord.EvaluateTick(Snap(VLCState.Playing, true, 420_800, 1_000_000, 5), 420_000, 1550, false);
Check(settledTick.ShouldUpdatePosition && settledTick.DisplayPositionMs == 420_800,
    "Seek coordinator releases lock immediately once LibVLC converges near target");
var bufferingStatus = seekCoord.EvaluateTick(Snap(VLCState.Buffering, false, 420_800, 1_000_000, 5), 420_800, 1600, false);
Check(bufferingStatus.EffectivePlaying && !bufferingStatus.ShowBuffering,
    "Transient VLC Buffering preserves logical IsPlaying and debounces the loading spinner");
var backwardBefore = seekCoord.EvaluateTick(Snap(VLCState.Playing, true, 600_000, 1_000_000, 5), 600_000, 2000, false);
seekCoord.CommitSeek(targetMs: 120_000, durationMs: 1_000_000, seekGeneration: 6, nowMs: 2100);
var afterBackward = seekCoord.EvaluateTick(Snap(VLCState.Playing, true, 120_500, 1_000_000, 6), 120_000, 2500, false);
Check(backwardBefore.EffectivePlaying && afterBackward.EffectivePlaying && !afterBackward.ShowBuffering && !afterBackward.SuggestLowerQuality,
    "Backward seek resets progress baseline without triggering false stall or pause loops");
var rel1 = seekCoord.ComputeRelativeSeekTarget(5, snapshotTimeMs: 120_500, durationMs: 1_000_000, nowMs: 2600);
seekCoord.CommitSeek(rel1, 1_000_000, 7, 2600);
var rel2 = seekCoord.ComputeRelativeSeekTarget(5, snapshotTimeMs: 120_500, durationMs: 1_000_000, nowMs: 2650);
Check(rel2 - rel1 == 5000, "Rapid relative keyboard seeks accumulate from the active seek target");

// --- 1.9.7 Regression: CloudBlockCache seek-epoch prefetch cancellation ---
var blockFixture = new CloudBlockFixture(totalSize: 25 * 1024 * 1024);
using var blockHttp = new HttpClient(blockFixture);
using var providerGate = new SemaphoreSlim(3, 3);
using (var cache = new CloudBlockCache(
    blockHttp,
    new Uri("https://cloud-block.example.invalid/video.mp4"),
    new Uri("https://cloud-block.example.invalid/video.mp4"),
    new Dictionary<string, string>(),
    totalLength: 25 * 1024 * 1024,
    contentType: "video/mp4",
    entityTag: null,
    blockSize: 1024 * 1024,
    maxThreads: 3,
    providerGate: providerGate,
    parentToken: CancellationToken.None,
    refresh: null,
    onUpstreamUpdated: null))
{
    var buf = new byte[4096];
    await using (var s1 = cache.OpenRangeStream(0, 4095))
    {
        var r1 = await s1.ReadAsync(buf, CancellationToken.None);
        Check(r1 == buf.Length, "CloudBlockCache serves initial range read");
    }
    await Task.Delay(50);
    var jumpOffset = 20L * 1024 * 1024;
    await using (var s2 = cache.OpenRangeStream(jumpOffset, jumpOffset + 4095))
    {
        var r2 = await s2.ReadAsync(buf, CancellationToken.None);
        Check(r2 == buf.Length && cache.PrefetchCancellations > 0,
            "CloudBlockCache cancels stale speculative lookahead blocks on distant seeks and serves target immediately");
    }
}

if (args.Contains("--verify-default-service", StringComparer.Ordinal))
{
    Check(danmaku.HasDefaultService, "The build contains a valid default relay URL");
    var (ok, message) = await danmaku.VerifyCredentialsAsync();
    Check(ok, $"Default DanDanPlay service responds to the release build ({message})");
    var episodes = (await danmaku.SearchEpisodesAsync("你的名字")).SelectMany(group => group.Episodes).ToArray();
    Check(episodes.Length > 0, "The default service supports the desktop application's v2 search");
    var episode = episodes.FirstOrDefault(entry => entry.EpisodeId == 60410001) ?? episodes[0];
    var comments = await danmaku.GetCommentsAsync(episode.EpisodeId);
    Check(comments.Length > 0, "The fresh profile loads real episode comments from the default service");
}
Console.WriteLine($"{checks} checks passed");

static string EncryptTvBoxAesHex(string plainText, string key, string iv)
{
    var keyBytes = Encoding.UTF8.GetBytes(key.PadRight(16, '0')[..16]);
    var ivBytes = Encoding.UTF8.GetBytes(iv.PadRight(16, '0')[..16]);
    using var aes = Aes.Create();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;
    aes.Key = keyBytes;
    aes.IV = ivBytes;
    using var encryptor = aes.CreateEncryptor();
    var plainBytes = Encoding.UTF8.GetBytes(plainText);
    var cipher = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    var prefixHex = Convert.ToHexString(Encoding.UTF8.GetBytes($"$#{key}#$")).ToLowerInvariant();
    var cipherHex = Convert.ToHexString(cipher).ToLowerInvariant();
    var ivHex = Convert.ToHexString(Encoding.UTF8.GetBytes(iv)).ToLowerInvariant();
    return prefixHex + cipherHex + ivHex;
}

sealed class RelayAndUniversalFixture : HttpMessageHandler
{
    internal int DeadRelayCalls { get; private set; }
    internal bool DirectDeliveryHeaderSeen { get; private set; }
    internal bool DirectCdnFetched { get; private set; }
    internal bool DirectCdnSawRelayHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        var host = uri.Host;
        var path = uri.AbsolutePath;

        if (host == "dead-relay.example.invalid")
        {
            DeadRelayCalls++;
            return Reply(HttpStatusCode.BadGateway, """{"success":false,"errorMessage":"upstream timeout"}""");
        }
        if (host == "healthy-relay.example.invalid")
        {
            if (path.EndsWith("/api/v2/search/episodes", StringComparison.Ordinal))
            {
                return Reply(HttpStatusCode.OK, """{"success":true,"animes":[{"animeId":2026,"animeTitle":"葬送的芙莉莲","episodes":[{"episodeId":98765,"episodeTitle":"第1话"}]}]}""");
            }
            if (path.EndsWith("/api/v2/comment/98765", StringComparison.Ordinal))
            {
                DirectDeliveryHeaderSeen = request.Headers.TryGetValues("X-Relay-Delivery", out var v) && v.Contains("direct");
                return Reply(HttpStatusCode.OK, """{"success":true,"deliveryUrl":"https://cas2.dandanplay.net/api/comment/98765?sig=ok"}""");
            }
        }
        if (host == "cdn-fixture.example.invalid" && path == "/api/comment/98765")
        {
            DirectCdnFetched = true;
            DirectCdnSawRelayHeader = request.Headers.Contains("X-Relay-Delivery") || request.Headers.Contains("X-Signature");
            return Reply(HttpStatusCode.OK, """{"count":1,"comments":[{"cid":1,"p":"12.5,1,16777215,u1","m":"国内CDN直连弹幕测试"}]}""");
        }
        if (host == "repo-fixture.example.invalid")
        {
            return path switch
            {
                "/multi.json" => Reply(HttpStatusCode.OK, """{"urls":[{"name":"仓库甲","url":"./sub/a.json"},{"name":"仓库乙","url":"./sub/b.json"}]}"""),
                "/sub/a.json" => Reply(HttpStatusCode.OK, """{"spider":"../jars/a.jar;md5;1111","sites":[{"key":"repo_a_site","name":"甲站点","type":3,"api":"csp_CustomAGuard"}]}"""),
                "/sub/b.json" => Reply(HttpStatusCode.OK, """{"spider":"../jars/b.jar;md5;2222","sites":[{"key":"repo_b_site","name":"乙站点","type":3,"api":"csp_CustomBGuard"}]}"""),
                _ => Reply(HttpStatusCode.NotFound, "{}")
            };
        }
        if (host == "appys.example.invalid")
        {
            if (path.EndsWith("/nav", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, """{"code":1,"list":[{"type_id":"1","type_name":"电影"}]}""");
            if (path.EndsWith("/index_video", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, """{"code":1,"list":[{"vlist":[{"vod_id":"101","vod_name":"星际穿越","vod_pic":"https://img.example.invalid/101.jpg","vod_remarks":"4K"}]}]}""");
            if (path.EndsWith("/video_detail", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, """{"code":1,"data":{"vod_id":"101","vod_name":"星际穿越","vod_url_with_player":[{"name":"直链","url":"正片$https://cdn.example.invalid/interstellar.m3u8"}]}}""");
        }
        if (host == "drpy.example.invalid")
        {
            if (path == "/js/sample.js")
            {
                const string script = """
                    var rule = {
                        title: 'Drpy示例',
                        host: 'https://drpy.example.invalid',
                        url: '/list/fyclass-fypage.html',
                        class_name: '电影&剧集',
                        class_url: '1&2',
                        推荐: '.vod-card;a&&Text;img&&data-original;.badge&&Text;a&&href',
                        一级: '.vod-card;a&&Text;img&&data-original;.badge&&Text;a&&href',
                        二级: { title: 'h1&&Text', tabs: '.line-tab', lists: '.ep-box:eq(#id) a' }
                    };
                    """;
                return Reply(HttpStatusCode.OK, script, "application/javascript");
            }
            if (path == "/js/catvod_module.js")
            {
                const string mod = """
                    export default {
                        init(ext) {},
                        home(filter) {
                            return JSON.stringify({
                                class: [{ type_id: 'movie', type_name: '电影' }],
                                list: [{ vod_id: 'js_vod_99', vod_name: '沙盒测试片', vod_pic: 'https://img.example.invalid/99.jpg', vod_remarks: 'HD' }]
                            });
                        }
                    };
                    """;
                return Reply(HttpStatusCode.OK, mod, "application/javascript");
            }
            if (path == "/")
            {
                const string html = """
                    <html><body>
                    <div class="vod-card"><a href="/detail/1.html">流光测试剧</a><img data-original="/img/1.jpg" /><span class="badge">更新至12集</span></div>
                    </body></html>
                    """;
                return Reply(HttpStatusCode.OK, html, "text/html");
            }
            if (path == "/play/1-1-1.html")
            {
                const string html = """<html><script>var player_aaaa={"flag":"play","encrypt":0,"url":"https://stream.example.invalid/ep1.m3u8"};</script></html>""";
                return Reply(HttpStatusCode.OK, html, "text/html");
            }
        }
        return Reply(HttpStatusCode.NotFound, "{}");
    }

    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body, string mediaType = "application/json") =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
}

sealed class CloudBlockFixture(long totalSize) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var from = range?.From ?? 0;
        var to = Math.Min(range?.To ?? (totalSize - 1), totalSize - 1);
        var length = (int)(to - from + 1);
        // Simulate slow network for speculative lookahead blocks (blocks 1..5) so a distant seek jump cancels them mid-flight
        if (from >= 1024 * 1024 && from < 6 * 1024 * 1024)
            await Task.Delay(5000, token).ConfigureAwait(false);
        var data = new byte[length];
        Array.Fill(data, (byte)(from / (1024 * 1024) + 1));
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, totalSize);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }
}

sealed class CloudFixture : HttpMessageHandler
{
    internal const int MiB = 1024 * 1024;
    internal const int PromoSize = 146 * MiB / 10;
    internal string Mode { get; set; } = "normal";
    internal string LastDownloadCookie { get; private set; } = "";
    internal string LastTranscodeCookie { get; private set; } = "";
    internal int TranscodeCalls { get; private set; }
    internal bool CrossOriginSawCredentials { get; private set; }
    internal bool CrossOriginRedirectReceived { get; private set; }
    internal bool SameOriginSawCredentials { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/file/download", StringComparison.Ordinal))
        {
            LastDownloadCookie = ReadCookie(request);
            if (Mode == "expired") return Reply(HttpStatusCode.Unauthorized, "{}");
            if (Mode is "denied" or "denied-all" or "lower-quality") return Reply(HttpStatusCode.Forbidden, """{"message":"需要会员权限"}""");
            if (Mode == "business-denial") return Reply(HttpStatusCode.OK, """{"code":"vip_required"}""");
            var asset = Mode switch { "clip" or "explicit-title" => "clip.mp4", "html" => "blocked.html", _ => "original.mp4" };
            return Reply(HttpStatusCode.OK, $$"""{"code":0,"data":[{"download_url":"https://cdn.uc.cn/{{asset}}"}]}""", "fixture=rotated; Path=/");
        }
        if (path.Contains("/file/v2/play", StringComparison.Ordinal))
        {
            LastTranscodeCookie = ReadCookie(request);
            TranscodeCalls++;
            if (Mode == "denied-all") return Reply(HttpStatusCode.Forbidden, "{}");
            if (Mode == "original-only") return Reply(HttpStatusCode.OK, """{"code":0,"data":{"video_list":[]}}""");
            return Reply(HttpStatusCode.OK, """
                {"code":0,"data":{"video_list":[
                    {"resolution":"high","video_info":{"url":"https://cdn.example.invalid/720.mp4"}},
                    {"resolution":"super","video_info":{"url":"https://cdn.example.invalid/1080.mp4"}}
                ]}}
                """);
        }
        if (path == "/same-origin") return Redirect(HttpStatusCode.Found, "/same-origin-target");
        if (path == "/same-origin-target")
        {
            SameOriginSawCredentials = request.Headers.Contains("Cookie") && request.Headers.Contains("Authorization");
            return Reply(HttpStatusCode.OK, "{}");
        }
        if (path == "/redirect") return Redirect(HttpStatusCode.Found, "https://cdn.example.invalid/redirect-target");
        if (path == "/redirect-307") return Redirect(HttpStatusCode.TemporaryRedirect, "https://cdn.example.invalid/redirect-target");
        if (path == "/redirect-target")
        {
            CrossOriginRedirectReceived = true;
            CrossOriginSawCredentials = request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization");
            return Reply(HttpStatusCode.OK, "{}", "fixture=external; Path=/");
        }
        if (path == "/blocked.html")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("开通会员才能在当前浏览器播放", Encoding.UTF8, "text/html") });
        if (path == "/1080.mp4" && Mode == "lower-quality") return Reply(HttpStatusCode.NotFound, "{}");
        if (path.EndsWith(".mp4", StringComparison.Ordinal))
        {
            var content = new ByteArrayContent([0]);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, path == "/clip.mp4" ? PromoSize : 7L * MiB);
            if (Mode == "explicit-title" && path == "/clip.mp4") content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = "开通会员.mp4" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
        throw new InvalidOperationException("Unexpected fixture route.");
    }

    private static string ReadCookie(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : string.Empty;

    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body, string? cookie = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (cookie is not null) response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return Task.FromResult(response);
    }

    private static Task<HttpResponseMessage> Redirect(HttpStatusCode status, string url)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(url, UriKind.RelativeOrAbsolute);
        return Task.FromResult(response);
    }
}
