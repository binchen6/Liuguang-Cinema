using System.Collections.Concurrent;
using System.Text.Json;
using TV.Models;
using TV.Services.Persistence;

namespace TV.Services.Cloud;

public sealed class AliCloudProvider : ICloudDriveProvider
{
    private const string VirtualDrivePrefix = "__drive__:";

    private sealed record DriveAccountContext(
        string AccessToken,
        string DefaultDriveId,
        string ResourceDriveId,
        string BackupDriveId,
        string UserId,
        bool IsOpenApi,
        AliDeviceSession? DeviceSession);

    private readonly CloudHttpClient _http;
    private readonly CloudShareTokenCache _tokens = new();
    private readonly Func<CancellationToken, Task<string>>? _credential;
    private readonly DatabaseService? _database;
    private readonly ConcurrentDictionary<string, string> _markers = new(StringComparer.Ordinal);
    private DriveAccountContext? _account;
    private DateTimeOffset _accessExpiresAt;
    private readonly SemaphoreSlim _accountLock = new(1, 1);
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Referer"] = "https://www.alipan.com/",
        ["Origin"] = "https://www.alipan.com"
    };

    public AliCloudProvider(CloudHttpClient http, Func<CancellationToken, Task<string>>? credential = null, DatabaseService? database = null)
    {
        _http = http;
        _credential = credential;
        _database = database;
    }
    public CloudProviderKind Kind => CloudProviderKind.Ali;
    public bool CanHandle(string url) => CloudShareLink.TryParse(url, out var link) && link.Provider == Kind;

    public void InvalidateAccountCache()
    {
        _account = null;
        _accessExpiresAt = DateTimeOffset.MinValue;
        _markers.Clear();
    }

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
            var response = await ExecuteDriveRequestAsync(async (ctx, ct) =>
            {
                var drive = string.IsNullOrWhiteSpace(file.FileToken) ? ctx.DefaultDriveId : file.FileToken;
                var body = ctx.IsOpenApi
                    ? (object)new { drive_id = drive, file_id = file.FileId }
                    : new { drive_id = drive, file_id = file.FileId, expire_sec = 14400 };
                var endpoints = ctx.IsOpenApi
                    ? new[] { "https://openapi.alipan.com/adrive/v1.0/openFile/getDownloadUrl" }
                    : new[] { "https://api.alipan.com/v2/file/get_download_url", "https://api.aliyundrive.com/v2/file/get_download_url" };
                return await PostWithFallbackAsync(endpoints, body, AccountHeaders(ctx), ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "阿里个人文件原画播放失败");
            var address = First(String(response.Json, "url"), String(response.Json, "download_url"));
            if (!Uri.TryCreate(address, UriKind.Absolute, out _)) throw new InvalidDataException("阿里未返回个人文件播放地址。");
            var mediaType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(file.Name), null);
            return new CloudPlayResult(address, Headers, mediaType, CloudSubtitleFiles.Extension(file.Name));
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
        var ctx = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        page = Math.Max(1, page);
        var hasDualDrives = HasDualDrives(ctx);
        if (hasDualDrives && string.Equals(folderId, "root", StringComparison.OrdinalIgnoreCase))
        {
            return new CloudDriveListing(
            [
                new CloudDriveItem(Kind, EncodeVirtualFolder(ctx.ResourceDriveId, "root"), "root", "资源库", "/资源库", 0, true, null, ctx.ResourceDriveId),
                new CloudDriveItem(Kind, EncodeVirtualFolder(ctx.BackupDriveId, "root"), "root", "备份盘", "/备份盘", 0, true, null, ctx.BackupDriveId)
            ], false);
        }

        var (drive, parentId) = DecodeFolder(folderId, ctx.DefaultDriveId);
        return await ReadDriveAsync(
            isSearch: false,
            driveId: drive,
            parentOrQuery: parentId,
            key: $"list/{drive}/{parentId}",
            path: path,
            page: page,
            encodeSubfolders: hasDualDrives,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudDriveListing> SearchFolderAsync(string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new CloudDriveListing([], false);
        var ctx = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        page = Math.Max(1, page);
        var term = keyword.Trim();
        var hasDualDrives = HasDualDrives(ctx);
        if (hasDualDrives && page == 1)
        {
            var resourceTask = ReadDriveAsync(true, ctx.ResourceDriveId, term, $"search/{ctx.ResourceDriveId}/{term}", "/资源库", 1, true, cancellationToken);
            var backupTask = ReadDriveAsync(true, ctx.BackupDriveId, term, $"search/{ctx.BackupDriveId}/{term}", "/备份盘", 1, true, cancellationToken);
            await Task.WhenAll(resourceTask, backupTask).ConfigureAwait(false);
            var resource = await resourceTask.ConfigureAwait(false);
            var backup = await backupTask.ConfigureAwait(false);
            var merged = new List<CloudDriveItem>(resource.Items.Count + backup.Items.Count);
            merged.AddRange(resource.Items);
            merged.AddRange(backup.Items);
            return new CloudDriveListing(merged, resource.HasMore);
        }
        var searchDrive = hasDualDrives ? ctx.ResourceDriveId : ctx.DefaultDriveId;
        var rootPath = hasDualDrives ? "/资源库" : "/";
        return await ReadDriveAsync(true, searchDrive, term, $"search/{searchDrive}/{term}", rootPath, page, hasDualDrives, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CloudDriveListing> ReadDriveAsync(bool isSearch, string driveId, string parentOrQuery, string key, string path,
        int page, bool encodeSubfolders, CancellationToken cancellationToken)
    {
        string? marker = null;
        if (page > 1)
        {
            if (!_markers.TryGetValue($"{key}/{page}", out marker)) throw new InvalidDataException("请按顺序加载下一页。");
        }
        var result = await ExecuteDriveRequestAsync(async (ctx, ct) =>
        {
            var body = BuildDriveBody(ctx.IsOpenApi, isSearch, driveId, parentOrQuery, marker);
            var endpoints = (ctx.IsOpenApi, isSearch) switch
            {
                (true, false) => new[] { "https://openapi.alipan.com/adrive/v1.0/openFile/list" },
                (true, true) => new[] { "https://openapi.alipan.com/adrive/v1.0/openFile/search" },
                (false, false) => new[] { "https://api.alipan.com/adrive/v3/file/list", "https://api.aliyundrive.com/adrive/v3/file/list" },
                (false, true) => new[] { "https://api.alipan.com/adrive/v3/file/search", "https://api.aliyundrive.com/adrive/v3/file/search" }
            };
            return await PostWithFallbackAsync(endpoints, body, AccountHeaders(ctx), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
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
                var isFolder = String(item, "type") == "folder" || String(item, "file_type") == "folder";
                var entryId = isFolder && encodeSubfolders ? EncodeVirtualFolder(driveId, id) : id;
                DateTimeOffset? modified = DateTimeOffset.TryParse(String(item, "updated_at"), out var updated) ? updated : null;
                entries.Add(new CloudDriveItem(Kind, entryId, parent, name, itemPath, Long(item, "size"),
                    isFolder, modified, driveId));
            }
        var next = String(result.Json, "next_marker");
        if (next.Length > 0) _markers[$"{key}/{page + 1}"] = next;
        return new CloudDriveListing(entries, next.Length > 0);
    }

    private static Dictionary<string, object?> BuildDriveBody(bool openApi, bool isSearch, string driveId, string parentOrQuery, string? marker)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = driveId,
            ["limit"] = 100
        };
        if (isSearch)
        {
            body["query"] = FormatSearchQuery(parentOrQuery);
            body["order_by"] = openApi ? "name ASC" : "updated_at DESC";
        }
        else
        {
            body["parent_file_id"] = parentOrQuery;
            body["order_by"] = "name";
            body["order_direction"] = "ASC";
            if (!openApi)
            {
                body["all"] = false;
                body["url_expire_sec"] = 14400;
                body["fields"] = "*";
            }
        }
        if (!string.IsNullOrEmpty(marker)) body["marker"] = marker;
        return body;
    }

    private static string FormatSearchQuery(string keyword)
    {
        var trimmed = keyword.Trim();
        if (trimmed.Contains(" match ", StringComparison.OrdinalIgnoreCase)) return trimmed;
        var escaped = trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"name match \"{escaped}\"";
    }

    private async Task<CloudHttpResult> ExecuteDriveRequestAsync(
        Func<DriveAccountContext, CancellationToken, Task<CloudHttpResult>> send,
        CancellationToken cancellationToken)
    {
        var ctx = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        var result = await send(ctx, cancellationToken).ConfigureAwait(false);
        if (result.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            InvalidateAccountCache();
            ctx = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
            result = await send(ctx, cancellationToken).ConfigureAwait(false);
            if (result.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new CloudAuthenticationException(Kind, "阿里云盘登录已失效，请重新扫码。");
        }
        return result;
    }

    private async Task<CloudHttpResult> PostWithFallbackAsync(string[] endpoints, object body, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        CloudHttpResult? lastResult = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                var result = await _http.PostJsonAsync(endpoint, body, headers, cancellationToken).ConfigureAwait(false);
                if (result.StatusCode == System.Net.HttpStatusCode.OK || result.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return result;
                lastResult = result;
                Diagnostics.Log($"阿里网盘接口 {new Uri(endpoint).Host}{new Uri(endpoint).AbsolutePath}: HTTP {(int)result.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                Diagnostics.Log($"阿里网盘接口 {new Uri(endpoint).Host}{new Uri(endpoint).AbsolutePath}: {ex.GetType().Name}");
            }
        }
        if (lastResult is not null) return lastResult;
        throw lastException ?? new InvalidOperationException("阿里云盘接口请求失败。");
    }

    private async Task<DriveAccountContext> GetAccountContextAsync(CancellationToken cancellationToken)
    {
        var cached = _account;
        if (cached is not null && cached.AccessToken.Length > 0 && cached.DefaultDriveId.Length > 0 &&
            _accessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return cached;
        await _accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _account;
            if (cached is not null && cached.AccessToken.Length > 0 && cached.DefaultDriveId.Length > 0 &&
                _accessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return cached;
            if (_credential is null) throw new CloudAuthenticationException(Kind, "阿里云盘需要扫码登录。");
            var refresh = await _credential(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refresh)) throw new CloudAuthenticationException(Kind, "阿里云盘需要扫码登录。");
            var token = await AliOAuth.RefreshAsync(_http, refresh, cancellationToken).ConfigureAwait(false);
            if (token is null) throw new CloudAuthenticationException(Kind, "阿里云盘登录已失效，请重新扫码。");
            if (_database is not null && token.RefreshToken.Length > 0 && token.RefreshToken != refresh)
                await _database.SetSettingAsync("cloud.aliToken", SecretStore.Protect(token.RefreshToken), cancellationToken).ConfigureAwait(false);

            var (defaultDrive, resourceDrive, backupDrive, userId) = await ReadDriveInfoAsync(token, cancellationToken).ConfigureAwait(false);
            var primaryDrive = First(defaultDrive, resourceDrive, backupDrive);
            if (primaryDrive.Length == 0)
                throw new InvalidDataException("阿里未返回默认网盘 ID，请重新验证登录。");
            if (backupDrive.Length == 0) backupDrive = defaultDrive.Length > 0 ? defaultDrive : primaryDrive;

            AliDeviceSession? session = null;
            if (!token.IsOpenApi && userId.Length > 0)
                session = await TryCreateDeviceSessionAsync(token.AccessToken, userId, token.DeviceId, cancellationToken).ConfigureAwait(false);

            var next = new DriveAccountContext(token.AccessToken, primaryDrive, resourceDrive, backupDrive, userId, token.IsOpenApi, session);
            _account = next;
            _accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn > 600 ? token.ExpiresIn - 300 : 1800);
            return next;
        }
        finally { _accountLock.Release(); }
    }

    private async Task<(string DefaultDrive, string ResourceDrive, string BackupDrive, string UserId)> ReadDriveInfoAsync(
        AliOAuthToken token, CancellationToken cancellationToken)
    {
        var defaultDrive = token.DefaultDriveId;
        var resourceDrive = token.ResourceDriveId;
        var backupDrive = token.BackupDriveId;
        var userId = token.UserId;

        string[] endpoints = token.IsOpenApi
            ? ["https://openapi.alipan.com/adrive/v1.0/user/getDriveInfo", "https://openapi.aliyundrive.com/adrive/v1.0/user/getDriveInfo"]
            : ["https://api.alipan.com/v2/user/get", "https://api.aliyundrive.com/v2/user/get", "https://user.alipan.com/v2/user/get", "https://user.aliyundrive.com/v2/user/get"];

        var headers = BearerHeaders(token.AccessToken);
        foreach (var endpoint in endpoints)
        {
            try
            {
                var user = await _http.PostJsonAsync(endpoint, new { }, headers, cancellationToken).ConfigureAwait(false);
                if (user.StatusCode == System.Net.HttpStatusCode.OK && user.Json.ValueKind == JsonValueKind.Object)
                {
                    var d = String(user.Json, "default_drive_id");
                    var r = String(user.Json, "resource_drive_id");
                    var b = String(user.Json, "backup_drive_id");
                    var u = String(user.Json, "user_id");
                    if (d.Length > 0) defaultDrive = d;
                    if (r.Length > 0) resourceDrive = r;
                    if (b.Length > 0) backupDrive = b;
                    if (u.Length > 0) userId = u;
                    if (First(defaultDrive, resourceDrive, backupDrive).Length > 0)
                        return (defaultDrive, resourceDrive, backupDrive, userId);
                }
                Diagnostics.Log($"阿里账号信息接口 {new Uri(endpoint).Host}: HTTP {(int)user.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log($"阿里账号信息接口 {new Uri(endpoint).Host}: {ex.GetType().Name}");
            }
        }
        return (defaultDrive, resourceDrive, backupDrive, userId);
    }

    private async Task<AliDeviceSession?> TryCreateDeviceSessionAsync(string accessToken, string userId, string? existingDeviceId, CancellationToken cancellationToken)
    {
        try
        {
            var session = AliDeviceSession.Create(userId, existingDeviceId);
            var headers = BearerHeaders(accessToken);
            headers["x-device-id"] = session.DeviceId;
            headers["x-signature"] = session.SignatureHex;
            headers["x-canary"] = "client=web,app=adrive,version=v6.8.12";
            var payload = new { deviceName = "Edge浏览器", modelName = "Windows网页版", pubKey = session.PublicKeyHex };
            var response = await _http.PostJsonAsync("https://api.alipan.com/users/v1/users/device/create_session",
                payload, headers, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
                return session;
            Diagnostics.Log($"阿里设备会话初始化返回 HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"阿里设备会话初始化跳过: {ex.GetType().Name}");
        }
        return null;
    }

    private static bool HasDualDrives(DriveAccountContext ctx) =>
        ctx.ResourceDriveId.Length > 0 && ctx.BackupDriveId.Length > 0 &&
        !ctx.ResourceDriveId.Equals(ctx.BackupDriveId, StringComparison.Ordinal);

    private static string EncodeVirtualFolder(string driveId, string folderId) =>
        VirtualDrivePrefix + driveId + ":" + folderId;

    private static (string DriveId, string FolderId) DecodeFolder(string folderId, string fallbackDriveId)
    {
        if (folderId.StartsWith(VirtualDrivePrefix, StringComparison.Ordinal))
        {
            var rest = folderId[VirtualDrivePrefix.Length..];
            var split = rest.IndexOf(':');
            if (split > 0 && split < rest.Length - 1)
                return (rest[..split], rest[(split + 1)..]);
        }
        return (fallbackDriveId, folderId);
    }

    private static Dictionary<string, string> BearerHeaders(string access) => new(Headers, StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer " + access
    };

    private static Dictionary<string, string> AccountHeaders(DriveAccountContext ctx)
    {
        var headers = BearerHeaders(ctx.AccessToken);
        if (!ctx.IsOpenApi)
            headers["x-canary"] = "client=web,app=adrive,version=v6.8.12";
        if (ctx.DeviceSession is { } session)
        {
            headers["x-device-id"] = session.DeviceId;
            headers["x-signature"] = session.SignatureHex;
        }
        return headers;
    }

    private static string JoinPath(string parent, string name) => (parent.Length == 0 ? "/" : parent).TrimEnd('/') + "/" + name;

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
