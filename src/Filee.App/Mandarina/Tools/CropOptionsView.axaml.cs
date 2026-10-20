using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class CropOptionsView : UserControl
{
    public CropOptionsView() => InitializeComponent();

    private CropPanelBase? Panel => DataContext as CropPanelBase;

    private void OnRatioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: CropRatio ratio } button)
            return;
        Panel?.SelectRatio(ratio);
        // The button flipped itself on the click; show what the panel decided (the pill clicked is the one on).
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, ratio.IsSelected);
    }

    private void OnRotate(object? sender, RoutedEventArgs e) => Panel?.RotateRatio();

    private void OnWidthCommit(object? sender, RoutedEventArgs e) => Commit(WidthBox);

    private void OnHeightCommit(object? sender, RoutedEventArgs e) => Commit(HeightBox);

    /// <summary>Enter takes the number typed; Escape puts the old one back.</summary>
    private void OnSizeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || sender is not TextBox box)
            return;
        e.Handled = true;
        if (e.Key == Key.Enter)
            Commit(box);
        else
            Show(box);
    }

    /// <summary>Pixels are whole numbers: letters and signs are not typed.</summary>
    private void OnSizeTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } text && !text.All(char.IsAsciiDigit))
            e.Handled = true;
    }

    private void Commit(TextBox box)
    {
        if (box == WidthBox)
            Panel?.CommitWidth(box.Text);
        else
            Panel?.CommitHeight(box.Text);
        Show(box);
    }

    /// <summary>Writes the panel's number in the box (it does not change when what was typed gave the same one).</summary>
    private void Show(TextBox box) =>
        box.SetCurrentValue(TextBox.TextProperty, box == WidthBox ? Panel?.WidthText : Panel?.HeightText);
}
