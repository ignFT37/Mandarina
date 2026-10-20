using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class VideoSnapshotsView : UserControl
{
    public VideoSnapshotsView() => InitializeComponent();

    private VideoSnapshotsPanel? Panel => DataContext as VideoSnapshotsPanel;

    private void OnPickMode(object? sender, RoutedEventArgs e) => Choose(sender, pick: true);

    private void OnIntervalMode(object? sender, RoutedEventArgs e) => Choose(sender, pick: false);

    /// <summary>The button flipped itself on the click; the pill of the mode in use stays on, whichever one was pressed.</summary>
    private void Choose(object? sender, bool pick)
    {
        Panel?.SelectMode(pick);
        if (sender is ToggleButton button)
            button.SetCurrentValue(ToggleButton.IsCheckedProperty, Panel?.IsPickMode == pick);
    }

    private void OnCapture(object? sender, RoutedEventArgs e) => Panel?.CaptureHere();

    private void OnRemoveCapture(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SnapshotItem item })
            Panel?.RemoveCapture(item);
    }
}
