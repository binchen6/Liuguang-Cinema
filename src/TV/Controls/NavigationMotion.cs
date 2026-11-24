using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TV.Views;

namespace TV.Controls;

public enum HorizontalIndicatorMode
{
    /// <summary>Full-width capsule pill gliding behind the active item (e.g., Vod categories, Library segments).</summary>
    PillMatchWidth,

    /// <summary>Fixed-width vermilion accent bar gliding beneath the active horizontal card (e.g., CloudDrive accounts).</summary>
    BottomAccentBar
}

/// <summary>
/// Unified navigation indicator &amp; scroll-sync controller shared across all vertical and horizontal
/// navigation bars in Liuguang Cinema. Eliminates duplicated per-page glide boilerplate, prevents stale
/// coordinates across theme/size/collection updates, and tracks ScrollViewer offsets cleanly.
/// </summary>
public static class NavigationMotion
{
    public static NavigationRailController AttachVerticalList(
        ListView listView,
        FrameworkElement host,
        FrameworkElement indicator,
        Func<FrameworkElement?>? fallbackTarget = null) =>
        new(
            isVertical: true,
            listView: listView,
            host: host,
            indicator: indicator,
            horizontalMode: HorizontalIndicatorMode.PillMatchWidth,
            outerScroller: null,
            targetResolver: () => ResolveListTarget(listView) ?? fallbackTarget?.Invoke());

    public static NavigationRailController AttachVerticalCustom(
        FrameworkElement host,
        FrameworkElement indicator,
        Func<FrameworkElement?> targetResolver) =>
        new(
            isVertical: true,
            listView: null,
            host: host,
            indicator: indicator,
            horizontalMode: HorizontalIndicatorMode.PillMatchWidth,
            outerScroller: null,
            targetResolver: targetResolver);

    public static NavigationRailController AttachHorizontalList(
        ListView listView,
        FrameworkElement host,
        FrameworkElement indicator,
        HorizontalIndicatorMode mode,
        ScrollViewer? outerScroller = null) =>
        new(
            isVertical: false,
            listView: listView,
            host: host,
            indicator: indicator,
            horizontalMode: mode,
            outerScroller: outerScroller,
            targetResolver: () => ResolveListTarget(listView));

    private static FrameworkElement? ResolveListTarget(ListView listView)
    {
        if (listView.SelectedItem is FrameworkElement direct && direct.XamlRoot is not null)
            return direct;
        if (listView.SelectedItem is not null &&
            listView.ContainerFromItem(listView.SelectedItem) is FrameworkElement byItem)
            return byItem;
        if (listView.SelectedIndex >= 0 &&
            listView.ContainerFromIndex(listView.SelectedIndex) is FrameworkElement byIndex)
            return byIndex;
        return null;
    }

    internal static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindDescendantScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}

public sealed class NavigationRailController
{
    private readonly bool _isVertical;
    private readonly ListView? _listView;
    private readonly FrameworkElement _host;
    private readonly FrameworkElement _indicator;
    private readonly HorizontalIndicatorMode _horizontalMode;
    private readonly ScrollViewer? _outerScroller;
    private readonly Func<FrameworkElement?> _targetResolver;
    private ScrollViewer? _innerScroller;
    private bool _hasPlaced;
    private int _syncVersion;

    internal NavigationRailController(
        bool isVertical,
        ListView? listView,
        FrameworkElement host,
        FrameworkElement indicator,
        HorizontalIndicatorMode horizontalMode,
        ScrollViewer? outerScroller,
        Func<FrameworkElement?> targetResolver)
    {
        _isVertical = isVertical;
        _listView = listView;
        _host = host;
        _indicator = indicator;
        _horizontalMode = horizontalMode;
        _outerScroller = outerScroller;
        _targetResolver = targetResolver;

        _host.SizeChanged += (_, _) => Sync(animate: false);
        _host.ActualThemeChanged += (_, _) => Sync(animate: false);
        _host.Loaded += (_, _) =>
        {
            TryAttachInnerScroller();
            Sync(animate: false);
        };

        if (_listView is not null)
        {
            _listView.SelectionChanged += (_, _) => Sync(animate: _hasPlaced, bringIntoView: !_isVertical);
            _listView.SizeChanged += (_, _) => Sync(animate: false);
            _listView.Loaded += (_, _) =>
            {
                TryAttachInnerScroller();
                Sync(animate: false);
            };
            _listView.ContainerContentChanging += (_, args) =>
            {
                if (!args.InRecycleQueue &&
                    _listView.SelectedItem is not null &&
                    Equals(args.Item, _listView.SelectedItem))
                {
                    Sync(animate: false);
                }
            };
        }
    }

