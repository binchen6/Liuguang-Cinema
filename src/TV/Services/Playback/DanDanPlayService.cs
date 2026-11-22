using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Vod;

namespace TV.Services.Playback;

internal static class DanDanPlayBuildConfig
{
    internal static readonly string AppId = Read("DanDanPlayAppId");
    internal static readonly string AppSecret1 = Read("DanDanPlayAppSecret1");
    internal static readonly string AppSecret2 = Read("DanDanPlayAppSecret2");

    private static string Read(string key)
    {
        foreach (var attr in typeof(DanDanPlayBuildConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attr.Value))
            {
                var raw = attr.Value.Trim();
                if (raw.StartsWith("b64:", StringComparison.Ordinal))
                {
                    try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw[4..])); }
                    catch { return string.Empty; }
                }
                return raw;
            }
        }
        return string.Empty;
    }
}

public sealed record DanDanPlayEpisode(long EpisodeId, string AnimeTitle, string EpisodeTitle, string TypeDescription = "")
{
    public string DisplayName => string.IsNullOrWhiteSpace(EpisodeTitle)
        ? AnimeTitle
        : $"{AnimeTitle} · {EpisodeTitle}";
}

public sealed record DanDanPlayAnimeGroup(long AnimeId, string AnimeTitle, string TypeDescription, IReadOnlyList<DanDanPlayEpisode> Episodes);

public sealed class DanDanPlayService
{
    public const string OpenPlatformUrl = "https://doc.dandanplay.com/open/";
    public const string ApiBaseUrl = "https://api.dandanplay.net";
    public const string SettingAppId = "danmaku.dandanplay.appId";
    public const string SettingAppSecret = "danmaku.dandanplay.appSecret";
    private const int HashPrefixBytes = 16 * 1024 * 1024; // 16 MB per DanDanPlay /api/v2/match spec

    private readonly SourceHttpClient _http;
    private readonly DatabaseService _database;
    private readonly ConcurrentDictionary<string, (DanDanPlayEpisode Episode, DateTimeOffset Expires)> _matchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, (DanmakuEntry[] Comments, DateTimeOffset Expires)> _commentCache = new();
    private volatile bool _useBackupSecret;

    public DanDanPlayService(SourceHttpClient http, DatabaseService database)
    {
        _http = http;
        _database = database;
    }

    public async Task<bool> HasCredentialsAsync(CancellationToken token = default)
    {
        var creds = await ResolveCredentialsAsync(token).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(creds.AppId) && creds.Secrets.Count > 0;
    }

    public async Task<bool> HasCustomCredentialsAsync(CancellationToken token = default)
    {
        var customId = (await _database.GetSettingAsync(SettingAppId, token).ConfigureAwait(false))?.Trim();
        var customSecret = SecretStore.Unprotect(await _database.GetSettingAsync(SettingAppSecret, token).ConfigureAwait(false));
        return !string.IsNullOrWhiteSpace(customId) && !string.IsNullOrWhiteSpace(customSecret);
    }

    public bool HasBuiltInCredentials =>
        !string.IsNullOrWhiteSpace(DanDanPlayBuildConfig.AppId) &&
        (!string.IsNullOrWhiteSpace(DanDanPlayBuildConfig.AppSecret1) || !string.IsNullOrWhiteSpace(DanDanPlayBuildConfig.AppSecret2));

    public async Task SaveCustomCredentialsAsync(string appId, string appSecret, CancellationToken token = default)
    {
        await _database.SetSettingAsync(SettingAppId, appId.Trim(), token).ConfigureAwait(false);
        await _database.SetSettingAsync(SettingAppSecret, SecretStore.Protect(appSecret), token).ConfigureAwait(false);
        _useBackupSecret = false;
    }

    public async Task ClearCustomCredentialsAsync(CancellationToken token = default)
    {
        await _database.SetSettingAsync(SettingAppId, string.Empty, token).ConfigureAwait(false);
        await _database.SetSettingAsync(SettingAppSecret, string.Empty, token).ConfigureAwait(false);
        _useBackupSecret = false;
    }

