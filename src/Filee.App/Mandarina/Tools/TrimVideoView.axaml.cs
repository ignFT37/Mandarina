using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class TrimVideoView : UserControl
{
    public TrimVideoView() => InitializeComponent();

    private TrimVideoPanel? Panel => DataContext as TrimVideoPanel;

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
        // SetCurrentValue keeps the one-way binding alive; the box shows the panel's number whatever was typed.
        box.SetCurrentValue(TextBox.TextProperty, box == StartBox ? Panel?.StartText : Panel?.EndText);
    }

    private void OnPreviousFrame(object? sender, RoutedEventArgs e) => Panel?.StepFrame(-1);

    private void OnNextFrame(object? sender, RoutedEventArgs e) => Panel?.StepFrame(1);

    private void OnMarkStart(object? sender, RoutedEventArgs e) => Panel?.MarkStart();

    private void OnMarkEnd(object? sender, RoutedEventArgs e) => Panel?.MarkEnd();

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
            await panel.PlayAsync();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => Panel?.StopPlayback();
}
