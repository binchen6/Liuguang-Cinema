using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Imaging;

/// <summary>
/// High-throughput, non-blocking poster and cover image cache.
/// Uses a dedicated HTTP/2 connection pool, negative caching for dead URLs,
/// bounded background normalization and UI-affine WIC decoding.
/// </summary>
public sealed partial class ImageCacheService : IDisposable
{
    private sealed class Entry(BitmapImage image, LinkedListNode<string> node, long pixels)
    {
        public BitmapImage Image { get; } = image;
        public LinkedListNode<string> Node { get; } = node;
        public long Pixels { get; } = pixels;
    }

    private sealed record DecodedImage(BitmapImage Image, long Pixels);

    private const int MaxMemoryItems = 240;
    private const long MaxMemoryPixels = 54_000_000;
    private const int MaxBytesPerImage = 8 * 1024 * 1024;
    private const int DefaultCardDecodeWidth = 340;
    private const int HighResDecodeWidth = 520;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(2);
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36";

    private readonly HttpClient _client;
    private readonly DispatcherQueue? _dispatcher;
    private readonly SemaphoreSlim _networkGate = new(16, 16);
    private readonly SemaphoreSlim _decodeGate = new(4, 4);
    private readonly SemaphoreSlim _diskGate = new(2, 2);
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _pendingBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Lock _memoryLock = new();
    private long _memoryPixels;
    private int _downloads;
    private volatile bool _dirEnsured;
    private bool _disposed;

    public ImageCacheService(DispatcherQueue? dispatcher = null, DnsResolverService? dns = null)
    {
        _dispatcher = dispatcher;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 24,
            EnableMultipleHttp2Connections = true,
            UseCookies = false
        };
        if (dns is not null) handler.ConnectCallback = dns.ConnectAsync;
        _client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<BitmapImage?> GetImageAsync(string? url, CancellationToken cancellationToken = default) =>
        GetImageAsync(url, highRes: false, cancellationToken);

