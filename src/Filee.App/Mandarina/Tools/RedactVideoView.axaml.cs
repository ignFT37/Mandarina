using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class RedactVideoView : UserControl
{
    public RedactVideoView() => InitializeComponent();

    private RedactVideoPanel? Panel => DataContext as RedactVideoPanel;

    private void OnFromCommit(object? sender, RoutedEventArgs e) => Panel?.CommitSelectedStart(FromBox.Text);

    private void OnToCommit(object? sender, RoutedEventArgs e) => Panel?.CommitSelectedEnd(ToBox.Text);

    /// <summary>Enter takes the time typed; Escape puts the old one back.</summary>
    private void OnTimeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || sender is not TextBox box)
            return;
        e.Handled = true;
        var from = box == FromBox;
        var text = e.Key == Key.Enter ? box.Text : null;
        if (from)
            Panel?.CommitSelectedStart(text);
        else
            Panel?.CommitSelectedEnd(text);
        box.Text = from ? Panel?.SelectedStartText : Panel?.SelectedEndText;
    }
}
