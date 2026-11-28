using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.AI;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Playback;

internal sealed record MusicPresentation(string Title, string Artist, string Album, byte[]? Cover, string? Lyrics,
    string? TranslatedLyrics = null);
internal sealed record LyricLine(long? TimeMs, string Text, string? Translation = null);
internal sealed record DisplayLyric(long? TimeMs, string Primary, string? Secondary);

internal sealed partial class MusicMetadataService(SourceHttpClient http)
{
    private const int MaxLyricsBytes = 512 * 1024;
    private const int MaxCoverBytes = 6 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> OnlineHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = $"LiuguangCinema/{typeof(MusicMetadataService).Assembly.GetName().Version?.ToString(3)} (https://github.com/binchen6/Liuguang-Cinema)"
    };

    internal static MusicPresentation Describe(PlaybackRequest request)
    {
        var episode = request.Lines.ElementAtOrDefault(request.LineIndex)?.Episodes.ElementAtOrDefault(request.EpisodeIndex)?.Name;
        var title = request.MusicTitle ?? (string.IsNullOrWhiteSpace(episode) ? request.Title : episode);
        var artist = request.MusicArtist ?? request.Item?.Actor ?? "";
        if (string.IsNullOrWhiteSpace(artist) && title.Split(" - ", 2, StringSplitOptions.TrimEntries) is [var first, var second] &&
            first.Length > 0 && second.Length > 0)
        {
            if (request.SiteKey.Equals("MTV1", StringComparison.OrdinalIgnoreCase)) (title, artist) = (first, second);
            else if (request.SiteKey == "local") (artist, title) = (first, second);
        }
        return new MusicPresentation(title, artist, request.MusicAlbum ?? "", null, null);
    }

    internal async Task<MusicPresentation> LoadAsync(PlaybackRequest request, CancellationToken token)
    {
        var result = Describe(request);
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var media) && media.IsFile)
            result = await Task.Run(() => ReadTags(media.LocalPath, result), token).ConfigureAwait(false);
        var local = media?.IsFile == true;
        var image = request.MusicCover ?? request.Picture;
        var coverTask = TryAsync(() => ReadResourceAsync(image, local, MaxCoverBytes, token), token);
        var lyricsTask = TryAsync(() => ReadSiteLyricsAsync(request.MusicLyrics, local, token), token);
        var translationTask = TryAsync(() => ReadSiteLyricsAsync(request.MusicTranslatedLyrics, local, token), token);
        var savedTask = TryAsync(() => ReadSavedLyricsAsync(request, token), token);
        var saved = await savedTask.ConfigureAwait(false);
        var siteLyrics = await lyricsTask.ConfigureAwait(false);
        var translated = await translationTask.ConfigureAwait(false);
        return result with
        {
            Cover = await coverTask.ConfigureAwait(false) ?? result.Cover,
            Lyrics = saved?.Lyrics ?? siteLyrics ?? result.Lyrics,
            TranslatedLyrics = saved is null ? translated : saved.Translation
        };
    }

    private sealed record SavedLyrics(string? Lyrics, string? Translation);

    private static string SavedLyricsPath(PlaybackRequest request)
    {
        var episode = request.Lines.ElementAtOrDefault(request.LineIndex)?.Episodes.ElementAtOrDefault(request.EpisodeIndex)?.Name;
        var identity = request.VodId.Length > 0
            ? $"{request.SiteKey}\n{request.VodId}\n{episode ?? request.EpisodeIndex.ToString(CultureInfo.InvariantCulture)}"
            : request.Url;
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.IsFile)
            identity += $"\n{File.GetLastWriteTimeUtc(uri.LocalPath).Ticks}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(AppPaths.Root, "cache", "music-lyrics", id + ".json");
    }

    private static async Task<SavedLyrics?> ReadSavedLyricsAsync(PlaybackRequest request, CancellationToken token)
    {
        var file = new FileInfo(SavedLyricsPath(request));
        if (!file.Exists || file.Length > 2L * MaxLyricsBytes) return null;
        await using var stream = file.OpenRead();
        return await JsonSerializer.DeserializeAsync<SavedLyrics>(stream, cancellationToken: token).ConfigureAwait(false);
    }

    internal static async Task SaveLyricsAsync(PlaybackRequest request, MusicPresentation presentation, CancellationToken token)
    {
        var path = SavedLyricsPath(request);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new SavedLyrics(presentation.Lyrics, presentation.TranslatedLyrics),
                    cancellationToken: token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("歌词缓存写入: " + ex.GetType().Name);
        }
        finally { AudioExtractionService.TryDelete(temp); }
    }

    internal async Task<MusicPresentation> MatchAsync(MusicPresentation track, string artist, long durationMs, CancellationToken token)
    {
        var lyrics = track.Lyrics is null
            ? TryAsync(() => MatchLyricsAsync(track, artist, durationMs, token), token) : Task.FromResult<string?>(track.Lyrics);
        var cover = track.Cover is null && artist.Length > 0
            ? TryAsync(() => MatchCoverAsync(track, artist, token), token) : Task.FromResult(track.Cover);
        return track with
        {
            Artist = artist,
            Cover = await cover.ConfigureAwait(false),
            Lyrics = await lyrics.ConfigureAwait(false)
        };
    }

    private async Task<string?> MatchLyricsAsync(MusicPresentation track, string artist, long durationMs, CancellationToken token)
    {
        var signature = "track_name=" + Uri.EscapeDataString(track.Title);
        if (artist.Length > 0)
        {
            signature += "&artist_name=" + Uri.EscapeDataString(artist);
            var duration = durationMs is > 0 and <= 3600000
                ? "&duration=" + Math.Max(1, Math.Round(durationMs / 1000d)).ToString(CultureInfo.InvariantCulture) : "";
            var album = track.Album.Length == 0 ? "" : "&album_name=" + Uri.EscapeDataString(track.Album);
            try
            {
                using var result = await FetchJsonAsync("https://lrclib.net/api/get?" + signature + duration + album, token).ConfigureAwait(false);
                if (LyricContent(result.RootElement) is { } exact) return exact;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { }
        }
        else if (durationMs <= 0) return null;

        using var search = await FetchJsonAsync("https://lrclib.net/api/search?" + signature, token).ConfigureAwait(false);
        return ChooseLyrics(search.RootElement, track.Title, artist, track.Album, durationMs);
    }

    internal static string? ChooseLyrics(JsonElement entries, string title, string artist, string album, long durationMs)
    {
        if (entries.ValueKind != JsonValueKind.Array) return null;
        var titleKey = SearchKey(title);
        var artistKey = SearchKey(artist);
        var albumKey = SearchKey(album);
        var eligible = new List<(string Lyrics, string Artist, int Score)>();
        foreach (var item in entries.EnumerateArray().Take(80))
        {
            if (titleKey.Length == 0 || SearchKey(ReadString(item, "trackName") ?? "") != titleKey ||
                LyricContent(item) is not { } lyrics) continue;
            var foundArtist = SearchKey(ReadString(item, "artistName") ?? "");
            if (artistKey.Length > 0 && foundArtist != artistKey) continue;
            var duration = item.TryGetProperty("duration", out var number) && number.TryGetDouble(out var seconds) ? seconds * 1000 : 0;
            var difference = durationMs > 0 && duration > 0 ? Math.Abs(duration - durationMs) : double.PositiveInfinity;
            if (artistKey.Length == 0 && difference > 2500 || durationMs > 0 && difference > 12000) continue;
            var synced = ReadString(item, "syncedLyrics") is { Length: > 0 };
            if (synced && durationMs > 0 && difference > 3500)
            {
                if (ReadString(item, "plainLyrics") is not { Length: > 0 } plain) continue;
                lyrics = plain;
                synced = false;
            }
            var score = (synced ? 20 : 0) + (difference <= 2500 ? 12 : 0) +
                (albumKey.Length > 0 && SearchKey(ReadString(item, "albumName") ?? "") == albumKey ? 10 : 0);
            eligible.Add((lyrics, foundArtist, score));
        }
        if (eligible.Count == 0 || artistKey.Length == 0 && eligible.Select(candidate => candidate.Artist).Distinct().Skip(1).Any())
            return null;
        return eligible.OrderByDescending(candidate => candidate.Score).First().Lyrics;
    }

    private static string? LyricContent(JsonElement item)
    {
        if (ReadString(item, "syncedLyrics") is { Length: > 0 and <= MaxLyricsBytes } synced) return synced;
        return ReadString(item, "plainLyrics") is { Length: > 0 and <= MaxLyricsBytes } plain ? plain : null;
    }

    private static string SearchKey(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(rune => Rune.ToLowerInvariant(rune).ToString()));

    private async Task<byte[]?> MatchCoverAsync(MusicPresentation track, string artist, CancellationToken token)
    {
        static string Phrase(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var query = "recording:" + Phrase(track.Title) + " AND artist:" + Phrase(artist);
        var url = "https://musicbrainz.org/ws/2/recording/?fmt=json&limit=8&query=" + Uri.EscapeDataString(query);
        using var data = await FetchJsonAsync(url, token).ConfigureAwait(false);
        if (!data.RootElement.TryGetProperty("recordings", out var recordings)) return null;
        var titleKey = SearchKey(track.Title);
        var artistKey = SearchKey(artist);
        var albumKey = SearchKey(track.Album);
        var releases = recordings.EnumerateArray()
            .Where(item => SearchKey(ReadString(item, "title") ?? "") == titleKey &&
                item.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array &&
                    credits.EnumerateArray().Any(credit => SearchKey(ReadString(credit, "name") ?? "") == artistKey))
            .SelectMany(item => item.TryGetProperty("releases", out var values) ? values.EnumerateArray().ToArray() : [])
            .OrderByDescending(release => albumKey.Length > 0 && SearchKey(ReadString(release, "title") ?? "") == albumKey)
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
            ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException or InvalidDataException) { return null; }
    }

    private static string? ReadString(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IReadOnlyList<LyricLine> JoinLyrics(string? original, string? translation)
    {
        var originals = ParseLyrics(original);
        var translations = ParseLyrics(translation);
        if (originals.Count == 0) return translations;
        if (translations.Count == 0) return originals;
        if (translations[0].TimeMs is null || originals[0].TimeMs is null)
            return originals.Count == translations.Count
                ? originals.Select((line, i) => line with { Translation = translations[i].Text }).ToArray() : originals;

        var paired = new LyricLine[originals.Count];
        var cursor = 0;
        for (var i = 0; i < originals.Count; i++)
        {
            var line = originals[i];
            var time = line.TimeMs.GetValueOrDefault();
            while (cursor < translations.Count && translations[cursor].TimeMs < time - 350) cursor++;
            if (cursor + 1 < translations.Count &&
                Math.Abs(translations[cursor + 1].TimeMs.GetValueOrDefault() - time) < Math.Abs(translations[cursor].TimeMs.GetValueOrDefault() - time))
                cursor++;
            if (cursor < translations.Count && Math.Abs(translations[cursor].TimeMs.GetValueOrDefault() - time) <= 350)
                line = line with { Translation = translations[cursor++].Text };
            paired[i] = line;
        }
        return paired;
    }

    internal static string FormatLyrics(IEnumerable<LyricLine> lines) => string.Join('\n', lines.Select(line =>
    {
        var text = line.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.TimeMs is not { } time) return text;
        return FormattableString.Invariant($"[{time / 60000:00}:{time / 1000 % 60:00}.{time % 1000:000}]{text}");
    }).Where(line => line.Length > 0));

    internal static DisplayLyric PresentLyric(LyricLine line, bool bilingual, bool translationPrimary)
    {
        var translation = string.IsNullOrWhiteSpace(line.Translation) || line.Translation == line.Text ? null : line.Translation;
        if (translation is null) return new DisplayLyric(line.TimeMs, line.Text, null);
        return translationPrimary
            ? new DisplayLyric(line.TimeMs, translation, bilingual ? line.Text : null)
            : new DisplayLyric(line.TimeMs, line.Text, bilingual ? translation : null);
    }

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
