using System.Text.Json;
using System.Text.Json.Serialization;

namespace TV.Models;

public sealed record ImportedRepository(string Url, string Name, ConfigDocument Document, string? GroupUrl = null)
{
    public string Summary => $"{Document.Sites.Count} 个点播站点 · {Document.Lives.Count} 个直播源";
}

/// <summary>
/// A selectable source repository (either the built-in default repo or an imported single/sub repository).
/// </summary>
public sealed record SourceRepository(
    string Id,
    string Name,
    string Url,
    bool IsBuiltIn,
    ConfigDocument Document,
    string? GroupUrl = null)
{
    public string Summary => $"{Document.Sites.Count} 个点播站点 · {Document.Lives.Count} 个直播源";
    public string MenuLabel => $"{Name}（{Document.Sites.Count} 站）";
    public override string ToString() => Name;
}

public sealed class ConfigDocument
{
    [JsonPropertyName("urls")] public List<ConfigDepot> Urls { get; set; } = [];
    [JsonPropertyName("storeHouse")] public List<StoreHouseEntry> StoreHouse { get; set; } = [];
    [JsonPropertyName("sites")] public List<SourceSite> Sites { get; set; } = [];
    [JsonPropertyName("lives")] public List<LiveSource> Lives { get; set; } = [];
    [JsonPropertyName("parses")] public List<ParseSource> Parses { get; set; } = [];
    [JsonPropertyName("flags")] public List<string> Flags { get; set; } = [];
    [JsonPropertyName("ads")] public List<string> Ads { get; set; } = [];
    [JsonPropertyName("rules"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Rules { get; set; }
    [JsonPropertyName("msg")] public string? Message { get; set; }
    [JsonPropertyName("spider")] public string? Spider { get; set; }
}

public sealed class ConfigDepot
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}

public sealed class StoreHouseEntry
{
    [JsonPropertyName("sourceName")] public string SourceName { get; set; } = string.Empty;
    [JsonPropertyName("sourceUrl")] public string SourceUrl { get; set; } = string.Empty;
}

public sealed class SourceSite
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("ext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Ext { get; set; }
    [JsonPropertyName("jar")] public string Jar { get; set; } = string.Empty;
    [JsonPropertyName("playUrl")] public string PlayUrl { get; set; } = string.Empty;
    [JsonPropertyName("click")] public string Click { get; set; } = string.Empty;
    [JsonPropertyName("searchable"), JsonConverter(typeof(FlexibleIntConverter))] public int Searchable { get; set; } = 1;
    [JsonPropertyName("changeable"), JsonConverter(typeof(FlexibleIntConverter))] public int Changeable { get; set; } = 1;
    [JsonPropertyName("quickSearch"), JsonConverter(typeof(FlexibleIntConverter))] public int QuickSearch { get; set; } = 1;
    [JsonPropertyName("filterable"), JsonConverter(typeof(FlexibleIntConverter))] public int Filterable { get; set; } = 1;
    [JsonPropertyName("timeout")] public int Timeout { get; set; } = 30;
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
    [JsonPropertyName("header"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Header { get; set; }

    /// <summary>
    /// Repository-level spider JAR fallback populated when loading a repository, used when site.Jar is empty.
    /// </summary>
    [JsonIgnore] public string RepositorySpider { get; set; } = string.Empty;

    [JsonIgnore] public string EffectiveJar => !string.IsNullOrWhiteSpace(Jar) ? Jar.Trim() : RepositorySpider.Trim();

    [JsonIgnore] public bool IsSupported => Type is 0 or 1 or 4 || Tool != SourceTool.None
        || Services.Vod.Native.HttpSpiderProvider.Supports(this)
        || Services.Vod.Native.HtmlVodProvider.Supports(this)
        || Services.Vod.Native.WoggNativeProvider.Supports(this)
        || Services.Vod.Native.BilibiliVodProvider.Supports(this)
        || Services.Vod.Native.UniversalProtocolVodProvider.Supports(this)
        || Services.Vod.Native.UniversalAutoHtmlVodProvider.Supports(this)
        || Services.Vod.Native.NativeVodProviderRouter.IsSearchOnly(this)
        || Services.Vod.Native.MyDriveVodProvider.Supports(this)
        || Services.Vod.Native.PushVodProvider.Supports(this)
        || Services.Spider.JsSpiderClient.Supports(this)
        || Services.Spider.PySpiderClient.Supports(this)
        || Type == 3 && Services.Spider.SpiderCatalog.IsAvailable(Api, Ext, EffectiveJar);

    [JsonIgnore] internal SourceTool Tool => Type == 3 ? Api.ToUpperInvariant() switch
    {
        "CSP_FISHCONFIG" => SourceTool.Settings,
        "CSP_LOCAL" => SourceTool.Files,
        "CSP_MARKET" => SourceTool.Market,
        "CSP_PUSH" or "CSP_PUSHGUARD" or "CSP_PUSHAGENT" => SourceTool.Push,
        _ => SourceTool.None
    } : SourceTool.None;

    [JsonIgnore] public string DisplayName => Tool switch
    {
        SourceTool.Settings => "设置中心",
        SourceTool.Files => "文件浏览",
        SourceTool.Market => "网盘市场",
        SourceTool.Push => "推送接收",
        _ => IsSupported ? Name : $"{Name}（无公开实现）"
    };

    public IReadOnlyDictionary<string, string> GetHeaders() => HeaderValueParser.Parse(Header);
}

internal enum SourceTool { None, Settings, Files, Market, Push }

public sealed class LiveSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("ua")] public string UserAgent { get; set; } = string.Empty;
    [JsonPropertyName("epg")] public string Epg { get; set; } = string.Empty;
    [JsonPropertyName("logo")] public string Logo { get; set; } = string.Empty;
    [JsonPropertyName("header"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Header { get; set; }
    [JsonIgnore] public bool IsBuiltIn { get; set; } = true;

    public IReadOnlyDictionary<string, string> GetHeaders() => HeaderValueParser.Parse(Header);
}

public sealed class ParseSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("ext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Ext { get; set; }
    [JsonPropertyName("header"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Header { get; set; }
}

public static class HeaderValueParser
{
    public static IReadOnlyDictionary<string, string> Parse(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, string>();
                using var document = JsonDocument.Parse(text);
                return Parse(document.RootElement);
            }

            if (element.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }
            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
