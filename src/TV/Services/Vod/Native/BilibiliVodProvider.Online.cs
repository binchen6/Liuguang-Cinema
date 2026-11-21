using System.Net;
using System.Text.Json;
using TV.Models;
using TV.Services.Playback;

namespace TV.Services.Vod.Native;

public sealed partial class BilibiliVodProvider
{
    public async Task<OnlineMediaMatch> MatchAsync(string title, string episodeName, int episodeIndex, CancellationToken token)
    {
        var site = OnlineSite();
        var cookie = account is null ? null : await account.GetPlaybackCookieAsync(token).ConfigureAwait(false);
        var wanted = MediaMatching.Title(title);
        if (wanted.Length == 0) throw new InvalidDataException("无法从当前标题识别节目名称。");
        var normalized = SearchScoring.Normalize(wanted);
        try
        {
            var candidates = new List<(JsonElement Item, string Name, string Original, string Season)>();
            foreach (var type in new[] { "media_ft", "media_bangumi" })
            {
                try
                {
                    var search = await ReadApiAsync(site, "/x/web-interface/wbi/search/type", token,
                        ("search_type", type), ("keyword", wanted), ("page", "1")).ConfigureAwait(false);
                    candidates.AddRange(Array(search, "result").Select(item => (
                        Item: item,
                        Name: SearchScoring.Normalize(PlaybackParsing.StripHtml(Text(item, "title"))),
                        Original: SearchScoring.Normalize(PlaybackParsing.StripHtml(Text(item, "org_title"))),
                        Season: Text(item, "season_id")
                    )).Where(item => item.Season.All(char.IsAsciiDigit) && item.Season.Length > 0 &&
                        (item.Name == normalized || item.Original == normalized)));
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or JsonException)
                {
                    Diagnostics.Log($"哔哩媒体搜索不可用 ({type}): {ex.GetType().Name}");
                }
            }
            foreach (var candidate in candidates)
            {
                var season = await ReadApiAsync(site, "/pgc/view/web/season", token, ("season_id", candidate.Season)).ConfigureAwait(false);
                var episode = SelectEpisode(Array(season, "episodes"), episodeName, episodeIndex);
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
        foreach (var video in Array(videos, "result").Where(item =>
            SearchScoring.Normalize(PlaybackParsing.StripHtml(Text(item, "title"))) == normalized).Take(3))
        {
            var detail = await ReadApiAsync(site, "/x/web-interface/view", token, ("bvid", Text(video, "bvid"))).ConfigureAwait(false);
            var pages = Array(detail, "pages");
            var episode = SelectEpisode(pages, episodeName, episodeIndex);
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

    private static JsonElement SelectEpisode(JsonElement[] episodes, string name, int index)
    {
        var wanted = MediaMatching.EpisodeNumber(name) ?? index + 1;
        foreach (var episode in episodes)
            if (MediaMatching.EpisodeNumber(Text(episode, "title")) == wanted ||
                MediaMatching.EpisodeNumber(Text(episode, "index_title")) == wanted) return episode;
        return index >= 0 && index < episodes.Length ? episodes[index] : episodes.FirstOrDefault();
    }
}
