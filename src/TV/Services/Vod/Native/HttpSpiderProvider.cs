using System.Globalization;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public sealed class HttpSpiderProvider(SourceHttpClient http) : INativePlaybackProvider
{
    public static bool Supports(SourceSite site) => site.Type == 4 &&
        Uri.TryCreate(site.Api, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        GetAsync(site, cancellationToken, ("filter", "true"));

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        categoryId.Length == 0 ? GetHomeAsync(site, cancellationToken) :
        GetAsync(site, cancellationToken, ("ac", "detail"), ("filter", "true"), ("t", categoryId),
            ("pg", page.ToString(CultureInfo.InvariantCulture)));

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        GetAsync(site, cancellationToken, ("ac", "detail"), ("quick", "false"), ("wd", keyword),
            ("pg", page.ToString(CultureInfo.InvariantCulture)));

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default) =>
        (await GetAsync(site, cancellationToken, ("ac", "detail"), ("ids", id)).ConfigureAwait(false)).List.FirstOrDefault()
        ?? throw new InvalidDataException("站点未返回视频详情。");

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var raw = await RequestAsync(site, token, [("play", id), ("flag", flag)]).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        return SpiderVodClient.ReadPlayResult(document.RootElement);
    }

    private async Task<CmsResponse> GetAsync(SourceSite site, CancellationToken token, params (string Key, string Value)[] parameters)
    {
        var raw = await RequestAsync(site, token, parameters).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CmsResponse>(raw, JsonUtil.Options)
            ?? throw new InvalidDataException("站点返回空响应。");
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Content = PlaybackParsing.StripHtml(item.Content);
            item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
        }
        return response;
    }

    private Task<string> RequestAsync(SourceSite site, CancellationToken token, (string Key, string Value)[] parameters)
    {
        var uri = new UriBuilder(site.Api);
        var parametersWithExt = parameters.ToList();
        var ext = site.Ext.ValueKind switch
        {
            JsonValueKind.String => site.Ext.GetString(),
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            _ => site.Ext.GetRawText()
        };
        if (!string.IsNullOrWhiteSpace(ext)) parametersWithExt.Add(("extend", ext));
        var query = string.Join('&', parametersWithExt.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        uri.Query = string.IsNullOrEmpty(uri.Query) ? query : uri.Query.TrimStart('?') + "&" + query;
        return http.GetStringAsync(uri.Uri.AbsoluteUri, site.GetHeaders(), token);
    }
}
