using TV.Models;

namespace TV.Services.Cloud;

public sealed record AliOAuthToken(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    bool IsOpenApi,
    string DefaultDriveId = "",
    string ResourceDriveId = "",
    string BackupDriveId = "",
    string UserId = "",
    string DeviceId = "");

public static class AliOAuth
{
    private static readonly Dictionary<string, string> TokenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Referer"] = "https://www.alipan.com/",
        ["Origin"] = "https://www.alipan.com"
    };

    private static readonly (string Url, bool OpenApi)[] Endpoints =
    [
        ("https://auth.alipan.com/v2/account/token", false),
        ("https://auth.aliyundrive.com/v2/account/token", false),
        ("https://openapi.alipan.com/oauth/access_token", true),
        ("https://api.aliyundrive.com/oauth/access_token", false)
    ];

    public static async Task<AliOAuthToken?> RefreshAsync(CloudHttpClient http, string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        foreach (var (url, openApi) in Endpoints)
        {
            try
            {
                var result = await http.PostJsonAsync(url,
                    new { grant_type = "refresh_token", refresh_token = refreshToken }, TokenHeaders, cancellationToken).ConfigureAwait(false);
                var access = String(result.Json, "access_token");
                if (result.StatusCode != System.Net.HttpStatusCode.OK || access.Length == 0)
                {
                    Diagnostics.Log($"阿里官方 Token 接口 {new Uri(url).Host}: HTTP {(int)result.StatusCode}");
                    continue;
                }
                var rotated = String(result.Json, "refresh_token");
                var expires = String(result.Json, "expires_in");
                _ = int.TryParse(expires, out var seconds);
                return new AliOAuthToken(
                    access,
                    rotated.Length > 0 ? rotated : refreshToken,
                    seconds,
                    openApi,
                    DefaultDriveId: String(result.Json, "default_drive_id"),
                    ResourceDriveId: String(result.Json, "resource_drive_id"),
                    BackupDriveId: String(result.Json, "backup_drive_id"),
                    UserId: String(result.Json, "user_id"),
                    DeviceId: String(result.Json, "device_id"));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Diagnostics.Log($"阿里官方 Token 接口 {new Uri(url).Host}: {ex.GetType().Name}");
            }
        }
        return null;
    }

    private static string String(System.Text.Json.JsonElement element, string name) => element.ValueKind == System.Text.Json.JsonValueKind.Object &&
        element.TryGetProperty(name, out var value)
        ? value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? string.Empty
            : value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetRawText() : string.Empty
        : string.Empty;
}
