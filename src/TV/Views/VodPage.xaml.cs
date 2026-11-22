using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TV.Controls;
using TV.Models;
using TV.Services;
using TV.Services.Vod.Native;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class VodPage : Page
{

    public VodPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    private VodViewModel ViewModel => (VodViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diagnostics.Log("VodPage loaded");
        MotionService.Reveal(this);
        try { await ViewModel.InitializeAsync(); Diagnostics.Log("VodPage initialized"); }
        catch (Exception ex) { ViewModel.StatusMessage = "加载失败：" + ex.Message; }
    }

    private ScrollViewer? _postersScroll;
    private bool _browseLayoutReady;
    private void OnSearchClick(object sender, RoutedEventArgs e) => App.Window.OpenSearch();
    private async void OnVerifySiteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PendingVerification is not { } challenge || XamlRoot is null) return;
        try
        {
            var dialog = new SiteVerificationDialog(App.Services, challenge, XamlRoot);
            if (await dialog.ShowAsync() && IsLoaded)
            {
                ViewModel.PendingVerification = null;
                ViewModel.ReloadCommand.Execute(null);
            }
        }
        catch (Exception ex) { ViewModel.StatusMessage = "验证面板打开失败：" + ex.Message; }
    }
    private void OnPostersLoaded(object sender, RoutedEventArgs e)
    {
        _browseLayoutReady = false;
        BrowseHeader.Visibility = GalleryHost.Visibility = Visibility.Visible;
        BackToTop.Visibility = Visibility.Collapsed;
        _postersScroll = SearchPage.FindScrollViewer(Posters);
        if (_postersScroll is not null)
        {
            _postersScroll.ViewChanged += OnBrowseScroll;
            _postersScroll.ChangeView(null, 0, null, true);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _browseLayoutReady = true);
    }
    private void OnPostersUnloaded(object sender, RoutedEventArgs e)
    {
        _browseLayoutReady = false;
        if (_postersScroll is not null) _postersScroll.ViewChanged -= OnBrowseScroll;
        _postersScroll = null;
    }
    private void OnTopClick(object sender, RoutedEventArgs e) => _postersScroll?.ChangeView(null, 0, null);
    private void OnBrowseScroll(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_browseLayoutReady) return;
        var offset = _postersScroll?.VerticalOffset ?? 0;
        if (offset > 0 && offset < 40) return;
        BrowseHeader.Visibility = GalleryHost.Visibility = offset >= 40 ? Visibility.Collapsed : Visibility.Visible;
        BackToTop.Visibility = offset >= 40 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCategoryPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: VodCategory category } button)
        {
            if (ViewModel.SelectedSite is { } site && BilibiliVodProvider.Supports(site) && BilibiliVodProvider.IsLoginCategory(category.Id))
            {
                button.IsChecked = false;
                if (XamlRoot is null) return;
                using var dialog = new BilibiliLoginDialog(App.Services.BilibiliAccount, XamlRoot);
                try { await dialog.ShowAsync(); }
                catch (Exception) { ViewModel.StatusMessage = "B 站账号面板无法打开，请重试。"; }
                return;
            }
            ViewModel.SelectedCategory = category;
            button.IsChecked = true;
        }
    }

    private void OnPosterContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is VodCardViewModel card) _ = card.LoadVisualAsync();
    }

    private void OnPosterViewportChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not GridView { ItemsPanelRoot: ItemsWrapGrid panel }) return;
        // 海报列宽按令牌自适应：最少一张 188 宽，列间距 18。
        const double posterWidth = 188, columnGap = 18;
        var width = Math.Max(1, args.NewSize.Width);
        var columns = Math.Max(1, (int)((width + columnGap) / (posterWidth + columnGap)));
        // ItemWidth 是列间距（pitch），模板里的 18 右边距由 item 内部 Button.Margin 承担，
        // 所以这里不能用 (width - gap*(n-1))/n，否则最后会空出一整列宽度的空白。
        panel.ItemWidth = Math.Floor(width / columns);
        // 行高 = 封面 282 + 行距 10 + 标题 44 + 行距 10 + 元数据 18 + 底部行距 22（模板内 Button Margin）。
        panel.ItemHeight = 386;
    }

    private void OnFeaturedLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VodCardViewModel card }) _ = card.LoadVisualAsync();
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VodCardViewModel card }) ViewModel.SelectedCard = card;
    }

    private void OnCardIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        ScaleTo(element, 1.03);
        FadeOverlay(element, 1);
    }

    private void OnCardOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        ScaleTo(element, 1.0);
        FadeOverlay(element, 0);
    }

    private static void FadeOverlay(FrameworkElement cardRoot, double opacity)
    {
        if (FindDescendant(cardRoot, "PlayOverlay") is not UIElement overlay) return;
        if (!MotionService.CanAnimate) { overlay.Opacity = opacity; return; }
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, overlay);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendant(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        }
        return null;
    }

    private static void ScaleTo(FrameworkElement element, double scale)
    {
        if (!MotionService.CanAnimate) return;
        if (element.RenderTransform is not CompositeTransform transform)
        {
            transform = new CompositeTransform();
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                To = scale,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        storyboard.Begin();
    }

}
