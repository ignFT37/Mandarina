using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Filee.App.Mandarina.Tools;

/// <summary>
/// One of the two pills (Edit · Remove all): a ToggleButton.pchip that behaves like a choice, so pressing the one that is on
/// leaves it on (a plain ToggleButton would turn itself off, and a two-way binding would not turn it back on).
/// </summary>
public sealed class MetadataModeButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
        if (IsChecked != true)
            base.Toggle();
    }
}

public partial class MetadataView : UserControl
{
    public MetadataView() => InitializeComponent();
}
