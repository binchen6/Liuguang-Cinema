using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 👀瓜子┃不卡 (csp_Appgz / csp_AppgzGuard) using the encrypted GZ360 JSON API.
/// </summary>
public sealed class GuaziVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private const string ApiBase = "https://haiwaiapi.1fc8ab0.com";
    private const string WebOrigin = "https://gz360.tv";
    private const string ImageHost = "https://images.67c6c7a.com/";
    private const string BrowserUa = NativeSiteToolkit.BrowserUserAgent;

    private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("181cc88340ae5b2b");
    private static readonly byte[] AesIv = Encoding.UTF8.GetBytes("4423d1e2773476ce");

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_AppgzGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Appgz", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Guazi", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_GuaziGuard", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var categories = new List<VodCategory>();
        using (var condDoc = await PostApiAsync("/Pc/Search/GetCondition", "{}", cancellationToken).ConfigureAwait(false))
        {
            if (condDoc.RootElement.TryGetProperty("column", out var cols) && cols.ValueKind == JsonValueKind.Array)
            {
                foreach (var col in cols.EnumerateArray())
                {
                    var id = ReadString(col, "value");
                    var name = ReadString(col, "name");
                    if (id.Length > 0 && id != "0" && name.Length > 0)
                        categories.Add(new VodCategory { Id = id, Name = name });
                }
            }
        }
        if (categories.Count == 0)
        {
            categories.AddRange([
                new VodCategory { Id = "1", Name = "电影" },
                new VodCategory { Id = "2", Name = "连续剧" },
                new VodCategory { Id = "3", Name = "综艺" },
                new VodCategory { Id = "4", Name = "动漫" },
                new VodCategory { Id = "64", Name = "短剧" }
            ]);
        }

        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using (var homeDoc = await PostApiAsync("/Pc/Index/CategoryList", "{}", cancellationToken).ConfigureAwait(false))
        {
            if (homeDoc.RootElement.TryGetProperty("list", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("list", out var cards) || cards.ValueKind != JsonValueKind.Array) continue;
                    foreach (var card in cards.EnumerateArray())
                    {
                        var vod = ParseCard(card, site);
                        if (vod is not null && seen.Add(vod.Id))
                            items.Add(vod);
                        if (items.Count >= 40) break;
                    }
                    if (items.Count >= 40) break;
                }
            }
        }

        return new CmsResponse
        {
            HomeCategoryName = "热门推荐",
            Categories = categories,
            List = items,
            Page = 1,
            PageCount = 1,
            Total = items.Count
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var tid = int.TryParse(categoryId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        var payload = JsonSerializer.Serialize(new { page, page_size = 24, sort = "d_addtime", tid });
        using var doc = await PostApiAsync("/Pc/Search/GetConditionList", payload, cancellationToken).ConfigureAwait(false);
        return ParseListResponse(doc.RootElement, site, page, 24);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var payload = JsonSerializer.Serialize(new { keywords = keyword, page, page_size = 24 });
        using var doc = await PostApiAsync("/Pc/Search/GetList", payload, cancellationToken).ConfigureAwait(false);
        return ParseListResponse(doc.RootElement, site, page, 24);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var numericId = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var infoPayload = numericId > 0
            ? JsonSerializer.Serialize(new { vod_id = numericId })
            : JsonSerializer.Serialize(new { vod_id = id });

        using var infoDoc = await PostApiAsync("/Pc/Resource/GetVodInfo", infoPayload, cancellationToken).ConfigureAwait(false);
        if (!infoDoc.RootElement.TryGetProperty("vodInfo", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("瓜子详情返回空数据。");

        var episodes = new List<string>();
        var totalEps = 0;
        for (var epPage = 1; epPage <= 5; epPage++)
        {
            var plPayload = numericId > 0
                ? JsonSerializer.Serialize(new { vod_id = numericId, page = epPage, page_size = 100 })
                : JsonSerializer.Serialize(new { vod_id = id, page = epPage, page_size = 100 });
            using var plDoc = await PostApiAsync("/Pc/Resource/GetOnePlayList", plPayload, cancellationToken).ConfigureAwait(false);
            if (epPage == 1 && plDoc.RootElement.TryGetProperty("total_vod_vurl", out var totalEl) &&
                int.TryParse(totalEl.ValueKind == JsonValueKind.String ? totalEl.GetString() : totalEl.GetRawText(), out var parsedTotal))
            {
                totalEps = parsedTotal;
            }
            if (!plDoc.RootElement.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array || urls.GetArrayLength() == 0)
                break;

            foreach (var ep in urls.EnumerateArray())
            {
                var url = ReadString(ep, "url");
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                var name = ReadString(ep, "name");
                if (name.Length == 0) name = $"第{episodes.Count + 1}集";
                episodes.Add($"{SanitizeLabel(name)}${url}");
            }
            if (totalEps <= 0 || episodes.Count >= totalEps || urls.GetArrayLength() < 100)
                break;
        }

        if (episodes.Count == 0)
        {
            var fallbackUrl = ReadString(info, "play_url");
            if (fallbackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var defName = FirstNonEmpty(ReadString(info, "default_play_name"), "正片");
                episodes.Add($"{SanitizeLabel(defName)}${fallbackUrl}");
            }
        }

        var tags = info.TryGetProperty("videoTag", out var tagArr) && tagArr.ValueKind == JsonValueKind.Array
            ? string.Join(" / ", tagArr.EnumerateArray().Select(e => e.GetString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)))
            : string.Empty;

        return new VodItem
        {
            Id = FirstNonEmpty(ReadString(info, "vod_id"), id),
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = ReadString(info, "vod_name"),
            Picture = NormalizePic(FirstNonEmpty(ReadString(info, "pic"), ReadString(info, "vod_pic"))),
            TypeName = tags,
            Remarks = FirstNonEmpty(ReadString(info, "vod_title"), ReadString(info, "vod_continu")),
            Year = ReadString(info, "vod_year"),
            Area = ReadString(info, "vod_area"),
            Actor = ReadString(info, "vod_actor"),
            Director = ReadString(info, "vod_director"),
            Content = ReadString(info, "vod_use_content"),
            PlayFrom = episodes.Count > 0 ? "瓜子蓝光" : string.Empty,
            PlayUrl = string.Join('#', episodes)
        };
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Origin"] = WebOrigin,
            ["Referer"] = WebOrigin + "/"
        };
        return Task.FromResult(new SpiderPlayResult(id, headers, 0, 0));
    }

    private async Task<JsonDocument> PostApiAsync(string path, string plainJson, CancellationToken token)
    {
        var encryptedHex = EncryptHex(plainJson);
        var requestBody = JsonSerializer.Serialize(new { @params = encryptedHex });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Origin"] = WebOrigin,
            ["Referer"] = WebOrigin + "/"
        };
        var exchange = await http.PostJsonAsync(ApiBase + path, requestBody, headers, maxBytes: 6 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        if ((int)exchange.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"瓜子接口返回 HTTP {(int)exchange.StatusCode}");

        using var wrapper = JsonDocument.Parse(exchange.Payload.Content);
        if (wrapper.RootElement.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var code) && code is not (0 or 200))
        {
            var msg = ReadString(wrapper.RootElement, "msg");
            throw new InvalidDataException($"瓜子接口返回错误({code})：{msg}");
        }
        if (wrapper.RootElement.TryGetProperty("data", out var dataEl))
        {
            if (dataEl.ValueKind == JsonValueKind.String)
            {
                var hex = dataEl.GetString();
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    var decrypted = DecryptHex(hex);
                    return JsonDocument.Parse(decrypted);
                }
            }
            return JsonDocument.Parse(dataEl.GetRawText());
        }
        return JsonDocument.Parse(exchange.Payload.Content);
    }

    private static CmsResponse ParseListResponse(JsonElement root, SourceSite site, int page, int pageSize)
    {
        var items = new List<VodItem>();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                var item = ParseCard(entry, site);
                if (item is not null) items.Add(item);
            }
        }
        var total = 0;
        if (root.TryGetProperty("total", out var totalEl) &&
            int.TryParse(totalEl.ValueKind == JsonValueKind.String ? totalEl.GetString() : totalEl.GetRawText(), out var parsedTotal))
            total = parsedTotal;
        var pageCount = total > 0
            ? Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
            : (items.Count >= pageSize ? page + 1 : page);
        return new CmsResponse { List = items, Page = page, PageCount = pageCount, Total = total > 0 ? total : items.Count };
    }

    private static VodItem? ParseCard(JsonElement entry, SourceSite site)
    {
        var id = ReadString(entry, "vod_id");
        var name = FirstNonEmpty(ReadString(entry, "vod_name"), ReadString(entry, "c_name"));
        if (id.Length == 0 || name.Length == 0) return null;
        var score = FirstNonEmpty(ReadString(entry, "vod_scroe"), ReadString(entry, "vod_douban_score"));
        var remarks = FirstNonEmpty(ReadString(entry, "new_continue"), ReadString(entry, "vod_title"), ReadString(entry, "vod_continu"));
        if (remarks.Length == 0 && score.Length > 0 && score != "0" && score != "0.0")
            remarks = $"{score}分";
        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = name,
            Picture = NormalizePic(FirstNonEmpty(ReadString(entry, "vod_pic"), ReadString(entry, "c_pic"), ReadString(entry, "pic"))),
            Remarks = remarks,
            Year = ReadString(entry, "vod_year"),
            Area = ReadString(entry, "vod_area"),
            Actor = ReadString(entry, "vod_actor"),
            Director = FirstNonEmpty(ReadString(entry, "vod_director"), ReadString(entry, "vod_directed")),
            Content = ReadString(entry, "vod_use_content")
        };
    }

    private static string NormalizePic(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return string.Empty;
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return raw;
        if (raw.StartsWith("//", StringComparison.Ordinal))
            return "https:" + raw;
        return ImageHost + raw.TrimStart('/');
    }

    private static string EncryptHex(string plainText)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.IV = AesIv;
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
        return Convert.ToHexString(cipher).ToLowerInvariant();
    }

    private static string DecryptHex(string hex)
    {
        var cipher = Convert.FromHexString(hex.Trim());
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.IV = AesIv;
        var plain = aes.CreateDecryptor().TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static string ReadString(JsonElement obj, string prop) => NativeSiteToolkit.ReadJsonString(obj, prop);

    private static string FirstNonEmpty(params string[] values) => NativeSiteToolkit.FirstNonEmpty(values);

    private static string SanitizeLabel(string s) => NativeSiteToolkit.SanitizeLabel(s);
}
