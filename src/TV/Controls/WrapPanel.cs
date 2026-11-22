using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TV.Controls;

/// <summary>
/// Variable-size horizontal wrapping layout panel for natural-width chips, badges, and tags.
/// Unlike ItemsWrapGrid, each child keeps its own desired width instead of clamping to item 0.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel panel) panel.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? double.PositiveInfinity
            : availableSize.Width;
        var hGap = Math.Max(0, HorizontalSpacing);
        var vGap = Math.Max(0, VerticalSpacing);

        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            var desired = child.DesiredSize;
            var nextWidth = lineWidth > 0 ? lineWidth + hGap + desired.Width : desired.Width;

            if (lineWidth > 0 && nextWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + vGap;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = nextWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;
        return new Size(
            double.IsFinite(availableSize.Width) ? Math.Min(totalWidth, availableSize.Width) : totalWidth,
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = finalSize.Width > 0 ? finalSize.Width : double.PositiveInfinity;
        var hGap = Math.Max(0, HorizontalSpacing);
        var vGap = Math.Max(0, VerticalSpacing);

        double x = 0;
        double y = 0;
        double lineHeight = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var desired = child.DesiredSize;
            if (x > 0 && x + desired.Width > maxWidth)
            {
                x = 0;
                y += lineHeight + vGap;
                lineHeight = 0;
            }

            var width = Math.Min(desired.Width, maxWidth);
            child.Arrange(new Rect(x, y, width, desired.Height));
            x += width + hGap;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, y + lineHeight));
    }
}
