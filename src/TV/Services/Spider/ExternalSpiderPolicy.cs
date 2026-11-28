using TV.Services.Persistence;

namespace TV.Services.Spider;

internal sealed class ExternalSpiderPolicy(DatabaseService database)
{
    internal const string SettingKey = "source.allowExternalExecutables";

    internal async Task<bool> IsEnabledAsync(CancellationToken token) =>
        string.Equals(await database.GetSettingAsync(SettingKey, token).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);

    internal static bool TryExecutableUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.UserInfo.Length == 0 && parsed.Fragment.Length == 0 &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback ||
                parsed.IsFile && parsed.Host.Length == 0 && Path.IsPathFullyQualified(parsed.LocalPath)))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    internal static async Task RemoveLegacyCredentialFilesAsync(DatabaseService database)
    {
        try
        {
            var accounts = new[] { (".quarkcookie", "cloud.quarkCookie"), (".uccookie", "cloud.ucCookie"), (".alitoken", "cloud.aliToken") };
            foreach (var (name, setting) in accounts)
            {
                var value = SecretStore.Unprotect(await database.GetSettingAsync(setting).ConfigureAwait(false));
                if (string.IsNullOrEmpty(value)) continue;
                foreach (var parent in new[] { Path.Combine(AppPaths.Root, "spider", "TV"), Path.Combine(AppPaths.Root, "spider", "sdcard", "TV") })
                {
                    var file = new FileInfo(Path.Combine(parent, name));
                    if (file.Exists && file.Length <= 128 * 1024 && (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        string.Equals(await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false), value, StringComparison.Ordinal))
                        file.Delete();
                }
            }
        }
        catch (Exception ex) { Diagnostics.Log("旧版站点凭据文件清理: " + ex.GetType().Name); }
    }
}
