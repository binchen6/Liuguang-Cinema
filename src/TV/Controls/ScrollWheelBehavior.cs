using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace TV.Controls;

public enum HorizontalWheelMode
{
    None,
    /// <summary>
    /// Standalone horizontal strip (not nested inside a vertical page scroller):
    /// both regular vertical wheel and Shift+Wheel smoothly scroll horizontally.
    /// </summary>
    Direct,
    /// <summary>
    /// Horizontal strip nested inside a vertical page scroller (e.g. Vod gallery/categories, Detail related):
    /// regular vertical wheel passes straight through to the page scroller; Shift+Wheel scrolls horizontally.
    /// </summary>
    ShiftOnly
}

/// <summary>
/// Unified desktop mouse-wheel consistency engine across all pages &amp; subpages:
/// 1. Consistent accumulated-target horizontal scrolling for standalone &amp; nested horizontal strips.
/// 2. Accumulated-target vertical wheel forwarding for overlay bands (e.g. VodPage collapsible header &amp; sticky dock)
///    so scrolling over floating headers matches native list scrolling 1:1.
/// 3. Unfocused NumberBox wheel guard inside SettingsScroller so scrolling never accidentally mutates numeric fields.
/// </summary>
public static class ScrollWheelBehavior
{
    private sealed class ScrollAccumulator
    {
        public double TargetX = double.NaN;
        public double TargetY = double.NaN;
        public long LastTicks;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAccumulator> Accumulators = new();
    private static readonly ConditionalWeakTable<FrameworkElement, PointerEventHandler> HorizontalHandlers = new();
    private static readonly ConditionalWeakTable<ScrollViewer, PointerEventHandler> NumberBoxGuardHandlers = new();

    public static readonly DependencyProperty HorizontalWheelModeProperty =
        DependencyProperty.RegisterAttached(
            "HorizontalWheelMode",
            typeof(HorizontalWheelMode),
            typeof(ScrollWheelBehavior),
            new PropertyMetadata(HorizontalWheelMode.None, OnHorizontalWheelModeChanged));

    public static readonly DependencyProperty GuardNumberBoxWheelProperty =
        DependencyProperty.RegisterAttached(
            "GuardNumberBoxWheel",
            typeof(bool),
            typeof(ScrollWheelBehavior),
            new PropertyMetadata(false, OnGuardNumberBoxWheelChanged));

    public static HorizontalWheelMode GetHorizontalWheelMode(DependencyObject obj) =>
        (HorizontalWheelMode)obj.GetValue(HorizontalWheelModeProperty);

    public static void SetHorizontalWheelMode(DependencyObject obj, HorizontalWheelMode value) =>
        obj.SetValue(HorizontalWheelModeProperty, value);

    public static bool GetGuardNumberBoxWheel(DependencyObject obj) =>
        (bool)obj.GetValue(GuardNumberBoxWheelProperty);

    public static void SetGuardNumberBoxWheel(DependencyObject obj, bool value) =>
        obj.SetValue(GuardNumberBoxWheelProperty, value);

    private static void OnHorizontalWheelModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (HorizontalHandlers.TryGetValue(element, out var existing))
        {
            element.RemoveHandler(UIElement.PointerWheelChangedEvent, existing);
            HorizontalHandlers.Remove(element);
        }

        var mode = (HorizontalWheelMode)e.NewValue;
        if (mode == HorizontalWheelMode.None) return;

        PointerEventHandler handler = (_, args) => HandleHorizontalWheel(element, mode, args);
        HorizontalHandlers.AddOrUpdate(element, handler);
        element.AddHandler(UIElement.PointerWheelChangedEvent, handler, handledEventsToo: true);
    }

