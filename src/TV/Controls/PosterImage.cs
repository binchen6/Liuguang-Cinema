using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TV.Controls;

/// <summary>
/// 海报图片自适应：竖版（2:3 及更瘦）铺满裁切，横版/方版完整显示并居中，
/// 避免横版剧照被 2:3 卡框裁掉大半。
/// </summary>
public static class PosterImage
{
    public static readonly DependencyProperty AutoFitProperty = DependencyProperty.RegisterAttached(
        "AutoFit", typeof(bool), typeof(PosterImage), new PropertyMetadata(false, OnAutoFitChanged));

    public static bool GetAutoFit(Image element) => (bool)element.GetValue(AutoFitProperty);
    public static void SetAutoFit(Image element, bool value) => element.SetValue(AutoFitProperty, value);

    private static void OnAutoFitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        if ((bool)e.NewValue)
        {
            image.ImageOpened += OnImageOpened;
            Apply(image);
        }
        else image.ImageOpened -= OnImageOpened;
    }

    private static void OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) Apply(image);
    }

    private static void Apply(Image image)
    {
        if (image.Source is not BitmapImage bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
        var ratio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        if (ratio < 0.82)
        {
            image.Stretch = Stretch.UniformToFill;
            image.HorizontalAlignment = HorizontalAlignment.Stretch;
            image.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            image.Stretch = Stretch.Uniform;
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
        }
    }
}
