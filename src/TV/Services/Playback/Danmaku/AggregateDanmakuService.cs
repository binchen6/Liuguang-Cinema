using System.Text.Json;
using TV.Services.Http;

namespace TV.Services.Playback;

/// <summary>聚合弹幕源的匹配结果（标题、分集与弹幕内容）。</summary>
public sealed record AggregateDanmakuResult(string SourceName, string Title, string EpisodeTitle, DanmakuEntry[] Comments);

internal sealed record AggregateSeries(string Id, string Title, int? Season, string? Year);

internal sealed record AggregateEpisode(string Id, string Title, int? Number);

/// <summary>
/// 「其他聚合」类弹幕源契约：按节目标题搜索 → 选择分集 → 拉取弹幕。
/// 实现参考 Danmaku-Anywhere/dango 的 dango-manifests 声明式契约，全部以 C# 原生实现。
/// </summary>
internal interface IAggregateDanmakuSource
{
    string Id { get; }
    string DisplayName { get; }
    Task<AggregateDanmakuResult> MatchAndLoadAsync(MediaIdentity identity, string episodeName, int episodeIndex, CancellationToken token);
}

/// <summary>聚合弹幕源编排器：按注册顺序逐源尝试，第一个命中分集弹幕的来源即返回。</summary>
public sealed class AggregateDanmakuService(SourceHttpClient http)
{
    private readonly IAggregateDanmakuSource[] _sources =
    [
        new AiyifanDanmakuSource(http),
        new RenrenDanmakuSource(http)
    ];

    public IReadOnlyList<string> SourceNames => _sources.Select(source => source.DisplayName).ToArray();

    public async Task<AggregateDanmakuResult> MatchAsync(string title, string episodeName, int episodeIndex,
        string? year = null, string? typeName = null, CancellationToken token = default)
    {
        var identity = MediaMatching.Parse(title, episodeName, episodeIndex, year, typeName);
        var failures = new List<string>();
        foreach (var source in _sources)
        {
            try
            {
                return await source.MatchAndLoadAsync(identity, episodeName, episodeIndex, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (DanmakuNotMatchedException ex)
            {
                failures.Add($"{source.DisplayName}：{ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or TimeoutException or InvalidOperationException)
            {
                failures.Add($"{source.DisplayName}：服务暂不可用（{ex.GetType().Name}）");
                Diagnostics.Log($"聚合弹幕源 {source.Id} 失败: {ex.GetType().Name}");
            }
        }
        throw new DanmakuNotMatchedException("未在聚合弹幕源中找到匹配节目。 " + string.Join("；", failures));
    }
}

/// <summary>聚合源共用的标题评分与分集挑选逻辑（与 B 站、弹弹play 匹配同源）。</summary>
internal static class AggregateDanmakuMatching
{
    public const double MinTitleRelevanceScore = 420;
    public const double StrongMatchScore = 1100;

    public static double ScoreSeries(MediaIdentity identity, string candidateTitle, out int? candidateSeason)
    {
        var wantedSeason = identity.SeasonNumber ?? 1;
        var titleScore = DanDanPlayService.ComputeTitleRelevance(identity, candidateTitle, out candidateSeason);
        return titleScore < MinTitleRelevanceScore ? 0
            : titleScore + ((candidateSeason ?? 1) == wantedSeason ? 300 : -400);
    }

    /// <summary>按集号/标题挑选分集；特殊集（PV/OVA/预告等）在不匹配特殊标签时被排除。</summary>
    public static AggregateEpisode? SelectEpisode(IReadOnlyList<AggregateEpisode> episodes, MediaIdentity identity,
        string episodeName, int episodeIndex)
    {
        if (episodes.Count == 0) return null;
        var normal = episodes.Where(ep => !MediaMatching.IsSpecialEpisodeTitle(ep.Title)).ToArray();
        var pool = identity.SpecialTag is null && normal.Length > 0 ? normal : episodes;
        if (identity.IsMovie && !identity.EpisodeNumber.HasValue) return pool[0];
        var wanted = identity.EpisodeNumber ?? MediaMatching.EpisodeNumber(episodeName) ?? (episodeIndex >= 0 ? episodeIndex + 1 : 1);
        foreach (var episode in pool)
            if ((episode.Number.HasValue && episode.Number.Value == wanted) || MediaMatching.EpisodeNumber(episode.Title) == wanted)
                return episode;
        var ordered = pool.Where(ep => ep.Number.HasValue).OrderBy(ep => ep.Number!.Value).ToArray();
        if (ordered.Length > 0)
        {
            if (wanted >= 1 && wanted <= ordered.Length) return ordered[wanted - 1];
            return episodeIndex >= 0 && episodeIndex < ordered.Length ? ordered[episodeIndex] : ordered[0];
        }
        return episodeIndex >= 0 && episodeIndex < pool.Count ? pool[episodeIndex] : pool[0];
    }

    public static bool TryParseColor(string? raw, out uint rgb)
    {
        rgb = 0xFFFFFF;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Trim().TrimStart('#');
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (value.Length is 6 && value.All(char.IsAsciiHexDigit) && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out rgb))
            return true;
        rgb = 0xFFFFFF;
        return false;
    }

    public static DanmakuMode ModeOf(int? raw) => raw switch
    {
        4 => DanmakuMode.Bottom,
        5 => DanmakuMode.Top,
        _ => DanmakuMode.Scroll
    };
}
