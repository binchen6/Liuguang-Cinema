using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TV.Services.Persistence;
using TV.Models;

namespace TV.Services.Cloud;

public sealed class QuarkCloudProvider : ICloudDriveProvider
{
    private readonly CloudHttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _credential;
    private readonly DatabaseService _database;
    private readonly string _api;
    private readonly string _home;
    private readonly CloudShareTokenCache _tokens = new();
    private readonly ConcurrentDictionary<string, string> _savedIds = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly CloudCookieJar _jar = new();
    private readonly SemaphoreSlim _jarLock = new(1, 1);
    private string? _seed;
    private string _persisted = string.Empty;
    private readonly ConcurrentDictionary<string, string> _saveDirIds = new(StringComparer.Ordinal);

    public QuarkCloudProvider(CloudHttpClient http, CloudProviderKind kind, Func<CancellationToken, Task<string>> credential, DatabaseService database)
    {
        _http = http;
        Kind = kind;
        _credential = credential;
        _database = database;
        _api = kind == CloudProviderKind.UC ? "https://pc-api.uc.cn/1/clouddrive/" : "https://drive-pc.quark.cn/1/clouddrive/";
        _home = kind == CloudProviderKind.UC ? "https://drive.uc.cn/" : "https://pan.quark.cn/";
    }

    public CloudProviderKind Kind { get; }

    private string ClientQuery => Kind == CloudProviderKind.UC ? "pr=UCBrowser&fr=pc" : "pr=ucpro&fr=pc";
    public bool CanHandle(string url) => CloudShareLink.TryParse(url, out var link) && link.Provider == Kind;

