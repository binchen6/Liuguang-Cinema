using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using TV.Views;

namespace TV.Controls;

/// <summary>
/// Reusable card hover, artwork zoom, and press feedback helper for virtualized GridView/ListView items.
/// Caches resolved template child references per card instance to avoid repeated visual-tree scans
/// and eliminates duplicated card pointer-handler boilerplate across pages.
/// </summary>
public sealed class CardHoverTracker
{
    private sealed record CardParts(FrameworkElement? Overlay, FrameworkElement? Badge, FrameworkElement? Art);

    private readonly string _artName;
    private readonly bool _withPlayOverlay;
    private readonly ConditionalWeakTable<FrameworkElement, CardParts> _cache = new();

    public CardHoverTracker(string artName, bool withPlayOverlay = false)
    {
        _artName = artName;
        _withPlayOverlay = withPlayOverlay;
    }

    private CardParts Resolve(FrameworkElement card)
    {
        if (_cache.TryGetValue(card, out var cached) && (cached.Art is not null || cached.Overlay is not null))
            return cached;
        var overlay = _withPlayOverlay ? VisualTreeHelpers.FindDescendantByName(card, "PlayOverlay") : null;
        var badge = _withPlayOverlay ? VisualTreeHelpers.FindDescendantByName(card, "PlayBadge") : null;
        var art = VisualTreeHelpers.FindDescendantByName(card, _artName);
        var parts = new CardParts(overlay, badge, art);
        if (art is not null || overlay is not null)
            _cache.AddOrUpdate(card, parts);
        return parts;
    }

    public void Reset(FrameworkElement card)
    {
        var parts = Resolve(card);
        MotionService.ResetCard(card, parts.Overlay, parts.Badge, parts.Art);
    }

    public void SetHovered(object sender, bool hovered)
    {
        if (sender is not FrameworkElement element) return;
        var parts = Resolve(element);
        MotionService.SetCardHover(element, parts.Overlay, parts.Badge, parts.Art, hovered);
    }

    public static void SetPressed(object sender, bool pressed)
    {
        if (sender is FrameworkElement element)
            MotionService.SetCardPressed(element, pressed);
    }
}