    private static void OnGuardNumberBoxWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scroller) return;
        if (NumberBoxGuardHandlers.TryGetValue(scroller, out var existing))
        {
            scroller.RemoveHandler(UIElement.PointerWheelChangedEvent, existing);
            NumberBoxGuardHandlers.Remove(scroller);
        }
        if (e.NewValue is not true) return;

        PointerEventHandler handler = (_, args) => HandleNumberBoxGuard(scroller, args);
        NumberBoxGuardHandlers.AddOrUpdate(scroller, handler);
        scroller.AddHandler(UIElement.PointerWheelChangedEvent, handler, handledEventsToo: false);
    }

    private static void HandleHorizontalWheel(FrameworkElement host, HorizontalWheelMode mode, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        var props = e.GetCurrentPoint(host).Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;

        var isHorizontalWheel = props.IsHorizontalMouseWheel;
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (mode == HorizontalWheelMode.ShiftOnly && !shiftDown && !isHorizontalWheel)
            return;

        var scroller = host as ScrollViewer ?? VisualTreeHelpers.FindDescendant<ScrollViewer>(host);
        if (scroller is null || scroller.ScrollableWidth <= 0.5) return;

        // Standard horizontal wheel reports positive=right; vertical wheel reports positive=up (scroll left)
        var step = isHorizontalWheel ? delta * 0.85 : -delta * 0.85;
        var acc = Accumulators.GetOrCreateValue(scroller);
        var now = Environment.TickCount64;
        var baseOffset = !double.IsNaN(acc.TargetX) && (now - acc.LastTicks) < 240
            ? acc.TargetX
            : scroller.HorizontalOffset;
        var target = Math.Clamp(baseOffset + step, 0, scroller.ScrollableWidth);
        acc.TargetX = target;
        acc.LastTicks = now;

        if (Math.Abs(target - scroller.HorizontalOffset) > 0.5)
        {
            scroller.ChangeView(target, null, null, disableAnimation: false);
            e.Handled = true;
        }
        else if (mode == HorizontalWheelMode.Direct || shiftDown || isHorizontalWheel)
        {
            // Keep Shift+Wheel from leaking vertically when already at left/right edge
            if (shiftDown) e.Handled = true;
        }
    }

    private static void HandleNumberBoxGuard(ScrollViewer scroller, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (VisualTreeHelpers.FindAncestor<NumberBox>(source) is not { } box) return;
        // If user explicitly focused the NumberBox to adjust its value, allow normal NumberBox wheel stepping.
        if (box.FocusState != FocusState.Unfocused) return;

        var props = e.GetCurrentPoint(scroller).Properties;
        if (props.IsHorizontalMouseWheel || props.MouseWheelDelta == 0) return;

        // Forward smoothly to SettingsScroller and mark handled so unfocused NumberBox cannot hijack wheel
        if (ForwardVerticalWheel(scroller, e, scroller))
            e.Handled = true;
    }

    /// <summary>
    /// Smoothly forwards vertical mouse-wheel deltas to <paramref name="target"/> using an accumulated
    /// target offset so rapid wheel spins match native ScrollViewer responsiveness 1:1.
    /// </summary>
    public static bool ForwardVerticalWheel(ScrollViewer? target, PointerRoutedEventArgs e, UIElement reference)
    {
        if (target is null || target.ScrollableHeight <= 0.5) return false;
        var props = e.GetCurrentPoint(reference).Properties;
        if (props.IsHorizontalMouseWheel) return false;
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftDown) return false;

        var delta = props.MouseWheelDelta;
        if (delta == 0) return false;

        var acc = Accumulators.GetOrCreateValue(target);
        var now = Environment.TickCount64;
        var baseOffset = !double.IsNaN(acc.TargetY) && (now - acc.LastTicks) < 240
            ? acc.TargetY
            : target.VerticalOffset;
        var next = Math.Clamp(baseOffset - delta * 0.9, 0, target.ScrollableHeight);
        acc.TargetY = next;
        acc.LastTicks = now;

        if (Math.Abs(next - target.VerticalOffset) <= 0.5) return false;
        target.ChangeView(null, next, null, disableAnimation: false);
        return true;
    }
}
