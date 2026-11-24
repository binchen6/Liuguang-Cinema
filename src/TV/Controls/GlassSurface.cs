using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace TV.Controls;

/// <summary>
/// Multi-layer optical frosted glass surface (SHIRO · MA · OPTICAL GLASS).
/// Composes:
///   L1 - Calibrated frosted base (GPU AcrylicBrush on standard XAML pages; SwapChain-safe
///        translucent optical frosted crystal over LibVLC VideoView SwapChainPanel)
///   L2 - Internal volumetric skylight, milky frost scatter & bottom optical thickness (VolumeBrush)
///   L3 - Fine 1:1 procedural acid-etched micro-silica frost grain (optional, zero CPU after one-time init)
///   L4 - Dual-bevel glass thickness rim (outer Fresnel specular edge + 1px inner chamfer rim)
///   L5 - Top 1px peak-centered caustic catchlight + subtle bottom inner lip reflection
///   L6 - Controlled ThemeShadow elevation applied by floating host containers
/// </summary>
public sealed class GlassSurface : ContentControl
{
    private static readonly AccessibilitySettings Accessibility = new();
    private static WriteableBitmap? _sharedFrostGrain;

    public static readonly DependencyProperty VolumeBrushProperty =
        DependencyProperty.Register(nameof(VolumeBrush), typeof(Brush), typeof(GlassSurface), new PropertyMetadata(null));

    public static readonly DependencyProperty InnerBevelBrushProperty =
        DependencyProperty.Register(nameof(InnerBevelBrush), typeof(Brush), typeof(GlassSurface), new PropertyMetadata(null));

    public static readonly DependencyProperty CausticBrushProperty =
        DependencyProperty.Register(nameof(CausticBrush), typeof(Brush), typeof(GlassSurface), new PropertyMetadata(null));

    public static readonly DependencyProperty BottomBevelBrushProperty =
        DependencyProperty.Register(nameof(BottomBevelBrush), typeof(Brush), typeof(GlassSurface), new PropertyMetadata(null));

    public static readonly DependencyProperty FrostGrainOpacityProperty =
        DependencyProperty.Register(nameof(FrostGrainOpacity), typeof(double), typeof(GlassSurface), new PropertyMetadata(0.0, OnFrostGrainOpacityChanged));

    public static readonly DependencyProperty InnerCornerRadiusProperty =
        DependencyProperty.Register(nameof(InnerCornerRadius), typeof(CornerRadius), typeof(GlassSurface), new PropertyMetadata(new CornerRadius(13)));

    public static readonly DependencyProperty BevelCornerRadiusProperty =
        DependencyProperty.Register(nameof(BevelCornerRadius), typeof(CornerRadius), typeof(GlassSurface), new PropertyMetadata(new CornerRadius(12)));

    public static readonly DependencyProperty CausticMarginProperty =
        DependencyProperty.Register(nameof(CausticMargin), typeof(Thickness), typeof(GlassSurface), new PropertyMetadata(new Thickness(12, 0, 12, 0)));

    private UIElement? _volumeLayer;
    private UIElement? _innerBevelLayer;
    private Border? _frostGrainLayer;
    private UIElement? _causticLine;
    private UIElement? _bottomBevel;

