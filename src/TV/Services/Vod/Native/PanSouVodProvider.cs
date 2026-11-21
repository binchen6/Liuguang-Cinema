using System.Collections.Concurrent;
using System.Text.Json;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;

namespace TV.Services.Vod.Native;

public sealed class PanSouVodProvider(SourceHttpClient http, CloudDriveService cloud) : INativeVodProvider
{
    private sealed record CachedSearch(Lazy<Task<CmsResponse>> Request, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, CachedSearch> _cache = new(StringComparer.Ordinal);
    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_S_zpsGuard", StringComparison.OrdinalIgnoreCase) ||
         site.Api.Equals("csp_PanSou", StringComparison.OrdinalIgnoreCase)) &&
        GetEndpoint(site.Ext) is not null;

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse());

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        GetHomeAsync(site, cancellationToken);

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (page > 1 || string.IsNullOrWhiteSpace(keyword)) return new CmsResponse();
        var endpoint = GetEndpoint(site.Ext) ?? throw new InvalidDataException("盘搜接口地址无效。");
        var key = endpoint.AbsoluteUri + "\n" + keyword.Trim();
        var now = DateTimeOffset.UtcNow;
        if (_cache.Count >= 128)
            foreach (var pair in _cache.Where(pair => pair.Value.ExpiresAt <= now)) _cache.TryRemove(pair.Key, out _);
        var candidate = new CachedSearch(new Lazy<Task<CmsResponse>>(() => ReadSearchAsync(endpoint, keyword, site)), now.AddMinutes(2));
        var entry = _cache.AddOrUpdate(key, candidate, (_, current) => current.ExpiresAt > now &&
            (!current.Request.IsValueCreated || !current.Request.Value.IsCompleted || current.Request.Value.IsCompletedSuccessfully) ? current : candidate);
        try { return await entry.Request.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            if (entry.Request.Value.IsFaulted || entry.Request.Value.IsCanceled) _cache.TryRemove(new KeyValuePair<string, CachedSearch>(key, entry));
            throw;
        }
    }

    private async Task<CmsResponse> ReadSearchAsync(Uri endpoint, string keyword, SourceSite site)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var request = new UriBuilder(endpoint)
            {
                Query = "kw=" + Uri.EscapeDataString(keyword.Trim()) + "&res=merge&cloud_types=quark,uc,aliyun,baidu"
            };
            var payload = await http.GetBytesAsync(request.Uri.AbsoluteUri, maxBytes: 4 * 1024 * 1024, cancellationToken: deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload.Content);
            var root = document.RootElement;
            if (Property(root, "code").ToString() != "0") throw new InvalidDataException("盘搜请求失败。");
            var data = Property(root, "data");
            var groups = Property(data, "merged_by_type");
            if (groups.ValueKind != JsonValueKind.Object)
            {
                if (Property(data, "total").ToString() == "0") return new CmsResponse();
                throw new InvalidDataException("盘搜返回了无法识别的结果。");
            }

            var response = new CmsResponse { Page = 1, PageCount = 1 };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in group.Value.EnumerateArray())
                {
                    if (!CloudShareLink.TryParse(ReadString(item, "url"), out var link)) continue;
                    var shareUrl = link.ToShareUrl(ReadString(item, "password").Trim());
                    if (!seen.Add(shareUrl)) continue;
                    var name = PlaybackParsing.StripHtml(ReadString(item, "note"));
                    var provider = CloudSearchDetail.ProviderName(link.Provider);
                    response.List.Add(new VodItem
                    {
                        Id = shareUrl, SiteKey = site.Key, SiteName = site.Name,
                        Name = Title(name, provider), Remarks = provider,
                        Content = name.Length > 480 ? name[..480] : name
                    });
                }
            }
            response.List = response.List.OrderByDescending(item => SearchScoring.Score(keyword, item)).ToList();
            response.Total = response.List.Count;
            return response;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("盘搜请求超时。");
        }
    }

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default) =>
        CloudSearchDetail.ReadAsync(cloud, site, id, id, null, cancellationToken);

    private static Uri? GetEndpoint(JsonElement extension)
    {
        var value = extension.ValueKind == JsonValueKind.String ? extension.GetString() : ReadString(extension, "siteUrl");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api/search", StringComparison.Ordinal)) path += "/api/search";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    private static string Title(string note, string provider)
    {
        var line = note.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return provider + "分享";
        return line.Length <= 100 ? line : line[..100].TrimEnd();
    }

    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    private static string ReadString(JsonElement item, string name) =>
        Property(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;
}
