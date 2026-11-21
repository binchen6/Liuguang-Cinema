using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using TV.Models;
using TV.Services;
using TV.Services.Cloud;
using TV.Services.Persistence;
using TV.Services.Maintenance;
using TV.Views;

namespace TV.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loaded;

    public SettingsViewModel(AppServices services) => _services = services;

    [ObservableProperty] private string _activeSource = "尚未加载";
    [ObservableProperty] private string _newSourceUrl = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _doubanEnabled = true;
    [ObservableProperty] private int _recommendationSourceIndex;
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<ImportedRepository> ImportedRepositories { get; } = [];
    public ObservableCollection<CloudAccountInfo> CloudAccounts { get; } = [];
    public ObservableCollection<CleanupCategory> CleanupCategories { get; } = [];
    [ObservableProperty] private bool _hasCleanupResults;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _cleanupSummary = string.Empty;
    [ObservableProperty] private bool _hardwareDecoding = true;
    [ObservableProperty] private bool _autoNext = true;
    [ObservableProperty] private bool _resumePlayback = true;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private int _closeBehaviorIndex;
    private bool _settingCloseBehavior;
    [ObservableProperty] private bool _autoRefreshSources = true;
    [ObservableProperty] private string _sourceSummary = string.Empty;
    [ObservableProperty] private string _lastSourceRefresh = string.Empty;
    [ObservableProperty] private string _storageSummary = string.Empty;
    [ObservableProperty] private string _downloadFolder = string.Empty;
    [ObservableProperty] private int _networkCache = 3600;
    [ObservableProperty] private int _liveCache = 1800;
    [ObservableProperty] private bool _adaptiveQuality = true;
    [ObservableProperty] private int _defaultVolume = 85;
    [ObservableProperty] private int _defaultQualityIndex;
    [ObservableProperty] private bool _autoSwitchLine = true;
    [ObservableProperty] private int _searchConcurrency = 3;
    [ObservableProperty] private bool _cloudAcceleration = true;
    [ObservableProperty] private int _cloudThreads = 2;
    [ObservableProperty] private int _cloudChunkMb = 4;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _dnsIndex;
    [ObservableProperty] private string _aliToken = string.Empty;
    [ObservableProperty] private string _quarkCookie = string.Empty;
    [ObservableProperty] private string _ucCookie = string.Empty;
    [ObservableProperty] private string _baiduCookie = string.Empty;
    public CloudLoginCoordinator CloudLogin => _services.CloudLogin;

    public async Task LoadAsync()
    {
        _loaded = false;
        ActiveSource = _services.Config.ActiveUrl;
        await LoadImportsAsync();
        await RefreshCloudAccountsAsync();
        await UpdateSourceSummaryAsync();
        await RefreshStorageAsync();
        DoubanEnabled = await Bool("douban.enabled", true);
        RecommendationSourceIndex = await Int("vod.recommendations", 0) == 1 ? 1 : 0;
        HardwareDecoding = await Bool("playback.hardware", true);
        AutoNext = await Bool("playback.autoNext", true);
        ResumePlayback = await Bool("playback.resume", true);
        AnimationsEnabled = await Bool("ui.animations", true);
        MinimizeToTray = await Bool("ui.minimizeToTray", true);
        var behavior = await Int("ui.closeBehavior", 0);
        CloseBehaviorIndex = Enum.IsDefined((WindowCloseBehavior)behavior) ? behavior : 0;
        AutoRefreshSources = await Bool("source.autoRefresh", true);
        NetworkCache = await Int("playback.networkCache", 3600);
        LiveCache = await Int("playback.liveCache", 1800);
        AdaptiveQuality = await Bool("playback.adaptiveQuality", true);
        DefaultVolume = await Int("playback.volume", 85);
        DefaultQualityIndex = Math.Clamp(await Int("playback.defaultQuality", 0), 0, 4);
        AutoSwitchLine = await Bool("playback.autoSwitchLine", true);
        SearchConcurrency = await Int("search.concurrency", 3);
        CloudAcceleration = await Bool("playback.cloudAcceleration", true);
        CloudThreads = await Int("playback.cloudThreads", 2);
        CloudChunkMb = await Int("playback.cloudChunkMb", 4);
        ThemeIndex = await Int("ui.theme", 0);
        DnsIndex = await Int("network.dns", 0);
        DownloadFolder = await _services.Database.GetSettingAsync("downloads.defaultFolder") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "流光影院");
        AliToken = string.Empty;
        QuarkCookie = string.Empty;
        UcCookie = string.Empty;
        BaiduCookie = string.Empty;
        _loaded = true;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusMessage = "正在更新…";
        try
        {
            await _services.Config.LoadAsync(true);
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
            StatusMessage = "已更新";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        try
        {
            await _services.Config.ImportAsync(NewSourceUrl);
            NewSourceUrl = string.Empty;
            await LoadImportsAsync();
            StatusMessage = "已导入";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(ImportedRepository? repository)
    {
        if (repository is null) return;
        try
        {
            await _services.Config.RemoveImportAsync(repository.Url);
            await LoadImportsAsync();
            StatusMessage = "已删除";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task LoadImportsAsync()
    {
        var repositories = await _services.Config.GetImportsAsync();
        ImportedRepositories.Clear();
        foreach (var repository in repositories) ImportedRepositories.Add(repository);
    }

    private async Task UpdateSourceSummaryAsync()
    {
        var config = _services.Config.Current;
        SourceSummary = config is null ? "尚未加载配置" : $"{config.Sites.Count} 个点播站点 · {config.Lives.Count} 个直播源 · {ImportedRepositories.Count} 个导入仓库";
        if (DateTimeOffset.TryParse(await _services.Database.GetSettingAsync("source.lastRefresh"), out var refreshed))
            LastSourceRefresh = "最近刷新 " + refreshed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        else LastSourceRefresh = "尚未记录刷新时间";
    }

    [RelayCommand]
    private async Task RefreshCloudAccountsAsync()
    {
        var accounts = new List<CloudAccountInfo>();
        foreach (var provider in Enum.GetValues<CloudProviderKind>()) accounts.Add(await _services.Cloud.GetAccountAsync(provider));
        CloudAccounts.Clear();
        foreach (var account in accounts) CloudAccounts.Add(account);
    }

    [RelayCommand]
    private async Task VerifyCloudAsync(CloudProviderKind provider)
    {
        var index = CloudAccounts.ToList().FindIndex(account => account.Provider == provider);
        if (index >= 0) CloudAccounts[index] = CloudAccounts[index] with { State = CloudAccountState.Checking, Status = "正在验证…" };
        var account = await _services.Cloud.VerifyAccountAsync(provider);
        if (index >= 0) CloudAccounts[index] = account;
        StatusMessage = $"{account.Name}：{account.Status}";
    }

    [RelayCommand]
    private void OpenCloudDrive() => App.Window.Navigate("cloud");

    [RelayCommand]
    private void ResetCloudAcceleration()
    {
        CloudAcceleration = true;
        CloudThreads = 2;
        CloudChunkMb = 4;
        StatusMessage = "已恢复安全加速默认值";
    }

    [RelayCommand]
    private async Task RefreshStorageAsync()
    {
        try
        {
            long images = Directory.Exists(AppPaths.Images)
                ? Directory.EnumerateFiles(AppPaths.Images, "*.img").Sum(path => new FileInfo(path).Length) : 0;
            long database = File.Exists(AppPaths.Database) ? new FileInfo(AppPaths.Database).Length : 0;
            var wal = AppPaths.Database + "-wal";
            if (File.Exists(wal)) database += new FileInfo(wal).Length;
            var history = await _services.Database.CountRowsAsync("history");
            var favorites = await _services.Database.CountRowsAsync("favorites");
            var downloads = await _services.Database.CountRowsAsync("downloads");
            var metadata = await _services.Database.CountRowsAsync("metadata");
            StorageSummary = $"图片缓存 {FormatSize(images)} · 元数据 {metadata} 项 · 数据库 {FormatSize(database)}\n播放记录 {history} 项 · 收藏 {favorites} 项 · 下载 {downloads} 项";
        }
        catch (Exception ex) { StorageSummary = "存储统计暂不可用：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SelectDownloadFolderAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) DownloadFolder = folder.Path;
        }
        catch (Exception ex) { StatusMessage = "选择下载目录失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task OpenDataFolderAsync()
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(AppPaths.Root);
        await Windows.System.Launcher.LaunchFolderAsync(folder);
    }

    [RelayCommand]
    private async Task OpenDiagnosticsAsync()
    {
        var path = Path.Combine(AppPaths.Root, "diagnostic.log");
        if (!File.Exists(path)) { StatusMessage = "诊断日志尚未生成"; return; }
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.##} GB" : $"{bytes / 1024d / 1024:0.##} MB";

    [RelayCommand]
    private async Task SaveCloudAsync()
    {
        if (!string.IsNullOrWhiteSpace(AliToken)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Ali, AliToken);
        if (!string.IsNullOrWhiteSpace(QuarkCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Quark, QuarkCookie);
        if (!string.IsNullOrWhiteSpace(UcCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.UC, UcCookie);
        if (!string.IsNullOrWhiteSpace(BaiduCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Baidu, BaiduCookie);
        _services.Cloud.ClearShareCache();
        await RefreshCloudAccountsAsync();
        StatusMessage = "网盘凭据已加密保存";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartCloudLoginAsync(string? provider)
    {
        var kind = CloudDriveService.ParseProvider(provider ?? string.Empty);
        await CloudLogin.StartAsync(kind);
    }

    [RelayCommand]
    private void CancelCloudLogin() => CloudLogin.Close();

    [RelayCommand]
    private async Task LogoutCloudAsync(CloudProviderKind kind)
    {
        await _services.Cloud.SaveCredentialAsync(kind, string.Empty);
        if (kind == CloudProviderKind.Ali) AliToken = string.Empty;
        else if (kind == CloudProviderKind.Quark) QuarkCookie = string.Empty;
        else if (kind == CloudProviderKind.UC) UcCookie = string.Empty;
        else BaiduCookie = string.Empty;
        _services.Cloud.ClearShareCache();
        await RefreshCloudAccountsAsync();
        StatusMessage = $"{kind} 已退出";
    }

    [RelayCommand]
    private async Task ScanCleanupAsync()
    {
        if (IsCleaning) return;
        IsCleaning = true;
        try
        {
            var categories = await _services.Cleanup.ScanAsync(CancellationToken.None);
            CleanupCategories.Clear();
            foreach (var category in categories) CleanupCategories.Add(category);
            HasCleanupResults = CleanupCategories.Count > 0;
            CleanupSummary = $"共扫描到 {CleanupCategories.Sum(category => category.Bytes) / 1024d / 1024:0.##} MB；高风险项目默认不选中。";
        }
        catch (Exception ex) { CleanupSummary = "扫描失败：" + ex.Message; }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        if (IsCleaning) return;
        var selected = CleanupCategories.Where(category => category.Selected).Select(category => category.Id).ToArray();
        if (selected.Length == 0) { StatusMessage = "请先勾选要清理的项目"; return; }
        IsCleaning = true;
        try
        {
            var result = await _services.Cleanup.CleanAsync(selected, new Progress<string>(value => StatusMessage = value), CancellationToken.None);
            StatusMessage = result.Summary;
            IsCleaning = false;
            await ScanCleanupAsync();
            await RefreshStorageAsync();
        }
        catch (Exception ex) { StatusMessage = "清理失败：" + ex.Message; }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private async Task ClearCachesAsync()
    {
        await _services.Images.ClearAsync();
        await _services.Database.ClearTableAsync("metadata");
        _services.Cloud.ClearShareCache();
        await RefreshStorageAsync();
        StatusMessage = "缓存已清理";
    }

    [RelayCommand]
    private async Task ClearImagesAsync() { await _services.Images.ClearAsync(); await RefreshStorageAsync(); StatusMessage = "图片缓存已清除"; }

    [RelayCommand]
    private async Task ClearHistoryAsync() { await _services.Database.ClearTableAsync("history"); StatusMessage = "播放记录已清除"; }

    [RelayCommand]
    private async Task ClearFavoritesAsync() { await _services.Database.ClearTableAsync("favorites"); StatusMessage = "收藏已清除"; }

    [RelayCommand]
    private async Task ClearMetadataAsync() { await _services.Database.ClearTableAsync("metadata"); await RefreshStorageAsync(); StatusMessage = "元数据缓存已清除"; }

    partial void OnDoubanEnabledChanged(bool value) => Save("douban.enabled", value);
    partial void OnRecommendationSourceIndexChanged(int value)
    {
        if (!_loaded || value is not (0 or 1)) return;
        App.Window.ViewModel.Vod.UseRecommendationSource(value == 1);
        _ = SaveRecommendationSourceAsync(value);
    }

    private async Task SaveRecommendationSourceAsync(int value)
    {
        try
        {
            await _services.Database.SetSettingAsync("vod.recommendations", value == 1 ? "1" : "0");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"推荐来源保存失败: {ex.Message}");
            StatusMessage = "推荐来源保存失败";
        }
    }
    partial void OnHardwareDecodingChanged(bool value) => Save("playback.hardware", value);
    partial void OnAutoNextChanged(bool value) => Save("playback.autoNext", value);
    partial void OnResumePlaybackChanged(bool value) => Save("playback.resume", value);
    partial void OnAutoRefreshSourcesChanged(bool value) => Save("source.autoRefresh", value);
    partial void OnNetworkCacheChanged(int value) => Save("playback.networkCache", Math.Clamp(value, 1200, 12000));
    partial void OnLiveCacheChanged(int value) => Save("playback.liveCache", Math.Clamp(value, 500, 8000));
    partial void OnAdaptiveQualityChanged(bool value) => Save("playback.adaptiveQuality", value);
    partial void OnDefaultVolumeChanged(int value) => Save("playback.volume", Math.Clamp(value, 0, 100));
    partial void OnDefaultQualityIndexChanged(int value) => Save("playback.defaultQuality", Math.Clamp(value, 0, 4));
    partial void OnAutoSwitchLineChanged(bool value) => Save("playback.autoSwitchLine", value);
    partial void OnSearchConcurrencyChanged(int value) => Save("search.concurrency", Math.Clamp(value, 1, 6));
    partial void OnCloudAccelerationChanged(bool value) => Save("playback.cloudAcceleration", value);
    partial void OnCloudThreadsChanged(int value) => Save("playback.cloudThreads", Math.Clamp(value, 1, 3));
    partial void OnCloudChunkMbChanged(int value) => Save("playback.cloudChunkMb", Math.Clamp(value, 2, 8));

    partial void OnAnimationsEnabledChanged(bool value)
    {
        Save("ui.animations", value);
        MotionService.Enabled = value;
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (!_loaded) return;
        App.Window.SetMinimizeToTray(value);
        Save("ui.minimizeToTray", value);
    }

    partial void OnCloseBehaviorIndexChanged(int value)
    {
        if (_loaded && !_settingCloseBehavior && Enum.IsDefined((WindowCloseBehavior)value))
            _ = SaveCloseBehaviorAsync((WindowCloseBehavior)value);
    }

    internal async Task SaveCloseBehaviorAsync(WindowCloseBehavior behavior)
    {
        try
        {
            await _services.Database.SetSettingAsync("ui.closeBehavior", ((int)behavior).ToString(System.Globalization.CultureInfo.InvariantCulture));
            App.Window.SetCloseBehavior(behavior);
            _settingCloseBehavior = true;
            CloseBehaviorIndex = (int)behavior;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"关闭偏好保存失败: {ex.Message}");
            StatusMessage = "关闭偏好保存失败";
        }
        finally { _settingCloseBehavior = false; }
    }

    partial void OnDnsIndexChanged(int value) => Save("network.dns", Math.Clamp(value, 0, 5));
    partial void OnDownloadFolderChanged(string value) => Save("downloads.defaultFolder", value.Trim());

    partial void OnThemeIndexChanged(int value)
    {
        Save("ui.theme", value);
        if (_loaded) App.Window.SetTheme(value switch { 1 => ElementTheme.Light, 2 => ElementTheme.Default, _ => ElementTheme.Dark });
    }

    private async Task<bool> Bool(string key, bool fallback) => await _services.Database.GetSettingAsync(key) is not { } value ? fallback : value != "false";
    private async Task<int> Int(string key, int fallback) => int.TryParse(await _services.Database.GetSettingAsync(key), out var value) ? value : fallback;
    private void Save(string key, object value)
    {
        if (_loaded) _ = _services.Database.SetSettingAsync(key, value is bool flag ? (flag ? "true" : "false") : value.ToString()!);
    }
}
