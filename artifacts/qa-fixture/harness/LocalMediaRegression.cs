global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net;
global using System.Net.Http;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
using TV.Models;
using TV.Services;
using TV.Services.Http;
using TV.Services.Library;
using TV.Services.Maintenance;
using TV.Services.Persistence;
using TV.Services.Playback;
using TV.Services.Vod;
using TV.Services.Vod.Native;

var root = args[0];
Directory.CreateDirectory(root);
var mediaFolder = Path.Combine(root, "media");
Directory.CreateDirectory(mediaFolder);
var database = new DatabaseService();
await database.InitializeAsync();
using (var local = new LocalMediaService(database))
{
    var single = Path.Combine(mediaFolder, "single.mkv");
    File.WriteAllBytes(single, [1, 2, 3]);
    var ignored = Path.Combine(mediaFolder, "notes.txt");
    File.WriteAllText(ignored, "not video");
    Require(await local.ImportFilesAsync([single, ignored]) == 1, "multi-file local import accepts videos and ignores text");
    var nestedFolder = Path.Combine(mediaFolder, "season");
    Directory.CreateDirectory(nestedFolder);
    var episode = Path.Combine(nestedFolder, "My.Show.S01E02.1080p.mkv");
    File.WriteAllBytes(episode, [4, 5, 6]);
    File.WriteAllText(Path.ChangeExtension(episode, ".srt"), "1\n00:00:00,000 --> 00:00:01,000\nfixture\n");
    File.WriteAllText(Path.ChangeExtension(episode, ".xml"), "<i><d p=\"1.0,1,25,16777215,0,0,0,0\">hello</d></i>");
    Require(await local.AddFolderAsync(mediaFolder) == 2, "folder import recursively indexes only supported media");
    var request = LocalMediaService.CreateRequest(episode);
    Require(request.Subtitles.Count == 1 && request.Danmaku is not null && request.SiteKey == "local", "local playback attaches matching subtitle and danmaku sidecars");
    Require(MediaMatching.Title(Path.GetFileNameWithoutExtension(episode)) == "My Show" && MediaMatching.EpisodeNumber("第 12 集") == 12,
        "filename matching removes technical tags and reads the episode number");
    var added = Path.Combine(nestedFolder, "added.mp4");
    File.WriteAllBytes(added, [7]);
    await Until(async () => (await database.GetLocalFilesAsync()).Any(file => file.Path == added), "watcher imports a new file");
    File.Delete(added);
    await Until(async () => (await database.GetLocalFilesAsync()).All(file => file.Path != added), "watcher removes a deleted file from the index");
    await local.RemoveFolderAsync(mediaFolder);
    Require((await database.GetLocalFilesAsync()).Count == 0 && File.Exists(single) && File.Exists(episode), "removing a watched folder removes only index rows");
}

using (var http = new HttpClient())
{
    var listener = new HttpListener();
    var port = FreePort();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var requests = 0;
    var serving = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            requests++;
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            byte[] data;
            string type;
            if (path == "/movie.mp4")
            {
                data = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray();
                type = "video/mp4";
            }
            else if (path == "/master.m3u8")
            {
                data = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100000,RESOLUTION=640x360\n/media.m3u8\n");
                type = "application/vnd.apple.mpegurl";
            }
            else if (path == "/media.m3u8")
            {
                data = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n/seg0.ts\n#EXTINF:1,\n/seg1.ts\n#EXT-X-ENDLIST\n");
                type = "application/vnd.apple.mpegurl";
            }
            else if (path is "/seg0.ts" or "/seg1.ts")
            {
                data = Encoding.ASCII.GetBytes(path == "/seg0.ts" ? "segment-zero" : "segment-one");
                type = "video/mp2t";
            }
            else
            {
                data = Encoding.UTF8.GetBytes("{\"body\":[{\"from\":1.25,\"to\":3.5,\"content\":\"在线字幕测试\"}]}");
                type = "application/json";
            }
            context.Response.ContentType = type;
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data);
            context.Response.Close();
        }
    });
    var sourceHttp = new SourceHttpClient();
    var provider = new BilibiliVodProvider(sourceHttp, null);
    var online = new OnlineMediaService(sourceHttp, provider);
    var track = new OnlineSubtitleTrack("中文", new Uri($"http://127.0.0.1:{port}/subtitle.json"));
    var subtitle = await online.SaveSubtitleAsync(track, CancellationToken.None);
    var vtt = File.ReadAllText(subtitle.LocalPath);
    Require(vtt.StartsWith("WEBVTT") && vtt.Contains("00:00:01.250 --> 00:00:03.500") && vtt.Contains("在线字幕测试"), "online JSON subtitles become valid WebVTT");
    Require((await online.SaveSubtitleAsync(track, CancellationToken.None)) == subtitle && requests == 1, "converted online subtitles are cached");
    using (var downloadService = new VideoDownloadService(new DnsResolverService(database)))
    {
        var downloadRoot = Path.Combine(root, "download-regression");
        Directory.CreateDirectory(downloadRoot);
        var mp4 = Path.Combine(downloadRoot, "episode1.mp4");
        var progress = 0d;
        await downloadService.SaveAsync(new PlaybackRequest { Url = $"http://127.0.0.1:{port}/movie.mp4" }, mp4,
            new Progress<double>(value => progress = value), CancellationToken.None);
        Require(new FileInfo(mp4).Length == 256 * 1024 && progress == 100, "batch download writes the complete MP4 and reports completion");
        var hls = Path.Combine(downloadRoot, "episode2.m3u8");
        await downloadService.SaveAsync(new PlaybackRequest { Url = $"http://127.0.0.1:{port}/master.m3u8" }, hls, new Progress<double>(), CancellationToken.None);
        var masterLines = await File.ReadAllLinesAsync(hls);
        var child = masterLines.Last(line => line.Length > 0 && !line.StartsWith('#'));
        var childPath = Path.Combine(downloadRoot, child);
        var segments = (await File.ReadAllLinesAsync(childPath)).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        Require(segments.Length == 2 && segments.All(segment => File.Exists(Path.Combine(Path.GetDirectoryName(childPath)!, segment))),
            "batch download materializes the HLS variant and both local segments");
    }
    listener.Stop();
    listener.Close();
    await serving;
}

