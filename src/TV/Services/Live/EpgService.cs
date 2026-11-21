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
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EpgService(SourceHttpClient http) => _http = http;

    public async Task<string?> GetCurrentProgramAsync(LiveChannel channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel.EpgUrl)) return null;
        var name = string.IsNullOrWhiteSpace(channel.EpgName) ? channel.Name : channel.EpgName;
        var url = channel.EpgUrl.Replace("{name}", Uri.EscapeDataString(name), StringComparison.OrdinalIgnoreCase);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cache.TryGetValue(url, out var cache) || cache.LoadedAt < DateTimeOffset.UtcNow.AddMinutes(-10))
            {
                var payload = await _http.GetBytesAsync(url, cancellationToken: cancellationToken, scopeRedirectHeaders: true).ConfigureAwait(false);
                var loadedAt = DateTimeOffset.UtcNow;
                cache = new Cache(loadedAt, Parse(payload.Content, loadedAt));
                if (_cache.Count > 8) _cache.Remove(_cache.Keys.First());
                _cache[url] = cache;
            }
            var now = DateTimeOffset.Now;
            Program? Find(string id) => id.Length == 0 ? null : cache.Programs.FirstOrDefault(program =>
                program.ChannelId.Equals(id, StringComparison.OrdinalIgnoreCase) && program.Start <= now && program.End > now);
            var current = Find(channel.EpgId) ?? Find(name) ?? Find(channel.Name);
            return current is null ? null :
                $"{current.Title}  {current.Start.ToLocalTime():HH:mm}–{current.End.ToLocalTime():HH:mm}" +
                (string.IsNullOrWhiteSpace(current.Description) ? string.Empty : $"\n{current.Description}");
        }
        finally { _lock.Release(); }
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
        var document = XDocument.Load(reader);
        var programmes = document.Root?.Elements("programme") ?? Enumerable.Empty<XElement>();
        var cacheUntil = loadedAt.AddMinutes(10);
        return programmes.Select(element => new Program(
            element.Attribute("channel")?.Value ?? string.Empty,
            element.Element("title")?.Value.Trim() ?? string.Empty,
            element.Element("desc")?.Value.Trim() ?? string.Empty,
            ParseTime(element.Attribute("start")?.Value),
            ParseTime(element.Attribute("stop")?.Value)))
            .Where(program => program.ChannelId.Length > 0 && program.Start != default &&
                program.End > program.Start && program.End > loadedAt && program.Start <= cacheUntil)
            .ToList();
    }

    private static DateTimeOffset ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        return DateTimeOffset.TryParseExact(value.Trim(), ["yyyyMMddHHmmss zzz", "yyyyMMddHHmmsszzz", "yyyyMMddHHmm zzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : default;
    }
}
