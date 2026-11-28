using System.Buffers.Binary;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
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
using TV.Views;
using Windows.UI;

namespace TV.Controls;

public sealed partial class MusicPlayerSurface : UserControl
{
    private readonly PlayerViewModel _player;
    private readonly MusicMetadataService _metadata;
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
    private float _smoothedBeat;
    private readonly long _lightOrigin = Environment.TickCount64;
    private ScrollViewer? _lyricScroller;
    private long _resumeLyricFollowAt;
    private long _nextLightAt;
    private int _volumeBeforeMute = 85;
    private SolarFieldVisuals? _solarField;

    private static readonly LinearGradientBrush ActiveLyricBgBrush = new()
    {
        StartPoint = new Windows.Foundation.Point(0.05, 0),
        EndPoint = new Windows.Foundation.Point(0.95, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(46, 255, 244, 224), Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(24, 204, 224, 255), Offset = 0.55 },
            new GradientStop { Color = Color.FromArgb(14, 255, 232, 210), Offset = 1.0 }
        }
    };

    private static readonly LinearGradientBrush ActiveLyricBorderBrush = new()
    {
        StartPoint = new Windows.Foundation.Point(0.08, 0),
        EndPoint = new Windows.Foundation.Point(0.92, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(108, 255, 248, 234), Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(38, 198, 224, 255), Offset = 0.55 },
            new GradientStop { Color = Color.FromArgb(56, 255, 226, 200), Offset = 1.0 }
        }
    };

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    internal MusicPlayerSurface(PlayerViewModel player)
    {
        _player = player;
        _metadata = new MusicMetadataService(App.Services.Http);
        InitializeComponent();
        DataContext = player;
        Seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSeekStart), true);
        Seek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSeekEnd), true);
        Seek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSeekEnd), true);
        Seek.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnSeekEnd), true);
        Lyrics.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnLyricWheel), true);
        Loaded += (_, _) => EnsureSolarField();
        Unloaded += (_, _) => ClearTrack();
    }

    internal void LoadTrack(PlaybackRequest request)
    {
        ClearTrack();
        _track = new CancellationTokenSource();
        _presentation = new MusicPresentation(request.Title, request.Item?.Actor ?? "", "", null, null);
        TrackTitle.Text = request.Title;
        EnsureSolarField();
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
        UpdateLight(playing: false);
        _solarField?.Dispose();
        _solarField = null;
        _presentation = null;
        _lines = [];
        _line = -1;
        _resumeLyricFollowAt = 0;
        _dragging = false;
        Lyrics.ItemsSource = null;
        ArtBrush.ImageSource = null;
        CoverFallback.Visibility = Visibility.Visible;
        LyricPlaceholder.Visibility = Visibility.Visible;
        TrackArtist.Text = "";
        TrackArtist.Visibility = Visibility.Collapsed;
        TrackTitle.Text = "";
        SetMetadataStatus("");
    }

    private void SetMetadataStatus(string text)
    {
        MetadataStatus.Text = text;
        MetadataStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
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
        var highContrast = Accessibility.HighContrast;
        var opacity = active || highContrast || _lines.ElementAtOrDefault(index)?.TimeMs is null ? 1f
            : distance == 1 ? 0.58f : distance == 2 ? 0.34f : 0.16f;
        var size = active ? new Vector3(1.022f, 1.022f, 1f) : Vector3.One;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(item, active ? "当前歌词" : "");

        if (VisualTreeHelpers.FindDescendantByName(item, "LyricGlassPlate") is Border plate)
        {
            if (active && !highContrast)
            {
                plate.Background = ActiveLyricBgBrush;
                plate.BorderBrush = ActiveLyricBorderBrush;
            }
            else
            {
                plate.Background = TransparentBrush;
                plate.BorderBrush = TransparentBrush;
            }
        }

        var visual = ElementCompositionPreview.GetElementVisual(item);
        visual.CenterPoint = new Vector3(16, (float)item.ActualHeight * 0.5f, 0);
        if (!animate || !MotionService.CanAnimate || highContrast)
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
        _lyricScroller ??= VisualTreeHelpers.FindDescendant<ScrollViewer>(Lyrics);
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
        var offset = Math.Clamp(scroll.VerticalOffset + center - scroll.ViewportHeight * 0.46, 0, scroll.ScrollableHeight);
        if (Math.Abs(offset - scroll.VerticalOffset) > 1)
            scroll.ChangeView(null, offset, null, !MotionService.CanAnimate || Accessibility.HighContrast);
    }

    private void OnLyricViewportChanged(object sender, SizeChangedEventArgs e)
    {
        var padding = _lines.FirstOrDefault()?.TimeMs is null ? 0 : Math.Max(0, e.NewSize.Height * 0.46 - 24);
        LyricLeadingSpace.Height = LyricTrailingSpace.Height = padding;
    }

    private void OnLyricWheel(object sender, PointerRoutedEventArgs e) =>
        _resumeLyricFollowAt = Environment.TickCount64 + 4200;

    private void EnsureSolarField()
    {
        if (Accessibility.HighContrast || !SolarStageHost.IsLoaded) return;
        _solarField ??= new SolarFieldVisuals(SolarStageHost);
        _solarField.Resize((float)MusicRoot.ActualWidth, (float)MusicRoot.ActualHeight);
    }

    private void UpdateLight(bool playing)
    {
        if (!playing || !MotionService.CanRunAmbientLoops || Accessibility.HighContrast)
        {
            StopCapture();
            if (_nextLightAt != 0)
            {
                _nextLightAt = 0;
                _solarField?.SetRestState();
                GlassOpticsEngine.SetIncidentLight(TransportGlass, Vector2.Zero, 0);
                GlassOpticsEngine.SetIncidentLight(CoverSurface, Vector2.Zero, 0);
                GlassOpticsEngine.SetIncidentLight(HeaderBackGlass, Vector2.Zero, 0);
                GlassOpticsEngine.SetIncidentLight(MetadataButton, Vector2.Zero, 0);
            }
            return;
        }
        var now = Environment.TickCount64;
        if (now < _nextLightAt) return;
        _nextLightAt = now + 85;
        if (_capture is null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            _bass = _average = _beat = _smoothedBeat = 0;
            _capture = new ProcessAudioLoopbackCapture(OnAudio, processOnly: true);
            _capture.Start();
        }

        EnsureSolarField();
        var width = Math.Max(360f, (float)MusicRoot.ActualWidth);
        var height = Math.Max(260f, (float)MusicRoot.ActualHeight);
        var t = (now - _lightOrigin) / 1000.0;
        var rawBeat = Volatile.Read(ref _beat);
        _smoothedBeat = rawBeat > _smoothedBeat
            ? _smoothedBeat + (rawBeat - _smoothedBeat) * 0.62f
            : _smoothedBeat * 0.78f;

        // Irregular organic multi-harmonic sunlight trajectory (incommensurate irrational-ratio frequencies)
        var driftX = (float)(0.48 * Math.Sin(t * 0.17 + 0.4) + 0.27 * Math.Sin(t * 0.41 + 2.1) + 0.14 * Math.Cos(t * 0.89 + 0.7));
        var driftY = (float)(0.45 * Math.Cos(t * 0.13 + 0.9) + 0.25 * Math.Sin(t * 0.37 + 1.4) + 0.12 * Math.Sin(t * 0.73 + 2.8));
        var cloudMod = (float)(0.78 + 0.13 * Math.Sin(t * 0.29 + 0.5) + 0.09 * Math.Sin(t * 0.67 + 1.9));
        var solarPower = Math.Clamp(cloudMod + 0.26f * _smoothedBeat, 0.35f, 1.0f);

        var primarySource = _solarField?.Update(width, height, t, driftX, driftY, _smoothedBeat, solarPower)
            ?? new Vector2(width * 0.35f, height * 0.25f);

        // Couple real screen-space diffuse + Fresnel rim reflection across all glass controls
        ReflectOn(CoverSurface, primarySource, width, height, solarPower * (0.64f + 0.24f * _smoothedBeat), warmth: 0.82f);
        ReflectOn(TransportGlass, primarySource, width, height, solarPower * (0.58f + 0.28f * _smoothedBeat), warmth: 0.70f);
        ReflectOn(HeaderBackGlass, primarySource, width, height, solarPower * 0.50f, warmth: 0.86f);
        ReflectOn(MetadataButton, primarySource, width, height, solarPower * 0.44f, warmth: 0.55f);
    }

    private void ReflectOn(FrameworkElement surface, Vector2 lightOrigin, float width, float height, float illuminance, float warmth)
    {
        if (!surface.IsLoaded || surface.ActualWidth <= 0 || surface.ActualHeight <= 0) return;
        try
        {
            var center = surface.TransformToVisual(MusicRoot).TransformPoint(
                new Windows.Foundation.Point(surface.ActualWidth * 0.5, surface.ActualHeight * 0.5));
            var delta = lightOrigin - new Vector2((float)center.X, (float)center.Y);
            var direction = new Vector2(delta.X / (width * 0.58f), delta.Y / (height * 0.64f));
            var attenuation = 1f / (1f + 0.28f * direction.LengthSquared());
            GlassOpticsEngine.SetIncidentLight(surface, direction, illuminance * attenuation, warmth);
        }
        catch { }
    }

    /// <summary>
    /// Full-bleed GPU Composition sunlight &amp; glass-refraction stage.
    /// Composes 5 overlapping asymmetric soft-falloff brushes directly across the viewport rectangle
    /// (zero hard oval/ellipse boundaries), driven by smooth Compositor animations.
    /// </summary>
    private sealed class SolarFieldVisuals : IDisposable
    {
        private readonly FrameworkElement _host;
        private readonly Compositor _compositor;
        private readonly ContainerVisual _root;
        private readonly CompositionRectangleGeometry _rect;
        private readonly CubicBezierEasingFunction _ease;

        private readonly ShapeVisual _shaftVisual;
        private readonly CompositionLinearGradientBrush _shaftBrush;

        private readonly ShapeVisual _keyVisual;
        private readonly CompositionRadialGradientBrush _keyBrush;

        private readonly ShapeVisual _causticVisual;
        private readonly CompositionRadialGradientBrush _causticBrush;

        private readonly ShapeVisual _skyVisual;
        private readonly CompositionRadialGradientBrush _skyBrush;

        private readonly ShapeVisual _haloVisual;
        private readonly CompositionRadialGradientBrush _haloBrush;

        private readonly Vector2KeyFrameAnimation _keyCenterAnim;
        private readonly Vector2KeyFrameAnimation _keyRadiusAnim;
        private readonly Vector2KeyFrameAnimation _causticCenterAnim;
        private readonly Vector2KeyFrameAnimation _causticRadiusAnim;
        private readonly Vector2KeyFrameAnimation _skyCenterAnim;
        private readonly ScalarKeyFrameAnimation _shaftOpacityAnim;
        private readonly ScalarKeyFrameAnimation _causticOpacityAnim;
        private readonly ScalarKeyFrameAnimation _haloOpacityAnim;

        private float _width;
        private float _height;

        internal SolarFieldVisuals(FrameworkElement host)
        {
            _host = host;
            var hostVisual = ElementCompositionPreview.GetElementVisual(host);
            _compositor = hostVisual.Compositor;
            _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.9f), new Vector2(0.35f, 1.0f));

            _root = _compositor.CreateContainerVisual();
            _root.Clip = _compositor.CreateInsetClip();
            _rect = _compositor.CreateRectangleGeometry();

            // 1. Broad diagonal upper-left solar shaft (atmospheric sunlight streaming across room)
            _shaftBrush = _compositor.CreateLinearGradientBrush();
            _shaftBrush.MappingMode = CompositionMappingMode.Relative;
            _shaftBrush.StartPoint = new Vector2(0.0f, 0.0f);
            _shaftBrush.EndPoint = new Vector2(0.88f, 0.92f);
            _shaftBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(62, 255, 224, 174)));
            _shaftBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.24f, Color.FromArgb(34, 238, 184, 152)));
            _shaftBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.56f, Color.FromArgb(14, 146, 174, 224)));
            _shaftBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 11, 11, 13)));
            _shaftVisual = CreateLayer(_shaftBrush, 0.65f);

            // 2. Irregular warm solar key lobe (ultra-soft Gaussian-like 5-stop decay — zero edge!)
            _keyBrush = _compositor.CreateRadialGradientBrush();
            _keyBrush.MappingMode = CompositionMappingMode.Absolute;
            _keyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(92, 255, 214, 156)));
            _keyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.22f, Color.FromArgb(56, 232, 166, 138)));
            _keyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.48f, Color.FromArgb(24, 162, 136, 168)));
            _keyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.76f, Color.FromArgb(6, 98, 120, 166)));
            _keyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 11, 11, 13)));
            _keyVisual = CreateLayer(_keyBrush, 0.85f);

            // 3. Refracted prismatic glass caustic ribbon (asymmetric off-center origin, responds to beat)
            _causticBrush = _compositor.CreateRadialGradientBrush();
            _causticBrush.MappingMode = CompositionMappingMode.Absolute;
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(74, 255, 238, 206)));
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.20f, Color.FromArgb(44, 236, 176, 164)));
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.46f, Color.FromArgb(18, 156, 192, 240)));
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.74f, Color.FromArgb(4, 112, 152, 214)));
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 11, 11, 13)));
            _causticVisual = CreateLayer(_causticBrush, 0.68f);

            // 4. Cool atmospheric sky counter-wash (lower-right ambient skylight fill)
            _skyBrush = _compositor.CreateRadialGradientBrush();
            _skyBrush.MappingMode = CompositionMappingMode.Absolute;
            _skyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(66, 114, 154, 224)));
            _skyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.30f, Color.FromArgb(34, 92, 126, 194)));
            _skyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.62f, Color.FromArgb(10, 68, 92, 146)));
            _skyBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 11, 11, 13)));
            _skyVisual = CreateLayer(_skyBrush, 0.82f);

            // 5. Cover-side warm ambient diffuse halo
            _haloBrush = _compositor.CreateRadialGradientBrush();
            _haloBrush.MappingMode = CompositionMappingMode.Absolute;
            _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(52, 246, 184, 136)));
            _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.34f, Color.FromArgb(20, 196, 134, 126)));
            _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.72f, Color.FromArgb(4, 96, 112, 156)));
            _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 11, 11, 13)));
            _haloVisual = CreateLayer(_haloBrush, 0.62f);

            _root.Children.InsertAtTop(_shaftVisual);
            _root.Children.InsertAtTop(_skyVisual);
            _root.Children.InsertAtTop(_haloVisual);
            _root.Children.InsertAtTop(_keyVisual);
            _root.Children.InsertAtTop(_causticVisual);

            _keyCenterAnim = CreateVec2Anim(280);
            _keyRadiusAnim = CreateVec2Anim(260);
            _causticCenterAnim = CreateVec2Anim(240);
            _causticRadiusAnim = CreateVec2Anim(220);
            _skyCenterAnim = CreateVec2Anim(340);
            _shaftOpacityAnim = CreateScalarAnim(260);
            _causticOpacityAnim = CreateScalarAnim(210);
            _haloOpacityAnim = CreateScalarAnim(240);

            ElementCompositionPreview.SetElementChildVisual(_host, _root);
            SetRestState();
        }

        private ShapeVisual CreateLayer(CompositionBrush brush, float opacity)
        {
            var shape = _compositor.CreateSpriteShape(_rect);
            shape.FillBrush = brush;
            var visual = _compositor.CreateShapeVisual();
            visual.Shapes.Add(shape);
            visual.Opacity = opacity;
            return visual;
        }

        private Vector2KeyFrameAnimation CreateVec2Anim(int ms)
        {
            var anim = _compositor.CreateVector2KeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(ms);
            return anim;
        }

        private ScalarKeyFrameAnimation CreateScalarAnim(int ms)
        {
            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(ms);
            return anim;
        }

        internal void Resize(float width, float height)
        {
            if (width <= 1f || height <= 1f) return;
            _width = width;
            _height = height;
            var size = new Vector2(width, height);
            _root.Size = size;
            _rect.Size = size;
            _shaftVisual.Size = size;
            _keyVisual.Size = size;
            _causticVisual.Size = size;
            _skyVisual.Size = size;
            _haloVisual.Size = size;

            _haloBrush.EllipseCenter = new Vector2(width * 0.24f, height * 0.44f);
            _haloBrush.EllipseRadius = new Vector2(width * 0.44f, height * 0.56f);
            if (_keyBrush.EllipseRadius == Vector2.Zero) SetRestState();
        }

        internal void SetRestState()
        {
            var w = Math.Max(600f, _width);
            var h = Math.Max(400f, _height);
            _keyBrush.EllipseCenter = new Vector2(w * 0.26f, h * 0.22f);
            _keyBrush.EllipseRadius = new Vector2(w * 0.62f, h * 0.68f);
            _keyBrush.GradientOriginOffset = new Vector2(-w * 0.14f, -h * 0.10f);

            _causticBrush.EllipseCenter = new Vector2(w * 0.62f, h * 0.46f);
            _causticBrush.EllipseRadius = new Vector2(w * 0.56f, h * 0.44f);
            _causticBrush.GradientOriginOffset = new Vector2(w * 0.12f, -h * 0.08f);

            _skyBrush.EllipseCenter = new Vector2(w * 0.78f, h * 0.78f);
            _skyBrush.EllipseRadius = new Vector2(w * 0.64f, h * 0.66f);

            _shaftVisual.Opacity = 0.58f;
            _causticVisual.Opacity = 0.52f;
            _haloVisual.Opacity = 0.54f;
        }

        internal Vector2 Update(float w, float h, double t, float driftX, float driftY, float beat, float solarPower)
        {
            if (MathF.Abs(w - _width) > 1f || MathF.Abs(h - _height) > 1f) Resize(w, h);

            // Dominant solar key point drifts irregularly across upper-left to upper-center
            var keyCenter = new Vector2(
                w * (0.30f + driftX * 0.24f),
                h * (0.24f + driftY * 0.18f - 0.03f * beat));
            var keyRadius = new Vector2(
                w * (0.60f + 0.10f * beat + 0.05f * MathF.Sin((float)t * 0.31f)),
                h * (0.66f + 0.08f * beat + 0.04f * MathF.Cos((float)t * 0.27f)));
            _keyBrush.GradientOriginOffset = new Vector2(
                -w * (0.12f + 0.05f * driftX),
                -h * (0.09f + 0.04f * driftY));

            // Asymmetric refracted caustic lobe wanders counter-phase through lyrics & mid-field
            var causticCenter = new Vector2(
                w * (0.60f - driftX * 0.19f + 0.06f * MathF.Sin((float)t * 0.53f)),
                h * (0.48f - driftY * 0.15f + 0.04f * beat));
            var causticRadius = new Vector2(
                w * (0.54f + 0.14f * beat),
                h * (0.38f + 0.06f * MathF.Sin((float)t * 0.43f) + 0.09f * beat));
            _causticBrush.GradientOriginOffset = new Vector2(
                w * (0.14f * MathF.Cos((float)t * 0.23f)),
                -h * (0.08f + 0.04f * beat));

            // Cool atmospheric sky fill shifts gently opposite the sun
            var skyCenter = new Vector2(
                w * (0.76f - driftX * 0.14f),
                h * (0.76f - driftY * 0.12f));
            _skyBrush.EllipseRadius = new Vector2(w * 0.66f, h * 0.68f);

            _keyCenterAnim.InsertKeyFrame(1f, keyCenter, _ease);
            _keyBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseCenter), _keyCenterAnim);

            _keyRadiusAnim.InsertKeyFrame(1f, keyRadius, _ease);
            _keyBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseRadius), _keyRadiusAnim);

            _causticCenterAnim.InsertKeyFrame(1f, causticCenter, _ease);
            _causticBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseCenter), _causticCenterAnim);

            _causticRadiusAnim.InsertKeyFrame(1f, causticRadius, _ease);
            _causticBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseRadius), _causticRadiusAnim);

            _skyCenterAnim.InsertKeyFrame(1f, skyCenter, _ease);
            _skyBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseCenter), _skyCenterAnim);

            _shaftOpacityAnim.InsertKeyFrame(1f, Math.Clamp(0.52f + 0.28f * solarPower, 0.35f, 0.88f), _ease);
            _shaftVisual.StartAnimation(nameof(Visual.Opacity), _shaftOpacityAnim);

            _causticOpacityAnim.InsertKeyFrame(1f, Math.Clamp(0.46f + 0.28f * solarPower + 0.24f * beat, 0.35f, 0.96f), _ease);
            _causticVisual.StartAnimation(nameof(Visual.Opacity), _causticOpacityAnim);

            _haloOpacityAnim.InsertKeyFrame(1f, Math.Clamp(0.48f + 0.26f * beat, 0.42f, 0.84f), _ease);
            _haloVisual.StartAnimation(nameof(Visual.Opacity), _haloOpacityAnim);

            // Weighted world-space luminous centroid for diffuse glass reflections on UI controls
            return Vector2.Lerp(keyCenter, causticCenter, 0.32f + 0.15f * beat);
        }

        public void Dispose()
        {
            try { ElementCompositionPreview.SetElementChildVisual(_host, null); } catch { }
            _keyCenterAnim.Dispose();
            _keyRadiusAnim.Dispose();
            _causticCenterAnim.Dispose();
            _causticRadiusAnim.Dispose();
            _skyCenterAnim.Dispose();
            _shaftOpacityAnim.Dispose();
            _causticOpacityAnim.Dispose();
            _haloOpacityAnim.Dispose();
        }
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
        var level = MathF.Sqrt(energy / Math.Max(1, samples.Length / 2));
        _average += 0.12f * (level - _average);
        var onset = Math.Clamp((level - _average * 1.15f) * 20f, 0, 1);
        Volatile.Write(ref _beat, Math.Max(onset, _beat * 0.64f));
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
        var artistText = string.Join(" · ", new[] { data.Artist, data.Album }.Where(value => !string.IsNullOrWhiteSpace(value)));
        TrackArtist.Text = artistText;
        TrackArtist.Visibility = string.IsNullOrWhiteSpace(artistText) ? Visibility.Collapsed : Visibility.Visible;
        _lines = MusicMetadataService.ParseLyrics(data.Lyrics);
        _line = -1;
        Lyrics.ItemsSource = _lines;
        var padding = _lines.FirstOrDefault()?.TimeMs is null ? 0 : Math.Max(0, Lyrics.ActualHeight * 0.46 - 24);
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
            ArtBrush.ImageSource = image;
            CoverFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (!token.IsCancellationRequested) SetMetadataStatus("无法读取歌曲封面。");
        }
    }

    private async void OnMatch(object sender, RoutedEventArgs e)
    {
        if (_track is not { } track || _presentation is not { } current || XamlRoot is null) return;
        var token = track.Token;
        if (current.Cover is not null && current.Lyrics is not null)
        {
            SetMetadataStatus("当前歌曲已有封面和歌词。");
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
            SetMetadataStatus("正在匹配…");
            var match = await _metadata.MatchAsync(current with { Title = title.Text.Trim() }, artist.Text.Trim(),
                _player.Service.LastSnapshot.LengthMs, token);
            var latest = _presentation ?? current;
            await ApplyPresentationAsync(match with { Cover = latest.Cover ?? match.Cover, Lyrics = latest.Lyrics ?? match.Lyrics }, token);
            if (!token.IsCancellationRequested)
                SetMetadataStatus(current.Cover != match.Cover || current.Lyrics != match.Lyrics ? "在线匹配完成。" : "无匹配结果，请核对曲名和歌手。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) SetMetadataStatus("在线匹配失败。 " + ex.Message); }
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
        catch (Exception ex) { if (!token.IsCancellationRequested) SetMetadataStatus("导入失败。 " + ex.Message); }
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        _solarField?.Resize((float)e.NewSize.Width, (float)e.NewSize.Height);
        var narrow = e.NewSize.Width < 700;
        TrackLayout.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 3, GridUnitType.Star);
        TrackLayout.RowDefinitions[0].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TrackLayout.RowDefinitions[1].Height = new GridLength(narrow ? 1 : 0, GridUnitType.Star);
        Grid.SetRow(LyricPanel, narrow ? 1 : 0);
        Grid.SetColumn(LyricPanel, narrow ? 0 : 1);
        CoverSurface.Width = CoverSurface.Height = narrow
            ? Math.Clamp(e.NewSize.Height * 0.24, 84, 156)
            : Math.Clamp(Math.Min(e.NewSize.Height * 0.44, e.NewSize.Width * 0.28), 160, 296);
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.Window.GoBack();

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_player.Volume == 0) _player.Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 85;
        else
        {
            _volumeBeforeMute = _player.Volume;
            _player.Volume = 0;
        }
    }

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

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingClock || _track is null) return;
        if (_dragging) _player.PreviewSeek(e.NewValue);
        else _player.EndSeek(e.NewValue);
    }
}
