using System.Net;
using System.Text.Json;
using TV.Models;
using TV.Services.Playback;

namespace TV.Services.Vod.Native;

public sealed partial class BilibiliVodProvider
{
    public async Task<OnlineMediaMatch> MatchAsync(string title, string episodeName, int episodeIndex, CancellationToken token, string? year = null, string? typeName = null)
    {
        var site = OnlineSite();
        var identity = MediaMatching.Parse(title, episodeName, episodeIndex, year, typeName);
        var wanted = identity.CleanTitle.Length >= 2 ? identity.CleanTitle : identity.BaseTitle;
        if (wanted.Length == 0) throw new InvalidDataException("无法从当前标题识别节目名称。");
        var wantedSeason = identity.SeasonNumber ?? 1;
        var queries = new List<string> { wanted };
        if (identity.BaseTitle.Length >= 2 && !identity.BaseTitle.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            queries.Add(identity.BaseTitle);

        try
        {
            var candidates = new List<(JsonElement Item, string Season, double Score)>();
            var seenSeasons = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in queries)
            {
                foreach (var type in new[] { "media_ft", "media_bangumi" })
                {
                    try
                    {
                        var search = await ReadApiAsync(site, "/x/web-interface/wbi/search/type", token,
                            ("search_type", type), ("keyword", q), ("page", "1")).ConfigureAwait(false);
                        foreach (var item in Array(search, "result"))
                        {
                            var seasonId = Text(item, "season_id");
                            if (seasonId.Length == 0 || !seasonId.All(char.IsAsciiDigit) || !seenSeasons.Add(seasonId)) continue;
                            var itemTitle = PlaybackParsing.StripHtml(Text(item, "title"));
                            var orgTitle = PlaybackParsing.StripHtml(Text(item, "org_title"));
                            var scoreA = DanDanPlayService.ComputeTitleRelevance(identity, itemTitle, out var seasonA);
                            int? seasonB = null;
                            var scoreB = orgTitle.Length > 0 ? DanDanPlayService.ComputeTitleRelevance(identity, orgTitle, out seasonB) : 0;
                            var titleScore = Math.Max(scoreA, scoreB);
                            if (titleScore < 420) continue;
                            var candSeason = (scoreA >= scoreB ? seasonA : seasonB) ?? 1;
                            var score = titleScore + (candSeason == wantedSeason ? 300 : -400);
                            candidates.Add((item, seasonId, score));
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or JsonException)
                    {
                        Diagnostics.Log($"哔哩媒体搜索不可用 ({type}): {ex.GetType().Name}");
                    }
                }
                if (candidates.Any(c => c.Score >= 1100)) break;
            }
            foreach (var candidate in candidates.OrderByDescending(c => c.Score).Take(4))
            {
                var season = await ReadApiAsync(site, "/pgc/view/web/season", token, ("season_id", candidate.Season)).ConfigureAwait(false);
                var episode = SelectEpisode(Array(season, "episodes"), identity, episodeName, episodeIndex);
                if (episode.ValueKind != JsonValueKind.Object) continue;
                var aid = Int(episode, "aid");
                var cid = Int(episode, "cid");
                if (aid <= 0 || cid <= 0) continue;
                return new OnlineMediaMatch(PlaybackParsing.StripHtml(Text(candidate.Item, "title")),
                    Text(episode, "long_title") is { Length: > 0 } name ? name : Text(episode, "title"), aid, cid);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or JsonException)
        {
            Diagnostics.Log($"哔哩番剧弹幕匹配失败: {ex.GetType().Name}");
        }
        var videos = await ReadApiAsync(site, "/x/web-interface/wbi/search/type", token,
            ("search_type", "video"), ("keyword", wanted)).ConfigureAwait(false);
        var normalized = SearchScoring.Normalize(identity.BaseTitle);
        foreach (var video in Array(videos, "result").Where(item =>
            SearchScoring.Normalize(PlaybackParsing.StripHtml(Text(item, "title"))) == normalized).Take(3))
        {
            var detail = await ReadApiAsync(site, "/x/web-interface/view", token, ("bvid", Text(video, "bvid"))).ConfigureAwait(false);
            var pages = Array(detail, "pages");
            var episode = SelectEpisode(pages, identity, episodeName, episodeIndex);
            if (episode.ValueKind != JsonValueKind.Object) episode = pages.FirstOrDefault();
            var aid = Int(detail, "aid");
            var cid = Int(episode, "cid");
            if (aid > 0 && cid > 0) return new OnlineMediaMatch(PlaybackParsing.StripHtml(Text(detail, "title")), Text(episode, "part"), aid, cid);
        }
        throw new InvalidDataException("未找到标题与集数完全匹配的哔哩哔哩节目，请手动选择其它来源。");
    }

    public async Task<IReadOnlyList<OnlineSubtitleTrack>> GetOnlineSubtitlesAsync(OnlineMediaMatch match, CancellationToken token)
    {
        var site = OnlineSite();
        var cookie = account is null ? null : await account.GetPlaybackCookieAsync(token).ConfigureAwait(false);
        var query = FormattableString.Invariant($"aid={match.Aid}&cid={match.Cid}");
        var data = await ReadApiQueryAsync(site, "/x/player/v2", query, token, cookie).ConfigureAwait(false);
        var result = new List<OnlineSubtitleTrack>();
        foreach (var subtitle in Array(Value(data, "subtitle"), "subtitles"))
        {
            var address = Text(subtitle, "subtitle_url");
            if (address.StartsWith("//", StringComparison.Ordinal)) address = "https:" + address;
            if (AsHttpUri(address) is not { } uri) continue;
            var name = Text(subtitle, "lan_doc");
            result.Add(new OnlineSubtitleTrack(name.Length > 0 ? name : Text(subtitle, "lan"), uri));
        }
        return result;
    }

    private static SourceSite OnlineSite() => new() { Key = "online", Name = "哔哩哔哩", Type = 3, Api = "csp_BiliGuard" };

    private static JsonElement SelectEpisode(JsonElement[] episodes, MediaIdentity identity, string name, int index)
    {
        if (episodes.Length == 0) return default;
        var normal = episodes.Where(ep => !MediaMatching.IsSpecialEpisodeTitle(Text(ep, "badge") + " " + Text(ep, "title") + " " + Text(ep, "long_title"))).ToArray();
        var pool = identity.SpecialTag is null && normal.Length > 0 ? normal : episodes;
        if (identity.IsMovie && !identity.EpisodeNumber.HasValue)
            return pool[0];
        var wanted = identity.EpisodeNumber ?? MediaMatching.EpisodeNumber(name) ?? (index >= 0 ? index + 1 : 1);
        foreach (var episode in pool)
            if (MediaMatching.EpisodeNumber(Text(episode, "title")) == wanted ||
                MediaMatching.EpisodeNumber(Text(episode, "index_title")) == wanted ||
                MediaMatching.EpisodeNumber(Text(episode, "part")) == wanted)
                return episode;
        if (wanted >= 1 && wanted <= pool.Length) return pool[wanted - 1];
        return index >= 0 && index < pool.Length ? pool[index] : pool[0];
    }
}
