using System.Text.Json;

namespace TV.Services.Spider;

public static class SpiderCatalog
{
    private static readonly HashSet<string> Available = new(StringComparer.OrdinalIgnoreCase)
    {
        "AiDj", "Ali", "AList", "Alllive", "Appgz", "AppTT", "AppYsV2", "Auete", "Bili", "BpanSo", "Bttwoo", "Dm84", "Doll", "Douban", "Doubao", "DouDou",
        "Eighteen", "FirstAid", "Hanime", "Hanquan", "Jable", "JavDb", "Jianpian", "JPJ", "Jpys", "JumiTv", "JustLive", "Kanqiu", "KkSs", "Kugou",
        "Libvio", "LiveGz", "Local", "Miss", "Music", "MyDrive", "NewCz", "NiNi", "Nmyswv", "PanSearch", "PanSou", "Push", "S_zps", "Seedhub", "ShortDrama", "SixV",
        "Sssp", "Star", "UpYun", "UuSs", "Wogg", "Xb6v", "XiaoZhiTiao", "XPath", "XPathFilter", "XPathMac", "XPathMacFilter", "YGP", "Ying",
        "Yingshiche", "YiSo", "YpanSo", "Ysj", "YueTing", "Zhaozy", "Quark", "UC", "Cloud"
    };

    private static readonly HashSet<string> NonPlayablePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Notice", "Market", "LocalFile", "Config", "ConfigCenter", "Danmu", "PushAgent"
    };

    private static readonly string[] BuiltInDrpyScripts = ["虎牙.js", "斗鱼直播.js", "兔小贝.js"];

    public static bool IsAvailable(string api, JsonElement ext = default, string? effectiveJar = null)
    {
        if (string.IsNullOrWhiteSpace(api)) return false;
        if (api.Contains("drpy2", StringComparison.OrdinalIgnoreCase) && ext.ValueKind == JsonValueKind.String)
        {
            var script = Uri.UnescapeDataString(ext.GetString() ?? string.Empty);
            if (BuiltInDrpyScripts.Any(name => script.Contains(name, StringComparison.Ordinal)))
                return true;
        }
        if (!api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)) return false;
        var name = api[4..];
        if (name.EndsWith("Guard", StringComparison.OrdinalIgnoreCase) && name.Length > 5) name = name[..^5];
        if (NonPlayablePlaceholders.Contains(name)) return false;

        // XPath spiders require a rule JSON or rule URL in ext; empty-ext entries (e.g. notice banners) cannot be queried.
        if (name.StartsWith("XPath", StringComparison.OrdinalIgnoreCase) || name.StartsWith("XYQ", StringComparison.OrdinalIgnoreCase))
        {
            var hasExt = ext.ValueKind switch
            {
                JsonValueKind.String => !string.IsNullOrWhiteSpace(ext.GetString()),
                JsonValueKind.Object => ext.EnumerateObject().Any(),
                _ => false
            };
            if (!hasExt) return false;
        }

        if (Available.Contains(name)) return true;

        // Any external TVBox repository supplying a spider JAR (Android DEX or JVM JAR) can be dynamically loaded.
        if (!string.IsNullOrWhiteSpace(effectiveJar))
        {
            var jarUrl = effectiveJar.Split(';', 2)[0].Trim();
            if (jarUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                jarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                jarUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static void Register(string name) => Available.Add(name);
}
