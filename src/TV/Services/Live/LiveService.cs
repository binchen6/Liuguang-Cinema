using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Live;

public sealed partial class LiveService
{
    private readonly SourceHttpClient _http;

    public LiveService(SourceHttpClient http) => _http = http;

    public async Task<List<LiveChannel>> LoadAsync(IReadOnlyList<LiveSource> sources, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (var source in sources.Where(item => !string.IsNullOrWhiteSpace(item.Url)))
        {
            try
            {
                var headers = source.GetHeaders().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(source.UserAgent)) headers["User-Agent"] = source.UserAgent;
                var channels = await LoadSourceAsync(source, headers, cancellationToken).ConfigureAwait(false);
                if (channels.Count > 0) return channels;
                errors.Add($"{source.Name}: 未解析到频道");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{source.Name}: 加载超时");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{source.Name}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("直播源加载失败。" + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : string.Empty));
    }

    private async Task<List<LiveChannel>> LoadSourceAsync(LiveSource source, Dictionary<string, string> headers, CancellationToken token)
    {
        var configured = new Uri(source.Url, UriKind.Absolute);
        if (GetPublicGitHubOrigin(configured) is { } raw)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var direct = await _http.GetBytesAsync(raw.AbsoluteUri, MediaRequestHeaders.ForTarget(headers, configured, raw),
                    cancellationToken: deadline.Token, scopeRedirectHeaders: true).ConfigureAwait(false);
                var channels = Parse(SourceHttpClient.DecodeText(direct.Content), source, headers, direct.FinalUri);
                if (channels.Count > 0) return channels;
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException)
            {
                Diagnostics.Log($"直播列表原址不可用 ({source.Name}): {ex.GetType().Name}");
            }
        }

        var payload = await _http.GetBytesAsync(source.Url, headers, cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
        return Parse(SourceHttpClient.DecodeText(payload.Content), source, headers, payload.FinalUri);
    }

    private static Uri? GetPublicGitHubOrigin(Uri configured)
    {
        if (configured.Scheme != "https" || !configured.IsDefaultPort || configured.IdnHost != "ghproxy.net" ||
            configured.UserInfo.Length > 0 || configured.Query.Length > 0 ||
            !configured.AbsolutePath.StartsWith("/https://raw.githubusercontent.com/", StringComparison.Ordinal))
            return null;

        return Uri.TryCreate(configured.AbsolutePath[1..], UriKind.Absolute, out var origin) &&
            origin.Scheme == "https" && origin.IdnHost == "raw.githubusercontent.com" && origin.IsDefaultPort
            ? origin : null;
    }

    internal static bool IsPlaylist(byte[] bytes, LiveSource source)
    {
        try
        {
            return Uri.TryCreate(source.Url, UriKind.Absolute, out var origin) &&
                Parse(SourceHttpClient.DecodeText(bytes), source, [], origin).Count > 0;
        }
        catch (JsonException) { return false; }
    }

