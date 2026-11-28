using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Services.Persistence;

namespace TV.Services.Playback;

internal static class DanmakuCache
{
    private const int MaxEntries = 160;
    private const long MaxTotalBytes = 96 * 1024 * 1024;
    private const int MaxEntryBytes = 16 * 1024 * 1024;
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    internal static string DirectoryPath => Path.Combine(AppPaths.Root, "cache", "danmaku");

    internal sealed record Entry(DanmakuEntry[]? Comments, string? NotMatched);

    internal static async Task<Entry?> GetOrLoadAsync(string? key, bool refresh,
        Func<Task<IReadOnlyList<DanmakuEntry>?>> load, CancellationToken token)
    {
        if (!refresh && key is not null && await ReadAsync(key, token).ConfigureAwait(false) is { } cached)
            return cached;
        Entry entry;
        try
        {
            var comments = await load().ConfigureAwait(false);
            if (comments is null) return null;
            entry = new Entry(comments as DanmakuEntry[] ?? comments.ToArray(), null);
        }
        catch (DanmakuNotMatchedException ex) { entry = new Entry(null, ex.Message); }
        if (key is not null) await WriteAsync(key, entry, token).ConfigureAwait(false);
        return entry;
    }

    internal static async Task<Entry?> ReadAsync(string key, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(CachePath(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is 0 or > MaxEntryBytes) return null;
            var entry = await JsonSerializer.DeserializeAsync<Entry>(file, cancellationToken: token).ConfigureAwait(false);
            return entry is not null && (entry.Comments is { Length: <= 30000 } || !string.IsNullOrEmpty(entry.NotMatched)) ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static async Task WriteAsync(string key, Entry entry, CancellationToken token)
    {
        var destination = CachePath(key);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await WriteGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(file, entry, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (new FileInfo(temp).Length > MaxEntryBytes) return;
            File.Move(temp, destination, overwrite: true);
            var remaining = MaxTotalBytes;
            var count = 0;
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json").OrderByDescending(item => item.LastWriteTimeUtc))
            {
                remaining -= file.Length;
                if (++count > MaxEntries || remaining < 0) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("弹幕缓存写入: " + ex.GetType().Name);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            WriteGate.Release();
        }
    }

    private static string CachePath(string key) =>
        Path.Combine(DirectoryPath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
}
