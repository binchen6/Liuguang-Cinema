using TV.Models;

namespace TV.Services.Cloud;

internal sealed record CloudShareLink(CloudProviderKind Provider, string ShareId, string FolderId, string Passcode)
{
    public static bool TryParse(string url, out CloudShareLink link)
    {
        link = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return false;

        CloudProviderKind? provider = uri.IdnHost switch
        {
            "pan.quark.cn" => CloudProviderKind.Quark,
            "drive.uc.cn" => CloudProviderKind.UC,
            "alipan.com" or "www.alipan.com" or "aliyundrive.com" or "www.aliyundrive.com" => CloudProviderKind.Ali,
            "pan.baidu.com" or "yun.baidu.com" => CloudProviderKind.Baidu,
            _ => null
        };
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (provider is null) return false;
        if (provider == CloudProviderKind.Baidu && path.Length >= 2 && path[0] == "share" && path[1] == "init")
        {
            var shortId = ReadQuery(uri, "surl");
            if (!IsIdentifier(shortId)) return false;
            link = new CloudShareLink(provider.Value, shortId.StartsWith('1') ? shortId : "1" + shortId, string.Empty, ReadPasscode(uri));
            return true;
        }
        if (path.Length < 2 || path[0] != "s" || !IsIdentifier(path[1])) return false;
        var folder = provider == CloudProviderKind.Ali && path.Length >= 4 && path[2] == "folder" && IsIdentifier(path[3])
            ? path[3] : string.Empty;
        link = new CloudShareLink(provider.Value, path[1], folder, ReadPasscode(uri));
        return true;
    }

    public static bool IsIdentifier(string value) => value.Length is > 0 and <= 160 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static string ReadPasscode(Uri uri)
    {
        var value = ReadQuery(uri, "pwd", "passcode", "share_pwd");
        return value.Length <= 64 ? value : string.Empty;
    }

    public static string ReadQuery(Uri uri, params string[] names)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0 || !names.Contains(pair[..separator], StringComparer.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    public string ToShareUrl(string fallbackPasscode = "")
    {
        var host = Provider switch
        {
            CloudProviderKind.Ali => "www.alipan.com",
            CloudProviderKind.Quark => "pan.quark.cn",
            CloudProviderKind.Baidu => "pan.baidu.com",
            _ => "drive.uc.cn"
        };
        var password = Passcode.Length == 0 && fallbackPasscode.Length <= 64 ? fallbackPasscode : Passcode;
        return $"https://{host}/s/{ShareId}" + (FolderId.Length == 0 ? "" : $"/folder/{FolderId}") + PasscodeQuery(password);
    }

    public static string PasscodeQuery(string passcode) => passcode.Length == 0 ? "" : "?pwd=" + Uri.EscapeDataString(passcode);
}
