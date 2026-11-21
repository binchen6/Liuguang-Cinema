using System.Collections.Concurrent;
using System.Text.Json;
using TV.Models;
using TV.Services.Persistence;

namespace TV.Services.Cloud;

public sealed class AliCloudProvider : ICloudDriveProvider
{
    private readonly CloudHttpClient _http;
    private readonly CloudShareTokenCache _tokens = new();
    private readonly Func<CancellationToken, Task<string>>? _credential;
    private readonly DatabaseService? _database;
    private readonly ConcurrentDictionary<string, string> _markers = new(StringComparer.Ordinal);
    private string _accessToken = string.Empty;
    private string _driveId = string.Empty;
    private DateTimeOffset _accessExpiresAt;
    private bool _openApi;
    private readonly SemaphoreSlim _accountLock = new(1, 1);
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Referer"] = "https://www.aliyundrive.com/"
    };

    public AliCloudProvider(CloudHttpClient http, Func<CancellationToken, Task<string>>? credential = null, DatabaseService? database = null)
    {
        _http = http;
        _credential = credential;
        _database = database;
    }
    public CloudProviderKind Kind => CloudProviderKind.Ali;
    public bool CanHandle(string url) => CloudShareLink.TryParse(url, out var link) && link.Provider == Kind;

    public async Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!CloudShareLink.TryParse(url, out var link) || link.Provider != Kind)
            throw new ArgumentException("阿里分享链接格式无效。", nameof(url));
        var shareId = link.ShareId;
        var tokenTask = GetShareTokenAsync(shareId, link.Passcode, cancellationToken);
        var infoTask = _http.PostJsonAsync("https://api.aliyundrive.com/adrive/v3/share_link/get_share_by_anonymous", new { share_id = shareId }, Headers, cancellationToken);
        await Task.WhenAll(tokenTask, infoTask).ConfigureAwait(false);
        var token = await tokenTask.ConfigureAwait(false);
        var info = await infoTask.ConfigureAwait(false);
        EnsureSuccess(info, "阿里分享不存在或已失效");
        var root = info.Json;
        var parent = link.FolderId;
        if (string.IsNullOrEmpty(parent) && root.TryGetProperty("file_infos", out var infos) && infos.ValueKind == JsonValueKind.Array && infos.GetArrayLength() > 0)
        {
            var first = infos[0];
            parent = String(first, "file_type") == "folder" ? String(first, "file_id") : "root";
        }
        if (string.IsNullOrEmpty(parent)) parent = "root";
        var files = new List<CloudFile>();
        var subtitles = new List<CloudFile>();
        await ListFilesAsync(link, parent, token, string.Empty, files, subtitles, 0, cancellationToken).ConfigureAwait(false);
        return new CloudShare
        {
            Provider = Kind,
            ShareId = shareId,
            SourceUrl = url,
            Name = First(String(root, "share_name"), String(root, "display_name"), String(root, "share_title")),
            Picture = String(root, "avatar"),
            Files = files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            SubtitleFiles = subtitles
        };
    }

    private Task<string> GetShareTokenAsync(string shareId, string passcode, CancellationToken cancellationToken) =>
        _tokens.GetAsync(shareId, passcode, async () =>
        {
            var result = await _http.PostJsonAsync("https://api.aliyundrive.com/adrive/v3/share_link/get_share_token", new { share_id = shareId, share_pwd = passcode }).ConfigureAwait(false);
            EnsureSuccess(result, "阿里分享令牌获取失败");
            var token = String(result.Json, "share_token");
            if (token.Length == 0) throw new InvalidDataException("阿里分享令牌为空或分享已失效。");
            return token;
        }, cancellationToken);

    private async Task ListFilesAsync(CloudShareLink link, string parent, string token, string parentName, List<CloudFile> files,
        List<CloudFile> subtitles, int depth, CancellationToken cancellationToken)
    {
        if (depth > 8 || files.Count >= 500) return;
        string? marker = null;
        do
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = 200, ["share_id"] = link.ShareId, ["parent_file_id"] = parent,
                ["order_by"] = "name", ["order_direction"] = "ASC"
            };
            if (marker is not null) body["marker"] = marker;
            var headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase) { ["x-share-token"] = token };
            var result = await _http.PostJsonAsync("https://api.aliyundrive.com/adrive/v3/file/list", body, headers, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "阿里文件列表读取失败");
            var items = result.Json.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array : default;
            if (items.ValueKind != JsonValueKind.Array) break;
            var folders = new List<(string Id, string Name)>();
            foreach (var item in items.EnumerateArray())
            {
                var type = String(item, "file_type");
                var id = String(item, "file_id");
                var name = String(item, "name");
                if (!CloudShareLink.IsIdentifier(id)) continue;
                if (type == "folder") folders.Add((id, name));
                else if (type == "file" && CloudSubtitleFiles.IsSubtitle(name))
                {
                    if (subtitles.Count < 1000)
                        subtitles.Add(new CloudFile(Kind, link.ShareId, id, string.Empty, name, Long(item, "size"), parentName, link.Passcode, link.FolderId, parent));
                }
                else if (type == "file" && IsMedia(String(item, "category"), name))
                    files.Add(new CloudFile(Kind, link.ShareId, id, string.Empty, name, Long(item, "size"), parentName, link.Passcode, link.FolderId, parent));
                if (files.Count >= 500) break;
            }
            foreach (var folder in folders) await ListFilesAsync(link, folder.Id, token, folder.Name, files, subtitles, depth + 1, cancellationToken).ConfigureAwait(false);
            marker = String(result.Json, "next_marker");
        } while (!string.IsNullOrWhiteSpace(marker) && files.Count < 500);
    }

    public async Task<CloudPlayResult> PlayAsync(CloudFile file, string quality, CancellationToken cancellationToken = default)
    {
        if (file.ShareId == "drive")
        {
            if (!quality.Contains("原画", StringComparison.OrdinalIgnoreCase) && quality != "自动")
                throw new NotSupportedException("阿里云盘当前线路仅提供原画。");
            var (access, defaultDrive) = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
            var drive = string.IsNullOrWhiteSpace(file.FileToken) ? defaultDrive : file.FileToken;
            var accountHeaders = AccountHeaders(access);
            var response = await _http.PostJsonAsync(ApiBase + "/adrive/v1.0/openFile/getDownloadUrl",
                new { drive_id = drive, file_id = file.FileId }, accountHeaders, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "阿里个人文件原画播放失败");
            var address = String(response.Json, "url") is { Length: > 0 } value ? value : String(response.Json, "download_url");
            if (!Uri.TryCreate(address, UriKind.Absolute, out _)) throw new InvalidDataException("阿里未返回个人文件播放地址。");
            var mediaType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(file.Name), null);
            return new CloudPlayResult(address, accountHeaders, mediaType, CloudSubtitleFiles.Extension(file.Name));
        }
        var token = await GetShareTokenAsync(file.ShareId, file.Passcode, cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase) { ["x-share-token"] = token };
        var result = await _http.PostJsonAsync("https://api.aliyundrive.com/v2/file/get_share_link_download_url", new { file_id = file.FileId, share_id = file.ShareId, expire_sec = 600 }, headers, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "阿里分享原画播放失败");
        var url = String(result.Json, "download_url");
        if (url.Length == 0) throw new InvalidDataException("阿里未返回下载地址，可能需要在设置中登录。");
        var contentType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(file.Name), null);
        return new CloudPlayResult(url, Headers, contentType, CloudSubtitleFiles.Extension(file.Name));
    }

    public async Task<CloudDriveListing> ListFolderAsync(string folderId, string path, int page, CancellationToken cancellationToken = default)
    {
        var (access, drive) = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        page = Math.Max(1, page);
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = drive, ["parent_file_id"] = folderId, ["limit"] = 100,
            ["order_by"] = "name", ["order_direction"] = "ASC"
        };
        return await ReadDriveAsync(ApiBase + "/adrive/v1.0/openFile/list", body,
            $"list/{drive}/{folderId}", path, page, access, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudDriveListing> SearchFolderAsync(string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new CloudDriveListing([], false);
        var (access, drive) = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        page = Math.Max(1, page);
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = drive, ["query"] = keyword.Trim(), ["limit"] = 100, ["order_by"] = "name ASC"
        };
        return await ReadDriveAsync(ApiBase + "/adrive/v1.0/openFile/search", body,
            $"search/{drive}/{keyword.Trim()}", "/", page, access, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CloudDriveListing> ReadDriveAsync(string endpoint, Dictionary<string, object?> body, string key, string path,
        int page, string access, CancellationToken cancellationToken)
    {
        if (page > 1)
        {
            if (!_markers.TryGetValue($"{key}/{page}", out var marker)) throw new InvalidDataException("请按顺序加载下一页。");
            body["marker"] = marker;
        }
        var result = await _http.PostJsonAsync(endpoint, body, AccountHeaders(access), cancellationToken).ConfigureAwait(false);
        if (result.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new CloudAuthenticationException(Kind, "阿里云盘登录已失效，请重新扫码。");
        EnsureSuccess(result, "阿里个人云盘读取失败");
        var items = result.Json.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
        var entries = new List<CloudDriveItem>();
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                var id = String(item, "file_id");
                var name = String(item, "name");
                if (id.Length == 0 || name.Length == 0) continue;
                var parent = String(item, "parent_file_id");
                var itemPath = JoinPath(path, name);
                DateTimeOffset? modified = DateTimeOffset.TryParse(String(item, "updated_at"), out var updated) ? updated : null;
                entries.Add(new CloudDriveItem(Kind, id, parent, name, itemPath, Long(item, "size"),
                    String(item, "type") == "folder", modified, (string)body["drive_id"]!));
            }
        var next = String(result.Json, "next_marker");
        if (next.Length > 0) _markers[$"{key}/{page + 1}"] = next;
        return new CloudDriveListing(entries, next.Length > 0);
    }

    private async Task<(string AccessToken, string DriveId)> GetAccountContextAsync(CancellationToken cancellationToken)
    {
        if (_accessToken.Length > 0 && _driveId.Length > 0 && _accessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return (_accessToken, _driveId);
        await _accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken.Length > 0 && _driveId.Length > 0 && _accessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return (_accessToken, _driveId);
            if (_credential is null) throw new CloudAuthenticationException(Kind, "阿里云盘需要扫码登录。");
            var refresh = await _credential(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refresh)) throw new CloudAuthenticationException(Kind, "阿里云盘需要扫码登录。");
            var token = await AliOAuth.RefreshAsync(_http, refresh, cancellationToken).ConfigureAwait(false);
            if (token is null) throw new CloudAuthenticationException(Kind, "阿里云盘登录已失效，请重新扫码。");
            _openApi = token.IsOpenApi;
            if (_database is not null && token.RefreshToken.Length > 0 && token.RefreshToken != refresh)
                await _database.SetSettingAsync("cloud.aliToken", SecretStore.Protect(token.RefreshToken), cancellationToken).ConfigureAwait(false);
            _driveId = await ReadDriveIdAsync(token.AccessToken, token.IsOpenApi, cancellationToken).ConfigureAwait(false);
            _accessToken = token.AccessToken;
            _accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn > 600 ? token.ExpiresIn - 300 : 1800);
            return (_accessToken, _driveId);
        }
        finally { _accountLock.Release(); }
    }

    private async Task<string> ReadDriveIdAsync(string access, bool openApi, CancellationToken cancellationToken)
    {
        string[] endpoints = openApi
            ? ["https://openapi.alipan.com/adrive/v1.0/user/getDriveInfo"]
            : ["https://user.alipan.com/v2/user/get", "https://user.aliyundrive.com/v2/user/get", "https://api.aliyundrive.com/v2/user/get"];
        foreach (var endpoint in endpoints)
        {
            try
            {
                var user = await _http.GetAsync(endpoint, AccountHeaders(access), cancellationToken).ConfigureAwait(false);
                var drive = String(user.Json, "default_drive_id");
                if (user.StatusCode == System.Net.HttpStatusCode.OK && drive.Length > 0) return drive;
                Diagnostics.Log($"阿里账号信息接口 {new Uri(endpoint).Host}: HTTP {(int)user.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log($"阿里账号信息接口 {new Uri(endpoint).Host}: {ex.GetType().Name}");
            }
        }
        throw new InvalidDataException("阿里未返回默认网盘 ID，请重新验证登录。");
    }

    private static Dictionary<string, string> AccountHeaders(string access) => new(Headers, StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer " + access
    };

    private static string JoinPath(string parent, string name) => (parent.Length == 0 ? "/" : parent).TrimEnd('/') + "/" + name;
    private string ApiBase => _openApi ? "https://openapi.alipan.com" : "https://api.aliyundrive.com";

    private static void EnsureSuccess(CloudHttpResult result, string fallback)
    {
        if ((int)result.StatusCode >= 400) throw new InvalidOperationException($"{fallback}（HTTP {(int)result.StatusCode}）");
        if (result.Json.ValueKind != JsonValueKind.Object) return;
        var code = String(result.Json, "code");
        if (code.Length > 0 && !code.Equals("OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{fallback}：{First(String(result.Json, "message"), code)}");
    }

    private static bool IsMedia(string category, string name) => category is "video" or "audio" || Path.GetExtension(name).ToLowerInvariant() is ".mp4" or ".mkv" or ".avi" or ".mov" or ".flv" or ".mp3" or ".m4a";
    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText().Trim('"') : string.Empty;
    private static long Long(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

}
