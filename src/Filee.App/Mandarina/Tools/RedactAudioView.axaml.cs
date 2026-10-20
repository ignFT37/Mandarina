using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class RedactAudioView : UserControl
{
    public RedactAudioView() => InitializeComponent();

    private RedactAudioPanel? Panel => DataContext as RedactAudioPanel;

    private void OnAdd(object? sender, RoutedEventArgs e) => Panel?.AddSelection();

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
            await panel.TogglePlayAsync();
    }

    /// <summary>The ✕ of a row: its button's data context is the stretch.</summary>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BleepRange range })
            Panel?.Remove(range);
    }
}
