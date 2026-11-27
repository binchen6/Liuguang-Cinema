using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using TV.Models;
using TV.Services.Http;

namespace TV.Services.Cloud;

/// <summary>
/// Shared, bounded, progressive-streaming block cache for a single cloud media session.
/// Features seek-aware demand priority and epoch-based cancellation of stale lookahead prefetches
/// so seeking immediately frees upstream concurrency slots for the new playback position.
/// </summary>
internal sealed class CloudBlockCache : IDisposable
{
    private const int DefaultMaxCacheBytes = 48 * 1024 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(15);

    private sealed class ProgressiveBlock
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<bool>> _waiters = [];
        public byte[] Buffer { get; }
        public int ExpectedLength { get; }
        public long BlockIndex { get; }
        public bool IsPrefetch { get; }
        public long LastAccessTick;
        private int _written;
        private bool _completed;
        private Exception? _error;

        public ProgressiveBlock(long blockIndex, int expectedLength, bool isPrefetch = false)
        {
            BlockIndex = blockIndex;
            ExpectedLength = expectedLength;
            IsPrefetch = isPrefetch;
            Buffer = GC.AllocateUninitializedArray<byte>(expectedLength);
            LastAccessTick = Environment.TickCount64;
        }

        public int Written => Volatile.Read(ref _written);
        public bool IsCompleted => Volatile.Read(ref _completed);
        public Exception? Error => Volatile.Read(ref _error);

        public void Append(ReadOnlySpan<byte> data)
        {
            List<TaskCompletionSource<bool>>? toNotify = null;
            lock (_sync)
            {
                var copy = Math.Min(data.Length, ExpectedLength - _written);
                if (copy > 0)
                {
                    data[..copy].CopyTo(Buffer.AsSpan(_written, copy));
                    _written += copy;
                }
                if (_waiters.Count > 0)
                {
                    toNotify = [.. _waiters];
                    _waiters.Clear();
                }
            }
            if (toNotify is not null)
                foreach (var waiter in toNotify) waiter.TrySetResult(true);
        }

        public void Complete()
        {
            List<TaskCompletionSource<bool>>? toNotify = null;
            lock (_sync)
            {
                _completed = true;
                if (_waiters.Count > 0)
                {
                    toNotify = [.. _waiters];
                    _waiters.Clear();
                }
            }
            if (toNotify is not null)
                foreach (var waiter in toNotify) waiter.TrySetResult(true);
        }

        public void Fail(Exception ex)
        {
            List<TaskCompletionSource<bool>>? toNotify = null;
            lock (_sync)
            {
                _error = ex;
                _completed = true;
                if (_waiters.Count > 0)
                {
                    toNotify = [.. _waiters];
                    _waiters.Clear();
                }
            }
            if (toNotify is not null)
                foreach (var waiter in toNotify) waiter.TrySetResult(false);
        }

