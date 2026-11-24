using System.Collections.Specialized;
using System.ComponentModel;
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
    private VodViewModel? _observed;
    private readonly NavigationRailController _categoryNav;
    private bool _syncingCategory;
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
        _categoryNav = NavigationMotion.AttachHorizontalList(
            CategoryNavList,
            CategoryTrackHost,
            CategoryGlidePill,
            HorizontalIndicatorMode.PillMatchWidth,
            CategoryScroller);
        MotionService.AttachTactile(HeaderSearchButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(HeaderReloadButton, 1.04f, 0.94f, -1f);
        MotionService.AttachTactile(FilterButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(BackToTop, 1.06f, 0.93f, -2f);
        _overlayWheelHandler = OnOverlayPointerWheelChanged;
        TopCollapsibleBand.AddHandler(PointerWheelChangedEvent, _overlayWheelHandler, handledEventsToo: true);
        StickyDockBand.AddHandler(PointerWheelChangedEvent, _overlayWheelHandler, handledEventsToo: true);
        FilterDockGlass.Translation = new System.Numerics.Vector3(0f, 0f, 12f);
        FilterDockShadow.Receivers.Add(Posters);
        Unloaded += OnPageUnloaded;
    }

    private VodViewModel ViewModel => (VodViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diagnostics.Log("VodPage loaded");
        AttachCategoryObserver(ViewModel);
        SyncCategorySelection(animate: false);
        try { await ViewModel.InitializeAsync(); Diagnostics.Log("VodPage initialized"); }
        catch (Exception ex) { ViewModel.StatusMessage = "加载失败：" + ex.Message; }
        if (IsLoaded) SyncCategorySelection(animate: false);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => AttachCategoryObserver(null);

    private void AttachCategoryObserver(VodViewModel? model)
    {
        if (ReferenceEquals(_observed, model)) return;
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnVodViewModelPropertyChanged;
            _observed.Categories.CollectionChanged -= OnCategoriesCollectionChanged;
        }
        _observed = model;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnVodViewModelPropertyChanged;
            _observed.Categories.CollectionChanged += OnCategoriesCollectionChanged;
        }
    }

    private void OnCategoriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _categoryNav.ResetAnchor();
            CategoryScroller.ChangeView(0, null, null, disableAnimation: true);
        }
        SyncCategorySelection(animate: false);
    }

    private void OnVodViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VodViewModel.SelectedCategory))
        {
            // 若 ListView 已指向同一选中项（由用户直接点击触发），保留正在滑行的弹簧动画不被 animate:false 覆盖
            if (_observed is not null && ReferenceEquals(CategoryNavList.SelectedItem, _observed.SelectedCategory))
                return;
            SyncCategorySelection(animate: false);
        }
    }

    private void SyncCategorySelection(bool animate)
    {
        if (_observed is null) return;
        var target = _observed.SelectedCategory;
        if (!ReferenceEquals(CategoryNavList.SelectedItem, target))
        {
            _syncingCategory = true;
            try { CategoryNavList.SelectedItem = target; }
            finally { _syncingCategory = false; }
        }
        _categoryNav.Sync(animate);
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
        if (e.Handled || _postersScroll is null) return;
        if (ScrollWheelBehavior.ForwardVerticalWheel(_postersScroll, e, this))
            e.Handled = true;
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

    private async void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCategory) return;
        if (CategoryNavList.SelectedItem is not VodCategory category) return;

        if (ViewModel.SelectedSite is { } site && BilibiliVodProvider.Supports(site) && BilibiliVodProvider.IsLoginCategory(category.Id))
        {
            _syncingCategory = true;
            try { CategoryNavList.SelectedItem = ViewModel.SelectedCategory; }
            finally { _syncingCategory = false; }
            _categoryNav.Sync(animate: false);
            if (XamlRoot is null) return;
            using var dialog = new BilibiliLoginDialog(App.Services.BilibiliAccount, XamlRoot);
            try { await dialog.ShowAsync(); }
            catch (Exception) { ViewModel.StatusMessage = "B 站账号面板无法打开，请重试。"; }
            return;
        }

        if (!ReferenceEquals(ViewModel.SelectedCategory, category))
        {
            ViewModel.SelectedCategory = category;
            _postersScroll?.ChangeView(null, 0, null, disableAnimation: false);
        }
    }

    private void OnCategoryContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is VodCategory category)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, category.Name);
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
