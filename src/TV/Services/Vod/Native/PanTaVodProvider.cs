using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;

namespace TV.Services.Vod.Native;

public sealed class PanTaVodProvider(SourceHttpClient http, CloudDriveService cloud) : INativeVodProvider
{
    private static readonly Uri DefaultOrigin = new("https://panso.vip");

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_BpanSoGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_BpanSo", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_PanTa", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse());

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetHomeAsync(site, cancellationToken);

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new CmsResponse();
        page = Math.Max(1, page);
        var request = new UriBuilder(new Uri(Origin(site), "/search"))
        {
            Query = $"exact=false&format=video&page={page}&q={Uri.EscapeDataString(keyword.Trim())}&share_time=&type=&user="
        };
        var (document, finalUri) = await ReadPageAsync(site, request.Uri, cancellationToken).ConfigureAwait(false);
        var result = new CmsResponse { Page = page, PageCount = page };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (!TryDetailUri(finalUri, anchor.GetAttribute("href"), out var detail)) continue;
            var name = anchor.GetAttribute("title")?.Trim();
            if (string.IsNullOrEmpty(name)) name = anchor.TextContent.Trim();
            if (name.Length == 0 || name == "详情页" || !seen.Add(detail.AbsoluteUri)) continue;
            result.List.Add(new VodItem { Id = detail.AbsoluteUri, Name = name, SiteKey = site.Key, SiteName = site.Name, Remarks = "网盘分享" });
        }
        result.Total = result.List.Count;
        return result;
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        if (!TryDetailUri(Origin(site), id, out var detail)) throw new InvalidDataException("盘搜详情地址无效。");
        var (document, finalUri) = await ReadPageAsync(site, detail, cancellationToken).ConfigureAwait(false);
        Exception? failure = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (!Uri.TryCreate(finalUri, anchor.GetAttribute("href"), out var target) ||
                !CloudShareLink.TryParse(target.AbsoluteUri, out _) || !seen.Add(target.AbsoluteUri)) continue;
            try
            {
                return await CloudSearchDetail.ReadAsync(cloud, site, id, target.AbsoluteUri,
                    document.QuerySelector("h1")?.TextContent.Trim(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failure = ex; }
        }
        throw failure ?? new InvalidDataException("盘搜详情未提供可用的网盘分享。");
    }

    private static Uri Origin(SourceSite site)
    {
        var extension = site.Ext;
        if (extension.ValueKind == JsonValueKind.Object && extension.TryGetProperty("siteUrl", out var configured)) extension = configured;
        return extension.ValueKind == JsonValueKind.String && Uri.TryCreate(extension.GetString(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : DefaultOrigin;
    }

    private static bool TryDetailUri(Uri origin, string? value, out Uri detail)
    {
        detail = null!;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(origin, value, out var uri) ||
            !MediaRequestHeaders.SameOrigin(origin, uri) || uri.UserInfo.Length > 0 ||
            !uri.AbsolutePath.StartsWith("/doc/", StringComparison.Ordinal)) return false;
        var id = uri.AbsolutePath[5..];
        if (id.Length is 0 or > 128 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        detail = new Uri(origin, uri.AbsolutePath);
        return true;
    }

    private async Task<(IDocument Document, Uri FinalUri)> ReadPageAsync(SourceSite site, Uri request, CancellationToken token)
    {
        var payload = await http.GetBytesAsync(request.AbsoluteUri, site.GetHeaders(), maxBytes: 4 * 1024 * 1024,
            cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
        if (!MediaRequestHeaders.SameOrigin(request, payload.FinalUri)) throw new InvalidDataException("盘搜页面跳转到了其他站点。");
        if (payload.Content.Length == 0) throw new InvalidDataException("站点响应为空。");
        var document = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(payload.Content), token).ConfigureAwait(false);
        return (document, payload.FinalUri);
    }
}
