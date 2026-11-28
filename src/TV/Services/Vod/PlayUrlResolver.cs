using System.Text.Json;
using TV.Models;
using TV.Services.Http;

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
                var separator = parse.Url.Contains('?') ? '&' : '?';
                var scoped = headers is not null && Uri.TryCreate(url, UriKind.Absolute, out var original) &&
                    Uri.TryCreate(parse.Url, UriKind.Absolute, out var parser)
                    ? MediaRequestHeaders.ForTarget(headers, original, parser) : null;
                var response = await _http.GetStringAsync(parse.Url + separator + "url=" + Uri.EscapeDataString(url), scoped, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(response);
                if (TryFindUrl(document.RootElement, out var resolved) && Uri.TryCreate(resolved, UriKind.Absolute, out var address) &&
                    address.Scheme is "http" or "https" && address.UserInfo.Length == 0) return address.AbsoluteUri;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"解析线路失败: {ex.Message}");
            }
        }
        return url;
    }

    private static bool IsDirectMedia(string url) => IsDirectMediaUrl(url);

    public static bool IsDirectMediaUrl(string url)
    {
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
            foreach (var name in new[] { "url", "playurl", "play_url" })
            {
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    url = value.GetString() ?? string.Empty;
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
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
