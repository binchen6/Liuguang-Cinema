using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TV.ViewModels;
using TV.Controls;
using TV.Services.Playback;

namespace TV.Views;

public sealed partial class SettingsPage : Page
{
    private readonly (ListViewItem Section, FrameworkElement Pane)[] _sections;
    private Flyout? _loginFlyout;

    public SettingsPage()
    {
        InitializeComponent();
        _sections =
        [
            (SourceSection, SourcePane), (PlaybackSection, PlaybackPane), (AiSection, AiPane), (CloudSection, CloudPane),
            (NetworkSection, NetworkPane), (AppearanceSection, AppearancePane), (StorageSection, StoragePane), (AboutSection, AboutPane)
        ];
        NavigationMotion.AttachVerticalList(SettingsSections, SettingsNavHost, SettingsNavIndicator);
        SettingsSections.SelectionChanged += OnSectionSelectionChanged;
        Unloaded += (_, _) => _loginFlyout?.Hide();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        MotionService.StaggerEntrance(offsetY: 12, stepMs: 35, SettingsHeader, SettingsBody);
        try
        {
            var version = typeof(SettingsPage).Assembly.GetName().Version;
            AppVersionText.Text = version is null ? "版本未知" : $"版本 {version.Major}.{version.Minor}.{version.Build}";
            SettingsSections.SelectedItem ??= SourceSection;
            await ViewModel.LoadAsync();
        }
        catch (Exception ex) { ViewModel.StatusMessage = "设置读取失败：" + ex.Message; }
    }

    public void SelectAiSection() => SettingsSections.SelectedItem = AiSection;

    private void OnSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsSections.SelectedItem is not ListViewItem selected) return;
        FrameworkElement? activePane = null;
        foreach (var (section, pane) in _sections)
        {
            var active = ReferenceEquals(section, selected);
            pane.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) activePane = pane;
        }
        if (activePane is null) return;
        SettingsScroller.ChangeView(null, 0, null, true);
        MotionService.EnterSection(activePane, offsetY: 12);
    }

    public string[] DanmakuOrderPresetLabels { get; } = [.. DanmakuSourceOrder.Presets.Select(preset => preset.Label)];

    private async void OnSelectImportedRepoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Models.ImportedRepository repository }) return;
        await ViewModel.SelectImportedRepositoryCommand.ExecuteAsync(repository);
    }

    private async void OnBackupSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Models.ImportedRepository repository } || XamlRoot is null) return;
        var input = new TextBox
        {
            Header = "备用地址",
            Text = repository.BackupUrl ?? string.Empty,
            PlaceholderText = "https://（留空清除备用地址）",
            InputScope = new Microsoft.UI.Xaml.Input.InputScope
            {
                Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Url } }
            }
        };
        input.SelectAll();
        var hint = new TextBlock
        {
            Text = "同一仓库的镜像或加速地址。主地址刷新失败时自动改用备用地址拉取并提示。",
            Style = (Style)Application.Current.Resources["MetaText"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"设置「{repository.Name}」备用地址",
            Content = new StackPanel { Spacing = 8, Children = { input, hint } },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SetBackupUrlAsync(repository, string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim());
    }

    private async void OnRenameSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Models.ImportedRepository repository } || XamlRoot is null) return;
        var input = new TextBox
        {
            Header = "仓库名称",
            Text = repository.Name,
            PlaceholderText = "输入自定义仓库名称",
            MaxLength = 60
        };
        input.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名仓库",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            await ViewModel.RenameSourceAsync(repository, input.Text);
    }

    private async void OnDownloadRepoPackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RepoPackItemViewModel item }) return;
        await ViewModel.InstallRepoPackAsync(item);
    }

    private async void OnImportRepoPackClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id);
        picker.FileTypeFilter.Add(".zip");
        try
        {
            if (await picker.PickSingleFileAsync() is { } file &&
                await ConfirmAsync("导入资源包", $"启用「{System.IO.Path.GetFileName(file.Path)}」中的规则。仅导入来源可信的文件。", "导入"))
                await ViewModel.ImportRepoPackAsync(file.Path);
        }
        catch (Exception ex) { ViewModel.RepoPackStatus = "无法选择资源包。 " + ex.Message; }
    }

    private async void OnRemoveRepoPackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RepoPackItemViewModel item }) return;
        if (await ConfirmAsync("卸载资源包", $"将删除本地安装的「{item.Name}」，不会影响已导入的片源仓库。", "卸载"))
            await ViewModel.RemoveRepoPackAsync(item);
    }

    private async void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Models.ImportedRepository repository }) return;
        if (await ConfirmAsync("删除仓库", "只会删除这个导入入口，不影响内置站点和播放记录。", "删除"))
            await ViewModel.RemoveSourceCommand.ExecuteAsync(repository);
    }

    private async void OnLogoutCloudClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Models.CloudProviderKind provider }) return;
        if (await ConfirmAsync("退出网盘", "将删除本机保存的登录凭据，不会删除网盘文件。", "退出登录"))
            await ViewModel.LogoutCloudCommand.ExecuteAsync(provider);
    }

    private void OnCloudQrClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Models.CloudProviderKind provider } anchor || _loginFlyout is not null) return;
        var model = ViewModel;
        var presenter = new CloudLoginPanel { DataContext = model.CloudLogin };
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, Content = presenter };
        presenter.CloseRequested += (_, _) => flyout.Hide();
        _loginFlyout = flyout;
        model.CloudLogin.Succeeded += OnCloudLoginSucceeded;
        flyout.Closed += (_, _) =>
        {
            model.CloudLogin.Succeeded -= OnCloudLoginSucceeded;
            _loginFlyout = null;
            model.CancelCloudLoginCommand.Execute(null);
        };
        model.StartCloudLoginCommand.Execute(provider.ToString());
        flyout.ShowAt(anchor);
    }

    private async void OnCloudLoginSucceeded(object? sender, Models.CloudProviderKind provider)
    {
        await ViewModel.RefreshCloudAccountsCommand.ExecuteAsync(null);
        _loginFlyout?.Hide();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = message,
            PrimaryButtonText = action, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnCleanSelectedClick(object sender, RoutedEventArgs e)
    {
        var risky = ViewModel.CleanupCategories.Where(category => category.Selected &&
            category.Id is "downloads" or "user-temp" or "recycle").Select(category => category.Name).ToArray();
        if (risky.Length > 0 && !await ConfirmAsync("确认高风险清理",
            $"以下项目会删除实际内容，且可能无法恢复：\n{string.Join("、", risky)}\n\n请确认已备份需要的文件。", "确认清理")) return;
        await ViewModel.CleanSelectedCommand.ExecuteAsync(null);
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("清空播放记录", "此操作会删除继续观看进度，且无法撤销。", "清空"))
        {
            await ViewModel.ClearHistoryCommand.ExecuteAsync(null);
            await ViewModel.RefreshStorageCommand.ExecuteAsync(null);
        }
    }

    private async void OnClearFavoritesClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("清空收藏", "此操作会删除所有收藏，且无法撤销。", "清空"))
        {
            await ViewModel.ClearFavoritesCommand.ExecuteAsync(null);
            await ViewModel.RefreshStorageCommand.ExecuteAsync(null);
        }
    }
}