    private static List<LiveChannel> Parse(string text, LiveSource source, Dictionary<string, string> headers, Uri origin)
    {
        var trimmed = text.Trim().Trim('\uFEFF');
        if (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return ParseM3U(trimmed, source, headers, origin);
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return ParseJson(trimmed, source, headers);
        return ParseText(trimmed, source, headers);
    }

    private static List<LiveChannel> ParseM3U(string text, LiveSource source, Dictionary<string, string> headers, Uri origin)
    {
        var result = new List<LiveChannel>();
        LiveChannel? pending = null;
        using var reader = new StringReader(text);
        var playlistAttributes = ReadM3uAttributes(reader.ReadLine() ?? string.Empty);
        var epgUrl = string.IsNullOrWhiteSpace(source.Epg) ? ReadPlaylistEpg(playlistAttributes, origin) : source.Epg;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                return []; // An HLS stream is not itself a channel catalog.
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = M3uTitleSeparator(line);
                var attributes = ReadM3uAttributes(comma >= 0 ? line[..comma] : line);
                var name = comma >= 0 ? line[(comma + 1)..].Trim() : string.Empty;
                pending = new LiveChannel
                {
                    Name = name.Length > 0 ? name : attributes.GetValueOrDefault("tvg-name", "未命名频道"),
                    Group = attributes.GetValueOrDefault("group-title", string.Empty),
                    Logo = ResolveHttpLink(origin, attributes.GetValueOrDefault("tvg-logo")),
                    EpgId = attributes.GetValueOrDefault("tvg-id", string.Empty),
                    EpgName = attributes.GetValueOrDefault("tvg-name", string.Empty),
                    SourceName = source.Name,
                    EpgUrl = epgUrl,
                    Headers = new(headers, StringComparer.OrdinalIgnoreCase)
                };
                if (string.IsNullOrWhiteSpace(pending.Group)) pending.Group = "默认分组";
                if (string.IsNullOrWhiteSpace(pending.Logo)) pending.Logo = source.Logo;
            }
            else if (pending is not null && line.Length > 0 && !line.StartsWith('#'))
            {
                if (Uri.TryCreate(origin, line, out var channel) && channel.UserInfo.Length == 0 && IsChannelUrl(channel.AbsoluteUri))
                {
                    pending.Url = channel.AbsoluteUri;
                    result.Add(pending);
                }
                pending = null;
            }
        }
        return result;
    }

    private static int M3uTitleSeparator(string line)
    {
        var inAttribute = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inAttribute = !inAttribute;
            if (!inAttribute && line[i] == ',') return i;
        }
        return -1;
    }

    private static Dictionary<string, string> ReadM3uAttributes(string line)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in M3uAttribute().Matches(line))
            attributes.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        return attributes;
    }

    private static string ReadPlaylistEpg(Dictionary<string, string> attributes, Uri origin)
    {
        var urls = attributes.GetValueOrDefault("x-tvg-url") ?? attributes.GetValueOrDefault("url-tvg", string.Empty);
        foreach (var value in urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ResolveHttpLink(origin, value) is { Length: > 0 } url) return url;
        return string.Empty;
    }

    private static string ResolveHttpLink(Uri origin, string? path) =>
        !string.IsNullOrWhiteSpace(path) && Uri.TryCreate(origin, path, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : string.Empty;

    private static List<LiveChannel> ParseText(string text, LiveSource source, Dictionary<string, string> headers)
    {
        var result = new List<LiveChannel>();
        var group = "默认分组";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#GENRE#", StringComparison.OrdinalIgnoreCase)) { group = "默认分组"; continue; }
            if (line.StartsWith('#')) continue;
            var comma = line.IndexOfAny([',', '，']);
            if (comma <= 0) { group = line.TrimEnd('#'); continue; }
            var name = line[..comma].Trim();
            if (line[(comma + 1)..].Trim().Equals("#genre#", StringComparison.OrdinalIgnoreCase))
            {
                group = name;
                continue;
            }
            var urls = line[(comma + 1)..].Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var url in urls)
            {
                if (!IsChannelUrl(url)) continue;
                result.Add(new LiveChannel
                {
                    Name = name, Url = url, Group = group, Logo = source.Logo, SourceName = source.Name,
                    EpgName = name, EpgUrl = source.Epg, Headers = new(headers, StringComparer.OrdinalIgnoreCase)
                });
            }
        }
        return result;
    }

    private static List<LiveChannel> ParseJson(string text, LiveSource source, Dictionary<string, string> headers)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("channels", out var channels)) root = channels;
            else if (root.TryGetProperty("list", out var list)) root = list;
            else if (root.TryGetProperty("lives", out var lives)) root = lives;
        }
        if (root.ValueKind != JsonValueKind.Array) return [];
        var result = new List<LiveChannel>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = StringProperty(item, "name", "title", "channel");
            var url = StringProperty(item, "url", "playurl", "play_url");
            if (name.Length == 0 || !IsChannelUrl(url)) continue;
            result.Add(new LiveChannel
            {
                Name = name, Url = url, Group = StringProperty(item, "group", "category") is { Length: > 0 } group ? group : "默认分组",
                Logo = StringProperty(item, "logo", "tvg-logo"), EpgName = StringProperty(item, "tvg-name", "epg_name"),
                EpgId = StringProperty(item, "tvg-id", "epg_id"),
                SourceName = source.Name, EpgUrl = source.Epg, Headers = new(headers, StringComparer.OrdinalIgnoreCase)
            });
        }
        return result;
    }

    private static bool IsChannelUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "rtmp" or "rtsp" or "udp" or "rtp";

    private static string StringProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value)) return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText().Trim('"');
        }
        return string.Empty;
    }

    [GeneratedRegex("(?<!\\S)([\\w-]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex M3uAttribute();
}
