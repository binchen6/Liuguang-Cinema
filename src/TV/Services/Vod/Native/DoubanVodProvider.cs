using System.Globalization;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native Douban catalog & detail provider for 豆豆┃片单 (csp_DouDou / csp_DouDouGuard).
/// Restores real Douban Frodo API lists, filters, subject metadata, and playable official HD trailers.
/// </summary>
public sealed class DoubanVodProvider(SourceHttpClient http) : ICategoryFilterVodProvider, INativePlaybackProvider
{
    private const string FrodoApi = "https://frodo.douban.com/api/v2";
    private const string ApiKey = "apikey=0ac44ae016490db2204ce0a042db2916";
    private const string MiniProgramUa = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/53.0.2785.143 Safari/537.36 MicroMessenger/7.0.9.501 NetType/WIFI MiniProgramEnv/Windows WindowsWechat";
    private const string MiniProgramReferer = "https://servicewechat.com/wx2f9b06c1de1ccfca/84/page-frame.html";

    private static readonly Dictionary<string, string> FrodoHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Referer"] = MiniProgramReferer,
        ["User-Agent"] = MiniProgramUa
    };

    private static readonly (string Id, string Name)[] Categories =
    [
        ("hot_gaia", "热门电影"),
        ("tv_hot", "热播剧集"),
        ("show_hot", "热播综艺"),
        ("movie", "电影筛选"),
        ("tv", "电视筛选"),
        ("rank_list_movie", "电影榜单"),
        ("rank_list_tv", "电视剧榜单")
    ];

    private static readonly JsonElement DefaultFilters = JsonDocument.Parse("""
        {
          "hot_gaia": [
            {"key":"sort","name":"排序","value":[{"n":"热度","v":"recommend"},{"n":"最新","v":"time"},{"n":"高分","v":"rank"}]},
            {"key":"area","name":"地区","value":[{"n":"全部","v":"全部"},{"n":"华语","v":"华语"},{"n":"欧美","v":"欧美"},{"n":"韩国","v":"韩国"},{"n":"日本","v":"日本"}]}
          ],
          "tv_hot": [
            {"key":"type","name":"分类","value":[{"n":"综合","v":"tv_hot"},{"n":"国产剧","v":"tv_domestic"},{"n":"欧美剧","v":"tv_american"},{"n":"日剧","v":"tv_japanese"},{"n":"韩剧","v":"tv_korean"},{"n":"动画","v":"tv_animation"},{"n":"纪录片","v":"tv_documentary"}]}
          ],
          "show_hot": [
            {"key":"type","name":"分类","value":[{"n":"综合","v":"show_hot"},{"n":"国内综艺","v":"show_domestic"},{"n":"国外综艺","v":"show_foreign"}]}
          ],
          "rank_list_movie": [
            {"key":"榜单","name":"榜单","value":[{"n":"实时热门电影","v":"movie_real_time_hotest"},{"n":"一周口碑电影","v":"movie_weekly_best"},{"n":"豆瓣Top250","v":"movie_top250"}]}
          ],
          "rank_list_tv": [
            {"key":"榜单","name":"榜单","value":[{"n":"实时热门剧集","v":"tv_real_time_hotest"},{"n":"华语口碑剧集","v":"tv_chinese_best_weekly"},{"n":"全球口碑剧集","v":"tv_global_best_weekly"}]}
          ]
        }
        """).RootElement.Clone();

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_DouDouGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_DouDou", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Douban", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var url = $"{FrodoApi}/subject_collection/subject_real_time_hotest/items?{ApiKey}&start=0&count=20";
        var (items, total) = await ReadCollectionAsync(url, "subject_collection_items", site, cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            HomeCategoryName = "实时热榜",
            Categories = Categories.Select(c => new VodCategory { Id = c.Id, Name = c.Name }).ToList(),
            List = items,
            Filters = DefaultFilters,
            Page = 1,
            PageCount = Math.Max(1, (int)Math.Ceiling(total / 20d)),
            Limit = 20,
            Total = total
        };
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetCategoryAsync(site, categoryId, page, null, cancellationToken);

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        var start = (page - 1) * 20;
        var ext = filters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sort = ext.TryGetValue("sort", out var s) && !string.IsNullOrWhiteSpace(s) ? s : "T";
        var tags = Uri.EscapeDataString(string.Join(',', ext.Where(p => !p.Key.Equals("sort", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Value)));

        string url;
        var itemKey = "items";
        switch (categoryId)
        {
            case "hot_gaia":
                var gaiaSort = ext.TryGetValue("sort", out var gs) && !string.IsNullOrWhiteSpace(gs) ? gs : "recommend";
                var area = ext.TryGetValue("area", out var ga) && !string.IsNullOrWhiteSpace(ga) ? ga : "全部";
                url = $"{FrodoApi}/movie/hot_gaia?{ApiKey}&sort={Uri.EscapeDataString(gaiaSort)}&area={Uri.EscapeDataString(area)}&start={start}&count=20";
                break;
            case "tv_hot":
                var tvType = ext.TryGetValue("type", out var tt) && !string.IsNullOrWhiteSpace(tt) ? tt : "tv_hot";
                url = $"{FrodoApi}/subject_collection/{Uri.EscapeDataString(tvType)}/items?{ApiKey}&start={start}&count=20";
                itemKey = "subject_collection_items";
                break;
            case "show_hot":
                var showType = ext.TryGetValue("type", out var st) && !string.IsNullOrWhiteSpace(st) ? st : "show_hot";
                url = $"{FrodoApi}/subject_collection/{Uri.EscapeDataString(showType)}/items?{ApiKey}&start={start}&count=20";
                itemKey = "subject_collection_items";
                break;
            case "tv":
                url = $"{FrodoApi}/tv/recommend?{ApiKey}&sort={Uri.EscapeDataString(sort)}&tags={tags}&start={start}&count=20";
                break;
            case "rank_list_movie":
                var rm = ext.TryGetValue("榜单", out var rmt) && !string.IsNullOrWhiteSpace(rmt) ? rmt : "movie_real_time_hotest";
                url = $"{FrodoApi}/subject_collection/{Uri.EscapeDataString(rm)}/items?{ApiKey}&start={start}&count=20";
                itemKey = "subject_collection_items";
                break;
            case "rank_list_tv":
                var rt = ext.TryGetValue("榜单", out var rtt) && !string.IsNullOrWhiteSpace(rtt) ? rtt : "tv_real_time_hotest";
                url = $"{FrodoApi}/subject_collection/{Uri.EscapeDataString(rt)}/items?{ApiKey}&start={start}&count=20";
                itemKey = "subject_collection_items";
                break;
            default:
                url = $"{FrodoApi}/movie/recommend?{ApiKey}&sort={Uri.EscapeDataString(sort)}&tags={tags}&start={start}&count=20";
                break;
        }

        var (items, total) = await ReadCollectionAsync(url, itemKey, site, cancellationToken).ConfigureAwait(false);
        var pageCount = total > 0 ? Math.Max(1, (int)Math.Ceiling(total / 20d)) : (items.Count >= 20 ? page + 1 : page);
        return new CmsResponse
        {
            List = items,
            Page = page,
            PageCount = pageCount,
            Limit = 20,
            Total = total > 0 ? total : items.Count
        };
    }

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var subjectId = ExtractSubjectId(id);
        if (subjectId.Length == 0) throw new InvalidDataException("豆瓣片目 ID 无效。");

        JsonDocument? doc = null;
        foreach (var kind in new[] { "movie", "tv" })
        {
            try
            {
                var payload = await http.GetBytesAsync($"{FrodoApi}/{kind}/{subjectId}?{ApiKey}", FrodoHeaders,
                    maxBytes: 1024 * 1024, cancellationToken: cancellationToken).ConfigureAwait(false);
                var parsed = JsonDocument.Parse(payload.Content);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                    parsed.RootElement.TryGetProperty("title", out _))
                {
                    doc = parsed;
                    break;
                }
                parsed.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        if (doc is null) throw new InvalidDataException("豆瓣未能返回影片详情。");
        using (doc)
        {
            var root = doc.RootElement;
            var title = ReadString(root, "title");
            var pic = ReadPic(root);
            var rating = ReadRating(root);
            var year = ReadString(root, "year");
            var genres = string.Join(" / ", ReadStringArray(root, "genres"));
            var countries = string.Join(" / ", ReadStringArray(root, "countries"));
            var directors = string.Join(" / ", ReadNamedArray(root, "directors"));
            var actors = string.Join(" / ", ReadNamedArray(root, "actors").Take(12));
            var intro = ReadString(root, "intro");

            var episodes = new List<string>();
            if (root.TryGetProperty("trailers", out var trailers) && trailers.ValueKind == JsonValueKind.Array)
            {
                var idx = 0;
                foreach (var tr in trailers.EnumerateArray())
                {
                    var videoUrl = ReadString(tr, "video_url");
                    if (string.IsNullOrWhiteSpace(videoUrl)) continue;
                    idx++;
                    var label = ReadString(tr, "title");
                    if (string.IsNullOrWhiteSpace(label)) label = $"官方预告 {idx}";
                    label = label.Replace("$", "＄", StringComparison.Ordinal).Replace("#", "＃", StringComparison.Ordinal);
                    episodes.Add($"{label}${videoUrl}");
                }
            }
            if (episodes.Count == 0 && root.TryGetProperty("trailer", out var single) && single.ValueKind == JsonValueKind.Object)
            {
                var videoUrl = ReadString(single, "video_url");
                if (!string.IsNullOrWhiteSpace(videoUrl)) episodes.Add($"官方预告${videoUrl}");
            }

            var item = new VodItem
            {
                Id = id,
                DoubanId = subjectId,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = title,
                Picture = pic,
                SiteRating = rating,
                Year = year,
                Area = countries,
                TypeName = genres,
                Director = directors,
                Actor = actors,
                Remarks = rating.Length > 0 ? $"豆瓣 {rating}" : "豆瓣片单",
                Content = string.IsNullOrWhiteSpace(intro) ? "可点击「查找换源」跨站检索正片播放线路。" : intro,
                PlayFrom = episodes.Count > 0 ? "豆瓣官方预告" : string.Empty,
                PlayUrl = episodes.Count > 0 ? string.Join('#', episodes) : string.Empty
            };
            return item;
        }
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("豆瓣预告片地址无效。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
            ["Referer"] = "https://movie.douban.com/"
        };
        return Task.FromResult(new SpiderPlayResult(uri.AbsoluteUri, headers, 0, 0, ReplaceHeaders: true) { UseHttpProxy = true });
    }

    private async Task<(List<VodItem> Items, int Total)> ReadCollectionAsync(string url, string itemKey, SourceSite site, CancellationToken token)
    {
        var payload = await http.GetBytesAsync(url, FrodoHeaders, maxBytes: 2 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload.Content);
        var root = doc.RootElement;
        var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var totalVal) ? totalVal : 0;
        var list = new List<VodItem>();
        if (!root.TryGetProperty(itemKey, out var array) || array.ValueKind != JsonValueKind.Array)
            return (list, total);

        foreach (var entry in array.EnumerateArray())
        {
            var id = ReadString(entry, "id");
            var title = ReadString(entry, "title");
            if (id.Length == 0 || title.Length == 0) continue;
            var rating = ReadRating(entry);
            list.Add(new VodItem
            {
                Id = "msearch:" + id,
                DoubanId = id,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = title,
                Picture = ReadPic(entry),
                SiteRating = rating,
                Year = ReadString(entry, "year"),
                Remarks = rating.Length > 0 ? $"评分 {rating}" : ReadString(entry, "card_subtitle")
            });
        }
        return (list, total);
    }

    private static string ExtractSubjectId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        var clean = id.StartsWith("msearch:", StringComparison.OrdinalIgnoreCase) ? id[8..] : id;
        return new string(clean.Trim().TakeWhile(char.IsAsciiDigit).ToArray());
    }

    private static string ReadString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string ReadRating(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!rating.TryGetProperty("value", out var v)) return string.Empty;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > 0)
            return d.ToString("0.0", CultureInfo.InvariantCulture);
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var sd) && sd > 0)
            return sd.ToString("0.0", CultureInfo.InvariantCulture);
        return string.Empty;
    }

    private static string ReadPic(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return string.Empty;
        if (obj.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.Object)
        {
            var normal = ReadString(pic, "large");
            if (normal.Length == 0) normal = ReadString(pic, "normal");
            if (normal.Length > 0) return normal;
        }
        if (obj.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object)
        {
            var url = ReadString(cover, "url");
            if (url.Length > 0) return url;
        }
        return ReadString(obj, "cover_url");
    }

    private static IEnumerable<string> ReadStringArray(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                yield return item.GetString()!.Trim();
        }
    }

    private static IEnumerable<string> ReadNamedArray(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in arr.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (name.Length > 0) yield return name;
        }
    }
}