    /// <summary>
    /// Computes X-Signature = Base64(SHA256(AppId + Timestamp + Path + AppSecret)) per DanDanPlay Open API v2 spec.
    /// </summary>
    public static string ComputeSignature(string appId, string timestamp, string apiPath, string appSecret)
    {
        var raw = appId + timestamp + apiPath + appSecret;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToBase64String(hash);
    }

    public async Task<DanDanPlayEpisode> MatchAsync(
        string title,
        string episode,
        int episodeIndex,
        string? localFilePath = null,
        int durationSeconds = 0,
        CancellationToken token = default)
    {
        var cleanTitle = MediaMatching.Title(title);
        var epNumber = MediaMatching.EpisodeNumber(episode) ?? MediaMatching.EpisodeNumber(title);
        var cacheKey = !string.IsNullOrWhiteSpace(localFilePath)
            ? "file:" + localFilePath
            : $"{cleanTitle}|{epNumber?.ToString(CultureInfo.InvariantCulture) ?? episode}|{episodeIndex}";

        if (_matchCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Episode;

        DanDanPlayEpisode? fileCandidate = null;

        // Strategy A: Exact local file 16 MB MD5 hash + filename matching via POST /api/v2/match
        if (!string.IsNullOrWhiteSpace(localFilePath) && File.Exists(localFilePath))
        {
            try
            {
                var (isMatched, match) = await MatchFileAsync(localFilePath, durationSeconds, token).ConfigureAwait(false);
                if (isMatched && match is not null)
                {
                    CacheMatch(cacheKey, match);
                    return match;
                }
                fileCandidate = match;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Diagnostics.Log($"DanDanPlay file hash match skipped: {ex.GetType().Name}");
            }
        }

        // Strategy B: Search episodes via GET /api/v2/search/episodes
        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            var groups = await SearchEpisodesAsync(cleanTitle, epNumber, token).ConfigureAwait(false);
            if (groups.Count == 0 && epNumber.HasValue)
                groups = await SearchEpisodesAsync(cleanTitle, null, token).ConfigureAwait(false);

            var picked = PickBestEpisode(groups, cleanTitle, episode, epNumber, episodeIndex);
            if (picked is not null)
            {
                CacheMatch(cacheKey, picked);
                return picked;
            }
        }

        // Strategy C: Filename-only fallback via POST /api/v2/match
        var syntheticName = !string.IsNullOrWhiteSpace(cleanTitle)
            ? (epNumber is > 0 ? $"{cleanTitle} 第{epNumber:D2}集.mp4" : $"{cleanTitle}.mp4")
            : Path.GetFileName(localFilePath ?? title);
        if (!string.IsNullOrWhiteSpace(syntheticName))
        {
            var (_, nameMatch) = await MatchFileNameOnlyAsync(syntheticName, durationSeconds, token).ConfigureAwait(false);
            if (nameMatch is not null)
            {
                CacheMatch(cacheKey, nameMatch);
                return nameMatch;
            }
        }

        if (fileCandidate is not null)
        {
            CacheMatch(cacheKey, fileCandidate);
            return fileCandidate;
        }

        throw new InvalidDataException($"弹弹play 弹幕库未找到与“{(string.IsNullOrWhiteSpace(cleanTitle) ? title : cleanTitle)}”匹配的剧集。");
    }

