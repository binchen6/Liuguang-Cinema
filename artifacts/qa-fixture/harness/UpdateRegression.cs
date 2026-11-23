global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Updates;

var state = Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("TV_DATA_ROOT", state);
Directory.CreateDirectory(state);
var listener = new HttpListener();
var port = 23000 + Random.Shared.Next(1000);
while (true)
{
    try
    {
        listener.Prefixes.Clear();
        listener.Prefixes.Add($"http://127.0.0.1:{port++}/");
        listener.Start();
        break;
    }
    catch (HttpListenerException) when (port < 24500) { }
}
var baseUrl = listener.Prefixes.Single();
Environment.SetEnvironmentVariable("TV_UPDATE_API_URL", baseUrl + "latest");
using var dns = new DnsResolverService(new DatabaseService());
using var http = new SourceHttpClient(dns);
using var service = new AppUpdateService(http, dns, new DatabaseService());
var current = service.CurrentVersion;
var next = new Version(current.Major, current.Minor, current.Build + 1);
var later = new Version(current.Major, current.Minor, current.Build + 2);
var deltaBytes = Encoding.UTF8.GetBytes("verified incremental package payload");
var fullBytes = Encoding.UTF8.GetBytes("verified full installer payload");
var resumes = 0;
var downloads = new List<string>();
string tag = "v" + next;
bool omitDelta = false;
bool corrupt = false;
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
object Asset(string name, byte[] bytes, bool includeDigest = true, string? url = null) => new
{
    name,
    size = bytes.Length,
    browser_download_url = url ?? baseUrl + name,
    digest = includeDigest ? "sha256:" + (corrupt ? new string('0', 64) : Hash(bytes)) : null
};

async Task ServeAsync()
{
    while (listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); }
        catch (Exception) when (!listener.IsListening) { return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var response = context.Response;
                var path = context.Request.Url!.AbsolutePath.TrimStart('/');
                byte[] body;
                if (path == "latest")
                {
                    var assets = new List<object>();
                    if (!omitDelta) assets.Add(Asset($"TV-Update-{next}-from-{current}-x64.exe", deltaBytes));
                    assets.Add(Asset($"TV-Setup-{next}-x64.exe", fullBytes));
                    body = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        tag_name = tag,
                        name = "流光影院验收",
                        body = "## 修复\n- **封面**\n- 更新",
                        html_url = baseUrl + "release",
                        published_at = DateTimeOffset.UtcNow,
                        draft = false,
                        prerelease = false,
                        assets
                    });
                    response.ContentType = "application/json";
                }
                else if (path == $"TV-Update-{next}-from-{current}-x64.exe") body = deltaBytes;
                else if (path == $"TV-Setup-{next}-x64.exe") body = fullBytes;
                else if (path.EndsWith(".sha256")) body = Encoding.ASCII.GetBytes(Hash(path.StartsWith("TV-Update") ? deltaBytes : fullBytes) + " *" + path);
                else { response.StatusCode = 404; response.Close(); return; }
                if (!path.EndsWith(".sha256") && path != "latest")
                {
                    downloads.Add(path + " " + context.Request.Headers["Range"]);
                    var range = context.Request.Headers["Range"];
                    if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes="))
                    {
                        var offset = int.Parse(range[6..].TrimEnd('-'));
                        response.StatusCode = 206;
                        response.AddHeader("Content-Range", $"bytes {offset}-{body.Length - 1}/{body.Length}");
                        body = body[offset..];
                        resumes++;
                    }
                }
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body);
                response.Close();
            }
            catch { }
        });
    }
}
var server = ServeAsync();
try
{
    var stale = Directory.CreateDirectory(Path.Combine(AppPaths.UpdateCache, "0.0.0-to-old"));
    await File.WriteAllBytesAsync(Path.Combine(stale.FullName, "old.exe"), [1]);
    var retained = Directory.CreateDirectory(Path.Combine(AppPaths.UpdateCache, current + "-to-current"));
    await File.WriteAllBytesAsync(Path.Combine(retained.FullName, "current.exe"), [1]);
    var release = await service.CheckAsync();
    Require(!stale.Exists && retained.Exists, "stale update packages cleaned without deleting current downloads");
    Require(release is not null && release.Version == next, "new stable GitHub release detected");
    var package = service.SelectPackage(release!);
    Require(package.Incremental, "matching incremental package preferred");
    var downloaded = await service.DownloadAsync(release!, null, default);
    Require(downloaded.Package.Incremental && await File.ReadAllBytesAsync(downloaded.Path) is var actual && actual.AsSpan().SequenceEqual(deltaBytes), "incremental package verified");
    var requestCount = downloads.Count;
    await service.DownloadAsync(release!, null, default);
    Require(downloads.Count == requestCount, "verified downloaded package reused");

    var partialPath = downloaded.Path + ".partial";
    File.Delete(downloaded.Path);
    await File.WriteAllBytesAsync(partialPath, deltaBytes[..9]);
    await service.DownloadAsync(release!, null, default);
    Require(resumes == 1 && downloads[^1].Contains("bytes=9-"), "interrupted download resumes with a bounded Range request");

    omitDelta = true;
    release = await service.CheckAsync();
    package = service.SelectPackage(release!);
    Require(!package.Incremental, "missing delta falls back to full installer");
    File.Delete(downloaded.Path);
    downloaded = await service.DownloadAsync(release!, null, default);
    Require(!downloaded.Package.Incremental && (await File.ReadAllBytesAsync(downloaded.Path)).AsSpan().SequenceEqual(fullBytes), "full package download and digest verified");

    omitDelta = false;
    corrupt = true;
    var corruptRelease = await service.CheckAsync();
    await AssertFails(() => service.DownloadAsync(corruptRelease!, null, default), "corrupt package rejected");
    Require(!File.Exists(downloaded.Path + ".partial") || new FileInfo(downloaded.Path + ".partial").Length != deltaBytes.Length, "corrupt partial removed");
    corrupt = false;

    tag = "v" + current;
    Require(await service.CheckAsync() is null, "same version does not report an update");
    tag = "v" + later;
    var hostile = await service.CheckAsync();
    var hostileAsset = hostile!.Assets.Single(asset => asset.Name.StartsWith("TV-Update"));
    var hostileRelease = hostile with { Version = next, Assets = [hostileAsset with { DownloadUrl = "https://example.invalid/update.exe", Digest = Hash(deltaBytes) }] };
    await AssertFails(() => service.DownloadAsync(hostileRelease, null, default), "non-GitHub asset rejected");
    Console.WriteLine($"PASS update protocol, requests={downloads.Count}, resume={resumes}");
}
finally
{
    listener.Stop();
    listener.Close();
    await server;
}

static void Require(bool condition, string name)
{
    if (!condition) throw new InvalidDataException("FAIL " + name);
    Console.WriteLine("PASS " + name);
}

static async Task AssertFails(Func<Task> action, string name)
{
    try { await action(); }
    catch (InvalidDataException) { Require(true, name); return; }
    throw new InvalidDataException("FAIL " + name);
}
