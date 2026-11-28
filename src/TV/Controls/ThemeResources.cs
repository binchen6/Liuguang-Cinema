using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace TV.Controls;

/// <summary>
/// Centralized theme-aware resource lookup helpers shared across windows and custom controls.
/// </summary>
public static class ThemeResources
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static bool IsHighContrast => Accessibility.HighContrast;

    public static Brush? ResolveThemeBrush(FrameworkElement scope, string key)
    {
        var themeKey = Accessibility.HighContrast
            ? "HighContrast"
            : scope.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themed) &&
                themed is ResourceDictionary rd &&
                rd.TryGetValue(key, out var raw) &&
                raw is Brush brush)
                return brush;
        }
        return Application.Current.Resources.TryGetValue(key, out var fallback) ? fallback as Brush : null;
    }

    public static double GetDouble(string key, double fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is double number
            ? number
            : fallback;

    public static T? TryGet<T>(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is T typed
            ? typed
            : default;
}
