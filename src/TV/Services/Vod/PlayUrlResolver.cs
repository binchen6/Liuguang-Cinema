using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Playback;

namespace TV.Services.Vod;

public sealed class PlayUrlResolver
{
    private readonly SourceHttpClient _http;

    public PlayUrlResolver(SourceHttpClient http) => _http = http;

    public async Task<string> ResolveAsync(string url, IReadOnlyList<ParseSource> parses, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        if (IsDirectMedia(url) || parses.Count == 0) return url;
        foreach (var parse in parses.Where(item => item.Type is 0 or 1 && !string.IsNullOrWhiteSpace(item.Url)))
        {
            try
            {
                if (!TryBuildParserRequest(parse.Url, url, out var parser)) continue;
                var scoped = headers is not null && Uri.TryCreate(url, UriKind.Absolute, out var original)
                    ? MediaRequestHeaders.ForTarget(headers, original, parser) : null;
                var response = await _http.GetBytesAsync(parser.AbsoluteUri, scoped, 512 * 1024,
                    cancellationToken, scopeRedirectHeaders: true, requestTimeout: TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                using var document = JsonDocument.Parse(response.Content);
                if (!TryFindUrl(document.RootElement, out var resolved)) continue;
                if (MagnetPlayback.TryParse(resolved, out _)) return resolved;
                if (Uri.TryCreate(resolved, UriKind.Absolute, out var address) && MediaRequestHeaders.IsHttpAddress(address))
                    return address.AbsoluteUri;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine($"解析线路失败: {ex.GetType().Name}");
            }
        }
        return url;
    }

    internal static bool TryBuildParserRequest(string parser, string mediaAddress, out Uri uri)
    {
        uri = null!;
        var input = Uri.EscapeDataString(mediaAddress);
        if (parser.Contains("{url}", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(parser.Replace("{url}", input, StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out uri!) &&
                MediaRequestHeaders.IsHttpAddress(uri);
        if (!Uri.TryCreate(parser, UriKind.Absolute, out var endpoint) || !MediaRequestHeaders.IsHttpAddress(endpoint)) return false;
        var fields = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(field => !field.Split('=', 2)[0].Equals("url", StringComparison.OrdinalIgnoreCase));
        uri = new UriBuilder(endpoint) { Query = string.Join('&', fields.Append("url=" + input)), Fragment = "" }.Uri;
        return true;
    }

    private static bool IsDirectMedia(string url) => IsDirectMediaUrl(url);

    public static bool IsDirectMediaUrl(string url)
    {
        if (MagnetPlayback.IsMagnet(url)) return MagnetPlayback.TryParse(url, out _);
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
            || TV.Models.AudioMedia.IsAudio(url)
            || url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindUrl(JsonElement element, out string url)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "url", "playurl", "play_url", "magnet" })
            {
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    url = value.GetString() ?? string.Empty;
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || MagnetPlayback.TryParse(url, out _)) return true;
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("header") || property.NameEquals("headers")) continue;
                if (TryFindUrl(property.Value, out url)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) if (TryFindUrl(item, out url)) return true;
        }
        url = string.Empty;
        return false;
    }
}
