using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Spider;

namespace TV.Services.Playback;

internal static partial class KugouPlayback
{
    internal const string UnavailableMessage = "酷狗网页未提供可用音频。此曲可能只支持客户端、试听或登录后播放，请切换曲目或打开原站。";

    internal static SpiderPlayResult Prepare(SourceSite site, SpiderPlayResult result)
    {
        var api = site.Api;
        if (!api.Equals("csp_Kugou", StringComparison.OrdinalIgnoreCase) &&
            !api.Equals("csp_KugouGuard", StringComparison.OrdinalIgnoreCase) &&
            !api.Equals("csp_Music", StringComparison.OrdinalIgnoreCase) &&
            !api.Equals("csp_MusicGuard", StringComparison.OrdinalIgnoreCase))
            return result;

        return IsSongPage(result.Url) ? result with
        {
            BrowserAudioPlayback = true,
            BrowserFailureMessage = UnavailableMessage,
            BrowserMediaFilter = IsKugouMediaHost,
            UseHttpProxy = true
        } : result;
    }

    internal static bool IsSongPage(string address) => Uri.TryCreate(address, UriKind.Absolute, out var page) &&
        page.Scheme == Uri.UriSchemeHttps && page.IdnHost == "www.kugou.com" && page.IsDefaultPort &&
        page.UserInfo.Length == 0 && (SongPath().IsMatch(page.AbsolutePath) || page.AbsolutePath == "/song/");

    private static bool IsKugouMediaHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        (uri.IdnHost is "kugou.com" or "kgimg.com" || uri.IdnHost.EndsWith(".kugou.com", StringComparison.Ordinal) ||
            uri.IdnHost.EndsWith(".kgimg.com", StringComparison.Ordinal));

    [GeneratedRegex("^/mixsong/[A-Za-z0-9]{1,40}\\.html$", RegexOptions.CultureInvariant)]
    private static partial Regex SongPath();
}

internal sealed class KugouMediaUnavailableException(string sourceAddress) : NotSupportedException(KugouPlayback.UnavailableMessage)
{
    internal string SourceAddress { get; } = sourceAddress;
}
