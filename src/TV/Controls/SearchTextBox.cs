using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TV.Controls;

public sealed class SearchTextBox : TextBox
{
    public event EventHandler? TextInputReceived;
    public event EventHandler<KeyRoutedEventArgs>? EditingKeyDown;
    public bool IsComposing { get; private set; }

    public SearchTextBox()
    {
        TextCompositionStarted += (_, _) => IsComposing = true;
        TextCompositionEnded += (_, _) =>
        {
            IsComposing = false;
            TextInputReceived?.Invoke(this, EventArgs.Empty);
        };
    }

    protected override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        if (!IsComposing) TextInputReceived?.Invoke(this, EventArgs.Empty);
        base.OnCharacterReceived(e);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        EditingKeyDown?.Invoke(this, e);
        if (e.Handled) return;
        if (!IsComposing && e.Key is VirtualKey.Back or VirtualKey.Delete or VirtualKey.Space)
            TextInputReceived?.Invoke(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }
}
