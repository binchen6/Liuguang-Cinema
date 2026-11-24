using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

public sealed partial class DanDanPlayService
{
    public const string OpenPlatformUrl = "https://doc.dandanplay.com/open/";
    public const string ApiBaseUrl = "https://api.dandanplay.net";
    public const string SettingAppId = "danmaku.dandanplay.appId";
    public const string SettingAppSecret = "danmaku.dandanplay.appSecret";
    private const int HashPrefixBytes = 16 * 1024 * 1024; // 16 MB per DanDanPlay /api/v2/match spec
    private const int MaxEpisodeQueryNumber = 9999;
    private const double MinTitleRelevanceScore = 230.0;
    private static readonly TimeSpan SearchRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommentRequestTimeout = TimeSpan.FromSeconds(25);

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
        CancellationToken token = default,
        string? year = null,
        string? typeName = null)
    {
        var identity = MediaMatching.Parse(title, episode, episodeIndex, year, typeName);
        var cacheKey = !string.IsNullOrWhiteSpace(localFilePath)
            ? "file:" + localFilePath
            : identity.CanonicalKey + (identity.EpisodeNumber.HasValue || identity.IsMovie ? string.Empty : $"|idx{episodeIndex}");

        if (_matchCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Episode;

        DanDanPlayEpisode? fileCandidate = null;

        // Strategy A: Exact local file 16 MB MD5 hash + filename matching via POST /api/v2/match
        if (!string.IsNullOrWhiteSpace(localFilePath) && File.Exists(localFilePath))
        {
            try
            {
                var (isMatched, matches) = await MatchFileAsync(localFilePath, durationSeconds, token).ConfigureAwait(false);
                if (isMatched && matches.Count > 0)
                {
                    CacheMatch(cacheKey, matches[0]);
                    return matches[0];
                }
                fileCandidate = PickBestFlatMatch(matches, identity, episode);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Diagnostics.Log($"DanDanPlay file hash match skipped: {ex.GetType().Name} {ex.Message}");
            }
        }

        // Strategy B: Multi-candidate canonical search via GET /api/v2/search/episodes (v2 search engine)
        // Query without the strict episode= filter first so DanDanPlay returns all seasons & episodes in one round-trip,
        // avoiding redundant sequential requests that increase timeout risk on high-latency links.
        var collectedGroups = new Dictionary<long, DanDanPlayAnimeGroup>();
        var queries = BuildSearchQueries(identity);
        Exception? lastTransientError = null;

        foreach (var query in queries)
        {
            var (groups, error) = await TrySearchEpisodesSafeAsync(query, null, useV2: true, token).ConfigureAwait(false);
            if (error is not null) lastTransientError = error;
            if (groups.Count == 0 && error is null)
            {
                var (legacyGroups, legacyError) = await TrySearchEpisodesSafeAsync(query, null, useV2: false, token).ConfigureAwait(false);
                if (legacyError is not null) lastTransientError = legacyError;
                groups = legacyGroups;
            }

            foreach (var group in groups)
                collectedGroups.TryAdd(group.AnimeId, group);

            var earlyPick = PickBestEpisode(collectedGroups.Values.ToArray(), identity, episode, episodeIndex);
            if (earlyPick is not null && earlyPick.Value.Confidence >= 1100)
            {
                CacheMatch(cacheKey, earlyPick.Value.Episode);
                return earlyPick.Value.Episode;
            }
        }

        if (collectedGroups.Count > 0)
        {
            var picked = PickBestEpisode(collectedGroups.Values.ToArray(), identity, episode, episodeIndex);
            if (picked is not null)
            {
                CacheMatch(cacheKey, picked.Value.Episode);
                return picked.Value.Episode;
            }
        }

        // Strategy C: Verified filename-only fallback via POST /api/v2/match (without extension per official spec)
        var syntheticName = BuildSyntheticMatchFileName(identity, localFilePath, title);
        if (syntheticName.Length >= 2)
        {
            try
            {
                var (isMatched, matches) = await MatchFileNameOnlyAsync(syntheticName, durationSeconds, token).ConfigureAwait(false);
                var nameMatch = isMatched && matches.Count > 0
                    ? matches[0]
                    : PickBestFlatMatch(matches, identity, episode);
                if (nameMatch is not null)
                {
                    CacheMatch(cacheKey, nameMatch);
                    return nameMatch;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (ex is TimeoutException or HttpRequestException or OperationCanceledException)
                    lastTransientError ??= ex;
                Diagnostics.Log($"DanDanPlay filename match fallback skipped: {ex.GetType().Name} {ex.Message}");
            }
        }

        if (fileCandidate is not null)
        {
            CacheMatch(cacheKey, fileCandidate);
            return fileCandidate;
        }

        if (collectedGroups.Count == 0 && lastTransientError is not null)
        {
            if (lastTransientError is TimeoutException or OperationCanceledException)
                throw new TimeoutException("弹弹play 服务响应超时，请稍后重试。", lastTransientError);
            throw lastTransientError;
        }

        var display = string.IsNullOrWhiteSpace(identity.CleanTitle) ? title : identity.CleanTitle;
        throw new InvalidDataException($"弹弹play 弹幕库未找到与“{display}”匹配的剧集。");
    }

    public async Task<IReadOnlyList<DanDanPlayAnimeGroup>> SearchEpisodesAsync(string keyword, int? episodeNumber = null, CancellationToken token = default)
    {
        var query = SanitizeSearchKeyword(keyword);
        if (query.Length < 2)
            throw new InvalidDataException("弹弹play 搜索关键词至少需要 2 个有效字符。");

        var groups = await SearchEpisodesCoreAsync(query, episodeNumber, useV2: true, token).ConfigureAwait(false);
        if (groups.Count == 0)
            groups = await SearchEpisodesCoreAsync(query, episodeNumber, useV2: false, token).ConfigureAwait(false);
        return groups;
    }

    private async Task<(IReadOnlyList<DanDanPlayAnimeGroup> Groups, Exception? TransientError)> TrySearchEpisodesSafeAsync(
        string keyword, int? episodeNumber, bool useV2, CancellationToken token)
    {
        var query = SanitizeSearchKeyword(keyword);
        if (query.Length < 2) return ([], null);
        try
        {
            var result = await SearchEpisodesCoreAsync(query, episodeNumber, useV2, token).ConfigureAwait(false);
            return (result, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex)
        {
            Diagnostics.Log($"DanDanPlay search skipped ({query}, ep={episodeNumber}, v2={useV2}): {ex.Message}");
            return ([], null);
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or OperationCanceledException)
        {
            Diagnostics.Log($"DanDanPlay search transient error ({query}, ep={episodeNumber}, v2={useV2}): {ex.GetType().Name} {ex.Message}");
            return ([], ex);
        }
    }

    private async Task<IReadOnlyList<DanDanPlayAnimeGroup>> SearchEpisodesCoreAsync(
        string query, int? episodeNumber, bool useV2, CancellationToken token)
    {
        const string path = "/api/v2/search/episodes";
        var url = $"{ApiBaseUrl}{path}?anime={Uri.EscapeDataString(query)}";
        if (episodeNumber is >= 1 and <= MaxEpisodeQueryNumber)
            url += $"&episode={episodeNumber.Value.ToString(CultureInfo.InvariantCulture)}";
        if (useV2)
            url += "&v2=true";

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
        var convert = Math.Clamp(chConvert, 0, 2);
        var url = $"{ApiBaseUrl}{path}?withRelated={(withRelated ? "true" : "false")}&chConvert={convert}";
        string json;
        try
        {
            json = await SendSignedAsync(HttpMethod.Get, path, url, bodyJson: null, token, CommentRequestTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (withRelated && !token.IsCancellationRequested &&
            (ex is TimeoutException || (ex is HttpRequestException hre && (hre.StatusCode is null || (int)hre.StatusCode >= 500))))
        {
            // Third-party related comment aggregation can stall at peak hours; fall back to direct DanDanPlay comments.
            Diagnostics.Log($"DanDanPlay withRelated=true failed ({ex.GetType().Name}), falling back to direct comments");
            var directUrl = $"{ApiBaseUrl}{path}?withRelated=false&chConvert={convert}";
            json = await SendSignedAsync(HttpMethod.Get, path, directUrl, bodyJson: null, token, CommentRequestTimeout).ConfigureAwait(false);
        }
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
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (false, "弹弹play 服务响应超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool IsMatched, List<DanDanPlayEpisode> Matches)> MatchFileAsync(string localFilePath, int durationSeconds, CancellationToken token)
    {
        var info = new FileInfo(localFilePath);
        if (!info.Exists || info.Length == 0) return (false, []);
        var hash = await ComputeFilePrefixMd5Async(localFilePath, token).ConfigureAwait(false);
        var fileName = SanitizeMatchFileName(Path.GetFileNameWithoutExtension(info.Name));
        if (fileName.Length < 2) fileName = SanitizeMatchFileName(info.Name);
        var payload = JsonSerializer.Serialize(new
        {
            fileName,
            fileHash = hash,
            fileSize = info.Length,
            videoDuration = Math.Clamp(durationSeconds, 0, 86400),
            matchMode = "hashAndFileName"
        });
        return await PostMatchAsync(payload, token).ConfigureAwait(false);
    }

    private Task<(bool IsMatched, List<DanDanPlayEpisode> Matches)> MatchFileNameOnlyAsync(string fileName, int durationSeconds, CancellationToken token)
    {
        var cleanName = SanitizeMatchFileName(fileName);
        var payload = JsonSerializer.Serialize(new
        {
            fileName = cleanName,
            fileHash = "00000000000000000000000000000000",
            fileSize = 0L,
            videoDuration = Math.Clamp(durationSeconds, 0, 86400),
            matchMode = "fileNameOnly"
        });
        return PostMatchAsync(payload, token);
    }

    private async Task<(bool IsMatched, List<DanDanPlayEpisode> Matches)> PostMatchAsync(string bodyJson, CancellationToken token)
    {
        const string path = "/api/v2/match";
        var json = await SendSignedAsync(HttpMethod.Post, path, ApiBaseUrl + path, bodyJson, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureSuccess(root, "弹弹play 文件匹配失败");

        var isMatched = root.TryGetProperty("isMatched", out var matchedProp) && matchedProp.ValueKind == JsonValueKind.True;
        var results = new List<DanDanPlayEpisode>();
        if (!root.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return (false, results);

        foreach (var item in matches.EnumerateArray())
        {
            var epId = ReadInt64(item, "episodeId");
            if (epId <= 0) continue;
            var animeTitle = ReadString(item, "animeTitle");
            var epTitle = ReadString(item, "episodeTitle");
            var typeDesc = ReadString(item, "typeDescription");
            results.Add(new DanDanPlayEpisode(epId, animeTitle, epTitle, typeDesc));
        }

        return (isMatched, results);
    }

    private static List<string> BuildSearchQueries(MediaIdentity identity)
    {
        var list = new List<string>();
        void Add(string value)
        {
            var sanitized = SanitizeSearchKeyword(value);
            if (sanitized.Length >= 2 && !list.Any(q => q.Equals(sanitized, StringComparison.OrdinalIgnoreCase)))
                list.Add(sanitized);
        }

        // Query BaseTitle first: DanDanPlay returns all seasons in one round-trip, and PickBestEpisode
        // disambiguates the exact season with +320 / -420 season scoring.
        Add(identity.BaseTitle);
        if (identity.SeasonNumber is >= 2 and <= 10)
        {
            var zh = identity.SeasonNumber.Value switch
            {
                2 => "二", 3 => "三", 4 => "四", 5 => "五",
                6 => "六", 7 => "七", 8 => "八", 9 => "九", 10 => "十", _ => identity.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture)
            };
            Add($"{identity.BaseTitle} 第{zh}季");
        }
        Add(identity.CleanTitle);
        foreach (var alias in identity.Aliases)
            Add(alias);

        return list;
    }

    private static string BuildSyntheticMatchFileName(MediaIdentity identity, string? localFilePath, string rawTitle)
    {
        var titlePart = !string.IsNullOrWhiteSpace(identity.BaseTitle) ? identity.BaseTitle : MediaMatching.StripMediaPathAndExtension(localFilePath ?? rawTitle);
        if (titlePart.Length == 0) return string.Empty;
        var sb = new StringBuilder(titlePart);
        if (identity.SeasonNumber is >= 1 and <= 50 && identity.EpisodeNumber is >= 1 and <= MaxEpisodeQueryNumber)
            sb.Append($" S{identity.SeasonNumber.Value:D2}E{identity.EpisodeNumber.Value:D2}");
        else if (identity.SeasonNumber is >= 2 and <= 50)
            sb.Append($" 第{identity.SeasonNumber.Value}季");
        else if (identity.EpisodeNumber is >= 1 and <= MaxEpisodeQueryNumber)
            sb.Append($" 第{identity.EpisodeNumber.Value:D2}集");
        else if (!string.IsNullOrWhiteSpace(identity.SpecialTag))
            sb.Append(' ').Append(identity.SpecialTag);
        return SanitizeMatchFileName(sb.ToString());
    }

    internal static (DanDanPlayEpisode Episode, double Confidence)? PickBestEpisode(
        IReadOnlyList<DanDanPlayAnimeGroup> groups,
        MediaIdentity identity,
        string rawEpisode,
        int episodeIndex)
    {
        if (groups.Count == 0) return null;

        var wantedSeason = identity.SeasonNumber ?? 1;
        var targetEp = identity.EpisodeNumber;

        (DanDanPlayEpisode Episode, double Confidence)? best = null;

        foreach (var group in groups)
        {
            var titleScore = ComputeTitleRelevance(identity, group.AnimeTitle, out var candSeason);
            if (titleScore < MinTitleRelevanceScore) continue;

            var score = titleScore;

            // Season alignment (critical for multi-season shows like 庆余年/葬送的芙莉莲/2.5次元的诱惑)
            var effectiveCandSeason = candSeason ?? 1;
            if (effectiveCandSeason == wantedSeason) score += 320;
            else score -= 420;

            // Media type alignment (movie vs TV vs OVA/SP)
            var isCandMovie = group.TypeDescription.Contains("剧场", StringComparison.OrdinalIgnoreCase)
                || group.TypeDescription.Contains("电影", StringComparison.OrdinalIgnoreCase);
            var isCandOva = group.TypeDescription.Contains("OVA", StringComparison.OrdinalIgnoreCase)
                || group.TypeDescription.Contains("OAD", StringComparison.OrdinalIgnoreCase)
                || group.AnimeTitle.Contains("OVA", StringComparison.OrdinalIgnoreCase)
                || group.AnimeTitle.Contains("总集篇", StringComparison.OrdinalIgnoreCase);

            if (identity.IsMovie)
            {
                if (isCandMovie || group.Episodes.Count == 1) score += 220;
                else if (group.Episodes.Count > 3) score -= 140;
            }
            else if (identity.SpecialTag is not null)
            {
                if (isCandOva) score += 160;
            }
            else
            {
                if (isCandOva || isCandMovie) score -= 180;
                else score += 60;
            }

            // Year hint bonus when title embeds release year
            if (identity.Year.HasValue)
            {
                var yearText = identity.Year.Value.ToString(CultureInfo.InvariantCulture);
                if (group.AnimeTitle.Contains(yearText, StringComparison.Ordinal)) score += 120;
            }

            var (pickedEp, epExact, epMatchBonus) = SelectEpisodeFromGroup(group.Episodes, identity, rawEpisode, episodeIndex);
            if (pickedEp is null) continue;

            score += epMatchBonus;
            if (targetEp is >= 1 && !epExact)
            {
                var normalCount = group.Episodes.Count(e => !MediaMatching.IsSpecialEpisodeTitle(e.EpisodeTitle));
                if (normalCount < targetEp.Value) score -= 260;
            }

            if (best is null || score > best.Value.Confidence)
                best = (pickedEp, score);
        }

        return best;
    }

    private static (DanDanPlayEpisode? Episode, bool Exact, double Bonus) SelectEpisodeFromGroup(
        IReadOnlyList<DanDanPlayEpisode> episodes,
        MediaIdentity identity,
        string rawEpisode,
        int episodeIndex)
    {
        if (episodes.Count == 0) return (null, false, 0);

        var normalEps = episodes.Where(e => !MediaMatching.IsSpecialEpisodeTitle(e.EpisodeTitle)).ToArray();
        var specialEps = episodes.Where(e => MediaMatching.IsSpecialEpisodeTitle(e.EpisodeTitle)).ToArray();

        // Special episode requested (SP / OVA / PV / 番外)
        if (identity.SpecialTag is not null)
        {
            if (identity.EpisodeNumber is >= 1)
            {
                var specialByNum = specialEps.FirstOrDefault(e => MediaMatching.EpisodeNumber(e.EpisodeTitle) == identity.EpisodeNumber.Value);
                if (specialByNum is not null) return (specialByNum, true, 180);
            }
            if (specialEps.Length > 0) return (specialEps[0], false, 80);
        }

        // Movie or single-episode work
        if (identity.IsMovie && !identity.EpisodeNumber.HasValue)
        {
            var main = normalEps.FirstOrDefault() ?? episodes[0];
            return (main, episodes.Count == 1, episodes.Count == 1 ? 160 : 60);
        }

        // Numbered episode: ALWAYS prefer non-special episodes first so PV/SP at index 0 never shifts numbering
        if (identity.EpisodeNumber is >= 1)
        {
            var target = identity.EpisodeNumber.Value;
            var exactNormal = normalEps.FirstOrDefault(e => MediaMatching.EpisodeNumber(e.EpisodeTitle) == target);
            if (exactNormal is not null) return (exactNormal, true, 200);

            // Check leading integer in DanDanPlay titles like "1. 冒险的结束" or "05 标题"
            var leadingNormal = normalEps.FirstOrDefault(e => LeadingEpisodeNumber(e.EpisodeTitle) == target);
            if (leadingNormal is not null) return (leadingNormal, true, 180);

            if (target <= normalEps.Length)
                return (normalEps[target - 1], false, 110);

            var exactAny = episodes.FirstOrDefault(e => MediaMatching.EpisodeNumber(e.EpisodeTitle) == target);
            if (exactAny is not null) return (exactAny, true, 120);

            if (target <= episodes.Count)
                return (episodes[target - 1], false, 40);

            return (null, false, -200);
        }

        // Unnumbered episode: try matching episode subtitle text before positional index
        if (!string.IsNullOrWhiteSpace(rawEpisode))
        {
            var normEp = SearchScoring.Normalize(MediaMatching.StripMediaPathAndExtension(rawEpisode));
            if (normEp.Length >= 2)
            {
                var byTitle = episodes.FirstOrDefault(e => SearchScoring.Normalize(e.EpisodeTitle).Contains(normEp, StringComparison.OrdinalIgnoreCase));
                if (byTitle is not null) return (byTitle, true, 140);
            }
        }

        var pool = normalEps.Length > 0 ? normalEps : episodes;
        if (episodeIndex >= 0 && episodeIndex < pool.Count)
            return (pool[episodeIndex], false, 40);
        return (pool[0], false, 0);
    }

    private static DanDanPlayEpisode? PickBestFlatMatch(IReadOnlyList<DanDanPlayEpisode> matches, MediaIdentity identity, string rawEpisode)
    {
        if (matches.Count == 0) return null;
        var wantedSeason = identity.SeasonNumber ?? 1;
        DanDanPlayEpisode? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var match in matches)
        {
            var titleScore = ComputeTitleRelevance(identity, match.AnimeTitle, out var candSeason);
            if (titleScore < MinTitleRelevanceScore) continue;

            var score = titleScore + ((candSeason ?? 1) == wantedSeason ? 280 : -380);
            if (identity.EpisodeNumber is >= 1)
            {
                var epNum = MediaMatching.EpisodeNumber(match.EpisodeTitle) ?? LeadingEpisodeNumber(match.EpisodeTitle);
                if (epNum == identity.EpisodeNumber.Value && !MediaMatching.IsSpecialEpisodeTitle(match.EpisodeTitle))
                    score += 240;
                else if (epNum.HasValue && epNum.Value != identity.EpisodeNumber.Value)
                    score -= 320;
            }
            else if (!string.IsNullOrWhiteSpace(rawEpisode))
            {
                var normEp = SearchScoring.Normalize(rawEpisode);
                if (normEp.Length >= 2 && SearchScoring.Normalize(match.EpisodeTitle).Contains(normEp, StringComparison.OrdinalIgnoreCase))
                    score += 120;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = match;
            }
        }

        return bestScore >= MinTitleRelevanceScore ? best : null;
    }

    internal static double ComputeTitleRelevance(MediaIdentity identity, string candidateTitle, out int? candidateSeason)
    {
        var (candBase, parsedSeason) = MediaMatching.ExtractBaseAndSeason(MediaMatching.Title(candidateTitle));
        candidateSeason = parsedSeason;

        var normCandFull = SearchScoring.Normalize(candidateTitle);
        var normCandBase = SearchScoring.Normalize(candBase);
        if (normCandBase.Length == 0) normCandBase = normCandFull;
        if (normCandBase.Length == 0) return 0;

        var best = 0.0;
        var normClean = SearchScoring.Normalize(identity.CleanTitle);
        if (normClean.Length > 0 && normCandFull.Equals(normClean, StringComparison.OrdinalIgnoreCase))
            best = Math.Max(best, 1120);

        foreach (var alias in identity.Aliases)
        {
            var normAlias = SearchScoring.Normalize(alias);
            if (normAlias.Length == 0) continue;

            var s = ScoreNormalizedPair(normAlias, normCandBase);
            if (normCandFull != normCandBase)
                s = Math.Max(s, ScoreNormalizedPair(normAlias, normCandFull));
            if (s > best) best = s;
        }

        return best;
    }

    private static double ScoreNormalizedPair(string query, string candidate)
    {
        if (query.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            return 1000;

        var minLen = Math.Min(query.Length, candidate.Length);
        var maxLen = Math.Max(query.Length, candidate.Length);
        var lengthRatio = maxLen > 0 ? (double)minLen / maxLen : 0;

        if (minLen >= 2 && (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                            query.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            if (lengthRatio >= 0.45)
                return 620 + lengthRatio * 180 - Math.Abs(candidate.Length - query.Length) * 4;
        }

        if (minLen >= 2 && (candidate.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            query.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            if (lengthRatio >= 0.50)
                return 420 + lengthRatio * 160 - Math.Abs(candidate.Length - query.Length) * 4;
        }

        var dice = BigramDiceSimilarity(query, candidate);
        if (dice >= 0.62)
            return dice * 420;

        return 0;
    }

    private static double BigramDiceSimilarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return 0;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalA = a.Length - 1;
        for (var i = 0; i < totalA; i++)
        {
            var bg = a.Substring(i, 2);
            counts[bg] = counts.TryGetValue(bg, out var c) ? c + 1 : 1;
        }
        var overlap = 0;
        var totalB = b.Length - 1;
        for (var i = 0; i < totalB; i++)
        {
            var bg = b.Substring(i, 2);
            if (counts.TryGetValue(bg, out var c) && c > 0)
            {
                overlap++;
                counts[bg] = c - 1;
            }
        }
        return (2.0 * overlap) / (totalA + totalB);
    }

    private static int? LeadingEpisodeNumber(string title)
    {
        var m = LeadingDigits().Match(title.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) && num is >= 1 and <= 2000
            ? num : null;
    }

    public static string SanitizeSearchKeyword(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        var prevSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || ch is '\u200B' or '\uFEFF') continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0) { sb.Append(' '); prevSpace = true; }
                continue;
            }
            sb.Append(ch);
            prevSpace = false;
            if (sb.Length >= 50) break;
        }
        return sb.ToString().Trim();
    }

    private static string SanitizeMatchFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var stripped = MediaMatching.StripMediaPathAndExtension(raw);
        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            if (char.IsControl(ch) || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append(' ');
            else
                sb.Append(ch);
            if (sb.Length >= 200) break;
        }
        return MultiWhitespace().Replace(sb.ToString(), " ").Trim();
    }

    private async Task<string> SendSignedAsync(
        HttpMethod method,
        string apiPath,
        string url,
        string? bodyJson,
        CancellationToken token,
        TimeSpan? requestTimeout = null)
    {
        var (appId, secrets) = await ResolveCredentialsAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(appId) || secrets.Count == 0)
            throw new InvalidOperationException("尚未配置弹弹play 开放平台 AppId/AppSecret，请在“设置 → 弹幕与 AI”中填入凭证。");

        var timeout = requestTimeout ?? SearchRequestTimeout;
        var startIdx = _useBackupSecret && secrets.Count > 1 ? 1 : 0;
        Exception? lastError = null;
        for (var attempt = 0; attempt < secrets.Count; attempt++)
        {
            var idx = (startIdx + attempt) % secrets.Count;
            for (var retry = 0; retry < 2; retry++)
            {
                try
                {
                    var result = await SendWithSingleCredentialAsync(method, apiPath, url, bodyJson, appId, secrets[idx], token, timeout).ConfigureAwait(false);
                    if (idx == 1) _useBackupSecret = true;
                    else if (idx == 0) _useBackupSecret = false;
                    return result;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt + 1 < secrets.Count)
                {
                    Diagnostics.Log($"DanDanPlay secret #{idx + 1} rejected ({(int?)ex.StatusCode}), failing over to backup secret");
                    lastError = ex;
                    break;
                }
                catch (Exception ex) when (retry == 0 && !token.IsCancellationRequested &&
                    (ex is TimeoutException || (ex is HttpRequestException hre && (hre.StatusCode is null || (int)hre.StatusCode >= 500 || hre.StatusCode == HttpStatusCode.TooManyRequests))))
                {
                    Diagnostics.Log($"DanDanPlay transient failure on {apiPath} ({ex.GetType().Name}), retrying once");
                    lastError = ex;
                    await Task.Delay(400, token).ConfigureAwait(false);
                }
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
        CancellationToken token,
        TimeSpan? requestTimeout = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(appId, timestamp, apiPath, appSecret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
            ["User-Agent"] = "LiuguangCinema/1.9 (DanDanPlay-OpenAPI)",
            ["X-AppId"] = appId,
            ["X-Timestamp"] = timestamp,
            ["X-Signature"] = signature
        };

        var timeout = requestTimeout ?? SearchRequestTimeout;
        HttpExchangeResult exchange;
        try
        {
            exchange = method == HttpMethod.Post
                ? await _http.PostJsonAsync(url, bodyJson ?? "{}", headers, maxBytes: 12 * 1024 * 1024, cancellationToken: token, requestTimeout: timeout).ConfigureAwait(false)
                : await _http.GetExchangeAsync(url, headers, maxBytes: 12 * 1024 * 1024, cancellationToken: token, requestTimeout: timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("弹弹play 服务响应超时，请稍后重试。", ex);
        }

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
            var detail = ReadString(root, "errorDetail");
            if (detail.Length > 0) Diagnostics.Log($"DanDanPlay API errorDetail: {detail}");
            throw new InvalidDataException(string.IsNullOrWhiteSpace(msg) ? fallbackMessage : msg);
        }
    }

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? string.Empty : string.Empty;

    private static long ReadInt64(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var val) ? val : 0;

    [GeneratedRegex(@"^(\d{1,4})(?:\s|\.|、|话|集|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDigits();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespace();
}
