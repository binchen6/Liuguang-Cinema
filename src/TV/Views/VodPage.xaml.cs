using Microsoft.UI.Input;
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
using VirtualKey = Windows.System.VirtualKey;

namespace TV.Views;

public sealed partial class VodPage : Page
{
    private ScrollViewer? _postersScroll;
    private bool _browseLayoutReady;
    private bool _backToTopVisible;
    private bool _topHidden;
    private double _collapseDistance;
    private bool _reducedCompact;
    private readonly PointerEventHandler _overlayWheelHandler;

    public VodPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        MotionService.AttachTactile(HeaderSearchButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(HeaderReloadButton, 1.04f, 0.94f, -1f);
        MotionService.AttachTactile(FilterButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(BackToTop, 1.06f, 0.93f, -2f);
        _overlayWheelHandler = OnOverlayPointerWheelChanged;
        TopCollapsibleBand.AddHandler(PointerWheelChangedEvent, _overlayWheelHandler, handledEventsToo: true);
        StickyDockBand.AddHandler(PointerWheelChangedEvent, _overlayWheelHandler, handledEventsToo: true);
    }

    private VodViewModel ViewModel => (VodViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diagnostics.Log("VodPage loaded");
        try { await ViewModel.InitializeAsync(); Diagnostics.Log("VodPage initialized"); }
        catch (Exception ex) { ViewModel.StatusMessage = "加载失败：" + ex.Message; }
    }

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
        _backToTopVisible = false;
        _topHidden = false;
        _reducedCompact = false;
        TopCollapsibleBand.Visibility = Visibility.Visible;
        TopCollapsibleBand.IsHitTestVisible = true;
        BackToTop.Visibility = Visibility.Collapsed;

        MotionService.Changed -= OnMotionPreferenceChanged;
        MotionService.Changed += OnMotionPreferenceChanged;

        _postersScroll = SearchPage.FindScrollViewer(Posters);
        if (_postersScroll is not null)
        {
            _postersScroll.ViewChanged -= OnBrowseScroll;
            _postersScroll.ViewChanged += OnBrowseScroll;
            _postersScroll.ChangeView(null, 0, null, true);
        }

        SyncCollapsibleHeaderMetrics(forceRebind: true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded) return;
            _browseLayoutReady = true;
            SyncCollapsibleHeaderMetrics(forceRebind: true);
        });
    }

    private void OnPostersUnloaded(object sender, RoutedEventArgs e)
    {
        _browseLayoutReady = false;
        MotionService.Changed -= OnMotionPreferenceChanged;
        if (_postersScroll is not null) _postersScroll.ViewChanged -= OnBrowseScroll;
        _postersScroll = null;
        MotionService.UnbindCollapsibleHeader(TopCollapsibleBand, StickyDockBand, FilterDockGlass, FilterDockDivider);
    }

    private void OnMotionPreferenceChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        SyncCollapsibleHeaderMetrics(forceRebind: true);
    }

    private void OnHeaderBandSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SyncCollapsibleHeaderMetrics(forceRebind: false);
    }

    private void SyncCollapsibleHeaderMetrics(bool forceRebind)
    {
        var topHeight = TopCollapsibleBand.Visibility == Visibility.Visible ? TopCollapsibleBand.ActualHeight : _collapseDistance;
        var dockHeight = StickyDockBand.ActualHeight;
        if (topHeight <= 0 || dockHeight <= 0) return;

        var changed = Math.Abs(topHeight - _collapseDistance) > 0.5;
        if (changed) _collapseDistance = topHeight;

        if (!MotionService.CanAnimate)
        {
            MotionService.UnbindCollapsibleHeader(TopCollapsibleBand, StickyDockBand, FilterDockGlass, FilterDockDivider);
            ApplyReducedMotionState(_postersScroll?.VerticalOffset ?? 0);
            return;
        }

        TopCollapsibleBand.Visibility = Visibility.Visible;
        var targetSpacer = topHeight + dockHeight + 6;
        if (Math.Abs(ScrollTopSpacer.Height - targetSpacer) > 0.5)
            ScrollTopSpacer.Height = targetSpacer;

        if ((changed || forceRebind) && _postersScroll is not null)
        {
            MotionService.BindCollapsibleHeader(
                _postersScroll,
                TopCollapsibleBand,
                StickyDockBand,
                FilterDockGlass,
                FilterDockDivider,
                (float)topHeight);
        }
    }

    private void ApplyReducedMotionState(double offset)
    {
        var compact = offset >= 40;
        _reducedCompact = compact;
        TopCollapsibleBand.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TopCollapsibleBand.IsHitTestVisible = !compact;
        FilterDockGlass.Opacity = compact ? 1.0 : 0.0;
        FilterDockDivider.Opacity = compact ? 0.0 : 1.0;
        var dockHeight = Math.Max(56, StickyDockBand.ActualHeight);
        ScrollTopSpacer.Height = compact ? dockHeight + 6 : _collapseDistance + dockHeight + 6;
    }

    private void OnOverlayPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_postersScroll is null) return;
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        if (props.IsHorizontalMouseWheel) return;
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftDown) return;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;
        var target = Math.Clamp(_postersScroll.VerticalOffset - delta * 0.9, 0, _postersScroll.ScrollableHeight);
        if (Math.Abs(target - _postersScroll.VerticalOffset) > 0.5)
        {
            _postersScroll.ChangeView(null, target, null, disableAnimation: false);
            e.Handled = true;
        }
    }

    private void OnTopClick(object sender, RoutedEventArgs e) => _postersScroll?.ChangeView(null, 0, null, disableAnimation: false);

    private void OnBrowseScroll(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_browseLayoutReady || _postersScroll is null) return;
        var offset = _postersScroll.VerticalOffset;

        if (!MotionService.CanAnimate)
        {
            if (offset > 0 && offset < 40) return;
            var compact = offset >= 40;
            if (compact != _reducedCompact) ApplyReducedMotionState(offset);
            UpdateBackToTop(compact);
            return;
        }

        var hideThreshold = Math.Max(36, _collapseDistance * 0.58);
        var hideTop = offset >= hideThreshold;
        if (hideTop != _topHidden)
        {
            _topHidden = hideTop;
            TopCollapsibleBand.IsHitTestVisible = !hideTop;
        }

        var showFab = offset >= Math.Max(80, _collapseDistance * 0.85);
        UpdateBackToTop(showFab);
    }

    private void UpdateBackToTop(bool visible)
    {
        if (visible == _backToTopVisible) return;
        _backToTopVisible = visible;
        MotionService.SetFloatingVisible(BackToTop, visible, offsetY: 16, hiddenScale: 0.84f);
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
            _postersScroll?.ChangeView(null, 0, null, disableAnimation: false);
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
        panel.ItemHeight = 356;
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
