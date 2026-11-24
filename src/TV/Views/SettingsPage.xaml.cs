using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TV.ViewModels;
using TV.Controls;

namespace TV.Views;

public sealed partial class SettingsPage : Page
{
    private readonly List<(Expander Section, UIElement Pane)> _sections = new();
    private readonly Dictionary<Expander, ToggleButton> _headerToggleCache = [];
    private readonly NavigationRailController _sectionNav;
    private Expander? _activeSection;
    private bool _switchingSection;
    private Flyout? _loginFlyout;

    public SettingsPage()
    {
        InitializeComponent();
        _sectionNav = NavigationMotion.AttachVerticalCustom(SettingsNavHost, SettingsNavIndicator, () => _activeSection);
        WireSectionNavigation();
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
            if (_activeSection is null)
            {
                SourceSection.IsExpanded = true;
                if (_activeSection is null) ActivateSection(SourceSection);
            }
            await ViewModel.LoadAsync();
        }
        catch (Exception ex) { ViewModel.StatusMessage = "设置读取失败：" + ex.Message; }
    }

    /// <summary>左侧分区导航：每个分区是一个可展开项，展开即切换到右侧对应内容面板。</summary>
    private void WireSectionNavigation()
    {
        _sections.Add((SourceSection, SourcePane));
        _sections.Add((PlaybackSection, PlaybackPane));
        _sections.Add((AiSection, AiPane));
        _sections.Add((CloudSection, CloudPane));
        _sections.Add((NetworkSection, NetworkPane));
        _sections.Add((AppearanceSection, AppearancePane));
        _sections.Add((StorageSection, StoragePane));
        _sections.Add((AboutSection, AboutPane));
        foreach (var (section, _) in _sections)
        {
            section.Expanding += (sender, _) => ActivateSection((Expander)sender);
            section.Collapsed += (sender, _) => OnSectionCollapsed((Expander)sender);
        }
    }

    public void SelectAiSection()
    {
        AiSection.IsExpanded = true;
        ActivateSection(AiSection);
    }

    private void ActivateSection(Expander section)
    {
        if (_switchingSection) return;
        var changed = !ReferenceEquals(_activeSection, section);
        _activeSection = section;
        _switchingSection = true;
        try
        {
            FrameworkElement? activePane = null;
            foreach (var (candidate, pane) in _sections)
            {
                var active = ReferenceEquals(candidate, section);
                if (!active && candidate.IsExpanded) candidate.IsExpanded = false;
                pane.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                if (active) activePane = pane as FrameworkElement;
                ResetSectionVisual(candidate, active);
            }
            if (changed && activePane is not null)
            {
                SettingsScroller.ChangeView(null, 0, null, true);
                MotionService.EnterSection(activePane, offsetY: 12);
            }
            _sectionNav.Sync(animate: true);
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var (candidate, _) in _sections) ResetSectionVisual(candidate, ReferenceEquals(candidate, _activeSection));
            });
        }
        finally { _switchingSection = false; }
    }

    private void ResetSectionVisual(Expander section, bool active)
    {
        if (!_headerToggleCache.TryGetValue(section, out var toggle))
        {
            if (FindTemplateChild(section, "HeaderToggle") is not ToggleButton found) return;
            _headerToggleCache[section] = toggle = found;
        }
        VisualStateManager.GoToState(toggle, active ? "Checked" : "Normal", false);
    }

    private static FrameworkElement? FindTemplateChild(DependencyObject root, string name)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindTemplateChild(child, name) is { } match) return match;
        }
        return null;
    }

    /// <summary>点击当前分区会折叠；为保持导航语义，折叠当前分区时立即重新展开。</summary>
    private void OnSectionCollapsed(Expander section)
    {
        if (_switchingSection || !ReferenceEquals(section, _activeSection)) return;
        DispatcherQueue.TryEnqueue(() => section.IsExpanded = true);
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

    private void OnCloseCloudQrClick(object sender, RoutedEventArgs e) => _loginFlyout?.Hide();
}
