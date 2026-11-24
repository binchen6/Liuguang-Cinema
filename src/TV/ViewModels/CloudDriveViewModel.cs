using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TV.Models;
using TV.Services;
using TV.Services.Cloud;

namespace TV.ViewModels;

public sealed partial class CloudDriveViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<PlaybackRequest> _play;
    private readonly List<CloudDriveFolder> _folders = [];
    private CancellationTokenSource _load = new();
    private int _page;
    private bool _suppressAccount;
    private bool _searchResults;

    public CloudDriveViewModel(AppServices services, Action<PlaybackRequest> play)
    {
        _services = services;
        _play = play;
    }

    public ObservableCollection<CloudAccountInfo> Accounts { get; } = [];
    public ObservableCollection<CloudDriveItem> Entries { get; } = [];
    public ObservableCollection<CloudDriveFolder> Breadcrumbs { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentAccountName))] private CloudAccountInfo? _selectedAccount;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _emptyText = "选择网盘后浏览文件";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRequiredLogin))] private CloudProviderKind? _requiredLoginProvider;
    [ObservableProperty] private bool _canGoUp;
    public string CurrentAccountName => SelectedAccount?.Name ?? "我的云盘";
    public bool HasRequiredLogin => RequiredLoginProvider.HasValue;

    public async Task InitializeAsync()
    {
        if (Accounts.Count > 0) return;
        var accounts = await ReadAccountsAsync();
        _suppressAccount = true;
        foreach (var account in accounts) Accounts.Add(account);
        SelectedAccount = accounts.FirstOrDefault(account => account.State != CloudAccountState.NotLoggedIn) ?? accounts.FirstOrDefault();
        _suppressAccount = false;
        if (SelectedAccount is not null) await LoadFolderAsync();
    }

    private async Task<CloudAccountInfo[]> ReadAccountsAsync()
    {
        var result = new List<CloudAccountInfo>();
        foreach (var provider in Enum.GetValues<CloudProviderKind>()) result.Add(await _services.Cloud.GetAccountAsync(provider));
        return result.ToArray();
    }

    public async Task RefreshAccountsAsync()
    {
        var selected = SelectedAccount?.Provider;
        var accounts = await ReadAccountsAsync();
        _suppressAccount = true;
        Accounts.Clear();
        foreach (var account in accounts) Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(account => account.Provider == selected) ?? Accounts.FirstOrDefault();
        _suppressAccount = false;
    }

    partial void OnSelectedAccountChanged(CloudAccountInfo? value)
    {
        if (!_suppressAccount && value is not null) _ = LoadFolderAsync();
    }

    private CancellationToken BeginLoad()
    {
        _load.Cancel();
        _load.Dispose();
        _load = new CancellationTokenSource();
        return _load.Token;
    }

    [RelayCommand]
    private async Task LoadFolderAsync()
    {
        if (SelectedAccount is not { } account) return;
        var token = BeginLoad();
        _searchResults = false;
        _page = 1;
        ResetFolders(account.Provider);
        await ReadFolderAsync(account.Provider, 1, false, token);
    }

    private async Task ReadFolderAsync(CloudProviderKind provider, int page, bool append, CancellationToken token)
    {
        IsLoading = true;
        if (!append) Entries.Clear();
        EmptyText = string.Empty;
        StatusMessage = string.Empty;
        RequiredLoginProvider = null;
        try
        {
            var folder = _folders[^1];
            var listing = await _services.Cloud.ListFolderAsync(provider, folder.Id, folder.Path, page, token);
            token.ThrowIfCancellationRequested();
            if (!append) Entries.Clear();
            foreach (var entry in listing.Items) Entries.Add(entry);
            _page = page;
            HasMore = listing.HasMore;
            StatusMessage = $"{Entries.Count} 项" + (HasMore ? " · 可加载更多" : "");
            EmptyText = Entries.Count == 0 ? "这个文件夹是空的" : string.Empty;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (CloudAuthenticationException ex)
        {
            RequiredLoginProvider = ex.Provider;
            Entries.Clear();
            EmptyText = "登录后浏览这个网盘";
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusMessage = ex.Message;
                EmptyText = Entries.Count == 0 ? "目录读取失败，请重试" : string.Empty;
            }
        }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SelectedAccount is not { } account || string.IsNullOrWhiteSpace(Query)) return;
        var token = BeginLoad();
        IsLoading = true;
        Entries.Clear();
        StatusMessage = string.Empty;
        EmptyText = string.Empty;
        RequiredLoginProvider = null;
        try
        {
            var listing = await _services.Cloud.SearchFolderAsync(account.Provider, Query.Trim(), 1, token);
            token.ThrowIfCancellationRequested();
            Entries.Clear();
            foreach (var entry in listing.Items) Entries.Add(entry);
            _searchResults = true;
            _page = 1;
            HasMore = listing.HasMore;
            CanGoUp = true;
            StatusMessage = $"找到 {Entries.Count} 项";
            EmptyText = Entries.Count == 0 ? "没有匹配的文件" : string.Empty;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (CloudAuthenticationException ex) { RequiredLoginProvider = ex.Provider; StatusMessage = ex.Message; }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusMessage = ex.Message; }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMore || SelectedAccount is not { } account) return;
        var token = _load.Token;
        if (_searchResults)
        {
            IsLoading = true;
            try
            {
                var listing = await _services.Cloud.SearchFolderAsync(account.Provider, Query.Trim(), _page + 1, token);
                token.ThrowIfCancellationRequested();
                foreach (var entry in listing.Items) Entries.Add(entry);
                _page++;
                HasMore = listing.HasMore;
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { StatusMessage = ex.Message; }
            finally { if (!token.IsCancellationRequested) IsLoading = false; }
        }
        else await ReadFolderAsync(account.Provider, _page + 1, true, token);
    }

    [RelayCommand]
    private async Task OpenEntryAsync(CloudDriveItem? entry)
    {
        if (entry is null) return;
        if (entry.IsFolder)
        {
            if (_searchResults)
            {
                _searchResults = false;
                _folders.Clear();
                _folders.Add(new CloudDriveFolder(entry.Provider == CloudProviderKind.Ali ? "root" : "0", "/", "全部文件"));
            }
            _folders.Add(new CloudDriveFolder(entry.Id, entry.Path, entry.Name));
            UpdateBreadcrumbs();
            await ReadFolderAsync(entry.Provider, 1, false, BeginLoad());
            return;
        }
        if (!IsMedia(entry.Name))
        {
            StatusMessage = "暂不支持在应用内预览该文件。";
            return;
        }
        var file = new CloudFile(entry.Provider, "drive", entry.Id, entry.Token, entry.Name, entry.Size,
            ParentDirectoryId: entry.ParentId, DrivePath: entry.Path);
        var url = CloudDriveService.GetPlaybackId(file);
        var line = new PlaybackLine
        {
            Name = SelectedAccount?.Name ?? "我的云盘",
            Episodes = [new Episode { Name = entry.Name, Url = url }]
        };
        var item = new VodItem
        {
            Id = url, SiteKey = "MDrive", SiteName = "我的云盘", Name = entry.Name,
            TypeName = "个人云盘", PlayFrom = line.Name, PlayUrl = CloudDriveService.SafeEpisodeName(entry.Name) + "$" + url
        };
        _play(new PlaybackRequest
        {
            Kind = PlaybackKind.Vod, Title = entry.Name, Subtitle = line.Name + " · 个人文件",
            Url = url, SiteKey = "MDrive", VodId = url, Item = item, Lines = [line]
        });
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (_searchResults)
        {
            Query = string.Empty;
            _searchResults = false;
            UpdateBreadcrumbs();
            await ReadFolderAsync(SelectedAccount!.Provider, 1, false, BeginLoad());
            return;
        }
        if (_folders.Count > 1)
        {
            _folders.RemoveAt(_folders.Count - 1);
            UpdateBreadcrumbs();
            await ReadFolderAsync(SelectedAccount!.Provider, 1, false, BeginLoad());
        }
    }

    [RelayCommand]
    private async Task OpenBreadcrumbAsync(CloudDriveFolder? folder)
    {
        if (folder is null) return;
        var index = _folders.IndexOf(folder);
        if (index < 0) return;
        _folders.RemoveRange(index + 1, _folders.Count - index - 1);
        _searchResults = false;
        Query = string.Empty;
        UpdateBreadcrumbs();
        await ReadFolderAsync(SelectedAccount!.Provider, 1, false, BeginLoad());
    }

    private void ResetFolders(CloudProviderKind provider)
    {
        _folders.Clear();
        _folders.Add(new CloudDriveFolder(provider == CloudProviderKind.Ali ? "root" : "0", "/", "全部文件"));
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        foreach (var folder in _folders) Breadcrumbs.Add(folder);
        CanGoUp = _folders.Count > 1 || _searchResults;
    }

    private static bool IsMedia(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".mp4" or ".m4v" or ".mkv" or ".avi" or ".mov" or ".webm" or ".flv" or ".ts" or ".m2ts" or
        ".rmvb" or ".wmv" or ".mpg" or ".mpeg" or ".3gp" or ".mp3" or ".m4a" or ".aac" or ".flac" or ".wav";
}
