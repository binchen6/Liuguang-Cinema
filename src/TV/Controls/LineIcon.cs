using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace TV.Controls;

/// <summary>
/// Architectural 24x24 geometric vector icon control inspired by Kenya Hara / Swiss graphic minimalism.
/// Safely instantiates fresh Geometry per instance so it works reliably inside virtualized DataTemplates
/// and automatically tracks Foreground changes across Dark, Light, HighContrast, and VisualState transitions.
/// </summary>
public sealed class LineIcon : UserControl
{
    private readonly Viewbox _viewbox;
    private readonly Canvas _canvas;
    private Microsoft.UI.Xaml.Shapes.Path? _path;

    public LineIcon()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        _canvas = new Canvas { Width = 24, Height = 24 };
        _viewbox = new Viewbox
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Child = _canvas
        };
        Content = _viewbox;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => ApplyBrush());
        Loaded += (_, _) => ApplyBrush();
        ActualThemeChanged += (_, _) => ApplyBrush();
    }

    public static readonly DependencyProperty PathDataProperty = DependencyProperty.Register(
        nameof(PathData), typeof(string), typeof(LineIcon),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(LineIcon),
        new PropertyMetadata(18d, OnSizeChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LineIcon),
        new PropertyMetadata(1.65d, OnStylePropertyChanged));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(LineIcon),
        new PropertyMetadata(false, OnStylePropertyChanged));

    public string PathData
    {
        get => (string)GetValue(PathDataProperty);
        set => SetValue(PathDataProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LineIcon icon && e.NewValue is double size && size > 0)
        {
            icon._viewbox.Width = size;
            icon._viewbox.Height = size;
        }
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LineIcon icon) icon.RebuildPath();
    }

    private static void OnStylePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LineIcon icon) icon.ApplyBrush();
    }

    private void RebuildPath()
    {
        _canvas.Children.Clear();
        _path = null;
        if (string.IsNullOrWhiteSpace(PathData)) return;

        try
        {
            var escaped = System.Security.SecurityElement.Escape(PathData);
            var xaml = $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{escaped}' />";
            if (XamlReader.Load(xaml) is Microsoft.UI.Xaml.Shapes.Path path)
            {
                path.StrokeLineJoin = PenLineJoin.Round;
                path.StrokeStartLineCap = PenLineCap.Round;
                path.StrokeEndLineCap = PenLineCap.Round;
                _path = path;
                _canvas.Children.Add(path);
                ApplyBrush();
            }
        }
        catch
        {
            // Ignore malformed path strings gracefully
        }
    }

    private void ApplyBrush()
    {
        if (_path is null) return;
        var brush = Foreground;
        if (IsFilled)
        {
            _path.Fill = brush;
            _path.Stroke = null;
            _path.StrokeThickness = 0;
        }
        else
        {
            _path.Fill = null;
            _path.Stroke = brush;
            _path.StrokeThickness = StrokeThickness;
        }
    }
}
