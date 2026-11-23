using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TV.Controls;

public sealed partial class BrandMark : UserControl
{
    public BrandMark()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty MarkSizeProperty = DependencyProperty.Register(
        nameof(MarkSize), typeof(double), typeof(BrandMark),
        new PropertyMetadata(28d, OnMarkSizeChanged));

    public double MarkSize
    {
        get => (double)GetValue(MarkSizeProperty);
        set => SetValue(MarkSizeProperty, value);
    }

    private static void OnMarkSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BrandMark mark && e.NewValue is double size && size > 0)
        {
            mark.MarkBox.Width = size;
            mark.MarkBox.Height = size;
        }
    }
}
