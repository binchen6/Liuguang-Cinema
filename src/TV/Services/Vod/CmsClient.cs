using System.Text.Json;
using System.Xml.Linq;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Vod;

public sealed class CmsClient
{
    private readonly SourceHttpClient _http;

    public CmsClient(SourceHttpClient http) => _http = http;

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        EnsureSupported(site);
        var response = await FetchAsync(site, BuildUrl(site.Api, ("ac", "class")), cancellationToken).ConfigureAwait(false);
        if (response.Categories.Count == 0 && site.Categories.Count > 0)
        {
            response.Categories = site.Categories.Select((name, index) => new VodCategory { Id = (index + 1).ToString(), Name = name }).ToList();
        }
        return Prepare(response, site);
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        EnsureSupported(site);
        var parameters = new List<(string Key, string Value)> { ("ac", "videolist"), ("pg", page.ToString()) };
        if (!string.IsNullOrWhiteSpace(categoryId)) parameters.Add(("t", categoryId));
        return Prepare(await FetchAsync(site, BuildUrl(site.Api, parameters), cancellationToken).ConfigureAwait(false), site);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page = 1, CancellationToken cancellationToken = default)
    {
        EnsureSupported(site);
        return Prepare(await FetchAsync(site, BuildUrl(site.Api, ("ac", "videolist"), ("wd", keyword), ("pg", page.ToString())), cancellationToken).ConfigureAwait(false), site);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        EnsureSupported(site);
        var response = await FetchAsync(site, BuildUrl(site.Api, ("ac", "videolist"), ("ids", id)), cancellationToken).ConfigureAwait(false);
        var item = Prepare(response, site).List.FirstOrDefault() ?? throw new InvalidDataException("详情接口没有返回该资源。");
        item.Content = PlaybackParsing.StripHtml(item.Content);
        item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
        return item;
    }

    private async Task<CmsResponse> FetchAsync(SourceSite site, string url, CancellationToken cancellationToken)
    {
        var text = (await _http.GetStringAsync(url, site.GetHeaders(), cancellationToken).ConfigureAwait(false)).Trim().Trim('\uFEFF');
        try
        {
            if (text.StartsWith('<')) return ParseXml(text);
            if (text.StartsWith("callback(", StringComparison.OrdinalIgnoreCase) || text.StartsWith("jsonp(", StringComparison.OrdinalIgnoreCase))
            {
                text = text[(text.IndexOf('(') + 1)..text.LastIndexOf(')')];
            }
            if (!text.StartsWith('{')) throw new InvalidDataException("站点返回了非 JSON/XML 内容。");
            return JsonSerializer.Deserialize<CmsResponse>(text, JsonUtil.Options) ?? new CmsResponse();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new InvalidDataException($"站点 {site.Name} 的响应格式无法解析。", ex);
        }
    }

    private static CmsResponse Prepare(CmsResponse response, SourceSite site)
    {
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Picture = NormalizeUrl(item.Picture, site.Api);
            item.Content = PlaybackParsing.StripHtml(item.Content);
            item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
        }
        return response;
    }

    private static CmsResponse ParseXml(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidDataException("XML 根节点为空。");
        var response = new CmsResponse
        {
            Page = ParseInt(root.Element("page")?.Value),
            PageCount = ParseInt(root.Element("pagecount")?.Value),
            Total = ParseInt(root.Element("total")?.Value),
            Limit = ParseInt(root.Element("limit")?.Value)
        };
        response.Categories = root.Element("class")?.Elements("ty").Select(element => new VodCategory
        {
            Id = element.Attribute("id")?.Value ?? string.Empty,
            Name = element.Value.Trim()
        }).Where(item => item.Name.Length > 0).ToList() ?? [];
        response.List = root.Element("list")?.Elements("video").Select(ParseXmlVideo).ToList() ?? [];
        return response;
    }

    private static VodItem ParseXmlVideo(XElement video)
    {
        var item = new VodItem
        {
            Id = Value(video, "id"), Name = Value(video, "name"), Picture = Value(video, "pic"),
            Remarks = Value(video, "note"), Year = Value(video, "year"), Area = Value(video, "area"),
            Director = Value(video, "director"), Actor = Value(video, "actor"), Content = Value(video, "des"),
            TypeId = Value(video, "type"), TypeName = video.Element("type")?.Attribute("name")?.Value ?? string.Empty
        };
        var lines = video.Element("dl")?.Elements("dd").ToList() ?? [];
        item.PlayFrom = string.Join("$$$", lines.Select(line => line.Attribute("flag")?.Value ?? "默认"));
        item.PlayUrl = string.Join("$$$", lines.Select(line => line.Value.Trim()));
        return item;
    }

    private static string Value(XElement element, string name) => element.Element(name)?.Value.Trim() ?? string.Empty;
    private static int ParseInt(string? value) => int.TryParse(value, out var number) ? number : 0;

    private static string NormalizeUrl(string value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (value.StartsWith("//")) return "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : new Uri(new Uri(baseUrl), value).ToString();
    }

    private static void EnsureSupported(SourceSite site)
    {
        if (!site.IsSupported) throw new NotSupportedException($"{site.Name} 依赖专用爬虫，当前版本无法直接运行。");
        if (!Uri.TryCreate(site.Api, UriKind.Absolute, out _)) throw new InvalidDataException($"{site.Name} 的 API 地址无效。");
    }

    private static string BuildUrl(string api, params (string Key, string Value)[] parameters) => BuildUrl(api, (IEnumerable<(string Key, string Value)>)parameters);

    private static string BuildUrl(string api, IEnumerable<(string Key, string Value)> parameters)
    {
        var builder = new UriBuilder(api);
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(builder.Query)) query.Add(builder.Query.TrimStart('?'));
        query.AddRange(parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        builder.Query = string.Join('&', query);
        return builder.Uri.ToString();
    }
}
