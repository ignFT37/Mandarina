// Mandarina: a colour picker for the VISUALIZE panel: a row of round swatches (the ones in Palette) and a box that takes "#RRGGBB".
// Bind Hex two-way:
//   <t:VisualSwatchPicker Palette="{x:Static t:VisualColors.WavePalette}" Hex="{Binding WaveColor, Mode=TwoWay}" />
// Clicking a swatch sets Hex at once; typing a full six-digit colour does too, a short "#RGB" is taken when the box loses focus or
// Enter is pressed, and anything unreadable puts the current colour back.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public sealed class VisualSwatchPicker : Grid
{
    public static readonly StyledProperty<string> HexProperty =
        AvaloniaProperty.Register<VisualSwatchPicker, string>(nameof(Hex), "#FFFFFF", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyList<string>?> PaletteProperty =
        AvaloniaProperty.Register<VisualSwatchPicker, IReadOnlyList<string>?>(nameof(Palette));

    private static readonly IBrush SelectedRing = new SolidColorBrush(Color.Parse("#FF5200"));
    private static readonly IBrush Outline = new SolidColorBrush(Color.Parse("#2E000000"));

    private readonly UniformGrid _row = new() { Rows = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(Border Ring, string Hex)> _swatches = [];
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0), Stroke = Outline, StrokeThickness = 1 };
    private readonly TextBox _box;
    private bool _syncing;

    public VisualSwatchPicker()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 10;
        Children.Add(_row);

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

    /// <summary>The swatches, each "#RRGGBB".</summary>
    public IReadOnlyList<string>? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty)
            BuildSwatches();
        else if (change.Property == HexProperty)
            Sync();
    }

    private void BuildSwatches()
    {
        _row.Children.Clear();
        _swatches.Clear();
        foreach (var hex in Palette ?? [])
            _row.Children.Add(MakeSwatch(hex));
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
        if ((complete || typed.Length == 6) && VisualColors.TryParse(typed, out var colour))
            Hex = VisualColors.ToHex(colour);
        if (complete)
            Sync();
    }

    /// <summary>Shows <see cref="Hex"/>: the ring on the matching swatch, the dot and the text.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var valid = VisualColors.TryParse(Hex, out var colour);
            var shown = valid ? VisualColors.ToHex(colour) : Hex;
            foreach (var (ring, hex) in _swatches)
                ring.BorderBrush = valid && hex.Equals(shown, StringComparison.OrdinalIgnoreCase) ? SelectedRing : Brushes.Transparent;
            _dot.Fill = valid ? new SolidColorBrush(colour) : Brushes.Transparent;
            if (_box.Text != shown)
                _box.Text = shown;
        }
        finally
        {
            _syncing = false;
        }
    }
}
