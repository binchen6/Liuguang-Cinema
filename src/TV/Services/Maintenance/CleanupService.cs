using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TV.Models;
using TV.Services.Persistence;

namespace TV.Services.Maintenance;

public sealed partial class CleanupCategory(string id, string name, string description, long bytes, int files, bool selected = false) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public long Bytes { get; } = bytes;
    public int Files { get; } = files;
    [ObservableProperty] private bool _selected = selected;
    public string Size => Bytes >= 1024 * 1024 * 1024 ? $"{Bytes / 1024d / 1024 / 1024:0.##} GB" : $"{Bytes / 1024d / 1024:0.##} MB";
}

public sealed record CleanupResult(long FreedBytes, int DeletedFiles, int FailedFiles)
{
    public string Summary => $"已释放 {FreedBytes / 1024d / 1024:0.##} MB，删除 {DeletedFiles} 项" +
        (FailedFiles > 0 ? $"；{FailedFiles} 项被占用或不可访问，已跳过" : string.Empty);
}

public sealed class CleanupService(DatabaseService database)
{
    private const string Images = "images";
    private const string Subtitles = "subtitles";
    private const string AppTemporary = "app-temp";
    private const string PartialDownloads = "partial-downloads";
    private const string Downloads = "downloads";
    private const string UserTemporary = "user-temp";
    private const string RecycleBin = "recycle";

