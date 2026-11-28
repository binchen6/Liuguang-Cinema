using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using TV.Models;
using TV.Services;
using TV.Services.AI;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Packs;
using TV.Services.Persistence;
using TV.Services.Maintenance;
using TV.Services.Playback;
using TV.Services.Spider;
using TV.Services.Updates;
using TV.Views;

namespace TV.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private bool _loaded;
    private bool _hasLoaded;
    private bool _syncingRepository;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly Dictionary<string, string> _pendingWrites = [];
    private readonly SemaphoreSlim _settingsWriteGate = new(1, 1);

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _saveTimer = _dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(250);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await FlushPendingWritesAsync();
        _services.Config.Changed += OnSourceConfigChanged;
    }

    private void OnSourceConfigChanged(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(async () =>
        {
            if (!_hasLoaded) return;
            SyncRepositories();
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
        });
    }

    [ObservableProperty] private string _activeSource = "尚未加载";
    [ObservableProperty] private string _newSourceUrl = string.Empty;
    [ObservableProperty] private string _newSourceName = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _doubanEnabled = true;
    [ObservableProperty] private int _recommendationSourceIndex;
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<SourceRepository> Repositories { get; } = [];
    public ObservableCollection<ImportedRepository> ImportedRepositories { get; } = [];
    public ObservableCollection<RepoPackItemViewModel> RepoPacks { get; } = [];
    [ObservableProperty] private string _repoPackStatus = "点击刷新或导入仓库时自动匹配专属资源包";
    [ObservableProperty] private bool _isLoadingRepoPacks;
    [ObservableProperty] private bool _isDownloadingRepoPack;
    [ObservableProperty] private double _repoPackDownloadProgress;
    [ObservableProperty] private SourceRepository? _selectedRepository;
    public ObservableCollection<CloudAccountInfo> CloudAccounts { get; } = [];
    public ObservableCollection<CleanupCategory> CleanupCategories { get; } = [];
    [ObservableProperty] private bool _hasCleanupResults;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _cleanupSummary = string.Empty;
    [ObservableProperty] private bool _hardwareDecoding = true;
    [ObservableProperty] private bool _preventSleep = true;
    [ObservableProperty] private bool _autoNext = true;
    [ObservableProperty] private bool _resumePlayback = true;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private int _closeBehaviorIndex;
    private bool _settingCloseBehavior;
    [ObservableProperty] private bool _autoRefreshSources = true;
    [ObservableProperty] private bool _allowExternalExecutables;
    [ObservableProperty] private string _sourceSummary = string.Empty;
    [ObservableProperty] private string _lastSourceRefresh = string.Empty;
    [ObservableProperty] private string _storageSummary = string.Empty;
    [ObservableProperty] private string _downloadFolder = string.Empty;
    [ObservableProperty] private int _networkCache = 2000;
    [ObservableProperty] private int _liveCache = 1500;
    [ObservableProperty] private bool _adaptiveQuality = true;
    [ObservableProperty] private bool _cleanHls = true;
    [ObservableProperty] private int _defaultVolume = 85;
    [ObservableProperty] private int _defaultQualityIndex;
    [ObservableProperty] private bool _autoSwitchLine = true;
    [ObservableProperty] private int _searchConcurrency = 3;
    [ObservableProperty] private bool _cloudAcceleration = true;
    [ObservableProperty] private int _cloudThreads = 3;
    [ObservableProperty] private int _cloudChunkMb = 2;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _dnsIndex;
    [ObservableProperty] private string _aliToken = string.Empty;
    [ObservableProperty] private string _quarkCookie = string.Empty;
    [ObservableProperty] private string _ucCookie = string.Empty;
    [ObservableProperty] private string _baiduCookie = string.Empty;
    public CloudLoginCoordinator CloudLogin => _services.CloudLogin;

    // --- 阿里云百炼 AI 字幕与翻译 ---
    [ObservableProperty] private string _bailianApiKeyInput = string.Empty;
    [ObservableProperty] private string _bailianKeyStatus = "尚未配置 API Key";
    [ObservableProperty] private int _bailianRegionIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AsrModelHint))] private int _asrModelIndex = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MtModelHint))] private int _mtModelIndex;
    [ObservableProperty] private int _mtTargetLanguageIndex;
    [ObservableProperty] private bool _aiBilingualSubtitles = true;
    [ObservableProperty] private bool _aiRealtimeTranslate;
    [ObservableProperty] private bool _isTestingAi;
    public string AsrModelHint => AiSubtitleModels.AsrModels[Math.Clamp(AsrModelIndex, 0, AiSubtitleModels.AsrModels.Count - 1)].Description;
    public string MtModelHint => AiSubtitleModels.MtModels[Math.Clamp(MtModelIndex, 0, AiSubtitleModels.MtModels.Count - 1)].Description;

    // --- 弹弹play 开放弹幕库 ---
    [ObservableProperty] private string _danDanPlayStatus = "正在检测…";
    [ObservableProperty] private string _danDanPlayRelayUrlInput = string.Empty;
    [ObservableProperty] private string _danDanPlayAppIdInput = string.Empty;
    [ObservableProperty] private string _danDanPlayAppSecretInput = string.Empty;
    [ObservableProperty] private bool _isTestingDanDanPlay;

    [ObservableProperty] private bool _autoCheckUpdates = true;
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private string _updateStatus = "尚未检查更新";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAvailableUpdate))] private AppUpdateRelease? _availableUpdate;
    public bool HasAvailableUpdate => AvailableUpdate is not null;

    internal async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            AutoCheckUpdates = await Bool("updates.autoCheck", true);
            if (!AutoCheckUpdates || !await _services.Updates.ShouldCheckAutomaticallyAsync()) return;
            await CheckForUpdatesAsync(userInitiated: false);
        }
        catch (Exception ex) { Diagnostics.Log($"自动更新检查初始化失败: {ex.GetType().Name}"); }
    }

    [RelayCommand]
    private async Task CheckUpdateAsync() => await CheckForUpdatesAsync(userInitiated: true);

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        UpdateStatus = userInitiated ? "正在检查 GitHub Releases…" : "正在后台检查更新…";
        try
        {
            AvailableUpdate = await _services.Updates.CheckAsync();
            UpdateStatus = AvailableUpdate is null
                ? $"当前已是最新版本（{_services.Updates.CurrentVersion.ToString(3)}）"
                : $"发现新版本 {AvailableUpdate.Version.ToString(3)}，可下载安装";
            if (AvailableUpdate is not null) await App.Window.ShowUpdateAsync(AvailableUpdate);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"检查更新失败: {ex.GetType().Name} {ex.Message}");
            UpdateStatus = userInitiated ? "检查更新失败：" + ex.Message : "自动检查暂不可用，将在下次启动时重试";
        }
        finally { IsCheckingUpdates = false; }
    }

    [RelayCommand]
    private async Task ShowUpdateAsync()
    {
        if (AvailableUpdate is not null) await App.Window.ShowUpdateAsync(AvailableUpdate);
    }

    public async Task LoadAsync()
    {
        if (_hasLoaded) return;
        _loaded = false;
        ActiveSource = _services.Config.ActiveUrl;
        await LoadImportsAsync();
        await RefreshCloudAccountsAsync();
        await UpdateSourceSummaryAsync();
        await RefreshStorageAsync();
        DoubanEnabled = await Bool("douban.enabled", true);
        RecommendationSourceIndex = await Int("vod.recommendations", 0) == 1 ? 1 : 0;
        HardwareDecoding = await Bool("playback.hardware", true);
        PreventSleep = await Bool(PlaybackKeepAwakeService.SettingKey, true);
        _services.KeepAwake.Enabled = PreventSleep;
        AutoNext = await Bool("playback.autoNext", true);
        ResumePlayback = await Bool("playback.resume", true);
        AnimationsEnabled = await Bool("ui.animations", true);
        MinimizeToTray = await Bool("ui.minimizeToTray", true);
        var behavior = await Int("ui.closeBehavior", 0);
        CloseBehaviorIndex = Enum.IsDefined((WindowCloseBehavior)behavior) ? behavior : 0;
        AutoRefreshSources = await Bool("source.autoRefresh", true);
        AllowExternalExecutables = await Bool(ExternalSpiderPolicy.SettingKey, false);
        NetworkCache = await Int("playback.networkCache", 2000);
        LiveCache = await Int("playback.liveCache", 1500);
        AdaptiveQuality = await Bool("playback.adaptiveQuality", true);
        CleanHls = await Bool(HlsPlaylistCleaner.SettingKey, true);
        DefaultVolume = await Int("playback.volume", 85);
        DefaultQualityIndex = Math.Clamp(await Int("playback.defaultQuality", 0), 0, 4);
        AutoSwitchLine = await Bool("playback.autoSwitchLine", true);
        SearchConcurrency = await Int("search.concurrency", 3);
        CloudAcceleration = await Bool("playback.cloudAcceleration", true);
        CloudThreads = await Int("playback.cloudThreads", 3);
        CloudChunkMb = await Int("playback.cloudChunkMb", 2);
        ThemeIndex = await Int("ui.theme", 0);
        AutoCheckUpdates = await Bool("updates.autoCheck", true);
        DnsIndex = await Int("network.dns", 0);
        DownloadFolder = await _services.Database.GetSettingAsync("downloads.defaultFolder") ?? AppPaths.DefaultDownloads;
        AliToken = string.Empty;
        QuarkCookie = string.Empty;
        UcCookie = string.Empty;
        BaiduCookie = string.Empty;
        await LoadAiAndDanmakuSettingsAsync();
        _ = RefreshRepoPacksSafeAsync(forceRefresh: false);
        _loaded = true;
        _hasLoaded = true;
    }

    private async Task LoadAiAndDanmakuSettingsAsync()
    {
        var aiConfig = await _services.BailianSubtitle.GetConfigAsync();
        BailianApiKeyInput = string.Empty;
        BailianKeyStatus = aiConfig.HasApiKey
            ? $"已加密保存（尾号 {(aiConfig.ApiKey.Length >= 4 ? aiConfig.ApiKey[^4..] : "****")}）"
            : "尚未配置，点击右侧按钮前往阿里云百炼免费生成密钥";
        BailianRegionIndex = string.Equals(aiConfig.Region, "intl", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AsrModelIndex = AiSubtitleModels.FindAsrIndex(aiConfig.AsrModel);
        MtModelIndex = AiSubtitleModels.FindMtIndex(aiConfig.MtModel);
        MtTargetLanguageIndex = AiSubtitleModels.FindTargetLanguageIndex(aiConfig.TargetLanguage);
        AiBilingualSubtitles = aiConfig.Bilingual;
        AiRealtimeTranslate = aiConfig.RealtimeTranslate;

        DanDanPlayRelayUrlInput = await _services.DanDanPlay.GetCustomRelayUrlAsync();
        DanDanPlayAppIdInput = await _services.Database.GetSettingAsync(DanDanPlayService.SettingAppId) ?? string.Empty;
        DanDanPlayAppSecretInput = string.Empty;
        await RefreshDanDanPlayStatusAsync();
    }

    private async Task RefreshDanDanPlayStatusAsync()
    {
        if (await _services.DanDanPlay.HasCustomCredentialsAsync())
            DanDanPlayStatus = "已启用自定义弹弹play 开放平台凭证（DPAPI 加密保存）";
        else if (await _services.DanDanPlay.HasCustomRelayAsync())
            DanDanPlayStatus = "已启用自定义弹幕签名中继（支持国内直连、多节点自动故障转移与国内 CDN 直连弹幕）";
        else if (_services.DanDanPlay.HasDefaultService)
            DanDanPlayStatus = _services.DanDanPlay.DefaultRelayCount > 1
                ? $"已启用默认弹幕服务（{_services.DanDanPlay.DefaultRelayCount} 个签名中继自动故障转移 + 国内 CDN 直连弹幕）。"
                : "已启用默认弹幕服务（签名中继 + 国内 CDN 直连弹幕）。可配置国内中继或自定义凭据。";
        else
            DanDanPlayStatus = "尚未配置凭证或中继；请在下方填入国内中继地址或自定义 AppId / AppSecret";
    }

    [RelayCommand]
    private async Task OpenBailianConsoleAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(AiSubtitleModels.BailianApiKeyConsoleUrl));
            StatusMessage = "已在浏览器打开阿里云百炼 API Key 管理页，生成密钥后粘贴到下方保存即可使用。";
        }
        catch (Exception ex) { StatusMessage = "无法打开浏览器：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveBailianKeyAsync()
    {
        var key = BailianApiKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusMessage = "请先粘贴阿里云百炼 API Key（通常以 sk- 开头）。";
            return;
        }
        try
        {
            await _services.BailianSubtitle.SaveApiKeyAsync(key);
            BailianApiKeyInput = string.Empty;
            BailianKeyStatus = $"已加密保存（尾号 {(key.Length >= 4 ? key[^4..] : "****")}）";
            StatusMessage = "阿里云百炼 API Key 已使用当前用户 DPAPI 加密保存，输入框已清空。";
        }
        catch (Exception ex) { StatusMessage = "保存百炼 API Key 失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task VerifyBailianKeyAsync()
    {
        if (IsTestingAi) return;
        IsTestingAi = true;
        StatusMessage = "正在验证阿里云百炼 API Key 连通性…";
        try
        {
            var region = BailianRegionIndex == 1 ? "intl" : "cn";
            var (ok, message) = await _services.BailianSubtitle.VerifyApiKeyAsync(
                string.IsNullOrWhiteSpace(BailianApiKeyInput) ? null : BailianApiKeyInput, region);
            StatusMessage = message;
            if (ok && !string.IsNullOrWhiteSpace(BailianApiKeyInput))
                await SaveBailianKeyAsync();
        }
        finally { IsTestingAi = false; }
    }

    [RelayCommand]
    private async Task ClearBailianKeyAsync()
    {
        await _services.BailianSubtitle.ClearApiKeyAsync();
        BailianApiKeyInput = string.Empty;
        BailianKeyStatus = "尚未配置，点击右侧按钮前往阿里云百炼免费生成密钥";
        StatusMessage = "已清除本机保存的阿里云百炼 API Key。";
    }

    [RelayCommand]
    private async Task OpenDanDanPlayDocsAsync()
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(DanDanPlayService.OpenPlatformUrl)); }
        catch (Exception ex) { StatusMessage = "无法打开浏览器：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveDanDanPlayRelayAsync()
    {
        try
        {
            await _services.DanDanPlay.SaveCustomRelayUrlAsync(DanDanPlayRelayUrlInput);
            DanDanPlayRelayUrlInput = await _services.DanDanPlay.GetCustomRelayUrlAsync();
            await RefreshDanDanPlayStatusAsync();
            StatusMessage = string.IsNullOrWhiteSpace(DanDanPlayRelayUrlInput)
                ? "已清除自定义弹幕中继地址，将使用默认中继服务。"
                : "自定义弹幕签名中继地址已保存（支持国内免费边缘节点与自动故障转移）。";
        }
        catch (Exception ex) { StatusMessage = "保存中继地址失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveDanDanPlayAsync()
    {
        var appId = DanDanPlayAppIdInput.Trim();
        var appSecret = DanDanPlayAppSecretInput.Trim();
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            StatusMessage = "请同时填写弹弹play 的 AppId 与 AppSecret。";
            return;
        }
        try
        {
            await _services.DanDanPlay.SaveCustomCredentialsAsync(appId, appSecret);
            DanDanPlayAppSecretInput = string.Empty;
            await RefreshDanDanPlayStatusAsync();
            StatusMessage = "自定义弹弹play 凭证已加密保存。";
        }
        catch (Exception ex) { StatusMessage = "保存弹弹play 凭证失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task VerifyDanDanPlayAsync()
    {
        if (IsTestingDanDanPlay) return;
        IsTestingDanDanPlay = true;
        StatusMessage = "正在验证弹弹play 开放弹幕库连接…";
        try
        {
            var id = string.IsNullOrWhiteSpace(DanDanPlayAppIdInput) ? null : DanDanPlayAppIdInput.Trim();
            var secret = string.IsNullOrWhiteSpace(DanDanPlayAppSecretInput) ? null : DanDanPlayAppSecretInput.Trim();
            var (ok, msg) = await _services.DanDanPlay.VerifyCredentialsAsync(id, secret);
            StatusMessage = ok ? msg : "弹弹play 验证失败：" + msg;
        }
        finally { IsTestingDanDanPlay = false; }
    }

    [RelayCommand]
    private async Task ClearDanDanPlayAsync()
    {
        if (IsTestingDanDanPlay) return;
        IsTestingDanDanPlay = true;
        try
        {
            await _services.DanDanPlay.RestoreDefaultServiceAsync();
            DanDanPlayRelayUrlInput = string.Empty;
            DanDanPlayAppIdInput = string.Empty;
            DanDanPlayAppSecretInput = string.Empty;
            await RefreshDanDanPlayStatusAsync();
            if (!_services.DanDanPlay.HasDefaultService)
            {
                StatusMessage = "已清除自定义配置。当前版本未配置默认弹幕服务。";
                return;
            }
            StatusMessage = "已恢复默认配置，正在验证服务连接…";
            var (ok, message) = await _services.DanDanPlay.VerifyCredentialsAsync();
            StatusMessage = ok ? "已恢复默认配置，弹弹play 连接正常。" : "已恢复默认配置，默认服务当前不可用。" + message;
        }
        catch (Exception ex) { StatusMessage = "恢复默认配置失败。" + ex.Message; }
        finally { IsTestingDanDanPlay = false; }
    }

    partial void OnBailianRegionIndexChanged(int value) =>
        Save(AiSubtitleModels.SettingRegion, value == 1 ? "intl" : "cn");

    partial void OnAsrModelIndexChanged(int value)
    {
        var model = AiSubtitleModels.AsrModels[Math.Clamp(value, 0, AiSubtitleModels.AsrModels.Count - 1)];
        Save(AiSubtitleModels.SettingAsrModel, model.Id);
    }

    partial void OnMtModelIndexChanged(int value)
    {
        var model = AiSubtitleModels.MtModels[Math.Clamp(value, 0, AiSubtitleModels.MtModels.Count - 1)];
        Save(AiSubtitleModels.SettingMtModel, model.Id);
    }

    partial void OnMtTargetLanguageIndexChanged(int value)
    {
        var lang = AiSubtitleModels.TargetLanguages[Math.Clamp(value, 0, AiSubtitleModels.TargetLanguages.Count - 1)];
        Save(AiSubtitleModels.SettingTargetLanguage, lang.ApiName);
    }

    partial void OnAiBilingualSubtitlesChanged(bool value) =>
        Save(AiSubtitleModels.SettingBilingual, value);

    partial void OnAiRealtimeTranslateChanged(bool value) =>
        Save(AiSubtitleModels.SettingRealtimeTranslate, value);

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusMessage = "正在更新…";
        try
        {
            await _services.Config.LoadAsync(false);
            if (_services.Config.ActiveRepository is null)
            {
                StatusMessage = "请先导入仓库。";
                return;
            }
            await _services.Config.LoadAsync(true);
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
            StatusMessage = "已更新";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    partial void OnSelectedRepositoryChanged(SourceRepository? value)
    {
        if (_syncingRepository || !_loaded || value is null) return;
        if (string.Equals(value.Id, _services.Config.ActiveRepositoryId, StringComparison.OrdinalIgnoreCase)) return;
        _ = SwitchRepositoryCoreAsync(value.Id, value.Name);
    }

    [RelayCommand]
    private async Task SelectImportedRepositoryAsync(ImportedRepository? repository)
    {
        if (repository is null) return;
        var repoId = Services.Configuration.SourceConfigService.ComputeRepositoryId(repository.Url);
        await SwitchRepositoryCoreAsync(repoId, repository.Name);
    }

    private async Task SwitchRepositoryCoreAsync(string repositoryId, string displayName)
    {
        try
        {
            await _services.Config.SelectRepositoryAsync(repositoryId);
            SyncRepositories();
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
            StatusMessage = $"已切换到「{displayName}」";
        }
        catch (Exception ex) { StatusMessage = "切换仓库失败：" + ex.Message; }
    }

    private void SyncRepositories()
    {
        _syncingRepository = true;
        try
        {
            Repositories.Clear();
            foreach (var repo in _services.Config.Repositories) Repositories.Add(repo);
            SelectedRepository = Repositories.FirstOrDefault(r => string.Equals(r.Id, _services.Config.ActiveRepositoryId, StringComparison.OrdinalIgnoreCase))
                ?? Repositories.FirstOrDefault();
        }
        finally { _syncingRepository = false; }
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        var rawUrl = NewSourceUrl.Trim();
        var customName = NewSourceName.Trim();
        try
        {
            StatusMessage = "正在导入片源仓库…";
            await _services.Config.ImportAsync(rawUrl, string.IsNullOrWhiteSpace(customName) ? null : customName);
            NewSourceUrl = string.Empty;
            NewSourceName = string.Empty;
            await LoadImportsAsync();
            SyncRepositories();
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
            var repoName = _services.Config.ActiveRepository?.Name ?? "新仓库";
            StatusMessage = $"已导入并切换到「{repoName}」，正在检查匹配资源包…";

            var progress = new Progress<GitHubDownloadProgress>(p =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsDownloadingRepoPack = p.Percent < 100;
                    RepoPackDownloadProgress = p.Percent;
                    var route = string.IsNullOrWhiteSpace(p.RouteLabel) ? "国内加速" : p.RouteLabel;
                    RepoPackStatus = $"正在下载仓库资源包（{route} · {p.Percent:0}%）…";
                });
            });
            var auto = await _services.RepoPacks.AutoInstallForRepositoryAsync(rawUrl, _services.Config.Current, progress);
            IsDownloadingRepoPack = false;
            if (auto.Matched)
            {
                RepoPackStatus = auto.Message;
                StatusMessage = $"已导入「{repoName}」· {auto.Message}";
                await RefreshRepoPacksSafeAsync(forceRefresh: false);
            }
            else
            {
                StatusMessage = $"已导入并切换到「{repoName}」";
            }
        }
        catch (Exception ex)
        {
            IsDownloadingRepoPack = false;
            StatusMessage = ex.Message;
        }
    }

    public async Task RenameSourceAsync(ImportedRepository repository, string newName)
    {
        if (repository is null) return;
        try
        {
            await _services.Config.RenameImportAsync(repository.Url, newName);
            await LoadImportsAsync();
            SyncRepositories();
            await UpdateSourceSummaryAsync();
            StatusMessage = $"仓库已重命名为「{newName.Trim()}」";
        }
        catch (Exception ex) { StatusMessage = "重命名失败：" + ex.Message; }
    }

    [RelayCommand]
    private Task RefreshRepoPacksAsync() => RefreshRepoPacksSafeAsync(forceRefresh: true);

    private async Task RefreshRepoPacksSafeAsync(bool forceRefresh)
    {
        if (IsLoadingRepoPacks) return;
        IsLoadingRepoPacks = true;
        RepoPackStatus = "正在检查仓库资源包更新…";
        try
        {
            if (await _services.RepoPacks.GetLocalCatalogAsync() is { } local)
                SyncRepoPacksCollection(local.Manifest);
            else SyncInstalledPacksOnly();

            var catalog = await _services.RepoPacks.GetCatalogAsync(forceRefresh);
            SyncRepoPacksCollection(catalog.Manifest);
            var installedCount = _services.RepoPacks.InstalledPacks.Count;
            var sourceText = catalog.Source switch
            {
                RepoPackCatalogSource.Cached => "使用上次成功获取的清单",
                RepoPackCatalogSource.Bundled => "使用随客户端提供的清单",
                _ => "在线清单已更新"
            };
            RepoPackStatus = $"{sourceText} · {catalog.Manifest.Packs.Count} 个可用资源包 · 本机已启用 {installedCount} 个";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Diagnostics.Log($"[RepoPack] Catalog refresh: {ex.GetType().Name}");
            RepoPackStatus = "暂时无法更新在线清单。可以稍后刷新或导入本地 ZIP。";
        }
        finally { IsLoadingRepoPacks = false; }
    }

    public async Task InstallRepoPackAsync(RepoPackItemViewModel? item)
    {
        if (item?.Entry is null || IsDownloadingRepoPack) return;
        IsDownloadingRepoPack = true;
        RepoPackDownloadProgress = 0;
        RepoPackStatus = $"正在下载「{item.Name}」…";
        try
        {
            var progress = new Progress<GitHubDownloadProgress>(p =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    RepoPackDownloadProgress = p.Percent;
                    var speedText = p.BytesPerSecond > 1024 ? $" · {p.BytesPerSecond / 1024:0} KB/s" : string.Empty;
                    RepoPackStatus = $"正在下载「{item.Name}」（{p.RouteLabel ?? "国内加速"} · {p.Percent:0}%{speedText}）";
                });
            });
            var route = await _services.RepoPacks.InstallPackAsync(item.Entry, progress);
            RepoPackDownloadProgress = 100;
            RepoPackStatus = $"已安装并启用「{item.Name}」v{item.Entry.Version}（经 {route}，SHA-256 校验通过）";
            StatusMessage = RepoPackStatus;
            await RefreshRepoPacksSafeAsync(forceRefresh: false);
        }
        catch (Exception ex)
        {
            RepoPackStatus = $"下载「{item.Name}」失败：{ex.Message}";
            StatusMessage = RepoPackStatus;
        }
        finally { IsDownloadingRepoPack = false; }
    }

    public async Task ImportRepoPackAsync(string path)
    {
        if (IsDownloadingRepoPack) return;
        IsDownloadingRepoPack = true;
        RepoPackDownloadProgress = 0;
        try
        {
            var pack = await _services.RepoPacks.ImportPackAsync(path);
            var installed = new RepoPackManifest { Packs = RepoPacks.Where(item => item.Entry is not null).Select(item => item.Entry!).ToList() };
            SyncRepoPacksCollection(installed);
            RepoPackStatus = $"已从本地导入并启用「{pack.Name}」v{pack.Version}";
        }
        catch (Exception ex) { RepoPackStatus = "导入资源包失败。 " + ex.Message; }
        finally { IsDownloadingRepoPack = false; }
    }

    public async Task RemoveRepoPackAsync(RepoPackItemViewModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id)) return;
        try
        {
            await _services.RepoPacks.RemovePackAsync(item.Id);
            RepoPackStatus = $"已卸载资源包「{item.Name}」";
            StatusMessage = RepoPackStatus;
            await RefreshRepoPacksSafeAsync(forceRefresh: false);
        }
        catch (Exception ex) { RepoPackStatus = "卸载资源包失败：" + ex.Message; }
    }

    private void SyncRepoPacksCollection(RepoPackManifest manifest)
    {
        RepoPacks.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Packs)
        {
            seen.Add(entry.Id);
            var installed = _services.RepoPacks.TryGetInstalled(entry.Id, out var local) ? local : null;
            RepoPacks.Add(RepoPackItemViewModel.FromManifest(entry, installed));
        }
        foreach (var local in _services.RepoPacks.InstalledPacks)
        {
            if (seen.Add(local.Definition.Id))
                RepoPacks.Add(RepoPackItemViewModel.FromInstalledOnly(local));
        }
    }

    private void SyncInstalledPacksOnly()
    {
        RepoPacks.Clear();
        foreach (var local in _services.RepoPacks.InstalledPacks)
            RepoPacks.Add(RepoPackItemViewModel.FromInstalledOnly(local));
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(ImportedRepository? repository)
    {
        if (repository is null) return;
        try
        {
            await _services.Config.RemoveImportAsync(repository.Url);
            await LoadImportsAsync();
            SyncRepositories();
            ActiveSource = _services.Config.ActiveUrl;
            await UpdateSourceSummaryAsync();
            StatusMessage = "已删除";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task LoadImportsAsync()
    {
        await _services.Config.LoadAsync(false);
        var repositories = await _services.Config.GetImportsAsync();
        ImportedRepositories.Clear();
        foreach (var repository in repositories) ImportedRepositories.Add(repository);
        SyncRepositories();
    }

    private async Task UpdateSourceSummaryAsync()
    {
        var config = _services.Config.Current;
        SourceSummary = config is null || _services.Config.ActiveRepository is not { } active
            ? "导入仓库后可浏览点播和直播。"
            : $"当前：{active.Name}（{config.Sites.Count} 个点播站点 · {config.Lives.Count} 个直播源）· 共 {Repositories.Count} 个仓库";
        if (DateTimeOffset.TryParse(await _services.Database.GetSettingAsync("source.lastRefresh"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var refreshed))
            LastSourceRefresh = "最近刷新 " + refreshed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
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
        try
        {
            var account = await _services.Cloud.VerifyAccountAsync(provider);
            if (index >= 0 && index < CloudAccounts.Count && CloudAccounts[index].Provider == provider) CloudAccounts[index] = account;
            StatusMessage = $"{account.Name}：{account.Status}";
        }
        catch (Exception ex)
        {
            if (index >= 0 && index < CloudAccounts.Count) CloudAccounts[index] = CloudAccounts[index] with
            { State = CloudAccountState.Error, Status = "验证失败，请稍后重试" };
            StatusMessage = "网盘账号验证失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenCloudDrive() => App.Window.Navigate("cloud");

    [RelayCommand]
    private void ResetCloudAcceleration()
    {
        CloudAcceleration = true;
        CloudThreads = 3;
        CloudChunkMb = 2;
        StatusMessage = "已恢复安全加速默认值";
    }

    [RelayCommand]
    private async Task RefreshStorageAsync()
    {
        try
        {
            long images = 0;
            long database = 0;
            await Task.Run(() =>
            {
                if (Directory.Exists(AppPaths.Images))
                    images = Directory.EnumerateFiles(AppPaths.Images, "*.img").Sum(path => new FileInfo(path).Length);
                if (File.Exists(AppPaths.Database)) database = new FileInfo(AppPaths.Database).Length;
                var wal = AppPaths.Database + "-wal";
                if (File.Exists(wal)) database += new FileInfo(wal).Length;
            });
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
        try
        {
            if (!string.IsNullOrWhiteSpace(AliToken)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Ali, AliToken);
            if (!string.IsNullOrWhiteSpace(QuarkCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Quark, QuarkCookie);
            if (!string.IsNullOrWhiteSpace(UcCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.UC, UcCookie);
            if (!string.IsNullOrWhiteSpace(BaiduCookie)) await _services.Cloud.SaveCredentialAsync(CloudProviderKind.Baidu, BaiduCookie);
            _services.Cloud.ClearShareCache();
            AliToken = QuarkCookie = UcCookie = BaiduCookie = string.Empty;
            await RefreshCloudAccountsAsync();
            StatusMessage = "网盘凭据已加密保存，输入框已清空";
        }
        catch (Exception ex) { StatusMessage = "网盘凭据保存失败：" + ex.Message; }
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
    partial void OnPreventSleepChanged(bool value)
    {
        Save(PlaybackKeepAwakeService.SettingKey, value);
        _services.KeepAwake.Enabled = value;
    }
    partial void OnAutoNextChanged(bool value) => Save("playback.autoNext", value);
    partial void OnResumePlaybackChanged(bool value) => Save("playback.resume", value);
    partial void OnAutoRefreshSourcesChanged(bool value) => Save("source.autoRefresh", value);
    partial void OnAllowExternalExecutablesChanged(bool value) => Save(ExternalSpiderPolicy.SettingKey, value);
    partial void OnAutoCheckUpdatesChanged(bool value) => Save("updates.autoCheck", value);
    partial void OnNetworkCacheChanged(int value) => Save("playback.networkCache", Math.Clamp(value, 1200, 12000));
    partial void OnLiveCacheChanged(int value) => Save("playback.liveCache", Math.Clamp(value, 500, 8000));
    partial void OnAdaptiveQualityChanged(bool value) => Save("playback.adaptiveQuality", value);
    partial void OnCleanHlsChanged(bool value) => Save(HlsPlaylistCleaner.SettingKey, value);
    partial void OnDefaultVolumeChanged(int value) => Save("playback.volume", Math.Clamp(value, 0, 100));
    partial void OnDefaultQualityIndexChanged(int value) => Save("playback.defaultQuality", Math.Clamp(value, 0, 4));
    partial void OnAutoSwitchLineChanged(bool value) => Save("playback.autoSwitchLine", value);
    partial void OnSearchConcurrencyChanged(int value) => Save("search.concurrency", Math.Clamp(value, 1, 6));
    partial void OnCloudAccelerationChanged(bool value) => Save("playback.cloudAcceleration", value);
    partial void OnCloudThreadsChanged(int value) => Save("playback.cloudThreads", Math.Clamp(value, 1, 6));
    partial void OnCloudChunkMbChanged(int value) => Save("playback.cloudChunkMb", Math.Clamp(value, 1, 8));

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
    private async Task<int> Int(string key, int fallback) => int.TryParse(await _services.Database.GetSettingAsync(key),
        System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private void Save(string key, object value)
    {
        if (!_loaded) return;
        _pendingWrites[key] = value is bool flag ? (flag ? "true" : "false") : value is IFormattable formattable
            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : value.ToString()!;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    internal Task FlushAsync()
    {
        _saveTimer.Stop();
        return FlushPendingWritesAsync();
    }

    private async Task FlushPendingWritesAsync()
    {
        await _settingsWriteGate.WaitAsync();
        try
        {
            while (_pendingWrites.Count > 0)
            {
                var writes = _pendingWrites.ToArray();
                _pendingWrites.Clear();
                foreach (var (key, value) in writes)
                {
                    try { await _services.Database.SetSettingAsync(key, value); }
                    catch (Exception ex)
                    {
                        Diagnostics.Log($"设置保存失败 ({key}): {ex.GetType().Name}");
                        StatusMessage = $"设置保存失败：{ex.Message}";
                    }
                }
            }
        }
        catch (Exception ex) { Diagnostics.Log($"设置写入队列失败: {ex.GetType().Name}"); }
        finally { _settingsWriteGate.Release(); }
    }
}
