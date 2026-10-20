// Mandarina: a row of round colour swatches for the flat cover of the REDACT tools. Click one to choose it; the chosen
// one has an orange ring.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public sealed class RedactSwatchPicker : Control
{
    private const double Diameter = 26;
    private const double Gap = 12;
    private const double Pad = 4;

    private static readonly IBrush Ring = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x00));
    private static readonly IPen RingPen = new Pen(Ring, 2);
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x2B, 0x24, 0x30)), 1);
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    public static readonly StyledProperty<IReadOnlyList<Color>?> ColorsProperty =
        AvaloniaProperty.Register<RedactSwatchPicker, IReadOnlyList<Color>?>(nameof(Colors));

    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<RedactSwatchPicker, Color>(nameof(SelectedColor), defaultBindingMode: BindingMode.TwoWay);

    static RedactSwatchPicker()
    {
        AffectsRender<RedactSwatchPicker>(ColorsProperty, SelectedColorProperty);
        AffectsMeasure<RedactSwatchPicker>(ColorsProperty);
        FocusableProperty.OverrideDefaultValue<RedactSwatchPicker>(false);
    }

    public RedactSwatchPicker()
    {
        Cursor = Hand;
    }

    public IReadOnlyList<Color>? Colors
    {
        get => GetValue(ColorsProperty);
        set => SetValue(ColorsProperty, value);
    }

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Colors?.Count ?? 0;
        return new Size(count == 0 ? 0 : 2 * Pad + count * Diameter + (count - 1) * Gap, Diameter + 2 * Pad);
    }

    private static double CentreOf(int index) => Pad + Diameter / 2 + index * (Diameter + Gap);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Colors is not { } colors)
            return;
        for (var i = 0; i < colors.Count; i++)
        {
            var centre = new Point(CentreOf(i), Bounds.Height / 2);
            context.DrawEllipse(new SolidColorBrush(colors[i]), EdgePen, centre, Diameter / 2, Diameter / 2);
            if (colors[i] == SelectedColor)
                context.DrawEllipse(null, RingPen, centre, Diameter / 2 + 3, Diameter / 2 + 3);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Colors is not { } colors || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var x = e.GetPosition(this).X;
        for (var i = 0; i < colors.Count; i++)
        {
            if (Math.Abs(x - CentreOf(i)) <= Diameter / 2 + Gap / 2)
            {
                SelectedColor = colors[i];
                e.Handled = true;
                return;
            }
        }
    }
}
