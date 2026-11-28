namespace TV.Services.Playback;

internal static class HlsPlaylistCleaner
{
    internal const string SettingKey = "playback.cleanHls";

    internal static bool IsClosedVod(string manifest) =>
        HlsManifest.IsMediaPlaylist(manifest) && manifest.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);

    internal static bool HasSeparateRenditions(string manifest) => manifest.Contains("#EXT-X-MEDIA:", StringComparison.Ordinal) &&
        (manifest.Contains("TYPE=AUDIO", StringComparison.Ordinal) || manifest.Contains("TYPE=SUBTITLES", StringComparison.Ordinal));

    internal static string Clean(string manifest)
    {
        if (!manifest.Contains("#EXT-X-CUE-OUT", StringComparison.Ordinal) ||
            !manifest.Contains("#EXT-X-CUE-IN", StringComparison.Ordinal) || !IsClosedVod(manifest)) return manifest;
        var lines = manifest.Split('\n');
        var retained = new List<string>(lines.Length);
        var inBreak = false;
        var pendingSegment = false;
        var breakSegments = 0;
        var removed = 0;
        var kept = 0;
        var discontinuityPending = false;
        var hasDiscontinuity = false;
        var endList = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-PART", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-SKIP:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-DATERANGE:", StringComparison.Ordinal))
                return manifest;
            if (line is "#EXT-X-CUE-OUT" || line.StartsWith("#EXT-X-CUE-OUT:", StringComparison.Ordinal))
            {
                if (inBreak || pendingSegment || endList) return manifest;
                inBreak = true;
                breakSegments = 0;
                continue;
            }
            if (line is "#EXT-X-CUE-IN")
            {
                if (!inBreak || pendingSegment || breakSegments == 0) return manifest;
                inBreak = false;
                discontinuityPending = kept > 0;
                removed += breakSegments;
                continue;
            }
            if (line == "#EXT-X-ENDLIST")
            {
                if (inBreak || pendingSegment || endList) return manifest;
                endList = true;
            }
            if (!inBreak && line == "#EXT-X-DISCONTINUITY")
            {
                if (hasDiscontinuity) continue;
                hasDiscontinuity = true;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingSegment || endList) return manifest;
                pendingSegment = true;
                if (!inBreak && discontinuityPending)
                {
                    if (!hasDiscontinuity) retained.Add("#EXT-X-DISCONTINUITY");
                    discontinuityPending = false;
                }
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                if (!pendingSegment || endList) return manifest;
                pendingSegment = false;
                if (inBreak) breakSegments++;
                else
                {
                    kept++;
                    hasDiscontinuity = false;
                }
            }
            if (!inBreak) retained.Add(raw.TrimEnd('\r'));
        }
        return endList && !inBreak && !pendingSegment && removed > 0 && kept > 0
            ? string.Join('\n', retained) : manifest;
    }
}
