using System.Globalization;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native cinema trailer provider for 🚀荐影┃预告片 (csp_YGP / csp_YGPGuard).
/// Aggregates real Maoyan theater trailers (direct HD MP4 streams) and Douban coming trailers.
/// </summary>
public sealed class YgpVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private const string MaoyanBase = "https://m.maoyan.com/ajax";
    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = BrowserUa,
        ["Referer"] = "https://m.maoyan.com/"
    };

    private static readonly (string Id, string Name)[] Categories =
    [
        ("hot", "正在热映"),
        ("coming", "即将上映"),
        ("expected", "最受期待")
    ];

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_YGPGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_YGP", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var hot = await ReadHotAsync(site, 1, cancellationToken).ConfigureAwait(false);
        hot.HomeCategoryName = "热映预告";
        hot.Categories = Categories.Select(c => new VodCategory { Id = c.Id, Name = c.Name }).ToList();
        return hot;
    }

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        categoryId switch
        {
            "coming" => ReadComingAsync(site, page, cancellationToken),
            "expected" => ReadExpectedAsync(site, page, cancellationToken),
            _ => ReadHotAsync(site, page, cancellationToken)
        };

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse { Page = page, PageCount = 1 });

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var movieId = id.StartsWith("my:", StringComparison.OrdinalIgnoreCase) ? id[3..] : id;
        if (!movieId.All(char.IsAsciiDigit) || movieId.Length == 0)
            throw new InvalidDataException("预告片编号无效。");

        var payload = await http.GetBytesAsync($"{MaoyanBase}/detailmovie?movieId={movieId}", Headers,
            maxBytes: 1024 * 1024, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload.Content);
        if (!doc.RootElement.TryGetProperty("detailMovie", out var movie) || movie.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("未能获取影片预告详情。");

        var title = ReadString(movie, "nm");
        var img = NormalizePoster(ReadString(movie, "img"));
        var vd = ReadString(movie, "vd");
        var videoName = ReadString(movie, "videoName");
        var score = ReadScore(movie, "sc");
        var pubDate = ReadString(movie, "pubDesc");
        var year = pubDate.Length >= 4 && pubDate[..4].All(char.IsAsciiDigit) ? pubDate[..4] : string.Empty;
        var episodes = new List<string>();
        if (!string.IsNullOrWhiteSpace(vd))
        {
            var epLabel = string.IsNullOrWhiteSpace(videoName) ? "官方高清预告" : videoName.Replace("$", "＄").Replace("#", "＃");
            episodes.Add($"{epLabel}${vd}");
        }

        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title,
            Picture = img,
            SiteRating = score,
            Year = year,
            Area = ReadString(movie, "src"),
            TypeName = ReadString(movie, "cat").Replace(",", " / "),
            Director = ReadString(movie, "dir"),
            Actor = ReadString(movie, "star").Replace(",", " / "),
            Remarks = !string.IsNullOrWhiteSpace(pubDate) ? pubDate : (score.Length > 0 ? $"评分 {score}" : "影院预告"),
            Content = ReadString(movie, "dra"),
            PlayFrom = episodes.Count > 0 ? "官方高清预告" : string.Empty,
            PlayUrl = episodes.Count > 0 ? string.Join('#', episodes) : string.Empty
        };
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("预告片播放地址无效。");
        return Task.FromResult(new SpiderPlayResult(uri.AbsoluteUri, Headers, 0, 0, ReplaceHeaders: true));
    }

    private async Task<CmsResponse> ReadHotAsync(SourceSite site, int page, CancellationToken token)
    {
        page = Math.Max(1, page);
        var payload = await http.GetBytesAsync($"{MaoyanBase}/movieOnInfoList", Headers, maxBytes: 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload.Content);
        var root = doc.RootElement;
        var movieIds = root.TryGetProperty("movieIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
            ? idsEl.EnumerateArray().Select(e => e.ToString()).Where(s => s.Length > 0).ToArray()
            : [];

        const int pageSize = 12;
        if (page == 1 && root.TryGetProperty("movieList", out var firstList) && firstList.ValueKind == JsonValueKind.Array)
        {
            var items = ParseMaoyanArray(firstList, site);
            var total = Math.Max(movieIds.Length, items.Count);
            return new CmsResponse
            {
                List = items,
                Page = 1,
                PageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
                Limit = pageSize,
                Total = total
            };
        }

        var slice = movieIds.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        if (slice.Length == 0)
            return new CmsResponse { Page = page, PageCount = Math.Max(1, (int)Math.Ceiling(movieIds.Length / (double)pageSize)) };

        var morePayload = await http.GetBytesAsync($"{MaoyanBase}/moreComingList?movieIds={string.Join(',', slice)}", Headers,
            maxBytes: 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        using var moreDoc = JsonDocument.Parse(morePayload.Content);
        var list = moreDoc.RootElement.TryGetProperty("coming", out var coming) && coming.ValueKind == JsonValueKind.Array
            ? ParseMaoyanArray(coming, site)
            : [];
        return new CmsResponse
        {
            List = list,
            Page = page,
            PageCount = Math.Max(1, (int)Math.Ceiling(movieIds.Length / (double)pageSize)),
            Limit = pageSize,
            Total = movieIds.Length
        };
    }

    private async Task<CmsResponse> ReadComingAsync(SourceSite site, int page, CancellationToken token)
    {
        page = Math.Max(1, page);
        var payload = await http.GetBytesAsync($"{MaoyanBase}/comingList?token=&limit=12", Headers, maxBytes: 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload.Content);
        var root = doc.RootElement;
        var movieIds = root.TryGetProperty("movieIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
            ? idsEl.EnumerateArray().Select(e => e.ToString()).Where(s => s.Length > 0).ToArray()
            : [];

        const int pageSize = 12;
        if (page == 1 && root.TryGetProperty("coming", out var firstList) && firstList.ValueKind == JsonValueKind.Array)
        {
            var items = ParseMaoyanArray(firstList, site);
            var total = Math.Max(movieIds.Length, items.Count);
            return new CmsResponse
            {
                List = items,
                Page = 1,
                PageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
                Limit = pageSize,
                Total = total
            };
        }

        var slice = movieIds.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        if (slice.Length == 0)
            return new CmsResponse { Page = page, PageCount = Math.Max(1, (int)Math.Ceiling(movieIds.Length / (double)pageSize)) };

        var morePayload = await http.GetBytesAsync($"{MaoyanBase}/moreComingList?movieIds={string.Join(',', slice)}", Headers,
            maxBytes: 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        using var moreDoc = JsonDocument.Parse(morePayload.Content);
        var list = moreDoc.RootElement.TryGetProperty("coming", out var coming) && coming.ValueKind == JsonValueKind.Array
            ? ParseMaoyanArray(coming, site)
            : [];
        return new CmsResponse
        {
            List = list,
            Page = page,
            PageCount = Math.Max(1, (int)Math.Ceiling(movieIds.Length / (double)pageSize)),
            Limit = pageSize,
            Total = movieIds.Length
        };
    }

    private async Task<CmsResponse> ReadExpectedAsync(SourceSite site, int page, CancellationToken token)
    {
        page = Math.Max(1, page);
        var offset = (page - 1) * 12;
        var payload = await http.GetBytesAsync($"{MaoyanBase}/mostExpected?limit=12&offset={offset}&token=", Headers,
            maxBytes: 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload.Content);
        var root = doc.RootElement;
        var list = root.TryGetProperty("coming", out var coming) && coming.ValueKind == JsonValueKind.Array
            ? ParseMaoyanArray(coming, site)
            : [];
        var total = root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv)
            ? tv
            : list.Count;
        return new CmsResponse
        {
            List = list,
            Page = page,
            PageCount = Math.Max(1, (int)Math.Ceiling(total / 12d)),
            Limit = 12,
            Total = total
        };
    }

    private static List<VodItem> ParseMaoyanArray(JsonElement array, SourceSite site)
    {
        var items = new List<VodItem>();
        foreach (var entry in array.EnumerateArray())
        {
            var id = entry.TryGetProperty("id", out var idEl) ? idEl.ToString() : string.Empty;
            var name = ReadString(entry, "nm");
            if (id.Length == 0 || name.Length == 0) continue;
            var score = ReadScore(entry, "sc");
            var rt = ReadString(entry, "rt");
            var comingTitle = ReadString(entry, "comingTitle");
            var wish = entry.TryGetProperty("wish", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
            var remarks = score.Length > 0
                ? $"评分 {score}"
                : !string.IsNullOrWhiteSpace(rt) ? $"{rt} 上映"
                : !string.IsNullOrWhiteSpace(comingTitle) ? comingTitle
                : wish > 0 ? $"{wish}人想看" : "新片预告";
            items.Add(new VodItem
            {
                Id = "my:" + id,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Picture = NormalizePoster(ReadString(entry, "img")),
                SiteRating = score,
                Year = rt.Length >= 4 && rt[..4].All(char.IsAsciiDigit) ? rt[..4] : string.Empty,
                Remarks = remarks
            });
        }
        return items;
    }

    private static string NormalizePoster(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return url.Replace("/w.h/", "/300.420/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string ReadScore(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var val)) return string.Empty;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var d) && d > 0)
            return d.ToString("0.0", CultureInfo.InvariantCulture);
        if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), CultureInfo.InvariantCulture, out var sd) && sd > 0)
            return sd.ToString("0.0", CultureInfo.InvariantCulture);
        return string.Empty;
    }
}
