using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using TV.Models;

namespace TV.Services.Cloud;

public sealed class PrefetchStream : Stream
{
    private readonly HttpClient _http;
    private string _url;
    private IReadOnlyDictionary<string, string> _headers;
    private readonly long _endExclusive;
    private readonly long _fileLength;
    private readonly int _chunkSize;
    private int _threads;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _lifetime;
    private readonly EntityTagHeaderValue? _entityTag;
    private readonly Func<CancellationToken, Task<CloudPlayResult>>? _refresh;
    private readonly ConcurrentDictionary<long, Task<byte[]>> _chunks = new();
    private Stream? _current;
    private long _nextPosition;
    private bool _disposed;

    public PrefetchStream(HttpClient http, string url, IReadOnlyDictionary<string, string> headers, Stream initial,
        long initialLength, long start, long endExclusive, long fileLength, int chunkSize, int threads,
        SemaphoreSlim gate, CancellationToken cancellationToken, EntityTagHeaderValue? entityTag,
        Func<CancellationToken, Task<CloudPlayResult>>? refresh = null)
    {
        _http = http;
        _url = url;
        _headers = headers;
        _current = initial;
        _endExclusive = endExclusive;
        _fileLength = fileLength;
        _chunkSize = chunkSize;
        _threads = Math.Clamp(threads, 1, 3);
        _gate = gate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _entityTag = entityTag;
        _refresh = refresh;
        _nextPosition = start + Math.Max(0, initialLength);
        ScheduleWindow();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            if (_disposed) return 0;
            if (_current is not null)
            {
                var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0) return read;
                _current.Dispose();
                _current = null;
            }
            if (_nextPosition >= _endExclusive) return 0;
            var start = _nextPosition;
            if (!_chunks.TryRemove(start, out var task)) task = FetchAsync(start, _lifetime.Token);
            var bytes = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _nextPosition += bytes.Length;
            _current = new MemoryStream(bytes, writable: false);
            ScheduleWindow();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    private void ScheduleWindow()
    {
        for (var i = 0; i < _threads - 1; i++)
        {
            var distance = (long)i * _chunkSize;
            if (_disposed || distance >= _endExclusive - _nextPosition) break;
            var start = _nextPosition + distance;
            if (!_chunks.ContainsKey(start)) _chunks.TryAdd(start, FetchAsync(start, _lifetime.Token));
        }
    }

    private async Task<byte[]> FetchAsync(long start, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(18));
                    var token = deadline.Token;
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    foreach (var (name, value) in _headers)
                        if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                            request.Headers.TryAddWithoutValidation(name, value);
                    var size = (int)Math.Min(_endExclusive - start, _chunkSize);
                    var end = start + size - 1;
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    if (_entityTag is { IsWeak: false }) request.Headers.IfRange = new RangeConditionHeaderValue(_entityTag);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (_refresh is not null && (int)response.StatusCode is 401 or 403 or 412)
                    {
                        var refreshed = await _refresh(token).ConfigureAwait(false);
                        _url = refreshed.Url;
                        _headers = refreshed.Headers;
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    {
                        _threads = 1;
                        var retryAfter = response.Headers.RetryAfter?.Delta;
                        var delay = retryAfter is { } value ? TimeSpan.FromMilliseconds(Math.Min(value.TotalMilliseconds, 5000)) : TimeSpan.FromMilliseconds(400 * (1 << attempt));
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range is not { Unit: "bytes" } ||
                        range.From != start || range.To != end || range.Length != _fileLength ||
                        response.Content.Headers.ContentLength is { } length && length != size)
                        throw new InvalidDataException("媒体服务器返回了错误的分块范围。");
                    await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var bytes = GC.AllocateUninitializedArray<byte>(size);
                    await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                    return bytes;
                }
                catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested &&
                    ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    await Task.Delay(400 * (1 << attempt), cancellationToken).ConfigureAwait(false);
                }
            }
            throw new IOException("媒体分块读取失败。");
        }
        finally { _gate.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _current?.Dispose();
            _current = null;
            _chunks.Clear();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
