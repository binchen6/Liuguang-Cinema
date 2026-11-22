using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TV.Models;
using TV.Services;
using TV.Services.Library;

namespace TV.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<PlaybackRequest> _play;
    private readonly Action<VodItem> _openDetail;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private List<HistoryEntry> _history = [];
    private List<FavoriteEntry> _favorites = [];
    private List<DownloadEntry> _downloads = [];
    private List<LocalMediaEntry> _localFiles = [];

    public LibraryViewModel(AppServices services, Action<PlaybackRequest> play, Action<VodItem> openDetail)
    {
        _services = services;
        _play = play;
        _openDetail = openDetail;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _services.LocalMedia.Changed += OnLocalMediaChanged;
    }

    private void OnLocalMediaChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() => _ = RefreshLocalFilesAsync());

    private async Task RefreshLocalFilesAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            _localFiles = await _services.LocalMedia.GetFilesAsync();
            ApplySearch();
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = "本地媒体自动更新失败：" + ex.Message; }
        finally { _loadGate.Release(); }
    }

    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<FavoriteEntry> Favorites { get; } = [];
    public ObservableCollection<DownloadEntry> Downloads { get; } = [];
    public ObservableCollection<LocalMediaEntry> LocalFiles { get; } = [];
    public ObservableCollection<string> LocalFolders { get; } = [];
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _emptyHistory = string.Empty;
    [ObservableProperty] private string _emptyLocal = string.Empty;
    [ObservableProperty] private bool _isLocalBusy;
    /// <summary>分段控件可见性：继续观看 / 收藏 / 下载。</summary>
    public bool IsHistoryTab => SelectedTab == 0;
    public bool IsFavoritesTab => SelectedTab == 1;
    public bool IsDownloadsTab => SelectedTab == 2;
    public bool IsLocalTab => SelectedTab == 3;
    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsHistoryTab));
        OnPropertyChanged(nameof(IsFavoritesTab));
        OnPropertyChanged(nameof(IsDownloadsTab));
        OnPropertyChanged(nameof(IsLocalTab));
    }

    [ObservableProperty] private string _emptyFavorites = string.Empty;
    [ObservableProperty] private string _emptyDownloads = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(HasSuccess))] private string _statusMessage = string.Empty;
    private bool _statusIsError;
    public bool HasError => _statusIsError && !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasSuccess => !_statusIsError && !string.IsNullOrWhiteSpace(StatusMessage);

    internal void ReportError(string message)
    {
        _statusIsError = true;
        StatusMessage = message;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var database = _services.Database;
            var historyTask = database.GetHistoryAsync();
            var favoritesTask = database.GetFavoritesAsync();
            var downloadsTask = database.GetDownloadsAsync();
            var localInitTask = _services.LocalMedia.InitializeAsync();
            await Task.WhenAll(historyTask, favoritesTask, downloadsTask, localInitTask);
            var history = await historyTask;
            var favorites = await favoritesTask;
            var downloads = await downloadsTask;
            var localFilesTask = _services.LocalMedia.GetFilesAsync();
            var foldersTask = _services.LocalMedia.GetFoldersAsync();
            await Task.WhenAll(localFilesTask, foldersTask);
            _localFiles = await localFilesTask;
            var folders = await foldersTask;
            LocalFolders.Clear();
            foreach (var folder in folders) LocalFolders.Add(folder);
            _history = history;
            _favorites = favorites;
            _downloads = downloads;
            ApplySearch();
            _statusIsError = false;
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
        finally { _loadGate.Release(); }
    }

    partial void OnSearchQueryChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var terms = SearchQuery.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        ReplaceItems(History, _history.Where(item => Matches(item.Title, item.Subtitle, terms)));
        ReplaceItems(Favorites, _favorites.Where(item => Matches(item.Title, item.Subtitle, terms)));
        ReplaceItems(Downloads, _downloads.Where(item => Matches(item.Title, item.Episode, terms)));
        ReplaceItems(LocalFiles, _localFiles.Where(item => Matches(item.Title, item.Path, terms)));
        EmptyLocal = EmptyLabel(LocalFiles.Count, terms.Length, "尚未导入本地视频");
        EmptyHistory = EmptyLabel(History.Count, terms.Length, "暂无播放记录");
        EmptyFavorites = EmptyLabel(Favorites.Count, terms.Length, "暂无收藏");
        EmptyDownloads = EmptyLabel(Downloads.Count, terms.Length, "暂无下载");
    }

    private static bool Matches(string title, string subtitle, string[] terms) =>
        terms.All(term => title.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                          subtitle.Contains(term, StringComparison.CurrentCultureIgnoreCase));

    private static string EmptyLabel(int count, int terms, string empty) =>
        count > 0 ? string.Empty : terms == 0 ? empty : "没有匹配项";

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    [RelayCommand]
    private void OpenHistory(HistoryEntry? entry)
    {
        if (entry is null) return;
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<PlaybackRequest>(entry.RequestJson, JsonUtil.Options);
            if (request is not null)
            {
                request.StartPositionMs = entry.IsCompleted ? 0 : entry.PositionMs;
                request.RecoverMissingSource = true;
                _play(request);
            }
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = "无法恢复播放记录：" + ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteHistoryAsync(HistoryEntry? entry)
    {
        if (entry is null) return;
        try
        {
            await _services.Database.DeleteHistoryAsync(entry.Id);
            _history.RemoveAll(item => item.Id == entry.Id);
            ApplySearch();
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void OpenFavorite(FavoriteEntry? entry)
    {
        if (entry is null) return;
        try
        {
            if (entry.Kind == "Live")
            {
                var channel = System.Text.Json.JsonSerializer.Deserialize<LiveChannel>(entry.DataJson, JsonUtil.Options);
                if (channel is not null) _play(new PlaybackRequest { Kind = PlaybackKind.Live, Title = channel.Name, Subtitle = channel.Group, Url = channel.Url, Picture = channel.Logo, Channel = channel, Headers = channel.Headers });
            }
            else
            {
                var item = System.Text.Json.JsonSerializer.Deserialize<VodItem>(entry.DataJson, JsonUtil.Options);
                if (item is not null) _openDetail(item);
            }
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = "无法打开收藏：" + ex.Message; }
    }

    [RelayCommand]
    private void OpenDownload(DownloadEntry? entry)
    {
        if (entry is not null) OpenLocal(entry.FilePath, entry.Title, entry.Episode);
    }

    [RelayCommand]
    private async Task RemoveDownloadAsync(DownloadEntry? entry)
    {
        if (entry is null) return;
        try
        {
            await _services.Database.RemoveDownloadAsync(entry);
            _downloads.RemoveAll(item => string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
            ApplySearch();
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        if (IsLocalBusy) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary
        };
        foreach (var extension in LocalMediaService.Extensions) picker.FileTypeFilter.Add(extension);
        IsLocalBusy = true;
        try
        {
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            var count = await _services.LocalMedia.ImportFilesAsync(files.Select(file => file.Path));
            _localFiles = await _services.LocalMedia.GetFilesAsync();
            SelectedTab = 3;
            ApplySearch();
            _statusIsError = false; StatusMessage = $"已导入 {count} 个本地视频";
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = "导入本地视频失败：" + ex.Message; }
        finally { IsLocalBusy = false; }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (IsLocalBusy) return;
        IsLocalBusy = true;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            StatusMessage = "正在读取本地文件夹…";
            var count = await _services.LocalMedia.AddFolderAsync(folder.Path);
            await LoadAsync();
            SelectedTab = 3;
            _statusIsError = false; StatusMessage = $"已导入文件夹，共 {count} 个视频；新增、删除和修改会自动更新。";
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = "导入文件夹失败：" + ex.Message; }
        finally { IsLocalBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshLocalAsync()
    {
        if (IsLocalBusy) return;
        IsLocalBusy = true;
        try { _statusIsError = false; await _services.LocalMedia.RefreshAsync(); await LoadAsync(); _statusIsError = false; StatusMessage = "本地媒体索引已更新"; }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
        finally { IsLocalBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveLocalFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { await _services.LocalMedia.RemoveFolderAsync(path); await LoadAsync(); StatusMessage = "已停止监视并移除该文件夹的索引，原文件未删除。"; }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void OpenLocalMedia(LocalMediaEntry? entry)
    {
        if (entry is null) return;
        try { _play(LocalMediaService.CreateRequest(entry.Path)); }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveLocalMediaAsync(LocalMediaEntry? entry)
    {
        if (entry is null) return;
        try { await _services.LocalMedia.RemoveFileAsync(entry.Path); _localFiles.Remove(entry); ApplySearch(); }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary
        };
        foreach (var extension in LocalMediaService.Extensions.Concat([".m3u8", ".mp3", ".m4a", ".media"]))
            picker.FileTypeFilter.Add(extension);
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is not null) OpenLocal(file.Path, Path.GetFileNameWithoutExtension(file.Path), string.Empty);
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }

    private void OpenLocal(string path, string title, string subtitle)
    {
        if (!File.Exists(path))
        {
            _statusIsError = true;
            StatusMessage = "本地文件已移动或删除。";
            return;
        }
        if (LocalMediaService.IsVideo(path))
        {
            _play(LocalMediaService.CreateRequest(path));
            return;
        }
        _play(new PlaybackRequest
        {
            Kind = PlaybackKind.Vod, Url = new Uri(path).AbsoluteUri, Title = title,
            SiteKey = "local", VodId = path, Subtitle = subtitle
        });
    }

    [RelayCommand]
    private async Task RemoveFavoriteAsync(FavoriteEntry? entry)
    {
        if (entry is null) return;
        try
        {
            await _services.Database.SetFavoriteAsync(entry.Id, entry.Title, entry.Picture, entry.Kind, entry.Subtitle, entry.DataJson, false);
            _favorites.RemoveAll(item => item.Id == entry.Id);
            ApplySearch();
        }
        catch (Exception ex) { _statusIsError = true; StatusMessage = ex.Message; }
    }
}
