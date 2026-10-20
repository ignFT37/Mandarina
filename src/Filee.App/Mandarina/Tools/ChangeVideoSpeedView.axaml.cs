using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class ChangeVideoSpeedView : UserControl
{
    public ChangeVideoSpeedView() => InitializeComponent();

    private ChangeVideoSpeedPanel? Panel => DataContext as ChangeVideoSpeedPanel;

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: SpeedPreset preset } button)
            return;
        Panel?.SelectSpeed(preset);
        // The button flipped itself on the click; show what the panel decided (the pill clicked is the one on).
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, preset.IsSelected);
    }
}
