using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Live;

public sealed class EpgService
{
    private sealed record Program(string ChannelId, string Title, string Description, DateTimeOffset Start, DateTimeOffset End);
    private sealed record Cache(DateTimeOffset LoadedAt, List<Program> Programs);
    private readonly SourceHttpClient _http;
    private readonly Dictionary<string, Cache> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<Cache>>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EpgService(SourceHttpClient http) => _http = http;

    public async Task<string?> GetCurrentProgramAsync(LiveChannel channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel.EpgUrl)) return null;
        var name = string.IsNullOrWhiteSpace(channel.EpgName) ? channel.Name : channel.EpgName;
        var url = channel.EpgUrl.Replace("{name}", Uri.EscapeDataString(name), StringComparison.OrdinalIgnoreCase);
        Cache? cache = null;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(url, out var cached) && cached.LoadedAt >= DateTimeOffset.UtcNow.AddMinutes(-10)) cache = cached;
        }
        finally { _lock.Release(); }
        if (cache is null)
        {
            var pending = _pending.GetOrAdd(url, _ => new Lazy<Task<Cache>>(() => LoadCacheAsync(url), LazyThreadSafetyMode.ExecutionAndPublication));
            cache = await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var now = DateTimeOffset.Now;
        Program? Find(string id) => id.Length == 0 ? null : cache.Programs.FirstOrDefault(program =>
            program.ChannelId.Equals(id, StringComparison.OrdinalIgnoreCase) && program.Start <= now && program.End > now);
        var current = Find(channel.EpgId) ?? Find(name) ?? Find(channel.Name);
        return current is null ? null :
            $"{current.Title}  {current.Start.ToLocalTime():HH:mm}–{current.End.ToLocalTime():HH:mm}" +
            (string.IsNullOrWhiteSpace(current.Description) ? string.Empty : $"\n{current.Description}");
    }

    private async Task<Cache> LoadCacheAsync(string url)
    {
        try
        {
            var payload = await _http.GetBytesAsync(url, cancellationToken: CancellationToken.None, scopeRedirectHeaders: true).ConfigureAwait(false);
            var loadedAt = DateTimeOffset.UtcNow;
            var cache = new Cache(loadedAt, Parse(payload.Content, loadedAt));
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cache.Count >= 8 && !_cache.ContainsKey(url))
                    _cache.Remove(_cache.MinBy(pair => pair.Value.LoadedAt).Key);
                _cache[url] = cache;
            }
            finally { _lock.Release(); }
            return cache;
        }
        finally { _pending.TryRemove(url, out _); }
    }

    private static List<Program> Parse(byte[] xml, DateTimeOffset loadedAt)
    {
        using var data = new MemoryStream(xml, writable: false);
        using Stream stream = xml is [0x1f, 0x8b, ..] ? new GZipStream(data, CompressionMode.Decompress) : data;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 32 * 1024 * 1024
        });
        var result = new List<Program>();
        var cacheUntil = loadedAt.AddMinutes(10);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme") continue;
            var channel = reader.GetAttribute("channel") ?? string.Empty;
            var start = ParseTime(reader.GetAttribute("start"));
            var end = ParseTime(reader.GetAttribute("stop"));
            using var subtree = reader.ReadSubtree();
            subtree.MoveToContent();
            var element = XElement.Load(subtree);
            if (channel.Length == 0 || start == default || end <= start || end <= loadedAt || start > cacheUntil) continue;
            result.Add(new Program(channel, element.Element("title")?.Value.Trim() ?? string.Empty,
                element.Element("desc")?.Value.Trim() ?? string.Empty, start, end));
        }
        return result;
    }

    private static DateTimeOffset ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        return DateTimeOffset.TryParseExact(value.Trim(), ["yyyyMMddHHmmss zzz", "yyyyMMddHHmmsszzz", "yyyyMMddHHmm zzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : default;
    }
}
