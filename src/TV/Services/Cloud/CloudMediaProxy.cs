using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Http;
using TV.Services.Playback;

namespace TV.Services.Cloud;

public sealed class CloudMediaProxy : IDisposable
{
    private sealed record Session(CloudPlayResult Result, CloudProviderKind? Provider, int Threads, int ChunkSize, DateTimeOffset ExpiresAt, string? Manifest,
        Func<CancellationToken, Task<CloudPlayResult>>? Refresh = null)
    {
        public ConcurrentDictionary<string, Lazy<string>> Resources { get; } = new(StringComparer.Ordinal);
    }
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<CloudProviderKind, SemaphoreSlim> _gates = new();
    private readonly SemaphoreSlim _mediaRangeGate = new(3);
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private long _nextSweepAt;

    public CloudMediaProxy(DnsResolverService? dns = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        if (dns is not null) handler.ConnectCallback = dns.ConnectAsync;
        _http = new HttpClient(handler);
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = AcceptLoopAsync();
    }

    public string BaseUrl { get; }
    public event EventHandler<CloudProviderKind>? AuthenticationRequired;

    public string Register(CloudPlayResult result, CloudProviderKind? provider, int threads, int chunkSize, string? manifest = null,
        Func<CancellationToken, Task<CloudPlayResult>>? refresh = null)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions.Where(pair => pair.Value.ExpiresAt < now)) ReleaseSession(pair.Key);
        if (_sessions.Count >= 64)
            foreach (var pair in _sessions.OrderBy(pair => pair.Value.ExpiresAt).Take(_sessions.Count - 63)) ReleaseSession(pair.Key);
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = new Session(result, provider, Math.Clamp(threads, 1, 3), Math.Clamp(chunkSize, 2, 8) * 1024 * 1024, now.AddHours(4), manifest, refresh);
        var ticks = Environment.TickCount64;
        var nextSweep = Interlocked.Read(ref _nextSweepAt);
        if (ticks >= nextSweep && Interlocked.CompareExchange(ref _nextSweepAt, ticks + 60_000, nextSweep) == nextSweep)
            foreach (var pair in _sessions.Where(pair => pair.Value.ExpiresAt < DateTimeOffset.UtcNow)) _sessions.TryRemove(pair);
        var extension = DashManifest.IsPlaylist(result.Url, result.ContentType) ? ".mpd" :
            manifest is not null || HlsManifest.IsPlaylist(result.Url, result.ContentType)
                ? ".m3u8" : result.FileExtension ?? Path.GetExtension(new Uri(result.Url).AbsolutePath);
        if (extension.Length > 8 || extension.Any(ch => ch != '.' && !char.IsAsciiLetterOrDigit(ch))) extension = string.Empty;
        return BaseUrl + "media/" + id + extension;
    }

    public void ReleaseAddress(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback) return;
        var id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('.', 2)[0];
        if (!string.IsNullOrWhiteSpace(id)) ReleaseSession(id);
    }

    private void ReleaseSession(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return;
        foreach (var resource in session.Resources.Values)
            if (resource.IsValueCreated) ReleaseAddress(resource.Value);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleAsync(client);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var header = await ReadHeaderAsync(stream, headerTimeout.Token).ConfigureAwait(false);
                var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var requestParts = lines[0].Split(' ');
                if (requestParts.Length < 2 || requestParts[0] is not ("GET" or "HEAD")) return;
                var id = (requestParts[1].Split('/').LastOrDefault() ?? string.Empty).Split('.', 2)[0];
                if (!_sessions.TryGetValue(id, out var session))
                {
                    await WriteResponseHeadAsync(stream, 404, "Not Found", "text/plain", 0).ConfigureAwait(false);
                    return;
                }
                _sessions[id] = session with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(4) };
                if (session.Manifest is { } master)
                {
                    var dash = DashManifest.IsPlaylist(session.Result.Url, session.Result.ContentType);
                    var bytes = Encoding.UTF8.GetBytes(dash ? master : RewritePlaylist(master, new Uri(session.Result.Url), session));
                    await WriteResponseHeadAsync(stream, 200, "OK", dash ? DashManifest.ContentType : "application/vnd.apple.mpegurl", bytes.Length).ConfigureAwait(false);
                    if (requestParts[0] != "HEAD") await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                    return;
                }
                var incoming = ParseHeaders(lines);
                var head = requestParts[0] == "HEAD";
                var origin = new Uri(session.Result.Url);
                var isPngWrappedSegment = session.Result.UnwrapPngHlsSegments && session.Result.FileExtension == ".ts";
                var range = isPngWrappedSegment || HlsManifest.IsPlaylist(session.Result.Url, session.Result.ContentType)
                    ? null : incoming.GetValueOrDefault("Range");
                var agent = incoming.GetValueOrDefault("User-Agent");
                var planned = head ? null : PlanPrefetch(session, range);
                var upstream = await SendUpstreamAsync(head ? HttpMethod.Head : HttpMethod.Get, origin, session.Result.Headers, planned?.InitialRange ?? range, agent).ConfigureAwait(false);
                if (planned is not null && !AcceptInitialRange(upstream.Response, planned))
                {
                    if (upstream.Response.StatusCode != HttpStatusCode.OK)
                    {
                        upstream.Response.Dispose();
                        upstream = await SendUpstreamAsync(HttpMethod.Get, origin, session.Result.Headers, range, agent).ConfigureAwait(false);
                    }
                    planned = null;
                }
                if (session.Refresh is not null &&
                    (session.Result.ExpiresAt <= DateTimeOffset.UtcNow || (int)upstream.Response.StatusCode is 401 or 403 or 412))
                {
                    upstream.Response.Dispose();
                    var refreshed = await session.Refresh(_cts.Token).ConfigureAwait(false);
                    session = session with { Result = refreshed };
                    _sessions[id] = session;
                    origin = new Uri(refreshed.Url);
                    planned = head ? null : PlanPrefetch(session, range);
                    upstream = await SendUpstreamAsync(head ? HttpMethod.Head : HttpMethod.Get, origin, refreshed.Headers,
                        planned?.InitialRange ?? range, agent).ConfigureAwait(false);
                    if (planned is not null && !AcceptInitialRange(upstream.Response, planned))
                    {
                        if (upstream.Response.StatusCode != HttpStatusCode.OK)
                        {
                            upstream.Response.Dispose();
                            upstream = await SendUpstreamAsync(HttpMethod.Get, origin, refreshed.Headers, range, agent).ConfigureAwait(false);
                        }
                        planned = null;
                    }
                }
                using var response = upstream.Response;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                var length = response.Content.Headers.ContentLength ?? -1;
                var sessionHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "Content-Range", "ETag", "Last-Modified", "Content-Disposition" })
                    if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                        sessionHeaders[name] = string.Join(',', values);
                if (HlsManifest.IsPlaylist(upstream.FinalUri.AbsoluteUri, contentType) || HlsManifest.IsPlaylist(session.Result.Url, session.Result.ContentType))
                {
                    if (head || !response.IsSuccessStatusCode)
                    {
                        await WriteResponseHeadAsync(stream, (int)response.StatusCode, response.ReasonPhrase ?? "OK", contentType, head ? -1 : 0).ConfigureAwait(false);
                        return;
                    }
                    await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, _cts.Token).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                    if (!HlsManifest.IsManifest(text))
                    {
                        await WriteResponseHeadAsync(stream, 502, "Bad Gateway", "text/plain", 0).ConfigureAwait(false);
                        return;
                    }
                    var bytes = Encoding.UTF8.GetBytes(RewritePlaylist(text, upstream.FinalUri, session));
                    await WriteResponseHeadAsync(stream, 200, "OK", "application/vnd.apple.mpegurl", bytes.Length).ConfigureAwait(false);
                    await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                    return;
                }
                if (head)
                {
                    await WriteResponseHeadAsync(stream, (int)response.StatusCode, response.ReasonPhrase ?? "OK", contentType, length, sessionHeaders).ConfigureAwait(false);
                    return;
                }
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? "OK";
                // Some segment hosts reply with a complete 206 even when VLC requested the whole file.
                if (range is null && response.StatusCode == HttpStatusCode.PartialContent &&
                    response.Content.Headers.ContentRange is { Unit: "bytes", From: 0, To: { } endByte, Length: { } total } &&
                    endByte == total - 1 && length == total)
                {
                    sessionHeaders.Remove("Content-Range");
                    statusCode = 200;
                    reason = "OK";
                }
                await using var source = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
                PrefetchStream? prefetched = null;
                if (planned is not null)
                {
                    var received = response.Content.Headers.ContentRange!;
                    var totalLength = received.Length!.Value;
                    var end = Math.Min(planned.ClientEnd ?? totalLength - 1, totalLength - 1);
                    if (planned.ClientHasRange) sessionHeaders["Content-Range"] = $"bytes {planned.Start}-{end}/{totalLength}";
                    else sessionHeaders.Remove("Content-Range");
                    statusCode = planned.ClientHasRange ? 206 : 200;
                    reason = planned.ClientHasRange ? "Partial Content" : "OK";
                    if (received.To!.Value < end)
                    {
                        var gate = session.Provider is { } provider ? _gates.GetOrAdd(provider, _ => new SemaphoreSlim(3)) : _mediaRangeGate;
                        prefetched = new PrefetchStream(_http, upstream.FinalUri.AbsoluteUri, upstream.Headers, source, length,
                            planned.Start, end + 1, totalLength, session.ChunkSize, session.Threads, gate, _cts.Token, response.Headers.ETag,
                            session.Refresh);
                    }
                    length = end - planned.Start + 1;
                }
                await using (prefetched)
                {
                    if (prefetched is null && isPngWrappedSegment && statusCode == 200 && contentType == "image/png")
                    {
                        await SendPngWrappedSegmentAsync(stream, source, length, sessionHeaders).ConfigureAwait(false);
                        return;
                    }
                    await WriteResponseHeadAsync(stream, statusCode, reason, contentType, length, sessionHeaders).ConfigureAwait(false);
                    await CopyToClientAsync(prefetched ?? source, stream).ConfigureAwait(false);
                }
            }
            catch (CloudAuthenticationException ex)
            {
                Diagnostics.Log($"Cloud authentication expired: {ex.Provider}");
                AuthenticationRequired?.Invoke(this, ex.Provider);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Diagnostics.Log($"Cloud proxy failed: {ex.GetType().Name}"); }
        }
    }

    private async Task SendPngWrappedSegmentAsync(Stream downstream, Stream upstream, long length, Dictionary<string, string> headers)
    {
        var buffer = new byte[4096];
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var read = await upstream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: readTimeout.Token).ConfigureAwait(false);
        var offset = FindPngWrappedTs(buffer.AsSpan(0, read), length);
        if (offset > 0)
        {
            if (length >= 0) length -= offset;
            headers.Remove("Content-Disposition");
            headers.Remove("ETag");
        }
        await WriteResponseHeadAsync(downstream, 200, "OK", offset > 0 ? "video/mp2t" : "image/png", length, headers).ConfigureAwait(false);
        await downstream.WriteAsync(buffer.AsMemory(offset, read - offset), _cts.Token).ConfigureAwait(false);
        await CopyToClientAsync(upstream, downstream).ConfigureAwait(false);
    }

    private static int FindPngWrappedTs(ReadOnlySpan<byte> bytes, long length)
    {
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!bytes.StartsWith(pngSignature)) return 0;
        const int packetLength = 188;
        for (var offset = 8; bytes.Length - offset >= 12;)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (chunkLength > bytes.Length - offset - 12) return 0;
            var isEnd = chunkLength == 0 && bytes.Slice(offset + 4, 4).SequenceEqual("IEND"u8);
            offset += (int)chunkLength + 12;
            if (!isEnd) continue;
            var hasPackets = bytes.Length - offset > 2 * packetLength &&
                bytes[offset] == 0x47 && bytes[offset + packetLength] == 0x47 && bytes[offset + 2 * packetLength] == 0x47;
            return hasPackets && (length < 0 || (length - offset) % packetLength == 0) ? offset : 0;
        }
        return 0;
    }

    private sealed record PlannedRange(long Start, long? ClientEnd, bool ClientHasRange, string InitialRange);
    private static PlannedRange? PlanPrefetch(Session session, string? range)
    {
        if ((session.Provider is null && !session.Result.UseBoundedRanges) || session.Threads <= 1 ||
            HlsManifest.IsPlaylist(session.Result.Url, session.Result.ContentType))
            return null;
        var origin = new Uri(session.Result.Url);
        if (!session.Result.UseBoundedRanges && session.Result.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true &&
            Path.GetExtension(origin.AbsolutePath).ToLowerInvariant() is not (".mp4" or ".mkv" or ".mov" or ".webm" or ".avi"))
            return null;
        var start = 0L;
        long? cap = null;
        if (range is not null)
        {
            if (!RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" ||
                parsed.Ranges.Count != 1 || parsed.Ranges.Single() is not { From: { } from } segment) return null;
            start = from;
            cap = segment.To;
        }
        if (start > long.MaxValue - session.ChunkSize || cap is { } end && end - start < session.ChunkSize) return null;
        return new PlannedRange(start, cap, range is not null, $"bytes={start}-{start + session.ChunkSize - 1}");
    }

    private static bool AcceptInitialRange(HttpResponseMessage response, PlannedRange plan) =>
        response.StatusCode == HttpStatusCode.PartialContent &&
        response.Content.Headers.ContentType?.MediaType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) != true &&
        response.Content.Headers.ContentRange is { Unit: "bytes", From: { } from, To: { } to, Length: { } total } &&
        from == plan.Start && to >= from && to < total &&
        (plan.ClientEnd is null || to <= plan.ClientEnd.Value) && response.Content.Headers.ContentLength == to - from + 1;

    private async Task<(HttpResponseMessage Response, Uri FinalUri, IReadOnlyDictionary<string, string> Headers)> SendUpstreamAsync(
        HttpMethod method, Uri origin, IReadOnlyDictionary<string, string> headers, string? range, string? agent)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(18));
        var uri = origin;
        for (var count = 0; ; count++)
        {
            var scoped = new Dictionary<string, string>(MediaRequestHeaders.ForTarget(headers, origin, uri), StringComparer.OrdinalIgnoreCase);
            if (agent is not null) scoped.TryAdd("User-Agent", agent);
            using var request = new HttpRequestMessage(method, uri);
            foreach (var (name, value) in scoped)
                if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !name.Equals("Range", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(name, value);
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
                return (response, uri, scoped);
            using (response)
            {
                if (count >= 7 || !Uri.TryCreate(uri, response.Headers.Location, out var target) || target.Scheme is not ("http" or "https") ||
                    target.UserInfo.Length > 0)
                    throw new InvalidDataException("媒体重定向无效。");
                if (uri.Scheme == "https" && target.Scheme == "http")
                {
                    // OTTIPTV advertises Huya's FLV edge with an HTTP URL. That edge also serves HTTPS.
                    if (!target.IsDefaultPort || !uri.IdnHost.EndsWith(".ottiptv.cc", StringComparison.OrdinalIgnoreCase) ||
                        !target.IdnHost.EndsWith(".flv.huya.com", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("媒体重定向无效。");
                    target = new UriBuilder(target) { Scheme = "https", Port = -1 }.Uri;
                }
                uri = target;
            }
        }
    }

    private string RewritePlaylist(string text, Uri baseUri, Session session)
    {
        string Resolve(string value, bool isPngWrappedSegment = false)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
            var uri = new Uri(baseUri, value);
            if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) return value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out _) && MediaRequestHeaders.SameOrigin(baseUri, uri) &&
                string.IsNullOrEmpty(uri.Query) && !string.IsNullOrEmpty(baseUri.Query))
                uri = new UriBuilder(uri) { Query = baseUri.Query }.Uri;
            var original = new Uri(session.Result.Url);
            var scoped = MediaRequestHeaders.ForTarget(session.Result.Headers, original, uri);
            var cacheKey = isPngWrappedSegment ? ".ts:" + uri.AbsoluteUri : uri.AbsoluteUri;
            return session.Resources.GetOrAdd(cacheKey, _ => new Lazy<string>(() =>
                Register(new CloudPlayResult(uri.AbsoluteUri, scoped, FileExtension: isPngWrappedSegment ? ".ts" : null)
                {
                    UnwrapPngHlsSegments = session.Result.UnwrapPngHlsSegments
                }, session.Provider, session.Threads, session.ChunkSize / (1024 * 1024)))).Value;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var hasWrappedSegments = session.Result.UnwrapPngHlsSegments &&
            Array.Exists(lines, static line => line.TrimStart().StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) &&
            !Array.Exists(lines, static line => line.TrimStart().StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('#')) lines[i] = Resolve(line, hasWrappedSegments);
            else if (line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                lines[i] = Regex.Replace(lines[i], "URI=\\\"([^\\\"]+)\\\"", match => "URI=\"" + Resolve(match.Groups[1].Value) + "\"", RegexOptions.IgnoreCase);
        }
        return string.Join('\n', lines);
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[1024];
        using var memory = new MemoryStream();
        while (memory.Length < 32768)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            var text = Encoding.Latin1.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text.Split("\r\n\r\n")[0];
        }
        return Encoding.Latin1.GetString(memory.ToArray());
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var split = line.IndexOf(':');
            if (split > 0) result[line[..split].Trim()] = line[(split + 1)..].Trim();
        }
        return result;
    }

    private async Task CopyToClientAsync(Stream source, Stream destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await source.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                if (read == 0) break;
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                await destination.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task WriteResponseHeadAsync(Stream stream, int status, string reason, string type, long length, Dictionary<string, string>? headers = null)
    {
        var builder = new StringBuilder($"HTTP/1.1 {status} {Sanitize(reason)}\r\nContent-Type: {Sanitize(type)}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n");
        if (length >= 0) builder.Append("Content-Length: ").Append(length).Append("\r\n");
        if (headers is not null) foreach (var pair in headers) builder.Append(pair.Key).Append(": ").Append(Sanitize(pair.Value)).Append("\r\n");
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString())).ConfigureAwait(false);
    }

    private static string Sanitize(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _http.Dispose();
        _mediaRangeGate.Dispose();
        foreach (var gate in _gates.Values) gate.Dispose();
        _cts.Dispose();
    }
}