    internal async Task SaveCredentialAsync(string credential, CancellationToken cancellationToken)
    {
        await _jarLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.SetSettingAsync(CredentialKey, SecretStore.Protect(credential), cancellationToken).ConfigureAwait(false);
            ResetCookieSession(credential);
            _saveDirIds.Clear();
            _savedIds.Clear();
        }
        finally { _jarLock.Release(); }
    }

    public async Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!CloudShareLink.TryParse(url, out var link) || link.Provider != Kind)
            throw new ArgumentException("网盘分享链接格式无效。", nameof(url));
        var token = await GetShareTokenAsync(link.ShareId, link.Passcode, cancellationToken).ConfigureAwait(false);
        var files = new List<CloudFile>();
        var subtitles = new List<CloudFile>();
        await ListFilesAsync(link, "0", token, string.Empty, files, subtitles, 0, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0) throw new InvalidDataException($"{Kind} 分享中没有可播放的媒体文件。");
        return new CloudShare
        {
            Provider = Kind,
            ShareId = link.ShareId,
            SourceUrl = url,
            Name = $"{Kind} 分享",
            Files = files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            SubtitleFiles = subtitles
        };
    }

    private Task<string> GetShareTokenAsync(string shareId, string passcode, CancellationToken cancellationToken) =>
        _tokens.GetAsync(shareId, passcode, async () =>
        {
            var result = await SendAsync(HttpMethod.Post, _api + "share/sharepage/token?" + ClientQuery, new { pwd_id = shareId, passcode }, string.Empty, CancellationToken.None).ConfigureAwait(false);
            EnsureSuccess(result, "分享令牌获取失败");
            var token = NestedString(result.Json, "data", "stoken");
            if (token.Length == 0) throw new InvalidDataException("分享不存在、已失效或需要提取码。");
            return token;
        }, cancellationToken);

    private async Task ListFilesAsync(CloudShareLink link, string folderId, string token, string parentName, List<CloudFile> files,
        List<CloudFile> subtitles, int depth, CancellationToken cancellationToken)
    {
        if (depth > 8 || files.Count >= 500) return;
        var page = 1;
        while (page <= 50 && files.Count < 500)
        {
            var url = _api + "share/sharepage/detail?" + ClientQuery + "&pwd_id=" + link.ShareId + "&stoken=" + Uri.EscapeDataString(token) + "&pdir_fid=" + Uri.EscapeDataString(folderId) + "&force=0&_page=" + page + "&_size=200&_sort=file_type:asc,file_name:asc";
            var result = await SendAsync(HttpMethod.Get, url, null, string.Empty, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "文件列表读取失败");
            var data = Property(result.Json, "data");
            var list = Property(data, "list");
            if (list.ValueKind != JsonValueKind.Array) break;
            var folders = new List<(string Id, string Name)>();
            foreach (var item in list.EnumerateArray())
            {
                var id = String(item, "fid");
                var name = String(item, "file_name");
                if (!CloudShareLink.IsIdentifier(id)) continue;
                if (Bool(item, "dir")) folders.Add((id, name));
                else if (CloudSubtitleFiles.IsSubtitle(name))
                {
                    if (subtitles.Count < 1000)
                        subtitles.Add(new CloudFile(Kind, link.ShareId, id, String(item, "share_fid_token"), name, Long(item, "size"), parentName, link.Passcode, ParentDirectoryId: folderId));
                }
                else if (IsMedia(item, name) && files.Count < 500)
                    files.Add(new CloudFile(Kind, link.ShareId, id, String(item, "share_fid_token"), name, Long(item, "size"), parentName, link.Passcode, ParentDirectoryId: folderId));
            }
            foreach (var folder in folders) await ListFilesAsync(link, folder.Id, token, folder.Name, files, subtitles, depth + 1, cancellationToken).ConfigureAwait(false);
            var metadata = Property(data, "metadata");
            var total = Long(metadata, "_total");
            if (total <= 0) total = Long(metadata, "total");
            if (list.GetArrayLength() < 200 || total > 0 && page * 200 >= total) break;
            page++;
        }
    }

    public Task<CloudPlayResult> PlayAsync(CloudFile file, string quality, CancellationToken cancellationToken = default)
    {
        var height = quality.Contains("原画", StringComparison.OrdinalIgnoreCase) ? -1 :
            quality.EndsWith('p') && int.TryParse(quality.AsSpan(0, quality.Length - 1), out var resolution) ? resolution : 0;
        return PlayAsync(file, new QualityOption(quality, height), includeOriginalAlternatives: false, allowTranscodeFallback: false, cancellationToken);
    }

    public Task<CloudPlayResult> PlayAsync(CloudFile file, QualityOption quality, CancellationToken cancellationToken = default) =>
        PlayAsync(file, quality, includeOriginalAlternatives: true, allowTranscodeFallback: true, cancellationToken);

    private async Task<CloudPlayResult> PlayAsync(
        CloudFile file,
        QualityOption quality,
        bool includeOriginalAlternatives,
        bool allowTranscodeFallback,
        CancellationToken cancellationToken)
    {
        var cookie = await ReadSessionSeedAsync(cancellationToken).ConfigureAwait(false);
        var driveName = CloudAccountInfo.DisplayName(Kind);
        if (string.IsNullOrWhiteSpace(cookie)) throw new CloudAuthenticationException(Kind, $"{driveName}转码与原画需要扫码登录；公开分享浏览无需登录。");
        var savedId = file.ShareId == "drive"
            ? file.FileId
            : await SaveFileAsync(file, cookie, forceRefresh: false, cancellationToken).ConfigureAwait(false);

        if (quality.RequestsOriginal)
            return await PlayOriginalAsync(file, savedId, cookie, includeOriginalAlternatives, allowTranscodeFallback, cancellationToken).ConfigureAwait(false);

        try
        {
            return await PlayTranscodedAsync(file, savedId, quality, cookie, cancellationToken).ConfigureAwait(false);
        }
        catch (UcTranscodeUnavailableException) when (allowTranscodeFallback && !quality.IsSpecific)
        {
            var media = await PlayOriginalAsync(file, savedId, cookie, includeOriginalAlternatives: false,
                allowTranscodeFallback: false, cancellationToken).ConfigureAwait(false);
            return media with
            {
                ResolvedQuality = new QualityOption("原画", -1) { IsOriginal = true },
                Notice = "UC 网盘暂无可用转码，已使用原画播放。"
            };
        }
    }

    private async Task<CloudPlayResult> PlayOriginalAsync(
        CloudFile file,
        string savedId,
        string cookie,
        bool includeOriginalAlternatives,
        bool allowTranscodeFallback,
        CancellationToken cancellationToken)
    {
        var driveName = CloudAccountInfo.DisplayName(Kind);
        string? rejectedUrl = null;
        try
        {
            DownloadLinkResult download;
            try
            {
                download = await GetDownloadUrlAsync(savedId, cookie, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (file.ShareId != "drive" && IsMissingFileError(ex.Message))
            {
                savedId = await SaveFileAsync(file, cookie, forceRefresh: true, cancellationToken).ConfigureAwait(false);
                download = await GetDownloadUrlAsync(savedId, cookie, cancellationToken).ConfigureAwait(false);
            }

            if (download.Url.Length == 0)
            {
                if (Kind == CloudProviderKind.UC && !CloudSubtitleFiles.IsSubtitle(file.Name))
                    throw new UcPlaybackRestrictionException(UcPlaybackRestriction.OriginalMessage);
                throw new InvalidDataException($"{driveName}未返回播放地址，请更换清晰度或重试。");
            }

            var downloadHeaders = MediaHeaders(download.Url, download.RequestCookie);
            if (Kind == CloudProviderKind.UC && !CloudSubtitleFiles.IsSubtitle(file.Name))
            {
                var probe = await _http.ProbeMediaAsync(download.Url, downloadHeaders, cancellationToken).ConfigureAwait(false);
                if (UcPlaybackRestriction.IsRestricted(probe, file.Name, file.Size))
                {
                    rejectedUrl = download.Url;
                    throw new UcPlaybackRestrictionException(UcPlaybackRestriction.OriginalMessage);
                }
            }

            var alternatives = includeOriginalAlternatives
                ? await ReadOptionalTranscodesAsync(savedId, cookie, cancellationToken).ConfigureAwait(false)
                : [];
            var contentType = Services.Playback.HlsManifest.RecognizeResponse("https://media.invalid/" + Uri.EscapeDataString(file.Name), null);
            return new CloudPlayResult(download.Url, downloadHeaders, contentType, CloudSubtitleFiles.Extension(file.Name))
            {
                AvailableQualities = includeOriginalAlternatives ? TranscodingOptions(alternatives) : null
            };
        }
        catch (UcPlaybackRestrictionException) when (allowTranscodeFallback)
        {
            try
            {
                var fallback = await PlayTranscodedAsync(file, savedId, new QualityOption("自动", 0), cookie, cancellationToken).ConfigureAwait(false);
                if (fallback.Url != rejectedUrl)
                    return fallback with { Notice = $"UC 网盘原画受限，已切换至 {fallback.ResolvedQuality?.Name ?? "可用转码"} 播放。" };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not CloudAuthenticationException &&
                ex is InvalidDataException or InvalidOperationException or HttpRequestException or NotSupportedException or OperationCanceledException)
            {
                Diagnostics.Log($"UC 原画受限后回退转码失败: {ex.GetType().Name}");
            }
            throw new UcPlaybackRestrictionException(UcPlaybackRestriction.PlaybackMessage);
        }
    }

    private async Task<CloudPlayResult> PlayTranscodedAsync(
        CloudFile file,
        string savedId,
        QualityOption quality,
        string cookie,
        CancellationToken cancellationToken)
    {
        TranscodeFetchResult fetched;
        try
        {
            fetched = await ReadTranscodesAsync(savedId, cookie, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (file.ShareId != "drive" && IsMissingFileError(ex.Message))
        {
            savedId = await SaveFileAsync(file, cookie, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            fetched = await ReadTranscodesAsync(savedId, cookie, cancellationToken).ConfigureAwait(false);
        }

        var variants = fetched.Variants;
        if (variants.Length == 0)
        {
            if (Kind == CloudProviderKind.UC) throw new UcTranscodeUnavailableException();
            throw new InvalidDataException($"{CloudAccountInfo.DisplayName(Kind)}未返回播放地址，请更换清晰度或重试。");
        }
        var candidates = variants.Where(item => item.Format.Matches(quality)).OrderByDescending(item => item.Format.Height)
            .ThenByDescending(item => item.Format.FrameRate);
        var options = TranscodingOptions(variants);
        var restricted = false;
        var unavailable = false;
        foreach (var transcoded in candidates.DistinctBy(item => item.Url))
        {
            var headers = MediaHeaders(transcoded.Url, fetched.RequestCookie);
            string? probedContentType = null;
            if (Kind == CloudProviderKind.UC)
            {
                var probe = await _http.ProbeMediaAsync(transcoded.Url, headers, cancellationToken).ConfigureAwait(false);
                if (UcPlaybackRestriction.IsRestricted(probe, file.Name))
                {
                    restricted = true;
                    continue;
                }
                if (probe.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    unavailable = true;
                    continue;
                }
                probedContentType = probe.ContentType;
            }
            var contentType = Services.Playback.HlsManifest.RecognizeResponse(transcoded.Url, probedContentType);
            return new CloudPlayResult(transcoded.Url, headers, contentType, CloudSubtitleFiles.Extension(file.Name))
            {
                AvailableVideoHeights = variants.Where(item => item.Format.Height > 0).Select(item => item.Format.Height).ToHashSet(),
                AvailableQualities = options,
                ResolvedQuality = options.FirstOrDefault(option => !option.RequestsOriginal && transcoded.Format.Matches(option))
            };
        }
        if (restricted) throw new UcPlaybackRestrictionException(UcPlaybackRestriction.PlaybackMessage);
        if (unavailable) throw new UcTranscodeUnavailableException();
        throw new NotSupportedException($"网盘未提供 {quality.Name}。");
    }

    private async Task<string> SaveFileAsync(CloudFile file, string cookie, bool forceRefresh, CancellationToken cancellationToken)
    {
        var account = AccountKey(cookie);
        var key = $"{account}/{file.ShareId}/{file.FileId}";
        var settingKey = $"cloud.saved.{Kind}.{key}";
        if (!forceRefresh)
        {
            if (_savedIds.TryGetValue(key, out var cached)) return cached;
            var stored = await _database.GetSettingAsync(settingKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stored))
            {
                // 会话 Cookie 会轮换，旧版本用整串 Cookie 做账号标识，这里顺手把历史转存记录迁移过来。
                var legacy = await _database.GetSettingAsync($"cloud.saved.{Kind}.{LegacyAccountKey(cookie)}/{file.ShareId}/{file.FileId}", cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    stored = legacy;
                    await _database.SetSettingAsync(settingKey, legacy, cancellationToken).ConfigureAwait(false);
                }
            }
            if (!string.IsNullOrWhiteSpace(stored)) return _savedIds[key] = stored;
        }
        else
        {
            _savedIds.TryRemove(key, out _);
            await _database.SetSettingAsync(settingKey, string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _savedIds.TryGetValue(key, out var cached)) return cached;
            var token = await GetShareTokenAsync(file.ShareId, file.Passcode, cancellationToken).ConfigureAwait(false);
            var dir = await EnsureSaveDirAsync(account, cookie, cancellationToken).ConfigureAwait(false);
            var body = new
            {
                fid_list = new[] { file.FileId },
                fid_token_list = new[] { file.FileToken },
                to_pdir_fid = dir,
                pwd_id = file.ShareId,
                stoken = token,
                pdir_fid = "0",
                scene = "link"
            };
            var saved = await SendAsync(HttpMethod.Post, _api + "share/sharepage/save?" + ClientQuery, body, cookie, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(saved, "转存失败");
            var taskId = NestedString(saved.Json, "data", "task_id");
            for (var attempt = 0; attempt < 20 && taskId.Length > 0; attempt++)
            {
                var task = await SendAsync(HttpMethod.Get, _api + "task?" + ClientQuery + "&task_id=" + Uri.EscapeDataString(taskId) + "&retry_index=" + attempt, null, cookie, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(task, "转存状态读取失败");
                var saveAs = Property(Property(task.Json, "data"), "save_as");
                var ids = Property(saveAs, "save_as_top_fids");
                if (ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0)
                {
                    var id = ids[0].GetString() ?? string.Empty;
                    if (id.Length > 0)
                    {
                        _savedIds[key] = id;
                        await _database.SetSettingAsync(settingKey, id, cancellationToken).ConfigureAwait(false);
                        return id;
                    }
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("网盘转存超时，请稍后重试。");
        }
        finally { _saveLock.Release(); }
    }

    private async Task<string> EnsureSaveDirAsync(string account, string cookie, CancellationToken cancellationToken)
    {
        if (_saveDirIds.TryGetValue(account, out var savedDirectory)) return savedDirectory;
        var root = await SendAsync(HttpMethod.Get, _api + "file/sort?" + ClientQuery + "&pdir_fid=0&_page=1&_size=200&_sort=file_type:asc,updated_at:desc", null, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(root, "网盘目录读取失败");
        var list = Property(Property(root.Json, "data"), "list");
        if (list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (String(item, "file_name") == "流光影院" && Bool(item, "dir") && String(item, "fid") is { Length: > 0 } folder)
                    return _saveDirIds[account] = folder;
        var created = await SendAsync(HttpMethod.Post, _api + "file?" + ClientQuery, new { pdir_fid = "0", file_name = "流光影院", dir_path = "", dir_init_lock = false }, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(created, "创建播放目录失败");
        return _saveDirIds[account] = NestedString(created.Json, "data", "fid") is { Length: > 0 } id ? id : throw new InvalidDataException("网盘未返回播放目录。");
    }

    private async Task<TranscodedVideo[]> ReadOptionalTranscodesAsync(string fileId, string cookie, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1600));
        try { return (await ReadTranscodesAsync(fileId, cookie, budget.Token).ConfigureAwait(false)).Variants; }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            return [];
        }
    }

    private async Task<TranscodeFetchResult> ReadTranscodesAsync(string fileId, string cookie, CancellationToken cancellationToken)
    {
        const string resolutions = "normal,low,high,super,2k,4k";
        var projectUrl = Kind == CloudProviderKind.Quark
            ? "https://drive.quark.cn/1/clouddrive/file/v2/play/project?pr=ucpro&fr=pc"
            : _api + "file/v2/play/project?pr=UCBrowser&fr=pc";
        var (result, requestCookie) = await SendAuthenticatedAsync(
            HttpMethod.Post,
            projectUrl,
            new { fid = fileId, resolutions, supports = "fmp4_av,m3u8,dolby_vision" },
            cookie,
            cancellationToken).ConfigureAwait(false);

        var parsed = ParseTranscodes(result.Json);
        if (result.StatusCode == HttpStatusCode.Unauthorized)
            EnsureSuccess(result, "转码播放失败");
        if (Kind == CloudProviderKind.UC && HasUcRestriction(result))
            throw new UcPlaybackRestrictionException(UcPlaybackRestriction.PlaybackMessage);
        if ((int)result.StatusCode is 400 or 404 or 405 or >= 500 ||
            String(result.Json, "message").Contains("plf_invalid", StringComparison.OrdinalIgnoreCase) ||
            parsed.Length == 0)
        {
            (result, requestCookie) = await SendAuthenticatedAsync(
                HttpMethod.Post,
                _api + "file/v2/play?" + ClientQuery,
                new { fid = fileId, resolutions, supports = "fmp4" },
                cookie,
                cancellationToken).ConfigureAwait(false);
            if (Kind == CloudProviderKind.UC && HasUcRestriction(result))
                throw new UcPlaybackRestrictionException(UcPlaybackRestriction.PlaybackMessage);
            EnsureSuccess(result, "转码播放失败");
            parsed = ParseTranscodes(result.Json);
        }
        else
        {
            EnsureSuccess(result, "转码播放失败");
        }

        return new TranscodeFetchResult(parsed, requestCookie);
    }

    private static TranscodedVideo[] ParseTranscodes(JsonElement root)
    {
        var list = Property(Property(root, "data"), "video_list");
        if (list.ValueKind != JsonValueKind.Array) return [];
        var candidates = new List<TranscodedVideo>();
        foreach (var video in list.EnumerateArray())
        {
            var info = Property(video, "video_info");
            var url = String(info, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || !Services.Http.MediaRequestHeaders.IsHttpAddress(address)) continue;
            var height = Long(info, "height");
            if (height == 0) height = Long(video, "height");
            if (height == 0) height = String(video, "resolution") switch
            {
                "4k" => 2160, "2k" => 1440, "super" => 1080, "high" => 720, "normal" => 480,
                "low" => 360, "420p" => 420, _ => 0
            };
            var frameRate = VideoFormat.ReadFrameRate(String(info, "frame_rate"));
            if (frameRate == 0) frameRate = VideoFormat.ReadFrameRate(String(info, "fps"));
            if (frameRate == 0) frameRate = VideoFormat.ReadFrameRate(String(video, "frame_rate"));
            if (frameRate == 0) frameRate = VideoFormat.ReadFrameRate(String(video, "fps"));
            candidates.Add(new TranscodedVideo(address.AbsoluteUri, new VideoFormat(height is > 0 and <= int.MaxValue ? (int)height : 0, frameRate)));
        }
        return [.. candidates];
    }

    private static IReadOnlyList<QualityOption> TranscodingOptions(TranscodedVideo[] variants) =>
        [new QualityOption("原画", -1) { IsOriginal = true }, .. VideoFormat.ToOptions(variants.Select(item => item.Format), markHighestOriginal: false)];

    private sealed record TranscodedVideo(string Url, VideoFormat Format);
    private sealed record TranscodeFetchResult(TranscodedVideo[] Variants, string RequestCookie);
    private sealed record DownloadLinkResult(string Url, string RequestCookie);
    private sealed class UcTranscodeUnavailableException() : InvalidOperationException("UC 网盘暂无可用转码，请尝试原画或其他线路。");

    private async Task<DownloadLinkResult> GetDownloadUrlAsync(string fileId, string cookie, CancellationToken cancellationToken)
    {
        var downloadApi = Kind == CloudProviderKind.Quark ? "https://drive.quark.cn/1/clouddrive/" : _api;
        var query = ClientQuery;
        var body = new { fids = new[] { fileId } };
        var (result, requestCookie) = await SendAuthenticatedAsync(HttpMethod.Post, downloadApi + "file/download?" + query, body, cookie, cancellationToken).ConfigureAwait(false);
        if (Kind == CloudProviderKind.Quark && (int)result.StatusCode is 404 or 405 or >= 500)
        {
            (result, requestCookie) = await SendAuthenticatedAsync(HttpMethod.Post, _api + "file/download?" + query, body, cookie, cancellationToken).ConfigureAwait(false);
        }
        if (Kind == CloudProviderKind.Quark && (int)result.StatusCode is 400 or >= 500)
        {
            (result, requestCookie) = await SendAuthenticatedAsync(HttpMethod.Post, _api + "file/download?" + query + "&uc_param_str=",
                new { fids = fileId }, cookie, cancellationToken).ConfigureAwait(false);
        }
        if (Kind == CloudProviderKind.UC && HasUcRestriction(result))
            throw new UcPlaybackRestrictionException(UcPlaybackRestriction.OriginalMessage);
        EnsureSuccess(result, "原画地址获取失败");
        var data = Property(result.Json, "data");
        if (data.ValueKind == JsonValueKind.Array)
        {
            if (data.GetArrayLength() == 0) return new DownloadLinkResult(string.Empty, requestCookie);
            data = data[0];
        }
        var url = data.ValueKind == JsonValueKind.String ? data.GetString() : String(data, "download_url");
        var resolved = Uri.TryCreate(url, UriKind.Absolute, out var media) && Services.Http.MediaRequestHeaders.IsHttpAddress(media)
            ? media.AbsoluteUri : string.Empty;
        return new DownloadLinkResult(resolved, requestCookie);
    }

    private static bool HasUcRestriction(CloudHttpResult result) =>
        result.StatusCode != HttpStatusCode.Unauthorized &&
        (result.StatusCode == HttpStatusCode.Forbidden ||
         UcPlaybackRestriction.HasRestrictionMarker(String(result.Json, "code")) ||
         UcPlaybackRestriction.HasRestrictionMarker(String(result.Json, "message")) ||
         UcPlaybackRestriction.HasRestrictionMarker(String(result.Json, "msg")));

    private Dictionary<string, string> MediaHeaders(string url, string cookie) =>
        HeadersForCookie(CloudMediaCredentials.CanSendCookie(Kind, new Uri(url)) ? cookie : string.Empty);

    private Dictionary<string, string> HeadersForCookie(string cookieHeader)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = Kind == CloudProviderKind.UC ? CloudHttpClient.DefaultUserAgent :
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) quark-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch",
            ["Referer"] = _home,
            ["Origin"] = _home.TrimEnd('/')
        };
        if (!string.IsNullOrWhiteSpace(cookieHeader)) headers["Cookie"] = cookieHeader;
        return headers;
    }

    private string CredentialKey => Kind == CloudProviderKind.Quark ? "cloud.quarkCookie" : "cloud.ucCookie";

    public async Task<bool> VerifyCookieAsync(CancellationToken cancellationToken = default)
    {
        var cookie = await ReadSessionSeedAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cookie)) return false;
        var result = await SendAsync(HttpMethod.Get, _api + "file/sort?" + ClientQuery + "&pdir_fid=0&_page=1&_size=1",
            null, cookie, cancellationToken).ConfigureAwait(false);
        return result.StatusCode == HttpStatusCode.OK && Property(Property(result.Json, "data"), "list").ValueKind == JsonValueKind.Array;
    }

    public async Task<CloudDriveListing> ListFolderAsync(string folderId, string path, int page, CancellationToken cancellationToken = default)
    {
        var cookie = await ReadSessionSeedAsync(cancellationToken).ConfigureAwait(false);
        if (cookie.Length == 0) throw new CloudAuthenticationException(Kind, $"{CloudAccountInfo.DisplayName(Kind)}需要扫码登录。");
        page = Math.Max(1, page);
        var result = await SendAsync(HttpMethod.Get, _api + "file/sort?" + ClientQuery + "&pdir_fid=" + Uri.EscapeDataString(folderId) +
            "&_page=" + page + "&_size=100&_sort=file_type:asc,file_name:asc", null, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "个人云盘目录读取失败");
        return ReadDriveListing(result, folderId, path, page);
    }

    public async Task<CloudDriveListing> SearchFolderAsync(string keyword, int page, CancellationToken cancellationToken = default)
    {
        var cookie = await ReadSessionSeedAsync(cancellationToken).ConfigureAwait(false);
        if (cookie.Length == 0) throw new CloudAuthenticationException(Kind, $"{CloudAccountInfo.DisplayName(Kind)}需要扫码登录。");
        if (string.IsNullOrWhiteSpace(keyword)) return new CloudDriveListing([], false);
        page = Math.Max(1, page);
        var result = await SendAsync(HttpMethod.Get, _api + "file/search?" + ClientQuery + "&q=" + Uri.EscapeDataString(keyword.Trim()) +
            "&_page=" + page + "&_size=100&_sort=file_type:asc,updated_at:desc", null, cookie, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "个人云盘搜索失败");
        return ReadDriveListing(result, "0", "/", page);
    }

    private CloudDriveListing ReadDriveListing(CloudHttpResult result, string parentId, string path, int page)
    {
        var data = Property(result.Json, "data");
        var list = Property(data, "list");
        if (list.ValueKind != JsonValueKind.Array) return new CloudDriveListing([], false);
        var items = new List<CloudDriveItem>();
        foreach (var item in list.EnumerateArray())
        {
            var id = String(item, "fid");
            var name = String(item, "file_name");
            if (id.Length == 0 || name.Length == 0) continue;
            var parent = String(item, "pdir_fid") is { Length: > 0 } value ? value : parentId;
            var itemPath = String(item, "file_path") is { Length: > 0 } savedPath ? savedPath : JoinPath(path, name);
            var updated = Long(item, "updated_at");
            DateTimeOffset? modified = updated > 0 ? updated > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(updated) : DateTimeOffset.FromUnixTimeSeconds(updated) : null;
            items.Add(new CloudDriveItem(Kind, id, parent, name, itemPath, Long(item, "size"), Bool(item, "dir"), modified));
        }
        var metadata = Property(data, "metadata");
        var total = Long(metadata, "_total") > 0 ? Long(metadata, "_total") : Long(metadata, "total");
        return new CloudDriveListing(items, total > page * 100 || total == 0 && list.GetArrayLength() == 100);
    }

    private static string JoinPath(string parent, string name) => (parent.Length == 0 ? "/" : parent).TrimEnd('/') + "/" + name;

    private static string AccountKey(string cookie) => StableToken(cookie) is { Length: > 0 } stable ? Hash(stable) : LegacyAccountKey(cookie);

    private static string LegacyAccountKey(string cookie) => Hash(cookie);

    /// <summary>会话 Cookie 每次调用都会轮换，只有 __uid 这类长期值能稳定标识账号。</summary>
    private static string StableToken(string cookie)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith("__uid=", StringComparison.Ordinal) && part.Length > 6) return part[6..];
        return string.Empty;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    private async Task<string> ReadSessionSeedAsync(CancellationToken cancellationToken)
    {
        await _jarLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seed is null) ResetCookieSession(await _credential(cancellationToken).ConfigureAwait(false));
            return _seed!;
        }
        finally { _jarLock.Release(); }
    }

    private void ResetCookieSession(string cookie)
    {
        _jar.Clear();
        _jar.MergeHeader(cookie);
        _seed = _persisted = cookie;
    }

    private async Task<CloudHttpResult> SendAsync(HttpMethod method, string url, object? json, string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return await _http.SendAsync(method, url, json, HeadersForCookie(string.Empty), cancellationToken: cancellationToken).ConfigureAwait(false);
        return (await SendAuthenticatedAsync(method, url, json, cookie, cancellationToken).ConfigureAwait(false)).Result;
    }

    private async Task<(CloudHttpResult Result, string RequestCookie)> SendAuthenticatedAsync(
        HttpMethod method, string url, object? json, string seed, CancellationToken cancellationToken)
    {
        // Each authenticated response can rotate cookies. Apply it before the next request or account change.
        await _jarLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_seed, seed, StringComparison.Ordinal))
                throw new InvalidOperationException("网盘账号已变更，请重新发起操作。");
            var requestCookie = _jar.ToHeader();
            var result = await _http.SendAsync(method, url, json, HeadersForCookie(requestCookie), cancellationToken: cancellationToken).ConfigureAwait(false);
            if ((int)result.StatusCode < 400 && result.Cookies.Count > 0) _jar.Absorb(result.Cookies);
            var merged = _jar.ToHeader();
            if (merged.Length > 0 && !string.Equals(merged, _persisted, StringComparison.Ordinal))
            {
                try
                {
                    await _database.SetSettingAsync(CredentialKey, SecretStore.Protect(merged), cancellationToken).ConfigureAwait(false);
                    _persisted = merged;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Diagnostics.Log($"{Kind} 会话 Cookie 保存失败：{ex.GetType().Name}");
                }
            }
            return (result, requestCookie);
        }
        finally { _jarLock.Release(); }
    }

    private static bool IsMissingFileError(string message) =>
        message.Contains("404", StringComparison.Ordinal) ||
        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("不存在", StringComparison.Ordinal) ||
        message.Contains("file_not_found", StringComparison.OrdinalIgnoreCase);

    private void EnsureSuccess(CloudHttpResult result, string fallback)
    {
        if (result.StatusCode == HttpStatusCode.Unauthorized)
            throw new CloudAuthenticationException(Kind, $"{CloudAccountInfo.DisplayName(Kind)}登录已失效，请重新扫码。");
        var code = String(result.Json, "code");
        var message = First(String(result.Json, "message"), String(result.Json, "msg"), code);
        if ((int)result.StatusCode >= 400) throw new InvalidOperationException($"{fallback}（HTTP {(int)result.StatusCode}）{message}");
        if ((int.TryParse(code, out var number) && number != 0) || (result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False))
            throw new InvalidOperationException($"{fallback}：{message}");
    }

    private static bool IsMedia(JsonElement item, string name) =>
        String(item, "obj_category") is "video" or "audio" ||
        Path.GetExtension(name).ToLowerInvariant() is
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".flv" or ".ts" or ".m2ts" or
            ".webm" or ".rmvb" or ".wmv" or ".mpg" or ".mpeg" or ".m4v" or ".3gp" or
            ".mp3" or ".m4a" or ".flac" or ".wav" or ".aac";

    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : Property(element, name).ValueKind is JsonValueKind.Number ? Property(element, name).GetRawText() : string.Empty;
    private static string NestedString(JsonElement element, params string[] path)
    {
        var value = path.Aggregate(element, Property);
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? string.Empty, JsonValueKind.Number => value.GetRawText(), _ => string.Empty };
    }
    private static long Long(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }
    private static bool Bool(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
