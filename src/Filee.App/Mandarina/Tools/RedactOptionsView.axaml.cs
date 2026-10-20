using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class RedactOptionsView : UserControl
{
    public RedactOptionsView() => InitializeComponent();

    private RedactPanelBase? Panel => DataContext as RedactPanelBase;

    private async void OnDetectFaces(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
            await panel.DetectFacesAsync();
    }

    private async void OnDetectText(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
            await panel.DetectTextAsync();
    }

    private void OnClear(object? sender, RoutedEventArgs e) => Panel?.ClearRegions();

    /// <summary>The button pressed picks its effect. Pressing the chosen one again would un-tick it, and one is always chosen.</summary>
    private void OnEffect(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string name } button || Panel is not { } panel || !Enum.TryParse<RedactEffect>(name, out var effect))
            return;
        panel.Effect = effect;
        // SetCurrentValue keeps the binding that follows the panel's choice.
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
    }
}
