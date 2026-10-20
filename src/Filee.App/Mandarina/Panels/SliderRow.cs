// Mandarina: one labelled slider row for the option cards: label on the left, the slider, its value on the right.
// Double-tap the label to put the slider back to DefaultValue. Used as
//   <p:SliderRow Label="Exposure" Minimum="-100" Maximum="100" Value="{Binding Exposure}" />

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Filee.App.Mandarina.Panels;

public sealed class SliderRow : Grid
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<SliderRow, string?>(nameof(Label));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<SliderRow, double>(nameof(Minimum), 0);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<SliderRow, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SliderRow, double>(nameof(Value), 0, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<SliderRow, double>(nameof(DefaultValue), 0);
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<SliderRow, double>(nameof(Step), 1);

    /// <summary>A .NET format for the number on the right ("0", "0.0", "0'%'"); the invariant culture is not used, so it follows the user's.</summary>
    public static readonly StyledProperty<string> ValueFormatProperty = AvaloniaProperty.Register<SliderRow, string>(nameof(ValueFormat), "0");

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly Slider _slider = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
    private readonly TextBlock _value = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        FontSize = 12.5,
        FontWeight = FontWeight.SemiBold,
    };

    public SliderRow()
    {
        ColumnDefinitions = new ColumnDefinitions("112,*,52");
        Children.Add(_label);
        Grid.SetColumn(_slider, 1);
        Children.Add(_slider);
        Grid.SetColumn(_value, 2);
        Children.Add(_value);
        // Fluent's Slider keeps 15 px free above and below its track for tick marks: a row would be 50 px high. No ticks here.
        _slider.MinHeight = 24;
        _slider.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Grid>("HorizontalTemplate") is { RowDefinitions.Count: 3 } grid)
                grid.RowDefinitions[0].Height = grid.RowDefinitions[2].Height = new GridLength(0);
        };
        _label.Classes.Add("plabel");
        _label.Background = Brushes.Transparent;
        _label.DoubleTapped += (_, _) => Value = DefaultValue;
        _label.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(_label, "↺");

        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
                return;
            if (Math.Abs(Value - _slider.Value) > 1e-9)
                Value = _slider.Value;
            ShowValue();
        };
        Sync();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double DefaultValue
    {
        get => GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string ValueFormat
    {
        get => GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == MinimumProperty || change.Property == MaximumProperty
            || change.Property == ValueProperty || change.Property == StepProperty || change.Property == ValueFormatProperty)
            Sync();
    }

    private void Sync()
    {
        _label.Text = Label;
        _slider.Minimum = Minimum;
        _slider.Maximum = Maximum;
        _slider.TickFrequency = Step;
        _slider.SmallChange = Step;
        if (Math.Abs(_slider.Value - Value) > 1e-9)
            _slider.Value = Value;
        ShowValue();
    }

    private void ShowValue() => _value.Text = Value.ToString(ValueFormat, System.Globalization.CultureInfo.CurrentCulture);
}
