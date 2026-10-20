using Avalonia.Controls;

namespace Filee.App.Mandarina.Tools;

/// <summary>The options of the COLLAGE panel; everything it does is bound to <see cref="CreateCollagePanel"/>.</summary>
public partial class CreateCollageView : UserControl
{
    public CreateCollageView()
    {
        InitializeComponent();
        TightSliders.Apply(this);
    }
}
