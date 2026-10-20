// Mandarina: the editor of the REDACT tools. It shows a picture (or a video frame) fitted into its box and, over it, a
// list of rectangles to cover. Drag over empty space to draw one; click one to select it (handles appear), drag it to
// move it or a handle to resize it, press Delete to remove it. Rectangles are kept in pixels of the ORIGINAL picture
// (PixelWidth × PixelHeight), whatever size the shown bitmap has, so the same editor serves a photo and a video.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Filee.App.Mandarina.Tools;

public sealed class RegionEditor : Control
{
    /// <summary>The smallest rectangle, in pixels of the original (and never under this many screen pixels either).</summary>
    public const double MinimumPixels = 8;

    private const double HandleSize = 9;
    private const double HandleGrab = 8;
    private const double DragThreshold = 3;

    private static readonly Color Orange = Color.FromRgb(0xFF, 0x52, 0x00);
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Orange);
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x52, 0x00));
    private static readonly IBrush NormalFill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush NewFill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x52, 0x00));
    private static readonly IPen SelectedPen = new Pen(OrangeBrush, 2);
    private static readonly IPen NormalPen = new Pen(Brushes.White, 1.5);
    private static readonly IPen NormalShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)), 1);
    private static readonly IPen InactivePen = new Pen(Brushes.White, 1.5, new DashStyle([4, 3], 0));
    private static readonly IPen NewPen = new Pen(OrangeBrush, 1.5, new DashStyle([5, 3], 0));
    private static readonly IPen HandlePen = new Pen(OrangeBrush, 1.5);

    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor NorthWestCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor NorthEastCursor = new(StandardCursorType.TopRightCorner);

    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<RegionEditor, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> PixelWidthProperty = AvaloniaProperty.Register<RegionEditor, double>(nameof(PixelWidth));

    public static readonly StyledProperty<double> PixelHeightProperty = AvaloniaProperty.Register<RegionEditor, double>(nameof(PixelHeight));

    public static readonly StyledProperty<ObservableCollection<RedactRegion>?> RegionsProperty =
        AvaloniaProperty.Register<RegionEditor, ObservableCollection<RedactRegion>?>(nameof(Regions));

    public static readonly StyledProperty<RedactRegion?> SelectedRegionProperty =
        AvaloniaProperty.Register<RegionEditor, RedactRegion?>(nameof(SelectedRegion), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> CoverBrushProperty = AvaloniaProperty.Register<RegionEditor, IBrush?>(nameof(CoverBrush));

    public static readonly StyledProperty<double?> CurrentTimeProperty = AvaloniaProperty.Register<RegionEditor, double?>(nameof(CurrentTime));

    /// <summary>Which edges a resize drags: a corner is two of them.</summary>
    [Flags]
    private enum Edges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private enum Drag
    {
        None,
        Create,
        Move,
        Resize,
    }

    private readonly List<RedactRegion> _watched = [];
    private ObservableCollection<RedactRegion>? _collection;
    private Drag _drag;
    private RedactRegion? _target;
    private Edges _edges;
    private Point _pressView;
    private Point _pressImage;
    private Rect _startRect;
    private bool _moved;
    private Rect? _pending;

    static RegionEditor()
    {
        AffectsRender<RegionEditor>(SourceProperty, PixelWidthProperty, PixelHeightProperty, SelectedRegionProperty, CoverBrushProperty, CurrentTimeProperty);
        FocusableProperty.OverrideDefaultValue<RegionEditor>(true);
        ClipToBoundsProperty.OverrideDefaultValue<RegionEditor>(true);
    }

    public RegionEditor()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>What is shown: the photo's preview or the video frame (any size; it is fitted into the box).</summary>
    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Width in pixels of the ORIGINAL picture (the unit of every rectangle); 0 = the shown bitmap's own.</summary>
    public double PixelWidth
    {
        get => GetValue(PixelWidthProperty);
        set => SetValue(PixelWidthProperty, value);
    }

    /// <summary>Height in pixels of the original picture; 0 = the shown bitmap's own.</summary>
    public double PixelHeight
    {
        get => GetValue(PixelHeightProperty);
        set => SetValue(PixelHeightProperty, value);
    }

    /// <summary>The rectangles. The editor adds the ones the user draws and removes the one Delete takes away.</summary>
    public ObservableCollection<RedactRegion>? Regions
    {
        get => GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    /// <summary>The region with the handles, in orange; null when none is picked.</summary>
    public RedactRegion? SelectedRegion
    {
        get => GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    /// <summary>When set, the rectangles are drawn filled with it (a flat cover is what the result will look like); null = translucent white.</summary>
    public IBrush? CoverBrush
    {
        get => GetValue(CoverBrushProperty);
        set => SetValue(CoverBrushProperty, value);
    }

    /// <summary>The second of the video being shown: regions not covered then are drawn faint and dashed. Null for a photo.</summary>
    public double? CurrentTime
    {
        get => GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    /// <summary>Where the picture is drawn, in this control's coordinates (empty before there is a picture).</summary>
    public Rect ImageRect
    {
        get
        {
            var (width, height) = OriginalSize;
            if (width <= 0 || height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
                return default;
            var scale = Math.Min(Bounds.Width / width, Bounds.Height / height);
            var size = new Size(width * scale, height * scale);
            return new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        }
    }

    /// <summary>Screen pixels per pixel of the original.</summary>
    public double Scale => OriginalSize.Width > 0 ? ImageRect.Width / OriginalSize.Width : 0;

    /// <summary>The smallest side a region can have right now, in pixels of the original.</summary>
    public double MinimumSize => Scale > 0 ? Math.Max(MinimumPixels, MinimumPixels / Scale) : MinimumPixels;

    /// <summary>A point of the original picture as a point of this control.</summary>
    public Point ToControl(Point imagePoint)
    {
        var image = ImageRect;
        var scale = Scale;
        return new Point(image.X + imagePoint.X * scale, image.Y + imagePoint.Y * scale);
    }

    /// <summary>A point of this control as a point of the original picture (not limited to the picture).</summary>
    public Point ToImage(Point controlPoint)
    {
        var image = ImageRect;
        var scale = Scale;
        return scale > 0 ? new Point((controlPoint.X - image.X) / scale, (controlPoint.Y - image.Y) / scale) : default;
    }

    private (double Width, double Height) OriginalSize =>
        PixelWidth > 0 && PixelHeight > 0 ? (PixelWidth, PixelHeight)
        : Source is { } source ? (source.Size.Width, source.Size.Height)
        : (0, 0);

    private Rect ToControl(Rect imageRect)
    {
        var topLeft = ToControl(imageRect.TopLeft);
        var scale = Scale;
        return new Rect(topLeft.X, topLeft.Y, imageRect.Width * scale, imageRect.Height * scale);
    }

    private static double Clamp(double value, double low, double high) => high < low ? low : Math.Min(Math.Max(value, low), high);

    // ───────── Following the list ─────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RegionsProperty)
        {
            if (_collection is not null)
                _collection.CollectionChanged -= OnCollectionChanged;
            _collection = change.GetNewValue<ObservableCollection<RedactRegion>?>();
            if (_collection is not null)
                _collection.CollectionChanged += OnCollectionChanged;
            Rewatch();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rewatch();

    /// <summary>Listens to every region of the list (a change of rectangle or time redraws) and drops the selection of one that left.</summary>
    private void Rewatch()
    {
        foreach (var region in _watched)
            region.PropertyChanged -= OnRegionChanged;
        _watched.Clear();
        if (_collection is not null)
        {
            _watched.AddRange(_collection);
            foreach (var region in _watched)
                region.PropertyChanged += OnRegionChanged;
        }
        if (SelectedRegion is { } selected && !_watched.Contains(selected))
            SelectedRegion = null;
        InvalidateVisual();
    }

    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ───────── Drawing ─────────

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Transparent, not nothing: that is what makes the whole box take the pointer.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var image = ImageRect;
        if (Source is not { } source || image.Width <= 0)
            return;
        context.DrawImage(source, new Rect(source.Size), image);

        if (Regions is { } regions)
        {
            foreach (var region in regions)
                if (region != SelectedRegion)
                    DrawRegion(context, region, selected: false);
            if (SelectedRegion is { } selected && regions.Contains(selected))
                DrawRegion(context, selected, selected: true);
        }
        if (_pending is { } pending)
        {
            var view = ToControl(pending);
            context.DrawRectangle(NewFill, NewPen, view);
        }
    }

    private void DrawRegion(DrawingContext context, RedactRegion region, bool selected)
    {
        var view = ToControl(region.Rect);
        var active = CurrentTime is not { } time || region.IsActiveAt(time);
        using (context.PushOpacity(active ? 1 : 0.45))
        {
            IBrush fill = CoverBrush ?? (selected ? SelectedFill : NormalFill);
            if (selected)
            {
                context.DrawRectangle(fill, SelectedPen, view);
            }
            else
            {
                // A dark hairline outside the white one keeps a box visible on a white sheet of paper.
                context.DrawRectangle(null, NormalShadowPen, view.Inflate(1));
                context.DrawRectangle(fill, active ? NormalPen : InactivePen, view);
            }
        }
        if (selected)
            foreach (var (edges, centre) in Handles(view))
                context.DrawRectangle(Brushes.White, HandlePen, new Rect(centre.X - HandleSize / 2, centre.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    /// <summary>The handles of a rectangle on screen: the four corners, and the four middles when the sides are long enough.</summary>
    private static IEnumerable<(Edges Edges, Point Centre)> Handles(Rect view)
    {
        yield return (Edges.Left | Edges.Top, view.TopLeft);
        yield return (Edges.Right | Edges.Top, view.TopRight);
        yield return (Edges.Left | Edges.Bottom, view.BottomLeft);
        yield return (Edges.Right | Edges.Bottom, view.BottomRight);
        if (view.Width >= 3 * HandleSize)
        {
            yield return (Edges.Top, new Point(view.Center.X, view.Top));
            yield return (Edges.Bottom, new Point(view.Center.X, view.Bottom));
        }
        if (view.Height >= 3 * HandleSize)
        {
            yield return (Edges.Left, new Point(view.Left, view.Center.Y));
            yield return (Edges.Right, new Point(view.Right, view.Center.Y));
        }
    }

    // ───────── Hit testing ─────────

    private Edges HandleAt(RedactRegion region, Point point)
    {
        foreach (var (edges, centre) in Handles(ToControl(region.Rect)))
            if (Math.Abs(point.X - centre.X) <= HandleGrab && Math.Abs(point.Y - centre.Y) <= HandleGrab)
                return edges;
        return Edges.None;
    }

    /// <summary>The topmost region under <paramref name="point"/> (a couple of pixels of slack around thin ones).</summary>
    private RedactRegion? RegionAt(Point point)
    {
        if (Regions is not { } regions)
            return null;
        for (var i = regions.Count - 1; i >= 0; i--)
            if (ToControl(regions[i].Rect).Inflate(2).Contains(point))
                return regions[i];
        return null;
    }

    private static Cursor CursorFor(Edges edges) => edges switch
    {
        Edges.Left or Edges.Right => WestEastCursor,
        Edges.Top or Edges.Bottom => NorthSouthCursor,
        Edges.Left | Edges.Top or Edges.Right | Edges.Bottom => NorthWestCursor,
        _ => NorthEastCursor,
    };

    // ───────── Pointer ─────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var image = ImageRect;
        // Nothing to draw on until the picture is there (a video frame takes a moment): the boxes would be invisible.
        if (Source is null || image.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var point = e.GetPosition(this);
        _pressView = point;
        _pressImage = ToImage(point);
        _moved = false;
        _pending = null;

        if (SelectedRegion is { } selected && Regions?.Contains(selected) == true && HandleAt(selected, point) is var edges and not Edges.None)
        {
            (_drag, _target, _edges, _startRect) = (Drag.Resize, selected, edges, selected.Rect);
        }
        else if (RegionAt(point) is { } hit)
        {
            SelectedRegion = hit;
            (_drag, _target, _startRect) = (Drag.Move, hit, hit.Rect);
        }
        else if (image.Contains(point))
        {
            SelectedRegion = null;
            _drag = Drag.Create;
        }
        else
        {
            SelectedRegion = null;
            return;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            UpdateCursor(point);
            return;
        }
        if (!_moved)
        {
            if (Math.Abs(point.X - _pressView.X) < DragThreshold && Math.Abs(point.Y - _pressView.Y) < DragThreshold)
                return;
            _moved = true;
        }

        var (width, height) = OriginalSize;
        var here = ToImage(point);
        switch (_drag)
        {
            case Drag.Create:
            {
                var start = new Point(Clamp(_pressImage.X, 0, width), Clamp(_pressImage.Y, 0, height));
                var end = new Point(Clamp(here.X, 0, width), Clamp(here.Y, 0, height));
                _pending = new Rect(
                    new Point(Math.Round(Math.Min(start.X, end.X)), Math.Round(Math.Min(start.Y, end.Y))),
                    new Point(Math.Round(Math.Max(start.X, end.X)), Math.Round(Math.Max(start.Y, end.Y))));
                InvalidateVisual();
                break;
            }
            case Drag.Move when _target is { } moving:
            {
                var x = Clamp(Math.Round(_startRect.X + here.X - _pressImage.X), 0, width - _startRect.Width);
                var y = Clamp(Math.Round(_startRect.Y + here.Y - _pressImage.Y), 0, height - _startRect.Height);
                moving.Rect = new Rect(x, y, _startRect.Width, _startRect.Height);
                break;
            }
            case Drag.Resize when _target is { } resizing:
            {
                double left = _startRect.Left, top = _startRect.Top, right = _startRect.Right, bottom = _startRect.Bottom;
                var minimum = MinimumSize;
                if (_edges.HasFlag(Edges.Left))
                    left = Clamp(Math.Round(here.X), 0, right - minimum);
                if (_edges.HasFlag(Edges.Right))
                    right = Clamp(Math.Round(here.X), left + minimum, width);
                if (_edges.HasFlag(Edges.Top))
                    top = Clamp(Math.Round(here.Y), 0, bottom - minimum);
                if (_edges.HasFlag(Edges.Bottom))
                    bottom = Clamp(Math.Round(here.Y), top + minimum, height);
                resizing.Rect = new Rect(left, top, right - left, bottom - top);
                break;
            }
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None)
            return;
        if (_drag == Drag.Create && _pending is { } drawn && drawn.Width >= MinimumSize && drawn.Height >= MinimumSize && Regions is { } regions)
        {
            var region = new RedactRegion(drawn);
            regions.Add(region);
            SelectedRegion = region;
        }
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _drag = Drag.None;
        _target = null;
        _pending = null;
        InvalidateVisual();
    }

    private void UpdateCursor(Point point)
    {
        var image = ImageRect;
        if (image.Width <= 0)
        {
            Cursor = ArrowCursor;
            return;
        }
        if (SelectedRegion is { } selected && Regions?.Contains(selected) == true && HandleAt(selected, point) is var edges and not Edges.None)
            Cursor = CursorFor(edges);
        else if (RegionAt(point) is not null)
            Cursor = MoveCursor;
        else
            Cursor = image.Contains(point) ? CrossCursor : ArrowCursor;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && SelectedRegion is { } selected && Regions is { } regions)
        {
            regions.Remove(selected);
            SelectedRegion = null;
            e.Handled = true;
        }
    }
}
