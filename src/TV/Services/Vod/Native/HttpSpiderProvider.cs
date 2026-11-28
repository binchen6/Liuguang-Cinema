using System.Globalization;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

public sealed class HttpSpiderProvider(SourceHttpClient http) : INativePlaybackProvider
{
    public static bool Supports(SourceSite site) => ResolveBaseUrl(site) is not null;

    public bool CanHandle(SourceSite site) => Supports(site);

    private static string? ResolveBaseUrl(SourceSite site)
    {
        if (site.Type == 4 && TryValidHttpUrl(site.Api, out var type4Url))
            return type4Url;
        if (ResolveDesktopEndpoint(site) is { } desktop) return desktop.AbsoluteUri;
        if (site.Type == 3 && (site.Api.Equals("csp_T4", StringComparison.OrdinalIgnoreCase) ||
            site.Api.Equals("csp_T4Guard", StringComparison.OrdinalIgnoreCase)))
        {
            if (site.Ext.ValueKind == JsonValueKind.String && TryValidHttpUrl(site.Ext.GetString(), out var extUrl))
                return extUrl;
            if (site.Ext.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "url", "api", "siteUrl", "host" })
                {
                    if (site.Ext.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String &&
                        TryValidHttpUrl(val.GetString(), out var objUrl))
                        return objUrl;
                }
            }
        }
        return null;
    }

    private static Uri? ResolveDesktopEndpoint(SourceSite site) => site.Type == 3 &&
        site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(site.DesktopApi, UriKind.Absolute, out var uri) && MediaRequestHeaders.IsHttpAddress(uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback) && uri.Fragment.Length == 0 ? uri : null;

    private static bool TryValidHttpUrl(string? candidate, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var trimmed = candidate.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

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
        if (PlayUrlResolver.IsDirectMediaUrl(id) && Uri.TryCreate(id, UriKind.Absolute, out var target) &&
            MediaRequestHeaders.IsHttpAddress(target) && ResolveBaseUrl(site) is { } httpEndpoint)
        {
            var origin = new Uri(httpEndpoint);
            var headers = NativeSiteToolkit.CreateBrowserHeaders(origin, ResolveDesktopEndpoint(site) is null
                ? MediaRequestHeaders.ForTarget(site.GetHeaders(), origin, target) : null);
            return new SpiderPlayResult(target.AbsoluteUri, headers, 0, 0) { ReplaceHeaders = true };
        }
        var raw = await RequestAsync(site, [("play", id), ("flag", flag)], token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        return SpiderVodClient.ReadPlayResult(document.RootElement);
    }

    private async Task<CmsResponse> GetAsync(SourceSite site, CancellationToken token, params (string Key, string Value)[] parameters)
    {
        var raw = await RequestAsync(site, parameters, token).ConfigureAwait(false);
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

    private Task<string> RequestAsync(SourceSite site, (string Key, string Value)[] parameters, CancellationToken token)
    {
        var baseUrl = ResolveBaseUrl(site) ?? throw new InvalidDataException("站点 T4 接口地址无效。");
        var uri = new UriBuilder(baseUrl);
        var parametersWithExt = parameters.ToList();
        // Type-3 csp_T4 stores the endpoint URL itself in Ext, so only forward Ext as "extend" for Type-4 sites.
        if (site.Type == 4)
        {
            var ext = site.Ext.ValueKind switch
            {
                JsonValueKind.String => site.Ext.GetString(),
                JsonValueKind.Undefined or JsonValueKind.Null => null,
                _ => site.Ext.GetRawText()
            };
            if (!string.IsNullOrWhiteSpace(ext)) parametersWithExt.Add(("extend", ext));
        }
        var query = string.Join('&', parametersWithExt.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        uri.Query = string.IsNullOrEmpty(uri.Query) ? query : uri.Query.TrimStart('?') + "&" + query;
        if (ResolveDesktopEndpoint(site) is { } desktop)
            return GetDesktopResponseAsync(uri.Uri, desktop, token);
        return http.GetStringAsync(uri.Uri.AbsoluteUri, site.GetHeaders(), token);
    }

    private async Task<string> GetDesktopResponseAsync(Uri address, Uri endpoint, CancellationToken token)
    {
        var payload = await http.GetTrustedBytesAsync(address, null, 4 * 1024 * 1024,
            destination => MediaRequestHeaders.SameOrigin(endpoint, destination), token).ConfigureAwait(false);
        return SourceHttpClient.DecodeText(payload.Content);
    }
}
