using System.Text.Json;
using System.Text.Json.Serialization;

namespace TV.Models;

public sealed record ImportedRepository(string Url, string Name, ConfigDocument Document)
{
    public string Summary => $"{Document.Sites.Count} 个点播站点 · {Document.Lives.Count} 个直播源";
}

public sealed class ConfigDocument
{
    [JsonPropertyName("urls")] public List<ConfigDepot> Urls { get; set; } = [];
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

public sealed class SourceSite
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("ext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Ext { get; set; }
    [JsonPropertyName("jar")] public string Jar { get; set; } = string.Empty;
    [JsonPropertyName("searchable")] public int Searchable { get; set; } = 1;
    [JsonPropertyName("changeable")] public int Changeable { get; set; } = 1;
    [JsonPropertyName("quickSearch")] public int QuickSearch { get; set; } = 1;
    [JsonPropertyName("timeout")] public int Timeout { get; set; } = 30;
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
    [JsonPropertyName("header"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Header { get; set; }

    [JsonIgnore] public bool IsSupported => Type is 0 or 1 || Services.Vod.Native.HttpSpiderProvider.Supports(this)
        || Services.Vod.Native.HtmlVodProvider.Supports(this)
        || Services.Vod.Native.NativeVodProviderRouter.IsSearchOnly(this)
        || Services.Vod.Native.MyDriveVodProvider.Supports(this)
        || Services.Vod.Native.PushVodProvider.Supports(this)
        || Type == 3 && Services.Spider.SpiderCatalog.IsAvailable(Api, Ext);
    [JsonIgnore] public string DisplayName => IsSupported ? Name : $"{Name}（无公开实现）";

    public IReadOnlyDictionary<string, string> GetHeaders() => HeaderValueParser.Parse(Header);
}

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
