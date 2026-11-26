using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TV.Models;
using TV.Services;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Playback;

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