    /// <summary>
    /// Resets initial placement state so the next sync snaps cleanly without animating from stale coordinates
    /// (e.g., when switching sites and rebuilding a dynamic category strip).
    /// </summary>
    public void ResetAnchor()
    {
        _hasPlaced = false;
    }

    public void Sync(bool animate, bool bringIntoView = false)
    {
        var version = ++_syncVersion;
        ScheduleSync(version, animate, bringIntoView, retry: 0);
    }

    private void TryAttachInnerScroller()
    {
        if (_listView is null || _outerScroller is not null) return;
        var found = NavigationMotion.FindDescendantScrollViewer(_listView);
        if (ReferenceEquals(found, _innerScroller)) return;
        if (_innerScroller is not null) _innerScroller.ViewChanged -= OnInnerScrollViewChanged;
        _innerScroller = found;
        if (_innerScroller is not null) _innerScroller.ViewChanged += OnInnerScrollViewChanged;
    }

    private void OnInnerScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ApplyImmediate(animate: false, bringIntoView: false);
    }

    private void ScheduleSync(int version, bool animate, bool bringIntoView, int retry)
    {
        var dq = _host.DispatcherQueue;
        if (dq is null) return;
        var priority = retry == 0 ? DispatcherQueuePriority.Normal : DispatcherQueuePriority.Low;
        dq.TryEnqueue(priority, () =>
        {
            if (version != _syncVersion) return;
            if (!_host.IsLoaded || _host.XamlRoot is null) return;
            TryAttachInnerScroller();
            if (!ApplyImmediate(animate, bringIntoView) && retry < 4)
                ScheduleSync(version, animate, bringIntoView, retry + 1);
        });
    }

    private bool ApplyImmediate(bool animate, bool bringIntoView)
    {
        if (!_host.IsLoaded || _host.XamlRoot is null) return false;
        var target = _targetResolver();
        if (target is null)
        {
            MotionService.HideNavIndicator(_indicator);
            return true;
        }
        if (target.XamlRoot is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            return false;

        try
        {
            var pt = target.TransformToVisual(_host).TransformPoint(new Windows.Foundation.Point(0, 0));

            if (_isVertical)
            {
                var indicatorHeight = _indicator.Height > 0 ? _indicator.Height : _indicator.ActualHeight;
                var centerY = pt.Y + (target.ActualHeight - indicatorHeight) * 0.5;

                // Clip guard for scrollable vertical rails (e.g., Search sites, Live groups):
                // hide indicator cleanly when the selected item is scrolled out of the visible list viewport.
                if (_listView is not null && _innerScroller is not null && _listView.ActualHeight > 0)
                {
                    var listOrigin = _listView.TransformToVisual(_host).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var minY = listOrigin.Y - 2;
                    var maxY = listOrigin.Y + _listView.ActualHeight - indicatorHeight + 2;
                    if (centerY < minY || centerY > maxY)
                    {
                        MotionService.HideNavIndicator(_indicator);
                        return true;
                    }
                }

                if (centerY >= -2)
                {
                    MotionService.GlideVerticalIndicator(_indicator, centerY, animate && _hasPlaced);
                    _hasPlaced = true;
                }
                return true;
            }
            else
            {
                if (_horizontalMode == HorizontalIndicatorMode.PillMatchWidth)
                {
                    MotionService.GlideHorizontalPill(_indicator, pt.X, target.ActualWidth, animate && _hasPlaced);
                }
                else
                {
                    var barWidth = _indicator.Width > 0 ? _indicator.Width : _indicator.ActualWidth;
                    var centeredX = pt.X + (target.ActualWidth - barWidth) * 0.5;
                    MotionService.GlideHorizontalBar(_indicator, centeredX, animate && _hasPlaced);
                }

                _hasPlaced = true;

                if (bringIntoView && _outerScroller is { ScrollableWidth: > 1 } scroller)
                    EnsureHorizontallyVisible(scroller, pt.X, target.ActualWidth);

                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureHorizontallyVisible(ScrollViewer scroller, double itemX, double itemWidth)
    {
        const double comfortMargin = 32;
        var viewport = scroller.ViewportWidth;
        if (viewport <= 0) return;
        var current = scroller.HorizontalOffset;
        var leftEdge = itemX - comfortMargin;
        var rightEdge = itemX + itemWidth + comfortMargin;
        if (leftEdge < current)
            scroller.ChangeView(Math.Max(0, leftEdge), null, null, disableAnimation: false);
        else if (rightEdge > current + viewport)
            scroller.ChangeView(Math.Min(scroller.ScrollableWidth, rightEdge - viewport), null, null, disableAnimation: false);
    }
}
