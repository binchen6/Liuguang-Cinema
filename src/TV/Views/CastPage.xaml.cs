using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using QRCoder;
using TV.Services;
using TV.Services.Casting;
using TV.Services.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace TV.Views;

public sealed partial class CastPage : Page
{
    private CastNetwork? _receiver;
    private CastOffer? _incoming;
    private CastOffer? _playing;
    private MediaPlayer? _player;
    private MediaSource? _media;
    private ContentDialog? _phoneFlyout;
    private CancellationTokenSource? _startCancellation;
    private Task? _startTask;
    private Task? _stopTask;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusClock;
    private bool _showingPosition;

    public CastPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs args) => MotionService.Reveal(this);
    private async void OnUnloaded(object sender, RoutedEventArgs args) => await StopAsync();

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // 宽屏铺满：左侧接收状态/设备会话 + 右侧投屏舞台；窄窗时上下堆叠。
        var narrow = args.NewSize.Width < 760;
        if (narrow)
        {
            BodyLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            BodyStageColumn.Width = new GridLength(0);
            BodyTopRow.Height = new GridLength(1, GridUnitType.Star);
            BodyBottomRow.Height = new GridLength(1, GridUnitType.Star);
            CastScroll.VerticalScrollMode = ScrollMode.Enabled;
            StageFrame.VerticalScrollMode = ScrollMode.Enabled;
            StageFrame.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            StageFrame.VerticalContentAlignment = VerticalAlignment.Top;
            BodyGrid.ColumnSpacing = 0;
            BodyGrid.RowSpacing = 16;
            Grid.SetRow(CastScroll, 0);
            Grid.SetRowSpan(CastScroll, 1);
            Grid.SetColumn(CastScroll, 0);
            Grid.SetRow(StageFrame, 1);
            Grid.SetRowSpan(StageFrame, 1);
            Grid.SetColumn(StageFrame, 0);
        }
        else
        {
            BodyLeftColumn.Width = new GridLength(360);
            BodyStageColumn.Width = new GridLength(1, GridUnitType.Star);
            BodyTopRow.Height = new GridLength(1, GridUnitType.Star);
            BodyBottomRow.Height = new GridLength(0);
            StageFrame.VerticalScrollMode = ScrollMode.Disabled;
            StageFrame.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StageFrame.VerticalContentAlignment = VerticalAlignment.Stretch;
            BodyGrid.ColumnSpacing = 32;
            BodyGrid.RowSpacing = 0;
            Grid.SetRow(CastScroll, 0);
            Grid.SetRowSpan(CastScroll, 2);
            Grid.SetColumn(CastScroll, 0);
            Grid.SetRow(StageFrame, 0);
            Grid.SetRowSpan(StageFrame, 2);
            Grid.SetColumn(StageFrame, 1);
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs args)
    {
        if (_startTask is { IsCompleted: false } || _stopTask is { IsCompleted: false }) return;
        _startTask = StartAsync();
    }

    private async Task StartAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _startCancellation = cancellation;
        StartButton.IsEnabled = false;
        Feedback.IsOpen = false;
        StatusText.Text = "正在开启";
        CastNetwork? receiver = null;
        try
        {
            receiver = await Task.Run(() => new CastNetwork(), cancellation.Token);
            receiver.Offered += OnOffered;
            receiver.Controlled += OnControlled;
            await receiver.StartAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _receiver = receiver;
            ReceiverName.Text = receiver.DeviceName;
            ReceiverActions.Visibility = Visibility.Visible;
            StartButton.Visibility = Visibility.Collapsed;
            StatusDot.Opacity = 1;
            UpdateWaitingState();
            _statusClock = DispatcherQueue.CreateTimer();
            _statusClock.Interval = TimeSpan.FromMilliseconds(500);
            _statusClock.Tick += (_, _) => PublishStatus();
            _statusClock.Start();
            Diagnostics.Log("Cast receiver ready: " + (receiver.IsDiscoverable ? "DLNA and browser" : "browser"));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (receiver is not null) await receiver.DisposeAsync();
        }
        catch (Exception ex)
        {
            if (receiver is not null) await receiver.DisposeAsync();
            _receiver = null;
            ResetPage();
            ShowError("无法开启接收", ex.Message);
        }
        finally
        {
            _startCancellation = null;
            StartButton.IsEnabled = true;
        }
    }

    private void OnOffered(CastOffer offer) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_receiver is null) return;
        _incoming = offer;
        IncomingTitle.Text = offer.Title;
        Incoming.Visibility = Visibility.Visible;
        StatusText.Text = "待接收";
        if (IsLoaded) Incoming.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = MotionService.CanAnimate });
    });

    private void OnRejectClick(object sender, RoutedEventArgs args)
    {
        if (_incoming is { } incoming) _receiver?.Reject(incoming.Id);
        ClearIncoming();
        PublishStatus();
    }

    private async void OnAcceptClick(object sender, RoutedEventArgs args)
    {
        if (_incoming is not { } incoming || _receiver is not { } receiver) return;
        ClearIncoming();
        ReleaseMedia();
        if (receiver.Accept(incoming.Id) is not { } approved) return;
        _playing = approved;
        Feedback.IsOpen = false;
        StatusText.Text = "正在打开";
        SessionTitleText.Text = incoming.Title;
        StageIdle.Visibility = Visibility.Collapsed;
        PreviewFrame.Visibility = Visibility.Visible;
        try
        {
            // 本地接收的文件用文件 URI 直接交给 MediaPlayer：未打包应用走 StorageFile 需要
            // WinRT 代理访问权限，某些受限环境会抛 UnauthorizedAccessException 导致投送无法播放。
            var media = approved.InternetMedia is { } uri
                ? MediaSource.CreateFromUri(uri)
                : MediaSource.CreateFromUri(new Uri(approved.LocalMedia!, UriKind.Absolute));
            if (_playing?.Id != approved.Id || _receiver != receiver) { media.Dispose(); return; }
            _media = media;
            _player = new MediaPlayer { Source = media, AutoPlay = false };
            _player.CommandManager.IsEnabled = false;
            _player.MediaFailed += OnMediaFailed;
            _player.MediaEnded += OnMediaEnded;
            Preview.SetMediaPlayer(_player);
            _player.Play();
            CastScroll.ChangeView(null, 0, null);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"投送播放异常: {ex.GetType().Name} {ex.Message}");
            ReleaseMedia();
            UpdateWaitingState();
            ShowError("无法播放投送视频", ex.Message);
        }
    }

    private void OnControlled(CastControl control) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_playing?.Id != control.Id || _player is not { } player) return;
        try
        {
            switch (control.Operation)
            {
                case CastOperation.Play: player.Play(); break;
                case CastOperation.Pause: player.Pause(); break;
                case CastOperation.Stop: ReleaseMedia(); UpdateWaitingState(); break;
                case CastOperation.Seek when player.PlaybackSession.CanSeek:
                    player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Min(control.Value, player.PlaybackSession.NaturalDuration.TotalSeconds));
                    break;
                case CastOperation.Volume: player.Volume = control.Value / 100; break;
                case CastOperation.Mute: player.IsMuted = control.Value != 0; break;
            }
            PublishStatus();
        }
        catch (Exception ex) { ShowError("投屏控制失败", ex.Message); }
    });

    private void PublishStatus()
    {
        if (_receiver is not { } receiver) return;
        if (_player is not { } player)
        {
            receiver.Status = CastStatus.Idle;
            StatusText.Text = _incoming is null ? "等待投送" : "待接收";
            SessionStateText.Text = StatusText.Text;
            if (_incoming is null)
            {
                SessionTitleText.Text = "等待手机发送";
                SessionPositionText.Text = "00:00";
            }
            return;
        }
        var session = player.PlaybackSession;
        var state = session.PlaybackState switch
        {
            MediaPlaybackState.Playing => "PLAYING",
            MediaPlaybackState.Paused => "PAUSED_PLAYBACK",
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => "TRANSITIONING",
            _ => "STOPPED"
        };
        if (state == "PLAYING" && receiver.Status.State != state) Diagnostics.Log("Cast video playing");
        receiver.Status = new CastStatus(state, session.Position, session.NaturalDuration, (int)Math.Round(player.Volume * 100), player.IsMuted);
        _showingPosition = true;
        PreviewToggle.IsEnabled = state is "PLAYING" or "PAUSED_PLAYBACK";
        PreviewPlayIcon.Glyph = state == "PLAYING" ? "\uE769" : "\uE768";
        PreviewPosition.IsEnabled = session.CanSeek;
        PreviewPosition.Maximum = Math.Max(1, session.NaturalDuration.TotalSeconds);
        PreviewPosition.Value = Math.Clamp(session.Position.TotalSeconds, 0, PreviewPosition.Maximum);
        PreviewTime.Text = session.Position.ToString(session.Position.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        _showingPosition = false;
        StatusText.Text = _incoming is not null ? "待接收" : state switch
        {
            "PLAYING" => "正在播放",
            "PAUSED_PLAYBACK" => "已暂停",
            _ => "正在打开"
        };
        SessionStateText.Text = StatusText.Text;
        SessionPositionText.Text = PreviewTime.Text;
    }

    private void OnPreviewToggle(object sender, RoutedEventArgs args)
    {
        if (_player is not { } player) return;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
        else player.Play();
    }

    private void OnPreviewPositionChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_showingPosition && _player is { } player && player.PlaybackSession.CanSeek)
            player.PlaybackSession.Position = TimeSpan.FromSeconds(args.NewValue);
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (sender != _player) return;
        Diagnostics.Log($"投送播放失败: {args.Error} {args.ErrorMessage} {args.ExtendedErrorCode?.Message}");
        ReleaseMedia();
        UpdateWaitingState();
        ShowError("无法播放投送视频", args.ErrorMessage);
    });

    private void OnMediaEnded(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (sender != _player) return;
        ReleaseMedia();
        UpdateWaitingState();
    });

    private void ReleaseMedia()
    {
        _playing = null;
        if (_player is { } player)
        {
            _player = null;
            player.MediaFailed -= OnMediaFailed;
            player.MediaEnded -= OnMediaEnded;
            Preview.SetMediaPlayer(null);
            player.Dispose();
        }
        _media?.Dispose();
        _media = null;
    }

    private void ClearIncoming()
    {
        _incoming = null;
        Incoming.Visibility = Visibility.Collapsed;
    }

    private void UpdateWaitingState()
    {
        StageIdle.Visibility = Visibility.Visible;
        PreviewFrame.Visibility = Visibility.Collapsed;
        StatusText.Text = _incoming is null ? "等待投送" : "待接收";
        StageTitle.Text = "等待投送";
        StageHint.Text = _receiver?.IsDiscoverable == true ? "在手机视频应用中选择这台电脑" : "请用手机扫码发送视频";
        SessionStateText.Text = StatusText.Text;
        if (_playing is null) SessionTitleText.Text = "等待手机发送";
        PublishStatus();
    }

    private async void OnSenderClick(object sender, RoutedEventArgs args)
    {
        if (_receiver is not { SenderAddress: { } address } receiver || _phoneFlyout is not null) return;
        try
        {
            using var data = QRCodeGenerator.GenerateQrCode(address.AbsoluteUri, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            var bitmap = await BitmapImages.FromBytesAsync(qr.GetGraphic(6));
            if (!IsLoaded || receiver != _receiver || _phoneFlyout is not null) return;
            var qrSize = Math.Clamp(ActualHeight - 268, 160, 256);
            var image = new Image { Width = qrSize, Height = qrSize, Source = bitmap,
                HorizontalAlignment = HorizontalAlignment.Center, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, "投屏二维码");
            var link = new TextBox { Text = address.AbsoluteUri, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, "投屏链接");
            var details = new StackPanel { Spacing = 12, Children =
                {
                    new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(12),
                        CornerRadius = new CornerRadius(8), Child = image, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "使用手机相机扫码，确保与电脑连接同一 Wi-Fi。",
                        TextWrapping = TextWrapping.Wrap, HorizontalTextAlignment = TextAlignment.Center },
                    link
                } };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "手机扫码投屏", PrimaryButtonText = "复制链接", CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary, MaxWidth = 400,
                Content = new ScrollViewer { Content = details, MaxHeight = Math.Max(200, ActualHeight - 160),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            };
            dialog.PrimaryButtonClick += async (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                for (var attempt = 0; attempt < 4 && receiver == _receiver; attempt++)
                {
                    try
                    {
                        var payload = new DataPackage();
                        payload.SetText(address.AbsoluteUri);
                        Clipboard.SetContent(payload);
                        dialog.PrimaryButtonText = "已复制";
                        return;
                    }
                    catch (Exception ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 3)
                    {
                        await Task.Delay(65);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log($"投屏链接复制失败 ({ex.HResult:X8})");
                        dialog.PrimaryButtonText = "重试复制";
                        return;
                    }
                }
            };
            dialog.Closed += (_, _) => _phoneFlyout = null;
            _phoneFlyout = dialog;
            var showing = dialog.ShowAsync();
            Diagnostics.Log("Cast sender panel shown");
            await showing;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"投屏二维码无法显示 ({ex.HResult:X8})");
            ShowError("无法显示二维码", ex.Message);
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs args) => await StopAsync();

    public Task StopAsync() => _stopTask is { IsCompleted: false } current ? current : _stopTask = StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _startCancellation?.Cancel();
        if (_startTask is { IsCompleted: false } starting) await starting;
        _phoneFlyout?.Hide();
        _statusClock?.Stop();
        _statusClock = null;
        ReleaseMedia();
        var receiver = _receiver;
        _receiver = null;
        ClearIncoming();
        ResetPage();
        if (receiver is null) return;
        receiver.Offered -= OnOffered;
        receiver.Controlled -= OnControlled;
        try { await receiver.DisposeAsync(); }
        catch (Exception ex) { Diagnostics.Log("关闭投屏接收失败: " + ex.Message); }
    }

    private void ResetPage()
    {
        PreviewFrame.Visibility = Visibility.Collapsed;
        StageIdle.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Visible;
        ReceiverActions.Visibility = Visibility.Collapsed;
        StatusDot.Opacity = 0.35;
        StatusText.Text = "未开启";
        StageTitle.Text = "接收手机视频";
        StageHint.Text = "手机与电脑连接同一 Wi-Fi";
        ReceiverName.Text = "等待开启";
        SessionStateText.Text = "未开启";
        SessionTitleText.Text = "等待手机发送";
        SessionPositionText.Text = "00:00";
    }

    private void ShowError(string title, string message)
    {
        Feedback.Title = title;
        Feedback.Message = message;
        Feedback.IsOpen = true;
    }
}
