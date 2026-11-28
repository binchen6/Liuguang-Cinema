using System.Text.Json;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Spider;

/// <summary>
/// .so 原生库站点的通用兼容兜底（参考影视仓 / OK影视对同类站点的通用处理）。
/// 大量闭源 Android ARM 原生库（如 FishGuard 系列防护）只是对站方标准 MacCMS JSON 接口做一层
/// 请求包装与响应加密；桌面端无法加载 ARM .so 时，按安卓客户端口径直接请求标准 CMS JSON 协议，
/// 命中即恢复该站点的目录、搜索、详情与筛选能力。
/// </summary>
internal sealed class AndroidCompatCmsFallback(SourceHttpClient http)
{
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = SourceHttpClient.DefaultUserAgent,
        ["Accept"] = "application/json, text/plain, */*"
    };

    public static bool IsNativeLibraryRequirement(Exception ex) => ex is NotSupportedException notSupported &&
        notSupported.Message.Contains("Android 原生库", StringComparison.Ordinal);

    /// <summary>尝试以标准 CMS JSON 协议完成同一动作；全部端点失败或不可解析时返回 null（调用方回退到原异常）。</summary>
    public async Task<CmsResponse?> TryFetchAsync(SourceSite site, string action,
        IReadOnlyDictionary<string, object?> request, CancellationToken token)
    {
        if (TryResolveBase(site.Ext) is not { } baseUrl) return null;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 8, 30));
        foreach (var endpoint in CandidateEndpoints(baseUrl))
        {
            try
            {
                var url = endpoint + BuildQuery(action, request);
                var payload = await http.GetBytesAsync(url, Headers, maxBytes: 4 * 1024 * 1024,
                    cancellationToken: token, requestTimeout: timeout).ConfigureAwait(false);
                var text = SourceHttpClient.DecodeText(payload.Content).TrimStart('\uFEFF', '\r', '\n', ' ', '\t');
                if (text.Length == 0 || text[0] is not ('{' or '[')) continue;
                var response = JsonSerializer.Deserialize<CmsResponse>(text, JsonUtil.Options);
                if (response is null) continue;
                if (action != "home" && response.List.Count == 0) continue;
                response.Page = NormalizePage(response.Page, 1);
                response.PageCount = NormalizePage(response.PageCount, 1);
                response.Limit = NormalizePage(response.Limit, 0);
                return response;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or TimeoutException)
            {
                Diagnostics.Log($"安卓兼容 CMS 兜底端点失败 ({baseUrl.IdnHost}): {ex.GetType().Name}");
            }
        }
        return null;
    }

    /// <summary>从站点 Ext 中解析可用的 HTTP(S) 基础地址（字符串或 siteUrl/siteUrls 字段）。</summary>
    internal static Uri? TryResolveBase(JsonElement extension)
    {
        var candidates = new List<string?>();
        if (extension.ValueKind == JsonValueKind.String) candidates.Add(extension.GetString());
        else if (extension.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in (ReadOnlySpan<string>)["siteUrl", "api", "url"])
                if (extension.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    candidates.Add(value.GetString());
            if (extension.TryGetProperty("siteUrls", out var mirrors) && mirrors.ValueKind == JsonValueKind.Array)
                foreach (var mirror in mirrors.EnumerateArray())
                    if (mirror.ValueKind == JsonValueKind.String) candidates.Add(mirror.GetString());
        }
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate?.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) continue;
            return uri;
        }
        return null;
    }

    private static IEnumerable<string> CandidateEndpoints(Uri baseUri)
    {
        var root = baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/');
        // 影视仓/OK影视通用的 MacCMS JSON 接口位置族，逐一按序探测
        yield return root + "/api.php/provide/vod/";
        yield return root + "/provide/vod/";
        yield return root + "/api.php/provide/vod";
        if (baseUri.AbsolutePath.TrimEnd('/').Length == 0) yield return baseUri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private static string BuildQuery(string action, IReadOnlyDictionary<string, object?> request)
    {
        string? Read(string key) => request.TryGetValue(key, out var value) ? value?.ToString() : null;
        var pairs = new List<(string Key, string Value)>();
        switch (action)
        {
            case "home":
                pairs.Add(("ac", "list"));
                pairs.Add(("pg", "1"));
                break;
            case "category":
                pairs.Add(("ac", "detail"));
                if (!string.IsNullOrEmpty(Read("tid"))) pairs.Add(("t", Read("tid")!));
                pairs.Add(("pg", string.IsNullOrEmpty(Read("pg")) ? "1" : Read("pg")!));
                break;
            case "search":
                pairs.Add(("ac", "detail"));
                if (!string.IsNullOrEmpty(Read("keyword"))) pairs.Add(("wd", Read("keyword")!));
                pairs.Add(("pg", string.IsNullOrEmpty(Read("pg")) ? "1" : Read("pg")!));
                break;
            case "detail":
                pairs.Add(("ac", "detail"));
                if (!string.IsNullOrEmpty(Read("id"))) pairs.Add(("ids", Read("id")!));
                break;
            default:
                return string.Empty;
        }
        return string.Join('&', pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    private static int NormalizePage(int value, int fallback) => value is > 0 and < int.MaxValue ? value : fallback;
}
