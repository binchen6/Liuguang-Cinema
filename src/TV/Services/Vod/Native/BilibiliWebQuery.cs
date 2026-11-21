using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace TV.Services.Vod.Native;

internal static class BilibiliWebQuery
{
    // The web API derives this compatibility signature from two public image names returned by /nav.
    private static readonly int[] MixinOrder =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
    ];

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "The public web API requires this checksum format.")]
    public static string Create(string image, string subtitle, params (string Key, string Value)[] values)
    {
        var keys = ImageKey(image) + ImageKey(subtitle);
        if (keys.Length != 64) throw new InvalidDataException("哔哩哔哩未返回投稿列表所需的公开校验参数。");
        var mixin = new string(MixinOrder.Select(index => keys[index]).ToArray());
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var query = string.Join('&', values.Append(("wts", timestamp)).OrderBy(pair => pair.Item1, StringComparer.Ordinal)
            .Select(pair => pair.Item1 + "=" + Uri.EscapeDataString(new string(pair.Item2.Where(ch => !"!'()*".Contains(ch)).ToArray()))));
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin))).ToLowerInvariant();
        return $"{query}&w_rid={digest}";
    }

    private static string ImageKey(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        ? Path.GetFileNameWithoutExtension(uri.AbsolutePath) : string.Empty;
}
