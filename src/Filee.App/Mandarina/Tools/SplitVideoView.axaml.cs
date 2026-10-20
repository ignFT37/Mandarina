using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class SplitVideoView : UserControl
{
    public SplitVideoView() => InitializeComponent();

    private SplitVideoPanel? Panel => DataContext as SplitVideoPanel;

    private void OnAddCut(object? sender, RoutedEventArgs e) => Panel?.AddCutHere();

    private void OnRemoveCut(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SplitCut cut })
            Panel?.RemoveCut(cut);
    }

    private void OnSplitEvenly(object? sender, RoutedEventArgs e) => Panel?.SplitEvenly();
}
