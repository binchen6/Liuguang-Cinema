using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
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
using TV.Services.Packs;
using TV.Services.Persistence;
using TV.Services.Playback;
using TV.Services.Spider;
using TV.Services.Updates;
using TV.Services.Vod;
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
var internationalAddress = new Uri("http://测试.example:8090/目录?固定=1");
using (var internationalHttp = new SourceHttpClient(new InternationalHostFixture()))
{
    var normalized = await internationalHttp.GetStringAsync(internationalAddress.AbsoluteUri,
        new Dictionary<string, string> { ["Referer"] = "https://测试.example/来源" });
    Check(normalized == internationalAddress.IdnHost + "|" + internationalAddress.Port + "|" + internationalAddress.PathAndQuery + "|" +
        "https://" + internationalAddress.IdnHost + "/%E6%9D%A5%E6%BA%90",
        "Source requests encode internationalized hosts and referrers for HTTP proxies while retaining port, path and query");
}
var danmaku = new DanDanPlayService(siteHttp, database);
var danmakuDefaults = DanmakuService.ParseContent("""
    <i>
      <d p="1,1,25,16777215">加群：12345678</d>
      <d p="2,5,25,16777215">广告合作 微信：sample_id</d>
      <d p="3,4,25,16777215">全网低价代充点券！</d>
      <d p="4,1,25,16777215">这个角色后来加群了吗？</d>
      <d p="5,1,25,16777215">剧情在讲刷单和博彩骗局</d>
      <d p="6,1,25,16777215">他说低价代充别信</d>
      <d p="7,1,25,16777215">哈哈哈哈哈哈</d>
      <d p="8,1,25,16777215">原片链接是 https://example.org/</d>
    </i>
    """);
Check(DanmakuFilter.Default.Apply(danmakuDefaults, default).Comments.Select(c => c.Seconds)
    .SequenceEqual(new[] { 4d, 5d, 6d, 7d, 8d }), "Default filter removes only complete ads and preserves ordinary discussion");

var legacyDanmaku = JsonSerializer.Deserialize<DanmakuPreferences>("""{"BasicFilter":false,"BlockedWords":"a.b，广告组、SPAM"}""")!.Normalize();
Check(legacyDanmaku.BlockedPatterns.Length == 0, "Old settings retain literal blocked words without migration");
var customDanmaku = legacyDanmaku with { BlockedPatterns = "^跳过[0-9]{2,4}$\r\n^弹幕 (?=样例)样例$\r\n\r\n(?i)^mUTE$" };
var danmakuInputs = new[] { "a.b", "aXb", "这是广告组", "Spam", "跳过120", "跳过1", "弹幕 样例", "弹幕示例", "MUTE", "加群：12345678" }
    .Select((line, i) => new DanmakuEntry(i, line, 0xFFFFFF)).ToArray();
Check(DanmakuFilter.TryCreate(customDanmaku, out var customFilter, out _) &&
    customFilter.Apply(danmakuInputs, default).Comments.Select(c => c.Text)
        .SequenceEqual(new[] { "aXb", "跳过1", "弹幕示例", "加群：12345678" }),
    "Regex and literal matching preserve commas, spaces, line endings, and case behavior");
Check(!DanmakuFilter.TryCreate(customDanmaku with { BlockedPatterns = "正常\r\n\r\n[" }, out _, out var regexError) &&
    regexError.Contains("第 3 行", StringComparison.Ordinal), "Invalid regex validation reports the original line number");
Check(ReferenceEquals(danmakuInputs, DanmakuFilter.Default.WithBasicFilter(false).Apply(danmakuInputs, default).Comments),
    "Turning off all rules returns the original array");
using (var cancelledFiltering = new CancellationTokenSource())
{
    cancelledFiltering.Cancel();
    await CheckThrows<OperationCanceledException>(() => Task.Run(() => DanmakuFilter.Default.Apply(danmakuInputs, cancelledFiltering.Token)),
        "A superseded danmaku filter can stop before processing comments");
}
var unicodeDanmaku = new string('字', 99) + "😊";
Check(DanmakuService.ParseContent($"\t<i><d p='1,1,25,16777215'>{unicodeDanmaku}</d></i>").Single().Text.Length == 99,
    "Local XML accepts leading whitespace and preserves well-formed UTF-16 when clipping long comments");

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

// --- 1.9.8 Regressions: Custom repo naming/renaming, Native+Universal expansion, js:/CryptoJS AES, GitHubMirror, RepoPack ---
var customDb = new DatabaseService();
using var customHttp = new SourceHttpClient(new RelayAndUniversalFixture());
using var customCfg = new SourceConfigService(customHttp, customDb);
await customCfg.ImportAsync("https://repo-fixture.example.invalid/sub/a.json", "我的专属片库");
Check(customCfg.ActiveRepository?.Name == "我的专属片库", "ImportAsync applies custom repository name");
await customCfg.RenameImportAsync("https://repo-fixture.example.invalid/sub/a.json", "重命名后片库");
Check(customCfg.ActiveRepository?.Name == "重命名后片库", "RenameImportAsync updates active repository display name immediately");
var reloadedImports = await customCfg.GetImportsAsync();
Check(reloadedImports.Any(r => r.Name == "重命名后片库"), "Renamed repository persists in SQLite");

