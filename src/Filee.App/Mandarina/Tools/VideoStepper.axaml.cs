using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace Filee.App.Mandarina.Tools;

public partial class VideoStepper : UserControl
{
    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<VideoStepper, int>(nameof(Minimum), 0);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<VideoStepper, int>(nameof(Maximum), 100);

    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<VideoStepper, int>(nameof(Value), 0, defaultBindingMode: BindingMode.TwoWay,
            coerce: (owner, value) => owner is VideoStepper stepper ? Math.Clamp(value, stepper.Minimum, Math.Max(stepper.Minimum, stepper.Maximum)) : value);

    public VideoStepper()
    {
        InitializeComponent();
        Refresh();
    }

    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The number shown, always between <see cref="Minimum"/> and <see cref="Maximum"/>.</summary>
    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Moves the number by <paramref name="delta"/>, as far as its limits allow.</summary>
    internal void Step(int delta) => Value += delta;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MinimumProperty || change.Property == MaximumProperty)
            CoerceValue(ValueProperty);
        if (change.Property == MinimumProperty || change.Property == MaximumProperty || change.Property == ValueProperty)
            Refresh();
    }

    private void Refresh()
    {
        Number.Text = Value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Less.IsEnabled = Value > Minimum;
        More.IsEnabled = Value < Maximum;
    }

    private void OnLess(object? sender, RoutedEventArgs e) => Step(-1);

    private void OnMore(object? sender, RoutedEventArgs e) => Step(1);
}
