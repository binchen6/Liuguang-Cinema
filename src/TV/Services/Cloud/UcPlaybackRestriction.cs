using System.Net;

namespace TV.Services.Cloud;

internal sealed class UcPlaybackRestrictionException(string message) : InvalidOperationException(message);

internal static class UcPlaybackRestriction
{
    private const long MiB = 1024 * 1024;
    internal const string OriginalMessage = "UC 网盘未向当前应用提供此文件的原画。该资源可能要求会员权限或指定客户端，可在 UC 网盘官方客户端检查播放权限。";
    internal const string PlaybackMessage = "UC 网盘未向当前应用提供可播放的视频。请在 UC 网盘官方客户端检查账号权限，或选择其他可用线路。";

    internal static bool HasRestrictionMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains("uc_browser", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("vip_required", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("membership required", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("会员", StringComparison.Ordinal) &&
                (value.Contains("开通", StringComparison.Ordinal) || value.Contains("仅", StringComparison.Ordinal) || value.Contains("需要", StringComparison.Ordinal) ||
                 value.Contains("权益", StringComparison.Ordinal) || value.Contains("限制", StringComparison.Ordinal)) ||
            value.Contains("浏览器", StringComparison.Ordinal) &&
                (value.Contains("当前浏览器", StringComparison.Ordinal) || value.Contains("UC", StringComparison.OrdinalIgnoreCase)) &&
                (value.Contains("播放", StringComparison.Ordinal) || value.Contains("仅", StringComparison.Ordinal));
    }

    internal static bool IsRestricted(CloudMediaProbe probe, string fileName, long? expectedSize = null)
    {
        if (probe.StatusCode == HttpStatusCode.Forbidden || HasRestrictionMarker(probe.ErrorText)) return true;
        var returnedName = probe.FileName?.Trim('"');
        if (!string.IsNullOrEmpty(returnedName) &&
            !string.Equals(fileName, returnedName, StringComparison.OrdinalIgnoreCase) &&
            HasRestrictionMarker(returnedName)) return true;

        // A size-only inference applies to the known short promo, and only to original downloads.
        return expectedSize is > 0 && probe.TotalLength is >= 12 * MiB and <= 17 * MiB &&
            Math.Abs(expectedSize.Value - probe.TotalLength.Value) >= 2 * MiB;
    }
}
