using System.Text.RegularExpressions;

namespace TV.Services.Playback;

public static partial class MediaMatching
{
    public static string Title(string value)
    {
        var title = Path.GetFileNameWithoutExtension(value.Trim());
        title = TechnicalTag().Replace(title, " ");
        title = TechnicalSuffix().Replace(title, "");
        title = EpisodeSuffix().Replace(title, "");
        return title.Replace('_', ' ').Replace('.', ' ').Trim(' ', '-', '–', '[', ']', '(', ')');
    }

    public static int? EpisodeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = NumberedEpisode().Match(value);
        if (!match.Success) match = ChineseEpisode().Match(value);
        if (!match.Success && int.TryParse(value.Trim(), out var plain)) return plain;
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) && number >= 0 ? number : null;
    }

    [GeneratedRegex(@"\[[^\]]*(?:1080p|2160p|4k|8k|x26[45]|h\.?26[45]|hevc|avc|aac|dts|bluray|web-?dl|hdr|10bit|8bit)[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalTag();
    [GeneratedRegex(@"\b(?:2160p|1080p|720p|480p|4k|8k|x26[45]|h\.?26[45]|hevc|avc|aac|dts|bluray|web-?dl|hdr10\+?|10bit|8bit)\b.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalSuffix();
    [GeneratedRegex(@"(?:\bS\d{1,3}E\d{1,4}\b.*|\bEP?\d{1,4}\b.*|第\s*\d{1,4}\s*(?:集|话|期).*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeSuffix();
    [GeneratedRegex(@"(?:\bS\d{1,3}E|\bEP?|Episode\s*)(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedEpisode();
    [GeneratedRegex(@"第\s*(\d{1,4})\s*(?:集|话|期)", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseEpisode();
}