var images = AppPaths.Images;
Directory.CreateDirectory(images);
var image = Path.Combine(images, "delete.img");
var imageSidecar = Path.Combine(images, "keep.txt");
File.WriteAllBytes(image, new byte[128]);
File.WriteAllText(imageSidecar, "not an image cache");
var appTemp = Path.Combine(AppPaths.Root, "cache", "cast-test.tmp");
Directory.CreateDirectory(Path.GetDirectoryName(appTemp)!);
File.WriteAllText(appTemp, "temporary");
var downloads = Path.Combine(root, "downloads");
Directory.CreateDirectory(downloads);
await database.SetSettingAsync("downloads.defaultFolder", downloads);
var partial = Path.Combine(downloads, "unfinished.partial");
var unregistered = Path.Combine(downloads, "keep.mp4");
File.WriteAllText(partial, "partial");
File.WriteAllText(unregistered, "keep");
var expired = Path.Combine(Path.GetTempPath(), "expired.tmp");
var recent = Path.Combine(Path.GetTempPath(), "recent.tmp");
Directory.CreateDirectory(Path.GetTempPath());
File.WriteAllText(expired, "old");
File.WriteAllText(recent, "recent");
File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
var cleanup = new CleanupService(database);
var result = await cleanup.CleanAsync(["images", "subtitles", "app-temp", "partial-downloads", "user-temp"], null, CancellationToken.None);
Require(!File.Exists(image) && File.Exists(imageSidecar) && !File.Exists(appTemp) && !File.Exists(partial) && File.Exists(unregistered),
    "normal cleanup deletes only known application and partial-download files");
Require(!File.Exists(expired) && File.Exists(recent), "user temporary cleanup keeps recent files");
var manifest = Path.Combine(downloads, "movie.m3u8");
var otherManifest = Path.Combine(downloads, "other.m3u8");
var selectedAssets = Path.Combine(downloads, "media-aaa");
var otherAssets = Path.Combine(downloads, "media-bbb");
Directory.CreateDirectory(selectedAssets);
Directory.CreateDirectory(otherAssets);
var selectedSegment = Path.Combine(selectedAssets, "0.ts");
var otherSegment = Path.Combine(otherAssets, "0.ts");
File.WriteAllText(selectedSegment, "selected");
File.WriteAllText(otherSegment, "other");
File.WriteAllText(manifest, "#EXTM3U\n#EXTINF:1,\nmedia-aaa/0.ts\n#EXT-X-ENDLIST\n");
File.WriteAllText(otherManifest, "#EXTM3U\n#EXTINF:1,\nmedia-bbb/0.ts\n#EXT-X-ENDLIST\n");
await database.SaveDownloadAsync(new DownloadEntry(manifest, "movie", "1"));
await cleanup.CleanAsync(["downloads"], null, CancellationToken.None);
Require(!File.Exists(manifest) && !File.Exists(selectedSegment) && File.Exists(otherSegment) && File.Exists(otherManifest) && File.Exists(unregistered),
    "completed HLS cleanup deletes only assets referenced by the registered manifest");
Require(File.Exists(Path.Combine(mediaFolder, "single.mkv")), "download and cache cleanup never removes imported local media");

using (var direct = new AppServices(playerOnly: true))
{
    var recovery = new SourceRecoveryService(direct);
    var oldLine = new PlaybackLine { Name = "主线路", Episodes = [new Episode { Name = "第1集", Url = "old1" }, new Episode { Name = "第2集", Url = "old2" }] };
    var original = new PlaybackRequest { Title = "片名", SiteKey = "old", VodId = "1", Url = "old2", Lines = [oldLine], EpisodeIndex = 1 };
    var source = new VodItem { Id = "new-id", Name = "片名", SiteKey = "new", SiteName = "新来源", PlayFrom = "主线路", PlayUrl = "第1集$new1#第2集$new2" };
    var switched = recovery.Switch(original, source);
    Require(switched.Url == "new2" && switched.EpisodeIndex == 1 && switched.LineIndex == 0 && switched.SiteKey == "new" && original.Url == "old2",
        "source switching keeps line, episode name and the original request unchanged");
    Require(direct.PlayerOnly && direct.Config is null && direct.Cloud is null && direct.Vod is null && direct.LocalMedia is null,
        "direct local player does not construct configuration, cloud, VOD router or local library watchers");
}
Require(EpisodeTiming.Normalize(new EpisodeTiming(5000, 3000)).OutroStartMs == 0 && EpisodeTiming.Normalize(new EpisodeTiming(1000, 5000)).IntroEndMs == 1000,
    "per-episode timing rejects inverted boundaries and keeps valid values");
Console.WriteLine("PASS local media, online subtitles, cleanup, source switching, direct player and episode timing");

static int FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task Until(Func<Task<bool>> condition, string name)
{
    for (var attempt = 0; attempt < 40; attempt++)
    {
        if (await condition()) { Console.WriteLine("PASS " + name); return; }
        await Task.Delay(250);
    }
    throw new InvalidOperationException(name);
}

static void Require(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}
