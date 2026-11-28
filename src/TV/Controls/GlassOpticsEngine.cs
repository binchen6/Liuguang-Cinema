using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
/// Dedicated GPU-composited physical sunlight &amp; optical glass engine (SHIRO · MA · OPTICAL GLASS).
/// Simulates realistic sunlight interaction with beveled optical glass on WinUI 3's DWM Compositor thread:
///   1. Resting global directional sunlight field (upper-left warm solar key + cool sky counter-bounce, zero idle loops)
///   2. Dynamic Snell's Law crown-glass (n=1.52) volumetric transmission + convex lens caustic &amp; lateral parallax
///   3. Schlick Fresnel chamfered rim reflection with subtle chromatic dispersion (warm solar / cool sky)
///   4. Dual top &amp; bottom total-internal-reflection (TIR) caustic ridges tracking pointer glide
///   5. Coupled external incident light diffuse wash + directional Fresnel rim catchlight (e.g. music player sunlight)
///   6. Expanding click caustic refraction shockwave on pointer press
/// Strictly maintains 0% idle GPU/CPU cost, SwapChainPanel safety (zero ThemeShadow, zero extra AcrylicBrush),
/// 120Hz pointer throttling, and HighContrast + MotionService compliance.
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
            Detach(element);
            return;
        }
        AttachOrUpdate(element, element, mode, GetProfile(element), GetRadiusOverride(element));
    }

    /// <summary>
    /// Attaches optical glass rendering where pointer events are observed on <paramref name="pointerHost"/>
    /// and GPU Composition visuals are hosted inside <paramref name="visualHost"/> (such as GlassSurface's OpticsHost).
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
            Detach(pointerHost);
            return;
        }

        if (Sessions.TryGetValue(pointerHost, out var session))
            session.UpdateConfig(visualHost, mode, profile, radiusOverride);
        else
            Sessions.Add(pointerHost, new OpticsSession(pointerHost, visualHost, mode, profile, radiusOverride));
    }

    public static void NotifyGeometryChanged(FrameworkElement pointerHost)
    {
        if (Sessions.TryGetValue(pointerHost, out var session))
            session.SyncGeometry();
    }

    internal static void SetIncidentLight(FrameworkElement host, Vector2 direction, float illuminance, float warmth = 0.65f)
    {
        if (Sessions.TryGetValue(host, out var session))
            session.SetIncidentLight(direction, illuminance, warmth);
    }

    public static void Detach(FrameworkElement pointerHost)
    {
        if (Sessions.TryGetValue(pointerHost, out var session))
        {
            session.Detach();
            Sessions.Remove(pointerHost);
        }
    }

    /// <summary>Schlick Fresnel approximation for optical crown glass (n ≈ 1.52, F0 ≈ 0.042).</summary>
    private static float Fresnel(float cosine)
    {
        const float f0 = 0.042f;
        var oneMinus = 1f - Math.Clamp(cosine, 0f, 1f);
        var sq = oneMinus * oneMinus;
        return f0 + (1f - f0) * (sq * sq * oneMinus);
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

        // Resting global sunlight layer (zero idle animation cost — static GPU gradient calibrated to upper-left sun)
        private ShapeVisual? _solarAmbientVisual;
        private CompositionLinearGradientBrush? _solarFillBrush;
        private ShapeVisual? _solarBevelVisual;
        private CompositionLinearGradientBrush? _solarBevelBrush;

        // Dynamic hover optical layers
        private ShapeVisual? _transmissionVisual;
        private CompositionRoundedRectangleGeometry? _transmissionGeometry;
        private CompositionRadialGradientBrush? _transmissionBrush;

        private ShapeVisual? _lensCoreVisual;
        private CompositionRadialGradientBrush? _lensCoreBrush;

        private ShapeVisual? _fresnelRimVisual;
        private CompositionRoundedRectangleGeometry? _rimGeometry;
        private CompositionRadialGradientBrush? _rimBrush;

        private ShapeVisual? _causticRidgeVisual;
        private CompositionRectangleGeometry? _causticGeometry;
        private CompositionRadialGradientBrush? _causticBrush;

        private ShapeVisual? _bottomBounceVisual;
        private CompositionRectangleGeometry? _bottomBounceGeometry;
        private CompositionRadialGradientBrush? _bottomBounceBrush;

        // Click caustic refraction shockwave
        private ShapeVisual? _rippleVisual;
        private CompositionRoundedRectangleGeometry? _rippleGeometry;
        private CompositionRadialGradientBrush? _rippleBrush;

        // Coupled external incident light (diffuse wash + directional Fresnel rim catchlight)
        private ContainerVisual? _incidentGroup;
        private ShapeVisual? _incidentDiffuseVisual;
        private CompositionRadialGradientBrush? _incidentDiffuseBrush;
        private ShapeVisual? _incidentRimVisual;
        private CompositionRadialGradientBrush? _incidentRimBrush;
        private Vector2KeyFrameAnimation? _incidentCenterAnim;
        private Vector2KeyFrameAnimation? _incidentRimCenterAnim;
        private ScalarKeyFrameAnimation? _incidentOpacityAnim;

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
        }

        public void UpdateConfig(FrameworkElement visualHost, GlassOpticsMode mode, GlassOpticsProfile profile, double radiusOverride)
        {
            var hostChanged = !ReferenceEquals(_visualHost, visualHost);
            if (hostChanged)
            {
                ClearChildVisual();
                _visualHost.Loaded -= OnVisualLoaded;
                _visualHost.SizeChanged -= OnVisualSizeChanged;
                _visualHost = visualHost;
                _visualHost.Loaded += OnVisualLoaded;
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
            else if (ShouldEagerlyAttachSolarLayer && _visualHost.IsLoaded)
            {
                TryEnsureVisuals();
            }
        }

        /// <summary>
        /// Materialize static light layer on load for architectural glass panels (GlassSurface, Ambient, Rail),
        /// while keeping transparent buttons, virtualized Cards, and small Controls clean until pointer hover or incident light.
        /// </summary>
        private bool ShouldEagerlyAttachSolarLayer =>
            !Accessibility.HighContrast &&
            (_mode == GlassOpticsMode.Ambient ||
             _pointerHost is GlassSurface ||
             _profile == GlassOpticsProfile.Rail);

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            _pointerHost.Unloaded += OnUnloaded;
            _pointerHost.ActualThemeChanged += OnThemeChanged;
            _visualHost.Loaded += OnVisualLoaded;
            _visualHost.SizeChanged += OnVisualSizeChanged;

            _pointerHost.AddHandler(UIElement.PointerEnteredEvent, _enteredHandler, handledEventsToo: true);
            _pointerHost.AddHandler(UIElement.PointerMovedEvent, _movedHandler, handledEventsToo: true);
            _pointerHost.AddHandler(UIElement.PointerExitedEvent, _exitedHandler, handledEventsToo: true);
            _pointerHost.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, handledEventsToo: true);

            if (ShouldEagerlyAttachSolarLayer && _visualHost.IsLoaded)
                TryEnsureVisuals();
        }

        public void Detach()
        {
            if (!_wired) return;
            _wired = false;
            _pointerHost.Unloaded -= OnUnloaded;
            _pointerHost.ActualThemeChanged -= OnThemeChanged;
            _visualHost.Loaded -= OnVisualLoaded;
            _visualHost.SizeChanged -= OnVisualSizeChanged;

            _pointerHost.RemoveHandler(UIElement.PointerEnteredEvent, _enteredHandler);
            _pointerHost.RemoveHandler(UIElement.PointerMovedEvent, _movedHandler);
            _pointerHost.RemoveHandler(UIElement.PointerExitedEvent, _exitedHandler);
            _pointerHost.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
            StopWatchingMotion();
            ClearChildVisual();
        }

        private void OnVisualLoaded(object sender, RoutedEventArgs e)
        {
            if (ShouldEagerlyAttachSolarLayer)
                TryEnsureVisuals();
        }

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

        private void OnThemeChanged(FrameworkElement sender, object args)
        {
            ApplyPalette();
            SyncGeometry();
        }

        private void OnMotionChanged(object? sender, EventArgs e)
        {
            if (!CanAnimatePointer && _hoverVisualGroup is not null)
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
            if (!CanReflectIncident && _incidentGroup is not null)
            {
                _incidentGroup.StopAnimation(nameof(Visual.Opacity));
                _incidentGroup.Opacity = 0f;
            }
            if (_solarAmbientVisual is not null && _solarBevelVisual is not null)
            {
                var visible = !Accessibility.HighContrast ? 1f : 0f;
                _solarAmbientVisual.Opacity = visible;
                _solarBevelVisual.Opacity = visible;
            }
        }

        internal void SetIncidentLight(Vector2 direction, float illuminance, float warmth)
        {
            if ((illuminance <= 0.001f && _incidentGroup is null) || !CanReflectIncident ||
                !TryEnsureVisuals() || _width <= 1f || _height <= 1f)
            {
                if (illuminance <= 0.001f && _incidentGroup is not null)
                {
                    _incidentGroup.StopAnimation(nameof(Visual.Opacity));
                    _incidentGroup.Opacity = 0f;
                }
                return;
            }

            var compositor = _compositor!;
            if (_incidentGroup is null)
            {
                _incidentGroup = compositor.CreateContainerVisual();
                _incidentGroup.Opacity = 0f;

                _incidentDiffuseBrush = compositor.CreateRadialGradientBrush();
                _incidentDiffuseBrush.MappingMode = CompositionMappingMode.Absolute;
                var diffuseShape = compositor.CreateSpriteShape(_transmissionGeometry);
                diffuseShape.FillBrush = _incidentDiffuseBrush;
                _incidentDiffuseVisual = compositor.CreateShapeVisual();
                _incidentDiffuseVisual.Shapes.Add(diffuseShape);

                _incidentRimBrush = compositor.CreateRadialGradientBrush();
                _incidentRimBrush.MappingMode = CompositionMappingMode.Absolute;
                var rimShape = compositor.CreateSpriteShape(_rimGeometry);
                rimShape.StrokeBrush = _incidentRimBrush;
                rimShape.StrokeThickness = _profile == GlassOpticsProfile.Cinema ? 1.35f : 1.15f;
                _incidentRimVisual = compositor.CreateShapeVisual();
                _incidentRimVisual.Shapes.Add(rimShape);

                _incidentGroup.Children.InsertAtTop(_incidentDiffuseVisual);
                _incidentGroup.Children.InsertAtTop(_incidentRimVisual);
                _rootVisual!.Children.InsertAbove(_incidentGroup, _solarBevelVisual);

                _incidentCenterAnim = compositor.CreateVector2KeyFrameAnimation();
                _incidentCenterAnim.Duration = TimeSpan.FromMilliseconds(240);
                _incidentRimCenterAnim = compositor.CreateVector2KeyFrameAnimation();
                _incidentRimCenterAnim.Duration = TimeSpan.FromMilliseconds(240);
                _incidentOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
                _incidentOpacityAnim.Duration = TimeSpan.FromMilliseconds(240);
                ApplyIncidentPalette(warmth);
            }

            var size = new Vector2(_width, _height);
            _incidentGroup.Size = size;
            _incidentDiffuseVisual!.Size = size;
            _incidentRimVisual!.Size = size;

            var clampedDir = new Vector2(Math.Clamp(direction.X, -1.6f, 1.6f), Math.Clamp(direction.Y, -1.6f, 1.6f));
            var rx = Math.Clamp(_width * 0.52f, 92f, 420f);
            var ry = Math.Clamp(_height * 1.18f, 68f, 260f);
            _incidentDiffuseBrush!.EllipseRadius = new Vector2(rx, ry);

            var rimR = Math.Clamp(MathF.Max(_width, _height) * 0.58f, 90f, 360f);
            _incidentRimBrush!.EllipseRadius = new Vector2(rimR, rimR * 0.84f);

            var incidenceCos = 0.62f / MathF.Sqrt(0.3844f + clampedDir.LengthSquared());
            var fresnelGain = 0.58f + 1.55f * Fresnel(incidenceCos);
            var targetOpacity = Math.Clamp(illuminance * fresnelGain, 0f, 0.96f);

            // Diffuse wash enters from the sunward side; Fresnel rim focuses right along the sunward beveled edge
            var diffuseCenter = new Vector2(
                (0.5f + clampedDir.X * 0.38f) * _width,
                (0.24f + clampedDir.Y * 0.30f) * _height);
            var rimCenter = new Vector2(
                (0.5f + clampedDir.X * 0.48f) * _width,
                (0.16f + clampedDir.Y * 0.36f) * _height);

            _incidentDiffuseBrush.GradientOriginOffset = -clampedDir * 14f;
            _incidentCenterAnim!.InsertKeyFrame(1f, diffuseCenter, _easeOutQuint!);
            _incidentDiffuseBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseCenter), _incidentCenterAnim);

            _incidentRimCenterAnim!.InsertKeyFrame(1f, rimCenter, _easeOutQuint!);
            _incidentRimBrush.StartAnimation(nameof(CompositionRadialGradientBrush.EllipseCenter), _incidentRimCenterAnim);

            _incidentOpacityAnim!.InsertKeyFrame(1f, targetOpacity, _easeOutQuint!);
            _incidentGroup.StartAnimation(nameof(Visual.Opacity), _incidentOpacityAnim);
        }

        private void ApplyIncidentPalette(float warmth)
        {
            if (_compositor is null || _incidentDiffuseBrush is null || _incidentRimBrush is null) return;
            var w = Math.Clamp(warmth, 0f, 1f);
            var coreR = (byte)Math.Clamp((int)MathF.Round(244 + 11 * w), 0, 255);
            var coreG = (byte)Math.Clamp((int)MathF.Round(232 + 12 * w), 0, 255);
            var coreB = (byte)Math.Clamp((int)MathF.Round(228 - 28 * w), 0, 255);

            _incidentDiffuseBrush.ColorStops.Clear();
            _incidentDiffuseBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(62, coreR, coreG, coreB)));
            _incidentDiffuseBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.32f, Color.FromArgb(30, 232, 224, 240)));
            _incidentDiffuseBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(12, 184, 214, 252)));
            _incidentDiffuseBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 184, 214, 252)));

            _incidentRimBrush.ColorStops.Clear();
            _incidentRimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(196, 255, 248, 232)));
            _incidentRimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.36f, Color.FromArgb(98, 255, 226, 188)));
            _incidentRimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.70f, Color.FromArgb(46, 186, 218, 255)));
            _incidentRimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 186, 218, 255)));
        }

        private void OnVisualSizeChanged(object sender, SizeChangedEventArgs e) => SyncGeometry();

        private bool CanRenderAny => !Accessibility.HighContrast && _mode != GlassOpticsMode.Off;

        private bool CanAnimatePointer =>
            _mode == GlassOpticsMode.Interactive &&
            !Accessibility.HighContrast &&
            MotionService.CanAnimate;

        private bool CanReflectIncident =>
            CanRenderAny &&
            MotionService.CanAnimate &&
            MotionService.CanRunAmbientLoops;

        private bool TryEnsureVisuals()
        {
            if (_visualsUnavailable || !CanRenderAny || !_visualHost.IsLoaded) return false;
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

            // Shared rounded geometries
            _transmissionGeometry = c.CreateRoundedRectangleGeometry();
            _rimGeometry = c.CreateRoundedRectangleGeometry();
            _causticGeometry = c.CreateRectangleGeometry();
            _bottomBounceGeometry = c.CreateRectangleGeometry();
            _rippleGeometry = c.CreateRoundedRectangleGeometry();

            // Layer 0a: Resting global directional sunlight wash (upper-left warm solar key -> cool sky tail)
            _solarFillBrush = c.CreateLinearGradientBrush();
            _solarFillBrush.MappingMode = CompositionMappingMode.Relative;
            _solarFillBrush.StartPoint = new Vector2(0.06f, 0.0f);
            _solarFillBrush.EndPoint = new Vector2(0.94f, 1.0f);
            var solarFillShape = c.CreateSpriteShape(_transmissionGeometry);
            solarFillShape.FillBrush = _solarFillBrush;
            _solarAmbientVisual = c.CreateShapeVisual();
            _solarAmbientVisual.Shapes.Add(solarFillShape);

            // Layer 0b: Resting directional sunlight bevel rim (catches upper-left daylight along chamfer)
            _solarBevelBrush = c.CreateLinearGradientBrush();
            _solarBevelBrush.MappingMode = CompositionMappingMode.Relative;
            _solarBevelBrush.StartPoint = new Vector2(0.08f, 0.0f);
            _solarBevelBrush.EndPoint = new Vector2(0.92f, 1.0f);
            var solarBevelShape = c.CreateSpriteShape(_rimGeometry);
            solarBevelShape.StrokeBrush = _solarBevelBrush;
            solarBevelShape.StrokeThickness = 1.0f;
            _solarBevelVisual = c.CreateShapeVisual();
            _solarBevelVisual.Shapes.Add(solarBevelShape);

            _rootVisual.Children.InsertAtTop(_solarAmbientVisual);
            _rootVisual.Children.InsertAtTop(_solarBevelVisual);

            // Hover optical group
            _hoverVisualGroup = c.CreateContainerVisual();
            _hoverVisualGroup.Opacity = 0f;

            // Layer 1: Volumetric specular transmission + Snell lateral refraction
            _transmissionBrush = c.CreateRadialGradientBrush();
            _transmissionBrush.MappingMode = CompositionMappingMode.Absolute;
            var transShape = c.CreateSpriteShape(_transmissionGeometry);
            transShape.FillBrush = _transmissionBrush;
            _transmissionVisual = c.CreateShapeVisual();
            _transmissionVisual.Shapes.Add(transShape);

            // Layer 1b: Focused convex optical lens caustic core
            _lensCoreBrush = c.CreateRadialGradientBrush();
            _lensCoreBrush.MappingMode = CompositionMappingMode.Absolute;
            var lensCoreShape = c.CreateSpriteShape(_transmissionGeometry);
            lensCoreShape.FillBrush = _lensCoreBrush;
            _lensCoreVisual = c.CreateShapeVisual();
            _lensCoreVisual.Shapes.Add(lensCoreShape);

            // Layer 2: Schlick Fresnel chamfered edge refraction + chromatic dispersion
            _rimBrush = c.CreateRadialGradientBrush();
            _rimBrush.MappingMode = CompositionMappingMode.Absolute;
            var rimShape = c.CreateSpriteShape(_rimGeometry);
            rimShape.StrokeBrush = _rimBrush;
            rimShape.StrokeThickness = _profile == GlassOpticsProfile.Cinema ? 1.45f : 1.3f;
            _fresnelRimVisual = c.CreateShapeVisual();
            _fresnelRimVisual.Shapes.Add(rimShape);

            // Layer 3a: Top-bevel total-internal-reflection caustic ridge
            _causticBrush = c.CreateRadialGradientBrush();
            _causticBrush.MappingMode = CompositionMappingMode.Absolute;
            var causticShape = c.CreateSpriteShape(_causticGeometry);
            causticShape.FillBrush = _causticBrush;
            _causticRidgeVisual = c.CreateShapeVisual();
            _causticRidgeVisual.Shapes.Add(causticShape);

            // Layer 3b: Bottom inner-lip counter-refracted sunlight bounce
            _bottomBounceBrush = c.CreateRadialGradientBrush();
            _bottomBounceBrush.MappingMode = CompositionMappingMode.Absolute;
            var bottomShape = c.CreateSpriteShape(_bottomBounceGeometry);
            bottomShape.FillBrush = _bottomBounceBrush;
            _bottomBounceVisual = c.CreateShapeVisual();
            _bottomBounceVisual.Shapes.Add(bottomShape);

            _hoverVisualGroup.Children.InsertAtTop(_transmissionVisual);
            _hoverVisualGroup.Children.InsertAtTop(_lensCoreVisual);
            _hoverVisualGroup.Children.InsertAtTop(_fresnelRimVisual);
            _hoverVisualGroup.Children.InsertAtTop(_causticRidgeVisual);
            _hoverVisualGroup.Children.InsertAtTop(_bottomBounceVisual);

            // Layer 4: Expanding click caustic refraction shockwave
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
            WatchMotion();
            ApplyPalette();
            SyncGeometry();
        }

        private void ClearChildVisual()
        {
            StopWatchingMotion();
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
            _solarAmbientVisual = null;
            _solarFillBrush = null;
            _solarBevelVisual = null;
            _solarBevelBrush = null;
            _transmissionVisual = null;
            _transmissionGeometry = null;
            _transmissionBrush = null;
            _lensCoreVisual = null;
            _lensCoreBrush = null;
            _fresnelRimVisual = null;
            _rimGeometry = null;
            _rimBrush = null;
            _causticRidgeVisual = null;
            _causticGeometry = null;
            _causticBrush = null;
            _bottomBounceVisual = null;
            _bottomBounceGeometry = null;
            _bottomBounceBrush = null;
            _rippleVisual = null;
            _rippleGeometry = null;
            _rippleBrush = null;
            _incidentGroup = null;
            _incidentDiffuseVisual = null;
            _incidentDiffuseBrush = null;
            _incidentRimVisual = null;
            _incidentRimBrush = null;
            _incidentCenterAnim?.Dispose();
            _incidentRimCenterAnim?.Dispose();
            _incidentOpacityAnim?.Dispose();
            _incidentCenterAnim = null;
            _incidentRimCenterAnim = null;
            _incidentOpacityAnim = null;
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
            _solarAmbientVisual!.Size = size;
            _solarBevelVisual!.Size = size;
            _hoverVisualGroup!.Size = size;
            _transmissionVisual!.Size = size;
            _lensCoreVisual!.Size = size;
            _fresnelRimVisual!.Size = size;
            _causticRidgeVisual!.Size = size;
            _bottomBounceVisual!.Size = size;
            _rippleVisual!.Size = size;
            if (_incidentGroup is not null)
            {
                _incidentGroup.Size = size;
                _incidentDiffuseVisual!.Size = size;
                _incidentRimVisual!.Size = size;
            }

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

            var causticInset = Math.Clamp(r * 0.62f, 4f, Math.Max(4f, w * 0.25f));
            var causticWidth = Math.Max(2f, w - causticInset * 2f);
            _causticGeometry!.Offset = new Vector2(causticInset, 0.5f);
            _causticGeometry.Size = new Vector2(causticWidth, 1.25f);

            _bottomBounceGeometry!.Offset = new Vector2(causticInset, Math.Max(1f, h - 1.5f));
            _bottomBounceGeometry.Size = new Vector2(causticWidth, 1.0f);

            _rippleGeometry!.Offset = Vector2.Zero;
            _rippleGeometry.Size = size;
            _rippleGeometry.CornerRadius = corner;

            // Calibrate optical radii to glass material:
            // Light (sunlight + frosted glass) diffuses wider and softer;
            // Dark (moonlight + clear glass) focuses crisper and tighter;
            // Cinema stays fixed and theme-independent.
            var isFrostedLight = _pointerHost.ActualTheme == ElementTheme.Light && _profile != GlassOpticsProfile.Cinema;
            var transFactor = isFrostedLight ? 0.56f : 0.42f;
            var transRadius = Math.Clamp(MathF.Max(w, h) * transFactor, 96f, isFrostedLight ? 360f : 290f);
            _transmissionBrush!.EllipseRadius = new Vector2(transRadius, transRadius * (isFrostedLight ? 0.94f : 0.84f));

            var lensFactor = isFrostedLight ? 0.72f : 0.46f;
            var lensRadius = Math.Clamp(MathF.Min(w, h) * lensFactor, 28f, isFrostedLight ? 140f : 96f);
            _lensCoreBrush!.EllipseRadius = new Vector2(lensRadius * 1.12f, lensRadius * 0.86f);

            var rimRadius = Math.Clamp(MathF.Min(w, h) * (isFrostedLight ? 1.04f : 0.90f), 74f, 240f);
            _rimBrush!.EllipseRadius = new Vector2(rimRadius, rimRadius);

            var causticSpan = Math.Clamp(w * (isFrostedLight ? 0.42f : 0.31f), 40f, 185f);
            _causticBrush!.EllipseRadius = new Vector2(causticSpan, isFrostedLight ? 22f : 15f);
            _bottomBounceBrush!.EllipseRadius = new Vector2(causticSpan * 0.80f, isFrostedLight ? 18f : 13f);

            if (_lastX < 0 || _lastY < 0)
                UpdateLightOrigin(w * 0.38f, h * 0.28f);
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
                    raw = 10;
            }
            return Math.Clamp((float)raw, 0f, MathF.Min(w, h) * 0.5f);
        }

        private void ApplyPalette()
        {
            if (!_visualsReady || _compositor is null) return;
            var isCinema = _profile == GlassOpticsProfile.Cinema;
            var isLight = !isCinema && _pointerHost.ActualTheme == ElementTheme.Light;

            var scale = _profile switch
            {
                GlassOpticsProfile.Cinema => 1.16f,
                GlassOpticsProfile.Control => 1.06f,
                GlassOpticsProfile.Card => 0.96f,
                GlassOpticsProfile.Rail => 0.86f,
                _ => 1.0f
            };
            byte ScaleA(byte a) => (byte)Math.Clamp((int)MathF.Round(a * scale), 0, 255);

            // 0. Resting directional light field:
            //    - Light: warm sunlit frosted glass (broad warm-cream diffuse + soft morning bevel)
            //    - Dark:  cool moonlit clear transmissive glass (crisp lunar silver-ice bevel + deep clarity)
            //    - Cinema: fixed theme-independent neutral optical glass (only on GlassSurface hosts, never outline transparent buttons)
            var ambientScale = _profile switch
            {
                GlassOpticsProfile.Cinema => _pointerHost is GlassSurface ? 0.95f : 0f,
                GlassOpticsProfile.Rail => 0.74f,
                GlassOpticsProfile.Card => 0.88f,
                GlassOpticsProfile.Control => 0.92f,
                _ => 1.0f
            };
            byte AmbA(byte a) => (byte)Math.Clamp((int)MathF.Round(a * ambientScale), 0, 255);

            _solarFillBrush!.ColorStops.Clear();
            _solarBevelBrush!.ColorStops.Clear();
            if (isLight)
            {
                // Sunlight + Frosted Glass: broad warm sunlit milky scatter -> soft sky shadow edge
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(AmbA(64), 255, 250, 236)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.32f, Color.FromArgb(AmbA(26), 255, 244, 222)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(0, 255, 255, 255)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(AmbA(20), 178, 206, 236)));

                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(AmbA(165), 255, 252, 242)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.34f, Color.FromArgb(AmbA(58), 255, 242, 218)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.74f, Color.FromArgb(AmbA(16), 174, 202, 232)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(AmbA(42), 180, 204, 232)));
            }
            else if (isCinema)
            {
                // Cinema player controls: theme-invariant clear optical glass
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(AmbA(22), 255, 248, 238)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.28f, Color.FromArgb(AmbA(8), 226, 238, 255)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(0, 255, 255, 255)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(AmbA(10), 156, 188, 240)));

                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(AmbA(60), 255, 250, 242)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.30f, Color.FromArgb(AmbA(22), 220, 236, 255)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.76f, Color.FromArgb(AmbA(6), 172, 204, 246)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(AmbA(24), 178, 208, 248)));
            }
            else
            {
                // Moonlight + Clear Transmissive Glass: cool lunar silver-blue transmission & razor-sharp crystal edge
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(AmbA(24), 228, 240, 255)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.24f, Color.FromArgb(AmbA(10), 182, 214, 255)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.64f, Color.FromArgb(0, 180, 210, 255)));
                _solarFillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(AmbA(12), 146, 180, 242)));

                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(AmbA(82), 238, 247, 255)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.28f, Color.FromArgb(AmbA(30), 184, 218, 255)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.74f, Color.FromArgb(AmbA(8), 148, 188, 246)));
                _solarBevelBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(AmbA(34), 174, 208, 252)));
            }

            // 1. Volumetric Specular Transmission + chromatic dispersion
            _transmissionBrush!.ColorStops.Clear();
            if (isLight)
            {
                // Warm diffused sunlight through frosted glass
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(68), 255, 250, 236)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.34f, Color.FromArgb(ScaleA(34), 255, 240, 216)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(ScaleA(14), 212, 234, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else if (isCinema)
            {
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(38), 255, 250, 242)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.32f, Color.FromArgb(ScaleA(18), 192, 224, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.66f, Color.FromArgb(ScaleA(10), 255, 214, 186)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                // Cool silver moonlight refracting cleanly through clear crystal glass
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(42), 236, 246, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.30f, Color.FromArgb(ScaleA(20), 178, 216, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.64f, Color.FromArgb(ScaleA(10), 194, 206, 255)));
                _transmissionBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 220, 238, 255)));
            }

            // 1b. Focused Convex Lens Specular Core (crisper on Dark clear glass, softer/warmer on Light frosted glass)
            _lensCoreBrush!.ColorStops.Clear();
            if (isLight)
            {
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(42), 255, 252, 240)));
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.46f, Color.FromArgb(ScaleA(16), 255, 242, 218)));
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else if (isCinema)
            {
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(26), 255, 253, 246)));
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.42f, Color.FromArgb(ScaleA(10), 226, 240, 255)));
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(34), 244, 250, 255)));
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.38f, Color.FromArgb(ScaleA(14), 186, 222, 255)));
                _lensCoreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 200, 228, 255)));
            }

            // 2. Schlick Fresnel Chamfer Rim + Prismatic Grazing Dispersion
            _rimBrush!.ColorStops.Clear();
            if (isLight)
            {
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(234), 255, 252, 240)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.36f, Color.FromArgb(ScaleA(122), 255, 232, 198)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(ScaleA(62), 184, 216, 250)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else if (isCinema)
            {
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(174), 255, 250, 242)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.36f, Color.FromArgb(ScaleA(90), 178, 218, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(ScaleA(48), 246, 174, 144)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(198), 242, 249, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.34f, Color.FromArgb(ScaleA(106), 172, 214, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.68f, Color.FromArgb(ScaleA(52), 198, 206, 255)));
                _rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 220, 238, 255)));
            }

            // 3a. Top-bevel traveling caustic catchlight
            _causticBrush!.ColorStops.Clear();
            if (isLight)
            {
                _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(228), 255, 252, 240)));
                _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, Color.FromArgb(ScaleA(96), 255, 236, 206)));
                _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(194), 242, 249, 255)));
                _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, Color.FromArgb(ScaleA(82), 186, 222, 255)));
                _causticBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 220, 238, 255)));
            }

            // 3b. Bottom inner-lip counter-refracted bounce
            _bottomBounceBrush!.ColorStops.Clear();
            if (isLight)
            {
                _bottomBounceBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(86), 255, 240, 214)));
                _bottomBounceBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.50f, Color.FromArgb(ScaleA(42), 198, 224, 252)));
                _bottomBounceBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
            }
            else
            {
                _bottomBounceBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(ScaleA(82), 194, 226, 255)));
                _bottomBounceBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.50f, Color.FromArgb(ScaleA(36), 210, 220, 255)));
                _bottomBounceBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 210, 232, 255)));
            }

            // 4. Click caustic refraction ring
            _rippleBrush!.ColorStops.Clear();
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.46f, Color.FromArgb(isLight ? (byte)18 : (byte)12, isLight ? (byte)255 : (byte)188, isLight ? (byte)242 : (byte)224, isLight ? (byte)214 : (byte)255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.76f, Color.FromArgb(isLight ? ScaleA(112) : ScaleA(78), isLight ? (byte)255 : (byte)240, isLight ? (byte)250 : (byte)248, isLight ? (byte)238 : (byte)255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.89f, Color.FromArgb(isLight ? ScaleA(44) : ScaleA(32), isLight ? (byte)240 : (byte)176, isLight ? (byte)158 : (byte)212, isLight ? (byte)122 : (byte)255)));
            _rippleBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!CanAnimatePointer || !TryEnsureVisuals()) return;
            if (_width <= 1f || _height <= 1f) SyncGeometry();
            _isHovering = true;
            var pt = e.GetCurrentPoint(_visualHost).Position;
            UpdateLightOrigin((float)pt.X, (float)pt.Y);
            AnimateHoverOpacity(1.0f, 180, _easeOutQuint!);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!CanAnimatePointer) return;
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
            if (!CanAnimatePointer || !e.GetCurrentPoint(_visualHost).Properties.IsLeftButtonPressed) return;
            for (var element = e.OriginalSource as DependencyObject; element is not null && element != _pointerHost;
                element = VisualTreeHelper.GetParent(element))
            {
                if (element is FrameworkElement child && Sessions.TryGetValue(child, out var session) && session.CanAnimatePointer)
                    return;
            }
            if (!TryEnsureVisuals()) return;
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

            // Snell's Law crown glass (n = 1.52, n^2 = 2.3104) slab refraction & convex lens shift
            if (_width > 1f && _height > 1f)
            {
                var lateral = new Vector2(x * 2f / _width - 1f, y * 2f / _height - 1f);
                var cosThetaI = 0.65f / MathF.Sqrt(0.4225f + lateral.LengthSquared());
                var sinSqThetaI = Math.Max(0f, 1f - cosThetaI * cosThetaI);
                var cosThetaT = MathF.Sqrt(Math.Max(0.01f, 1f - sinSqThetaI / 2.3104f));
                var bend = (1f - cosThetaI / cosThetaT) * 34f;

                _transmissionBrush.GradientOriginOffset = -lateral * bend;
                _lensCoreBrush!.EllipseCenter = pos - lateral * (bend * 0.35f);
                _lensCoreBrush.GradientOriginOffset = new Vector2(-4f, -6f); // upper-left solar bias
                _rimBrush.GradientOriginOffset = lateral * (4f + 32f * Fresnel(cosThetaI));

                _fresnelRimVisual!.Opacity = Math.Clamp(0.56f + 2.95f * Fresnel(cosThetaI), 0f, 1f);
                var normalizedY = Math.Clamp(y / _height, 0f, 1f);
                _causticRidgeVisual!.Opacity = Math.Clamp(0.28f + (1f - normalizedY) * 0.68f, 0f, 1f);
                _bottomBounceVisual!.Opacity = Math.Clamp(0.22f + normalizedY * 0.56f, 0f, 0.82f);
            }

            // Traveling top-bevel caustic tracks pointer X; bottom refracted bounce shifts slightly counter-angle
            _causticBrush!.EllipseCenter = new Vector2(x, 0.5f);
            _bottomBounceBrush!.EllipseCenter = new Vector2(_width - (_width - x) * 0.88f, Math.Max(1f, _height - 1f));
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
