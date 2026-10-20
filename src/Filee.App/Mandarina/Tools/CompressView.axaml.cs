using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class CompressView : UserControl
{
    public CompressView() => InitializeComponent();

    private CompressPanel? Panel => DataContext as CompressPanel;

    private void OnPresetMode(object? sender, RoutedEventArgs e) => Choose(sender, CompressMode.Preset);

    private void OnPercentMode(object? sender, RoutedEventArgs e) => Choose(sender, CompressMode.Percent);

    private void OnTargetMode(object? sender, RoutedEventArgs e) => Choose(sender, CompressMode.TargetSize);

    private void Choose(object? sender, CompressMode mode)
    {
        Panel?.SelectMode(mode);
        // The chip flipped itself on the click; show what the panel decided (the chip clicked is the one on).
        if (sender is ToggleButton button)
            button.SetCurrentValue(ToggleButton.IsCheckedProperty, Panel?.Mode == mode);
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: CompressPanelPreset preset } button)
            return;
        Panel?.SelectPreset(preset);
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, preset.IsSelected);
    }

    /// <summary>A size is digits with at most one decimal separator ("1.5", "1,5"): letters and signs are not typed.</summary>
    private void OnTargetTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || sender is not TextBox box)
            return;
        var hasSeparator = box.Text is { } current && (current.Contains('.') || current.Contains(','));
        if (!text.All(c => char.IsAsciiDigit(c) || (!hasSeparator && c is '.' or ',')))
            e.Handled = true;
    }
}
