using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TV.Controls;

/// <summary>
/// Shared visual-tree lookup utilities used across pages and controls to avoid duplicating
/// recursive VisualTreeHelper traversal logic.
/// </summary>
public static class VisualTreeHelpers
{
    public static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    public static FrameworkElement? FindDescendantByName(DependencyObject? root, string name)
    {
        if (root is null || string.IsNullOrEmpty(name)) return null;
        if (root is FrameworkElement element && element.Name == name) return element;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendantByName(VisualTreeHelper.GetChild(root, i), name) is { } found)
                return found;
        }
        return null;
    }

    public static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