    public async Task<IReadOnlyList<DanDanPlayAnimeGroup>> SearchEpisodesAsync(string keyword, int? episodeNumber = null, CancellationToken token = default)
    {
        var query = keyword.Trim();
        if (query.Length == 0) return [];
        var path = "/api/v2/search/episodes";
        var url = $"{ApiBaseUrl}{path}?anime={Uri.EscapeDataString(query)}";
        if (episodeNumber is > 0)
            url += $"&episode={episodeNumber.Value.ToString(CultureInfo.InvariantCulture)}";

        var json = await SendSignedAsync(HttpMethod.Get, path, url, bodyJson: null, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureSuccess(root, "弹弹play 剧集搜索失败");

        var groups = new List<DanDanPlayAnimeGroup>();
        if (!root.TryGetProperty("animes", out var animes) || animes.ValueKind != JsonValueKind.Array)
            return groups;

        foreach (var anime in animes.EnumerateArray().Take(30))
        {
            var animeId = ReadInt64(anime, "animeId");
            var animeTitle = ReadString(anime, "animeTitle");
            var typeDesc = ReadString(anime, "typeDescription");
            var episodes = new List<DanDanPlayEpisode>();
            if (anime.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray().Take(500))
                {
                    var epId = ReadInt64(ep, "episodeId");
                    var epTitle = ReadString(ep, "episodeTitle");
                    if (epId > 0)
                        episodes.Add(new DanDanPlayEpisode(epId, animeTitle, epTitle, typeDesc));
                }
            }
            if (episodes.Count > 0)
                groups.Add(new DanDanPlayAnimeGroup(animeId, animeTitle, typeDesc, episodes));
        }

        return groups;
    }

    public async Task<DanmakuEntry[]> GetCommentsAsync(long episodeId, bool withRelated = true, int chConvert = 1, CancellationToken token = default)
    {
        if (episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(episodeId));
        if (_commentCache.TryGetValue(episodeId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Comments;

        var path = $"/api/v2/comment/{episodeId.ToString(CultureInfo.InvariantCulture)}";
        var url = $"{ApiBaseUrl}{path}?withRelated={(withRelated ? "true" : "false")}&chConvert={Math.Clamp(chConvert, 0, 2)}";
        var json = await SendSignedAsync(HttpMethod.Get, path, url, bodyJson: null, token).ConfigureAwait(false);
        var comments = DanmakuService.ParseContent(json);

        if (_commentCache.Count >= 32)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _commentCache.Where(p => p.Value.Expires < now))
                _commentCache.TryRemove(pair.Key, out _);
            if (_commentCache.Count >= 32)
                foreach (var pair in _commentCache.OrderBy(p => p.Value.Expires).Take(_commentCache.Count - 31))
                    _commentCache.TryRemove(pair.Key, out _);
        }
        _commentCache[episodeId] = (comments, DateTimeOffset.UtcNow.AddHours(2));
        return comments;
    }

    public async Task<(bool Ok, string Message)> VerifyCredentialsAsync(string? appIdOverride = null, string? appSecretOverride = null, CancellationToken token = default)
    {
        var path = "/api/v2/search/episodes";
        var url = $"{ApiBaseUrl}{path}?anime={Uri.EscapeDataString("你的名字")}";
        try
        {
            string json;
            if (!string.IsNullOrWhiteSpace(appIdOverride) && !string.IsNullOrWhiteSpace(appSecretOverride))
            {
                json = await SendWithSingleCredentialAsync(HttpMethod.Get, path, url, null, appIdOverride.Trim(), appSecretOverride.Trim(), token).ConfigureAwait(false);
            }
            else
            {
                json = await SendSignedAsync(HttpMethod.Get, path, url, null, token).ConfigureAwait(false);
            }
            using var doc = JsonDocument.Parse(json);
            EnsureSuccess(doc.RootElement, "凭证验证失败");
            return (true, "弹弹play 开放弹幕库连接正常");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool IsMatched, DanDanPlayEpisode? Episode)> MatchFileAsync(string localFilePath, int durationSeconds, CancellationToken token)
    {
        var info = new FileInfo(localFilePath);
        if (!info.Exists || info.Length == 0) return (false, null);
        var hash = await ComputeFilePrefixMd5Async(localFilePath, token).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new
        {
            fileName = info.Name,
            fileHash = hash,
            fileSize = info.Length,
            videoDuration = Math.Max(0, durationSeconds),
            matchMode = "hashAndFileName"
        });
        return await PostMatchAsync(payload, token).ConfigureAwait(false);
    }

