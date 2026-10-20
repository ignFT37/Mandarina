// Mandarina: the waveform of the audio panels. Rounded bars (the loudest part of each slice of the file), the chosen
// range in orange between two grip handles you drag, a playhead you click to move, and optional marked bands (the
// ranges the Bleep tool covers). It only draws and edits numbers: Peaks come from MediaTools.PeaksAsync.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Filee.App.Mandarina.Panels;

/// <summary>A stretch of a file in seconds.</summary>
public readonly record struct WaveRange(double Start, double End);

public sealed class WaveformView : Control
{
    private const double Inset = 12;          // room for the handles at 0 and at the end
    private const double BarWidth = 3;
    private const double BarStep = 5;
    private const double GrabDistance = 14;
    private const double MinimumGap = 0.05;   // seconds between the handles

    private static readonly IBrush BarIn = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x00));
    private static readonly IBrush BarOut = new SolidColorBrush(Color.FromRgb(0xCF, 0xC6, 0xD6));
    private static readonly IBrush MarkBand = new SolidColorBrush(Color.FromArgb(0x38, 0xE1, 0x1D, 0x48));
    private static readonly IBrush MarkBar = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x2B, 0x24, 0x30));
    private static readonly IBrush Empty = new SolidColorBrush(Color.FromRgb(0xCF, 0xC6, 0xD6));
    private static readonly IPen HandlePen = new Pen(BarIn, 3, lineCap: PenLineCap.Round);
    private static readonly IPen KnobPen = new Pen(BarIn, 2);
    private static readonly IPen PlayheadPen = new Pen(Ink, 2, lineCap: PenLineCap.Round);

    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<float>?>(nameof(Peaks));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Duration));

    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Start), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: (_, v) => Math.Max(0, v));

    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(End), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: (_, v) => Math.Max(0, v));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Position), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowHandlesProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowHandles), true);

    public static readonly StyledProperty<IReadOnlyList<WaveRange>?> MarksProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<WaveRange>?>(nameof(Marks));

    private static readonly Cursor Resize = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor Arrow = new(StandardCursorType.Arrow);

    private enum Drag { None, Start, End, Seek }

    private Drag _drag;

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, DurationProperty, StartProperty, EndProperty, PositionProperty, ShowHandlesProperty, MarksProperty);
        FocusableProperty.OverrideDefaultValue<WaveformView>(false);
        ClipToBoundsProperty.OverrideDefaultValue<WaveformView>(true);
    }

    /// <summary>The loudness of each slice of the file, 0..1.</summary>
    public IReadOnlyList<float>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>Length of the file in seconds.</summary>
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>Left handle, in seconds.</summary>
    public double Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    /// <summary>Right handle, in seconds.</summary>
    public double End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    /// <summary>The playhead in seconds; below zero hides it. Clicking the wave sets it.</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>False to draw the wave without handles (everything in orange).</summary>
    public bool ShowHandles
    {
        get => GetValue(ShowHandlesProperty);
        set => SetValue(ShowHandlesProperty, value);
    }

    /// <summary>Bands drawn in red (the stretches the Bleep tool covers).</summary>
    public IReadOnlyList<WaveRange>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>Raised when the user clicks the wave to move the playhead.</summary>
    public event EventHandler<double>? Seeked;

    private double TrackWidth => Math.Max(1, Bounds.Width - 2 * Inset);

    private double XOf(double seconds) => Inset + (Duration > 0 ? Math.Clamp(seconds / Duration, 0, 1) : 0) * TrackWidth;

    private double SecondsAt(double x) => Duration > 0 ? Math.Clamp((x - Inset) / TrackWidth, 0, 1) * Duration : 0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 2 * Inset || height <= 8)
            return;

        var middle = height / 2;
        var peaks = Peaks;
        var bars = (int)(TrackWidth / BarStep);
        if (peaks is not { Count: > 0 } || bars < 2)
        {
            // Not decoded yet (or no audio): a flat line of dots.
            for (var i = 0; i < Math.Max(2, bars); i++)
                context.DrawRectangle(Empty, null, new RoundedRect(new Rect(Inset + i * BarStep, middle - 1.5, BarWidth, 3), 1.5));
            DrawOverlay(context, height);
            return;
        }

        var loudest = 0f;
        foreach (var peak in peaks)
            loudest = Math.Max(loudest, peak);
        var scale = 1 / Math.Max(loudest, 0.05f);
        var from = ShowHandles ? Math.Min(Start, End) : 0;
        var to = ShowHandles ? Math.Max(Start, End) : Duration;
        var marks = Marks;

        for (var i = 0; i < bars; i++)
        {
            var first = (int)((long)i * peaks.Count / bars);
            var last = Math.Max(first + 1, (int)((long)(i + 1) * peaks.Count / bars));
            var top = 0f;
            for (var p = first; p < Math.Min(last, peaks.Count); p++)
                top = Math.Max(top, peaks[p]);
            var barHeight = Math.Max(3, Math.Min(1, top * scale) * (height - 16));
            var centre = Inset + (i + 0.5) * BarStep;
            var time = Duration > 0 ? (centre - Inset) / TrackWidth * Duration : 0;
            IBrush brush = time >= from && time <= to ? BarIn : BarOut;
            if (marks is not null)
                foreach (var mark in marks)
                    if (time >= mark.Start && time <= mark.End)
                        brush = MarkBar;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(centre - BarWidth / 2, middle - barHeight / 2, BarWidth, barHeight), 1.5));
        }
        DrawOverlay(context, height);
    }

    /// <summary>Marked bands, handles and the playhead, over the bars.</summary>
    private void DrawOverlay(DrawingContext context, double height)
    {
        if (Marks is { } marks)
            foreach (var mark in marks)
                context.DrawRectangle(MarkBand, null, new RoundedRect(new Rect(XOf(mark.Start), 0, Math.Max(2, XOf(mark.End) - XOf(mark.Start)), height), 4));

        if (ShowHandles && Duration > 0)
        {
            foreach (var seconds in new[] { Math.Min(Start, End), Math.Max(Start, End) })
            {
                var x = XOf(seconds);
                context.DrawLine(HandlePen, new Point(x, 4), new Point(x, height - 4));
                context.DrawEllipse(Brushes.White, KnobPen, new Point(x, height / 2), 7, 7);
                context.DrawLine(KnobPen, new Point(x - 2, height / 2 - 2.5), new Point(x - 2, height / 2 + 2.5));
                context.DrawLine(KnobPen, new Point(x + 2, height / 2 - 2.5), new Point(x + 2, height / 2 + 2.5));
            }
        }

        if (Position >= 0 && Duration > 0)
        {
            var x = XOf(Position);
            context.DrawLine(PlayheadPen, new Point(x, 2), new Point(x, height - 2));
            context.DrawEllipse(Ink, null, new Point(x, 4), 3.5, 3.5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var x = e.GetPosition(this).X;
        var nearStart = Math.Abs(x - XOf(Math.Min(Start, End)));
        var nearEnd = Math.Abs(x - XOf(Math.Max(Start, End)));
        if (ShowHandles && Math.Min(nearStart, nearEnd) <= GrabDistance)
            _drag = nearStart <= nearEnd ? Drag.Start : Drag.End;
        else
            _drag = Drag.Seek;
        e.Pointer.Capture(this);
        Apply(x);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (_drag != Drag.None)
        {
            Apply(x);
            return;
        }
        var near = ShowHandles && Duration > 0 &&
                   Math.Min(Math.Abs(x - XOf(Start)), Math.Abs(x - XOf(End))) <= GrabDistance;
        Cursor = near ? Resize : Arrow;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    private void Apply(double x)
    {
        var seconds = SecondsAt(x);
        switch (_drag)
        {
            case Drag.Start:
                Start = Math.Clamp(seconds, 0, Math.Max(0, End - MinimumGap));
                break;
            case Drag.End:
                End = Math.Clamp(seconds, Math.Min(Duration, Start + MinimumGap), Duration);
                break;
            case Drag.Seek:
                Position = seconds;
                Seeked?.Invoke(this, seconds);
                break;
        }
    }
}