        public async ValueTask<int> ReadAsync(int offsetInBlock, Memory<byte> destination, CancellationToken token)
        {
            Volatile.Write(ref LastAccessTick, Environment.TickCount64);
            while (true)
            {
                TaskCompletionSource<bool>? waiter = null;
                lock (_sync)
                {
                    var available = _written - offsetInBlock;
                    if (available > 0)
                    {
                        var count = Math.Min(available, destination.Length);
                        Buffer.AsMemory(offsetInBlock, count).CopyTo(destination);
                        return count;
                    }
                    if (_completed)
                    {
                        if (_error is not null) throw _error;
                        return 0;
                    }
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(waiter);
                }

                await waiter.Task.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _providerGate;
    private readonly Func<CancellationToken, Task<CloudPlayResult>>? _refresh;
    private readonly Action<Uri, IReadOnlyDictionary<string, string>, CloudPlayResult?>? _onUpstreamUpdated;
    private readonly CancellationTokenSource _lifetime;
    private readonly ConcurrentDictionary<long, ProgressiveBlock> _blocks = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _adaptSync = new();
    private readonly object _prefetchSync = new();

    private CancellationTokenSource _prefetchCts;
    private long _lastDemandBlock = -1;
    private int _prefetchCancellations;

    private Uri _originUri;
    private Uri _resolvedUri;
    private IReadOnlyDictionary<string, string> _headers;
    private EntityTagHeaderValue? _entityTag;
    private long _totalLength;
    private string _contentType;
    private readonly int _blockSize;
    private readonly int _maxBlocks;
    private readonly int _maxThreads;
    private int _activeThreads;
    private int _fastBlocks;
    private int _disposed;

    public CloudBlockCache(
        HttpClient http,
        Uri originUri,
        Uri resolvedUri,
        IReadOnlyDictionary<string, string> headers,
        long totalLength,
        string contentType,
        EntityTagHeaderValue? entityTag,
        int blockSize,
        int maxThreads,
        SemaphoreSlim providerGate,
        CancellationToken parentToken,
        Func<CancellationToken, Task<CloudPlayResult>>? refresh,
        Action<Uri, IReadOnlyDictionary<string, string>, CloudPlayResult?>? onUpstreamUpdated)
    {
        _http = http;
        _originUri = originUri;
        _resolvedUri = resolvedUri;
        _headers = headers;
        _totalLength = totalLength;
        _contentType = contentType;
        _entityTag = entityTag;
        _blockSize = Math.Clamp(blockSize, 512 * 1024, 4 * 1024 * 1024);
        _maxBlocks = Math.Clamp(DefaultMaxCacheBytes / _blockSize, 8, 48);
        _maxThreads = Math.Clamp(maxThreads, 1, 6);
        _activeThreads = _maxThreads;
        _providerGate = providerGate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refresh = refresh;
        _onUpstreamUpdated = onUpstreamUpdated;
    }

    public long TotalLength => _totalLength;
    public string ContentType => _contentType;
    public EntityTagHeaderValue? EntityTag => _entityTag;
    public int BlockSize => _blockSize;
    internal int PrefetchCancellations => Volatile.Read(ref _prefetchCancellations);

    public void SeedFromStream(long blockIndex, Stream initialStream, int expectedLength, IDisposable responseOwner)
    {
        if (Volatile.Read(ref _disposed) != 0 || expectedLength <= 0)
        {
            responseOwner.Dispose();
            return;
        }
        var block = new ProgressiveBlock(blockIndex, expectedLength, isPrefetch: false);
        if (!_blocks.TryAdd(blockIndex, block))
        {
            responseOwner.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            using (responseOwner)
            await using (initialStream)
            {
                try
                {
                    await PumpStreamIntoBlockAsync(initialStream, block, _lifetime.Token).ConfigureAwait(false);
                    block.Complete();
                }
                catch (Exception ex)
                {
                    block.Fail(ex);
                    _blocks.TryRemove(new KeyValuePair<long, ProgressiveBlock>(blockIndex, block));
                }
            }
        });
    }

    public Stream OpenRangeStream(long start, long endInclusive) =>
        new RangeReaderStream(this, start, endInclusive);

    internal void NotifyDemandBlock(long blockIndex)
    {
        var previous = Interlocked.Exchange(ref _lastDemandBlock, blockIndex);
        if (previous >= 0 && Math.Abs(blockIndex - previous) > 3)
            CancelStalePrefetches();
    }

    private void CancelStalePrefetches()
    {
        CancellationTokenSource oldCts;
        lock (_prefetchSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            oldCts = _prefetchCts;
            _prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        Interlocked.Increment(ref _prefetchCancellations);
        try { oldCts.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { oldCts.Dispose(); }
    }

    private CancellationToken CurrentPrefetchToken()
    {
        lock (_prefetchSync)
            return _prefetchCts.Token;
    }

    private ProgressiveBlock GetOrStartBlock(long blockIndex, bool isDemand)
    {
        if (isDemand) NotifyDemandBlock(blockIndex);

        if (_blocks.TryGetValue(blockIndex, out var existing))
        {
            if (existing.Error is null)
            {
                Volatile.Write(ref existing.LastAccessTick, Environment.TickCount64);
                return existing;
            }
            _blocks.TryRemove(new KeyValuePair<long, ProgressiveBlock>(blockIndex, existing));
        }

        var blockStart = blockIndex * _blockSize;
        var expected = (int)Math.Min(_blockSize, _totalLength - blockStart);
        var created = new ProgressiveBlock(blockIndex, expected, isPrefetch: !isDemand);
        var actual = _blocks.GetOrAdd(blockIndex, created);
        if (ReferenceEquals(actual, created))
        {
            EvictIfNeeded(blockIndex);
            var fetchToken = isDemand ? _lifetime.Token : CurrentPrefetchToken();
            _ = FetchBlockAsync(blockIndex, created, isDemand, fetchToken);
        }
        return actual;
    }

    private void ScheduleLookahead(long currentBlockIndex, long maxBlockIndex)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var lookahead = Math.Clamp(_activeThreads + 1, 2, 6);
        for (var i = 1; i <= lookahead; i++)
        {
            var target = currentBlockIndex + i;
            if (target > maxBlockIndex) break;
            if (_blocks.ContainsKey(target)) continue;
            // Reserve at least 1 slot in _providerGate for instant interactive seek demands
            if (_providerGate.CurrentCount <= 1 && i > 1) break;
            _ = GetOrStartBlock(target, isDemand: false);
        }
    }

    private void EvictIfNeeded(long protectedBlockIndex)
    {
        if (_blocks.Count <= _maxBlocks) return;
        var lastBlockIndex = (_totalLength - 1) / _blockSize;
        var candidates = _blocks.Values
            .Where(b => b.IsCompleted
                && b.BlockIndex != protectedBlockIndex
                && b.BlockIndex != 0
                && b.BlockIndex != lastBlockIndex
                && (b.BlockIndex < protectedBlockIndex || b.BlockIndex > protectedBlockIndex + 8))
            .OrderBy(b => b.LastAccessTick)
            .Take(_blocks.Count - _maxBlocks + 2)
            .ToArray();

        foreach (var candidate in candidates)
            _blocks.TryRemove(new KeyValuePair<long, ProgressiveBlock>(candidate.BlockIndex, candidate));
    }

    private async Task FetchBlockAsync(long blockIndex, ProgressiveBlock block, bool isDemand, CancellationToken token)
    {
        var blockStart = blockIndex * _blockSize;
        var blockEnd = blockStart + block.ExpectedLength - 1;

        var acquired = false;
        try
        {
            await _providerGate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            var maxAttempts = isDemand ? 4 : 2;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var sw = Stopwatch.StartNew();
                    var currentUri = _resolvedUri;
                    var currentHeaders = _headers;
                    var offset = block.Written;
                    if (offset >= block.ExpectedLength)
                    {
                        block.Complete();
                        return;
                    }

                    using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    headerDeadline.CancelAfter(HeaderTimeout);

                    using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    foreach (var (name, value) in currentHeaders)
                        if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !name.Equals("Range", StringComparison.OrdinalIgnoreCase))
                            request.Headers.TryAddWithoutValidation(name, value);
                    request.Headers.Range = new RangeHeaderValue(blockStart + offset, blockEnd);
                    if (_entityTag is { IsWeak: false })
                        request.Headers.IfRange = new RangeConditionHeaderValue(_entityTag);

                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerDeadline.Token).ConfigureAwait(false);

                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                    {
                        if (Uri.TryCreate(currentUri, location, out var nextUri) && nextUri.Scheme is "http" or "https" && nextUri.UserInfo.Length == 0)
                        {
                            var scoped = MediaRequestHeaders.ForTarget(currentHeaders, _originUri, nextUri);
                            _resolvedUri = nextUri;
                            _headers = scoped;
                            _onUpstreamUpdated?.Invoke(nextUri, scoped, null);
                            continue;
                        }
                    }

                    if (_refresh is not null && (int)response.StatusCode is 401 or 403 or 412)
                    {
                        await RefreshUpstreamAsync(currentUri, token).ConfigureAwait(false);
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    {
                        ThrottleDown();
                        var retryAfter = response.Headers.RetryAfter?.Delta;
                        var delay = retryAfter is { } v
                            ? TimeSpan.FromMilliseconds(Math.Clamp(v.TotalMilliseconds, 300, 4000))
                            : TimeSpan.FromMilliseconds(350 * (1 << attempt));
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    var cr = response.Content.Headers.ContentRange;
                    var expectedSlice = block.ExpectedLength - offset;
                    if (response.StatusCode != HttpStatusCode.PartialContent || cr is not { Unit: "bytes" } ||
                        cr.From != blockStart + offset || cr.To != blockEnd || cr.Length != _totalLength ||
                        response.Content.Headers.ContentLength is { } cl && cl != expectedSlice)
                        throw new InvalidDataException("网盘服务器返回了不一致的分块范围。");

                    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await PumpStreamIntoBlockAsync(stream, block, token).ConfigureAwait(false);
                    AdjustSpeed(sw.Elapsed);
                    block.Complete();
                    return;
                }
                catch (Exception ex) when (attempt + 1 < maxAttempts && !token.IsCancellationRequested &&
                    ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    await Task.Delay(350 * (1 << attempt), token).ConfigureAwait(false);
                }
            }
            throw new IOException("网盘媒体分块读取重试耗尽。");
        }
        catch (Exception ex)
        {
            block.Fail(ex);
            _blocks.TryRemove(new KeyValuePair<long, ProgressiveBlock>(blockIndex, block));
        }
        finally
        {
            if (acquired) _providerGate.Release();
        }
    }

