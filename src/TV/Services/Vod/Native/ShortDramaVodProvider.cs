using System.Globalization;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Native provider for ❤️AI剧┃弹幕 (csp_AiDj / csp_AiDjGuard / csp_ShortDrama).
/// Replaces the retired zzdj.cc Java spider with live MacCMS V10 short-drama JSON APIs
/// (primary: 360zy t=46 爽文短剧, failover: lziapi t=46 短剧) delivering direct HLS (.m3u8) playback.
/// </summary>
public sealed class ShortDramaVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private sealed record Upstream(string Name, string ApiUrl, string ShortDramaTypeId, string PreferredPlayFlag);

    private static readonly Upstream[] Upstreams =
    [
        new("360zy", "https://360zy.com/api.php/provide/vod", "46", "360zy"),
        new("lzi", "https://cj.lziapi.com/api.php/provide/vod/", "46", "lzm3u8")
    ];

    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
        ["Accept"] = "application/json, text/plain, */*"
    };

    private static readonly VodCategory[] DefaultCategories =
    [
        new() { Id = "short", Name = "爽文短剧" },
        new() { Id = "hot", Name = "热播精选" }
    ];

    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_AiDjGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_AiDj", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_ShortDrama", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_ShortDramaGuard", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default)
    {
        var response = await FetchFromUpstreamsAsync(site, (u, _) => $"{u.ApiUrl}?ac=detail&t={u.ShortDramaTypeId}&pg=1",
            shortDramaOnly: false, cancellationToken).ConfigureAwait(false);
        response.HomeCategoryName = "热门短剧";
        response.Categories = [.. DefaultCategories];
        return response;
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var effectivePage = categoryId == "hot" ? page + 1 : page;
        var response = await FetchFromUpstreamsAsync(site,
            (u, _) => $"{u.ApiUrl}?ac=detail&t={u.ShortDramaTypeId}&pg={effectivePage.ToString(CultureInfo.InvariantCulture)}",
            shortDramaOnly: false, cancellationToken).ConfigureAwait(false);
        response.Page = page;
        return response;
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new CmsResponse { Page = page, PageCount = 1 };
        page = Math.Max(1, page);
        var encoded = Uri.EscapeDataString(keyword);
        return await FetchFromUpstreamsAsync(site,
            (u, _) => $"{u.ApiUrl}?ac=detail&wd={encoded}&pg={page.ToString(CultureInfo.InvariantCulture)}",
            shortDramaOnly: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        var (upstreamIndex, rawId) = ParseCompositeId(id);
        var upstream = Upstreams[upstreamIndex];
        var raw = await http.GetStringAsync($"{upstream.ApiUrl}?ac=detail&ids={Uri.EscapeDataString(rawId)}", Headers, cancellationToken).ConfigureAwait(false);
        var parsed = ParseResponse(raw, site, upstreamIndex, upstream);
        return parsed.List.FirstOrDefault() ?? throw new InvalidDataException("短剧详情返回空数据。");
    }

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var url = id.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        var isDirect = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new SpiderPlayResult(url, Headers, isDirect ? 0 : 1, 0, ReplaceHeaders: true));
    }

    private async Task<CmsResponse> FetchFromUpstreamsAsync(
        SourceSite site,
        Func<Upstream, int, string> buildUrl,
        bool shortDramaOnly,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var i = 0; i < Upstreams.Length; i++)
        {
            var upstream = Upstreams[i];
            try
            {
                var raw = await http.GetStringAsync(buildUrl(upstream, i), Headers, cancellationToken).ConfigureAwait(false);
                var response = ParseResponse(raw, site, i, upstream);
                if (shortDramaOnly)
                    response.List = response.List.Where(v => v.TypeName.Contains("短剧", StringComparison.Ordinal)).ToList();
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidDataException("短剧源暂不可用。");
    }

    private static CmsResponse ParseResponse(string raw, SourceSite site, int upstreamIndex, Upstream upstream)
    {
        var text = raw.Trim().Trim('\uFEFF');
        if (!text.StartsWith('{')) throw new InvalidDataException("站点响应格式无法识别。");
        var response = JsonSerializer.Deserialize<CmsResponse>(text, JsonUtil.Options) ?? new CmsResponse();
        response.List ??= [];
        response.List.RemoveAll(static item => item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name));
        foreach (var item in response.List)
        {
            item.Id = $"{upstreamIndex}:{item.Id}";
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Content = PlaybackParsing.StripHtml(item.Content);
            item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
            KeepDirectLinesFirst(item, upstream.PreferredPlayFlag);
        }
        return response;
    }

    private static void KeepDirectLinesFirst(VodItem item, string preferredFlag)
    {
        if (string.IsNullOrWhiteSpace(item.PlayFrom) || string.IsNullOrWhiteSpace(item.PlayUrl)) return;
        var flags = item.PlayFrom.Split("$$$", StringSplitOptions.None);
        var urls = item.PlayUrl.Split("$$$", StringSplitOptions.None);
        if (flags.Length <= 1 || flags.Length != urls.Length) return;

        var ordered = flags.Zip(urls, (f, u) => (Flag: f.Trim(), Url: u.Trim()))
            .Where(p => p.Flag.Length > 0 && p.Url.Length > 0)
            .OrderByDescending(p => p.Flag.Equals(preferredFlag, StringComparison.OrdinalIgnoreCase)
                || p.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (ordered.Length == 0) return;
        item.PlayFrom = string.Join("$$$", ordered.Select(p => p.Flag));
        item.PlayUrl = string.Join("$$$", ordered.Select(p => p.Url));
    }

    private static (int UpstreamIndex, string RawId) ParseCompositeId(string id)
    {
        var sep = id.IndexOf(':');
        if (sep > 0 && int.TryParse(id.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
            && idx >= 0 && idx < Upstreams.Length)
        {
            return (idx, id[(sep + 1)..]);
        }
        return (0, id);
    }
}
