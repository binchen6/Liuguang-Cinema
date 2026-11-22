using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TV.Services.Persistence;

namespace TV.Services.Http;

public sealed class DnsResolverService : IDisposable
{
    private sealed record Entry(Lazy<Task<IPAddress[]>> Task, DateTimeOffset ExpiresAt);
    private static readonly IReadOnlyDictionary<int, string[]> UdpProviders = new Dictionary<int, string[]>
    {
        [3] = ["114.114.114.114", "114.114.115.115"],
        [4] = ["101.198.198.198", "101.198.199.200"],
        [5] = ["180.76.76.76"]
    };
    private readonly DatabaseService _database;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly HttpClient _doh = new() { Timeout = TimeSpan.FromSeconds(5) };

    public DnsResolverService(DatabaseService database) => _database = database;

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        if (IPAddress.TryParse(host, out var address)) return await ConnectAsync(address, context.DnsEndPoint.Port, token).ConfigureAwait(false);
        var provider = await ProviderAsync(token).ConfigureAwait(false);
        if (provider != 0)
        {
            try
            {
                var addresses = await ResolveAsync(host, provider, token).ConfigureAwait(false);
                foreach (var item in addresses)
                {
                    try { return await ConnectAsync(item, context.DnsEndPoint.Port, token).ConfigureAwait(false); }
                    catch (Exception) when (item != addresses[^1]) { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Log("DNS fallback: " + ex.Message); }
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, context.DnsEndPoint.Port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<int> ProviderAsync(CancellationToken token) => int.TryParse(await _database.GetSettingAsync("network.dns", token).ConfigureAwait(false), out var value) ? Math.Clamp(value, 0, 5) : 0;

    private Task<IPAddress[]> ResolveAsync(string host, int provider, CancellationToken token)
    {
        var key = $"{provider}:{host}";
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Task.Value.WaitAsync(token);

        if (_cache.Count >= 256)
        {
            foreach (var pair in _cache)
                if (pair.Value.ExpiresAt <= now) _cache.TryRemove(pair.Key, out _);
            if (_cache.Count >= 256)
                foreach (var pair in _cache.OrderBy(p => p.Value.ExpiresAt).Take(_cache.Count - 200))
                    _cache.TryRemove(pair.Key, out _);
        }

        var fresh = new Entry(new Lazy<Task<IPAddress[]>>(() => ResolveCoreAsync(host, provider), LazyThreadSafetyMode.ExecutionAndPublication), now.AddMinutes(10));
        var active = cached is not null
            ? (_cache.TryUpdate(key, fresh, cached) ? fresh : _cache.GetOrAdd(key, fresh))
            : _cache.GetOrAdd(key, fresh);
        return active.Task.Value.WaitAsync(token);
    }

    private async Task<IPAddress[]> ResolveCoreAsync(string host, int provider)
    {
        try
        {
            var addresses = provider is 1 or 2 ? await ResolveDohAsync(host, provider).ConfigureAwait(false) : await ResolveUdpAsync(host, provider).ConfigureAwait(false);
            if (addresses.Length == 0) throw new InvalidDataException("没有可用的 A 记录。");
            return addresses;
        }
        catch
        {
            if (_cache.TryRemove($"{provider}:{host}", out var entry))
                _cache[$"{provider}:{host}"] = entry with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) };
            throw;
        }
    }

    private async Task<IPAddress[]> ResolveDohAsync(string host, int provider)
    {
        var endpoint = provider == 1 ? "https://dns.alidns.com/resolve" : "https://doh.pub/dns-query";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "?name=" + Uri.EscapeDataString(host) + "&type=A");
        request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using var response = await _doh.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.TryGetProperty("Status", out var status) && status.GetInt32() != 0) return [];
        if (!root.TryGetProperty("Answer", out var answers)) return [];
        return answers.EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetInt32() == 1)
            .Select(item => item.TryGetProperty("data", out var data) ? data.GetString() : null)
            .Where(value => IPAddress.TryParse(value, out _)).Select(value => IPAddress.Parse(value!)).ToArray();
    }

    private static async Task<IPAddress[]> ResolveUdpAsync(string host, int provider)
    {
        if (!UdpProviders.TryGetValue(provider, out var servers)) return [];
        var query = BuildQuery(host, out var id);
        foreach (var server in servers)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Connect(IPAddress.Parse(server), 53);
                await udp.SendAsync(query, query.Length).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                var response = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                return ParseResponse(response.Buffer, id);
            }
            catch (TimeoutException) { }
        }
        return [];
    }

    private static byte[] BuildQuery(string host, out ushort id)
    {
        id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        using var memory = new MemoryStream();
        void U16(ushort value) { memory.WriteByte((byte)(value >> 8)); memory.WriteByte((byte)value); }
        U16(id); U16(0x0100); U16(1); U16(0); U16(0); U16(0);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            memory.WriteByte((byte)bytes.Length);
            memory.Write(bytes);
        }
        memory.WriteByte(0); U16(1); U16(1);
        return memory.ToArray();
    }

    private static IPAddress[] ParseResponse(byte[] data, ushort id)
    {
        if (data.Length < 12 || data[0] != id >> 8 || data[1] != (byte)id) return [];
        var questions = data[4] << 8 | data[5];
        var answers = data[6] << 8 | data[7];
        var offset = 12;
        for (var i = 0; i < questions; i++) offset = SkipName(data, offset) + 4;
        var result = new List<IPAddress>();
        for (var i = 0; i < answers; i++)
        {
            offset = SkipName(data, offset);
            if (offset + 10 > data.Length) break;
            var type = data[offset] << 8 | data[offset + 1];
            var length = data[offset + 8] << 8 | data[offset + 9];
            offset += 10;
            if (offset + length > data.Length) break;
            if (type == 1 && length == 4) result.Add(new IPAddress(data[offset..(offset + 4)]));
            offset += length;
        }
        return result.ToArray();
    }

    private static int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            var length = data[offset++];
            if (length == 0) return offset;
            if ((length & 0xC0) == 0xC0) return offset + 1;
            offset += length;
        }
        return offset;
    }

    public void Dispose() => _doh.Dispose();
}
