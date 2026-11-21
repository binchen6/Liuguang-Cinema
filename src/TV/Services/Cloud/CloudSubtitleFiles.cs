using TV.Models;

namespace TV.Services.Cloud;

internal static class CloudSubtitleFiles
{
    private static readonly char[] Separators = ['.', '_', '-', ' ', '&', '+'];
    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh", "zho", "chi", "chs", "cht", "hans", "hant", "cn", "tw", "hk", "en", "eng",
        "ja", "jpn", "ko", "kor", "fr", "fre", "de", "ger", "es", "spa", "pt", "por", "br",
        "ru", "rus", "it", "ita", "th", "tha", "vi", "vie", "ar", "ara", "forced", "sdh", "cc",
        "简", "繁", "简体", "繁体", "中文", "英文", "双语", "中字", "粤语", "中英", "简中", "繁中"
    };

    public static bool IsSubtitle(string name) => Extension(name) is not null;

    public static string? Extension(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".srt" => ".srt",
        ".vtt" => ".vtt",
        ".ass" => ".ass",
        ".ssa" => ".ssa",
        _ => null
    };

    public static IReadOnlyList<CloudSubtitleTrack> Match(CloudFile video, IEnumerable<CloudFile> candidates)
    {
        var stem = Path.GetFileNameWithoutExtension(video.Name);
        if (stem.Length == 0 || video.ParentDirectoryId.Length == 0) return [];
        return candidates.Where(file => IsSubtitle(file.Name) && file.ParentDirectoryId == video.ParentDirectoryId && Matches(stem, file.Name))
            .DistinctBy(file => file.FileId)
            .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .Select(file => new CloudSubtitleTrack(file.Name, new Uri(CloudDriveService.GetPlaybackId(file))))
            .ToArray();
    }

    private static bool Matches(string videoStem, string name)
    {
        var candidate = Path.GetFileNameWithoutExtension(name);
        if (candidate.Equals(videoStem, StringComparison.OrdinalIgnoreCase)) return true;
        if (!candidate.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase) || !Separators.Contains(candidate[videoStem.Length]))
            return false;
        var suffixes = candidate[videoStem.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return suffixes.Length > 0 && suffixes.All(Suffixes.Contains);
    }
}