    public async Task<IReadOnlyList<CleanupCategory>> ScanAsync(CancellationToken token)
    {
        var categories = new List<CleanupCategory>();
        var image = await Task.Run(() => Measure(AppPaths.Images, path => Path.GetExtension(path) == ".img"), token);
        categories.Add(new CleanupCategory(Images, "海报与图片缓存", "可安全重建，不影响收藏与历史", image.Bytes, image.Files, true));
        var subtitles = Path.Combine(AppPaths.Root, "cache", "subtitles");
        var subtitle = await Task.Run(() => Measure(subtitles, path => Path.GetExtension(path) is ".vtt" or ".tmp"), token);
        categories.Add(new CleanupCategory(Subtitles, "在线字幕缓存", "删除后再次使用时重新下载转换", subtitle.Bytes, subtitle.Files, true));
        var temporary = await Task.Run(() => Measure(Path.Combine(AppPaths.Root, "cache"), path =>
            !IsUnder(path, AppPaths.Images) && !IsUnder(path, subtitles) && (Path.GetFileName(path).StartsWith("cast-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path) is ".tmp" or ".partial")), token);
        categories.Add(new CleanupCategory(AppTemporary, "应用临时文件", "投屏接收残留和未完成的应用缓存", temporary.Bytes, temporary.Files, true));
        var directory = await DownloadDirectoryAsync(token);
        var partial = await Task.Run(() => Measure(directory, path => path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase), recursive: false), token);
        categories.Add(new CleanupCategory(PartialDownloads, "未完成下载", "仅删除下载目录中的 .partial 文件", partial.Bytes, partial.Files));
        var entries = await database.GetDownloadsAsync(token);
        long bytes = 0;
        var count = 0;
        foreach (var entry in entries)
        {
            try
            {
                if (File.Exists(entry.FilePath)) { bytes += new FileInfo(entry.FilePath).Length; count++; }
            }
            catch (IOException) { }
        }
        categories.Add(new CleanupCategory(Downloads, "已下载媒体", "会删除媒体库记录对应的本地文件，请先确认", bytes, count));
        var temp = await Task.Run(() => Measure(Path.GetTempPath(), path =>
            !IsUnder(path, AppPaths.Root) && IsExpired(path), limit: 5000), token);
        categories.Add(new CleanupCategory(UserTemporary, "用户临时目录中的过期文件", "仅七天前的文件；被占用或无权限的文件自动跳过", temp.Bytes, temp.Files));
        if (QueryRecycleBin(out var recycleBytes, out var recycleFiles))
            categories.Add(new CleanupCategory(RecycleBin, "回收站", "清空后无法从回收站恢复，必须单独勾选", recycleBytes, (int)Math.Min(int.MaxValue, recycleFiles)));
        return categories;
    }

    public async Task<CleanupResult> CleanAsync(IEnumerable<string> selected, IProgress<string>? progress, CancellationToken token)
    {
        var ids = selected.ToHashSet(StringComparer.Ordinal);
        long freed = 0;
        var deleted = 0;
        var failed = 0;
        async Task DeleteFilesAsync(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var length = new FileInfo(path).Length;
                    await Task.Run(() => File.Delete(path), token);
                    freed += length;
                    deleted++;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
        }
        if (ids.Contains(Images))
        {
            progress?.Report("正在清理图片缓存…");
            await DeleteFilesAsync(Files(AppPaths.Images, path => Path.GetExtension(path) == ".img"));
        }
        if (ids.Contains(Subtitles))
        {
            progress?.Report("正在清理在线字幕缓存…");
            var folder = Path.Combine(AppPaths.Root, "cache", "subtitles");
            await DeleteFilesAsync(Files(folder, path => Path.GetExtension(path) is ".vtt" or ".tmp"));
        }
        if (ids.Contains(AppTemporary))
        {
            progress?.Report("正在清理应用临时文件…");
            var images = AppPaths.Images;
            var subtitles = Path.Combine(AppPaths.Root, "cache", "subtitles");
            await DeleteFilesAsync(Files(Path.Combine(AppPaths.Root, "cache"), path => !IsUnder(path, images) && !IsUnder(path, subtitles) &&
                (Path.GetFileName(path).StartsWith("cast-", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path) is ".tmp" or ".partial")));
        }
        var directory = await DownloadDirectoryAsync(token);
        if (ids.Contains(PartialDownloads))
        {
            progress?.Report("正在清理未完成下载…");
            await DeleteFilesAsync(Files(directory, path => path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase), recursive: false));
        }
        if (ids.Contains(Downloads))
        {
            progress?.Report("正在删除勾选的已下载媒体…");
            foreach (var entry in await database.GetDownloadsAsync(token))
            {
                token.ThrowIfCancellationRequested();
                await DeleteDownloadAssetsAsync(entry.FilePath, DeleteFilesAsync);
                await DeleteFilesAsync([entry.FilePath]);
                await database.RemoveDownloadAsync(entry, token);
            }
        }
        if (ids.Contains(UserTemporary))
        {
            progress?.Report("正在清理七天前的临时文件…");
            await DeleteFilesAsync(Files(Path.GetTempPath(), path => !IsUnder(path, AppPaths.Root) && IsExpired(path), limit: 5000));
        }
        if (ids.Contains(RecycleBin))
        {
            progress?.Report("正在清空回收站…");
            var result = SHEmptyRecycleBin(0, null, 1 | 2 | 4);
            if (result != 0 && QueryRecycleBin(out _, out var remaining) && remaining > 0) failed += (int)Math.Min(int.MaxValue, remaining);
        }
        return new CleanupResult(freed, deleted, failed);
    }

    private static async Task DeleteDownloadAssetsAsync(string manifest, Func<IEnumerable<string>, Task> delete)
    {
        if (!manifest.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || !File.Exists(manifest)) return;
        var folder = Path.GetDirectoryName(manifest);
        if (folder is null) return;
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(manifest))
        {
            var candidates = line.StartsWith('#') ? new[] { line } : [line.Trim()];
            foreach (var candidate in candidates)
            {
                var start = candidate.IndexOf("media-", StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                var relative = candidate[start..].TrimEnd('"');
                var name = relative.Split('/', '\\')[0];
                if (name.Length is < 7 or > 40 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) continue;
                var path = Path.GetFullPath(Path.Combine(folder, name));
                if (IsUnder(path, folder) && Directory.Exists(path) && !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) assets.Add(path);
            }
        }
        foreach (var directory in assets)
            await delete(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
    }

    private async Task<string> DownloadDirectoryAsync(CancellationToken token)
    {
        var configured = await database.GetSettingAsync("downloads.defaultFolder", token);
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "流光影院") : configured;
    }

    private static (long Bytes, int Files) Measure(string root, Func<string, bool> predicate, bool recursive = true, int limit = 10000)
    {
        long bytes = 0;
        var count = 0;
        foreach (var path in Files(root, predicate, recursive, limit))
        {
            try { bytes += new FileInfo(path).Length; count++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (bytes, count);
    }

    private static IEnumerable<string> Files(string root, Func<string, bool> predicate, bool recursive = true, int limit = 10000)
    {
        if (!Directory.Exists(root)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, ReturnSpecialDirectories = false };
        var count = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var path in files)
        {
            if (++count > limit || !predicate(path)) continue;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { continue; }
            if (predicate(full)) yield return full;
        }
    }

    private static bool IsExpired(string path)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool QueryRecycleBin(out long bytes, out long files)
    {
        bytes = files = 0;
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBin(null, ref info) != 0) return false;
        bytes = info.i64Size;
        files = info.i64NumItems;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(nint window, string? root, uint flags);
}
