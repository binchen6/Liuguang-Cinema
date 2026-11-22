using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Imaging;

public sealed class ImageCacheService
{
    private sealed class Entry(BitmapImage image, LinkedListNode<string> node, long pixels)
    {
        public BitmapImage Image { get; } = image;
        public LinkedListNode<string> Node { get; } = node;
        public long Pixels { get; } = pixels;
    }

    private readonly SourceHttpClient _http;
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _pending = new();
    private readonly Dictionary<string, Entry> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Lock _memoryLock = new();
    private long _memoryPixels;
    private int _downloads;

    public ImageCacheService(SourceHttpClient http) => _http = http;

    public Task<BitmapImage?> GetImageAsync(string? url, CancellationToken cancellationToken = default)
    {
        url = Unwrap(url);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return Task.FromResult<BitmapImage?>(null);
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(url, out var entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                return Task.FromResult<BitmapImage?>(entry.Image);
            }
        }
        return _pending.GetOrAdd(url, key => GetCoreAsync(key, CancellationToken.None)).WaitAsync(cancellationToken);
    }

    private async Task<BitmapImage?> GetCoreAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            AppPaths.EnsureCreated();
            var path = Path.Combine(AppPaths.Images, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".img");
            byte[] bytes;
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-14))
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36"
                };
                if (url.Contains("douban", StringComparison.OrdinalIgnoreCase)) headers["Referer"] = "https://movie.douban.com/";
                else if (url.Contains("baidu.com", StringComparison.OrdinalIgnoreCase) || url.Contains("bdstatic.com", StringComparison.OrdinalIgnoreCase)) headers["Referer"] = "https://www.baidu.com/";
                else if (url.Contains("qpic.cn", StringComparison.OrdinalIgnoreCase)) headers["Referer"] = "https://v.qq.com/";
                var payload = await _http.GetBytesAsync(url, headers, 10 * 1024 * 1024, cancellationToken).ConfigureAwait(true);
                if (!LooksLikeImage(payload.Content)) throw new InvalidDataException("图片地址返回了非图片内容。");
                bytes = await Task.Run(() => NormalizeImage(payload.Content), cancellationToken).ConfigureAwait(true);
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(true);
                File.Move(temp, path, true);
                if (++_downloads % 40 == 0) _ = Task.Run(CleanupDisk);
            }
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 480, DecodePixelType = DecodePixelType.Physical };
            await image.SetSourceAsync(stream);
            lock (_memoryLock)
            {
                if (_memory.TryGetValue(url, out var existing)) return existing.Image;
                var node = _lru.AddFirst(url);
                var pixels = Math.Max(1L, (long)image.PixelWidth * image.PixelHeight);
                _memory[url] = new Entry(image, node, pixels);
                _memoryPixels += pixels;
                while (_memory.Count > 64 || _memoryPixels > 24_000_000)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _memoryPixels -= _memory[last.Value].Pixels;
                    _memory.Remove(last.Value);
                }
            }
            return image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"图片加载失败 {url}: {ex}");
            return null;
        }
        finally { _pending.TryRemove(url, out _); }
    }

    public async Task ClearAsync()
    {
        lock (_memoryLock) { _memory.Clear(); _lru.Clear(); _memoryPixels = 0; }
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

    private static byte[] NormalizeImage(byte[] bytes)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null) return bytes;
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
            throw new InvalidDataException("图片像素尺寸无效或过大。");
        var scale = Math.Min(1, Math.Min(480d / width, 1440d / height));
        if (scale == 1) return bytes;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)),
            SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var scaled = SKBitmap.Decode(codec, info);
        if (scaled is null) return bytes;
        using var image = SKImage.FromBitmap(scaled);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 84);
        return encoded is not null && encoded.Size < bytes.LongLength ? encoded.ToArray() : bytes;
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

    private static string? Unwrap(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        string? nested = null;
        if (uri.Host.Equals("www.wogg.net", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Equals("/db.php", StringComparison.OrdinalIgnoreCase))
            nested = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"];
        else if (uri.Host.Contains("baidu.com", StringComparison.OrdinalIgnoreCase))
            nested = System.Text.RegularExpressions.Regex.Match(url, @"[?&]src=([^&]+)").Groups[1].Value;
        if (!Uri.TryCreate(nested, UriKind.Absolute, out _)) return url;
        return nested;
    }

    private static bool LooksLikeImage(byte[] bytes) => bytes.Length > 12 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8) || (bytes[0] == 0x89 && bytes[1] == 0x50)
        || (bytes[0] == 0x47 && bytes[1] == 0x49) || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57)
        || (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70));
}
