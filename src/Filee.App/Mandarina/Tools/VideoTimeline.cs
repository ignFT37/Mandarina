// Mandarina: the timeline of the video panels, in the family of WaveformView: a rounded track made of the film strip
// (small frames of the clip), the kept range in orange between two grip handles you drag, the part outside it faded, a
// cursor you click or drag to look at a frame, and orange flags for cut points. It only draws and edits numbers: the strip
// comes from VideoStrip, the frames from the panel. The arrow keys move the cursor one frame.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public sealed class VideoTimeline : Control
{
    private const double Inset = 12;           // room for the handles at 0 and at the end
    private const double Pad = 8;              // above and below the track: the knobs and flags stand out of it
    private const double Radius = 8;
    private const double GrabDistance = 14;
    private const double DefaultHeight = 68;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xDD, 0xE8));
    private static readonly IBrush FadeBrush = new SolidColorBrush(Color.FromArgb(0xC4, 0xF4, 0xEF, 0xF4));
    private static readonly IBrush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x00));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x2B, 0x24, 0x30));
    private static readonly IBrush Halo = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
    private static readonly IPen RangePen = new Pen(Orange, 3);
    private static readonly IPen HandlePen = new Pen(Orange, 3, lineCap: PenLineCap.Round);
    private static readonly IPen KnobPen = new Pen(Orange, 2);
    private static readonly IPen MarkerPen = new Pen(Orange, 2);
    private static readonly IPen CursorPen = new Pen(Ink, 2, lineCap: PenLineCap.Round);
    private static readonly IPen HaloPen = new Pen(Halo, 5, lineCap: PenLineCap.Round);
    private static readonly IPen DotPen = new Pen(Halo, 1.5);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<VideoTimeline, double>(nameof(Duration));

    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<VideoTimeline, double>(nameof(Start), defaultBindingMode: BindingMode.TwoWay, coerce: (_, v) => Math.Max(0, v));

    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<VideoTimeline, double>(nameof(End), defaultBindingMode: BindingMode.TwoWay, coerce: (_, v) => Math.Max(0, v));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<VideoTimeline, double>(nameof(Position), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IImage?> StripProperty =
        AvaloniaProperty.Register<VideoTimeline, IImage?>(nameof(Strip));

    public static readonly StyledProperty<IReadOnlyList<double>?> MarkersProperty =
        AvaloniaProperty.Register<VideoTimeline, IReadOnlyList<double>?>(nameof(Markers));

    public static readonly StyledProperty<bool> ShowRangeProperty =
        AvaloniaProperty.Register<VideoTimeline, bool>(nameof(ShowRange), true);

    public static readonly StyledProperty<double> FrameRateProperty =
        AvaloniaProperty.Register<VideoTimeline, double>(nameof(FrameRate));

    private static readonly Cursor Resize = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor Arrow = new(StandardCursorType.Arrow);

    private enum Drag { None, Start, End, Seek }

    private Drag _drag;
    private double _grabOffset;      // pixels from where a handle was taken to the handle's centre, so it does not jump

    static VideoTimeline()
    {
        AffectsRender<VideoTimeline>(DurationProperty, StartProperty, EndProperty, PositionProperty, StripProperty, MarkersProperty, ShowRangeProperty);
        FocusableProperty.OverrideDefaultValue<VideoTimeline>(true);
        ClipToBoundsProperty.OverrideDefaultValue<VideoTimeline>(true);
    }

    public VideoTimeline()
    {
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    /// <summary>Length of the clip in seconds.</summary>
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

    /// <summary>The cursor in seconds; below zero hides it. Clicking or dragging along the track sets it.</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>The film strip, stretched over the track (null: a plain track).</summary>
    public IImage? Strip
    {
        get => GetValue(StripProperty);
        set => SetValue(StripProperty, value);
    }

    /// <summary>Cut points in seconds, drawn as orange flags.</summary>
    public IReadOnlyList<double>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>False to draw the clip without handles and without fading anything.</summary>
    public bool ShowRange
    {
        get => GetValue(ShowRangeProperty);
        set => SetValue(ShowRangeProperty, value);
    }

    /// <summary>Frames per second of the clip: one frame is the least the handles can be apart, and the step of the arrow keys.</summary>
    public double FrameRate
    {
        get => GetValue(FrameRateProperty);
        set => SetValue(FrameRateProperty, value);
    }

    /// <summary>Raised when the user moves the cursor (a click, a drag along the track, the arrow keys).</summary>
    public event EventHandler<double>? Seeked;

    /// <summary>The track the thumbnails fill, in this control's coordinates.</summary>
    internal Rect TrackRect => new(Inset, Pad, Math.Max(0, Bounds.Width - 2 * Inset), Math.Max(0, Bounds.Height - 2 * Pad));

    /// <summary>The least seconds between the handles: one frame.</summary>
    private double Gap => 1 / FrameStep.Rate(FrameRate);

    internal double XOf(double seconds) =>
        Inset + (Duration > 0 ? Math.Clamp(seconds / Duration, 0, 1) : 0) * Math.Max(1, Bounds.Width - 2 * Inset);

    internal double SecondsAt(double x) =>
        Duration > 0 ? Math.Clamp((x - Inset) / Math.Max(1, Bounds.Width - 2 * Inset), 0, 1) * Duration : 0;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Transparent over everything, so the whole control takes the pointer.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var track = TrackRect;
        if (track.Width < 8 || track.Height < 8)
            return;

        var range = ShowRange && Duration > 0;
        var from = XOf(Math.Min(Start, End));
        var to = XOf(Math.Max(Start, End));
        using (context.PushClip(new RoundedRect(track, Radius)))
        {
            context.DrawRectangle(TrackBrush, null, track);
            if (Strip is { } strip && strip.Size.Width > 0 && strip.Size.Height > 0)
                context.DrawImage(strip, new Rect(strip.Size), track);
            if (range)
            {
                context.DrawRectangle(FadeBrush, null, new Rect(track.Left, track.Top, Math.Max(0, from - track.Left), track.Height));
                context.DrawRectangle(FadeBrush, null, new Rect(to, track.Top, Math.Max(0, track.Right - to), track.Height));
                // The kept part is framed in orange between the handles.
                context.DrawLine(RangePen, new Point(from, track.Top + 1.5), new Point(to, track.Top + 1.5));
                context.DrawLine(RangePen, new Point(from, track.Bottom - 1.5), new Point(to, track.Bottom - 1.5));
            }
        }

        if (range)
        {
            foreach (var x in new[] { from, to })
            {
                context.DrawLine(HandlePen, new Point(x, track.Top - 3), new Point(x, track.Bottom + 3));
                context.DrawEllipse(Brushes.White, KnobPen, new Point(x, Bounds.Height / 2), 7, 7);
                context.DrawLine(KnobPen, new Point(x - 2, Bounds.Height / 2 - 2.5), new Point(x - 2, Bounds.Height / 2 + 2.5));
                context.DrawLine(KnobPen, new Point(x + 2, Bounds.Height / 2 - 2.5), new Point(x + 2, Bounds.Height / 2 + 2.5));
            }
        }

        if (Markers is { Count: > 0 } markers && Duration > 0)
            foreach (var seconds in markers)
                if (seconds >= 0 && seconds <= Duration)
                    DrawFlag(context, XOf(seconds));

        if (Position >= 0 && Duration > 0)
        {
            var x = XOf(Position);
            // White under the dark line, so the cursor shows on a dark frame as well as on a light one.
            context.DrawLine(HaloPen, new Point(x, 2), new Point(x, Bounds.Height - 2));
            context.DrawLine(CursorPen, new Point(x, 2), new Point(x, Bounds.Height - 2));
            context.DrawEllipse(Ink, DotPen, new Point(x, 4), 3.5, 3.5);
        }
    }

    /// <summary>A cut point: a line down the track with a small banner at the top.</summary>
    private void DrawFlag(DrawingContext context, double x)
    {
        context.DrawLine(HaloPen, new Point(x, 1), new Point(x, Bounds.Height - 1));
        context.DrawLine(MarkerPen, new Point(x, 1), new Point(x, Bounds.Height - 1));
        var banner = new StreamGeometry();
        using (var g = banner.Open())
        {
            g.BeginFigure(new Point(x, 1), true);
            g.LineTo(new Point(x + 11, 1));
            g.LineTo(new Point(x + 8, 5.5));
            g.LineTo(new Point(x + 11, 10));
            g.LineTo(new Point(x, 10));
            g.EndFigure(true);
        }
        context.DrawGeometry(Orange, null, banner);
    }

    // ───────── Pointer and keys ─────────

    /// <summary>Takes what lies under <paramref name="at"/>: a handle when one is near, otherwise the cursor. False without a clip.</summary>
    internal bool BeginDrag(Point at)
    {
        if (Duration <= 0)
            return false;
        var startX = XOf(Math.Min(Start, End));
        var endX = XOf(Math.Max(Start, End));
        var nearStart = Math.Abs(at.X - startX);
        var nearEnd = Math.Abs(at.X - endX);
        if (ShowRange && Math.Min(nearStart, nearEnd) <= GrabDistance)
        {
            // Both handles under the pointer (a very short range): the one on the side it came from.
            var takeStart = nearStart < nearEnd || (nearStart == nearEnd && at.X <= startX);
            _drag = takeStart ? Drag.Start : Drag.End;
            _grabOffset = (takeStart ? startX : endX) - at.X;
        }
        else
        {
            _drag = Drag.Seek;
            _grabOffset = 0;
            DragTo(at);
        }
        return true;
    }

    /// <summary>Moves what <see cref="BeginDrag"/> took to the pointer position <paramref name="at"/>.</summary>
    internal void DragTo(Point at)
    {
        var seconds = SecondsAt(at.X + _grabOffset);
        switch (_drag)
        {
            case Drag.Start:
                Start = Math.Clamp(seconds, 0, Math.Max(0, End - Gap));
                break;
            case Drag.End:
                End = Math.Clamp(seconds, Math.Min(Duration, Start + Gap), Duration);
                break;
            case Drag.Seek:
                Position = seconds;
                Seeked?.Invoke(this, seconds);
                break;
        }
    }

    internal void EndDrag() => _drag = Drag.None;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !BeginDrag(e.GetPosition(this)))
            return;
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_drag != Drag.None)
        {
            DragTo(at);
            return;
        }
        var near = ShowRange && Duration > 0 && Math.Min(Math.Abs(at.X - XOf(Start)), Math.Abs(at.X - XOf(End))) <= GrabDistance;
        Cursor = near ? Resize : Arrow;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None)
            return;
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Duration <= 0 || e.Key is not (Key.Left or Key.Right))
            return;
        var seconds = FrameStep.Next(Math.Max(0, Position), FrameRate, e.Key == Key.Right ? 1 : -1, Duration);
        Position = seconds;
        Seeked?.Invoke(this, seconds);
        e.Handled = true;
    }
}
