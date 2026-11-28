using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Playback;

internal sealed record MusicPresentation(string Title, string Artist, string Album, byte[]? Cover, string? Lyrics);
internal sealed record LyricLine(long? TimeMs, string Text);

internal sealed partial class MusicMetadataService(SourceHttpClient http)
{
    private const int MaxLyricsBytes = 512 * 1024;
    private const int MaxCoverBytes = 6 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> OnlineHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = "LiuguangCinema/2.0.1 (https://github.com/binchen6/Liuguang-Cinema)"
    };

    internal async Task<MusicPresentation> LoadAsync(PlaybackRequest request, CancellationToken token)
    {
        var result = new MusicPresentation(request.Title, request.Item?.Actor ?? "", "", null, null);
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var media) && media.IsFile)
            result = await Task.Run(() => ReadTags(media.LocalPath, result), token).ConfigureAwait(false);
        var local = media?.IsFile == true;
        var image = request.MusicCover ?? request.Picture;
        var coverTask = TryAsync(() => ReadResourceAsync(image, local, MaxCoverBytes, token), token);
        var lyricsTask = TryAsync(() => ReadSiteLyricsAsync(request.MusicLyrics, local, token), token);
        return result with
        {
            Cover = await coverTask.ConfigureAwait(false) ?? result.Cover,
            Lyrics = await lyricsTask.ConfigureAwait(false) ?? result.Lyrics
        };
    }

    internal async Task<MusicPresentation> MatchAsync(MusicPresentation track, string artist, long durationMs, CancellationToken token)
    {
        var duration = durationMs > 0 ? "&duration=" + Math.Round(durationMs / 1000d).ToString(CultureInfo.InvariantCulture) : "";
        var lyricUrl = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(track.Title) +
            "&artist_name=" + Uri.EscapeDataString(artist) + duration +
            (string.IsNullOrWhiteSpace(track.Album) ? "" : "&album_name=" + Uri.EscapeDataString(track.Album));
        var lyrics = TryAsync(async () =>
        {
            using var data = await FetchJsonAsync(lyricUrl, token).ConfigureAwait(false);
            return ReadString(data.RootElement, "syncedLyrics") ?? ReadString(data.RootElement, "plainLyrics");
        }, token);
        var cover = TryAsync(() => MatchCoverAsync(track, artist, token), token);
        return track with
        {
            Artist = artist,
            Cover = track.Cover ?? await cover.ConfigureAwait(false),
            Lyrics = track.Lyrics ?? await lyrics.ConfigureAwait(false)
        };
    }

    private async Task<byte[]?> MatchCoverAsync(MusicPresentation track, string artist, CancellationToken token)
    {
        static string Phrase(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var query = "recording:" + Phrase(track.Title) + " AND artist:" + Phrase(artist);
        if (track.Album.Length > 0) query += " AND release:" + Phrase(track.Album);
        var url = "https://musicbrainz.org/ws/2/recording/?fmt=json&limit=5&query=" + Uri.EscapeDataString(query);
        using var data = await FetchJsonAsync(url, token).ConfigureAwait(false);
        if (!data.RootElement.TryGetProperty("recordings", out var recordings)) return null;
        var releases = recordings.EnumerateArray()
            .Where(item => string.Equals(ReadString(item, "title"), track.Title, StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("artist-credit", out var credits) && credits.EnumerateArray()
                    .Any(credit => string.Equals(ReadString(credit, "name"), artist, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(item => item.TryGetProperty("releases", out var values) ? values.EnumerateArray().ToArray() : [])
            .Where(release => track.Album.Length == 0 || string.Equals(ReadString(release, "title"), track.Album, StringComparison.OrdinalIgnoreCase))
            .Select(release => ReadString(release, "id")).OfType<string>().Distinct().Take(3);
        foreach (var id in releases)
        {
            if (!Guid.TryParse(id, out var releaseId)) continue;
            try
            {
                var address = "https://coverartarchive.org/release/" + releaseId + "/front-250";
                var image = await http.GetBytesAsync(address, OnlineHeaders, MaxCoverBytes, token,
                    scopeRedirectHeaders: true, requestTimeout: TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                if (image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return image.Content;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        }
        return null;
    }

    internal static async Task<byte[]> ReadLocalAsync(string path, bool cover, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var limit = cover ? MaxCoverBytes : MaxLyricsBytes;
        if (stream.Length > limit) throw new InvalidDataException("文件超过允许大小。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private async Task<string?> ReadSiteLyricsAsync(string? value, bool local, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!local && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            return value.Length <= MaxLyricsBytes ? value : null;
        var bytes = await ReadResourceAsync(value, local, MaxLyricsBytes, token).ConfigureAwait(false);
        return bytes is null ? null : SourceHttpClient.DecodeText(bytes);
    }

    private async Task<byte[]?> ReadResourceAsync(string? value, bool local, int limit, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var payload = await http.GetBytesAsync(uri.AbsoluteUri, maxBytes: limit, cancellationToken: token,
                scopeRedirectHeaders: true, requestTimeout: TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            return payload.Content;
        }
        return local && Path.IsPathFullyQualified(value) ? await ReadLocalAsync(value, limit == MaxCoverBytes, token).ConfigureAwait(false) : null;
    }

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken token)
    {
        var response = await http.GetBytesAsync(url, OnlineHeaders, MaxLyricsBytes, token, scopeRedirectHeaders: true,
            requestTimeout: TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        return JsonDocument.Parse(response.Content);
    }

    private static MusicPresentation ReadTags(string path, MusicPresentation track)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            var tag = file.Tag;
            var picture = tag.Pictures.FirstOrDefault(image => image.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures.FirstOrDefault();
            return new MusicPresentation(
                string.IsNullOrWhiteSpace(tag.Title) ? track.Title : tag.Title,
                tag.FirstPerformer ?? track.Artist, tag.Album ?? "",
                picture?.Data.Count is > 0 and <= MaxCoverBytes ? picture.Data.Data : null,
                tag.Lyrics?.Length <= MaxLyricsBytes ? tag.Lyrics : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.UnsupportedFormatException or TagLib.CorruptFileException)
        {
            return track;
        }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> load, CancellationToken token) where T : class
    {
        try { return await load().ConfigureAwait(false); }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException) { return null; }
    }

    private static string? ReadString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IReadOnlyList<LyricLine> ParseLyrics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = new List<LyricLine>();
        var offsetMatch = OffsetPattern().Match(text);
        var offset = offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].ValueSpan, out var value) ? value : 0;
        foreach (var raw in text.Split('\n').Take(3000))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var times = TimePattern().Matches(line);
            if (times.Count == 0)
            {
                if (!MetadataPattern().IsMatch(line)) lines.Add(new LyricLine(null, line));
                continue;
            }
            var content = line[(times[^1].Index + times[^1].Length)..].Trim();
            if (content.Length == 0) continue;
            foreach (Match stamp in times)
            {
                var mins = int.Parse(stamp.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                var secs = int.Parse(stamp.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                var fraction = stamp.Groups[3].Value;
                var millis = fraction.Length > 0 ? int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
                lines.Add(new LyricLine(Math.Max(0, mins * 60_000L + secs * 1000 + millis + offset), content));
            }
        }
        if (lines.All(line => line.TimeMs is null)) return lines;
        return [.. lines.Where(line => line.TimeMs.HasValue).OrderBy(line => line.TimeMs)
            .GroupBy(line => line.TimeMs).Select(group => new LyricLine(group.Key, string.Join("\n", group.Select(line => line.Text).Distinct())))];
    }

    internal static int ActiveLine(IReadOnlyList<LyricLine> lines, long timeMs)
    {
        var left = 0;
        var right = lines.Count;
        while (left < right)
        {
            var mid = left + (right - left) / 2;
            if (lines[mid].TimeMs is { } value && value <= timeMs) left = mid + 1;
            else right = mid;
        }
        return left - 1;
    }

    [GeneratedRegex(@"\[(\d{1,4}):([0-5]\d)(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();
    [GeneratedRegex(@"^\[[a-z]+:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetadataPattern();
}
