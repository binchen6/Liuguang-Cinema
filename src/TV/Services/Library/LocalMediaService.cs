using System.Collections.Concurrent;
using System.Text.Json;
using TV.Models;
using TV.Services.Persistence;
using TV.Services.Playback;

namespace TV.Services.Library;

public sealed class LocalMediaService : IDisposable
{
    private readonly DatabaseService database;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".ts", ".m2ts", ".mts", ".flv", ".f4v",
        ".wmv", ".asf", ".mpg", ".mpeg", ".vob", ".3gp", ".3g2", ".rm", ".rmvb", ".ogv"
    };
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer _timer;
    private bool _started;

    public event EventHandler? Changed;
    public static IReadOnlySet<string> Extensions => SupportedExtensions;

    public LocalMediaService(DatabaseService database)
    {
        this.database = database;
        _timer = new Timer(_ => { if (_started && !_lifetime.IsCancellationRequested) _ = RefreshAsync(_lifetime.Token); },
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _refreshTimer = new Timer(_ => _ = DrainPendingAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        if (_started) return;
        await _initializeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_started) return;
            foreach (var folder in await ReadFoldersAsync(token).ConfigureAwait(false)) Watch(folder);
            _started = true;
            _timer.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        finally { _initializeGate.Release(); }
    }

    private readonly Timer _refreshTimer;

    public async Task<IReadOnlyList<string>> GetFoldersAsync(CancellationToken token = default) => await ReadFoldersAsync(token).ConfigureAwait(false);

    public async Task<int> ImportFilesAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        var files = paths.Where(IsVideo).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => Read(path, string.Empty)).OfType<LocalMediaEntry>().ToArray();
        if (files.Length > 0) await database.SaveLocalFilesAsync(files, null, token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return files.Length;
    }

    public async Task<int> AddFolderAsync(string path, CancellationToken token = default)
    {
        var folder = ValidateFolder(path);
        var folders = (await ReadFoldersAsync(token).ConfigureAwait(false)).Append(folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await database.SetSettingAsync("local.folders", JsonSerializer.Serialize(folders), token).ConfigureAwait(false);
        Watch(folder);
        var count = await RefreshFolderAsync(folder, token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    public async Task RemoveFolderAsync(string path, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        var folders = (await ReadFoldersAsync(token).ConfigureAwait(false)).Where(folder => !folder.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
        await database.SetSettingAsync("local.folders", JsonSerializer.Serialize(folders), token).ConfigureAwait(false);
        if (_watchers.TryRemove(path, out var watcher)) watcher.Dispose();
        await database.RemoveLocalFileAsync(path, path, token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<List<LocalMediaEntry>> GetFilesAsync(CancellationToken token = default) => database.GetLocalFilesAsync(token);

    public async Task RemoveFileAsync(string path, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        await database.RemoveLocalFileAsync(path, token: token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        foreach (var folder in await ReadFoldersAsync(token).ConfigureAwait(false))
        {
            if (Directory.Exists(folder)) await RefreshFolderAsync(folder, token).ConfigureAwait(false);
            Watch(folder);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<int> RefreshFolderAsync(string folder, CancellationToken token)
    {
        await _refreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var files = await Task.Run(() =>
            {
                var result = new List<LocalMediaEntry>();
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, ReturnSpecialDirectories = false };
                    foreach (var path in Directory.EnumerateFiles(folder, "*", options).Where(IsVideo).Take(5000))
                        if (Read(path, folder) is { } file) result.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Log($"本地媒体目录读取失败: {ex.GetType().Name}");
                }
                return result;
            }, token).ConfigureAwait(false);
            await database.SaveLocalFilesAsync(files, folder, token).ConfigureAwait(false);
            return files.Count;
        }
        finally { _refreshGate.Release(); }
    }

    private async Task<string[]> ReadFoldersAsync(CancellationToken token)
    {
        try
        {
            var json = await database.GetSettingAsync("local.folders", token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return (JsonSerializer.Deserialize<string[]>(json) ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(folder => folder.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diagnostics.Log($"本地媒体目录设置读取失败: {ex.GetType().Name}");
            return [];
        }
    }

    private void Watch(string folder)
    {
        if (!Directory.Exists(folder) || _watchers.ContainsKey(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, args) => Schedule(args.FullPath, folder);
            RenamedEventHandler renamed = (_, args) => Schedule(args.FullPath, folder);
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.EnableRaisingEvents = true;
            if (!_watchers.TryAdd(folder, watcher)) watcher.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"本地媒体目录监视不可用: {ex.GetType().Name}");
        }
    }

    private void Schedule(string path, string folder)
    {
        if (!IsVideo(path) || _lifetime.IsCancellationRequested) return;
        _pending.TryAdd(folder, 0);
        _refreshTimer.Change(TimeSpan.FromMilliseconds(900), Timeout.InfiniteTimeSpan);
    }

    private async Task DrainPendingAsync()
    {
        foreach (var folder in _pending.Keys)
        {
            _pending.TryRemove(folder, out _);
            try { await RefreshFolderAsync(folder, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Log($"本地媒体刷新失败: {ex.GetType().Name}"); }
        }
        if (_pending.IsEmpty) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ValidateFolder(string path)
    {
        var folder = Path.GetFullPath(path);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("选择的文件夹不存在。");
        if (folder.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(folder)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能直接监视整个磁盘，请选择视频所在的文件夹。");
        return folder;
    }

    private static LocalMediaEntry? Read(string path, string folder)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new LocalMediaEntry(file.FullName, Path.GetFileNameWithoutExtension(file.Name), folder, file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsVideo(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    private static bool IsExtension(string path, params string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static PlaybackRequest CreateRequest(string path)
    {
        var file = Path.GetFullPath(path);
        if (!File.Exists(file) || !IsVideo(file)) throw new FileNotFoundException("本地视频不存在或格式不受支持。", file);
        var request = new PlaybackRequest { Kind = PlaybackKind.Vod, Url = new Uri(file).AbsoluteUri,
            Title = Path.GetFileNameWithoutExtension(file), SiteKey = "local", VodId = file, Subtitle = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty) };
        AttachSidecars(request, file);
        return request;
    }

    private static void AttachSidecars(PlaybackRequest request, string file)
    {
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (directory is null) return;
            var basename = Path.GetFileNameWithoutExtension(file);
            var files = Directory.EnumerateFiles(directory, basename + ".*").Take(32).ToArray();
            request.Subtitles = files.Where(path => IsExtension(path, ".srt", ".ass", ".ssa", ".vtt")).Select(path => new Uri(path)).ToList();
            var danmaku = files.FirstOrDefault(path => IsExtension(path, ".xml", ".json"));
            if (danmaku is not null) request.Danmaku = new Uri(danmaku);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        _refreshTimer.Dispose();
        _timer.Dispose();
        _lifetime.Dispose();
    }
}
