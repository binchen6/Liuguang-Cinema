using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace TV.Services.Vod.Native;

internal static partial class HtmlPageMedia
{
    public static Uri? ReadVideoElement(IDocument document, Uri page)
    {
        foreach (var media in document.QuerySelectorAll("video[src], video source[src]"))
            if (ReadHttpMedia(page, media.GetAttribute("src")) is { } uri) return uri;
        return null;
    }

    public static Uri? ReadBase64Player(IDocument document, Uri page)
    {
        if (document.GetElementById("cciframe") is null) return null;
        foreach (var script in document.QuerySelectorAll("script:not([src])"))
        {
            try
            {
                var match = CurrentTrack().Match(script.TextContent);
                if (!match.Success) continue;
                var address = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["address"].Value));
                if (ReadHttpMedia(page, address) is { } media) return media;
            }
            catch (FormatException) { }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }

    public static Uri? ReadEpisodeDanmaku(IDocument document, Uri page)
    {
        var player = document.QuerySelector("[data-danmaku-api-url][data-danmaku-enabled='true']");
        var address = player?.GetAttribute("data-danmaku-api-url");
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(page, address, out var api) ||
            !IsHttpAddress(page, api) || api.Query.Length != 0 || api.Fragment.Length != 0) return null;
        var video = document.GetElementById("player-error-message-detail")?.GetAttribute("data-v-id");
        var manager = document.QuerySelector("[x-data^='episodeManager(']")?.GetAttribute("x-data");
        var episode = manager is null ? null : EpisodeNumber().Match(manager).Groups["episode"].Value;
        if (!PositiveNumber(video, out var videoId) || !PositiveNumber(episode, out var episodeId)) return null;
        return new Uri(api.AbsoluteUri.TrimEnd('/') + FormattableString.Invariant($"/video/{videoId}.{episodeId}"));
    }

    private static bool PositiveNumber(string? value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static Uri? ReadHttpMedia(Uri page, string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(page, value.Trim(), out var uri) &&
        IsHttpAddress(page, uri) && PlayUrlResolver.IsDirectMediaUrl(uri.AbsoluteUri)
            ? uri : null;

    private static bool IsHttpAddress(Uri page, Uri target) =>
        target.Scheme is "http" or "https" && target.UserInfo.Length == 0 && !(page.Scheme == "https" && target.Scheme == "http");

    [GeneratedRegex("""\bvar\s+now\s*=\s*base64decode\(\s*(?<quote>["'])(?<address>[A-Za-z0-9+/=]{4,8192})\k<quote>\s*\)""",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex CurrentTrack();

    [GeneratedRegex(@"^episodeManager\(\s*[0-9]+\s*,\s*(?<episode>[0-9]+)\s*,", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EpisodeNumber();
}