    public GlassSurface()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        RegisterPropertyChangedCallback(CornerRadiusProperty, OnCornerRadiusChanged);
        ActualThemeChanged += (_, _) => UpdateOpticalLayers();
        UpdateDerivedGeometry();
    }

    public Brush? VolumeBrush
    {
        get => (Brush?)GetValue(VolumeBrushProperty);
        set => SetValue(VolumeBrushProperty, value);
    }

    public Brush? InnerBevelBrush
    {
        get => (Brush?)GetValue(InnerBevelBrushProperty);
        set => SetValue(InnerBevelBrushProperty, value);
    }

    public Brush? CausticBrush
    {
        get => (Brush?)GetValue(CausticBrushProperty);
        set => SetValue(CausticBrushProperty, value);
    }

    public Brush? BottomBevelBrush
    {
        get => (Brush?)GetValue(BottomBevelBrushProperty);
        set => SetValue(BottomBevelBrushProperty, value);
    }

    public double FrostGrainOpacity
    {
        get => (double)GetValue(FrostGrainOpacityProperty);
        set => SetValue(FrostGrainOpacityProperty, value);
    }

    public CornerRadius InnerCornerRadius
    {
        get => (CornerRadius)GetValue(InnerCornerRadiusProperty);
        private set => SetValue(InnerCornerRadiusProperty, value);
    }

    public CornerRadius BevelCornerRadius
    {
        get => (CornerRadius)GetValue(BevelCornerRadiusProperty);
        private set => SetValue(BevelCornerRadiusProperty, value);
    }

    public Thickness CausticMargin
    {
        get => (Thickness)GetValue(CausticMarginProperty);
        private set => SetValue(CausticMarginProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _volumeLayer = GetTemplateChild("VolumeLayer") as UIElement;
        _innerBevelLayer = GetTemplateChild("InnerBevelLayer") as UIElement;
        _frostGrainLayer = GetTemplateChild("FrostGrainLayer") as Border;
        _causticLine = GetTemplateChild("CausticLine") as UIElement;
        _bottomBevel = GetTemplateChild("BottomBevel") as UIElement;
        UpdateDerivedGeometry();
        UpdateOpticalLayers();
    }

    private static void OnFrostGrainOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlassSurface surface) surface.UpdateOpticalLayers();
    }

    private void OnCornerRadiusChanged(DependencyObject sender, DependencyProperty dp) => UpdateDerivedGeometry();

    private void UpdateDerivedGeometry()
    {
        var r = CornerRadius;
        InnerCornerRadius = new CornerRadius(
            Math.Max(0, r.TopLeft - 1),
            Math.Max(0, r.TopRight - 1),
            Math.Max(0, r.BottomRight - 1),
            Math.Max(0, r.BottomLeft - 1));
        BevelCornerRadius = new CornerRadius(
            Math.Max(0, r.TopLeft - 2),
            Math.Max(0, r.TopRight - 2),
            Math.Max(0, r.BottomRight - 2),
            Math.Max(0, r.BottomLeft - 2));
        var insetLeft = Math.Clamp(r.TopLeft * 0.65, 6, 26);
        var insetRight = Math.Clamp(r.TopRight * 0.65, 6, 26);
        CausticMargin = new Thickness(insetLeft, 0, insetRight, 0);
    }

    private void UpdateOpticalLayers()
    {
        var hc = Accessibility.HighContrast;
        var vis = hc ? Visibility.Collapsed : Visibility.Visible;
        if (_volumeLayer is not null) _volumeLayer.Visibility = vis;
        if (_innerBevelLayer is not null) _innerBevelLayer.Visibility = vis;
        if (_causticLine is not null) _causticLine.Visibility = vis;
        if (_bottomBevel is not null) _bottomBevel.Visibility = vis;

        if (_frostGrainLayer is not null)
        {
            var showGrain = !hc && FrostGrainOpacity > 0.001;
            _frostGrainLayer.Visibility = showGrain ? Visibility.Visible : Visibility.Collapsed;
            if (showGrain)
            {
                if (_frostGrainLayer.Background is not ImageBrush brush)
                {
                    brush = new ImageBrush
                    {
                        ImageSource = EnsureFrostGrainBitmap(),
                        Stretch = Stretch.UniformToFill
                    };
                    _frostGrainLayer.Background = brush;
                }
                brush.Opacity = FrostGrainOpacity;
            }
        }
    }

    /// <summary>
    /// Generates a single shared 1:1 fine acid-etched micro-silica frost texture (1152x160 BGRA, ~720KB) once.
    /// Applied via ImageBrush on a Border so it adds 0x0 intrinsic layout size and 0 per-frame CPU/shader cost.
    /// </summary>
    private static WriteableBitmap EnsureFrostGrainBitmap()
    {
        if (_sharedFrostGrain is not null) return _sharedFrostGrain;
        const int w = 1152;
        const int h = 160;
        var bmp = new WriteableBitmap(w, h);
        var pixels = new byte[w * h * 4];
        uint state = 0x9E3779B9u;
        for (var i = 0; i < w * h; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            var n1 = (int)(state & 0xFF);
            var n2 = (int)((state >> 8) & 0xFF);
            var n3 = (int)((state >> 16) & 0xFF);
            var lum = (n1 + n2 + n3) / 3; // tighter bell curve -> silky-fine luxury optical frost mist
            var alpha = (byte)Math.Clamp((lum - 108) * 2, 0, 210);
            var idx = i * 4;
            pixels[idx] = (byte)((255 * alpha) / 255);
            pixels[idx + 1] = (byte)((252 * alpha) / 255);
            pixels[idx + 2] = (byte)((248 * alpha) / 255);
            pixels[idx + 3] = alpha;
        }
        using var stream = bmp.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bmp.Invalidate();
        _sharedFrostGrain = bmp;
        return bmp;
    }
}