var arrExtSite = new SourceSite { Type = 3, Api = "csp_Libvio", Ext = JsonDocument.Parse("""{"site":["https://libvio.app","https://libvio.pw"]}""").RootElement.Clone() };
var commaExtSite = new SourceSite { Type = 3, Api = "csp_WenCai", Ext = JsonDocument.Parse("\"https://a.example.invalid,https://b.example.invalid\"").RootElement.Clone() };
Check(NativeSiteToolkit.MergeConfiguredOrigins(arrExtSite, []).Count() == 2, "MergeConfiguredOrigins parses JSON array ext.site");
Check(NativeSiteToolkit.MergeConfiguredOrigins(commaExtSite, []).Count() == 2, "MergeConfiguredOrigins parses comma-separated ext URLs");

Check(BilibiliVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_Bili", Ext = JsonDocument.Parse("""{"cookie":""}""").RootElement.Clone() }), "BilibiliVodProvider supports csp_Bili without json URL");
Check(WoggNativeProvider.Supports(new SourceSite { Type = 3, Api = "csp_FishCloud", Ext = JsonDocument.Parse("""{"site":["https://tv.yydsys.top"]}""").RootElement.Clone() }), "WoggNativeProvider supports csp_FishCloud with ext.site array");
Check(ChangZhangVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_CZ", Ext = JsonDocument.Parse("""{"site":["https://www.4kcz.com"]}""").RootElement.Clone() }), "ChangZhangVodProvider supports csp_CZ with object ext");
Check(JpysVodProvider.Supports(commaExtSite), "JpysVodProvider supports csp_WenCai");
Check(GuaziVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_Guazi" }), "GuaziVodProvider supports csp_Guazi");
Check(DoubanVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_PianDan" }), "DoubanVodProvider supports csp_PianDan");
Check(ShortDramaVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_DJHub" }), "ShortDramaVodProvider supports csp_DJHub");
Check(KanqiuVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_Sir88" }), "KanqiuVodProvider supports csp_Sir88");
Check(UniversalProtocolVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_Kan360" }), "UniversalProtocolVodProvider supports csp_Kan360");
Check(UniversalProtocolVodProvider.Supports(new SourceSite { Type = 3, Api = "csp_Market", Ext = JsonDocument.Parse("""{"categories":[{"name":"测试","url":"https://pan.quark.cn/s/969cc97687d3"}]}""").RootElement.Clone() }), "UniversalProtocolVodProvider supports csp_Market CloudShare catalog");

var autoHtmlProvider = new UniversalAutoHtmlVodProvider(customHttp);
var autoHtmlSite = new SourceSite { Key = "wn_test", Name = "蜗牛测试", Type = 3, Api = "csp_WoNiu", Ext = JsonDocument.Parse("""{"site":"https://autohtml.example.invalid/"}""").RootElement.Clone() };
Check(UniversalAutoHtmlVodProvider.Supports(autoHtmlSite), "UniversalAutoHtmlVodProvider supports csp_* with plaintext ext.site");
var autoHome = await autoHtmlProvider.GetHomeAsync(autoHtmlSite);
Check(autoHome.Categories.Count >= 2 && autoHome.List.Count >= 1 && autoHome.List[0].Name == "银河漫游", "UniversalAutoHtmlVodProvider extracts categories and Panlian cards generically");

using var discoveryFixture = new AutomaticDiscoveryFixture();
using var discoveryHttp = new SourceHttpClient(discoveryFixture);
using var discoveryConfig = new SourceConfigService(discoveryHttp, customDb);
await discoveryConfig.ImportAsync("https://archive.example.invalid/repositories/depot.json");
var discoveryRepos = discoveryConfig.Repositories.Where(repo => !repo.IsBuiltIn && repo.Url.Contains("archive.example.invalid", StringComparison.Ordinal)).ToArray();
Check(discoveryRepos.Length == 2, "Nested repositories deduplicate redirects and retain usable leaves after a partial failure");
var htmlSite = discoveryRepos.Single(repo => repo.Name == "HTML").Document.Sites.Single();
var cmsSite = discoveryRepos.Single(repo => repo.Name == "CMS").Document.Sites.Single();
Check(htmlSite.Key == cmsSite.Key && htmlSite.Searchable == 1 && htmlSite.Filterable == 0 &&
    htmlSite.Ext.GetProperty("config").GetProperty("sites")[1].GetString() == "https://archive.example.invalid/repositories/a/catalog/",
    "Repository import resolves nested relative URLs and boolean capability flags");
Check(UniversalAutoHtmlVodProvider.Supports(cmsSite), "An embedded JSON ext string retains its normalized CMS endpoint");
var autodetect = new UniversalAutoHtmlVodProvider(discoveryHttp);
var coldSearch = await autodetect.SearchAsync(htmlSite, "科学 站", 1);
var discoveredHome = await autodetect.GetHomeAsync(htmlSite);
Check(coldSearch.List.Single().Name == "科学搜索结果" && discoveredHome.List.Single().Name == "太空站" && discoveryFixture.HtmlHomeReads == 1,
    "Cold search discovers the site's GET form, tries the backup subpath, and reuses home discovery");
var categoryId = discoveredHome.Categories.Single().Id;
var firstPage = await autodetect.GetCategoryAsync(htmlSite, categoryId, 1);
var lastPage = await autodetect.GetCategoryAsync(htmlSite, categoryId, 2);
Check(firstPage.PageCount == 2 && lastPage.PageCount == 2 && lastPage.List.Single().Name == "第二页",
    "HTML pagination follows the real cursor link and terminates at the last page");
var htmlDetail = await autodetect.GetDetailAsync(htmlSite, discoveredHome.List.Single().Id);
var htmlEpisode = htmlDetail.PlayUrl.Split('$', 2)[1];
var htmlPlay = await autodetect.ResolvePlayAsync(htmlSite, "在线播放", htmlEpisode, default);
Check(htmlPlay.Url == "https://media.example.invalid/files/stream" && htmlPlay.Parse == 0 && htmlPlay.ContentType == "video/mp4" &&
    htmlPlay.ReplaceHeaders && !htmlPlay.Headers.ContainsKey("Cookie"),
    "Semantic video elements resolve without a browser and cross-origin playback excludes site credentials");
var audioStream = await autodetect.ResolvePlayAsync(htmlSite, "音频", "https://media.example.invalid/files/audio", default);
Check(audioStream.Parse == 0 && audioStream.ContentType == "audio/flac" && discoveryFixture.AudioReadAttempts == 1,
    "Extensionless audio is classified from response headers without buffering the media");
var cmsResult = await autodetect.SearchAsync(cmsSite, "纸飞机", 1);
Check(cmsResult.List.Single().Id == "cms-one" && cmsResult.PageCount == 1 && (await autodetect.GetHomeAsync(htmlSite)).List.Single().Id.StartsWith('/'),
    "Two repositories with the same site key keep independent HTML and CMS routes without phantom pagination");
discoveryFixture.PrimaryCmsAvailable = false;
var onBackup = await autodetect.SearchAsync(cmsSite, "纸飞机", 1);
Check(onBackup.List.Single().Id == "cms-backup", "A stale CMS route returning 412 is rediscovered on its configured backup endpoint");
var readyT4 = await new HttpSpiderProvider(discoveryHttp).ResolvePlayAsync(
    new SourceSite { Type = 4, Api = "https://archive.example.invalid/no-t4-player" }, "默认", "https://media.example.invalid/files/video.mp4", default);
Check(readyT4.Parse == 0 && readyT4.Url.EndsWith("/video.mp4", StringComparison.Ordinal), "A type-4 direct stream does not require a player endpoint");

var jsAesClient = new JsSpiderClient(customHttp);
var jsAesRule = """
    var rule = {
        title: 'JS规则与AES测试',
        host: 'https://drpy.example.invalid',
        url: '/list/fyclass-fypage.html',
        class_name: '电影',
        class_url: '1',
        推荐: 'js: var c = CryptoJS.AES.encrypt("流光AES测试", "1234567890abcdef", { iv: "abcdef1234567890", mode: CryptoJS.mode.CBC, padding: CryptoJS.pad.Pkcs7 }).toString(); var p = CryptoJS.AES.decrypt(c, "1234567890abcdef", { iv: "abcdef1234567890", mode: CryptoJS.mode.CBC, padding: CryptoJS.pad.Pkcs7 }).toString(); VODS = [{ vod_id: "aes_1", vod_name: p, vod_pic: "", vod_remarks: "OK" }];'
    };
    """;
var jsAesHome = await jsAesClient.ExecuteInlineHomeAsync(new SourceSite { Key = "js_aes", Name = "JS测试", Type = 3, Api = "drpy" }, jsAesRule);
Check(jsAesHome.List.Count == 1 && jsAesHome.List[0].Name == "流光AES测试", "JsSpiderClient executes drpy2 js: blocks and real CryptoJS.AES round-trip");

var jsSelectorRule = """
    var rule = {
        title: '选择器测试',
        host: 'https://m.ting15.com',
        url: '/list/fyclass-fypage.html',
        class_name: '有声',
        class_url: '1',
        推荐: 'js: var h = "<div class=\\"binfo\\"><p>播音：A</p><p>作者：B</p></div><div class=\\"tlist\\"><a href=\\"/book/1.html\\"><h3>书名</h3></a></div>"; var card = pdfa(h, ".tlist a")[0]; VODS = [{ vod_id: pd(card, "a&&href", "https://m.ting15.com/"), vod_name: pdfh(h, ".binfo p:eq(1)&&Text"), vod_pic: "", vod_remarks: "OK" }];'
    };
    """;
var jsSelHome = await jsAesClient.ExecuteInlineHomeAsync(new SourceSite { Key = "js_sel", Name = "选择器", Type = 3, Api = "drpy" }, jsSelectorRule);
Check(jsSelHome.List.Count == 1 && jsSelHome.List[0].Name == "作者：B" && jsSelHome.List[0].Id == "https://m.ting15.com/book/1.html",
    "JsSpiderClient :eq() selector and self-anchor a&&href resolve accurately");

var verifyBundled = args.Contains("--verify-bundled-runtimes");
if (verifyBundled || args.Contains("--verify-python"))
{
    if (verifyBundled)
    {
        var bundledPython = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        Check(File.Exists(bundledPython) && File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "MicrosoftEdgeWebView2Setup.exe")),
            "The Windows payload includes Python and the WebView2 bootstrapper");
        Environment.SetEnvironmentVariable("TV_PYTHON_EXECUTABLE", bundledPython);
    }
    var pythonScript = Path.Combine(Directory.CreateDirectory(Path.Combine(args[0], "测试 爬虫")).FullName, "本地.py");
    await File.WriteAllTextAsync(pythonScript, """
        from base.spider import Spider as BaseSpider
        class Spider(BaseSpider):
            def init(self, extend):
                self.extend = extend
            def homeContent(self, filter=False):
                print("fixture noise")
                return {"list": [{"vod_id": "local", "vod_name": self.extend}]}
        """);
    var python = new PySpiderClient(siteHttp);
    var pythonSite = new SourceSite
    {
        Key = "py_fixture", Name = "Python", Type = 3, Api = new Uri(pythonScript).AbsoluteUri,
        Ext = JsonSerializer.SerializeToElement("中文 Python 回归")
    };
    var pythonResponses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => python.GetHomeAsync(pythonSite)));
    Check(pythonResponses.All(item => item.List.Single().Name == "中文 Python 回归"),
        "Python launcher, concurrent requests, UTF-8 IPC, and non-ASCII paths work together");
    if (verifyBundled)
    {
        Environment.SetEnvironmentVariable("JAVA_HOME", Path.Combine(args[0], "不存在的 JDK"));
        Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", "-XX:InvalidFixtureOption");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
        using var java = new SpiderRuntime(customDb);
        JsonElement javaResponse;
        try { javaResponse = await java.SendAsync(new SourceSite { Key = "java_fixture", Api = "csp_PanTa", Type = 3 }, "home", []); }
        catch
        {
            var log = Path.Combine(AppPaths.Root, "diagnostic.log");
            if (File.Exists(log))
                foreach (var line in File.ReadLines(log).Where(line => line.Contains("Spider Java startup", StringComparison.Ordinal)))
                    Console.Error.WriteLine(line);
            throw;
        }
        finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
        Check(javaResponse.ValueKind == JsonValueKind.Object && javaResponse.TryGetProperty("list", out _),
            "The bundled Java engine answers IPC without using the machine's JAVA_HOME or JVM options");
    }
}

var mirrorRoutes = GitHubMirrorService.BuildCandidateRoutes("https://github.com/binchen6/Liuguang-RepoPacks/releases/download/v1.0.0/y456y-pack-v1.0.0.zip");
Check(mirrorRoutes.Count == 6 && mirrorRoutes[0].IsMirror && !mirrorRoutes[^1].IsMirror, "GitHubMirrorService prioritizes all 5 China mirrors before direct GitHub");

var packRoot = Path.Combine(args[0], "test-packs");
Directory.CreateDirectory(packRoot);
var packStage = Path.Combine(args[0], "pack-src");
Directory.CreateDirectory(packStage);
File.WriteAllText(Path.Combine(packStage, "pack.json"), """
    {
      "id": "demo",
      "name": "演示资源包",
      "version": "1.0.0",
      "matchHosts": ["www.y456y.com"],
      "sites": [
        { "api": "csp_DemoInfo", "kind": "info", "title": "测试说明", "message": "资源包生效" }
      ]
    }
    """, Encoding.UTF8);
var demoZip = Path.Combine(args[0], "demo-pack.zip");
if (File.Exists(demoZip)) File.Delete(demoZip);
ZipFile.CreateFromDirectory(packStage, demoZip);
var demoSha = await GitHubMirrorService.ComputeFileSha256Async(demoZip);

// Verify zip-slip defense
var evilZip = Path.Combine(args[0], "evil-pack.zip");
using (var za = ZipFile.Open(evilZip, ZipArchiveMode.Create))
{
    var entry = za.CreateEntry("../escape.txt");
    using var sw = new StreamWriter(entry.Open());
    sw.Write("bad");
}
await CheckThrows<InvalidDataException>(() => { RepoPackService.ExtractZipSafely(evilZip, Path.Combine(args[0], "evil-out")); return Task.CompletedTask; },
    "RepoPackService blocks Zip-Slip path traversal");

// Verify in-memory manifest + SHA-256 install + hot reload + RepoPackVodProvider execution + uninstall
var demoZipBytes = await File.ReadAllBytesAsync(demoZip);
var packManifestJson = $$"""
    {
      "schemaVersion": 1,
      "packs": [{
        "id": "demo",
        "name": "演示资源包",
        "version": "1.0.0",
        "matchHosts": ["www.y456y.com"],
        "downloadUrl": "https://github.com/binchen6/Liuguang-RepoPacks/releases/download/v1.0.0/demo-pack.zip",
        "sha256": "{{demoSha}}",
        "siteCount": 1
      }]
    }
    """;
using var packFixtureHandler = new RepoPackFixtureHandler(packManifestJson, demoZipBytes);
using var packFixtureHttp = new SourceHttpClient(packFixtureHandler);
using var packDns = new DnsResolverService(customDb);
using var packSvc = new RepoPackService(
    packFixtureHttp,
    packDns,
    packRoot,
    "https://raw.githubusercontent.com/binchen6/Liuguang-RepoPacks/main/manifest.json",
    packFixtureHandler);
var autoRes = await packSvc.AutoInstallForRepositoryAsync("http://www.y456y.com", null);
Check(autoRes.Matched && autoRes.Installed && packSvc.TryGetInstalled("demo", out _), "RepoPackService matches host, downloads via China mirror, verifies SHA-256, and auto-installs pack");
Check(packFixtureHandler.SawMirrorDownload && packFixtureHandler.SawOriginManifest,
    "RepoPackService reads the digest from the origin and uses mirrors for the verified ZIP");
using (var alternatePack = new RepoPackService(packFixtureHttp, packDns, Path.Combine(args[0], "alternate-packs")))
{
    packFixtureHandler.RawUnavailable = true;
    packFixtureHandler.UseApiEnvelope = true;
    Check((await alternatePack.GetManifestAsync()).Packs.Single().Id == "demo" && packFixtureHandler.SawApiManifest,
        "Resource-pack refresh reads the GitHub contents envelope if the raw endpoint is unreachable");
    packFixtureHandler.ApiUnavailable = true;
    packFixtureHandler.CancelManifest = true;
    var cachedCatalog = await alternatePack.GetCatalogAsync(forceRefresh: true);
    Check(cachedCatalog.Manifest.Packs.Single().Id == "demo" && cachedCatalog.Source == RepoPackCatalogSource.Cached,
        "An endpoint cancellation retains the last origin-verified catalog and reports its offline state");
    var restartRoot = Path.Combine(args[0], "clean-offline-pack-profile");
    using var offline = new RepoPackService(packFixtureHttp, packDns, restartRoot,
        RepoPackService.DefaultManifestUrl, packFixtureHandler);
    Check((await offline.GetLocalCatalogAsync())?.Source == RepoPackCatalogSource.Bundled,
        "A fresh profile can show the bundled catalog before connecting");
    var bundled = await offline.GetCatalogAsync(forceRefresh: true);
    Check(bundled.Source == RepoPackCatalogSource.Bundled && bundled.Manifest.Packs.Any(pack => pack.Id == "y456y"),
        "Both official endpoints cancel without preventing a clean Windows profile from listing the default pack");
    var offlineInstall = await offline.AutoInstallForRepositoryAsync("http://www.y456y.com", null);
    Check(offlineInstall.Installed && offline.TryGetInstalled("y456y", out _) && offlineInstall.RouteLabel == "客户端自带资源包",
        "A verified bundled ZIP can activate the matching repository without downloading");
    using var explicitCancellation = new CancellationTokenSource();
    explicitCancellation.Cancel();
    await CheckThrows<OperationCanceledException>(() => offline.GetCatalogAsync(true, explicitCancellation.Token),
        "Explicit caller cancellation still cancels the refresh");
}
packFixtureHandler.RawUnavailable = packFixtureHandler.ApiUnavailable = false;
packFixtureHandler.CancelManifest = packFixtureHandler.UseApiEnvelope = false;
var mismatchedEntry = new RepoPackManifestEntry
{
    Id = "demo", Version = "2.0.0", DownloadUrl = "https://github.com/binchen6/Liuguang-RepoPacks/releases/download/v1.0.0/demo-pack.zip", Sha256 = demoSha
};
await CheckThrows<InvalidDataException>(() => packSvc.InstallPackAsync(mismatchedEntry),
    "A ZIP with a conflicting version cannot replace the installed pack");
using var packCloud = new CloudDriveService(customDb, packDns);
var packBili = new BilibiliVodProvider(customHttp, packCloud);
var packNative = NativeVodProviderRouter.CreateDefault(customHttp, packCloud, packBili);
var packVod = new RepoPackVodProvider(packSvc, packNative, packBili, jsAesClient, new CmsClient(customHttp));
var infoHome = await packVod.GetHomeAsync(new SourceSite { Key = "d1", Name = "演示", Type = 3, Api = "csp_DemoInfo" });
Check(infoHome.List.Count == 1 && infoHome.List[0].Content == "资源包生效", "RepoPackVodProvider executes installed pack rule immediately");
using var fallbackRuntime = new SpiderRuntime(customDb);
var routed = new VodClientRouter(new CmsClient(discoveryHttp), new SpiderVodClient(fallbackRuntime), packNative,
    autoHtml: autodetect, repoPack: packVod);
var unsupportedWeb = new SourceSite
{
    Key = "pack-fallback", Api = "csp_DemoInfo", Type = 3, Name = "通用不匹配",
    Ext = JsonSerializer.SerializeToElement("https://archive.example.invalid/blank/")
};
var fallbackHome = await routed.GetHomeAsync(unsupportedWeb);
var fallbackDetail = await routed.GetDetailAsync(unsupportedWeb, fallbackHome.List.Single().Id);
Check(fallbackDetail.Content == "资源包生效", "A matched package serves detail after a confirmed generic mismatch");
var verifiedOnly = new SourceSite
{
    Key = "pack-verification", Api = "csp_DemoInfo", Type = 3, Name = "验证站点",
    Ext = JsonSerializer.SerializeToElement("https://archive.example.invalid/verification/")
};
await CheckThrows<SiteVerificationRequiredException>(() => routed.GetHomeAsync(verifiedOnly),
    "The package fallback does not replace a site's verification requirement");
await packSvc.RemovePackAsync("demo");
Check(!SpiderCatalog.IsAvailable("csp_DemoInfo"), "Removing a pack removes its dynamically registered API");
Check(!packSvc.TryGetInstalled("demo", out _), "RepoPackService uninstalls pack cleanly");
var manualPack = await packSvc.ImportPackAsync(demoZip);
Check(manualPack.Id == "demo" && packVod.CanHandle(new SourceSite { Api = "csp_DemoInfo" }),
    "Manual ZIP import installs an adaptation offline and hot-enables its rule");
await CheckThrows<InvalidDataException>(() => packSvc.ImportPackAsync(evilZip),
    "Manual ZIP import rejects an invalid archive before replacing installed rules");
Check(packSvc.TryGetInstalled("demo", out _), "A rejected manual ZIP leaves the installed package active");

var lyricLines = MusicMetadataService.ParseLyrics("[offset:-150]\n[00:02.05][00:05.120]第一句\n[00:02.05]译文\n[00:04]中间");
Check(lyricLines.Count == 3 && lyricLines[0].TimeMs == 1900 && lyricLines[0].Text == "第一句\n译文" &&
    MusicMetadataService.ActiveLine(lyricLines, 1899) == -1 && MusicMetadataService.ActiveLine(lyricLines, 4500) == 1,
    "Timed lyrics apply offsets, merge translations and locate the current line after a seek");
Check(AudioMedia.IsAudio("https://music.example.invalid/recording.FLAC?quality=original") &&
    !AudioMedia.IsAudio("https://music.example.invalid/recording.mp4?title=track.mp3") &&
    new LocalMediaEntry(@"C:\Music\条目.opus", "条目", "", 4, DateTimeOffset.Now).IsMusic,
    "Music and video classification handles local filenames and signed stream URLs");

// Cross-version & incremental update hardening regressions
{
    var updateTestRoot = Path.Combine(args[0], "update-hardening");
    var installDir = Path.Combine(updateTestRoot, "app");
    var cacheDir = Path.Combine(updateTestRoot, "cache");
    Directory.CreateDirectory(Path.Combine(installDir, "runtime", "java", "bin"));
    Directory.CreateDirectory(Path.Combine(installDir, "runtime", "python"));
    Directory.CreateDirectory(Path.Combine(installDir, "libvlc", "win-x64"));
    Directory.CreateDirectory(cacheDir);

    var sampleFiles = new Dictionary<string, byte[]>
    {
        ["TV.exe"] = Encoding.UTF8.GetBytes("tv-exe-v201"),
        ["TV.dll"] = Encoding.UTF8.GetBytes("tv-dll-v201"),
        ["TV.pri"] = Encoding.UTF8.GetBytes("tv-pri-v201"),
        [@"runtime\catvod-engine.jar"] = Encoding.UTF8.GetBytes("jar-v201"),
        [@"runtime\java\bin\java.exe"] = Encoding.UTF8.GetBytes("java-v201"),
        [@"runtime\python\python.exe"] = Encoding.UTF8.GetBytes("py-v201"),
        [@"libvlc\win-x64\libvlc.dll"] = Encoding.UTF8.GetBytes("vlc-v201"),
        ["extra.txt"] = Encoding.UTF8.GetBytes("extra-v201")
    };
    var manifestBuilder = new List<string> { "# version=2.0.1" };
    foreach (var (rel, bytes) in sampleFiles)
    {
        var path = Path.Combine(installDir, rel);
        File.WriteAllBytes(path, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        manifestBuilder.Add($"{sha}|{bytes.Length}|{rel}");
    }

    var fakeDigest = new string('a', 64);
    var release202 = new AppUpdateRelease(
        new Version(2, 0, 2), "v2.0.2", "流光影院 2.0.2", "常规更新",
        "https://github.com/binchen6/Liuguang-Cinema/releases/tag/v2.0.2",
        DateTimeOffset.UtcNow,
        [
            new AppUpdateAsset("TV-Setup-2.0.2-x64.exe", 160_000_000, "https://github.com/binchen6/Liuguang-Cinema/releases/download/v2.0.2/TV-Setup-2.0.2-x64.exe", fakeDigest),
            new AppUpdateAsset("TV-Update-2.0.2-from-2.0.1-x64.exe", 8_000_000, "https://github.com/binchen6/Liuguang-Cinema/releases/download/v2.0.2/TV-Update-2.0.2-from-2.0.1-x64.exe", fakeDigest)
        ]);

    // 1. Missing install-manifest.txt -> forces full installer
    var noManifestPkg = AppUpdateService.SelectPackageCore(release202, true, new Version(2, 0, 1), installDir, cacheDir);
    Check(!noManifestPkg.Incremental && noManifestPkg.Asset.Name == "TV-Setup-2.0.2-x64.exe",
        "Missing install-manifest.txt forces full installer instead of unsafe incremental patch");

    // 2. Valid manifest -> selects incremental patch
    File.WriteAllLines(Path.Combine(installDir, "install-manifest.txt"), manifestBuilder, new UTF8Encoding(false));
    var intactPkg = AppUpdateService.SelectPackageCore(release202, true, new Version(2, 0, 1), installDir, cacheDir);
    Check(intactPkg.Incremental && intactPkg.Asset.Name == "TV-Update-2.0.2-from-2.0.1-x64.exe",
        "Intact manifest selects incremental update package for matching base version");

    // 3. Cross-version jump (1.9.7 -> 2.0.2) -> automatically selects full installer
    var crossVersionPkg = AppUpdateService.SelectPackageCore(release202, true, new Version(1, 9, 7), installDir, cacheDir);
    Check(!crossVersionPkg.Incremental && crossVersionPkg.Asset.Name == "TV-Setup-2.0.2-x64.exe",
        "Cross-version upgrade automatically selects full installer");

    // 4. Release-level [FORCE-FULL-UPDATE] flag -> forces full installer even when delta exists
    var forceFullRelease = release202 with { Notes = "[FORCE-FULL-UPDATE]\n重大运行时升级" };
    var forceFullPkg = AppUpdateService.SelectPackageCore(forceFullRelease, true, new Version(2, 0, 1), installDir, cacheDir);
    Check(!forceFullPkg.Incremental && forceFullPkg.Asset.Name == "TV-Setup-2.0.2-x64.exe",
        "Release [FORCE-FULL-UPDATE] signal forces full installer");

    // 5. Failed previous incremental install -> auto-recovers with full installer
    AppUpdateService.RecordInstallAttemptCore(true, "2.0.1", "2.0.2", cacheDir);
    var retryAfterFailPkg = AppUpdateService.SelectPackageCore(release202, true, new Version(2, 0, 1), installDir, cacheDir);
    Check(!retryAfterFailPkg.Incremental && retryAfterFailPkg.Asset.Name == "TV-Setup-2.0.2-x64.exe",
        "Previous failed incremental attempt automatically falls back to full installer");
    Check(!AppUpdateService.WasPreviousIncrementalFailed("2.0.2", "2.0.3", cacheDir) &&
          !File.Exists(Path.Combine(cacheDir, "incremental-attempt.txt")),
        "Successful upgrade clears previous incremental attempt marker");

    // 6. Tampered core binary -> detected by SHA-256 check and falls back to full installer
    File.WriteAllBytes(Path.Combine(installDir, "TV.dll"), Encoding.UTF8.GetBytes("tv-dll-XXXX"));
    var tamperedPkg = AppUpdateService.SelectPackageCore(release202, true, new Version(2, 0, 1), installDir, cacheDir);
    Check(!tamperedPkg.Incremental && tamperedPkg.Asset.Name == "TV-Setup-2.0.2-x64.exe",
        "Tampered core file hash fails incremental pre-flight and switches to full installer");
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

sealed class InternationalHostFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(uri.Host + "|" + uri.Port + "|" + uri.PathAndQuery + "|" + request.Headers.Referrer?.AbsoluteUri)
        });
    }
}

sealed class AutomaticDiscoveryFixture : HttpMessageHandler
{
    internal int HtmlHomeReads { get; private set; }
    internal int AudioReadAttempts { get; private set; }
    internal bool PrimaryCmsAvailable { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var query = uri.Query;
        return Task.FromResult(Reply());

        HttpResponseMessage Reply()
        {
            if (uri.Host == "media.example.invalid" && path == "/files/audio")
            {
                AudioReadAttempts++;
                var audio = Response("", "audio/flac");
                audio.Content.Headers.ContentLength = 16 * 1024 * 1024;
                return audio;
            }
            if (uri.Host != "archive.example.invalid") return Response("", status: HttpStatusCode.NotFound);
            if (path == "/repositories/depot.json") return Response("""
                {"urls":[{"name":"HTML","url":"./a/leaf.json"},{"name":"重复","url":"./alias.json"},
                {"name":"分发","url":"./nested.json"},{"name":"失效","url":"./gone.json"}]}
                """);
            if (path == "/repositories/alias.json")
            {
                var redirect = Response("", status: HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("./a/leaf.json", UriKind.Relative);
                return redirect;
            }
            if (path == "/repositories/nested.json")
                return Response("""{"storeHouse":[{"sourceName":"CMS","sourceUrl":"./b/leaf.json"}],"urls":[{"name":"循环","url":"./depot.json"}]}""");
            if (path == "/repositories/a/leaf.json") return Response("""
                {"sites":[null,{"key":"shared","name":"子目录","type":"3","api":"csp_FixtureUnknown","searchable":true,
                "filterable":false,"header":{"Cookie":"fixture=opaque"},
                "ext":{"config":{"sites":["./old/","./catalog/"]}}}]}
                """);
            if (path == "/repositories/b/leaf.json") return Response("""
                {"sites":[{"key":"shared","name":"无首页接口","type":3,"api":"csp_FixtureUnknown",
                "ext":"{\"site\":[\"./api.php/provide/vod/?source=archive\",\"./backup/api/vod?source=archive\"]}"}]}
                """);
            if (path == "/repositories/a/old/") return Response("", status: HttpStatusCode.ServiceUnavailable);
            if (path == "/repositories/a/catalog/")
            {
                HtmlHomeReads++;
                return Response("""
                    <html><header><nav><a href="category/science/">科学</a></nav></header>
                      <form method="get" action="search?collection=lab"><input type="search" name="keyword"></form>
                    """ + Card("first", "太空站") + "</html>", "text/html");
            }
            if (path == "/repositories/a/catalog/search" && query.Contains("collection=lab", StringComparison.Ordinal) &&
                Uri.UnescapeDataString(query).Contains("keyword=科学 站", StringComparison.Ordinal))
                return Response("<html>" + Card("first", "科学搜索结果") + "</html>", "text/html");
            if (path == "/repositories/a/catalog/category/science/")
                return query == "?cursor=next"
                    ? Response("<html>" + Card("second", "第二页") + "</html>", "text/html")
                    : Response("<html>" + Card("first", "第一页") + "<nav><a rel='next' href='?cursor=next'>下一页</a></nav></html>", "text/html");
            if (path == "/repositories/a/catalog/movie/first")
                return Response("""
                    <html><h1>太空站</h1><video><source src="https://media.example.invalid/files/stream" type="video/mp4"></video></html>
                    """, "text/html");
            if (path is "/repositories/b/api.php/provide/vod/" or "/repositories/b/backup/api/vod")
            {
                if (!query.Contains("source=archive", StringComparison.Ordinal))
                    return Response("", status: HttpStatusCode.NotFound);
                if (path.EndsWith("/vod/", StringComparison.Ordinal) && !PrimaryCmsAvailable)
                    return Response("", status: HttpStatusCode.PreconditionFailed);
                return Response(path.EndsWith("/vod/", StringComparison.Ordinal)
                    ? """{"class":[{"type_id":"1","type_name":"短片"}],"pagecount":1,"list":[{"vod_id":"cms-one","vod_name":"纸飞机"}]}"""
                    : """{"data":{"pagecount":"1","list":[{"vod_id":"cms-backup","vod_name":"备用纸飞机"}]}}""");
            }
            if (path == "/blank/") return Response("<html><h1>没有公开目录</h1></html>", "text/html");
            if (path == "/verification/") return Response("<html><h1>请人工验证</h1></html>", "text/html", HttpStatusCode.Forbidden);
            return Response("", status: HttpStatusCode.NotFound);
        }

        HttpResponseMessage Response(string body, string mime = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, mime), RequestMessage = request };

        static string Card(string id, string title) =>
            $"<main><article><a href='/repositories/a/catalog/movie/{id}'><img src='poster.jpg' alt='{title}'></a></article></main>";
    }
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
        if (host == "autohtml.example.invalid")
        {
            const string html = """
                <html><body>
                <nav class="nav-links"><a href="/vodtype/1/">电影</a><a href="/vodtype/2/">连续剧</a></nav>
                <form action="/vodsearch/-------------/"><input name="wd" /></form>
                <div class="video-grid">
                  <a href="/voddetail/9001/" class="video-card" title="银河漫游">
                    <div class="video-poster"><img src="/upload/9001.jpg" /><div class="video-episode">4K</div></div>
                    <div class="video-info"><div class="video-title">银河漫游</div></div>
                  </a>
                </div>
                </body></html>
                """;
            return Reply(HttpStatusCode.OK, html, "text/html");
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

sealed class RepoPackFixtureHandler(string manifestJson, byte[] zipBytes) : HttpMessageHandler
{
    internal bool SawMirrorDownload { get; private set; }
    internal bool SawOriginManifest { get; private set; }
    internal bool SawApiManifest { get; private set; }
    internal bool RawUnavailable { get; set; }
    internal bool ApiUnavailable { get; set; }
    internal bool UseApiEnvelope { get; set; }
    internal bool CancelManifest { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (request.RequestUri?.AbsolutePath.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var raw = request.RequestUri.Host == "raw.githubusercontent.com";
            SawOriginManifest |= raw;
            SawApiManifest |= !raw && request.RequestUri.Host == "api.github.com" &&
                request.Headers.Accept.Any(media => media.MediaType == "application/vnd.github.raw+json");
            if (CancelManifest)
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("The operation was canceled."));
            if (raw ? RawUnavailable : ApiUnavailable)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(!raw && UseApiEnvelope
                    ? JsonSerializer.Serialize(new { encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestJson)) })
                    : manifestJson, Encoding.UTF8, "application/json")
            });
        }
        if (url.EndsWith("demo-pack.zip", StringComparison.OrdinalIgnoreCase))
        {
            SawMirrorDownload |= request.RequestUri is { } uri && GitHubMirrorService.IsAllowedMirrorHost(uri.Host);
            var content = new ByteArrayContent(zipBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Headers.ContentLength = zipBytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
