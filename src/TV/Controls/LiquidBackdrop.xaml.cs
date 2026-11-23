using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace TV.Controls;

public sealed partial class LiquidBackdrop : UserControl
{
    private static readonly AccessibilitySettings Accessibility = new();

    public LiquidBackdrop()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateAmbientVisibility();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateAmbientVisibility();

    private void UpdateAmbientVisibility()
    {
        AmbientLayer.Visibility = Accessibility.HighContrast ? Visibility.Collapsed : Visibility.Visible;
    }
}
