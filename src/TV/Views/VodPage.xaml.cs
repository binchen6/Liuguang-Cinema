using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        MotionService.AttachTactile(HeaderSearchButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(HeaderReloadButton, 1.04f, 0.94f, -1f);
        MotionService.AttachTactile(FilterButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(BackToTop, 1.06f, 0.93f, -2f);
    }

    private VodViewModel ViewModel => (VodViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diagnostics.Log("VodPage loaded");
        try { await ViewModel.InitializeAsync(); Diagnostics.Log("VodPage initialized"); }
        catch (Exception ex) { ViewModel.StatusMessage = "加载失败：" + ex.Message; }
    }

    private ScrollViewer? _postersScroll;
    private bool _browseLayoutReady;
    private bool _scrolledCompact;

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
        _scrolledCompact = false;
        BrowseHeader.Visibility = GalleryHost.Visibility = Visibility.Visible;
        ApplyFilterDockVisual(compact: false);
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
        var compact = offset >= 40;
        if (compact == _scrolledCompact) return;
        _scrolledCompact = compact;
        BrowseHeader.Visibility = GalleryHost.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilterDockVisual(compact, animate: true);
        if (compact) MotionService.EnterSection(FilterDock, offsetY: -6);
        MotionService.SetFloatingVisible(BackToTop, compact, offsetY: 16, hiddenScale: 0.84f);
    }

    private void ApplyFilterDockVisual(bool compact, bool animate = false)
    {
        FilterBar.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(0, 6, 0, 12);
        FilterDock.Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 10, 0, 12);
        if (animate)
        {
            MotionService.FadeOpacity(FilterDockGlass, compact ? 1f : 0f, 180);
            MotionService.FadeOpacity(FilterDockDivider, compact ? 0f : 1f, 150);
        }
        else
        {
            FilterDockGlass.Opacity = compact ? 1.0 : 0.0;
            FilterDockDivider.Opacity = compact ? 0.0 : 1.0;
        }
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
            MotionService.PulseElement(button, 0.93f);
        }
    }

    private void OnPosterContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is FrameworkElement card)
        {
            var parts = GetCardParts(card, "PosterArt");
            MotionService.ResetCard(card, parts.Overlay, parts.Badge, parts.Art);
        }
        if (!args.InRecycleQueue && args.Item is VodCardViewModel vm) _ = vm.LoadVisualAsync();
    }

    private void OnPosterViewportChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not GridView { ItemsPanelRoot: ItemsWrapGrid panel }) return;
        const double posterWidth = 188, columnGap = 18;
        var width = Math.Max(1, args.NewSize.Width);
        var columns = Math.Max(1, (int)((width + columnGap) / (posterWidth + columnGap)));
        panel.ItemWidth = Math.Floor(width / columns);
        panel.ItemHeight = 350;
    }

    private void OnFeaturedLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VodCardViewModel card }) _ = card.LoadVisualAsync();
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VodCardViewModel card }) ViewModel.SelectedCard = card;
    }

    private sealed record CardParts(FrameworkElement? Overlay, FrameworkElement? Badge, FrameworkElement? Art);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, CardParts> _cardParts = new();

    private CardParts GetCardParts(FrameworkElement card, string artName)
    {
        if (_cardParts.TryGetValue(card, out var parts) && (parts.Art is not null || parts.Overlay is not null)) return parts;
        parts = new CardParts(FindDescendant(card, "PlayOverlay"), FindDescendant(card, "PlayBadge"), FindDescendant(card, artName));
        if (parts.Art is not null || parts.Overlay is not null) _cardParts.AddOrUpdate(card, parts);
        return parts;
    }

    private void OnFeaturedIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var parts = GetCardParts(element, "GalleryArt");
        MotionService.SetCardHover(element, null, null, parts.Art, true);
    }

    private void OnFeaturedOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var parts = GetCardParts(element, "GalleryArt");
        MotionService.SetCardHover(element, null, null, parts.Art, false);
    }

    private void OnCardIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var parts = GetCardParts(element, "PosterArt");
        MotionService.SetCardHover(element, parts.Overlay, parts.Badge, parts.Art, true);
    }

    private void OnCardOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var parts = GetCardParts(element, "PosterArt");
        MotionService.SetCardHover(element, parts.Overlay, parts.Badge, parts.Art, false);
    }

    private void OnCardPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, true);
    }

    private void OnCardReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, false);
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
}
