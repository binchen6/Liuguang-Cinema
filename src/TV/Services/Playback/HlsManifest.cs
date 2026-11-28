using System.Globalization;
using System.Text.RegularExpressions;
using TV.Models;

namespace TV.Services.Playback;

internal enum HlsVideoRange { Unknown, Sdr, Pq, Hlg, DolbyVision }

internal sealed record HlsSelection(string? Playlist, IReadOnlySet<int> Heights, HlsVideoRange Range, int VariantCount, bool RequiresDolbyVision = false)
{
    public IReadOnlyList<QualityOption> QualityOptions { get; init; } = [];
    public QualityOption? SelectedQuality { get; init; }
}

internal static partial class HlsManifest
{
    private const string VariantTag = "#EXT-X-STREAM-INF:";
    private const string RenditionTag = "#EXT-X-MEDIA:";
    private const string HlsContentType = "application/vnd.apple.mpegurl";
    private static readonly string[] RenditionTypes = ["AUDIO", "VIDEO", "SUBTITLES", "CLOSED-CAPTIONS"];

    public static bool IsPlaylist(string url, string? contentType) =>
        contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true ||
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static bool IsManifest(string text) =>
        text.TrimStart('\uFEFF').AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);

    internal static bool IsMediaPlaylist(string text) => IsManifest(text) &&
        !text.Contains(VariantTag, StringComparison.Ordinal) && text.Contains("#EXTINF:", StringComparison.Ordinal);

    public static string? RecognizeResponse(string url, string? mediaType)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var path = uri.AbsolutePath;
        if (path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            mediaType is "video/mp2t" or "video/iso.segment" or "audio/iso.segment")
            return null;
        if (mediaType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true) return HlsContentType;
        if (mediaType == "application/dash+xml" || mediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
            mediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return mediaType;
        if (mediaType is not (null or "" or "application/octet-stream" or "binary/octet-stream")) return null;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".m3u8" => HlsContentType,
            ".mpd" => "application/dash+xml",
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".flv" => "video/x-flv",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            _ => null
        };
    }

    public static string? SelectVariant(string master, int height) =>
        ResolveVariants(master, height, hdrDisplay: null)?.Playlist;

    public static HlsSelection? ResolveVariants(string master, int height, bool? hdrDisplay)
        => SelectQualities(master, new QualityOption(string.Empty, height), hdrDisplay);

    internal static HlsSelection? SelectQualities(string master, QualityOption quality, bool? hdrDisplay)
    {
        if (!IsManifest(master)) return null;
        var lines = master.TrimStart('\uFEFF').Split('\n').Select(line => line.Trim()).ToArray();
        var variants = new List<Variant>();
        var tags = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.StartsWith(VariantTag, StringComparison.Ordinal))
            {
                if (line.StartsWith('#') && !line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal)) tags.Add(line);
                continue;
            }
            if (++index >= lines.Length || lines[index].Length == 0 || lines[index].StartsWith('#')) return null;
            var attributes = ParseAttributes(line[VariantTag.Length..]);
            var resolution = attributes.GetValueOrDefault("RESOLUTION", "").Split('x', 'X');
            var variantHeight = resolution.Length == 2 && int.TryParse(resolution[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
            long.TryParse(attributes.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth);
            var frameRate = VideoFormat.ReadFrameRate(attributes.GetValueOrDefault("FRAME-RATE"));
            variants.Add(new Variant(line, lines[index], attributes, new VideoFormat(variantHeight, frameRate), bandwidth, GetVideoRange(attributes)));
        }
        if (variants.Count == 0) return null;
        var originalVariantCount = variants.Count;
        if (hdrDisplay.HasValue)
        {
            // Profile 5 has no compatible base color space in the current decoder.
            variants.RemoveAll(variant => variant.RequiresDolbyVision);
            if (variants.Count == 0) return new HlsSelection(null, new HashSet<int>(), HlsVideoRange.DolbyVision, 0, true);
        }
        var heights = variants.Where(item => item.Format.Height > 0).Select(item => item.Format.Height).ToHashSet();
        var options = VideoFormat.ToOptions(variants.Select(item => item.Format));
        var candidates = variants.Where(item => item.Format.Matches(quality)).ToArray();
        if (candidates.Length == 0) return new HlsSelection(null, heights, HlsVideoRange.Unknown, 0) { QualityOptions = options };
        var automatic = !quality.IsSpecific && !quality.RequestsOriginal;

        if (hdrDisplay.HasValue)
        {
            var baseLayer = candidates.Where(item => item.Range != HlsVideoRange.DolbyVision).ToArray();
            if (baseLayer.Length > 0) candidates = baseLayer;
            if (automatic && !hdrDisplay.Value &&
                candidates.Where(item => item.Range is HlsVideoRange.Sdr or HlsVideoRange.Unknown).ToArray() is { Length: > 0 } sdr)
                candidates = sdr;
            if (automatic && hdrDisplay.Value)
            {
                var hdrHeights = candidates.Where(item => item.Range is HlsVideoRange.Pq or HlsVideoRange.Hlg)
                    .Select(item => item.Format).ToHashSet();
                candidates = candidates.Where(item => item.Format.Height == 0 || !hdrHeights.Contains(item.Format) ||
                    item.Range is HlsVideoRange.Pq or HlsVideoRange.Hlg).ToArray();
            }
        }

        QualityOption? selected = null;
        if (!automatic)
        {
            var top = candidates.OrderByDescending(item => item.Format.Height)
                .ThenByDescending(item => item.Format.FrameRate)
                .ThenByDescending(item => hdrDisplay.HasValue && MatchesDisplay(item.Range, hdrDisplay.Value))
                .ThenByDescending(item => item.Bandwidth).First();
            candidates = [top];
            selected = options.FirstOrDefault(top.Format.Matches);
        }

        var range = candidates.Select(item => item.Range).Distinct().Take(2).ToArray();
        var variantCount = candidates.Length;
        var preservesRouting = !tags.Any(tag => tag.StartsWith("#EXT-X-CONTENT-STEERING:", StringComparison.Ordinal) ||
            tag.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal));
        var rewrite = !automatic || variantCount < originalVariantCount && preservesRouting;
        var playlist = rewrite ? WritePlaylist(tags, candidates) : null;
        return new HlsSelection(playlist, heights, range.Length == 1 && (rewrite || variantCount == originalVariantCount)
            ? range[0] : HlsVideoRange.Unknown, rewrite ? variantCount : originalVariantCount)
        {
            QualityOptions = options,
            SelectedQuality = selected
        };
    }

    private static bool MatchesDisplay(HlsVideoRange range, bool hdr) => hdr
        ? range is HlsVideoRange.Pq or HlsVideoRange.Hlg
        : range is HlsVideoRange.Sdr or HlsVideoRange.Unknown;

    private static string WritePlaylist(List<string> tags, Variant[] variants)
    {
        var groups = variants.SelectMany(item => RenditionTypes.Select(type => (Type: type, Group: item.Values.GetValueOrDefault(type, ""))))
            .Where(item => item.Group.Length > 0).ToHashSet();
        return string.Join('\n', tags.Where(tag => KeepTag(tag, groups)).Concat(variants.SelectMany(item => new[] { item.Tag, item.Url }))) + "\n";
    }

    private static HlsVideoRange GetVideoRange(Dictionary<string, string> attributes)
    {
        // A Dolby Vision entry in SUPPLEMENTAL-CODECS describes optional
        // metadata on a decodable base layer. A primary Dolby codec does not.
        var codecs = attributes.GetValueOrDefault("CODECS", "");
        if (codecs.Split(',', StringSplitOptions.TrimEntries).Any(codec =>
            codec.StartsWith("dvh1.", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("dvhe.", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("dav1.", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("dva1.", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("dvav.", StringComparison.OrdinalIgnoreCase)))
            return HlsVideoRange.DolbyVision;

        return attributes.GetValueOrDefault("VIDEO-RANGE", "") switch
        {
            "SDR" => HlsVideoRange.Sdr,
            "PQ" => HlsVideoRange.Pq,
            "HLG" => HlsVideoRange.Hlg,
            _ => HlsVideoRange.Unknown
        };
    }

    private static bool KeepTag(string tag, HashSet<(string Type, string Group)> groups)
    {
        if (!tag.StartsWith(RenditionTag, StringComparison.Ordinal)) return true;
        var attributes = ParseAttributes(tag[RenditionTag.Length..]);
        return groups.Contains((attributes.GetValueOrDefault("TYPE", ""), attributes.GetValueOrDefault("GROUP-ID", "")));
    }

    private static Dictionary<string, string> ParseAttributes(string attributes)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AttributeRegex().Matches(attributes))
            values[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value.TrimEnd();
        return values;
    }

    private sealed record Variant(string Tag, string Url, Dictionary<string, string> Values, VideoFormat Format, long Bandwidth, HlsVideoRange Range)
    {
        public bool RequiresDolbyVision => Range == HlsVideoRange.DolbyVision &&
            Values.GetValueOrDefault("CODECS", "").Split(',', StringSplitOptions.TrimEntries).Any(codec =>
                codec.StartsWith("dvh1.05.", StringComparison.OrdinalIgnoreCase) ||
                codec.StartsWith("dvhe.05.", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("(?:^|,)\\s*([A-Z0-9-]+)\\s*=\\s*(?:\"([^\"]*)\"|([^,]*))", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();
}
