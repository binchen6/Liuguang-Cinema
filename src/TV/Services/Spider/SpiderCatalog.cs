namespace TV.Services.Spider;

public static class SpiderCatalog
{
    private static readonly HashSet<string> Available = new(StringComparer.OrdinalIgnoreCase)
    {
        "AiDj", "Ali", "AList", "Alllive", "Appgz", "AppTT", "AppYsV2", "Auete", "Bili", "BpanSo", "Bttwoo", "Dm84", "Doll", "Douban", "Doubao", "DouDou",
        "Eighteen", "FirstAid", "Hanime", "Hanquan", "Jable", "JavDb", "Jianpian", "JPJ", "Jpys", "JumiTv", "JustLive", "Kanqiu", "KkSs", "Kugou",
        "Libvio", "LiveGz", "Local", "Miss", "Music", "NewCz", "NiNi", "Nmyswv", "PanSearch", "PanSou", "Push", "Seedhub", "ShortDrama", "SixV",
        "Sssp", "Star", "UpYun", "UuSs", "Wogg", "Xb6v", "XiaoZhiTiao", "XPath", "XPathFilter", "XPathMac", "XPathMacFilter", "YGP", "Ying",
        "Yingshiche", "YiSo", "YpanSo", "Ysj", "YueTing", "Zhaozy", "Quark", "UC", "Cloud"
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
