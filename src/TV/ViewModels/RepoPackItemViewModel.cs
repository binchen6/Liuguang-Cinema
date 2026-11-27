using TV.Services.Packs;

namespace TV.ViewModels;

public sealed record RepoPackItemViewModel(
    string Id,
    string Name,
    string Description,
    string VersionBadge,
    string MetaSummary,
    string ActionLabel,
    bool IsInstalled,
    bool CanDownload,
    RepoPackManifestEntry? Entry)
{
    public static RepoPackItemViewModel FromManifest(RepoPackManifestEntry entry, InstalledRepoPack? installed)
    {
        var isInstalled = installed is not null;
        var hasUpdate = isInstalled && RepoPackService.IsNewerVersion(entry.Version, installed!.Definition.Version);
        var versionBadge = isInstalled
            ? (hasUpdate ? $"已安装 v{installed!.Definition.Version} → 可更新 v{entry.Version}" : $"已启用 v{installed!.Definition.Version}")
            : $"v{entry.Version}";
        var hosts = entry.MatchHosts.Count > 0 ? $"适配：{string.Join(" / ", entry.MatchHosts)}" : "通用扩展包";
        var sizeText = entry.SizeBytes > 0 ? $" · {FormatKb(entry.SizeBytes)}" : string.Empty;
        var siteText = entry.SiteCount > 0 ? $" · 覆盖 {entry.SiteCount} 站" : string.Empty;
        var actionLabel = !isInstalled ? "下载" : (hasUpdate ? "更新" : "重新下载");
        return new RepoPackItemViewModel(
            entry.Id,
            entry.Name,
            entry.Description,
            versionBadge,
            $"{hosts}{siteText}{sizeText}",
            actionLabel,
            isInstalled,
            CanDownload: true,
            Entry: entry);
    }

    public static RepoPackItemViewModel FromInstalledOnly(InstalledRepoPack local) => new(
        local.Definition.Id,
        string.IsNullOrWhiteSpace(local.Definition.Name) ? local.Definition.Id : local.Definition.Name,
        local.Definition.Description,
        $"已启用 v{local.Definition.Version}",
        $"本地资源包 · 覆盖 {local.Definition.Sites.Count} 站",
        "已安装",
        IsInstalled: true,
        CanDownload: false,
        Entry: null);

    private static string FormatKb(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:0.##} MB" : $"{Math.Max(1, bytes / 1024d):0.#} KB";
}
