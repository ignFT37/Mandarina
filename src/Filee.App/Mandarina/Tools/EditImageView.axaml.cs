using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

/// <summary>
/// A group button of the panel: a ToggleButton.pchip that behaves like a tab, so pressing the one that is open leaves it
/// open (a plain ToggleButton would turn itself off, and a two-way binding does not turn it back on).
/// </summary>
public sealed class EditTabButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
        if (IsChecked != true)
            base.Toggle();
    }
}

public partial class EditImageView : UserControl
{
    public EditImageView()
    {
        InitializeComponent();
        // A button keeps the press and the release for itself (they are "handled"): listen to them anyway. The hold also
        // ends if the pointer is taken away (focus lost, window closed).
        CompareButton.AddHandler(PointerPressedEvent, OnComparePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        CompareButton.AddHandler(PointerReleasedEvent, OnCompareReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        CompareButton.PointerCaptureLost += OnCompareReleased;
    }

    private EditImagePanel? Panel => DataContext as EditImagePanel;

    private void OnComparePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(CompareButton).Properties.IsLeftButtonPressed)
            Panel?.HoldCompare(true);
    }

    private void OnCompareReleased(object? sender, RoutedEventArgs e) => Panel?.HoldCompare(false);

    private void OnReset(object? sender, RoutedEventArgs e) => Panel?.ResetAll();
}
