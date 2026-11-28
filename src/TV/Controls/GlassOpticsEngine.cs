using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using TV.Services;
using TV.Views;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TV.Controls;

public enum GlassOpticsMode
{
    Off = 0,
    Interactive = 1,
    Ambient = 2
}

public enum GlassOpticsProfile
{
    Standard = 0,
    Cinema = 1,
    Rail = 2,
    Card = 3,
    Control = 4
}

/// <summary>
/// Dedicated GPU-composited optical glass light &amp; shadow engine (SHIRO · MA · OPTICAL GLASS).
/// Simulates physical optical glass phenomena entirely on WinUI 3's DWM Compositor thread:
///   1. Volumetric specular transmission + Snell's Law perspective lateral refraction shift
///   2. Schlick Fresnel chamfered rim reflection with subtle chromatic dispersion (cool-ice / warm-caustic)
///   3. Sliding top-bevel total-internal-reflection caustic catchlight tracking pointer glide
///   4. Expanding click caustic refraction shockwave on pointer press
/// Strictly maintains 0% idle GPU/CPU cost (no perpetual timers/loops), SwapChainPanel safety
/// (zero ThemeShadow, zero extra AcrylicBrush), 120Hz pointer throttling, and HighContrast + MotionService compliance.
/// </summary>
public static class GlassOpticsEngine
{
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly ConditionalWeakTable<FrameworkElement, OpticsSession> Sessions = new();

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(GlassOpticsMode),
            typeof(GlassOpticsEngine),
            new PropertyMetadata(GlassOpticsMode.Off, OnOpticsPropertyChanged));

    public static readonly DependencyProperty ProfileProperty =
        DependencyProperty.RegisterAttached(
            "Profile",
            typeof(GlassOpticsProfile),
            typeof(GlassOpticsEngine),
            new PropertyMetadata(GlassOpticsProfile.Standard, OnOpticsPropertyChanged));

    public static readonly DependencyProperty RadiusOverrideProperty =
        DependencyProperty.RegisterAttached(
            "RadiusOverride",
            typeof(double),
            typeof(GlassOpticsEngine),
            new PropertyMetadata(-1.0, OnOpticsPropertyChanged));

    public static GlassOpticsMode GetMode(DependencyObject obj) => (GlassOpticsMode)obj.GetValue(ModeProperty);
    public static void SetMode(DependencyObject obj, GlassOpticsMode value) => obj.SetValue(ModeProperty, value);

    public static GlassOpticsProfile GetProfile(DependencyObject obj) => (GlassOpticsProfile)obj.GetValue(ProfileProperty);
    public static void SetProfile(DependencyObject obj, GlassOpticsProfile value) => obj.SetValue(ProfileProperty, value);

    public static double GetRadiusOverride(DependencyObject obj) => (double)obj.GetValue(RadiusOverrideProperty);
    public static void SetRadiusOverride(DependencyObject obj, double value) => obj.SetValue(RadiusOverrideProperty, value);

    private static void OnOpticsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        var mode = GetMode(element);
        if (mode == GlassOpticsMode.Off)
        {
            if (Sessions.TryGetValue(element, out var existing))
            {
                existing.Detach();
                Sessions.Remove(element);
            }
            return;
        }
        AttachOrUpdate(element, element, mode, GetProfile(element), GetRadiusOverride(element));
    }

    /// <summary>
    /// Attaches optical glass rendering where pointer events are observed on <paramref name="pointerHost"/>
    /// and GPU Composition visuals are hosted inside <paramref name="visualHost"/> (which may be a dedicated
    /// non-hit-testable template layer such as GlassSurface's OpticsHost).
    /// </summary>
    public static void AttachOrUpdate(
        FrameworkElement pointerHost,
        FrameworkElement visualHost,
        GlassOpticsMode mode,
        GlassOpticsProfile profile = GlassOpticsProfile.Standard,
        double radiusOverride = -1.0)
    {
        if (mode == GlassOpticsMode.Off)
        {
            if (Sessions.TryGetValue(pointerHost, out var existing))
            {
                existing.Detach();
                Sessions.Remove(pointerHost);
            }
            return;
        }

        if (Sessions.TryGetValue(pointerHost, out var session))
        {
            session.UpdateConfig(visualHost, mode, profile, radiusOverride);
        }
        else
        {
            var created = new OpticsSession(pointerHost, visualHost, mode, profile, radiusOverride);
            Sessions.Add(pointerHost, created);
        }
    }

    public static void NotifyGeometryChanged(FrameworkElement pointerHost)
    {
        if (Sessions.TryGetValue(pointerHost, out var session))
            session.SyncGeometry();
    }

    internal static void SetIncidentLight(FrameworkElement host, Vector2 direction, float illuminance)
    {
        if (Sessions.TryGetValue(host, out var session)) session.SetIncidentLight(direction, illuminance);
    }

    private static float Fresnel(float cosine)
    {
        const float reflectanceAtNormal = 0.04f;
        return reflectanceAtNormal + (1f - reflectanceAtNormal) * MathF.Pow(1f - Math.Clamp(cosine, 0f, 1f), 5f);
    }

    public static void Detach(FrameworkElement pointerHost)
    {
        if (Sessions.TryGetValue(pointerHost, out var session))
        {
            session.Detach();
            Sessions.Remove(pointerHost);
        }
    }

    private sealed class OpticsSession
    {
        private readonly FrameworkElement _pointerHost;
        private FrameworkElement _visualHost;
        private GlassOpticsMode _mode;
        private GlassOpticsProfile _profile;
        private double _radiusOverride;

        private Compositor? _compositor;
        private ContainerVisual? _rootVisual;
        private ContainerVisual? _hoverVisualGroup;

        private ShapeVisual? _transmissionVisual;
        private CompositionRoundedRectangleGeometry? _transmissionGeometry;
        private CompositionRadialGradientBrush? _transmissionBrush;

        private ShapeVisual? _fresnelRimVisual;
        private CompositionRoundedRectangleGeometry? _rimGeometry;
        private CompositionRadialGradientBrush? _rimBrush;

        private ShapeVisual? _causticRidgeVisual;
        private CompositionRectangleGeometry? _causticGeometry;
        private CompositionRadialGradientBrush? _causticBrush;

        private ShapeVisual? _rippleVisual;
        private CompositionRoundedRectangleGeometry? _rippleGeometry;
        private CompositionRadialGradientBrush? _rippleBrush;
        private ShapeVisual? _incidentVisual;
        private CompositionRadialGradientBrush? _incidentBrush;

        private CubicBezierEasingFunction? _easeOutExpo;
        private CubicBezierEasingFunction? _easeOutQuint;

        private readonly PointerEventHandler _movedHandler;
        private readonly PointerEventHandler _enteredHandler;
        private readonly PointerEventHandler _exitedHandler;
        private readonly PointerEventHandler _pressedHandler;

        private bool _wired;
        private bool _watchingMotion;
        private bool _visualsReady;
        private bool _visualsUnavailable;
        private bool _isHovering;
        private long _lastMoveTick;
        private float _lastX = -1000f;
        private float _lastY = -1000f;
        private float _width;
        private float _height;

        public OpticsSession(
            FrameworkElement pointerHost,
            FrameworkElement visualHost,
            GlassOpticsMode mode,
            GlassOpticsProfile profile,
            double radiusOverride)
        {
            _pointerHost = pointerHost;
            _visualHost = visualHost;
            _mode = mode;
            _profile = profile;
            _radiusOverride = radiusOverride;

            _movedHandler = OnPointerMoved;
            _enteredHandler = OnPointerEntered;
            _exitedHandler = OnPointerExited;
            _pressedHandler = OnPointerPressed;

            Wire();
            if (_pointerHost.IsLoaded) WatchMotion();
        }

        public void UpdateConfig(FrameworkElement visualHost, GlassOpticsMode mode, GlassOpticsProfile profile, double radiusOverride)
        {
            var hostChanged = !ReferenceEquals(_visualHost, visualHost);
            if (hostChanged)
            {
                ClearChildVisual();
                _visualHost.SizeChanged -= OnVisualSizeChanged;
                _visualHost = visualHost;
                _visualHost.SizeChanged += OnVisualSizeChanged;
                _visualsUnavailable = false;
            }
            _mode = mode;
            _profile = profile;
            _radiusOverride = radiusOverride;
            if (_visualsReady)
            {
                ApplyPalette();
                SyncGeometry();
                OnMotionChanged(this, EventArgs.Empty);
            }
        }

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            _pointerHost.Loaded += OnLoaded;
            _pointerHost.Unloaded += OnUnloaded;
            _pointerHost.ActualThemeChanged += OnThemeChanged;
            _visualHost.SizeChanged += OnVisualSizeChanged;

            // Use handledEventsToo: true so optical light responds seamlessly even when child buttons/controls handle clicks
            _pointerHost.AddHandler(UIElement.PointerEnteredEvent, _enteredHandler, handledEventsToo: true);
            _pointerHost.AddHandler(UIElement.PointerMovedEvent, _movedHandler, handledEventsToo: true);
            _pointerHost.AddHandler(UIElement.PointerExitedEvent, _exitedHandler, handledEventsToo: true);
            _pointerHost.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, handledEventsToo: true);
        }

        public void Detach()
        {
            if (!_wired) return;
            _wired = false;
            _pointerHost.Loaded -= OnLoaded;
            _pointerHost.Unloaded -= OnUnloaded;
            _pointerHost.ActualThemeChanged -= OnThemeChanged;
            _visualHost.SizeChanged -= OnVisualSizeChanged;

            _pointerHost.RemoveHandler(UIElement.PointerEnteredEvent, _enteredHandler);
            _pointerHost.RemoveHandler(UIElement.PointerMovedEvent, _movedHandler);
            _pointerHost.RemoveHandler(UIElement.PointerExitedEvent, _exitedHandler);
            _pointerHost.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
            StopWatchingMotion();
            ClearChildVisual();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => WatchMotion();

        private void WatchMotion()
        {
            if (_watchingMotion) return;
            MotionService.Changed += OnMotionChanged;
            _watchingMotion = true;
        }

        private void StopWatchingMotion()
        {
            if (!_watchingMotion) return;
            MotionService.Changed -= OnMotionChanged;
            _watchingMotion = false;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopWatchingMotion();
            ClearChildVisual();
        }

        private void OnThemeChanged(FrameworkElement sender, object args) => ApplyPalette();

        private void OnMotionChanged(object? sender, EventArgs e)
        {
            if (!IsActive && _hoverVisualGroup is not null)
            {
                _isHovering = false;
                _hoverVisualGroup.StopAnimation(nameof(Visual.Opacity));
                _hoverVisualGroup.Opacity = 0f;
                if (_rippleVisual is not null)
                {
                    _rippleVisual.StopAnimation(nameof(Visual.Opacity));
                    _rippleVisual.Opacity = 0f;
                }
            }
            if (!IsActive && _incidentVisual is not null)
            {
                _incidentVisual.StopAnimation(nameof(Visual.Opacity));
                _incidentVisual.Opacity = 0;
            }
        }

        internal void SetIncidentLight(Vector2 direction, float illuminance)
        {
            if (illuminance <= 0 && _incidentVisual is null || !TryEnsureVisuals() || _width <= 1 || _height <= 1) return;
            var compositor = _compositor!;
            if (_incidentVisual is null)
            {
                _incidentBrush = compositor.CreateRadialGradientBrush();
                _incidentBrush.MappingMode = CompositionMappingMode.Absolute;
                _incidentBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(70, 255, 239, 208)));
                _incidentBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.42f, Color.FromArgb(35, 202, 220, 251)));
                _incidentBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 202, 220, 251)));
                var shape = compositor.CreateSpriteShape(_transmissionGeometry);
                shape.FillBrush = _incidentBrush;
                shape.StrokeBrush = _incidentBrush;
                shape.StrokeThickness = 0.85f;
                _incidentVisual = compositor.CreateShapeVisual();
                _incidentVisual.Shapes.Add(shape);
                _incidentVisual.Opacity = 0;
                _rootVisual!.Children.InsertAtBottom(_incidentVisual);
            }

            _incidentVisual.Size = new Vector2(_width, _height);
            var radius = Math.Clamp(_width * 0.39f, 68f, 330f);
            _incidentBrush!.EllipseRadius = new Vector2(radius, Math.Clamp(_height * 0.98f, 46f, 210f));
            var incidence = 0.62f / MathF.Sqrt(0.3844f + direction.LengthSquared());
            var strength = Math.Clamp(illuminance * (0.60f + 1.3f * Fresnel(incidence)), 0, 0.95f);
            using var center = compositor.CreateVector2KeyFrameAnimation();
            center.InsertKeyFrame(1, new Vector2((0.5f + direction.X * 0.42f) * _width, (0.18f + direction.Y * 0.22f) * _height), _easeOutQuint!);
            center.Duration = TimeSpan.FromMilliseconds(210);
            _incidentBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseCenter), center);
            using var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1, strength, _easeOutQuint!);
            opacity.Duration = TimeSpan.FromMilliseconds(210);
            _incidentVisual.StartAnimation(nameof(Visual.Opacity), opacity);
        }

        private void OnVisualSizeChanged(object sender, SizeChangedEventArgs e) => SyncGeometry();

        private bool IsActive =>
            _mode == GlassOpticsMode.Interactive &&
            !Accessibility.HighContrast &&
            MotionService.CanAnimate;

        private bool TryEnsureVisuals()
        {
            if (_visualsUnavailable || !IsActive || !_visualHost.IsLoaded) return false;
            if (_visualsReady) return true;
            try
            {
                CreateVisuals();
                return true;
            }
            catch (Exception ex) when (ex is COMException or NotSupportedException)
            {
                _visualsUnavailable = true;
                ClearChildVisual();
                Diagnostics.Log($"玻璃光效不可用，保留基础样式: {ex.HResult:X8}");
                return false;
            }
        }

        private void CreateVisuals()
        {
            var hostVisual = ElementCompositionPreview.GetElementVisual(_visualHost);
            var c = hostVisual.Compositor;
            _compositor = c;
            _easeOutExpo = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));
            _easeOutQuint = c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

            _rootVisual = c.CreateContainerVisual();
            _rootVisual.Clip = c.CreateInsetClip();
            _hoverVisualGroup = c.CreateContainerVisual();
            _hoverVisualGroup.Opacity = 0f;

            // Layer 1: Volumetric specular transmission + Snell lateral refraction
            _transmissionGeometry = c.CreateRoundedRectangleGeometry();
            _transmissionBrush = c.CreateRadialGradientBrush();
            _transmissionBrush.MappingMode = CompositionMappingMode.Absolute;
            var transShape = c.CreateSpriteShape(_transmissionGeometry);
            transShape.FillBrush = _transmissionBrush;
            _transmissionVisual = c.CreateShapeVisual();
            _transmissionVisual.Shapes.Add(transShape);

            // Layer 2: Schlick Fresnel chamfered edge refraction + chromatic dispersion
            _rimGeometry = c.CreateRoundedRectangleGeometry();
            _rimBrush = c.CreateRadialGradientBrush();
            _rimBrush.MappingMode = CompositionMappingMode.Absolute;
            var rimShape = c.CreateSpriteShape(_rimGeometry);
            rimShape.StrokeBrush = _rimBrush;
            rimShape.StrokeThickness = _profile == GlassOpticsProfile.Cinema ? 1.45f : 1.3f;
            _fresnelRimVisual = c.CreateShapeVisual();
            _fresnelRimVisual.Shapes.Add(rimShape);

            // Layer 3: Dynamic top-bevel total-internal-reflection caustic ridge
            _causticGeometry = c.CreateRectangleGeometry();
            _causticBrush = c.CreateRadialGradientBrush();
            _causticBrush.MappingMode = CompositionMappingMode.Absolute;
            var causticShape = c.CreateSpriteShape(_causticGeometry);
            causticShape.FillBrush = _causticBrush;
            _causticRidgeVisual = c.CreateShapeVisual();
            _causticRidgeVisual.Shapes.Add(causticShape);

            _hoverVisualGroup.Children.InsertAtTop(_transmissionVisual);
            _hoverVisualGroup.Children.InsertAtTop(_fresnelRimVisual);
            _hoverVisualGroup.Children.InsertAtTop(_causticRidgeVisual);

            // Layer 4: Expanding click caustic refraction shockwave
            _rippleGeometry = c.CreateRoundedRectangleGeometry();
            _rippleBrush = c.CreateRadialGradientBrush();
            _rippleBrush.MappingMode = CompositionMappingMode.Absolute;
            _rippleBrush.EllipseRadius = new Vector2(10f, 10f);
            var rippleShape = c.CreateSpriteShape(_rippleGeometry);
            rippleShape.FillBrush = _rippleBrush;
            _rippleVisual = c.CreateShapeVisual();
            _rippleVisual.Opacity = 0f;
            _rippleVisual.Shapes.Add(rippleShape);

            _rootVisual.Children.InsertAtTop(_hoverVisualGroup);
            _rootVisual.Children.InsertAtTop(_rippleVisual);

            ElementCompositionPreview.SetElementChildVisual(_visualHost, _rootVisual);
            _visualsReady = true;
            ApplyPalette();
            SyncGeometry();
        }

        private void ClearChildVisual()
        {
            if (_visualsReady)
            {
                try { ElementCompositionPreview.SetElementChildVisual(_visualHost, null); } catch { }
            }
            _visualsReady = false;
            _isHovering = false;
            _lastX = _lastY = -1000f;
            _width = _height = 0f;
            _compositor = null;
            _easeOutExpo = _easeOutQuint = null;
            _rootVisual = null;
            _hoverVisualGroup = null;
            _transmissionVisual = null;
            _transmissionGeometry = null;
            _transmissionBrush = null;
            _fresnelRimVisual = null;
            _rimGeometry = null;
            _rimBrush = null;
            _causticRidgeVisual = null;
            _causticGeometry = null;
            _causticBrush = null;
            _rippleVisual = null;
            _rippleGeometry = null;
            _rippleBrush = null;
            _incidentVisual = null;
            _incidentBrush = null;
        }

        public void SyncGeometry()
        {
            if (!_visualsReady || _rootVisual is null) return;
            var w = (float)_visualHost.ActualWidth;
            var h = (float)_visualHost.ActualHeight;
            if (w <= 1f || h <= 1f) return;
            _width = w;
            _height = h;

            var size = new Vector2(w, h);
            _rootVisual.Size = size;
            _hoverVisualGroup!.Size = size;
            _transmissionVisual!.Size = size;
            _fresnelRimVisual!.Size = size;
            _causticRidgeVisual!.Size = size;
            _rippleVisual!.Size = size;

            var r = ResolveCornerRadius(w, h);
            var corner = new Vector2(r, r);

            _transmissionGeometry!.Offset = Vector2.Zero;
            _transmissionGeometry.Size = size;
            _transmissionGeometry.CornerRadius = corner;

            var inset = 0.75f;
            var rimSize = new Vector2(Math.Max(1f, w - inset * 2f), Math.Max(1f, h - inset * 2f));
            var rimCorner = Math.Max(0f, r - inset);
            _rimGeometry!.Offset = new Vector2(inset, inset);
            _rimGeometry.Size = rimSize;
            _rimGeometry.CornerRadius = new Vector2(rimCorner, rimCorner);

            var causticInset = Math.Clamp(r * 0.65f, 4f, Math.Max(4f, w * 0.25f));
            _causticGeometry!.Offset = new Vector2(causticInset, 0.5f);
            _causticGeometry.Size = new Vector2(Math.Max(2f, w - causticInset * 2f), 1.25f);

            _rippleGeometry!.Offset = Vector2.Zero;
            _rippleGeometry.Size = size;
            _rippleGeometry.CornerRadius = corner;

            // Calibrate optical radii to glass surface dimensions
            var transRadius = Math.Clamp(MathF.Max(w, h) * 0.42f, 90f, 290f);
            _transmissionBrush!.EllipseRadius = new Vector2(transRadius, transRadius * 0.88f);

            var rimRadius = Math.Clamp(MathF.Min(w, h) * 0.92f, 76f, 220f);
            _rimBrush!.EllipseRadius = new Vector2(rimRadius, rimRadius);

            var causticSpan = Math.Clamp(w * 0.34f, 42f, 165f);
            _causticBrush!.EllipseRadius = new Vector2(causticSpan, 18f);

            if (_lastX < 0 || _lastY < 0)
            {
                UpdateLightOrigin(w * 0.5f, h * 0.35f);
            }
        }

        private float ResolveCornerRadius(float w, float h)
        {
            double raw = _radiusOverride >= 0 ? _radiusOverride : -1;
            if (raw < 0)
            {
                if (_pointerHost is GlassSurface gs)
                    raw = Math.Max(0, gs.CornerRadius.TopLeft - 1);
                else if (_pointerHost is Control ctrl)
                    raw = ctrl.CornerRadius.TopLeft;
                else if (_pointerHost is Border border)
                    raw = border.CornerRadius.TopLeft;
                else if (_visualHost is Border vBorder)
                    raw = vBorder.CornerRadius.TopLeft;
                else
                    raw = 12;
            }
            return Math.Clamp((float)raw, 0f, MathF.Min(w, h) * 0.5f);
        }

        private void ApplyPalette()
        {
            if (!_visualsReady || _compositor is null) return;
            var isLight = _pointerHost.ActualTheme == ElementTheme.Light && _profile != GlassOpticsProfile.Cinema;

            var scale = _profile switch
            {
                GlassOpticsProfile.Cinema => 1.15f,
                GlassOpticsProfile.Control => 1.08f,
                GlassOpticsProfile.Card => 0.96f,
                GlassOpticsProfile.Rail => 0.85f,
                _ => 1.0f
            };
            byte ScaleA(byte a) => (byte)Math.Clamp((int)MathF.Round(a * scale), 0, 255);

            // 1. Volumetric Specular Transmission + subtle chromatic refraction dispersion
            _transmissionBrush!.ColorStops.Clear();
            if (isLight)
            {
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(58), 255, 255, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.32f, Color.FromArgb(ScaleA(28), 232, 244, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.65f, Color.FromArgb(ScaleA(14), 255, 238, 224)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(34), 255, 255, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.34f, Color.FromArgb(ScaleA(16), 186, 220, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(ScaleA(9), 255, 214, 198)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }

            // 2. Schlick Fresnel Chamfer Rim + Prismatic Grazing Dispersion
            _rimBrush!.ColorStops.Clear();
            if (isLight)
            {
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(225), 255, 255, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.35f, Color.FromArgb(ScaleA(120), 178, 214, 252)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.66f, Color.FromArgb(ScaleA(64), 236, 152, 134)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(165), 255, 255, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.36f, Color.FromArgb(ScaleA(86), 174, 214, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(ScaleA(46), 245, 166, 146)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }

            // 3. Top-bevel traveling caustic catchlight
            _causticBrush!.ColorStops.Clear();
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(isLight ? ScaleA(220) : ScaleA(170), 255, 255, 255)));
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, Color.FromArgb(isLight ? ScaleA(90) : ScaleA(68), 210, 232, 255)));
            _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

            // 4. Click caustic refraction ring (hollow center -> crisp prism crest -> warm tail)
            _rippleBrush!.ColorStops.Clear();
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.46f, Color.FromArgb(isLight ? (byte)16 : (byte)10, 200, 228, 255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.76f, Color.FromArgb(isLight ? ScaleA(110) : ScaleA(66), 255, 255, 255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.89f, Color.FromArgb(isLight ? ScaleA(42) : ScaleA(28), 232, 118, 98)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!TryEnsureVisuals()) return;
            if (_width <= 1f || _height <= 1f) SyncGeometry();
            _isHovering = true;
            var pt = e.GetCurrentPoint(_visualHost).Position;
            UpdateLightOrigin((float)pt.X, (float)pt.Y);
            AnimateHoverOpacity(1.0f, 180, _easeOutQuint!);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var now = Environment.TickCount64;
            if (now - _lastMoveTick < 8) return; // 120Hz cap — zero stutter on 1000Hz gaming mice
            if (!TryEnsureVisuals()) return;
            if (_width <= 1f || _height <= 1f) SyncGeometry();

            var pt = e.GetCurrentPoint(_visualHost).Position;
            var x = (float)pt.X;
            var y = (float)pt.Y;
            if (MathF.Abs(x - _lastX) < 1.25f && MathF.Abs(y - _lastY) < 1.25f) return;
            _lastMoveTick = now;

            if (!_isHovering)
            {
                _isHovering = true;
                AnimateHoverOpacity(1.0f, 160, _easeOutQuint!);
            }
            UpdateLightOrigin(x, y);
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_visualsReady || !_isHovering) return;
            _isHovering = false;
            AnimateHoverOpacity(0f, 320, _easeOutExpo!);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(_visualHost).Properties.IsLeftButtonPressed || !TryEnsureVisuals()) return;
            if (_width <= 1f || _height <= 1f) SyncGeometry();
            var pt = e.GetCurrentPoint(_visualHost).Position;
            var x = (float)pt.X;
            var y = (float)pt.Y;
            UpdateLightOrigin(x, y);
            TriggerCausticRipple(x, y);
        }

        private void UpdateLightOrigin(float x, float y)
        {
            _lastX = x;
            _lastY = y;
            if (!_visualsReady) return;

            var pos = new Vector2(x, y);
            _transmissionBrush!.EllipseCenter = pos;
            _rimBrush!.EllipseCenter = pos;

            // Snell's Law optical slab parallax: shift internal specular origin slightly counter to pointer angle
            if (_width > 1f && _height > 1f)
            {
                var lateral = new Vector2(x * 2 / _width - 1, y * 2 / _height - 1);
                var normal = 0.65f / MathF.Sqrt(0.4225f + lateral.LengthSquared());
                var transmitted = MathF.Sqrt(1f - (1f - normal * normal) / 2.25f);
                var bend = (1f - normal / transmitted) * 32f;
                _transmissionBrush.GradientOriginOffset = -lateral * bend;
                _rimBrush.GradientOriginOffset = lateral * (4f + 30f * Fresnel(normal));
                _fresnelRimVisual!.Opacity = Math.Clamp(0.54f + 2.9f * Fresnel(normal), 0, 1);
                _causticRidgeVisual!.Opacity = Math.Clamp(0.30f + (1f - y / _height) * 0.65f, 0, 1);
            }

            // Traveling top-bevel caustic tracks pointer X smoothly
            _causticBrush!.EllipseCenter = new Vector2(x, 0.5f);
        }

        private void AnimateHoverOpacity(float target, int durationMs, CompositionEasingFunction easing)
        {
            if (_compositor is null || _hoverVisualGroup is null) return;
            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1.0f, target, easing);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            _hoverVisualGroup.StartAnimation(nameof(Visual.Opacity), anim);
        }

        private void TriggerCausticRipple(float x, float y)
        {
            if (_compositor is null || _rippleBrush is null || _rippleVisual is null) return;
            var maxDistX = MathF.Max(x, _width - x);
            var maxDistY = MathF.Max(y, _height - y);
            var endRadius = Math.Clamp(MathF.Sqrt(maxDistX * maxDistX + maxDistY * maxDistY) * 1.15f, 64f, 460f);

            _rippleBrush.EllipseCenter = new Vector2(x, y);
            _rippleBrush.GradientOriginOffset = Vector2.Zero;

            var radiusAnim = _compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.InsertKeyFrame(0.0f, new Vector2(10f, 10f));
            radiusAnim.InsertKeyFrame(1.0f, new Vector2(endRadius, endRadius), _easeOutExpo!);
            radiusAnim.Duration = TimeSpan.FromMilliseconds(460);

            var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(0.14f, 1.0f, _easeOutQuint!);
            opacityAnim.InsertKeyFrame(1.0f, 0.0f, _easeOutExpo!);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(480);

            _rippleBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseRadius), radiusAnim);
            _rippleVisual.StartAnimation(nameof(Visual.Opacity), opacityAnim);
        }
    }
}
