// Mandarina: a picture with an editable crop box on top, shared by the photo and video crop panels. The picture is
// fitted to the space it gets; outside the box it is dimmed, the box has a white edge, rule-of-thirds lines and eight
// handles (corners and sides). Drag inside the box to move it, drag a handle to resize it (a fixed AspectRatio keeps the
// proportion). The box lives in pixels of the ORIGINAL picture (PixelWidth × PixelHeight), not of the preview shown, so a
// shrunken preview or a video frame can stand in for the real thing. The geometry itself is in CropMath.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Filee.App.Mandarina.Tools;

public sealed class CropSurface : Control
{
    /// <summary>Room around the picture so a handle on its very edge can still be seen and grabbed.</summary>
    private const double Inset = 12;

    /// <summary>How close (in screen pixels) the pointer must be to an edge or corner to take it.</summary>
    private const double GrabDistance = 12;

    private const double BracketLength = 16;
    private const double BarLength = 22;

    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
    private static readonly IPen EdgePen = new Pen(Brushes.White, 1.5);
    private static readonly IPen ThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), 1);
    private static readonly IPen HandleShadow = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen HandlePen = new Pen(Brushes.White, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor TopLeftCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeftCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRightCursor = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor VerticalCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor HorizontalCursor = new(StandardCursorType.SizeWestEast);

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<CropSurface, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> PixelWidthProperty =
        AvaloniaProperty.Register<CropSurface, double>(nameof(PixelWidth));

    public static readonly StyledProperty<double> PixelHeightProperty =
        AvaloniaProperty.Register<CropSurface, double>(nameof(PixelHeight));

    public static readonly StyledProperty<Rect> SelectionProperty =
        AvaloniaProperty.Register<CropSurface, Rect>(nameof(Selection), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CropSurface, double>(nameof(AspectRatio));

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<CropSurface, bool>(nameof(IsEditable), true);

    public static readonly StyledProperty<double> MinimumSizeProperty =
        AvaloniaProperty.Register<CropSurface, double>(nameof(MinimumSize), 16);

    private CropHandle _handle;
    private Point _grabbedAt;
    private Rect _boxAtGrab;

    static CropSurface()
    {
        AffectsRender<CropSurface>(SourceProperty, PixelWidthProperty, PixelHeightProperty, SelectionProperty, IsEditableProperty);
        FocusableProperty.OverrideDefaultValue<CropSurface>(false);
        ClipToBoundsProperty.OverrideDefaultValue<CropSurface>(true);
    }

    public CropSurface()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>The picture to show (a preview; it is stretched to the original's proportions).</summary>
    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Width of the ORIGINAL picture in pixels (the unit of <see cref="Selection"/>). The source's own size when 0.</summary>
    public double PixelWidth
    {
        get => GetValue(PixelWidthProperty);
        set => SetValue(PixelWidthProperty, value);
    }

    /// <summary>Height of the ORIGINAL picture in pixels.</summary>
    public double PixelHeight
    {
        get => GetValue(PixelHeightProperty);
        set => SetValue(PixelHeightProperty, value);
    }

    /// <summary>The crop box in pixels of the original picture.</summary>
    public Rect Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Width / height the box keeps while it is resized; 0 for a free box. Changing it refits the current box.</summary>
    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>False to show the box without handles and without letting it be dragged.</summary>
    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>The smallest side, in original pixels, the box can be dragged down to.</summary>
    public double MinimumSize
    {
        get => GetValue(MinimumSizeProperty);
        set => SetValue(MinimumSizeProperty, value);
    }

    /// <summary>Where the picture is drawn, in this control's coordinates (empty until it has a size and a picture).</summary>
    internal Rect ImageRect
    {
        get
        {
            var (width, height) = PixelSize();
            var room = new Size(Bounds.Width - 2 * Inset, Bounds.Height - 2 * Inset);
            if (width < 1 || height < 1 || room.Width <= 0 || room.Height <= 0)
                return default;
            var scale = Math.Min(room.Width / width, room.Height / height);
            var (w, h) = (width * scale, height * scale);
            return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        }
    }

    /// <summary>The box in this control's coordinates (cut to the picture).</summary>
    internal Rect SelectionRect
    {
        get
        {
            var image = ImageRect;
            var (width, height) = PixelSize();
            var box = Selection.Intersect(new Rect(0, 0, width, height));
            if (image.Width <= 0 || box.Width <= 0 || box.Height <= 0)
                return default;
            var (kx, ky) = (image.Width / width, image.Height / height);
            return new Rect(image.X + box.X * kx, image.Y + box.Y * ky, box.Width * kx, box.Height * ky);
        }
    }

    private (double Width, double Height) PixelSize()
    {
        double width = PixelWidth, height = PixelHeight;
        if ((width < 1 || height < 1) && Source is { } source)
            (width, height) = (Math.Round(source.Size.Width), Math.Round(source.Size.Height));
        return (width, height);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AspectRatioProperty)
            Refit();
    }

    /// <summary>A new proportion: the box becomes the largest one with it, around the same centre. Nothing to do when it already has it.</summary>
    private void Refit()
    {
        var (width, height) = PixelSize();
        var aspect = AspectRatio;
        if (aspect <= 0 || width < 1 || height < 1 || CropMath.HasAspect(Selection, aspect))
            return;
        Selection = CropMath.FitAspect(Selection, aspect, width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Transparent over everything, so the whole control takes the pointer even before a picture is there.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var image = ImageRect;
        if (image.Width <= 0)
            return;

        if (Source is { } source)
            context.DrawImage(source, new Rect(source.Size), image);

        var box = SelectionRect;
        if (box.Width <= 0 || box.Height <= 0)
            return;

        // Dim the four slabs around the box.
        Fill(context, new Rect(image.Left, image.Top, image.Width, box.Top - image.Top));
        Fill(context, new Rect(image.Left, box.Bottom, image.Width, image.Bottom - box.Bottom));
        Fill(context, new Rect(image.Left, box.Top, box.Left - image.Left, box.Height));
        Fill(context, new Rect(box.Right, box.Top, image.Right - box.Right, box.Height));

        // Rule of thirds, then the edge.
        for (var i = 1; i <= 2; i++)
        {
            var x = box.Left + box.Width * i / 3;
            var y = box.Top + box.Height * i / 3;
            context.DrawLine(ThirdsPen, new Point(x, box.Top), new Point(x, box.Bottom));
            context.DrawLine(ThirdsPen, new Point(box.Left, y), new Point(box.Right, y));
        }
        context.DrawRectangle(null, EdgePen, box);

        if (IsEditable)
            DrawHandles(context, box);
    }

    private static void Fill(DrawingContext context, Rect area)
    {
        if (area.Width > 0.01 && area.Height > 0.01)
            context.DrawRectangle(Dim, null, area);
    }

    /// <summary>Corner brackets and a short bar in the middle of each side, white over a soft shadow so they show on any picture.</summary>
    private static void DrawHandles(DrawingContext context, Rect box)
    {
        var bracket = Math.Min(BracketLength, Math.Min(box.Width, box.Height) / 3);
        var barAcross = Math.Min(BarLength, box.Width / 3) / 2;
        var barDown = Math.Min(BarLength, box.Height / 3) / 2;
        var middle = box.Center;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            void Corner(Point corner, double toX, double toY)
            {
                g.BeginFigure(new Point(corner.X + toX * bracket, corner.Y), false);
                g.LineTo(corner);
                g.LineTo(new Point(corner.X, corner.Y + toY * bracket));
                g.EndFigure(false);
            }

            void Bar(Point from, Point to)
            {
                g.BeginFigure(from, false);
                g.LineTo(to);
                g.EndFigure(false);
            }

            Corner(box.TopLeft, 1, 1);
            Corner(box.TopRight, -1, 1);
            Corner(box.BottomRight, -1, -1);
            Corner(box.BottomLeft, 1, -1);
            Bar(new Point(middle.X - barAcross, box.Top), new Point(middle.X + barAcross, box.Top));
            Bar(new Point(middle.X - barAcross, box.Bottom), new Point(middle.X + barAcross, box.Bottom));
            Bar(new Point(box.Left, middle.Y - barDown), new Point(box.Left, middle.Y + barDown));
            Bar(new Point(box.Right, middle.Y - barDown), new Point(box.Right, middle.Y + barDown));
        }
        context.DrawGeometry(null, HandleShadow, geometry);
        context.DrawGeometry(null, HandlePen, geometry);
    }

    // ───────── Pointer ─────────

    /// <summary>What lies under <paramref name="at"/> (this control's coordinates): a corner, a side, the inside of the box, or nothing.</summary>
    internal CropHandle HitTest(Point at)
    {
        var box = SelectionRect;
        if (box.Width <= 0 || box.Height <= 0)
            return CropHandle.None;
        // A small box keeps its middle: the grab zones never take more than a third of it.
        var grab = Math.Clamp(Math.Min(box.Width, box.Height) / 3, 4, GrabDistance);
        var nearLeft = Math.Abs(at.X - box.Left) <= grab;
        var nearRight = Math.Abs(at.X - box.Right) <= grab;
        var nearTop = Math.Abs(at.Y - box.Top) <= grab;
        var nearBottom = Math.Abs(at.Y - box.Bottom) <= grab;
        var withinX = at.X >= box.Left - grab && at.X <= box.Right + grab;
        var withinY = at.Y >= box.Top - grab && at.Y <= box.Bottom + grab;

        if (nearTop && nearLeft)
            return CropHandle.TopLeft;
        if (nearTop && nearRight)
            return CropHandle.TopRight;
        if (nearBottom && nearRight)
            return CropHandle.BottomRight;
        if (nearBottom && nearLeft)
            return CropHandle.BottomLeft;
        if (nearTop && withinX)
            return CropHandle.Top;
        if (nearBottom && withinX)
            return CropHandle.Bottom;
        if (nearLeft && withinY)
            return CropHandle.Left;
        if (nearRight && withinY)
            return CropHandle.Right;
        return box.Contains(at) ? CropHandle.Move : CropHandle.None;
    }

    /// <summary>Takes the handle (or the inside of the box) under <paramref name="at"/>; false when there is nothing to take.</summary>
    internal bool BeginDrag(Point at)
    {
        if (!IsEditable)
            return false;
        var handle = HitTest(at);
        if (handle == CropHandle.None)
            return false;
        (_handle, _grabbedAt, _boxAtGrab) = (handle, at, Selection);
        return true;
    }

    /// <summary>Moves what <see cref="BeginDrag"/> took to the pointer position <paramref name="at"/>.</summary>
    internal void DragTo(Point at)
    {
        var image = ImageRect;
        var (width, height) = PixelSize();
        if (_handle == CropHandle.None || image.Width <= 0)
            return;
        var dx = (at.X - _grabbedAt.X) * width / image.Width;
        var dy = (at.Y - _grabbedAt.Y) * height / image.Height;
        Selection = CropMath.Drag(_boxAtGrab, _handle, dx, dy, AspectRatio, width, height, MinimumSize);
    }

    internal void EndDrag() => _handle = CropHandle.None;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !BeginDrag(e.GetPosition(this)))
            return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_handle != CropHandle.None)
            DragTo(at);
        else
            Cursor = IsEditable ? CursorFor(HitTest(at)) : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_handle == CropHandle.None)
            return;
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private static Cursor? CursorFor(CropHandle handle) => handle switch
    {
        CropHandle.Move => MoveCursor,
        CropHandle.TopLeft => TopLeftCursor,
        CropHandle.TopRight => TopRightCursor,
        CropHandle.BottomLeft => BottomLeftCursor,
        CropHandle.BottomRight => BottomRightCursor,
        CropHandle.Top or CropHandle.Bottom => VerticalCursor,
        CropHandle.Left or CropHandle.Right => HorizontalCursor,
        _ => null,
    };
}
