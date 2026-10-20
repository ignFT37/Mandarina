using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class TrimAudioView : UserControl
{
    public TrimAudioView() => InitializeComponent();

    private TrimAudioPanel? Panel => DataContext as TrimAudioPanel;

    private void OnStartCommit(object? sender, RoutedEventArgs e) => Panel?.CommitStart(StartBox.Text);

    private void OnEndCommit(object? sender, RoutedEventArgs e) => Panel?.CommitEnd(EndBox.Text);

    /// <summary>Enter takes the time typed; Escape puts the old one back.</summary>
    private void OnTimeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || sender is not TextBox box)
            return;
        e.Handled = true;
        if (e.Key == Key.Enter)
        {
            if (box == StartBox)
                Panel?.CommitStart(box.Text);
            else
                Panel?.CommitEnd(box.Text);
        }
        else if (box == StartBox)
        {
            Panel?.CommitStart(null);
        }
        else
        {
            Panel?.CommitEnd(null);
        }
        box.Text = box == StartBox ? Panel?.StartText : Panel?.EndText;
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
            await panel.TogglePlayAsync();
    }

    private void OnSilence(object? sender, RoutedEventArgs e) => Panel?.TrimSilence();

    private void OnReset(object? sender, RoutedEventArgs e) => Panel?.Reset();
}
