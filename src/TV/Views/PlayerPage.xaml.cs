using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using VirtualKey = Windows.System.VirtualKey;
using TV.ViewModels;
using TV.Models;
using TV.Controls;
using TV.Services;
using TV.Services.Playback;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace TV.Views;

public sealed partial class PlayerPage : Page
{
    private static readonly double[] Rates = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _danmakuTimer;
    private bool _controlsVisible = true;
    private PlayerViewModel? _vm;
    private CancellationTokenSource? _danmakuLoad;
    private IReadOnlyList<DanmakuEntry> _comments = [];
    private readonly List<(TextBlock Text, double Start, int Lane)> _visibleComments = [];
    private double _lastCommentTime = -1;
    private int _nextComment;
    private DanmakuPreferences _danmaku = new();
    private Uri? _danmakuAddress;
    private Flyout? _danmakuFlyout;
    private ContentDialog? _cloudLoginDialog;
    private BilibiliLoginDialog? _bilibiliLoginDialog;
    private ContentDialog? _sourceDialog;
    private ContentDialog? _onlineSubtitleDialog;
    private Flyout? _timingFlyout;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint cursorName);
    private static void ShowArrowCursor() => SetCursor(LoadCursor(0, 32512));

    private sealed record DanmakuPreferences(bool Enabled = false, int Size = 20, int Color = 0,
        bool BasicFilter = true, string BlockedWords = "", string Source = "origin", bool AutoMatch = true);
    private static readonly string[] BasicDanmakuBlockedWords = ["加群", "代充", "博彩", "刷单", "加微信"];

    private PlayerViewModel ViewModel => _vm ??= App.Window.ViewModel.Player;

    public PlayerPage()
    {
        InitializeComponent();
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += OnHideTimer;
        _danmakuTimer = DispatcherQueue.CreateTimer();
        _danmakuTimer.Interval = TimeSpan.FromMilliseconds(33);
        _danmakuTimer.Tick += (_, _) => UpdateDanmaku();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged; // 页面被缓存会重复 Loaded，防止重复订阅
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.DanmakuChanged -= OnDanmakuChanged;
        ViewModel.DanmakuChanged += OnDanmakuChanged;
        ViewModel.SourceRecoveryOffered -= OnSourceRecoveryOffered;
        ViewModel.SourceRecoveryOffered += OnSourceRecoveryOffered;
        Diagnostics.Log($"Player page loaded, output ready={ViewModel.Service.IsVideoOutputReady}");
        if (RateFlyout.Items.Count == 0) BuildFlyouts();
        if (ViewModel.Service.IsVideoOutputReady)
        {
            VideoView.MediaPlayer = ViewModel.Service.MediaPlayer;
            ViewModel.NotifyVideoReady();
        }
        VideoView.Initialized -= OnVideoViewInitialized;
        VideoView.Initialized += OnVideoViewInitialized;
        Focus(FocusState.Programmatic);
        _ = RefreshDanmakuToggleAsync();
        ShowControls();
    }

    private async void OnVideoViewInitialized(object? sender, LibVLCSharp.Platforms.Windows.InitializedEventArgs e)
    {
        Diagnostics.Log("VideoView initialization callback");
        try
        {
            VideoView.MediaPlayer = null;
            await ViewModel.Service.EnsureInitializedAsync(e.SwapChainOptions);
            if (!IsLoaded) return;
            VideoView.MediaPlayer = ViewModel.Service.MediaPlayer;
            ViewModel.Service.SetVolume(ViewModel.Volume);
            ViewModel.NotifyVideoReady();
            Diagnostics.Log("VideoView output attached");
        }
        catch (Exception ex) { Diagnostics.Log("VideoView initialization failed: " + ex); ViewModel.StatusMessage = "播放器初始化失败：" + ex.Message; }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _bilibiliLoginDialog?.Hide();
        if (_cloudLoginDialog is not null)
        {
            App.Services.CloudLogin.Succeeded -= OnCloudLoginSucceeded;
            App.Services.CloudLogin.Close();
            _cloudLoginDialog.Hide();
            _cloudLoginDialog = null;
        }
        PlaylistFlyout.Hide();
        _danmakuFlyout?.Hide();
        _hideTimer.Stop();
        _danmakuTimer.Stop();
        CancelDanmakuLoad();
        _danmakuAddress = null;
        ClearDanmaku();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.DanmakuChanged -= OnDanmakuChanged;
        ViewModel.SourceRecoveryOffered -= OnSourceRecoveryOffered;
        _sourceDialog?.Hide();
        _sourceDialog = null;
        _onlineSubtitleDialog?.Hide();
        _onlineSubtitleDialog = null;
        ShowArrowCursor();
        VideoView.Initialized -= OnVideoViewInitialized;
        VideoView.MediaPlayer = null;
    }

    private void BuildFlyouts()
    {
        foreach (var rate in Rates)
        {
            var item = new MenuFlyoutItem { Text = $"{rate:0.##}x", Tag = rate };
            item.Click += (_, _) => ViewModel.Rate = rate;
            RateFlyout.Items.Add(item);
        }
    }

    private void OnQualityMenuOpening(object? sender, object args)
    {
        _hideTimer.Stop();
        QualityFlyout.Items.Clear();
        foreach (var quality in ViewModel.QualityOptions)
        {
            var item = new ToggleMenuFlyoutItem { Text = quality.Name, Tag = quality, IsChecked = quality == ViewModel.SelectedQuality,
                IsEnabled = ViewModel.CanSelectQuality(quality) };
            item.Click += async (_, _) => await ViewModel.ChangeQualityAsync(quality);
            QualityFlyout.Items.Add(item);
        }
        if (ViewModel.IsBilibiliPlayback)
        {
            QualityFlyout.Items.Add(new MenuFlyoutSeparator());
            var account = new MenuFlyoutItem { Text = "B 站账号" };
            account.Click += async (_, _) => await ShowBilibiliLoginAsync();
            QualityFlyout.Items.Add(account);
        }
    }

    private async Task ShowBilibiliLoginAsync()
    {
        if (XamlRoot is null || _bilibiliLoginDialog is not null) return;
        _hideTimer.Stop();
        using var dialog = new BilibiliLoginDialog(App.Services.BilibiliAccount, XamlRoot);
        _bilibiliLoginDialog = dialog;
        try
        {
            await dialog.ShowAsync();
            if (dialog.AccountChanged && IsLoaded) await ViewModel.RefreshBilibiliQualityAsync();
        }
        catch (Exception) { ViewModel.QualityMessage = "B 站账号面板无法打开，请重试。"; }
        finally
        {
            _bilibiliLoginDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.RequiredLoginProvider) && ViewModel.RequiredLoginProvider is not null && IsLoaded)
            _ = ShowCloudLoginAsync();
        if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.IsBuffering)
            or nameof(PlayerViewModel.HasMedia) or nameof(PlayerViewModel.ShowStatusPanel))
        {
            UpdateCenterPlayVisibility();
            if (e.PropertyName == nameof(PlayerViewModel.ShowStatusPanel) && ViewModel.ShowStatusPanel && IsLoaded)
                ShowControls();
        }
    }

    private void OnPlaylistOpening(object? sender, object args)
    {
        _hideTimer.Stop();
        PlaylistEpisodes.MaxHeight = Math.Clamp(ActualHeight - 175, 100, 400);
        if (ViewModel.SelectedEpisode is { } episode) PlaylistEpisodes.ScrollIntoView(episode);
    }

    private void OnPlaylistClosed(object? sender, object args)
    {
        if (IsLoaded) ShowControls();
    }

    private void OnPlaybackOptionsOpening(object? sender, object e)
    {
        _hideTimer.Stop();
        var player = ViewModel.Service.LastSnapshot;
        BuildTracks(AudioTracks, ViewModel.AudioTracks, player.AudioTrack, id => _ = ViewModel.SelectTrackAsync(false, id));
        var selectedCloud = ViewModel.SelectedCloudSubtitle;
        var cloudTracks = ViewModel.CloudSubtitleTracks;
        BuildTracks(SubtitleTracks, ViewModel.SubtitleTracks, player.SubtitleTrack, id => _ = ViewModel.SelectTrackAsync(true, id),
            selectedCloud is not null, cloudTracks.Count > 0);
        if (cloudTracks.Count == 0) return;
        SubtitleTracks.Items.Add(new MenuFlyoutSeparator());
        foreach (var track in cloudTracks)
        {
            var item = new ToggleMenuFlyoutItem { Text = track.Name, IsChecked = track == selectedCloud };
            item.Click += async (_, _) => await ViewModel.SelectCloudSubtitleAsync(track);
            SubtitleTracks.Items.Add(item);
        }
    }

    private static void BuildTracks(MenuFlyoutSubItem menu, IEnumerable<PlaybackTrack> tracks, int selected, Action<int> select,
        bool externalSelected = false, bool hasExternalTracks = false)
    {
        menu.Items.Clear();
        var available = tracks.Where(track => track.Id >= 0).ToArray();
        var off = new ToggleMenuFlyoutItem { Text = "关闭", IsChecked = selected < 0 && !externalSelected, IsEnabled = available.Length > 0 || hasExternalTracks };
        off.Click += (_, _) => select(-1);
        menu.Items.Add(off);
        for (var index = 0; index < available.Length; index++)
        {
            var track = available[index];
            var item = new ToggleMenuFlyoutItem { Text = string.IsNullOrWhiteSpace(track.Name) ? $"轨道 {index + 1}" : track.Name, IsChecked = track.Id == selected && !externalSelected };
            item.Click += (_, _) => select(track.Id);
            menu.Items.Add(item);
        }
    }

    private static void BuildTracks(MenuFlyout menu, IEnumerable<PlaybackTrack> tracks, int selected, Action<int> select,
        bool externalSelected = false, bool hasExternalTracks = false)
    {
        menu.Items.Clear();
        var available = tracks.Where(track => track.Id >= 0).ToArray();
        var off = new ToggleMenuFlyoutItem { Text = "关闭", IsChecked = selected < 0 && !externalSelected, IsEnabled = available.Length > 0 || hasExternalTracks };
        off.Click += (_, _) => select(-1);
        menu.Items.Add(off);
        for (var index = 0; index < available.Length; index++)
        {
            var track = available[index];
            var item = new ToggleMenuFlyoutItem { Text = string.IsNullOrWhiteSpace(track.Name) ? $"轨道 {index + 1}" : track.Name, IsChecked = track.Id == selected && !externalSelected };
            item.Click += (_, _) => select(track.Id);
            menu.Items.Add(item);
        }
    }

    private async void OnLoadSubtitleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id);
            foreach (var extension in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !await ViewModel.AddSubtitleAsync(new Uri(file.Path)))
                ViewModel.QualityMessage = "无法加载字幕。";
        }
        catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
    }

    private async void OnMatchSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_onlineSubtitleDialog is not null || XamlRoot is null) return;
        _hideTimer.Stop();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var tracks = await ViewModel.GetOnlineSubtitlesAsync(timeout.Token);
            if (!IsLoaded || XamlRoot is null) return;
            if (tracks.Count == 0) { ViewModel.QualityMessage = "匹配节目没有提供在线字幕，可使用本地字幕。"; return; }
            var choices = new ComboBox { ItemsSource = tracks, DisplayMemberPath = "Name", SelectedIndex = 0,
                Header = "字幕语言", HorizontalAlignment = HorizontalAlignment.Stretch };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "在线字幕", Content = choices,
                PrimaryButtonText = "加载字幕", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            _onlineSubtitleDialog = dialog;
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is OnlineSubtitleTrack track)
                    await ViewModel.SelectOnlineSubtitleAsync(track);
            }
            finally { _onlineSubtitleDialog = null; }
        }
        catch (OperationCanceledException) { ViewModel.QualityMessage = "在线字幕匹配超时，请重试。"; }
        catch (Exception ex) { ViewModel.QualityMessage = "在线字幕匹配失败：" + ex.Message; }
        finally { if (IsLoaded) ShowControls(); }
    }

    private async void OnDanmakuSettingsClick(object sender, RoutedEventArgs e)
    {
        _hideTimer.Stop();
        try
        {
            var item = (MenuFlyoutItem)sender;
            var anchor = item.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(item.ActualWidth / 2, item.ActualHeight / 2));
            await ReadDanmakuPreferencesAsync();
            if (!IsLoaded) return;
            var enabled = new ToggleSwitch { Header = "显示弹幕", IsOn = _danmaku.Enabled };
            var source = new AnchoredChoice { Header = "弹幕来源", Options = "来源自带 / 本地文件|哔哩哔哩在线匹配",
                SelectedIndex = _danmaku.Source == "bilibili" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var autoMatch = new ToggleSwitch { Header = "换集后自动匹配对应集数", IsOn = _danmaku.AutoMatch };
            var size = new AnchoredChoice { Header = "字号", ItemsSource = new[] { 16, 20, 24, 28 }, SelectedItem = _danmaku.Size, HorizontalAlignment = HorizontalAlignment.Stretch };
            var color = new AnchoredChoice { Header = "颜色", Options = "跟随来源|白色|金色|蓝色", SelectedIndex = _danmaku.Color, HorizontalAlignment = HorizontalAlignment.Stretch };
            var filter = new ToggleSwitch { Header = "基础屏蔽规则", IsOn = _danmaku.BasicFilter };
            var words = new TextBox { Header = "屏蔽词", Text = _danmaku.BlockedWords, PlaceholderText = "空格或逗号分隔", MaxLength = 400 };
            var advanced = new StackPanel { Spacing = 10, Children = { source, autoMatch, size, color, filter, words },
                Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed };
            enabled.Toggled += (_, _) => advanced.Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed;
            var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right };
            var matchNow = new Button { Content = "立即匹配哔哩哔哩弹幕", HorizontalAlignment = HorizontalAlignment.Stretch };
            var options = new StackPanel { Width = 296, Spacing = 12, Children = { enabled, advanced, matchNow, save } };
            var flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
                Content = new ScrollViewer { MaxHeight = Math.Max(140, ActualHeight - 126), Content = options }
            };
            flyout.Closed += (_, _) => { _danmakuFlyout = null; if (IsLoaded) ShowControls(); };
            matchNow.Click += async (_, _) =>
            {
                flyout.Hide();
                try
                {
                    _danmaku = _danmaku with { Enabled = true, Source = "bilibili" };
                    await App.Services.Database.SetSettingAsync("playback.danmaku", System.Text.Json.JsonSerializer.Serialize(_danmaku));
                    UpdateDanmakuToggle();
                    await ReloadDanmakuAsync(true);
                }
                catch (Exception ex) { ViewModel.QualityMessage = "在线匹配失败：" + ex.Message; }
            };
            save.Click += async (_, _) =>
            {
                flyout.Hide();
                try
                {
                    _danmaku = new DanmakuPreferences(enabled.IsOn, (int)(size.SelectedItem ?? 20), Math.Max(0, color.SelectedIndex), filter.IsOn, words.Text,
                        source.SelectedIndex == 1 ? "bilibili" : "origin", autoMatch.IsOn);
                    await App.Services.Database.SetSettingAsync("playback.danmaku", System.Text.Json.JsonSerializer.Serialize(_danmaku));
                    if (IsLoaded) OnDanmakuChanged(this, _danmakuAddress);
                }
                catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
            };
            _danmakuFlyout = flyout;
            PlaybackOptionsFlyout.Hide();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded && _danmakuFlyout == flyout)
                    flyout.ShowAt(this, new FlyoutShowOptions { Position = anchor, Placement = FlyoutPlacementMode.TopEdgeAlignedRight });
            });
        }
        catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
    }

    private async Task ReadDanmakuPreferencesAsync()
    {
        var saved = await App.Services.Database.GetSettingAsync("playback.danmaku");
        if (!string.IsNullOrWhiteSpace(saved))
            _danmaku = System.Text.Json.JsonSerializer.Deserialize<DanmakuPreferences>(saved) ?? new();
    }

    private void OnDanmakuChanged(object? sender, Uri? uri)
    {
        _danmakuAddress = uri;
        _ = ReloadDanmakuAsync(false);
    }

    private async Task ReloadDanmakuAsync(bool forceMatch)
    {
        CancelDanmakuLoad();
        _danmakuTimer.Stop();
        _comments = [];
        ClearDanmaku();
        _danmakuLoad = new CancellationTokenSource();
        var token = _danmakuLoad.Token;
        try
        {
            await ReadDanmakuPreferencesAsync();
            UpdateDanmakuToggle();
            if (!_danmaku.Enabled || token.IsCancellationRequested) return;
            var uri = _danmakuAddress;
            var online = _danmaku.Source == "bilibili";
            if (online && (forceMatch || _danmaku.AutoMatch && (!ViewModel.IsBilibiliPlayback || uri is null)))
            {
                var match = await ViewModel.MatchOnlineMediaAsync(token);
                uri = match.Danmaku;
            }
            else if (online && !_danmaku.AutoMatch && !forceMatch && !ViewModel.IsBilibiliPlayback)
            {
                ViewModel.QualityMessage = "在线弹幕自动匹配已关闭，可在弹幕设置中手动匹配。";
                return;
            }
            if (uri is null)
            {
                ViewModel.QualityMessage = "当前来源没有弹幕，可在设置中切换哔哩哔哩匹配。";
                return;
            }
            var blocked = _danmaku.BlockedWords.Split([' ', '，', ',', '\n', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var comments = await App.Services.Danmaku.LoadAsync(uri, token);
            if (token.IsCancellationRequested) return;
            _comments = comments.Where(comment =>
                !blocked.Any(word => comment.Text.Contains(word, StringComparison.OrdinalIgnoreCase)) &&
                (!_danmaku.BasicFilter || !BasicDanmakuBlockedWords.Any(word => comment.Text.Contains(word, StringComparison.Ordinal)))).ToArray();
            Diagnostics.Log($"Danmaku loaded: {_comments.Count} entries");
            if (_comments.Count > 0) _danmakuTimer.Start();
            else ViewModel.QualityMessage = "当前匹配节目没有可显示的弹幕。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ViewModel.QualityMessage = "弹幕加载失败：" + ex.Message; }
    }

    private void CancelDanmakuLoad()
    {
        var load = _danmakuLoad;
        _danmakuLoad = null;
        load?.Cancel();
        load?.Dispose();
    }

    private void ClearDanmaku()
    {
        DanmakuLayer.Children.Clear();
        _visibleComments.Clear();
        _lastCommentTime = -1;
        _nextComment = 0;
    }

    private void UpdateDanmaku()
    {
        if (!ViewModel.IsPlaying || ViewModel.Service.IsStopping || DanmakuLayer.ActualWidth <= 0) return;
        var now = ViewModel.Service.LastSnapshot.TimeMs / 1000d;
        if (now < 0) return;
        if (now < _lastCommentTime || now - _lastCommentTime > 2)
        {
            ClearDanmaku();
            while (_nextComment < _comments.Count && _comments[_nextComment].Seconds < now - .3) _nextComment++;
        }
        _lastCommentTime = now;
        var duration = 7d;
        foreach (var item in _visibleComments.ToArray())
        {
            var fraction = (now - item.Start) / duration;
            if (fraction >= 1)
            {
                DanmakuLayer.Children.Remove(item.Text);
                _visibleComments.Remove(item);
            }
            else Canvas.SetLeft(item.Text, DanmakuLayer.ActualWidth - fraction * (DanmakuLayer.ActualWidth + item.Text.ActualWidth));
        }
        var laneHeight = _danmaku.Size + 8;
        var lanes = Math.Clamp((int)(DanmakuLayer.ActualHeight * .32 / laneHeight), 1, 9);
        while (_nextComment < _comments.Count && _comments[_nextComment].Seconds <= now)
        {
            var comment = _comments[_nextComment++];
            var freeLane = Enumerable.Range(0, lanes).FirstOrDefault(lane => !_visibleComments.Any(item => item.Lane == lane), -1);
            if (freeLane < 0) continue;
            var rgb = _danmaku.Color switch { 1 => 0xFFFFFFu, 2 => 0xFFD18Au, 3 => 0xA7B8FFu, _ => comment.Rgb };
            var text = new TextBlock
            {
                Text = comment.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = _danmaku.Size,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb))
            };
            Canvas.SetLeft(text, DanmakuLayer.ActualWidth);
            Canvas.SetTop(text, 66 + freeLane * laneHeight);
            DanmakuLayer.Children.Add(text);
            _visibleComments.Add((text, now, freeLane));
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
        if (!_controlsVisible) SetCursor(0);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => ShowArrowCursor();

    private void OnBottomChromeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        // 给两组固定尺寸的按钮留足空间，窄窗叠为两行。
        var stacked = e.NewSize.Width < 800;
        BottomChromeExtraRow.Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(TransportControls, stacked ? 2 : 1);
        Grid.SetRow(MediaControls, stacked ? 1 : 0);
        Grid.SetColumn(MediaControls, stacked ? 0 : 1);
        Grid.SetColumnSpan(MediaControls, stacked ? 2 : 1);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && IsInsideControls(element)) return;
        if (_controlsVisible) HideControlsNow(); else ShowControls();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && IsInsideControls(element)) return;
        // 双击画面只切换控制层显隐；全屏必须显式点「全屏」按钮或按 F，
        // 避免误触把窗口切到全屏（用户反馈：打开播放器就全屏、没有侧边栏）。
        if (_controlsVisible) HideControlsNow(); else ShowControls();
        e.Handled = true;
    }

    private static bool IsInsideControls(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Button or Slider or ListView) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // 控制层淡入淡出时长：140–180ms（1.2 规范 §6）
    private const int ChromeFadeInMs = 150;
    private const int ChromeFadeOutMs = 160;

    private void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            SetChromeVisible(true);
        }
        _hideTimer.Stop();
        _hideTimer.Start();
        UpdateCenterPlayVisibility();
    }

    private void HideControlsNow()
    {
        _controlsVisible = false;
        SetChromeVisible(false);
        UpdateCenterPlayVisibility();
    }

    /// <summary>
    /// 顶部标题、进度条和底部操作作为一个层级显隐。隐藏时仅透明度与命中测试变化，
    /// 控件仍保留在自动化树中，键盘或自动化唤醒后可以继续操作。
    /// </summary>
    private void SetChromeVisible(bool visible)
    {
        ControlsLayer.IsHitTestVisible = visible;
        if (visible) ShowArrowCursor();
        else SetCursor(0);
        if (!MotionService.CanAnimate)
        {
            ControlsLayer.Opacity = visible ? 1 : 0;
            return;
        }
        Fade(ControlsLayer, visible ? 1 : 0, visible ? ChromeFadeInMs : ChromeFadeOutMs);
    }

    private void UpdateCenterPlayVisibility()
    {
        CenterPlayButton.Visibility = _controlsVisible && ViewModel.HasMedia && !ViewModel.IsPlaying
            && !ViewModel.IsBuffering && !ViewModel.ShowStatusPanel
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool AnyFlyoutOpen() => PlaylistFlyout.IsOpen || PlaybackOptionsFlyout.IsOpen || RateFlyout.IsOpen ||
        QualityFlyout.IsOpen || VolumeFlyout.IsOpen ||
        _danmakuFlyout is { IsOpen: true } || _timingFlyout is { IsOpen: true } || _sourceDialog is not null ||
        _onlineSubtitleDialog is not null || _bilibiliLoginDialog is not null;

    private void OnHideTimer(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsPlaying || AnyFlyoutOpen()) return;
        HideControlsNow();
    }

    private void OnMenuFlyoutOpening(object? sender, object e) => _hideTimer.Stop();

    private void OnMenuFlyoutClosed(object? sender, object e)
    {
        if (IsLoaded) ShowControls();
    }

    private static void Fade(UIElement target, double to, int milliseconds)
    {
        if (!MotionService.CanAnimate) { target.Opacity = to; return; }
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(milliseconds) };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        App.Window.GoBack();
    }

    private void OnCloudLoginClick(object sender, RoutedEventArgs e) => _ = ShowCloudLoginAsync();

    private async Task ShowCloudLoginAsync()
    {
        if (_cloudLoginDialog is not null || ViewModel.RequiredLoginProvider is not { } provider || XamlRoot is null) return;
        _hideTimer.Stop();
        var coordinator = App.Services.CloudLogin;
        var panel = new CloudLoginPanel { DataContext = coordinator };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{provider} 网盘登录",
            Content = panel,
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close
        };
        _cloudLoginDialog = dialog;
        coordinator.Succeeded += OnCloudLoginSucceeded;
        panel.CloseRequested += (_, _) => dialog.Hide();
        try
        {
            var showing = dialog.ShowAsync();
            await coordinator.StartAsync(provider);
            await showing;
        }
        catch (Exception ex)
        {
            ViewModel.QualityMessage = "登录面板无法打开：" + ex.Message;
        }
        finally
        {
            coordinator.Succeeded -= OnCloudLoginSucceeded;
            coordinator.Close();
            _cloudLoginDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private async void OnCloudLoginSucceeded(object? sender, Models.CloudProviderKind provider)
    {
        if (ViewModel.RequiredLoginProvider != provider) return;
        _cloudLoginDialog?.Hide();
        await ViewModel.ResumeAfterCloudLoginAsync();
    }

    private async void OnSourceRecoveryOffered(object? sender, IReadOnlyList<VodItem> sources)
    {
        if (!IsLoaded || XamlRoot is null || _sourceDialog is not null || sources.Count == 0) return;
        _hideTimer.Stop();
        var choices = new ComboBox { ItemsSource = sources, DisplayMemberPath = "SiteName", SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, Header = "可用来源" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "当前站点资源可能已失效",
            Content = new StackPanel { Spacing = 12, Children =
            {
                new TextBlock { Text = "已找到同名、同年份且包含播放线路的其他来源。是否保留当前集数和进度并切换？", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                choices
            } },
            PrimaryButtonText = "切换并播放", CloseButtonText = "暂不切换", DefaultButton = ContentDialogButton.Primary
        };
        _sourceDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is VodItem source)
                await ViewModel.SwitchSourceAsync(source);
        }
        catch (Exception ex) { ViewModel.StatusMessage = "来源切换失败：" + ex.Message; }
        finally
        {
            _sourceDialog = null;
            if (IsLoaded) ShowControls();
        }
    }

    private void OnTimingSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_timingFlyout is not null) return;
        _hideTimer.Stop();
        var intro = new NumberBox { Header = "片头结束时间（秒）", Minimum = 0, Maximum = 21600,
            Value = ViewModel.Timing.IntroEndMs / 1000d, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var outro = new NumberBox { Header = "片尾开始时间（秒），0 为不跳过", Minimum = 0, Maximum = 21600,
            Value = ViewModel.Timing.OutroStartMs / 1000d, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var enabled = new ToggleSwitch { Header = "自动跳过本集片头片尾", IsOn = ViewModel.SkipEnabled };
        var message = new TextBlock { Text = "仅保存到当前站点、线路和集数，不影响其他剧集。", TextWrapping = TextWrapping.Wrap };
        var markIntro = new Button { Content = "当前位置设为片头结束" };
        var markOutro = new Button { Content = "当前位置设为片尾开始" };
        var save = new Button { Content = "保存", Style = Application.Current.Resources["PrimaryButtonStyle"] as Style, HorizontalAlignment = HorizontalAlignment.Right };
        var clear = new Button { Content = "清除本集设置" };
        async Task MarkAsync(bool isIntro)
        {
            try
            {
                await ViewModel.SetTimingFromPositionAsync(isIntro);
                intro.Value = ViewModel.Timing.IntroEndMs / 1000d;
                outro.Value = ViewModel.Timing.OutroStartMs / 1000d;
                enabled.IsOn = ViewModel.SkipEnabled;
                message.Text = ViewModel.TimingMessage;
            }
            catch (Exception ex) { message.Text = ex.Message; }
        }
        markIntro.Click += async (_, _) => await MarkAsync(true);
        markOutro.Click += async (_, _) => await MarkAsync(false);
        clear.Click += async (_, _) =>
        {
            try { await ViewModel.SaveTimingAsync(0, 0, enabled.IsOn); intro.Value = outro.Value = 0; message.Text = "已清除当前集设置。"; }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            try { await ViewModel.SaveTimingAsync(intro.Value, outro.Value, enabled.IsOn); message.Text = ViewModel.TimingMessage; }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        var content = new StackPanel { Width = 332, Spacing = 12, Children = { intro, markIntro, outro, markOutro, enabled, message, clear, save } };
        var flyout = new Flyout { Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(240, ActualHeight - 100) } };
        flyout.Closed += (_, _) => { _timingFlyout = null; if (IsLoaded) ShowControls(); };
        _timingFlyout = flyout;
        PlaybackOptionsFlyout.Hide();
        flyout.ShowAt(PlaybackOptionsButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void OnCastClick(object sender, RoutedEventArgs e)
    {
        if (App.PlayerOnly) ViewModel.QualityMessage = "直达播放模式仅加载本地播放资源；需要投屏时请从完整应用打开。";
        else App.Window.Navigate("cast");
    }

    private void OnDanmakuToggleClick(object sender, RoutedEventArgs e) => _ = ToggleDanmakuAsync();

    private async Task ToggleDanmakuAsync()
    {
        try
        {
            await ReadDanmakuPreferencesAsync();
            _danmaku = _danmaku with { Enabled = !_danmaku.Enabled };
            await App.Services.Database.SetSettingAsync("playback.danmaku", System.Text.Json.JsonSerializer.Serialize(_danmaku));
            UpdateDanmakuToggle();
            if (IsLoaded) OnDanmakuChanged(this, _danmakuAddress);
            ShowControls();
        }
        catch (Exception ex) { ViewModel.QualityMessage = ex.Message; }
    }

    private async Task RefreshDanmakuToggleAsync()
    {
        try
        {
            await ReadDanmakuPreferencesAsync();
            UpdateDanmakuToggle();
        }
        catch (Exception) { }
    }

    private void UpdateDanmakuToggle()
    {
        if (DanmakuOnIndicator is null) return;
        DanmakuOnIndicator.Visibility = _danmaku.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(DanmakuToggleButton, _danmaku.Enabled ? "关闭弹幕" : "显示弹幕");
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => ViewModel.Volume = ViewModel.Volume == 0 ? 80 : 0;

    private void OnSeekPressed(object sender, PointerRoutedEventArgs e) => ViewModel.BeginSeek();

    private void OnSeekReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider) ViewModel.EndSeek(slider.Value);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePauseCommand.Execute(null);
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.F:
                ViewModel.FullScreenCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.M:
                OnMuteClick(this, e);
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.P:
                if (PlaylistFlyout.IsOpen) PlaylistFlyout.Hide();
                else if (ViewModel.HasPlaylist)
                {
                    ShowControls();
                    PlaylistFlyout.ShowAt(PlaylistButton);
                }
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (App.Window.IsPlayerFullScreen)
                {
                    ViewModel.FullScreenCommand.Execute(null);
                    ShowControls();
                }
                else if (PlaylistFlyout.IsOpen) PlaylistFlyout.Hide();
                else OnBackClick(this, e);
                e.Handled = true;
                break;
            case VirtualKey.Left:
                ViewModel.SeekBy(-5);
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                ViewModel.SeekBy(5);
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ViewModel.Volume = Math.Min(100, ViewModel.Volume + 5);
                ShowControls();
                e.Handled = true;
                break;
            case VirtualKey.Down:
                ViewModel.Volume = Math.Max(0, ViewModel.Volume - 5);
                ShowControls();
                e.Handled = true;
                break;
        }
    }
}
