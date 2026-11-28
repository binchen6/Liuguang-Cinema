using System.Text.Json;
using System.Text.Json.Serialization;
using TV.Models;

namespace TV.Services.Packs;

internal enum RepoPackCatalogSource { Online, Cached, Bundled }

internal sealed record RepoPackCatalog(RepoPackManifest Manifest, RepoPackCatalogSource Source, DateTimeOffset CheckedAt);

public sealed class RepoPackManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = string.Empty;
    [JsonPropertyName("packs")] public List<RepoPackManifestEntry> Packs { get; set; } = [];
}

public sealed class RepoPackManifestEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("matchHosts")] public List<string> MatchHosts { get; set; } = [];
    [JsonPropertyName("matchUrlKeywords")] public List<string> MatchUrlKeywords { get; set; } = [];
    [JsonPropertyName("matchSiteApis")] public List<string> MatchSiteApis { get; set; } = [];
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("siteCount")] public int SiteCount { get; set; }
}

public sealed class RepoPackDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("matchHosts")] public List<string> MatchHosts { get; set; } = [];
    [JsonPropertyName("matchSiteApis")] public List<string> MatchSiteApis { get; set; } = [];
    [JsonPropertyName("sites")] public List<RepoPackSiteRule> Sites { get; set; } = [];
    [JsonPropertyName("lives")] public List<LiveSource> Lives { get; set; } = [];
}

public sealed class RepoPackSiteRule
{
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty; // alias | js | cms | bili-preset | info
    [JsonPropertyName("overrideUniversal")] public bool OverrideUniversal { get; set; }
    [JsonPropertyName("aliasApi")] public string AliasApi { get; set; } = string.Empty;
    [JsonPropertyName("aliasExt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement AliasExt { get; set; }
    [JsonPropertyName("scriptFile")] public string ScriptFile { get; set; } = string.Empty;
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = string.Empty;
    [JsonPropertyName("typeFilter")] public string TypeFilter { get; set; } = string.Empty;
    [JsonPropertyName("preferredFlag")] public string PreferredFlag { get; set; } = string.Empty;
    [JsonPropertyName("categories")] public List<RepoPackCategoryPreset> Categories { get; set; } = [];
    [JsonPropertyName("defaultKeyword")] public string DefaultKeyword { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public sealed class RepoPackCategoryPreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed record InstalledRepoPack(RepoPackDefinition Definition, string DirectoryPath, DateTimeOffset InstalledAt);

public sealed record RepoPackAutoInstallResult(bool Matched, bool Installed, string? PackName, string? Version, string? RouteLabel, string Message);
