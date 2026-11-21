using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using TV.Services;
using TV.Controls;
using TV.Models;
using TV.ViewModels;
using TV.Views;
using TV.Services.Library;

namespace TV;

public sealed partial class MainWindow : Window
{
    private string _currentTag = string.Empty;
    private bool _fullScreen;
    private bool _compact;
    private bool _leavingPlayer;
    private Task? _leavePlayerTask;
    private readonly WindowTray _tray;
    private WindowCloseBehavior _closeBehavior;
    private bool _titleInputQueued;
    private RectInt32[]? _titleInputRegions;
    private sealed record NavigationEntry(string Tag, VodCardViewModel? Detail = null, PlaybackRequest? Playback = null);
    private readonly Stack<NavigationEntry> _back = new();

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        RestorePlacement();
        AppWindow.Changed += (_, _) => CapturePlacement();
        Title = "流光影院";
        Diagnostics.Log("MainWindow initialized");
        if (!App.PlayerOnly) App.Services.Sniffer.Attach(ShellRoot);
        ViewModel = new MainViewModel(App.Services, DispatcherQueue);
        Diagnostics.Log("ViewModel initialized");
        ShellRoot.DataContext = ViewModel;
        ContentFrame.Navigated += (_, args) =>
        {
            if (args.Content is FrameworkElement page)
                page.DataContext = _currentTag switch
                {
                    "live" => ViewModel.Live, "library" => ViewModel.Library, "settings" => ViewModel.Settings,
                    "player" => ViewModel.Player, "search" => ViewModel.Search, "cloud" => ViewModel.CloudDrive, _ => ViewModel.Vod
                };
        };
        ViewModel.NavigationRequested += OnNavigationRequested;
        ViewModel.Player.FullScreenRequested += OnFullScreenRequested;
        ViewModel.Player.CompactOverlayRequested += OnCompactOverlayRequested;
        ShellRoot.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        ShellRoot.SizeChanged += (_, _) => UpdatePaneLayout();
        var backShortcut = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Left,
            Modifiers = Windows.System.VirtualKeyModifiers.Menu
        };
        backShortcut.Invoked += (_, args) => { GoBack(); args.Handled = true; };
        ShellRoot.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        ShellRoot.KeyboardAccelerators.Add(backShortcut);
        var searchShortcut = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        searchShortcut.Invoked += (_, args) => { OpenSearch(); args.Handled = true; };
        ShellRoot.KeyboardAccelerators.Add(searchShortcut);
        _tray = new WindowTray(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, Title, Activate, () => _ = ShutdownAsync());
        AppWindow.Closing += OnClosing;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closing) return;
            if (App.PlayerOnly && App.StartupVideoPath is { } path)
            {
                Navigate("player");
                try { _ = ViewModel.Player.LoadAsync(LocalMediaService.CreateRequest(path)); }
                catch (Exception ex) { ViewModel.Player.StatusMessage = ex.Message; }
            }
            else Navigate("vod");
            _ = LoadPreferencesAsync();
        });
    }

    public void SetTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root) root.RequestedTheme = theme;
    }

    private async Task LoadPreferencesAsync()
    {
        try
        {
            var settings = App.Services.Database;
            var theme = await settings.GetSettingAsync("ui.theme");
            if (_closing) return;
            SetTheme(theme switch { "1" => ElementTheme.Light, "2" => ElementTheme.Default, _ => ElementTheme.Dark });
            MotionService.Enabled = await settings.GetSettingAsync("ui.animations") != "false";
            _tray.Enabled = await settings.GetSettingAsync("ui.minimizeToTray") != "false";
            if (int.TryParse(await settings.GetSettingAsync("ui.closeBehavior"), out var closeBehavior) &&
                Enum.IsDefined((WindowCloseBehavior)closeBehavior))
                _closeBehavior = (WindowCloseBehavior)closeBehavior;
        }
        catch (Exception ex) { Diagnostics.Log($"窗口设置读取失败: {ex.Message}"); }
    }

    internal void SetMinimizeToTray(bool enabled) => _tray.Enabled = enabled;
    internal void SetCloseBehavior(WindowCloseBehavior behavior) => _closeBehavior = behavior;
    internal void RestoreWindow() { if (!_closing) _tray.Restore(); }

    public MainViewModel ViewModel { get; }

    public void Navigate(string tag)
    {
        if (App.PlayerOnly && tag != "player") return;
        if (_currentTag == tag || _leavingPlayer || _closing) return;
        _ = NavigateAsync(tag, false);
    }

    public void GoBack()
    {
        if (App.PlayerOnly) { _ = ShutdownAsync(); return; }
        if (_leavingPlayer || _closing) return;
        if (_fullScreen || _compact)
        {
            SetPlayerPresenter(AppWindowPresenterKind.Overlapped);
            return;
        }
        if (_back.TryPop(out var previous)) _ = NavigateAsync(previous.Tag, true, previous);
    }

    private async Task NavigateAsync(string tag, bool goingBack, NavigationEntry? restoring = null)
    {
        var previous = _currentTag;
        var resume = previous == "player" && tag == "search" ? ViewModel.Player.GetResumeRequest() : null;
        if (previous == "player")
        {
            _leavingPlayer = true;
            try { await (_leavePlayerTask = ViewModel.Player.PrepareLeaveAsync()); }
            finally { _leavingPlayer = false; }
            if (_closing) return;
            SetPlayerPresenter(AppWindowPresenterKind.Overlapped);
        }
        if (previous == "search" && tag != "detail") ViewModel.Search.CancelPending();
        if (!goingBack && previous.Length > 0)
        {
            if (tag is "player" or "detail" or "search")
                _back.Push(new NavigationEntry(previous, previous == "detail" ? ViewModel.Vod.SelectedCard : null, resume));
            else
            {
                while (previous is "player" or "detail" or "search" && _back.TryPop(out var parent))
                {
                    if (previous == "search") ViewModel.Search.CancelPending();
                    previous = parent.Tag;
                }
                if (previous != tag && previous is not ("player" or "detail" or "search")) _back.Push(new NavigationEntry(previous));
                ViewModel.Vod.SelectedCard = null;
            }
        }
        if (_currentTag == "detail" && tag != "player") ViewModel.Vod.SelectedCard = null;
        NavigateCore(tag, goingBack);
        if (tag == "detail" && restoring?.Detail is { } detail) ViewModel.Vod.SelectedCard = detail;
        if (tag == "player" && restoring?.Playback is { } playback) _ = ViewModel.Player.LoadAsync(playback);
    }

    private void NavigateCore(string tag, bool goingBack)
    {
        Diagnostics.Log($"NavigateCore tag={tag} prev={_currentTag}");
        _currentTag = tag;
        Type? pageType = tag switch
        {
            "live" => typeof(LivePage),
            "cast" => typeof(CastPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            "cloud" => typeof(CloudDrivePage),
            "player" => typeof(PlayerPage),
            "detail" => typeof(DetailPage),
            "search" => typeof(SearchPage),
            _ => typeof(VodPage)
        };
        NavigationTransitionInfo transition = MotionService.CanAnimate
            ? new SlideNavigationTransitionInfo { Effect = goingBack ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight }
            : new SuppressNavigationTransitionInfo();
        try
        {
            ContentFrame.Navigate(pageType, null, transition);
            UpdatePaneLayout();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"页面导航失败 ({tag}): {ex}");
        }
        ContentFrame.BackStack.Clear();
        SyncNavSelection(tag);
        UpdateBackButton();
    }

    private bool _syncingNav;
    private static readonly string[] PrimaryTags = { "vod", "live", "library", "cast" };

    private void SyncNavSelection(string tag)
    {
        SettingsButton.Background = tag == "settings" ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SelectionBrush"] : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        if (tag == "settings")
        {
            _syncingNav = true;
            try { NavList.SelectedItem = null; }
            finally { _syncingNav = false; }
            UpdateNavVisuals(tag);
            return;
        }
        if (!PrimaryTags.Contains(tag)) return;
        _syncingNav = true;
        try
        {
            NavList.SelectedItem = NavList.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag as string == tag);
        }
        finally { _syncingNav = false; }
        UpdateNavVisuals(tag);
    }

    // ListViewItem 的 SelectionStates 视觉态在切换选中项时不会回退（deselected 项保留选中底色/指示条），
    // 这里直接按当前 tag 校正每项的选中底板与指示条，避免出现两个高亮项。
    private void UpdateNavVisuals(string tag)
    {
        foreach (var item in NavList.Items.OfType<ListViewItem>())
        {
            var active = item.Tag as string == tag;
            if (FindTemplateChild(item, "SelectionPlate") is UIElement plate) plate.Opacity = active ? 1 : 0;
            if (FindTemplateChild(item, "Indicator") is UIElement indicator) indicator.Opacity = active ? 1 : 0;
        }
    }

    private static FrameworkElement? FindTemplateChild(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindTemplateChild(child, name) is { } found) return found;
        }
        return null;
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav) return;
        if (NavList.SelectedItem is ListViewItem { Tag: string tag })
        {
            Diagnostics.Log($"Nav selection changed tag={tag}");
            ViewModel.NavigateCommand.Execute(tag);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ViewModel.NavigateCommand.Execute("settings");

    private bool _narrowPane;
    private void UpdatePaneLayout()
    {
        if (ShellRoot.XamlRoot is null) return;
        var hidePane = _currentTag == "player" || _fullScreen || _compact;
        NavPane.Visibility = hidePane ? Visibility.Collapsed : Visibility.Visible;
        if (hidePane)
        {
            PaneColumn.Width = new GridLength(0);
            return;
        }
        var narrow = ShellRoot.ActualWidth < 920;
        _narrowPane = narrow;
        if (narrow)
        {
            PaneColumn.Width = new GridLength(76);
        }
        else
        {
            var labels = new FrameworkElement[] { BrandText, NavLabelVod, NavLabelLive, NavLabelLibrary, NavLabelCast, NavLabelSettings };
            var available = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            foreach (var label in labels)
            {
                label.Visibility = Visibility.Visible;
                label.Measure(available);
            }
            var textWidth = labels.Max(label => label.DesiredSize.Width);
            var brandWidth = BrandText.DesiredSize.Width + 56;
            var navigationWidth = textWidth + 92;
            PaneColumn.Width = new GridLength(Math.Clamp(Math.Max(brandWidth, navigationWidth), 196, 320));
        }
        var labelVisibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in new FrameworkElement[] { BrandText, NavLabelVod, NavLabelLive, NavLabelLibrary, NavLabelCast, NavLabelSettings })
            label.Visibility = labelVisibility;
        var alignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var content in new FrameworkElement[] { NavContentVod, NavContentLive, NavContentLibrary, NavContentCast, NavContentSettings })
            content.HorizontalAlignment = alignment;
        SettingsButton.HorizontalContentAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        BrandBlock.Margin = narrow ? new Thickness(0, 0, 0, 0) : new Thickness(20, 0, 0, 0);
        BrandBlock.HorizontalAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    internal void OpenLocal(string path)
    {
        try
        {
            var request = LocalMediaService.CreateRequest(path);
            if (_currentTag != "player") Navigate("player");
            _ = ViewModel.Player.LoadAsync(request);
            RestoreWindow();
        }
        catch (Exception ex) { Diagnostics.Log($"打开本地视频失败: {ex.GetType().Name} {ex.Message}"); }
    }

    internal void OpenSearch()
    {
        if (App.PlayerOnly) return;
        if (_currentTag == "search" && ContentFrame.Content is SearchPage page)
        {
            page.FocusQuery();
        }
        else if (_back.TryPeek(out var previous) && previous.Tag == "search") GoBack();
        else Navigate("search");
    }

    internal bool IsPlayerFullScreen => _fullScreen;

    private void UpdateBackButton()
    {
        // 播放器页有自己的顶部返回按钮，避免出现两个返回键。
        var canGoBack = _currentTag is not ("vod" or "live" or "library" or "player") && _back.Count > 0;
        BackButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Opacity = 1;
        BackButton.IsHitTestVisible = canGoBack;
        ScheduleTitleBarInput();
    }

    private void OnTitleBarLoaded(object sender, RoutedEventArgs e)
    {
        AppTitleBar.XamlRoot.Changed += (_, _) => ScheduleTitleBarInput();
        UpdateCaptionTheme();
        ScheduleTitleBarInput();
    }

    private void UpdateCaptionTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        Windows.UI.Color? foreground = null;
        Windows.UI.Color? inactiveForeground = null;
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            // 与 Styles/Tokens.xaml 的 PrimaryTextBrush / SecondaryTextBrush 保持一致。
            if (ShellRoot.ActualTheme == ElementTheme.Light)
            {
                foreground = Windows.UI.Color.FromArgb(255, 0x17, 0x19, 0x1E);
                inactiveForeground = Windows.UI.Color.FromArgb(255, 0x56, 0x5E, 0x6B);
            }
            else
            {
                foreground = Windows.UI.Color.FromArgb(255, 0xF3, 0xF0, 0xE9);
                inactiveForeground = Windows.UI.Color.FromArgb(255, 0xA6, 0xAE, 0xBC);
            }
        }
        var caption = AppWindow.TitleBar;
        caption.ButtonForegroundColor = foreground;
        caption.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleTitleBarInput();
    private void OnTitleControlsLayoutUpdated(object? sender, object e) => ScheduleTitleBarInput();

    private void ScheduleTitleBarInput()
    {
        if (_titleInputQueued) return;
        _titleInputQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _titleInputQueued = false;
            if (_closing || AppTitleBar.XamlRoot is not { } root) return;
            var rects = new List<RectInt32>();
            if (AppTitleBar.Visibility == Visibility.Visible)
            {
                var scale = root.RasterizationScale;
                foreach (var button in new Control[] { BackButton }.Where(button => button.Visibility == Visibility.Visible && button.IsHitTestVisible))
                {
                    var bounds = button.TransformToVisual(null).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    rects.Add(new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                        (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
                }
            }
            var regions = rects.ToArray();
            if (_titleInputRegions is not null && _titleInputRegions.SequenceEqual(regions)) return;
            try
            {
                InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, regions);
                _titleInputRegions = regions;
            }
            catch (System.Runtime.InteropServices.COMException ex) { Diagnostics.Log($"标题栏输入区域不可用 (0x{ex.HResult:X8})"); }
        });
    }

    private void OnNavigationRequested(object? sender, string tag) => Navigate(tag);

    private void OnFullScreenRequested(object? sender, EventArgs e) =>
        SetPlayerPresenter(_fullScreen ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);

    private void OnCompactOverlayRequested(object? sender, EventArgs e) =>
        SetPlayerPresenter(_compact ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.CompactOverlay);

    private void SetPlayerPresenter(AppWindowPresenterKind kind)
    {
        if (AppWindow.Presenter.Kind != kind) AppWindow.SetPresenter(kind);
        _fullScreen = kind == AppWindowPresenterKind.FullScreen;
        _compact = kind == AppWindowPresenterKind.CompactOverlay;
        AppTitleBar.Visibility = _fullScreen || _compact ? Visibility.Collapsed : Visibility.Visible;
        UpdatePaneLayout();
    }

    private bool _closing;
    private bool _allowClose;
    private bool _closePromptOpen;
    private Flyout? _closeFlyout;

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (App.PlayerOnly)
        {
            if (!_closing) _ = ShutdownAsync();
            return;
        }
        if (_tray.IsEndingSession) _ = ShutdownAsync();
        else if (!_closing && !_closePromptOpen) _ = ApplyCloseChoiceAsync();
    }

    private async Task ApplyCloseChoiceAsync()
    {
        _closePromptOpen = true;
        try
        {
            var behavior = _closeBehavior;
            if (behavior == WindowCloseBehavior.Ask)
            {
                var (choice, remember) = await ShowCloseChoiceAsync();
                if (choice is null || _closing) return;
                behavior = choice.Value;
                if (remember) await ViewModel.Settings.SaveCloseBehaviorAsync(behavior);
            }
            if (behavior == WindowCloseBehavior.MinimizeToTray)
            {
                if (!_tray.TryMinimizeFromClose() && AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Minimize();
            }
            else await ShutdownAsync();
        }
        catch (Exception ex) { Diagnostics.Log($"关闭窗口操作失败: {ex.Message}"); }
        finally { _closePromptOpen = false; }
    }

    private async Task<(WindowCloseBehavior? Choice, bool Remember)> ShowCloseChoiceAsync()
    {
        var result = new TaskCompletionSource<WindowCloseBehavior?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remember = new CheckBox { Content = "记住此选择" };
        var tray = new Button { Content = "最小化到托盘" };
        var exit = new Button { Content = "退出" };
        var cancel = new Button { Content = "取消" };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { tray, exit, cancel }
        };
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel { Spacing = 12, Children = { remember, actions } } };
        _closeFlyout = flyout;
        tray.Click += (_, _) => { result.TrySetResult(WindowCloseBehavior.MinimizeToTray); flyout.Hide(); };
        exit.Click += (_, _) => { result.TrySetResult(WindowCloseBehavior.Exit); flyout.Hide(); };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) => result.TrySetResult(null);
        flyout.ShowAt(ClosePromptAnchor);
        try { return (await result.Task, remember.IsChecked == true); }
        finally { _closeFlyout = null; }
    }

    private async Task ShutdownAsync()
    {
        if (_closing) return;
        CapturePlacement();
        SavePlacement();
        _closing = true;
        _closeFlyout?.Hide();
        Diagnostics.Log("Shutdown requested");
        _tray.Dispose();
        try
        {
            if (!App.PlayerOnly)
            {
                ViewModel.Search.CancelPending();
                if (ViewModel.Search.SubmitCommand.ExecutionTask is { IsCompleted: false } pendingSearch)
                    await pendingSearch.WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                if (ContentFrame.Content is SearchPage search) search.CloseTransientUI(true);
                var download = ViewModel.Vod.CancelPendingWork();
                ViewModel.Settings.CancelCloudLoginCommand.Execute(null);
                if (_currentTag == "player")
                    await (_leavingPlayer && _leavePlayerTask is not null ? _leavePlayerTask : ViewModel.Player.PrepareLeaveAsync());
                if (ContentFrame.Content is CastPage cast) await cast.StopAsync();
                if (download is { IsCompleted: false })
                    await download.WaitAsync(TimeSpan.FromMilliseconds(750));
            }
            else if (_currentTag == "player")
                await (_leavingPlayer && _leavePlayerTask is not null ? _leavePlayerTask : ViewModel.Player.PrepareLeaveAsync());
        }
        catch (Exception ex) { Diagnostics.Log($"结束当前任务失败: {ex.Message}"); }
        try
        {
            if (!App.PlayerOnly) App.Services.Sniffer.Detach();
            await Task.Run(App.Services.Dispose);
        }
        catch (Exception ex) { Diagnostics.Log($"释放应用资源失败: {ex.Message}"); }
        finally
        {
            Diagnostics.Log("Shutdown completed");
            ContentFrame.Content = null;
            _allowClose = true;
            Close();
        }
    }
}