    private Task<(bool IsMatched, DanDanPlayEpisode? Episode)> MatchFileNameOnlyAsync(string fileName, int durationSeconds, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fileName,
            fileHash = "00000000000000000000000000000000",
            fileSize = 0L,
            videoDuration = Math.Max(0, durationSeconds),
            matchMode = "fileNameOnly"
        });
        return PostMatchAsync(payload, token);
    }

    private async Task<(bool IsMatched, DanDanPlayEpisode? Episode)> PostMatchAsync(string bodyJson, CancellationToken token)
    {
        const string path = "/api/v2/match";
        var json = await SendSignedAsync(HttpMethod.Post, path, ApiBaseUrl + path, bodyJson, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureSuccess(root, "弹弹play 文件匹配失败");

        var isMatched = root.TryGetProperty("isMatched", out var matchedProp) && matchedProp.ValueKind == JsonValueKind.True;
        if (!root.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return (false, null);

        foreach (var item in matches.EnumerateArray())
        {
            var epId = ReadInt64(item, "episodeId");
            if (epId <= 0) continue;
            var animeTitle = ReadString(item, "animeTitle");
            var epTitle = ReadString(item, "episodeTitle");
            var typeDesc = ReadString(item, "typeDescription");
            return (isMatched, new DanDanPlayEpisode(epId, animeTitle, epTitle, typeDesc));
        }

        return (false, null);
    }

    private static DanDanPlayEpisode? PickBestEpisode(
        IReadOnlyList<DanDanPlayAnimeGroup> groups,
        string cleanTitle,
        string rawEpisode,
        int? targetEpisodeNumber,
        int episodeIndex)
    {
        if (groups.Count == 0) return null;
        var normQuery = SearchScoring.Normalize(cleanTitle);

        var rankedGroups = groups
            .Select(group =>
            {
                var normTitle = SearchScoring.Normalize(group.AnimeTitle);
                var score = 0.0;
                if (normTitle.Equals(normQuery, StringComparison.OrdinalIgnoreCase)) score += 1000;
                else if (normTitle.StartsWith(normQuery, StringComparison.OrdinalIgnoreCase)) score += 700;
                else if (normTitle.Contains(normQuery, StringComparison.OrdinalIgnoreCase)) score += 500;
                else if (normQuery.Contains(normTitle, StringComparison.OrdinalIgnoreCase) && normTitle.Length >= 2) score += 350;
                // Prefer closer title lengths to avoid picking spin-offs/OVAs over main series
                score -= Math.Abs(normTitle.Length - normQuery.Length) * 5;
                if (targetEpisodeNumber is > 1 && group.Episodes.Count >= targetEpisodeNumber.Value) score += 120;
                if (targetEpisodeNumber is null && group.Episodes.Count == 1) score += 80;
                return (Group: group, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToArray();

        foreach (var candidate in rankedGroups)
        {
            var eps = candidate.Group.Episodes;
            if (targetEpisodeNumber is > 0)
            {
                var exact = eps.FirstOrDefault(e => MediaMatching.EpisodeNumber(e.EpisodeTitle) == targetEpisodeNumber.Value);
                if (exact is not null) return exact;
                if (targetEpisodeNumber.Value <= eps.Count)
                    return eps[targetEpisodeNumber.Value - 1];
            }
            if (!string.IsNullOrWhiteSpace(rawEpisode))
            {
                var normEp = SearchScoring.Normalize(rawEpisode);
                var titleMatch = eps.FirstOrDefault(e => SearchScoring.Normalize(e.EpisodeTitle).Contains(normEp, StringComparison.OrdinalIgnoreCase));
                if (titleMatch is not null) return titleMatch;
            }
            if (episodeIndex >= 0 && episodeIndex < eps.Count) return eps[episodeIndex];
            if (eps.Count > 0) return eps[0];
        }

        return null;
    }

    private async Task<string> SendSignedAsync(HttpMethod method, string apiPath, string url, string? bodyJson, CancellationToken token)
    {
        var (appId, secrets) = await ResolveCredentialsAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(appId) || secrets.Count == 0)
            throw new InvalidOperationException("尚未配置弹弹play 开放平台 AppId/AppSecret，请在“设置 → 弹幕与 AI”中填入凭证。");

        var startIdx = _useBackupSecret && secrets.Count > 1 ? 1 : 0;
        Exception? lastError = null;
        for (var attempt = 0; attempt < secrets.Count; attempt++)
        {
            var idx = (startIdx + attempt) % secrets.Count;
            try
            {
                var result = await SendWithSingleCredentialAsync(method, apiPath, url, bodyJson, appId, secrets[idx], token).ConfigureAwait(false);
                if (idx == 1) _useBackupSecret = true;
                else if (idx == 0) _useBackupSecret = false;
                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt + 1 < secrets.Count)
            {
                Diagnostics.Log($"DanDanPlay secret #{idx + 1} rejected ({(int?)ex.StatusCode}), failing over to backup secret");
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException("弹弹play API 鉴权失败。");
    }

    private async Task<string> SendWithSingleCredentialAsync(
        HttpMethod method,
        string apiPath,
        string url,
        string? bodyJson,
        string appId,
        string appSecret,
        CancellationToken token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(appId, timestamp, apiPath, appSecret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
            ["User-Agent"] = "LiuguangCinema/1.7 (DanDanPlay-OpenAPI)",
            ["X-AppId"] = appId,
            ["X-Timestamp"] = timestamp,
            ["X-Signature"] = signature
        };

        var exchange = method == HttpMethod.Post
            ? await _http.PostJsonAsync(url, bodyJson ?? "{}", headers, maxBytes: 12 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false)
            : await _http.GetExchangeAsync(url, headers, maxBytes: 12 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);

        if (exchange.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException($"弹弹play API 鉴权失败（HTTP {(int)exchange.StatusCode}），请检查密钥配置或系统时间是否准确。", null, exchange.StatusCode);

        if ((int)exchange.StatusCode < 200 || (int)exchange.StatusCode >= 300)
            throw new HttpRequestException($"弹弹play 服务返回 HTTP {(int)exchange.StatusCode}。", null, exchange.StatusCode);

        return SourceHttpClient.DecodeText(exchange.Payload.Content);
    }

    private async Task<(string AppId, List<string> Secrets)> ResolveCredentialsAsync(CancellationToken token)
    {
        var customId = (await _database.GetSettingAsync(SettingAppId, token).ConfigureAwait(false))?.Trim();
        var customSecret = SecretStore.Unprotect(await _database.GetSettingAsync(SettingAppSecret, token).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(customId) && !string.IsNullOrWhiteSpace(customSecret))
            return (customId, [customSecret]);

        var builtInId = DanDanPlayBuildConfig.AppId;
        var secrets = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(DanDanPlayBuildConfig.AppSecret1)) secrets.Add(DanDanPlayBuildConfig.AppSecret1);
        if (!string.IsNullOrWhiteSpace(DanDanPlayBuildConfig.AppSecret2)) secrets.Add(DanDanPlayBuildConfig.AppSecret2);
        return (builtInId, secrets);
    }

    private void CacheMatch(string key, DanDanPlayEpisode episode)
    {
        if (_matchCache.Count >= 64)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _matchCache.Where(p => p.Value.Expires < now))
                _matchCache.TryRemove(pair.Key, out _);
            if (_matchCache.Count >= 64)
                foreach (var pair in _matchCache.OrderBy(p => p.Value.Expires).Take(_matchCache.Count - 63))
                    _matchCache.TryRemove(pair.Key, out _);
        }
        _matchCache[key] = (episode, DateTimeOffset.UtcNow.AddHours(2));
    }

    private static async Task<string> ComputeFilePrefixMd5Async(string filePath, CancellationToken token)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var remaining = HashPrefixBytes;
        while (remaining > 0)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureSuccess(JsonElement root, string fallbackMessage)
    {
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var msg = ReadString(root, "errorMessage");
            throw new InvalidDataException(string.IsNullOrWhiteSpace(msg) ? fallbackMessage : msg);
        }
    }

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? string.Empty : string.Empty;

    private static long ReadInt64(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var val) ? val : 0;
}
