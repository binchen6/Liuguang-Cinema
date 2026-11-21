using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TV.Controls;

public sealed partial class CloudLoginPanel : UserControl
{
    public CloudLoginPanel() => InitializeComponent();
    public event EventHandler? CloseRequested;
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
