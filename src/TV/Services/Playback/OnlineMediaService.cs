using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Vod.Native;

namespace TV.Services.Playback;

public sealed class OnlineMediaService(SourceHttpClient http, BilibiliVodProvider bilibili)
{
    private readonly ConcurrentDictionary<string, (OnlineMediaMatch Match, DateTimeOffset Expires)> _matches = new(StringComparer.OrdinalIgnoreCase);

    public async Task<OnlineMediaMatch> MatchAsync(string title, string episode, int index, CancellationToken token)
    {
        var key = $"{MediaMatching.Title(title)}|{episode}|{index}";
        if (_matches.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Match;
        var match = await bilibili.MatchAsync(title, episode, index, token).ConfigureAwait(false);
        if (_matches.Count >= 64)
        {
            foreach (var pair in _matches.Where(pair => pair.Value.Expires < DateTimeOffset.UtcNow)) _matches.TryRemove(pair.Key, out _);
            if (_matches.Count >= 64)
                foreach (var pair in _matches.OrderBy(pair => pair.Value.Expires).Take(_matches.Count - 63)) _matches.TryRemove(pair.Key, out _);
        }
        _matches[key] = (match, DateTimeOffset.UtcNow.AddHours(2));
        return match;
    }

    public Task<IReadOnlyList<OnlineSubtitleTrack>> GetSubtitlesAsync(OnlineMediaMatch match, CancellationToken token) =>
        bilibili.GetOnlineSubtitlesAsync(match, token);

    public async Task<Uri> SaveSubtitleAsync(OnlineSubtitleTrack track, CancellationToken token)
    {
        var directory = Path.Combine(AppPaths.Root, "cache", "subtitles");
        Directory.CreateDirectory(directory);
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(track.Address.AbsoluteUri)))[..24];
        var destination = Path.Combine(directory, identity + ".vtt");
        if (File.Exists(destination) && new FileInfo(destination).Length > 8) return new Uri(destination);
        var payload = await http.GetBytesAsync(track.Address.AbsoluteUri, new Dictionary<string, string>
        {
            ["Referer"] = BilibiliAccountService.Site,
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36"
        }, maxBytes: 4 * 1024 * 1024, cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
        var text = SourceHttpClient.DecodeText(payload.Content).TrimStart('\uFEFF');
        string webVtt;
        if (text.StartsWith("WEBVTT", StringComparison.Ordinal)) webVtt = text;
        else
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("在线字幕格式无法识别。");
            var output = new StringBuilder("WEBVTT\n\n");
            var index = 0;
            foreach (var item in body.EnumerateArray().Take(30_000))
            {
                if (!item.TryGetProperty("from", out var from) || !item.TryGetProperty("to", out var to) ||
                    !from.TryGetDouble(out var start) || !to.TryGetDouble(out var end) ||
                    !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > 24 * 60 * 60) continue;
                var content = item.TryGetProperty("content", out var value) ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(content)) continue;
                output.AppendLine((++index).ToString(CultureInfo.InvariantCulture));
                output.Append(Timestamp(start)).Append(" --> ").AppendLine(Timestamp(end));
                output.AppendLine(content.Replace("\0", "")).AppendLine();
            }
            webVtt = output.ToString();
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, webVtt, new UTF8Encoding(false), token).ConfigureAwait(false);
            File.Move(temporary, destination, true);
            return new Uri(destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static string Timestamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
