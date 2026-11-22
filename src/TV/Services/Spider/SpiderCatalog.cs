namespace TV.Services.Spider;

public static class SpiderCatalog
{
    private static readonly HashSet<string> Available = new(StringComparer.OrdinalIgnoreCase)
    {
        "AiDj", "Ali", "AList", "Alllive", "AppYsV2", "Bili", "BpanSo", "Dm84", "Doll", "Douban", "Doubao", "DouDou", "Eighteen", "FirstAid", "Hanime",
        "Jable", "JavDb", "Jianpian", "JPJ", "JustLive", "Kanqiu", "KkSs", "Kugou", "Libvio", "Local", "Miss", "NewCz", "NiNi", "Nmyswv", "PanSearch",
        "Kugou", "LiveGz", "Music", "PanSou", "Push", "ShortDrama", "SixV", "Sssp", "Star", "UpYun", "UuSs", "Wogg", "Xb6v", "XiaoZhiTiao", "XPath", "XPathFilter",
        "XPathMac", "XPathMacFilter", "YGP", "Ying", "Yingshiche", "YiSo", "YpanSo", "Ysj", "YueTing", "Zhaozy", "Quark", "UC", "Cloud"
    };

    public static bool IsAvailable(string api, System.Text.Json.JsonElement ext = default)
    {
        if (api.Contains("drpy2", StringComparison.OrdinalIgnoreCase) && ext.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var script = Uri.UnescapeDataString(ext.GetString() ?? string.Empty);
            return new[] { "虎牙.js", "斗鱼直播.js", "兔小贝.js" }.Any(name => script.Contains(name, StringComparison.Ordinal));
        }
        if (!api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)) return false;
        var name = api[4..];
        if (name.EndsWith("Guard", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        return Available.Contains(name);
    }

    public static void Register(string name) => Available.Add(name);
}
