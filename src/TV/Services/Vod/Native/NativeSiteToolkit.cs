using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TV.Models;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Shared, reusable primitives extracted from native VOD site adapters:
/// browser headers, multi-origin HTML fetching with browser-challenge detection,
/// URL/image/label normalization, JSON helpers, MacCMS player_aaaa decoding,
/// Stui/MacCMS playlist scraping, and cloud share expansion.
/// </summary>
internal static partial class NativeSiteToolkit
{
    internal const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    internal sealed record MacCmsPlayerInfo(string RawUrl, string DecodedUrl, string From, int Encrypt);

    internal static Dictionary<string, string> CreateBrowserHeaders(
        Uri? referer = null,
        IReadOnlyDictionary<string, string>? siteHeaders = null,
        bool includeOrigin = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (siteHeaders is not null)
        {
            foreach (var (key, value) in siteHeaders)
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    headers[key] = value;
        }
        headers.TryAdd("User-Agent", BrowserUserAgent);
        if (referer is not null)
        {
            headers.TryAdd("Referer", referer.AbsoluteUri);
            if (includeOrigin)
                headers.TryAdd("Origin", referer.GetLeftPart(UriPartial.Authority));
        }
        return headers;
    }

    internal static string ResolveUrl(Uri baseUri, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            trimmed = baseUri.Scheme + ":" + trimmed;
        return Uri.TryCreate(baseUri, trimmed, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : string.Empty;
    }

    internal static string ExtractImageUrl(IElement? element, Uri baseUri)
    {
        if (element is null) return string.Empty;
        var raw = FirstNonEmpty(
            element.GetAttribute("data-original"),
            element.GetAttribute("data-src"),
            element.GetAttribute("data-lazy-src"),
            element.GetAttribute("data-echo"),
            element.GetAttribute("src"));
        if (raw.Length == 0 && element.QuerySelector("img") is { } nested)
        {
            raw = FirstNonEmpty(
                nested.GetAttribute("data-original"),
                nested.GetAttribute("data-src"),
                nested.GetAttribute("data-lazy-src"),
                nested.GetAttribute("data-echo"),
                nested.GetAttribute("src"));
        }
        if (raw.Length == 0 && element.GetAttribute("style") is { Length: > 0 } style)
        {
            var bgMatch = BackgroundUrlRegex().Match(style);
            if (bgMatch.Success) raw = bgMatch.Groups[1].Value.Trim('"', '\'', ' ');
        }
        return ResolveUrl(baseUri, raw);
    }

    internal static string CollapseSpaces(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string SanitizeLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : CollapseSpaces(value)
                .Replace("$", "＄", StringComparison.Ordinal)
                .Replace("#", "＃", StringComparison.Ordinal);

    internal static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return string.Empty;
    }

    internal static string ReadJsonString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var prop)) return string.Empty;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => string.Empty
        };
    }

    internal static string ReadJsonStringAny(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadJsonString(obj, name);
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    internal static int ReadJsonInt(JsonElement obj, string name, int fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)) return n;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString()?.Trim(), out var parsed)) return parsed;
        return fallback;
    }

    internal static bool IsBrowserChallenge(HttpExchangeResult exchange, string html) =>
        exchange.StatusCode == HttpStatusCode.Forbidden ||
        exchange.HasCloudflareChallenge ||
        html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("browser verification required", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase) &&
            (html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("_cf_chl_opt", StringComparison.OrdinalIgnoreCase));

    internal static async Task<(IDocument Document, Uri FinalUri)> FetchHtmlAsync(
        SourceHttpClient http,
        SourceSite site,
        IEnumerable<Uri> origins,
        string relativePath,
        string siteLabel,
        CancellationToken token,
        bool detectChallenge = true,
        int maxBytes = 4 * 1024 * 1024,
        int minHtmlLength = 300)
    {
        Uri? challengedOrigin = null;
        Exception? lastError = null;
        var siteHeaders = site.GetHeaders();
        foreach (var origin in origins)
        {
            var target = Uri.TryCreate(relativePath, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https"
                ? abs : new Uri(origin, relativePath);
            var headers = CreateBrowserHeaders(origin, siteHeaders);
            try
            {
                var exchange = await http.GetExchangeAsync(target.AbsoluteUri, headers, maxBytes: maxBytes, cancellationToken: token).ConfigureAwait(false);
                var html = SourceHttpClient.DecodeText(exchange.Payload.Content);
                if (detectChallenge && IsBrowserChallenge(exchange, html))
                {
                    challengedOrigin ??= target;
                    continue;
                }
                if ((int)exchange.StatusCode is >= 200 and < 300 && html.Length >= minHtmlLength)
                {
                    var doc = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
                    return (doc, exchange.Payload.FinalUri);
                }
                lastError = new HttpRequestException($"{siteLabel}站点返回 HTTP {(int)exchange.StatusCode}", null, exchange.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        if (challengedOrigin is not null)
        {
            throw new SiteVerificationRequiredException(
                site.Key, site.Name, challengedOrigin, SiteVerificationKind.BrowserChallenge,
                $"{siteLabel}站点需要完成浏览器安全验证后访问。");
        }
        throw lastError ?? new HttpRequestException($"{siteLabel}站点当前无法访问。");
    }

    internal static bool TryParsePlayerAaaa(string html, out MacCmsPlayerInfo info)
    {
        info = default!;
        var match = PlayerAaaaRegex().Match(html);
        if (!match.Success) return false;
        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            var rawUrl = ReadJsonString(root, "url");
            var from = ReadJsonString(root, "from");
            var encrypt = ReadJsonInt(root, "encrypt", 0);
            var decoded = DecodeMacUrl(rawUrl, encrypt);
            info = new MacCmsPlayerInfo(rawUrl, decoded, from, encrypt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string DecodeMacUrl(string raw, int encrypt)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            return encrypt switch
            {
                1 => Uri.UnescapeDataString(raw),
                2 => Uri.UnescapeDataString(Encoding.UTF8.GetString(Convert.FromBase64String(raw))),
                _ => raw
            };
        }
        catch (Exception)
        {
            return raw;
        }
    }

    internal static (List<string> LineNames, List<string> LineUrls) ExtractStuiPlaylists(
        IDocument doc, Uri pageUri, string tabSelector, string listSelector, string? fallbackLinePrefix = null)
    {
        var lineNames = new List<string>();
        var lineUrls = new List<string>();
        var tabs = doc.QuerySelectorAll(tabSelector)
            .Select(e => CollapseSpaces(e.TextContent))
            .Where(s => s.Length > 0
                && !s.Contains("简介", StringComparison.Ordinal)
                && !s.Contains("猜你喜欢", StringComparison.Ordinal)
                && !s.Contains("推荐", StringComparison.Ordinal))
            .ToList();

        var lists = doc.QuerySelectorAll(listSelector);
        for (var i = 0; i < lists.Length; i++)
        {
            var eps = new List<string>();
            foreach (var a in lists[i].QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href")?.Trim() ?? string.Empty;
                var label = SanitizeLabel(a.TextContent);
                if (href.Length == 0 || label.Length == 0 || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                var fullUri = ResolveUrl(pageUri, href);
                if (fullUri.Length == 0) continue;
                eps.Add($"{label}${fullUri}");
            }
            if (eps.Count == 0) continue;
            var name = i < tabs.Count ? SanitizeLabel(tabs[i]) : $"{fallbackLinePrefix ?? "线路"}{lineNames.Count + 1}";
            lineNames.Add(name);
            lineUrls.Add(string.Join('#', eps));
        }
        return (lineNames, lineUrls);
    }

    internal static async Task AppendCloudShareLinesAsync(
        CloudDriveService cloud,
        IDocument doc,
        List<string> lineNames,
        List<string> lineUrls,
        CancellationToken token,
        bool allowMultiplePerProvider = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shareLinks = new List<CloudShareLink>();
        foreach (var node in doc.QuerySelectorAll("[data-clipboard-text], a[href]"))
        {
            var raw = node.GetAttribute("data-clipboard-text") ?? node.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(raw) || !CloudShareLink.TryParse(raw.Trim(), out var parsed)) continue;
            var shareUrl = parsed.ToShareUrl();
            if (seen.Add(shareUrl)) shareLinks.Add(parsed);
        }

        foreach (var shareLink in shareLinks)
        {
            var shareUrl = shareLink.ToShareUrl();
            var providerName = CloudSearchDetail.ProviderName(shareLink.Provider);
            if (!allowMultiplePerProvider && lineNames.Any(n => n.Contains(providerName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var lineTitle = lineNames.Contains(providerName) ? $"{providerName}#{lineNames.Count + 1}" : providerName;
            try
            {
                var share = await cloud.GetShareAsync(shareUrl, token).ConfigureAwait(false);
                if (share.Files.Count > 0)
                {
                    lineNames.Add(lineTitle);
                    lineUrls.Add(string.Join('#', share.Files.Select(CloudDriveService.FormatEpisode)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lineNames.Add(lineTitle);
                lineUrls.Add($"登录后加载分享${shareUrl}");
            }
        }
    }

    internal static async Task<SpiderPlayResult?> TryResolveDirectCloudShareAsync(
        CloudDriveService cloud, string candidateUrl, CancellationToken token, string? emptyMessage = null)
    {
        if (!CloudShareLink.TryParse(candidateUrl, out var shareLink)) return null;
        var share = await cloud.GetShareAsync(shareLink.ToShareUrl(), token).ConfigureAwait(false);
        if (share.Files.Count == 0)
            throw new InvalidDataException(emptyMessage ?? "网盘分享中没有可播放的媒体文件。");
        var resolved = await cloud.ResolveAsync(
            CloudDriveService.GetPlaybackId(share.Files[0]),
            new QualityOption("原画", 0) { IsOriginal = true },
            token).ConfigureAwait(false);
        return new SpiderPlayResult(
            resolved.Url,
            new Dictionary<string, string>(resolved.Headers, StringComparer.OrdinalIgnoreCase),
            0, 0);
    }

    internal static IReadOnlyList<Uri> ExtractConfiguredOrigins(SourceSite site) =>
        MergeConfiguredOrigins(site, []).ToArray();

    internal static IEnumerable<Uri> MergeConfiguredOrigins(SourceSite site, IEnumerable<Uri> fallbacks)
    {
        var list = new List<Uri>();
        void AddOrigin(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var token in raw.Split(["$$$", ",", ";", "|", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(token, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                {
                    var root = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
                    if (!list.Any(u => u.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase) && u.Port == root.Port))
                        list.Add(root);
                }
            }
        }

        void AddElement(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
                AddOrigin(el.GetString());
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) AddOrigin(item.GetString());
            }
        }

        if (site.Ext.ValueKind is JsonValueKind.String or JsonValueKind.Array)
        {
            AddElement(site.Ext);
        }
        else if (site.Ext.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in new[] { "siteUrl", "url", "host", "site", "sites", "urls", "domains", "api" })
            {
                if (site.Ext.TryGetProperty(prop, out var value))
                    AddElement(value);
            }
        }

        foreach (var fb in fallbacks)
        {
            if (!list.Any(u => u.Host.Equals(fb.Host, StringComparison.OrdinalIgnoreCase) && u.Port == fb.Port))
                list.Add(fb);
        }
        return list;
    }

    [GeneratedRegex(@"player_aaaa\s*=\s*(\{.*?\})\s*;?\s*(?:</script>|[\r\n]|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PlayerAaaaRegex();

    [GeneratedRegex(@"url\(\s*['""]?([^'""\)]+)['""]?\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackgroundUrlRegex();
}
