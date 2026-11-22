using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 💮文采┃秒播 (csp_Jpys / csp_JpysGuard) using the signed JSON API.
/// </summary>
public sealed class JpysVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private const string SignKey = "cb808529bae6b6be45ecfab29a4889bc";
    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private static readonly Uri[] Origins =
    [
        new("https://www.hellorlgzf7sn.com/"),
        new("https://www.cfkj86.com/")
    ];

    private readonly string _deviceId = Guid.NewGuid().ToString();

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_JpysGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Jpys", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var categories = new List<VodCategory>();
        using (var typeDoc = await GetApiAsync("/api/mw-movie/anonymous/get/filer/type", null, cancellationToken).ConfigureAwait(false))
        {
            if (typeDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    var id = ReadString(entry, "typeId");
                    var name = ReadString(entry, "typeName");
                    if (id.Length > 0 && name.Length > 0 && !name.Contains("伦理", StringComparison.Ordinal))
                        categories.Add(new VodCategory { Id = id, Name = name });
                }
            }
        }
        if (categories.Count == 0)
        {
            categories.AddRange([
                new VodCategory { Id = "1", Name = "电影" },
                new VodCategory { Id = "2", Name = "电视剧" },
                new VodCategory { Id = "3", Name = "综艺" },
                new VodCategory { Id = "4", Name = "动漫" }
            ]);
        }

        var firstCat = categories[0].Id;
        var list = await GetCategoryAsync(site, firstCat, 1, cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            HomeCategoryName = "最新推荐",
            Categories = categories,
            List = list.List,
            Page = 1,
            PageCount = list.PageCount,
            Total = list.Total
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["pageNum"] = page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = "24",
            ["type1"] = categoryId
        };
        using var doc = await GetApiAsync("/api/mw-movie/anonymous/video/list", query, cancellationToken).ConfigureAwait(false);
        return ParsePagedList(doc.RootElement, site, page);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["keyword"] = keyword,
            ["pageNum"] = page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = "24"
        };
        using var doc = await GetApiAsync("/api/mw-movie/anonymous/video/searchByWord", query, cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var container = data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result : data;
            return ParsePagedContainer(container, site, page);
        }
        return new CmsResponse { Page = page, PageCount = page };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["id"] = id };
        using var doc = await GetApiAsync("/api/mw-movie/anonymous/video/detail", query, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("文采详情返回空数据。");

        var vodId = ReadString(data, "vodId");
        if (vodId.Length == 0) vodId = id;
        var episodes = new List<string>();
        if (data.TryGetProperty("episodeList", out var epArr) && epArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in epArr.EnumerateArray())
            {
                var nid = ReadString(ep, "nid");
                var name = ReadString(ep, "name");
                if (nid.Length == 0) continue;
                if (name.Length == 0) name = $"第{episodes.Count + 1}集";
                episodes.Add($"{SanitizeLabel(name)}${vodId}|{nid}");
            }
        }

        return new VodItem
        {
            Id = vodId,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = ReadString(data, "vodName"),
            Picture = ReadString(data, "vodPic"),
            TypeName = ReadString(data, "typeName"),
            Remarks = FirstNonEmpty(ReadString(data, "vodRemarks"), ReadString(data, "vodVersion")),
            Year = ReadString(data, "vodYear"),
            Area = ReadString(data, "vodArea"),
            Actor = ReadString(data, "vodActor"),
            Director = ReadString(data, "vodDirector"),
            Content = ReadString(data, "vodContent"),
            PlayFrom = episodes.Count > 0 ? "文采秒播" : string.Empty,
            PlayUrl = string.Join('#', episodes)
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var parts = id.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new InvalidDataException("文采播放参数无效。");

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["clientType"] = "1",
            ["id"] = parts[0],
            ["nid"] = parts[1]
        };
        using var doc = await GetApiAsync("/api/mw-movie/anonymous/v2/video/episode/url", query, token).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("文采播放地址解析失败。");

        string? bestUrl = null;
        var bestResolution = -1;
        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                var url = ReadString(entry, "url");
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                var resolution = entry.TryGetProperty("resolution", out var rEl) && rEl.TryGetInt32(out var r) ? r : 0;
                if (resolution > bestResolution)
                {
                    bestResolution = resolution;
                    bestUrl = url;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(bestUrl))
            throw new InvalidDataException("文采未返回有效播放流地址。");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa
        };
        return new SpiderPlayResult(bestUrl, headers, 0, 0);
    }

    private async Task<JsonDocument> GetApiAsync(string path, SortedDictionary<string, string>? query, CancellationToken token)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var rawQs = query is { Count: > 0 }
            ? string.Join('&', query.Select(kv => $"{kv.Key}={kv.Value}"))
            : string.Empty;
        var encodedQs = query is { Count: > 0 }
            ? string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))
            : string.Empty;

        var signInput = (rawQs.Length > 0 ? rawQs + "&" : string.Empty) + $"key={SignKey}&t={t}";
        var md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signInput))).ToLowerInvariant();
        var sign = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(md5Hex))).ToLowerInvariant();

        Exception? lastError = null;
        foreach (var origin in Origins)
        {
            var url = new Uri(origin, path + (encodedQs.Length > 0 ? "?" + encodedQs : string.Empty)).AbsoluteUri;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = BrowserUa,
                ["Referer"] = origin.AbsoluteUri,
                ["t"] = t,
                ["sign"] = sign,
                ["deviceid"] = _deviceId
            };
            try
            {
                var exchange = await http.GetExchangeAsync(url, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
                if ((int)exchange.StatusCode is >= 200 and < 300 && exchange.Payload.Content.Length > 2)
                {
                    var doc = JsonDocument.Parse(exchange.Payload.Content);
                    if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var code) && code != 200)
                    {
                        var msg = ReadString(doc.RootElement, "msg");
                        doc.Dispose();
                        lastError = new InvalidDataException($"文采接口返回错误({code})：{msg}");
                        continue;
                    }
                    return doc;
                }
                lastError = new HttpRequestException($"文采接口返回 HTTP {(int)exchange.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException("文采站点当前无法访问。");
    }

    private static CmsResponse ParsePagedList(JsonElement root, SourceSite site, int page) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? ParsePagedContainer(data, site, page)
            : new CmsResponse { Page = page, PageCount = page };

    private static CmsResponse ParsePagedContainer(JsonElement container, SourceSite site, int page)
    {
        var items = new List<VodItem>();
        if (container.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                var id = ReadString(entry, "vodId");
                var name = ReadString(entry, "vodName");
                if (id.Length == 0 || name.Length == 0) continue;
                var score = ReadString(entry, "vodDoubanScore");
                var remarks = FirstNonEmpty(ReadString(entry, "vodRemarks"), ReadString(entry, "vodVersion"));
                if (remarks.Length == 0 && score.Length > 0 && score != "0" && score != "0.0")
                    remarks = $"{score}分";
                items.Add(new VodItem
                {
                    Id = id,
                    SiteKey = site.Key,
                    SiteName = site.Name,
                    Name = name,
                    Picture = ReadString(entry, "vodPic"),
                    Remarks = remarks,
                    Year = ReadString(entry, "vodYear"),
                    Area = ReadString(entry, "vodArea"),
                    Actor = ReadString(entry, "vodActor"),
                    Director = ReadString(entry, "vodDirector"),
                    TypeName = ReadString(entry, "vodClass")
                });
            }
        }
        var total = container.TryGetProperty("totalCount", out var tc) && tc.TryGetInt32(out var tv) ? tv : items.Count;
        var totalPage = container.TryGetProperty("totalPage", out var tp) && tp.TryGetInt32(out var pv) && pv > 0
            ? pv
            : (items.Count > 0 ? page + 1 : page);
        return new CmsResponse { List = items, Page = page, PageCount = totalPage, Total = total };
    }

    private static string ReadString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            _ => string.Empty
        };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string SanitizeLabel(string s) => s.Replace("$", "＄").Replace("#", "＃").Trim();
}
