global using System;
global using System.Linq;
global using System.IO;
global using System.Reflection;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
using TV.Models;
using TV.Services.Http;
using TV.Services.Playback;
using TV.Services.Vod.Native;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
var http = new SourceHttpClient();
var provider = new BilibiliVodProvider(http, null);
try
{
    var match = await provider.MatchAsync("流浪地球2", "正片", 0, timeout.Token);
    Console.WriteLine($"MATCH {match.Title} · {match.EpisodeTitle}");
    var danmaku = await new DanmakuService(http).LoadAsync(match.Danmaku, timeout.Token);
    if (danmaku.Count == 0) throw new InvalidOperationException("Matched official Bilibili episode returned no danmaku.");
    Console.WriteLine($"DANMAKU {danmaku.Count} entries; ordered={danmaku.SequenceEqual(danmaku.OrderBy(item => item.Seconds))}");
    var subtitles = await provider.GetOnlineSubtitlesAsync(match, timeout.Token);
    Console.WriteLine($"SUBTITLES {subtitles.Count} official tracks");
}
catch (InvalidDataException)
{
    var read = typeof(BilibiliVodProvider).GetMethod("ReadApiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var site = new SourceSite { Key = "online", Name = "哔哩哔哩", Type = 3, Api = "csp_BiliGuard" };
    var request = (Task<JsonElement>)read.Invoke(provider, [site, "/x/web-interface/search/type", timeout.Token,
        new (string Key, string Value)[] { ("search_type", "media_ft"), ("keyword", "流浪地球2"), ("page", "1") }])!;
    var data = await request;
    if (data.TryGetProperty("result", out var results))
        foreach (var item in results.EnumerateArray())
            Console.WriteLine($"PUBLIC title={item.GetProperty("title")} org={item.GetProperty("org_title")} season={item.GetProperty("season_id")}");
    else Console.WriteLine("PUBLIC search response=" + data.GetRawText());
    throw;
}
