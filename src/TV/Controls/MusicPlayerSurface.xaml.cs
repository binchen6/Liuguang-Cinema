using System.Buffers.Binary;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TV.Models;
using TV.Services;
using TV.Services.AI;
using TV.Services.Http;
using TV.Services.Playback;
using TV.ViewModels;

namespace TV.Controls;

public sealed partial class MusicPlayerSurface : UserControl
{
    private readonly PlayerViewModel _player;
    private readonly MusicMetadataService _metadata;
    private static readonly Windows.UI.ViewManagement.UISettings MotionSettings = new();
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private CancellationTokenSource? _track;
    private ProcessAudioLoopbackCapture? _capture;
    private MusicPresentation? _presentation;
    private IReadOnlyList<LyricLine> _lines = [];
    private int _line = -1;
    private bool _updatingClock;
    private bool _dragging;
    private float _bass;
    private float _average;
    private float _beat;
    private readonly long _lightOrigin = Environment.TickCount64;
    private ScrollViewer? _lyricScroller;
    private long _resumeLyricFollowAt;
    private long _nextLightAt;

    internal MusicPlayerSurface(PlayerViewModel player)
    {
        _player = player;
        _metadata = new MusicMetadataService(App.Services.Http);
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(GlassLight, true);
        ElementCompositionPreview.SetIsTranslationEnabled(ReflectedLight, true);
        DataContext = player;
        Seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSeekStart), true);
        Seek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSeekEnd), true);
        Seek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSeekEnd), true);
        Seek.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnSeekEnd), true);
        Lyrics.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnLyricWheel), true);
        Unloaded += (_, _) => ClearTrack();
    }

    internal void LoadTrack(PlaybackRequest request)
    {
        ClearTrack();
        _track = new CancellationTokenSource();
        _presentation = new MusicPresentation(request.Title, request.Item?.Actor ?? "", "", null, null);
        TrackTitle.Text = request.Title;
        _ = LoadMetadataAsync(request, _track.Token);
    }

    private async Task LoadMetadataAsync(PlaybackRequest request, CancellationToken token)
    {
        try
        {
            var data = await _metadata.LoadAsync(request, token);
            if (token.IsCancellationRequested) return;
            var current = _presentation;
            await ApplyPresentationAsync(data with { Cover = current?.Cover ?? data.Cover, Lyrics = current?.Lyrics ?? data.Lyrics }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostics.Log("Music metadata: " + ex.GetType().Name); }
    }

    internal void ClearTrack()
    {
        if (_track is { } track)
        {
            track.Cancel();
            track.Dispose();
            _track = null;
        }
        StopCapture();
        _presentation = null;
        _lines = [];
        _line = -1;
        _resumeLyricFollowAt = 0;
        _dragging = false;
        Lyrics.ItemsSource = null;
        Art.Source = null;
        CoverFallback.Visibility = Visibility.Visible;
        LyricPlaceholder.Visibility = Visibility.Visible;
        TrackArtist.Text = "";
        TrackTitle.Text = "";
        MetadataStatus.Text = "";
    }

    internal void UpdateClock(PlayerSnapshot snapshot)
    {
        if (_track is null) return;
        _updatingClock = true;
        Seek.Maximum = Math.Max(1, snapshot.LengthMs);
        Seek.IsEnabled = snapshot.CanSeek && snapshot.LengthMs > 0;
        if (!_dragging) Seek.Value = Math.Clamp(snapshot.TimeMs, 0, Seek.Maximum);
        _updatingClock = false;
        SyncLine(snapshot.TimeMs);
        UpdateLight(snapshot.IsPlaying);
    }

    private void SyncLine(long time)
    {
        var line = MusicMetadataService.ActiveLine(_lines, time);
        if (line == _line) return;
        var previous = _line;
        _line = line;
        RefreshRealizedLyrics(previous);
        RefreshRealizedLyrics(line);
        if (line >= 0 && Environment.TickCount64 >= _resumeLyricFollowAt) CenterCurrentLine();
    }

    private void OnLyricContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue) StyleLyric(args.ItemContainer, args.ItemIndex, animate: false);
    }

    private void RefreshRealizedLyrics(int index)
    {
        for (var nearby = Math.Max(0, index - 4); nearby < Math.Min(_lines.Count, index + 5); nearby++)
            if (Lyrics.ContainerFromIndex(nearby) is { } container) StyleLyric((SelectorItem)container, nearby, animate: true);
    }

    private void StyleLyric(SelectorItem item, int index, bool animate)
    {
        var active = _line == index;
        var distance = Math.Abs(index - _line);
        var opacity = active || Accessibility.HighContrast || _lines.ElementAtOrDefault(index)?.TimeMs is null ? 1f
            : distance == 1 ? 0.64f : distance == 2 ? 0.48f : 0.36f;
        var size = active ? new Vector3(1.035f, 1.035f, 1f) : Vector3.One;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(item, active ? "当前歌词" : "");
        var visual = ElementCompositionPreview.GetElementVisual(item);
        visual.CenterPoint = new Vector3(8, (float)item.ActualHeight * 0.5f, 0);
        if (!animate || !MotionSettings.AnimationsEnabled || Accessibility.HighContrast)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = opacity;
            visual.Scale = Vector3.One;
            return;
        }
        using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity);
        fade.Duration = TimeSpan.FromMilliseconds(360);
        visual.StartAnimation("Opacity", fade);
        using var lift = visual.Compositor.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(1, size);
        lift.Duration = TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("Scale", lift);
    }

    private void CenterCurrentLine()
    {
        if (_line < 0 || _line >= _lines.Count) return;
        _lyricScroller ??= FindLyricScroller(Lyrics);
        if (_lyricScroller is not { } scroll) return;
        if (Lyrics.ContainerFromIndex(_line) is not FrameworkElement active)
        {
            Lyrics.ScrollIntoView(_lines[_line], ScrollIntoViewAlignment.Leading);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CenterRealizedLine);
            return;
        }
        Center(scroll, active);
    }

    private void CenterRealizedLine()
    {
        if (_lyricScroller is { } scroll && _line >= 0 && Lyrics.ContainerFromIndex(_line) is FrameworkElement active)
            Center(scroll, active);
    }

    private static void Center(ScrollViewer scroll, FrameworkElement active)
    {
        var center = active.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(0, active.ActualHeight / 2)).Y;
        var offset = Math.Clamp(scroll.VerticalOffset + center - scroll.ViewportHeight * 0.48, 0, scroll.ScrollableHeight);
        if (Math.Abs(offset - scroll.VerticalOffset) > 1)
            scroll.ChangeView(null, offset, null, !MotionSettings.AnimationsEnabled);
    }

    private static ScrollViewer? FindLyricScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindLyricScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnLyricViewportChanged(object sender, SizeChangedEventArgs e)
    {
        var padding = _lines.FirstOrDefault()?.TimeMs is null ? 0 : Math.Max(0, e.NewSize.Height * 0.48 - 22);
        LyricLeadingSpace.Height = LyricTrailingSpace.Height = padding;
    }

    private void OnLyricWheel(object sender, PointerRoutedEventArgs e) =>
        _resumeLyricFollowAt = Environment.TickCount64 + 4200;

    private void UpdateLight(bool playing)
    {
        if (!playing || !MotionSettings.AnimationsEnabled || Accessibility.HighContrast)
        {
            StopCapture();
            if (_nextLightAt != 0)
            {
                _nextLightAt = 0;
                GlassOpticsEngine.SetIncidentLight(TransportGlass, Vector2.Zero, 0);
                GlassOpticsEngine.SetIncidentLight(CoverSurface, Vector2.Zero, 0);
            }
            return;
        }
        var now = Environment.TickCount64;
        if (now < _nextLightAt) return;
        _nextLightAt = now + 95;
        if (_capture is null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            _bass = _average = _beat = 0;
            _capture = new ProcessAudioLoopbackCapture(OnAudio, processOnly: true);
            _capture.Start();
        }

        var seconds = (now - _lightOrigin) / 1000.0;
        var beat = Volatile.Read(ref _beat);
        var horizontal = (float)(0.53 * Math.Sin(seconds * 0.19) + 0.28 * Math.Sin(seconds * 0.43 + 1.5)
            + 0.13 * Math.Sin(seconds * 0.71));
        var vertical = (float)(0.52 * Math.Cos(seconds * 0.16 + 0.3) + 0.20 * Math.Sin(seconds * 0.37));
        var occlusion = (float)(0.74 + 0.15 * Math.Sin(seconds * 0.57) + 0.07 * Math.Sin(seconds * 0.99 + 1.3));
        var width = Math.Max(300f, (float)MusicRoot.ActualWidth);
        var height = Math.Max(240f, (float)MusicRoot.ActualHeight);
        var intensity = Math.Clamp(occlusion + 0.28f * beat, 0, 1);
        MoveLight(GlassLight, new Vector2(horizontal * width * 0.36f, vertical * height * 0.28f - 42 - 22 * beat),
            -0.34f + horizontal * 0.20f, intensity * 0.85f, 1 + 0.10f * beat);
        MoveLight(ReflectedLight, new Vector2(horizontal * width * 0.23f + 107, vertical * height * -0.20f + 57 + 18 * beat),
            -0.60f - vertical * 0.15f, intensity * (0.32f + 0.10f * beat), 1 + 0.06f * beat);
        var direction = new Vector2(horizontal, vertical);
        GlassOpticsEngine.SetIncidentLight(TransportGlass, direction, intensity * (0.38f + 0.32f * beat));
        GlassOpticsEngine.SetIncidentLight(CoverSurface, direction - new Vector2(0.28f, 0.12f), intensity * 0.52f);
    }

    private static void MoveLight(FrameworkElement element, Vector2 point, float rotation, float opacity, float stretch)
    {
        var light = ElementCompositionPreview.GetElementVisual(element);
        light.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        using var drift = light.Compositor.CreateVector3KeyFrameAnimation();
        drift.InsertKeyFrame(1, new Vector3(point, 0));
        drift.Duration = TimeSpan.FromMilliseconds(260);
        light.StartAnimation("Translation", drift);
        using var intensity = light.Compositor.CreateScalarKeyFrameAnimation();
        intensity.InsertKeyFrame(1, opacity);
        intensity.Duration = TimeSpan.FromMilliseconds(240);
        light.StartAnimation("Opacity", intensity);
        using var tilt = light.Compositor.CreateScalarKeyFrameAnimation();
        tilt.InsertKeyFrame(1, rotation);
        tilt.Duration = TimeSpan.FromMilliseconds(320);
        light.StartAnimation("RotationAngle", tilt);
        using var spread = light.Compositor.CreateVector3KeyFrameAnimation();
        spread.InsertKeyFrame(1, new Vector3(stretch, 1 / stretch, 1));
        spread.Duration = TimeSpan.FromMilliseconds(260);
        light.StartAnimation("Scale", spread);
    }

    private void OnAudio(byte[] samples)
    {
        float energy = 0;
        for (var offset = 0; offset + 1 < samples.Length; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(offset, 2)) / 32768f;
            _bass += 0.056f * (sample - _bass);
            energy += _bass * _bass;
        }
        var level = MathF.Sqrt(energy / (samples.Length / 2));
        _average += 0.12f * (level - _average);
        var onset = Math.Clamp((level - _average * 1.18f) * 18f, 0, 1);
        Volatile.Write(ref _beat, Math.Max(onset, _beat * 0.62f));
    }

    private void StopCapture()
    {
        if (_capture is not { } capture) return;
        _capture = null;
        capture.Dispose();
    }

    private async Task ApplyPresentationAsync(MusicPresentation data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _presentation = data;
        TrackTitle.Text = data.Title;
        TrackArtist.Text = string.Join(" · ", new[] { data.Artist, data.Album }.Where(value => !string.IsNullOrWhiteSpace(value)));
        _lines = MusicMetadataService.ParseLyrics(data.Lyrics);
        _line = -1;
        Lyrics.ItemsSource = _lines;
        var padding = _lines.FirstOrDefault()?.TimeMs is null ? 0 : Math.Max(0, Lyrics.ActualHeight * 0.48 - 22);
        LyricLeadingSpace.Height = LyricTrailingSpace.Height = padding;
        LyricPlaceholder.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncLine(_player.Service.LastSnapshot.TimeMs);
        if (data.Cover is not { Length: > 0 } cover) return;
        try
        {
            using var stream = new MemoryStream(cover, writable: false);
            var image = new BitmapImage { DecodePixelWidth = 512 };
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            if (token.IsCancellationRequested || !ReferenceEquals(data, _presentation)) return;
            Art.Source = image;
            CoverFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (!token.IsCancellationRequested) MetadataStatus.Text = "无法读取歌曲封面。";
        }
    }

    private async void OnMatch(object sender, RoutedEventArgs e)
    {
        if (_track is not { } track || _presentation is not { } current || XamlRoot is null) return;
        var token = track.Token;
        if (current.Cover is not null && current.Lyrics is not null)
        {
            MetadataStatus.Text = "当前歌曲已有封面和歌词。";
            return;
        }
        var title = new TextBox { Header = "曲名", Text = current.Title, MaxLength = 150 };
        var artist = new TextBox { Header = "歌手", Text = current.Artist, MaxLength = 150 };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(title);
        panel.Children.Add(artist);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "匹配封面与歌词", Content = panel,
            PrimaryButtonText = "匹配", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || token.IsCancellationRequested ||
                string.IsNullOrWhiteSpace(artist.Text) || string.IsNullOrWhiteSpace(title.Text)) return;
            MetadataStatus.Text = "正在匹配…";
            var match = await _metadata.MatchAsync(current with { Title = title.Text.Trim() }, artist.Text.Trim(),
                _player.Service.LastSnapshot.LengthMs, token);
            var latest = _presentation ?? current;
            await ApplyPresentationAsync(match with { Cover = latest.Cover ?? match.Cover, Lyrics = latest.Lyrics ?? match.Lyrics }, token);
            if (!token.IsCancellationRequested)
                MetadataStatus.Text = current.Cover != match.Cover || current.Lyrics != match.Lyrics ? "在线匹配完成。" : "无匹配结果，请核对曲名和歌手。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) MetadataStatus.Text = "在线匹配失败。 " + ex.Message; }
    }

    private async void OnImportCover(object sender, RoutedEventArgs e) => await ImportAsync(cover: true);
    private async void OnImportLyrics(object sender, RoutedEventArgs e) => await ImportAsync(cover: false);

    private async Task ImportAsync(bool cover)
    {
        if (_track is not { } track || _presentation is null) return;
        var token = track.Token;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id);
            foreach (var ext in cover ? new[] { ".png", ".jpg", ".jpeg", ".webp" } : new[] { ".lrc", ".txt" })
                picker.FileTypeFilter.Add(ext);
            if (await picker.PickSingleFileAsync() is not { } file || token.IsCancellationRequested) return;
            var bytes = await MusicMetadataService.ReadLocalAsync(file.Path, cover, token);
            if (_presentation is not { } current || token.IsCancellationRequested) return;
            await ApplyPresentationAsync(cover ? current with { Cover = bytes } : current with { Lyrics = SourceHttpClient.DecodeText(bytes) }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) MetadataStatus.Text = "导入失败。 " + ex.Message; }
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 680;
        TrackLayout.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 3, GridUnitType.Star);
        TrackLayout.RowDefinitions[0].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TrackLayout.RowDefinitions[1].Height = new GridLength(narrow ? 1 : 0, GridUnitType.Star);
        Grid.SetRow(LyricPanel, narrow ? 1 : 0);
        Grid.SetColumn(LyricPanel, narrow ? 0 : 1);
        CoverSurface.Width = CoverSurface.Height = narrow
            ? Math.Clamp(e.NewSize.Height * 0.23, 70, 145)
            : Math.Clamp(Math.Min(e.NewSize.Height * 0.45, e.NewSize.Width * 0.30), 140, 290);
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.Window.GoBack();

    private void OnLyricClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LyricLine { TimeMs: { } time } && Seek.IsEnabled)
        {
            _resumeLyricFollowAt = 0;
            _player.EndSeek(time);
        }
    }

    private void OnSeekStart(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _player.BeginSeek();
    }

    private void OnSeekEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _player.EndSeek(Seek.Value);
    }

    private void OnSeekValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingClock || _track is null) return;
        if (_dragging) _player.PreviewSeek(e.NewValue);
        else _player.EndSeek(e.NewValue);
    }
}
