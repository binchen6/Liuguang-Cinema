using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for 🐱韩迷┃外剧 (csp_Hanquan / csp_HanquanGuard) backed by 9hanju.com.
/// </summary>
public sealed partial class HanjuVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private static readonly Uri Origin = new("https://www.9hanju.com/");
    private const string BrowserUa = NativeSiteToolkit.BrowserUserAgent;

    private static readonly byte[] AesKey = CreatePaddedKey("my-to-newhan-2025");

    private static readonly VodCategory[] DefaultCategories =
    [
        new() { Id = "1", Name = "韩剧" },
        new() { Id = "3", Name = "韩国电影" },
        new() { Id = "4", Name = "韩综" }
    ];

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_HanquanGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_Hanquan", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var (doc, pageUri) = await FetchHtmlAsync("/", cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            HomeCategoryName = "热播韩剧",
            Categories = [.. DefaultCategories],
            List = ParseCards(doc, pageUri, site),
            Page = 1,
            PageCount = 1
        };
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var path = page == 1 ? $"/list/{categoryId}---.html" : $"/list/{categoryId}---{page}.html";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, pageUri, site);
        return new CmsResponse
        {
            List = items,
            Page = page,
            PageCount = items.Count > 0 ? page + 1 : page,
            Total = items.Count
        };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0 || page > 1) return new CmsResponse { Page = page, PageCount = 1 };
        var target = new Uri(Origin, "/search/").AbsoluteUri;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Referer"] = Origin.AbsoluteUri
        };
        var form = new KeyValuePair<string, string>[]
        {
            new("show", "searchkey"),
            new("keyboard", keyword)
        };
        var exchange = await http.PostFormAsync(target, form, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: cancellationToken, followRedirects: true).ConfigureAwait(false);
        if ((int)exchange.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"韩迷搜索返回 HTTP {(int)exchange.StatusCode}");
        var html = Encoding.UTF8.GetString(exchange.Payload.Content);
        var doc = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var items = ParseCards(doc, exchange.Payload.FinalUri, site);
        return new CmsResponse { List = items, Page = 1, PageCount = 1, Total = items.Count };
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var path = id.StartsWith('/') ? id : $"/detail/{id}.html";
        var (doc, pageUri) = await FetchHtmlAsync(path, cancellationToken).ConfigureAwait(false);
        var vodId = DetailIdRegex().Match(path) is { Success: true } dm ? dm.Groups[1].Value : id;

        var title = doc.QuerySelector("#m")?.TextContent.Trim() ?? string.Empty;
        var pic = ResolveUrl(pageUri,
            doc.QuerySelector(".detail .pic img")?.GetAttribute("data-original")
            ?? doc.QuerySelector(".detail .pic img")?.GetAttribute("src"));

        string actor = string.Empty, remarks = string.Empty, year = string.Empty, typeName = string.Empty, shortDesc = string.Empty;
        foreach (var dl in doc.QuerySelectorAll(".detail .info dl"))
        {
            var dt = dl.QuerySelector("dt")?.TextContent.Trim() ?? string.Empty;
            var dd = CollapseSpaces(dl.QuerySelector("dd")?.TextContent ?? string.Empty);
            if (dt.StartsWith("片名", StringComparison.Ordinal) && title.Length == 0) title = dd;
            else if (dt.StartsWith("主演", StringComparison.Ordinal)) actor = dd;
            else if (dt.StartsWith("状态", StringComparison.Ordinal)) remarks = dd;
            else if (dt.StartsWith("上映", StringComparison.Ordinal)) year = dd;
            else if (dt.StartsWith("电视", StringComparison.Ordinal)) typeName = dd;
            else if (dt.StartsWith("剧情", StringComparison.Ordinal)) shortDesc = dd;
        }
        var fullDesc = CollapseSpaces(doc.QuerySelector("#jq .juqing, .juqing")?.TextContent ?? string.Empty);

        var episodes = new List<string>();
        foreach (var a in doc.QuerySelectorAll(".play ul li a[onclick]"))
        {
            var onclick = a.GetAttribute("onclick") ?? string.Empty;
            var m = PlayCallRegex().Match(onclick);
            if (!m.Success) continue;
            var ud = m.Groups[1].Value.Trim();
            var label = CollapseSpaces(a.TextContent);
            if (label.Length == 0) label = m.Groups[2].Value.Trim();
            if (ud.Length > 0 && label.Length > 0)
                episodes.Add($"{label.Replace("$", "＄").Replace("#", "＃")}${vodId}|{ud}");
        }

        return new VodItem
        {
            Id = vodId,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title,
            Picture = pic,
            TypeName = typeName,
            Remarks = remarks,
            Year = year,
            Area = "韩国",
            Actor = actor,
            Content = fullDesc.Length > 0 ? fullDesc : shortDesc,
            PlayFrom = episodes.Count > 0 ? "在线云播" : string.Empty,
            PlayUrl = string.Join('#', episodes)
        };
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var parts = id.Split('|', 2, StringSplitOptions.TrimEntries);
        var vodId = parts.Length == 2 ? parts[0] : string.Empty;
        var ud = parts.Length == 2 ? parts[1] : id;

        var target = new Uri(Origin, $"/u/u1.php?ud={Uri.EscapeDataString(ud)}").AbsoluteUri;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Referer"] = vodId.Length > 0 ? new Uri(Origin, $"/detail/{vodId}.html").AbsoluteUri : Origin.AbsoluteUri
        };
        var exchange = await http.GetExchangeAsync(target, headers, maxBytes: 256 * 1024, cancellationToken: token).ConfigureAwait(false);
        if ((int)exchange.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"韩迷播放接口返回 HTTP {(int)exchange.StatusCode}");

        var encrypted = Encoding.UTF8.GetString(exchange.Payload.Content).Trim();
        var streamUrl = DecryptMediaUrl(encrypted);
        if (!streamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("韩迷解密后的播放地址无效。");

        var playHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa
        };
        return new SpiderPlayResult(streamUrl, playHeaders, 0, 0);
    }

    private async Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(string relativePath, CancellationToken token)
    {
        var target = new Uri(Origin, relativePath);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUa,
            ["Referer"] = Origin.AbsoluteUri
        };
        var exchange = await http.GetExchangeAsync(target.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
        if ((int)exchange.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"韩迷站点返回 HTTP {(int)exchange.StatusCode}");
        var html = Encoding.UTF8.GetString(exchange.Payload.Content);
        var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
        return (doc, exchange.Payload.FinalUri);
    }

    private static List<VodItem> ParseCards(IDocument doc, Uri pageUri, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = doc.QuerySelectorAll("a.tu[href*='/detail/']");
        if (nodes.Length == 0) nodes = doc.QuerySelectorAll("a[href*='/detail/']");
        foreach (var a in nodes)
        {
            var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
            var idMatch = DetailIdRegex().Match(href);
            if (!idMatch.Success) continue;
            var vodId = idMatch.Groups[1].Value;
            var name = a.GetAttribute("title")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = CollapseSpaces(a.TextContent);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(vodId)) continue;
            var pic = ResolveUrl(pageUri,
                a.GetAttribute("data-original")
                ?? a.QuerySelector("img")?.GetAttribute("data-original")
                ?? a.QuerySelector("img")?.GetAttribute("src"));
            var remark = CollapseSpaces(a.QuerySelector(".tip")?.TextContent ?? string.Empty);
            items.Add(new VodItem
            {
                Id = vodId,
                SiteKey = site.Key,
                SiteName = site.Name,
                Name = name,
                Picture = pic,
                Remarks = remark,
                Area = "韩国"
            });
        }
        return items;
    }

    private static string DecryptMediaUrl(string base64Cipher)
    {
        var raw = Convert.FromBase64String(base64Cipher);
        if (raw.Length <= 16) throw new InvalidDataException("韩迷播放密文长度不足。");
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.IV = raw[..16];
        var plain = aes.CreateDecryptor().TransformFinalBlock(raw, 16, raw.Length - 16);
        return Encoding.UTF8.GetString(plain).Trim();
    }

    private static byte[] CreatePaddedKey(string key)
    {
        var padded = new byte[32];
        var src = Encoding.UTF8.GetBytes(key);
        Buffer.BlockCopy(src, 0, padded, 0, Math.Min(src.Length, padded.Length));
        return padded;
    }

    private static string ResolveUrl(Uri pageUri, string? raw) => NativeSiteToolkit.ResolveUrl(pageUri, raw);

    private static string CollapseSpaces(string value) => NativeSiteToolkit.CollapseSpaces(value);

    [GeneratedRegex(@"/detail/(\d+)\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailIdRegex();

    [GeneratedRegex(@"bb_a\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]*)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayCallRegex();
}
