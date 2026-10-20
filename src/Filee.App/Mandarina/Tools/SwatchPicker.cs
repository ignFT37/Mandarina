// Mandarina: a plain colour picker for the panels (this project has no ColorPicker): a row of round swatches and a box that
// takes "#RRGGBB". Bind Hex two-way:
//   <t:SwatchPicker Hex="{Binding Color, Mode=TwoWay}" />
// Clicking a swatch sets Hex at once; typing a full six-digit colour does too, a short "#RGB" is taken when the box loses
// focus or Enter is pressed, and anything unreadable puts the current colour back.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public sealed class SwatchPicker : Grid
{
    public static readonly StyledProperty<string> HexProperty =
        AvaloniaProperty.Register<SwatchPicker, string>(nameof(Hex), "#FFFFFF", defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The swatches: neutrals, the app's own orange, coral and lilac, then a few friendly hues.</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#FFFFFF", "#F2EDE6", "#C9CCD3", "#6B7280", "#111111",
        "#FF8E3A", "#F4707F", "#FF8FB1", "#A98CEF",
        "#3B82F6", "#1E2A4A", "#2EC4B6", "#4CC38A", "#FFD54A",
    ];

    private static readonly IBrush SelectedRing = new SolidColorBrush(Color.Parse("#FF5200"));
    private static readonly IBrush Outline = new SolidColorBrush(Color.Parse("#2E000000"));

    private readonly List<(Border Ring, string Hex)> _swatches = [];
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0), Stroke = Outline, StrokeThickness = 1 };
    private readonly TextBox _box;
    private bool _syncing;

    public SwatchPicker()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 8;

        var row = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in Palette)
            row.Children.Add(MakeSwatch(hex));
        Children.Add(row);

        _box = new TextBox
        {
            Width = 108,
            MinHeight = 28,
            Padding = new Thickness(4, 3, 8, 3),
            FontSize = 12.5,
            MaxLength = 7,
            VerticalContentAlignment = VerticalAlignment.Center,
            InnerLeftContent = _dot,
        };
        SetColumn(_box, 1);
        Children.Add(_box);
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncing)
                TakeTypedColour(complete: false);
        };
        _box.LostFocus += (_, _) => TakeTypedColour(complete: true);
        _box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape))
                return;
            e.Handled = true;
            if (e.Key == Key.Enter)
                TakeTypedColour(complete: true);
            Sync();
        };
        Sync();
    }

    /// <summary>The colour as "#RRGGBB".</summary>
    public string Hex
    {
        get => GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HexProperty)
            Sync();
    }

    private Border MakeSwatch(string hex)
    {
        var ring = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Padding = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Ellipse { Fill = new SolidColorBrush(Color.Parse(hex)), Stroke = Outline, StrokeThickness = 1 },
        };
        ToolTip.SetTip(ring, hex);
        ring.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(ring).Properties.IsLeftButtonPressed)
            {
                Hex = hex;
                e.Handled = true;
            }
        };
        _swatches.Add((ring, hex));
        return ring;
    }

    /// <summary>
    /// Reads what is in the box. While typing only a full six-digit colour counts (a "#FFF" on the way to "#FFAA00" must not
    /// flash white); when typing is over (<paramref name="complete"/>) the short form is taken too.
    /// </summary>
    private void TakeTypedColour(bool complete)
    {
        var typed = _box.Text?.Trim().TrimStart('#') ?? "";
        if ((complete || typed.Length == 6) && ImageCompose.TryParseColor(typed, out var colour))
            Hex = ImageCompose.ToHex(colour);
        if (complete)
            Sync();
    }

    /// <summary>Shows <see cref="Hex"/>: the ring on the matching swatch, the dot and the text.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var valid = ImageCompose.TryParseColor(Hex, out var colour);
            var shown = valid ? ImageCompose.ToHex(colour) : Hex;
            foreach (var (ring, hex) in _swatches)
                ring.BorderBrush = valid && hex.Equals(shown, StringComparison.OrdinalIgnoreCase) ? SelectedRing : Brushes.Transparent;
            _dot.Fill = valid ? new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)) : Brushes.Transparent;
            if (_box.Text != shown)
                _box.Text = shown;
        }
        finally
        {
            _syncing = false;
        }
    }
}
