using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Playback;

// MD5 为爱壹帆接口的协议签名要求（vv 校验），非安全用途，豁免弱哈希告警。
#pragma warning disable CA5351

/// <summary>爱壹帆（yfsp.tv）聚合弹幕源：标题搜索 → 分集 → 弹幕，签名口径与 dango-manifests/aiyifan 一致。</summary>
internal sealed class AiyifanDanmakuSource(SourceHttpClient http) : IAggregateDanmakuSource
{
    public string Id => "aiyifan";
    public string DisplayName => "爱壹帆";

    private const string DesktopUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0 Safari/537.36";
    private const string HomePage = "https://www.yfsp.tv/";
    private static readonly TimeSpan ConfigLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private (string PublicKey, string PrivateKey)? _config;

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

    private async Task<(string PublicKey, string PrivateKey)> GetConfigAsync(CancellationToken token)
    {
        if (_config is { } cached) return cached;
        await _configGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_config is { } fresh) return fresh;
            var page = await GetStringAsync(HomePage, token).ConfigureAwait(false);
            var publicKey = Regex.Match(page, "\"pConfig\"\\s*:\\s*\\{\\s*\"publicKey\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            var privateKey = Regex.Match(page, "\"privateKey\"\\s*:\\s*\\[\\s*\"([^\"]+)\"").Groups[1].Value;
            if (publicKey.Length == 0 || privateKey.Length == 0)
                throw new InvalidDataException("无法从爱壹帆页面提取签名密钥。");
            _config = (publicKey, privateKey);
            _ = Task.Run(async () =>
            {
                await Task.Delay(ConfigLifetime).ConfigureAwait(false);
                _config = null;
            });
            return _config.Value;
        }
        finally { _configGate.Release(); }
    }

    private static string Signature(string publicKey, string canon, string privateKey) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(publicKey + "&" + canon.ToLowerInvariant() + "&" + privateKey))).ToLowerInvariant();

    private async Task<string> GetStringAsync(string url, CancellationToken token) =>
        Encoding.UTF8.GetString((await http.GetBytesAsync(url, new Dictionary<string, string>
        {
            ["User-Agent"] = DesktopUa
        }, cancellationToken: token).ConfigureAwait(false)).Content);

    private static string BuildQuery(IEnumerable<(string Key, string Value)> pairs) => string.Join('&',
        pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private async Task<List<AggregateSeries>> SearchAsync(string query, CancellationToken token)
    {
        var (publicKey, privateKey) = await GetConfigAsync(token).ConfigureAwait(false);
        var canon = $"tags={query}&orderby=4&page=1&size=10&desc=1&isserial=-1";
        var queryText = BuildQuery(
        [
            ("tags", query), ("orderby", "4"), ("page", "1"), ("size", "10"), ("desc", "1"),
            ("isserial", "-1"), ("vv", Signature(publicKey, canon, privateKey)), ("pub", publicKey)
        ]);
        var payload = await GetStringAsync("https://rankv21.tripdata.app/v3/list/briefsearch?" + queryText, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var result = new List<AggregateSeries>();
        foreach (var item in ReadArray(document.RootElement, "data", "info", "result"))
        {
            var id = Text(item, "contxt");
            var title = PlaybackParsing.StripHtml(Text(item, "title"));
            if (id.Length == 0 || title.Length == 0) continue;
            var year = Text(item, "postTime");
            result.Add(new AggregateSeries(id, title, null, year.Length >= 4 ? year[..4] : null));
        }
        return result;
    }

    private async Task<List<AggregateEpisode>> LoadEpisodesAsync(string vid, CancellationToken token)
    {
        var (publicKey, privateKey) = await GetConfigAsync(token).ConfigureAwait(false);
        var canon = $"cinema=1&vid={vid}&lsk=1&taxis=0&cid=0,1,4,152";
        var queryText = BuildQuery(
        [
            ("cinema", "1"), ("vid", vid), ("lsk", "1"), ("taxis", "0"), ("cid", "0,1,4,152"),
            ("vv", Signature(publicKey, canon, privateKey)), ("pub", publicKey)
        ]);
        var payload = await GetStringAsync("https://m10.yfsp.tv/v3/video/languagesplaylist?" + queryText, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var episodes = new List<AggregateEpisode>();
        var playList = document.RootElement;
        foreach (var segment in (string[])["data", "info", "playList"])
        {
            if (playList.ValueKind != JsonValueKind.Object || !playList.TryGetProperty(segment, out playList)) return episodes;
        }
        if (playList.ValueKind != JsonValueKind.Object) return episodes;
        var index = 0;
        foreach (var property in playList.EnumerateObject())
        {
            index++;
            var entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = Text(entry, "name");
            var number = int.TryParse(Text(entry, "id"), out var parsed) ? parsed : (int?)null;
            episodes.Add(new AggregateEpisode(property.Name,
                name.Length > 0 ? name : $"第{number ?? index}集", number));
        }
        return episodes;
    }

    private async Task<DanmakuEntry[]> LoadCommentsAsync(string epKey, CancellationToken token)
    {
        var (publicKey, privateKey) = await GetConfigAsync(token).ConfigureAwait(false);
        var playCanon = $"cinema=1&id={epKey}&a=0&lang=none&usersign=1&region=GL.&device=0&isMasterSupport=1";
        var playQuery = BuildQuery(
        [
            ("cinema", "1"), ("id", epKey), ("a", "0"), ("lang", "none"), ("usersign", "1"),
            ("region", "GL."), ("device", "0"), ("isMasterSupport", "1"),
            ("vv", Signature(publicKey, playCanon, privateKey)), ("pub", publicKey)
        ]);
        var playPayload = await GetStringAsync("https://m10.yfsp.tv/v3/video/play?" + playQuery, token).ConfigureAwait(false);
        using var playDocument = JsonDocument.Parse(playPayload);
        var uniqueKey = string.Empty;
        if (playDocument.RootElement.TryGetProperty("data", out var playData) &&
            playData.TryGetProperty("info", out var playInfo) && playInfo.ValueKind == JsonValueKind.Array &&
            playInfo.GetArrayLength() > 0 && playInfo[0].TryGetProperty("uniqueKey", out var unique))
            uniqueKey = unique.GetString() ?? string.Empty;
        if (uniqueKey.Length == 0) throw new DanmakuNotMatchedException("未取得分集弹幕标识。");

        var barCanon = $"cinema=1&page=1&size=30000&uniqueKey={uniqueKey}";
        var barQuery = BuildQuery(
        [
            ("cinema", "1"), ("page", "1"), ("size", "30000"), ("uniqueKey", uniqueKey),
            ("vv", Signature(publicKey, barCanon, privateKey)), ("pub", publicKey)
        ]);
        var barPayload = await GetStringAsync("https://m10.yfsp.tv/api/video/getBarrage?" + barQuery, token).ConfigureAwait(false);
        using var barDocument = JsonDocument.Parse(barPayload);
        var comments = new List<DanmakuEntry>();
        foreach (var item in ReadArray(barDocument.RootElement, "data", "info"))
        {
            var text = Text(item, "contxt") is { Length: > 0 } primary ? primary : Text(item, "content");
            if (text.Length == 0) continue;
            var seconds = double.TryParse(Text(item, "second"), out var parsed) ? parsed : 0d;
            var position = int.TryParse(Text(item, "position"), out var pos) ? pos : 0;
            AggregateDanmakuMatching.TryParseColor(Text(item, "color"), out var rgb);
            comments.Add(new DanmakuEntry(seconds, text, rgb,
                AggregateDanmakuMatching.ModeOf(position == 1 ? 5 : 1)));
            if (comments.Count >= 30_000) break;
        }
        return [.. comments];
    }

    private static JsonElement[] ReadArray(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return [];
        }
        return current.ValueKind == JsonValueKind.Array ? [.. current.EnumerateArray()] : [];
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty : string.Empty;
}
