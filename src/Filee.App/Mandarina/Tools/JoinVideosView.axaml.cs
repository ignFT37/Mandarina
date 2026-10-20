using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class JoinVideosView : UserControl
{
    public JoinVideosView() => InitializeComponent();

    private JoinVideosPanel? Panel => DataContext as JoinVideosPanel;

    private void OnUp(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: JoinRow clip })
            Panel?.MoveUp(clip);
    }

    private void OnDown(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: JoinRow clip })
            Panel?.MoveDown(clip);
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: JoinRow clip })
            Panel?.Remove(clip);
    }

    private void OnSizeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: JoinSizeOption option } button)
            return;
        Panel?.SelectSize(option);
        // The button flipped itself on the click; show what the panel decided (the pill clicked is the one on).
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, option.IsSelected);
    }
}