    public Task<BitmapImage?> GetImageAsync(string? url, bool highRes, CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.FromResult<BitmapImage?>(null);
        url = Unwrap(url);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme is not ("http" or "https") && LocalArtworkPath(uri) is null))
            return Task.FromResult<BitmapImage?>(null);

        var cacheKey = highRes ? "H|" + url : url;
        lock (_memoryLock)
        {
            // A cached high-res bitmap also satisfies card-resolution requests immediately.
            if (_memory.TryGetValue(cacheKey, out var entry) || (!highRes && _memory.TryGetValue("H|" + url, out entry)))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                return Task.FromResult<BitmapImage?>(entry.Image);
            }
        }

        if (_failed.TryGetValue(url, out var failedUntil))
        {
            if (failedUntil > DateTimeOffset.UtcNow) return Task.FromResult<BitmapImage?>(null);
            _failed.TryRemove(url, out _);
        }

        var dispatcher = _dispatcher ?? TryGetCurrentDispatcher();
        var decodeWidth = highRes ? HighResDecodeWidth : DefaultCardDecodeWidth;
        var task = _pending.GetOrAdd(cacheKey, _ => GetCoreAsync(cacheKey, url, decodeWidth, dispatcher));
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>
    /// Pre-warms disk/network bytes for upcoming poster URLs without touching the UI thread.
    /// </summary>
    public void Prefetch(IEnumerable<string?> urls, int maxCount = 16)
    {
        if (_disposed) return;
        var started = 0;
        foreach (var raw in urls)
        {
            if (started >= maxCount) break;
            var url = Unwrap(raw);
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            lock (_memoryLock)
            {
                if (_memory.ContainsKey(url) || _memory.ContainsKey("H|" + url)) continue;
            }
            if (_failed.TryGetValue(url, out var failedUntil) && failedUntil > DateTimeOffset.UtcNow) continue;
            if (_pending.ContainsKey(url) || _pendingBytes.ContainsKey(url)) continue;

            started++;
            var captured = url;
            _ = Task.Run(async () =>
            {
                try { await GetBytesCachedAsync(captured).ConfigureAwait(false); }
                catch { }
            });
        }
    }

    private async Task<BitmapImage?> GetCoreAsync(string cacheKey, string url, int decodeWidth, DispatcherQueue? dispatcher)
    {
        try
        {
            var bytes = await GetBytesCachedAsync(url).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                RecordFailure(url);
                return null;
            }

            await _decodeGate.WaitAsync().ConfigureAwait(false);
            DecodedImage? decoded;
            try
            {
                decoded = await DecodeBitmapAsync(bytes, decodeWidth, dispatcher).ConfigureAwait(false);
                if (decoded is null)
                {
                    // Fallback: transcode exotic formats (e.g. AVIF / unsupported WebP / CMYK JPEG) via SkiaSharp off-UI.
                    var transcoded = await Task.Run(() => ForceTranscodeJpeg(bytes, decodeWidth)).ConfigureAwait(false);
                    if (transcoded is not null)
                        decoded = await DecodeBitmapAsync(transcoded, decodeWidth, dispatcher).ConfigureAwait(false);
                }
            }
            finally { _decodeGate.Release(); }

            if (decoded is null)
            {
                RecordFailure(url);
                return null;
            }

            var image = decoded.Image;
            lock (_memoryLock)
            {
                if (_memory.TryGetValue(cacheKey, out var existing)) return existing.Image;
                var node = _lru.AddFirst(cacheKey);
                var pixels = decoded.Pixels;
                _memory[cacheKey] = new Entry(image, node, pixels);
                _memoryPixels += pixels;
                while (_memory.Count > MaxMemoryItems || _memoryPixels > MaxMemoryPixels)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    if (_memory.Remove(last.Value, out var evicted))
                        _memoryPixels -= evicted.Pixels;
                }
            }
            return image;
        }
        catch (OperationCanceledException)
        {
            RecordFailure(url);
            return null;
        }
        catch (Exception ex)
        {
            RecordFailure(url);
            Diagnostics.Log($"图片加载失败 {url}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            _pending.TryRemove(cacheKey, out _);
        }
    }

    private Task<byte[]?> GetBytesCachedAsync(string url) =>
        _pendingBytes.GetOrAdd(url, LoadOrFetchBytesCoreAsync);

    private async Task<byte[]?> LoadOrFetchBytesCoreAsync(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && LocalArtworkPath(uri) is { } artwork)
            {
                var artworkFile = new FileInfo(artwork);
                if (!artworkFile.Exists || artworkFile.Length is <= 12 or > Library.LocalMediaArtwork.MaxPictureBytes) return null;
                return await File.ReadAllBytesAsync(artwork).ConfigureAwait(false);
            }
            if (!_dirEnsured)
            {
                AppPaths.EnsureCreated();
                _dirEnsured = true;
            }

            var path = DiskCachePath(url);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 12 && info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-14))
            {
                try
                {
                    return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                }
                catch (IOException) { }
            }

            await _networkGate.WaitAsync().ConfigureAwait(false);
            byte[] raw;
            try
            {
                raw = await FetchImageBytesAsync(url).ConfigureAwait(false);
            }
            finally
            {
                _networkGate.Release();
            }

            if (!LooksLikeImage(raw))
                throw new InvalidDataException("图片地址返回了非图片内容。");

            // Paint first, normalize + persist to disk asynchronously so UI doesn't wait for Skia encode or file I/O.
            _ = Task.Run(() => PersistNormalizedToDiskAsync(path, raw));
            return raw;
        }
        catch (OperationCanceledException)
        {
            RecordFailure(url);
            return null;
        }
        catch (Exception ex)
        {
            RecordFailure(url);
            Diagnostics.Log($"图片下载失败 {url}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            _pendingBytes.TryRemove(url, out _);
        }
    }

    private async Task<byte[]> FetchImageBytesAsync(string url)
    {
        var uri = new Uri(url);
        using var cts = new CancellationTokenSource(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        var referer = ResolveReferer(uri);
        if (referer is not null) request.Headers.TryAddWithoutValidation("Referer", referer);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytesPerImage)
            throw new InvalidDataException("图片体积超过允许大小。");

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var capacity = response.Content.Headers.ContentLength is > 0 and <= MaxBytesPerImage
            ? (int)response.Content.Headers.ContentLength.Value
            : 48 * 1024;
        using var memory = new MemoryStream(capacity);
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxBytesPerImage)
                throw new InvalidDataException("图片体积超过允许大小。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private async Task PersistNormalizedToDiskAsync(string path, byte[] raw)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await _diskGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var normalized = NormalizeForDisk(raw);
            await File.WriteAllBytesAsync(temp, normalized).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            if (Interlocked.Increment(ref _downloads) % 40 == 0) CleanupDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        finally
        {
            _diskGate.Release();
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static async Task<DecodedImage?> DecodeBitmapAsync(byte[] bytes, int decodeWidth, DispatcherQueue? dispatcher)
    {
        try
        {
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource<DecodedImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dispatcher.TryEnqueue(async () =>
                {
                    try { tcs.TrySetResult(await SetSourceOnUiAsync(bytes, decodeWidth)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }))
                {
                    return null;
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            return await SetSourceOnUiAsync(bytes, decodeWidth).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"图片解码失败: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
    }

    private static async Task<DecodedImage> SetSourceOnUiAsync(byte[] bytes, int decodeWidth)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage
        {
            DecodePixelWidth = decodeWidth,
            DecodePixelType = DecodePixelType.Physical
        };
        await image.SetSourceAsync(stream);
        var pixels = Math.Max(1L, (long)image.PixelWidth * image.PixelHeight);
        return new DecodedImage(image, pixels);
    }

    private void RecordFailure(string url)
    {
        if (_failed.Count >= 512)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _failed)
                if (pair.Value <= now) _failed.TryRemove(pair.Key, out _);
            if (_failed.Count >= 512) _failed.Clear();
        }
        _failed[url] = DateTimeOffset.UtcNow.Add(NegativeCacheTtl);
    }

    public async Task ClearAsync()
    {
        lock (_memoryLock) { _memory.Clear(); _lru.Clear(); _memoryPixels = 0; }
        _failed.Clear();
        if (!Directory.Exists(AppPaths.Images)) return;
        await Task.Run(() =>
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.Images, "*.img"))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    private static string DiskCachePath(string url) =>
        Path.Combine(AppPaths.Images, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

    private static string? ResolveReferer(Uri uri)
    {
        var host = uri.Host;
        if (host.Contains("douban", StringComparison.OrdinalIgnoreCase)) return "https://movie.douban.com/";
        if (host.Contains("baidu.com", StringComparison.OrdinalIgnoreCase) || host.Contains("bdstatic.com", StringComparison.OrdinalIgnoreCase)) return "https://www.baidu.com/";
        if (host.Contains("qpic.cn", StringComparison.OrdinalIgnoreCase) || host.Contains("gtimg.com", StringComparison.OrdinalIgnoreCase)) return "https://v.qq.com/";
        if (host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase) || host.Contains("biliimg.com", StringComparison.OrdinalIgnoreCase)) return "https://www.bilibili.com/";
        if (host.Contains("sinaimg.cn", StringComparison.OrdinalIgnoreCase)) return "https://weibo.com/";
        if (host.Contains("iqiyipic.com", StringComparison.OrdinalIgnoreCase)) return "https://www.iqiyi.com/";
        if (host.Contains("ykimg.com", StringComparison.OrdinalIgnoreCase)) return "https://www.youku.com/";
        if (host.Contains("mgtv.com", StringComparison.OrdinalIgnoreCase)) return "https://www.mgtv.com/";
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static byte[] NormalizeForDisk(byte[] bytes)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null) return bytes;
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
            throw new InvalidDataException("图片像素尺寸无效或过大。");
        var scale = Math.Min(1, Math.Min(520d / width, 1440d / height));
        if (scale >= 0.99 && bytes.Length <= 180 * 1024) return bytes;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)),
            SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var scaled = SKBitmap.Decode(codec, info);
        if (scaled is null) return bytes;
        using var image = SKImage.FromBitmap(scaled);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 84);
        return encoded is not null && encoded.Size < bytes.LongLength ? encoded.ToArray() : bytes;
    }

    private static byte[]? ForceTranscodeJpeg(byte[] bytes, int maxWidth)
    {
        try
        {
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) return null;
            var scale = Math.Min(1, Math.Min((double)maxWidth / width, 1440d / height));
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)),
                SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var scaled = SKBitmap.Decode(codec, info);
            if (scaled is null) return null;
            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 86);
            return encoded?.ToArray();
        }
        catch { return null; }
    }

    private static void CleanupDisk()
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.Images).EnumerateFiles("*.img").OrderByDescending(file => file.LastAccessTimeUtc).ToList();
            long size = 0;
            foreach (var file in files)
            {
                size += file.Length;
                if (size <= 256 * 1024 * 1024 && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-30)) continue;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string? Unwrap(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        string? nested = null;
        if (uri.Host.Equals("www.wogg.net", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Equals("/db.php", StringComparison.OrdinalIgnoreCase))
            nested = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"];
        else if (uri.Host.Contains("baidu.com", StringComparison.OrdinalIgnoreCase))
            nested = BaiduSrcPattern().Match(url).Groups[1].Value;
        if (!Uri.TryCreate(nested, UriKind.Absolute, out _)) return url;
        return nested;
    }

    private static string? LocalArtworkPath(Uri uri)
    {
        if (uri.Scheme != "tv-artwork" || uri.IdnHost != "local" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath.Length != 65) return null;
        var key = uri.AbsolutePath[1..];
        return key.All(char.IsAsciiHexDigit) ? Library.LocalMediaArtwork.ImagePath(key) : null;
    }

    internal static bool LooksLikeImage(byte[] bytes) => bytes.Length > 12 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8) || (bytes[0] == 0x89 && bytes[1] == 0x50)
        || (bytes[0] == 0x47 && bytes[1] == 0x49) || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57)
        || (bytes[0] == 0x42 && bytes[1] == 0x4D)
        || (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70));

    private static DispatcherQueue? TryGetCurrentDispatcher()
    {
        try { return DispatcherQueue.GetForCurrentThread(); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _networkGate.Dispose();
    }

    [GeneratedRegex(@"[?&]src=([^&]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BaiduSrcPattern();
}
