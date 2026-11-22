using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TV.Controls;

/// <summary>
/// 海报图片辅助：
/// 1. AutoFit：竖版（2:3 及更瘦）铺满裁切，横版/方版完整显示并居中，避免横版剧照被 2:3 卡框裁掉大半。
/// 2. CachedSource：经由 ImageCacheService 异步加载封面（带内存/磁盘缓存、防盗链 Referer 与快速失败），
///    替代 WinUI 原生裸 URL 下载，避免列表项回收错图。
/// </summary>
public static class PosterImage
{
    private sealed class LoadState
    {
        public int Version;
    }

    private static readonly ConditionalWeakTable<Image, LoadState> LoadStates = new();

    public static readonly DependencyProperty AutoFitProperty = DependencyProperty.RegisterAttached(
        "AutoFit", typeof(bool), typeof(PosterImage), new PropertyMetadata(false, OnAutoFitChanged));

    public static readonly DependencyProperty CachedSourceProperty = DependencyProperty.RegisterAttached(
        "CachedSource", typeof(string), typeof(PosterImage), new PropertyMetadata(null, OnCachedSourceChanged));

    public static bool GetAutoFit(Image element) => (bool)element.GetValue(AutoFitProperty);
    public static void SetAutoFit(Image element, bool value) => element.SetValue(AutoFitProperty, value);

    public static string? GetCachedSource(Image element) => (string?)element.GetValue(CachedSourceProperty);
    public static void SetCachedSource(Image element, string? value) => element.SetValue(CachedSourceProperty, value);

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

    private static async void OnCachedSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var state = LoadStates.GetOrCreateValue(image);
        var version = ++state.Version;
        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            image.Source = null;
            return;
        }

        // Clear stale image when container recycles to a different URL
        image.Source = null;
        if (App.Services?.Images is not { } cache) return;
        try
        {
            var bitmap = await cache.GetImageAsync(url);
            if (state.Version != version) return;
            image.Source = bitmap;
            if (bitmap is not null && GetAutoFit(image)) Apply(image);
        }
        catch { }
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