    private static async Task PumpStreamIntoBlockAsync(Stream source, ProgressiveBlock block, CancellationToken token)
    {
        var temp = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            while (block.Written < block.ExpectedLength)
            {
                idle.CancelAfter(ReadIdleTimeout);
                var toRead = Math.Min(temp.Length, block.ExpectedLength - block.Written);
                var read = await source.ReadAsync(temp.AsMemory(0, toRead), idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (block.Written < block.ExpectedLength)
                        throw new IOException("网盘分块连接提前中断。");
                    break;
                }
                block.Append(temp.AsSpan(0, read));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private async Task RefreshUpstreamAsync(Uri failedUri, CancellationToken token)
    {
        await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_resolvedUri != failedUri) return;
            var refreshed = await _refresh!(token).ConfigureAwait(false);
            var nextUri = new Uri(refreshed.Url);
            _originUri = nextUri;
            _resolvedUri = nextUri;
            _headers = refreshed.Headers;
            _entityTag = null;
            _onUpstreamUpdated?.Invoke(nextUri, refreshed.Headers, refreshed);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ThrottleDown()
    {
        lock (_adaptSync)
        {
            _fastBlocks = 0;
            if (_activeThreads > 1) _activeThreads--;
        }
    }

    private void AdjustSpeed(TimeSpan elapsed)
    {
        lock (_adaptSync)
        {
            if (elapsed > TimeSpan.FromSeconds(6))
            {
                _fastBlocks = 0;
                if (_activeThreads > 1) _activeThreads--;
            }
            else if (elapsed < TimeSpan.FromMilliseconds(1500))
            {
                if (++_fastBlocks >= 3 && _activeThreads < _maxThreads)
                {
                    _activeThreads++;
                    _fastBlocks = 0;
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_prefetchSync)
        {
            try { _prefetchCts.Cancel(); } catch { }
            _prefetchCts.Dispose();
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _blocks.Clear();
        _refreshLock.Dispose();
    }

    private sealed class RangeReaderStream(CloudBlockCache cache, long start, long endInclusive) : Stream
    {
        private readonly long _start = start;
        private long _position = start;
        private readonly long _endExclusive = endInclusive + 1;
        private readonly long _maxBlockIndex = endInclusive / cache._blockSize;
        private long _lastLookaheadBlock = -1;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || _position >= _endExclusive) return 0;

            var blockIndex = _position / cache._blockSize;
            var offsetInBlock = (int)(_position - blockIndex * cache._blockSize);
            var remainingInRange = _endExclusive - _position;
            var slice = buffer.Length > remainingInRange ? buffer[..(int)remainingInRange] : buffer;

            // Start/verify demand block FIRST so any stale prefetches are cancelled before scheduling new lookahead
            var block = cache.GetOrStartBlock(blockIndex, isDemand: true);

            if (blockIndex != _lastLookaheadBlock)
            {
                _lastLookaheadBlock = blockIndex;
                cache.ScheduleLookahead(blockIndex, _maxBlockIndex);
            }

            try
            {
                var read = await block.ReadAsync(offsetInBlock, slice, cancellationToken).ConfigureAwait(false);
                _position += read;
                return read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && block.IsPrefetch)
            {
                // The block was a speculative prefetch cancelled by a concurrent seek; retry cleanly as a demand block
                block = cache.GetOrStartBlock(blockIndex, isDemand: true);
                var read = await block.ReadAsync(offsetInBlock, slice, cancellationToken).ConfigureAwait(false);
                _position += read;
                return read;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _endExclusive - _start;
        public override long Position { get => _position - _start; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
