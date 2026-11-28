using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Playback;

// MD5 为人人视频接口的协议签名要求（sign 校验），非安全用途，豁免弱哈希告警。
#pragma warning disable CA5351

/// <summary>人人视频聚合弹幕源：标题搜索 → 分集 → 弹幕，签名口径与 dango-manifests/renren 一致。</summary>
internal sealed class RenrenDanmakuSource(SourceHttpClient http) : IAggregateDanmakuSource
{
    public string Id => "renren";
    public string DisplayName => "人人";

    private const string Salt = "cf65GPholnICgyw1xbrpA79XVkizOdMq";
    private const string ApiBase = "https://api.gorafie.com";
    private const string DanmakuBase = "https://static-dm.qwdjapp.com";

    public async Task<AggregateDanmakuResult> MatchAndLoadAsync(MediaIdentity identity, string episodeName, int episodeIndex, CancellationToken token)
    {
        var wanted = identity.CleanTitle.Length >= 2 ? identity.CleanTitle : identity.BaseTitle;
        if (wanted.Length < 2) throw new DanmakuNotMatchedException("无法从当前标题识别节目名称。");
        var queries = new List<string> { wanted };
        if (identity.BaseTitle.Length >= 2 && !identity.BaseTitle.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            queries.Add(identity.BaseTitle);

        AggregateSeries? series = null;
        var bestScore = 0d;
        foreach (var query in queries)
        {
            foreach (var candidate in await SearchAsync(query, token).ConfigureAwait(false))
            {
                var score = AggregateDanmakuMatching.ScoreSeries(identity, candidate.Title, out _);
                if (score > bestScore)
                {
                    bestScore = score;
                    series = candidate;
                }
            }
            if (bestScore >= AggregateDanmakuMatching.StrongMatchScore) break;
        }
        if (series is null || bestScore < AggregateDanmakuMatching.MinTitleRelevanceScore)
            throw new DanmakuNotMatchedException("没有标题相符的节目。");

        var episodes = await LoadEpisodesAsync(series.Id, token).ConfigureAwait(false);
        var episode = AggregateDanmakuMatching.SelectEpisode(episodes, identity, episodeName, episodeIndex)
            ?? throw new DanmakuNotMatchedException("节目下没有可用的分集。");
        var comments = await LoadCommentsAsync(episode.Id, token).ConfigureAwait(false);
        if (comments.Length == 0) throw new DanmakuNotMatchedException("该分集暂无弹幕。");
        return new AggregateDanmakuResult(DisplayName, series.Title, episode.Title, comments);
    }

    private static IReadOnlyDictionary<string, string> BaseHeaders(string timestamp, string sign) => new Dictionary<string, string>
    {
        ["clientVersion"] = "1.2.2",
        ["p"] = "Android",
        ["deviceid"] = "tWEtIN7JG2DTDkBBigvj6A%3D%3D",
        ["token"] = "",
        ["aliid"] = "aYN4D0XfSREDAJaw3UAjG33K",
        ["umid"] = "",
        ["clienttype"] = "android_qwtv_RRSP",
        ["pkt"] = "rrmj",
        ["t"] = timestamp,
        ["sign"] = sign,
        ["isAgree"] = "1",
        ["et"] = "2",
        ["User-Agent"] = "okhttp/3.12.13"
    };

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task<string> GetStringAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        Encoding.UTF8.GetString((await http.GetBytesAsync(url, headers, cancellationToken: token).ConfigureAwait(false)).Content);

    private async Task<List<AggregateSeries>> SearchAsync(string query, CancellationToken token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = Md5Hex("/qwtv/search" + "t" + timestamp + "num" + "30" + "searchNext" + "" +
            "searchWord" + query + "well" + "match" + Salt);
        var payload = await GetStringAsync(
            ApiBase + "/qwtv/search?searchWord=" + Uri.EscapeDataString(query) + "&num=30&searchNext=&well=match",
            BaseHeaders(timestamp, sign), token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var result = new List<AggregateSeries>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var id = Text(item, "id");
            var title = PlaybackParsing.StripHtml(Text(item, "title"));
            if (id.Length == 0 || title.Length == 0) continue;
            result.Add(new AggregateSeries(id, title, null, Text(item, "year")));
        }
        return result;
    }

    private async Task<List<AggregateEpisode>> LoadEpisodesAsync(string seriesId, CancellationToken token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = Md5Hex("/qwtv/drama/details" + "t" + timestamp +
            "caption" + "0" + "clarity" + "HD" + "episodeId" + "" + "hevcOpen" + "1" +
            "isAgeLimit" + "false" + "seriesId" + seriesId + Salt);
        var payload = await GetStringAsync(
            ApiBase + "/qwtv/drama/details?isAgeLimit=false&seriesId=" + Uri.EscapeDataString(seriesId) +
            "&episodeId=&clarity=HD&caption=0&hevcOpen=1",
            BaseHeaders(timestamp, sign), token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var episodes = new List<AggregateEpisode>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("episodeList", out var list) || list.ValueKind != JsonValueKind.Array) return episodes;
        foreach (var item in list.EnumerateArray())
        {
            var sid = Text(item, "sid");
            if (sid.Length == 0) continue;
            var title = Text(item, "title");
            var number = int.TryParse(Text(item, "episodeNo"), out var parsed) ? parsed : (int?)null;
            episodes.Add(new AggregateEpisode(sid,
                title.Length > 0 ? title : $"第{number ?? episodes.Count + 1}集", number));
        }
        return episodes;
    }

    private async Task<DanmakuEntry[]> LoadCommentsAsync(string sid, CancellationToken token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var path = "/v1/produce/danmu/EPISODE/" + sid;
        var sign = Md5Hex(path + "t" + timestamp + Salt);
        var payload = await GetStringAsync(DanmakuBase + path, BaseHeaders(timestamp, sign), token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var list = document.RootElement;
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("data", out var data)) list = data;
        if (list.ValueKind != JsonValueKind.Array) return [];
        var comments = new List<DanmakuEntry>();
        foreach (var item in list.EnumerateArray())
        {
            var text = Text(item, "d");
            var meta = Text(item, "p");
            if (text.Length == 0 || meta.Length == 0) continue;
            var parts = meta.Split(',');
            if (parts.Length < 7) continue;
            var seconds = double.TryParse(parts[0], out var parsed) ? parsed : 0d;
            // dango renren 契约：原生 p 为 time,?,?,color,?,?,id —— 颜色在 parts[3]，模式固定滚动
            AggregateDanmakuMatching.TryParseColor(parts[3], out var rgb);
            comments.Add(new DanmakuEntry(seconds, text, rgb, DanmakuMode.Scroll));
            if (comments.Count >= 30_000) break;
        }
        return [.. comments];
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Number)
        ? value.ToString() ?? string.Empty : string.Empty;
}
