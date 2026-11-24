using System.Collections.Concurrent;
using System.Net;
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
    private readonly ConcurrentDictionary<string, (string DriveId, string FileId)> _savedIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _saveDirIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private DriveAccountContext? _account;
    private DateTimeOffset _accessExpiresAt;
    private readonly SemaphoreSlim _accountLock = new(1, 1);

    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Referer"] = "https://www.alipan.com/",
        ["Origin"] = "https://www.alipan.com"
    };

    private static readonly string[] ShareInfoEndpoints =
    [
        "https://api.alipan.com/adrive/v3/share_link/get_share_by_anonymous",
        "https://api.aliyundrive.com/adrive/v3/share_link/get_share_by_anonymous"
    ];

    private static readonly string[] ShareTokenEndpoints =
    [
        "https://api.alipan.com/v2/share_link/get_share_token",
        "https://api.aliyundrive.com/v2/share_link/get_share_token"
    ];

    private static readonly string[] ShareListEndpoints =
    [
        "https://api.alipan.com/adrive/v2/file/list_by_share",
        "https://api.aliyundrive.com/adrive/v2/file/list_by_share",
        "https://api.alipan.com/adrive/v3/file/list"
    ];

    private static readonly string[] BatchEndpoints =
    [
        "https://api.alipan.com/adrive/v4/batch",
        "https://api.alipan.com/v3/batch",
        "https://api.aliyundrive.com/v3/batch"
    ];

    private static readonly string[] CreateFolderEndpoints =
    [
        "https://api.alipan.com/adrive/v2/file/createWithFolders",
        "https://api.alipan.com/adrive/v2/file/create_with_proof",
        "https://api.aliyundrive.com/adrive/v2/file/createWithFolders"
    ];

    private static readonly string[] SharePreviewEndpoints =
    [
        "https://api.alipan.com/v2/file/get_share_link_video_preview_play_info",
        "https://api.aliyundrive.com/v2/file/get_share_link_video_preview_play_info"
    ];

    private static readonly string[] LegacyShareDownloadEndpoints =
    [
        "https://api.alipan.com/v2/file/get_share_link_download_url",
        "https://api.aliyundrive.com/v2/file/get_share_link_download_url"
    ];

    private static readonly string[] DirectCopyEndpoints =
    [
        "https://api.alipan.com/adrive/v2/file/copy",
        "https://api.alipan.com/v2/file/copy"
    ];

    private static readonly string[] OpenApiDownloadEndpoints = ["https://openapi.alipan.com/adrive/v1.0/openFile/getDownloadUrl"];
    private static readonly string[] WebDownloadEndpoints = ["https://api.alipan.com/v2/file/get_download_url", "https://api.aliyundrive.com/v2/file/get_download_url"];
    private static readonly string[] OpenApiListEndpoints = ["https://openapi.alipan.com/adrive/v1.0/openFile/list"];
    private static readonly string[] OpenApiSearchEndpoints = ["https://openapi.alipan.com/adrive/v1.0/openFile/search"];
    private static readonly string[] OpenApiCreateEndpoints = ["https://openapi.alipan.com/adrive/v1.0/openFile/create"];
    private static readonly string[] WebListEndpoints = ["https://api.alipan.com/adrive/v3/file/list", "https://api.aliyundrive.com/adrive/v3/file/list"];
    private static readonly string[] WebSearchEndpoints = ["https://api.alipan.com/adrive/v3/file/search", "https://api.aliyundrive.com/adrive/v3/file/search"];
    private static readonly string[] OpenApiUserEndpoints = ["https://openapi.alipan.com/adrive/v1.0/user/getDriveInfo", "https://openapi.aliyundrive.com/adrive/v1.0/user/getDriveInfo"];
    private static readonly string[] WebUserEndpoints = ["https://api.alipan.com/v2/user/get", "https://api.aliyundrive.com/v2/user/get", "https://user.alipan.com/v2/user/get", "https://user.aliyundrive.com/v2/user/get"];

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
        _savedIds.Clear();
        _saveDirIds.Clear();
    }

    public async Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!CloudShareLink.TryParse(url, out var link) || link.Provider != Kind)
            throw new ArgumentException("阿里分享链接格式无效。", nameof(url));
        var shareId = link.ShareId;
        var infoTask = PostWithFallbackAsync(ShareInfoEndpoints, new { share_id = shareId }, Headers, cancellationToken);
        var tokenTask = GetShareTokenAsync(shareId, link.Passcode, cancellationToken);

        var info = await infoTask.ConfigureAwait(false);
        try
        {
            EnsureSuccess(info, "阿里分享不存在或已失效");
        }
        catch
        {
            _ = tokenTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }

        var root = info.Json;
        string token;
        try
        {
            token = await tokenTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            string.IsNullOrWhiteSpace(link.Passcode) &&
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("has_pwd", out var hasPwd) &&
            hasPwd.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("阿里分享需要提取码。", ex);
        }

        var files = new List<CloudFile>();
        var subtitles = new List<CloudFile>();
        var initialParent = string.IsNullOrEmpty(link.FolderId) ? "root" : link.FolderId;
        await ListFilesAsync(link, initialParent, token, string.Empty, files, subtitles, 0, cancellationToken).ConfigureAwait(false);

        // Fallback: if listing "root" returned nothing, try any folder IDs advertised in anonymous share metadata.
        if (string.IsNullOrEmpty(link.FolderId) && files.Count == 0 && subtitles.Count == 0 &&
            root.TryGetProperty("file_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in infos.EnumerateArray())
            {
                var entryType = First(String(entry, "type"), String(entry, "file_type"));
                var entryId = String(entry, "file_id");
                if (entryType == "folder" && CloudShareLink.IsIdentifier(entryId))
                    await ListFilesAsync(link, entryId, token, String(entry, "file_name"), files, subtitles, 1, cancellationToken).ConfigureAwait(false);
            }
        }

        if (files.Count == 0 && subtitles.Count == 0)
            throw new InvalidDataException("阿里分享中没有可播放的媒体文件。");

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
            var result = await PostWithFallbackAsync(
                ShareTokenEndpoints,
                new { share_id = shareId, share_pwd = passcode },
                Headers,
                CancellationToken.None).ConfigureAwait(false);
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
                ["limit"] = 200,
                ["share_id"] = link.ShareId,
                ["parent_file_id"] = parent,
                ["order_by"] = "name",
                ["order_direction"] = "ASC",
                ["url_expire_sec"] = 14400
            };
            if (marker is not null) body["marker"] = marker;
            var headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase) { ["x-share-token"] = token };
            var result = await PostWithFallbackAsync(ShareListEndpoints, body, headers, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "阿里文件列表读取失败");
            var items = result.Json.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array : default;
            if (items.ValueKind != JsonValueKind.Array) break;
            var folders = new List<(string Id, string Name)>();
            foreach (var item in items.EnumerateArray())
            {
                var type = First(String(item, "type"), String(item, "file_type"));
                var id = String(item, "file_id");
                var name = String(item, "name");
                if (!CloudShareLink.IsIdentifier(id)) continue;
                if (type == "folder") folders.Add((id, name));
                else if (type == "file" && CloudSubtitleFiles.IsSubtitle(name))
                {
                    if (subtitles.Count < 1000)
                        subtitles.Add(new CloudFile(Kind, link.ShareId, id, string.Empty, name, Long(item, "size"), parentName, link.Passcode, link.FolderId, parent));
                }
                else if (type == "file" && IsMedia(String(item, "category"), String(item, "mime_type"), name))
                    files.Add(new CloudFile(Kind, link.ShareId, id, string.Empty, name, Long(item, "size"), parentName, link.Passcode, link.FolderId, parent));
                if (files.Count >= 500) break;
            }
            foreach (var folder in folders)
                await ListFilesAsync(link, folder.Id, token, folder.Name, files, subtitles, depth + 1, cancellationToken).ConfigureAwait(false);
            marker = String(result.Json, "next_marker");
        } while (!string.IsNullOrWhiteSpace(marker) && files.Count < 500);
    }

    public async Task<CloudPlayResult> PlayAsync(CloudFile file, string quality, CancellationToken cancellationToken = default)
    {
        if (file.ShareId == "drive")
        {
            if (!quality.Contains("原画", StringComparison.OrdinalIgnoreCase) && quality != "自动")
                throw new NotSupportedException("阿里云盘当前线路仅提供原画。");
            return await PlayPersonalFileAsync(file.FileToken, file.FileId, file.Name, cancellationToken).ConfigureAwait(false);
        }

        var shareToken = await GetShareTokenAsync(file.ShareId, file.Passcode, cancellationToken).ConfigureAwait(false);
        var contentType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(file.Name), null);

        // Authenticated playback path (required since Alipan retired anonymous get_share_link_download_url with HTTP 410):
        // Save the shared item to the user's drive folder "流光影院" and resolve its original download URL,
        // falling back to authenticated share video preview transcoding if copy fails (e.g. drive full).
        if (_credential is not null)
        {
            var ctx = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var saved = await SaveShareFileAsync(ctx, file, shareToken, forceRefresh: false, cancellationToken).ConfigureAwait(false);
                try
                {
                    return await PlayPersonalFileAsync(saved.DriveId, saved.FileId, file.Name, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (IsMissingFileError(ex.Message))
                {
                    // Cached copied file was deleted in user's drive — re-copy and retry once.
                    var recopied = await SaveShareFileAsync(ctx, file, shareToken, forceRefresh: true, cancellationToken).ConfigureAwait(false);
                    return await PlayPersonalFileAsync(recopied.DriveId, recopied.FileId, file.Name, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (CloudAuthenticationException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception copyEx) when (!CloudSubtitleFiles.IsSubtitle(file.Name))
            {
                Diagnostics.Log($"阿里分享转存原画失败，尝试分享转码预览: {copyEx.Message}");
                var previewUrl = await TryGetSharePreviewUrlAsync(file, shareToken, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(previewUrl))
                    return new CloudPlayResult(previewUrl, Headers, FileExtension: CloudSubtitleFiles.Extension(file.Name));
                throw;
            }
        }

        // Legacy anonymous share download fallback (for test fixtures / environments without account credentials).
        var headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase) { ["x-share-token"] = shareToken };
        var result = await PostWithFallbackAsync(
            LegacyShareDownloadEndpoints,
            new { file_id = file.FileId, share_id = file.ShareId, expire_sec = 600 },
            headers,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "阿里分享原画播放失败");
        var url = First(String(result.Json, "download_url"), String(result.Json, "url"));
        if (url.Length == 0) throw new InvalidDataException("阿里未返回下载地址，可能需要在设置中登录。");
        return new CloudPlayResult(url, Headers, contentType, CloudSubtitleFiles.Extension(file.Name));
    }

    private async Task<CloudPlayResult> PlayPersonalFileAsync(string driveHint, string fileId, string fileName, CancellationToken cancellationToken)
    {
        var response = await ExecuteDriveRequestAsync(async (ctx, ct) =>
        {
            var drive = string.IsNullOrWhiteSpace(driveHint) ? ctx.DefaultDriveId : driveHint;
            var body = ctx.IsOpenApi
                ? (object)new { drive_id = drive, file_id = fileId }
                : new { drive_id = drive, file_id = fileId, expire_sec = 14400 };
            var endpoints = ctx.IsOpenApi ? OpenApiDownloadEndpoints : WebDownloadEndpoints;
            return await PostWithFallbackAsync(endpoints, body, AccountHeaders(ctx), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "阿里个人文件原画播放失败");
        var address = First(String(response.Json, "url"), String(response.Json, "download_url"));
        if (!Uri.TryCreate(address, UriKind.Absolute, out _)) throw new InvalidDataException("阿里未返回个人文件播放地址。");
        var mediaType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(fileName), null);
        return new CloudPlayResult(address, Headers, mediaType, CloudSubtitleFiles.Extension(fileName));
    }

    private async Task<(string DriveId, string FileId)> SaveShareFileAsync(
        DriveAccountContext ctx, CloudFile file, string shareToken, bool forceRefresh, CancellationToken cancellationToken)
    {
        var targetDrive = ctx.ResourceDriveId.Length > 0 ? ctx.ResourceDriveId : ctx.DefaultDriveId;
        var accountKey = ctx.UserId.Length > 0 ? ctx.UserId : targetDrive;
        var cacheKey = $"{accountKey}/{file.ShareId}/{file.FileId}";
        var settingKey = $"cloud.saved.Ali.{cacheKey}";

        if (!forceRefresh)
        {
            if (_savedIds.TryGetValue(cacheKey, out var cached)) return cached;
            if (_database is not null)
            {
                var stored = await _database.GetSettingAsync(settingKey, cancellationToken).ConfigureAwait(false);
                if (TryParseSavedPair(stored, out var pair))
                    return _savedIds[cacheKey] = pair;
            }
        }
        else
        {
            _savedIds.TryRemove(cacheKey, out _);
            if (_database is not null)
                await _database.SetSettingAsync(settingKey, string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _savedIds.TryGetValue(cacheKey, out var cached)) return cached;
            var saveDirId = await EnsureSaveDirAsync(targetDrive, cancellationToken).ConfigureAwait(false);

            var copyResult = await ExecuteDriveRequestAsync(async (activeCtx, ct) =>
            {
                var headers = AccountHeaders(activeCtx);
                headers["x-share-token"] = shareToken;
                var batchBody = new
                {
                    requests = new[]
                    {
                        new
                        {
                            id = "0",
                            method = "POST",
                            url = "/file/copy",
                            headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                            body = new
                            {
                                file_id = file.FileId,
                                share_id = file.ShareId,
                                auto_rename = true,
                                to_parent_file_id = saveDirId,
                                to_drive_id = targetDrive
                            }
                        }
                    },
                    resource = "file"
                };
                var res = await PostWithFallbackAsync(BatchEndpoints, batchBody, headers, ct).ConfigureAwait(false);
                if ((int)res.StatusCode is 404 or 405)
                {
                    var directBody = new
                    {
                        file_id = file.FileId,
                        share_id = file.ShareId,
                        auto_rename = true,
                        to_parent_file_id = saveDirId,
                        to_drive_id = targetDrive
                    };
                    res = await PostWithFallbackAsync(
                        DirectCopyEndpoints,
                        directBody,
                        headers,
                        ct).ConfigureAwait(false);
                }
                return res;
            }, cancellationToken).ConfigureAwait(false);

            EnsureSuccess(copyResult, "阿里分享转存失败");
            var copiedFileId = ExtractCopiedFileId(copyResult.Json, out var subStatus, out var subMessage);
            if (copiedFileId.Length == 0)
            {
                if (subStatus == 401)
                    throw new CloudAuthenticationException(Kind, "阿里云盘登录已失效，请重新扫码。");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(subMessage)
                    ? "阿里分享转存失败，未返回文件 ID。"
                    : $"阿里分享转存失败：{subMessage}");
            }

            var saved = (targetDrive, copiedFileId);
            _savedIds[cacheKey] = saved;
            if (_database is not null)
                await _database.SetSettingAsync(settingKey, $"{targetDrive}:{copiedFileId}", cancellationToken).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task<string> EnsureSaveDirAsync(string driveId, CancellationToken cancellationToken)
    {
        if (_saveDirIds.TryGetValue(driveId, out var cached)) return cached;

        // First check root listing for an existing "流光影院" folder.
        var listResult = await ExecuteDriveRequestAsync(async (ctx, ct) =>
        {
            var body = BuildDriveBody(ctx.IsOpenApi, isSearch: false, driveId, "root", marker: null);
            var endpoints = ctx.IsOpenApi ? OpenApiListEndpoints : WebListEndpoints;
            return await PostWithFallbackAsync(endpoints, body, AccountHeaders(ctx), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (listResult.StatusCode == HttpStatusCode.OK &&
            listResult.Json.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var isFolder = String(item, "type") == "folder" || String(item, "file_type") == "folder";
                if (isFolder && String(item, "name") == "流光影院" && String(item, "file_id") is { Length: > 0 } existingId)
                    return _saveDirIds[driveId] = existingId;
            }
        }

        // Create "流光影院" folder (check_name_mode = refuse returns existing folder's file_id if raced).
        var createResult = await ExecuteDriveRequestAsync(async (ctx, ct) =>
        {
            var body = new
            {
                drive_id = driveId,
                parent_file_id = "root",
                name = "流光影院",
                type = "folder",
                check_name_mode = "refuse"
            };
            var endpoints = ctx.IsOpenApi ? OpenApiCreateEndpoints : CreateFolderEndpoints;
            return await PostWithFallbackAsync(endpoints, body, AccountHeaders(ctx), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(createResult, "创建阿里播放目录失败");
        var createdId = String(createResult.Json, "file_id");
        if (createdId.Length == 0) throw new InvalidDataException("阿里云盘未返回播放目录 ID。");
        return _saveDirIds[driveId] = createdId;
    }

    private async Task<string?> TryGetSharePreviewUrlAsync(CloudFile file, string shareToken, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteDriveRequestAsync(async (ctx, ct) =>
            {
                var headers = AccountHeaders(ctx);
                headers["x-share-token"] = shareToken;
                var body = new
                {
                    share_id = file.ShareId,
                    file_id = file.FileId,
                    category = "live_transcoding",
                    template_id = ""
                };
                return await PostWithFallbackAsync(SharePreviewEndpoints, body, headers, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK || response.Json.ValueKind != JsonValueKind.Object)
                return null;
            if (!response.Json.TryGetProperty("video_preview_play_info", out var info) || info.ValueKind != JsonValueKind.Object)
                return null;
            if (!info.TryGetProperty("live_transcoding_task_list", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                return null;

            string? bestUrl = null;
            var bestRank = -1;
            foreach (var task in tasks.EnumerateArray())
            {
                var url = String(task, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                var rank = String(task, "template_id").ToUpperInvariant() switch
                {
                    "QHD" => 5,
                    "FHD" => 4,
                    "HD" => 3,
                    "SD" => 2,
                    "LD" => 1,
                    _ => 0
                };
                if (rank > bestRank)
                {
                    bestRank = rank;
                    bestUrl = uri.AbsoluteUri;
                }
            }
            return bestUrl;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Diagnostics.Log($"阿里分享预览流获取失败: {ex.GetType().Name}");
            return null;
        }
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
                (true, false) => OpenApiListEndpoints,
                (true, true) => OpenApiSearchEndpoints,
                (false, false) => WebListEndpoints,
                (false, true) => WebSearchEndpoints
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
        if (result.StatusCode == HttpStatusCode.Unauthorized)
        {
            InvalidateAccountCache();
            ctx = await GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
            result = await send(ctx, cancellationToken).ConfigureAwait(false);
            if (result.StatusCode == HttpStatusCode.Unauthorized)
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
                if (result.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Unauthorized)
                    return result;
                if (IsDefinitiveBusinessError(result))
                    return result;
                lastResult = result;
                Diagnostics.Log($"阿里网盘接口 {new Uri(endpoint).Host}{new Uri(endpoint).AbsolutePath}: HTTP {(int)result.StatusCode}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                Diagnostics.Log($"阿里网盘接口 {new Uri(endpoint).Host}{new Uri(endpoint).AbsolutePath}: {ex.GetType().Name}");
            }
        }
        if (lastResult is not null) return lastResult;
        throw lastException ?? new InvalidOperationException("阿里云盘接口请求失败。");
    }

    private static bool IsDefinitiveBusinessError(CloudHttpResult result)
    {
        if (result.StatusCode != HttpStatusCode.BadRequest && result.StatusCode != HttpStatusCode.Forbidden)
            return false;
        var code = String(result.Json, "code");
        return code.StartsWith("ShareLink", StringComparison.OrdinalIgnoreCase) ||
               code.StartsWith("InvalidParameter", StringComparison.OrdinalIgnoreCase) ||
               code.Equals("NotFound.File", StringComparison.OrdinalIgnoreCase) ||
               code.Equals("NotFound.ShareLink", StringComparison.OrdinalIgnoreCase);
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

        var endpoints = token.IsOpenApi ? OpenApiUserEndpoints : WebUserEndpoints;

        var headers = BearerHeaders(token.AccessToken);
        foreach (var endpoint in endpoints)
        {
            try
            {
                var user = await _http.PostJsonAsync(endpoint, new { }, headers, cancellationToken).ConfigureAwait(false);
                if (user.StatusCode == HttpStatusCode.OK && user.Json.ValueKind == JsonValueKind.Object)
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
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
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
            if (response.StatusCode == HttpStatusCode.OK)
                return session;
            Diagnostics.Log($"阿里设备会话初始化返回 HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Diagnostics.Log($"阿里设备会话初始化跳过: {ex.GetType().Name}");
        }
        return null;
    }

    private static string ExtractCopiedFileId(JsonElement root, out int subStatus, out string subMessage)
    {
        subStatus = 200;
        subMessage = string.Empty;
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;

        // Direct /file/copy response
        var directId = String(root, "file_id");
        if (directId.Length > 0) return directId;

        // Batch /adrive/v4/batch or /v3/batch response
        if (root.TryGetProperty("responses", out var responses) && responses.ValueKind == JsonValueKind.Array && responses.GetArrayLength() > 0)
        {
            var first = responses[0];
            if (first.TryGetProperty("status", out var st) && st.TryGetInt32(out var statusNum))
                subStatus = statusNum;
            if (first.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                var id = String(body, "file_id");
                if (id.Length > 0 && subStatus is >= 200 and < 300) return id;
                subMessage = First(String(body, "message"), String(body, "code"));
            }
        }
        return string.Empty;
    }

    private static bool TryParseSavedPair(string? stored, out (string DriveId, string FileId) pair)
    {
        pair = default;
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var sep = stored.IndexOf(':');
        if (sep <= 0 || sep >= stored.Length - 1) return false;
        pair = (stored[..sep], stored[(sep + 1)..]);
        return true;
    }

    private static bool IsMissingFileError(string message) =>
        message.Contains("404", StringComparison.Ordinal) ||
        message.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("不存在", StringComparison.Ordinal);

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
        var code = String(result.Json, "code");
        var message = First(String(result.Json, "message"), code);
        if (code.Equals("ShareLink.Cancelled", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ShareLink.Forbidden", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ShareLink.NotFound", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("NotFound.ShareLink", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ShareLinkExpired", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"阿里分享已失效（{code}）");
        if (code.Equals("InvalidParameter.SharePwd", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ShareLink.InvalidPassword", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ShareLinkTokenInvalid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"阿里分享提取码错误或已失效（{code}）");
        if ((int)result.StatusCode >= 400)
            throw new InvalidOperationException(message.Length > 0
                ? $"{fallback}（HTTP {(int)result.StatusCode}）：{message}"
                : $"{fallback}（HTTP {(int)result.StatusCode}）");
        if (result.Json.ValueKind != JsonValueKind.Object) return;
        if (code.Length > 0 && !code.Equals("OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{fallback}：{message}");
    }

    private static bool IsMedia(string category, string mimeType, string name) =>
        category is "video" or "audio" ||
        mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(name).ToLowerInvariant() is
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".flv" or ".ts" or ".m2ts" or
            ".webm" or ".rmvb" or ".wmv" or ".mpg" or ".mpeg" or ".m4v" or ".3gp" or
            ".mp3" or ".m4a" or ".flac" or ".wav" or ".aac";

    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText().Trim('"') : string.Empty;
    private static long Long(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
