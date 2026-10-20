// Mandarina: the canvas of the ANNOTATE tool. ONE piece of drawing code (AnnotationPainter) paints the photo with its
// marks, on screen scaled to fit its slot and offscreen at the photo's own resolution for the saved PNG, so what you see
// is what you get (strokes and text scale with the photo; see Annotation). The same control edits: a drawing tool makes
// a mark by dragging, Select moves and resizes marks, Text opens an in-place TextBox. Every change goes through
// AnnotationDocument, so each action can be undone. The pointer logic lives in BeginGesture/UpdateGesture/EndGesture
// (view coordinates in, document edits out) so tests can drive it without a mouse.

using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Filee.App.Mandarina.Tools;

/// <summary>How marks are drawn, measured and hit. Everything is in image pixels; callers push the view transform.</summary>
internal static class AnnotationPainter
{
    /// <summary>The photo's longer side that thickness 1 and font size 1 are measured against.</summary>
    public const double ReferenceSide = 1600;

    private const double FillAlpha = 0.3;
    private const double CalloutDiscRatio = 0.8;

    private static readonly Dictionary<(string Text, double Size, string Family), FormattedText> Texts = [];

    /// <summary>Image pixels per thickness unit for a photo of this size.</summary>
    public static double UnitOf(Size image) => Math.Max(1, Math.Max(image.Width, image.Height)) / ReferenceSide;

    public static double StrokeOf(Annotation a, double unit) => Math.Max(1, a.Thickness * unit);

    public static double RadiusOf(Annotation a, double unit) => Math.Max(6, a.FontSize * unit * CalloutDiscRatio);

    /// <summary>White or near-black text, whichever reads better on <paramref name="fill"/>.</summary>
    public static Color OnColor(Color fill)
    {
        static double Linear(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        // Leaning towards white: red and orange discs keep their white digit, yellow and green get a black one.
        var luminance = 0.2126 * Linear(fill.R) + 0.7152 * Linear(fill.G) + 0.0722 * Linear(fill.B);
        return luminance > 0.35 ? Colors.Black : Colors.White;
    }

    public static void Draw(DrawingContext context, Annotation a, int number, double unit, FontFamily family)
    {
        if (a.Points.IsDefaultOrEmpty)
            return;
        var brush = new ImmutableSolidColorBrush(a.Color);
        var stroke = StrokeOf(a, unit);
        switch (a.Kind)
        {
            case AnnotationKind.Line when a.Points.Length >= 2:
                context.DrawLine(new Pen(brush, stroke, lineCap: PenLineCap.Round), a.Points[0], a.Points[1]);
                break;

            case AnnotationKind.Arrow when a.Points.Length >= 2:
                DrawArrow(context, a.Points[0], a.Points[1], brush, stroke, unit);
                break;

            case AnnotationKind.Rectangle when a.Points.Length >= 2:
                var rectangle = a.Corners;
                if (rectangle.Width >= 0.5 || rectangle.Height >= 0.5)
                    context.DrawRectangle(a.Filled ? FillOf(a.Color) : null, new Pen(brush, stroke, lineJoin: PenLineJoin.Miter), rectangle);
                break;

            case AnnotationKind.Ellipse when a.Points.Length >= 2:
                var oval = a.Corners;
                if (oval.Width >= 0.5 || oval.Height >= 0.5)
                    context.DrawEllipse(a.Filled ? FillOf(a.Color) : null, new Pen(brush, stroke), oval);
                break;

            case AnnotationKind.Pen:
                if (a.Points.Length == 1)
                    context.DrawEllipse(brush, null, a.Points[0], stroke / 2, stroke / 2);
                else
                    context.DrawGeometry(null, new Pen(brush, stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Smooth(a.Points));
                break;

            case AnnotationKind.Text when a.Text.Length > 0:
                context.DrawText(Format(a.Text, a.FontSize * unit, family, brush), a.Points[0]);
                break;

            case AnnotationKind.Callout:
                DrawCallout(context, a, number, unit, family, brush);
                break;
        }
    }

    private static IBrush FillOf(Color color) => new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(255 * FillAlpha), color.R, color.G, color.B));

    private static void DrawArrow(DrawingContext context, Point from, Point to, IBrush brush, double stroke, double unit)
    {
        if (ArrowHead(from, to, stroke, unit) is not var (shaftEnd, left, right))
        {
            context.DrawEllipse(brush, null, from, stroke / 2, stroke / 2);
            return;
        }
        context.DrawLine(new Pen(brush, stroke, lineCap: PenLineCap.Round), from, shaftEnd);
        var head = new StreamGeometry();
        using (var geometry = head.Open())
        {
            geometry.BeginFigure(to, true);
            geometry.LineTo(left);
            geometry.LineTo(right);
            geometry.EndFigure(true);
        }
        // The thin outline rounds the head's corners a little.
        context.DrawGeometry(brush, new Pen(brush, stroke * 0.3, lineJoin: PenLineJoin.Round), head);
    }

    /// <summary>Where the shaft stops and the two back corners of the triangular head are; null when the arrow is too short to have one.</summary>
    internal static (Point ShaftEnd, Point Left, Point Right)? ArrowHead(Point from, Point to, double stroke, double unit)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return null;
        (dx, dy) = (dx / length, dy / length);
        var headLength = Math.Min(length, Math.Max(stroke * 5, 10 * unit));
        var half = headLength * 0.5;
        var back = new Point(to.X - dx * headLength, to.Y - dy * headLength);
        var shaftEnd = new Point(to.X - dx * headLength * 0.7, to.Y - dy * headLength * 0.7);
        return (shaftEnd, new Point(back.X - dy * half, back.Y + dx * half), new Point(back.X + dy * half, back.Y - dx * half));
    }

    private static void DrawCallout(DrawingContext context, Annotation a, int number, double unit, FontFamily family, IBrush brush)
    {
        var radius = RadiusOf(a, unit);
        var centre = a.Points[0];
        var ink = new ImmutableSolidColorBrush(OnColor(a.Color));
        var ring = Math.Max(1.5, radius * 0.1);
        // The ring is stroked on the disc's edge: shrink the disc so the whole badge measures exactly RadiusOf.
        context.DrawEllipse(brush, new Pen(ink, ring), centre, radius - ring / 2, radius - ring / 2);
        var label = number.ToString(CultureInfo.InvariantCulture);
        var size = radius * (label.Length <= 1 ? 1.25 : label.Length == 2 ? 1.05 : 0.8);
        var text = Format(label, size, family, ink);
        // Digits stand on the baseline: centre their height (about 0.72 em) on the disc, not the line box.
        context.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Baseline + 0.36 * size));
    }

    /// <summary>The stroke as a curve through the middle of each pair of points: smooth even when the pointer was quick.</summary>
    private static StreamGeometry Smooth(ImmutableArray<Point> points)
    {
        var path = new StreamGeometry();
        using var geometry = path.Open();
        geometry.BeginFigure(points[0], false);
        if (points.Length == 2)
        {
            geometry.LineTo(points[1]);
        }
        else
        {
            for (var i = 1; i < points.Length - 1; i++)
                geometry.QuadraticBezierTo(points[i], new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2));
            geometry.LineTo(points[^1]);
        }
        geometry.EndFigure(false);
        return path;
    }

    private static FormattedText Format(string text, double size, FontFamily family, IBrush brush)
    {
        size = Math.Max(1, size);
        var key = (text, size, family.ToString());
        if (!Texts.TryGetValue(key, out var formatted))
        {
            if (Texts.Count > 64)
                Texts.Clear();
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyle.Normal, FontWeight.Bold), size, brush);
            Texts[key] = formatted;
        }
        formatted.SetForegroundBrush(brush);
        return formatted;
    }

    /// <summary>The room a text mark takes, in image pixels.</summary>
    public static Size MeasureText(Annotation a, double unit, FontFamily family)
    {
        var formatted = Format(a.Text.Length == 0 ? " " : a.Text, a.FontSize * unit, family, Brushes.Black);
        return new Size(formatted.Width, formatted.Height);
    }

    /// <summary>The box that holds the whole mark, outline included.</summary>
    public static Rect BoundsOf(Annotation a, double unit, FontFamily family)
    {
        if (a.Points.IsDefaultOrEmpty)
            return default;
        var stroke = StrokeOf(a, unit);
        switch (a.Kind)
        {
            case AnnotationKind.Text:
                return new Rect(a.Points[0], MeasureText(a, unit, family));
            case AnnotationKind.Callout:
                var radius = RadiusOf(a, unit);
                return new Rect(a.Points[0].X - radius, a.Points[0].Y - radius, 2 * radius, 2 * radius);
            case AnnotationKind.Rectangle or AnnotationKind.Ellipse when a.Points.Length >= 2:
                return a.Corners.Inflate(stroke / 2);
            default:
                var (left, top, right, bottom) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                void Include(Point p) => (left, top, right, bottom) = (Math.Min(left, p.X), Math.Min(top, p.Y), Math.Max(right, p.X), Math.Max(bottom, p.Y));
                foreach (var point in a.Points)
                    Include(point);
                if (a.Kind == AnnotationKind.Arrow && a.Points.Length >= 2 && ArrowHead(a.Points[0], a.Points[1], stroke, unit) is var (_, headLeft, headRight))
                {
                    Include(headLeft);
                    Include(headRight);
                }
                return new Rect(left, top, right - left, bottom - top).Inflate(stroke / 2);
        }
    }

    /// <summary>True when <paramref name="p"/> is on the mark or within <paramref name="slop"/> image pixels of it.</summary>
    public static bool HitTest(Annotation a, Point p, double slop, double unit, FontFamily family)
    {
        if (a.Points.IsDefaultOrEmpty)
            return false;
        var reach = slop + StrokeOf(a, unit) / 2;
        switch (a.Kind)
        {
            case AnnotationKind.Line when a.Points.Length >= 2:
                return DistanceToSegment(p, a.Points[0], a.Points[1]) <= reach;

            case AnnotationKind.Arrow when a.Points.Length >= 2:
                if (DistanceToSegment(p, a.Points[0], a.Points[1]) <= reach)
                    return true;
                return ArrowHead(a.Points[0], a.Points[1], StrokeOf(a, unit), unit) is var (_, left, right) && InTriangle(p, a.Points[1], left, right, slop);

            case AnnotationKind.Rectangle when a.Points.Length >= 2:
                var box = a.Corners;
                if (a.Filled && box.Inflate(slop).Contains(p))
                    return true;
                var inside = Math.Min(Math.Min(p.X - box.Left, box.Right - p.X), Math.Min(p.Y - box.Top, box.Bottom - p.Y));
                var outsideX = Math.Max(Math.Max(box.Left - p.X, p.X - box.Right), 0);
                var outsideY = Math.Max(Math.Max(box.Top - p.Y, p.Y - box.Bottom), 0);
                var distance = inside >= 0 ? inside : Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
                return distance <= reach;

            case AnnotationKind.Ellipse when a.Points.Length >= 2:
                var oval = a.Corners;
                var (rx, ry) = (Math.Max(oval.Width / 2, 0.5), Math.Max(oval.Height / 2, 0.5));
                var (x, y) = (p.X - oval.Center.X, p.Y - oval.Center.Y);
                var q = Math.Sqrt(x * x / (rx * rx) + y * y / (ry * ry));
                if (a.Filled && q <= 1 + slop / Math.Min(rx, ry))
                    return true;
                // Distance to the outline, to first order: |q - 1| over the gradient of q.
                var gradient = q < 1e-9 ? 1 / Math.Min(rx, ry) : Math.Sqrt(x * x / Math.Pow(rx, 4) + y * y / Math.Pow(ry, 4)) / q;
                return Math.Abs(q - 1) / gradient <= reach;

            case AnnotationKind.Pen:
                if (a.Points.Length == 1)
                    return Distance(p, a.Points[0]) <= reach;
                for (var i = 1; i < a.Points.Length; i++)
                    if (DistanceToSegment(p, a.Points[i - 1], a.Points[i]) <= reach)
                        return true;
                return false;

            case AnnotationKind.Text:
                return BoundsOf(a, unit, family).Inflate(slop).Contains(p);

            case AnnotationKind.Callout:
                return Distance(p, a.Points[0]) <= RadiusOf(a, unit) + slop;

            default:
                return false;
        }
    }

    public static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
    }

    private static bool InTriangle(Point p, Point a, Point b, Point c, double slop)
    {
        static double Side(Point p, Point a, Point b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        var (d1, d2, d3) = (Side(p, a, b), Side(p, b, c), Side(p, c, a));
        if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0)))
            return true;
        return Math.Min(DistanceToSegment(p, a, b), Math.Min(DistanceToSegment(p, b, c), DistanceToSegment(p, c, a))) <= slop;
    }
}

public sealed class AnnotationCanvas : Control
{
    private const double HandleSize = 10;
    private const double HandleGrab = 9;      // view pixels from a handle's centre that still grab it
    private const double HitSlop = 6;         // view pixels from a mark that still select it
    private const double MinDrag = 4;         // a drag shorter than this (view pixels) is a click and draws nothing
    private const double PenStep = 1.5;       // view pixels between the points of a freehand stroke
    private const double EditorPadX = 4;
    private const double EditorPadY = 2;
    private const double EditorBorder = 1;

    private static readonly ImmutableSolidColorBrush Orange = new(Color.FromRgb(0xFF, 0x52, 0x00));
    private static readonly IPen ChromePen = new ImmutablePen(Orange, 1.25, new ImmutableDashStyle([4, 3], 0));
    private static readonly IPen ChromeHalo = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 3.5);
    private static readonly IPen HandlePen = new ImmutablePen(Orange, 1.5);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor BeamCursor = new(StandardCursorType.Ibeam);

    public static readonly StyledProperty<Bitmap?> PhotoProperty =
        AvaloniaProperty.Register<AnnotationCanvas, Bitmap?>(nameof(Photo));

    /// <summary>The photo's size in its own pixels; <see cref="Photo"/> may be a smaller copy of it.</summary>
    public static readonly StyledProperty<Size> ImageSizeProperty =
        AvaloniaProperty.Register<AnnotationCanvas, Size>(nameof(ImageSize));

    public static readonly StyledProperty<AnnotationDocument?> DocumentProperty =
        AvaloniaProperty.Register<AnnotationCanvas, AnnotationDocument?>(nameof(Document));

    public static readonly StyledProperty<AnnotationTool> ToolProperty =
        AvaloniaProperty.Register<AnnotationCanvas, AnnotationTool>(nameof(Tool), AnnotationTool.Arrow);

    public static readonly StyledProperty<int> SelectedIdProperty =
        AvaloniaProperty.Register<AnnotationCanvas, int>(nameof(SelectedId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>The style new marks get.</summary>
    public static readonly StyledProperty<Color> PenColorProperty =
        AvaloniaProperty.Register<AnnotationCanvas, Color>(nameof(PenColor), AnnotationPalette.Red);

    public static readonly StyledProperty<double> PenThicknessProperty =
        AvaloniaProperty.Register<AnnotationCanvas, double>(nameof(PenThickness), Annotation.DefaultThickness);

    public static readonly StyledProperty<bool> FillShapesProperty =
        AvaloniaProperty.Register<AnnotationCanvas, bool>(nameof(FillShapes));

    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<AnnotationCanvas, double>(nameof(TextSize), Annotation.DefaultFontSize);

    /// <summary>True while a non-empty text is being typed: it is about to become a mark, so Apply may already run.</summary>
    public static readonly StyledProperty<bool> HasPendingTextProperty =
        AvaloniaProperty.Register<AnnotationCanvas, bool>(nameof(HasPendingText), defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    private enum Drag { None, Create, Move, Resize }

    /// <summary>Where the photo sits in the control: <c>view = Origin + image * Scale</c>.</summary>
    private readonly record struct ViewMap(double Scale, Point Origin)
    {
        public Point ToView(Point image) => new(Origin.X + image.X * Scale, Origin.Y + image.Y * Scale);

        public Point ToImage(Point view) => new((view.X - Origin.X) / Scale, (view.Y - Origin.Y) / Scale);
    }

    private readonly TextBox _editor;
    private Bitmap? _scaled;             // the photo resampled to its on-screen size (see PhotoToDraw)
    private Bitmap? _scaledFrom;
    private Drag _drag;
    private Annotation? _working;        // the mark being drawn or dragged (not yet in the document)
    private Annotation? _original;       // the mark as it was when a move or resize began
    private int _handle;
    private Point _grab;                 // image point under the pointer when a move began
    private Point _pressed;              // view point where the gesture began
    private Point _lastView;
    private bool _moved;
    private bool _editorOpen;
    private int _editingId;              // the text mark being edited (0: a new one)
    private Point _editAt;               // image point of the text's top-left corner
    private Color _editColor;
    private double _editSize;

    static AnnotationCanvas()
    {
        AffectsRender<AnnotationCanvas>(PhotoProperty, ImageSizeProperty, DocumentProperty, ToolProperty, SelectedIdProperty);
        FocusableProperty.OverrideDefaultValue<AnnotationCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<AnnotationCanvas>(true);
    }

    public AnnotationCanvas()
    {
        var paper = new ImmutableSolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
        _editor = new TextBox
        {
            IsVisible = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 90,
            MinHeight = 0,
            Padding = new Thickness(EditorPadX, EditorPadY),
            BorderThickness = new Thickness(EditorBorder),
            BorderBrush = Orange,
            Background = paper,
            FontWeight = FontWeight.Bold,
            CornerRadius = new CornerRadius(4),
        };
        // Fluent restyles the TextBox on hover and focus from these keys: keep it one calm paper card.
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            _editor.Resources[key] = paper;
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _editor.Resources[key] = Orange;
        _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _editor.LostFocus += (_, _) => CommitEditor();
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                ShowPending();
        };
        FocusAdorner = null;   // no focus rectangle round the whole photo
        Cursor = CrossCursor;  // the default tool draws
        // The text box is this control's only child (Panel would seal Render, which draws the photo).
        LogicalChildren.Add(_editor);
        VisualChildren.Add(_editor);
    }

    public Bitmap? Photo
    {
        get => GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    public Size ImageSize
    {
        get => GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    public AnnotationDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public AnnotationTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>The selected mark (0: none). Two-way, so the style card can show and change it.</summary>
    public int SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public Color PenColor
    {
        get => GetValue(PenColorProperty);
        set => SetValue(PenColorProperty, value);
    }

    public double PenThickness
    {
        get => GetValue(PenThicknessProperty);
        set => SetValue(PenThicknessProperty, value);
    }

    public bool FillShapes
    {
        get => GetValue(FillShapesProperty);
        set => SetValue(FillShapesProperty, value);
    }

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    public bool HasPendingText
    {
        get => GetValue(HasPendingTextProperty);
        private set => SetValue(HasPendingTextProperty, value);
    }

    /// <summary>The empty text box's hint.</summary>
    public string? TextPlaceholder
    {
        get => _editor.PlaceholderText;
        set => _editor.PlaceholderText = value;
    }

    /// <summary>Room kept round the photo for handles that sit on its edge. 0 when rendering the result.</summary>
    internal double Inset { get; set; } = 12;

    /// <summary>Fill the control with the photo (rendering the result) instead of fitting it with margins.</summary>
    internal bool Cover { get; set; }

    /// <summary>False when rendering the result: no selection handles.</summary>
    internal bool ShowSelection { get; set; } = true;

    internal bool IsEditing => _editorOpen;

    internal bool IsGesturing => _drag != Drag.None;

    internal string EditorText
    {
        get => _editor.Text ?? "";
        set => _editor.Text = value;
    }

    private Size Extent =>
        ImageSize is { Width: > 0, Height: > 0 } size ? size
        : Photo is { } photo ? new Size(photo.PixelSize.Width, photo.PixelSize.Height)
        : default;

    private FontFamily Family => AppFont(this);

    /// <summary>The app's UI font, so marks and the text box look like the rest of the window (CJK fallbacks included).</summary>
    internal static FontFamily AppFont(Control? context)
    {
        object? found = null;
        if (context is not null && context.TryFindResource("AppFontFamily", context.ActualThemeVariant, out found) && found is FontFamily local)
            return local;
        if (Application.Current is { } app && app.TryGetResource("AppFontFamily", app.ActualThemeVariant, out found) && found is FontFamily global)
            return global;
        return FontFamily.Default;
    }

    // ───────── Where the photo is ─────────

    private ViewMap Map()
    {
        var image = Extent;
        var area = new Rect(Bounds.Size).Deflate(Inset);
        if (image.Width <= 0 || image.Height <= 0 || area.Width <= 0 || area.Height <= 0)
            return new ViewMap(1, default);
        if (Cover)
            return new ViewMap(Math.Max(area.Width / image.Width, area.Height / image.Height), area.TopLeft);
        var scale = Math.Min(area.Width / image.Width, area.Height / image.Height);
        // On whole device pixels, so the photo is blitted without being resampled again.
        var pixel = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new ViewMap(scale, new Point(
            Math.Round((area.X + (area.Width - image.Width * scale) / 2) * pixel) / pixel,
            Math.Round((area.Y + (area.Height - image.Height * scale) / 2) * pixel) / pixel));
    }

    /// <summary>A point of the photo (its pixels) in this control's coordinates.</summary>
    internal Point ToView(Point image) => Map().ToView(image);

    /// <summary>A point of this control as a point of the photo (its pixels).</summary>
    internal Point ToImage(Point view) => Map().ToImage(view);

    private Point Clamp(Point image)
    {
        var extent = Extent;
        return new Point(Math.Clamp(image.X, 0, extent.Width), Math.Clamp(image.Y, 0, extent.Height));
    }

    // ───────── Layout (only the text box is a child) ─────────

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_editorOpen)
        {
            var at = ToView(_editAt);
            _editor.Measure(new Size(Math.Max(120, availableSize.Width - at.X + EditorPadX), availableSize.Height));
        }
        // The canvas fills whatever slot it is given; asked with no limit it wants only a small area.
        return new Size(double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editorOpen)
        {
            // The text starts where the mark's text will: the box sits out by its border and padding.
            var at = ToView(_editAt);
            var (x, y) = (at.X - EditorBorder - EditorPadX, at.Y - EditorBorder - EditorPadY);
            var desired = _editor.DesiredSize;
            _editor.Arrange(new Rect(x, y, Math.Min(desired.Width, Math.Max(40, finalSize.Width - x)), desired.Height));
        }
        return finalSize;
    }

    // ───────── Drawing ─────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Transparent but drawn: it makes the whole control, not only the photo, take the pointer.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var image = Extent;
        if (image.Width <= 0 || image.Height <= 0)
            return;
        var map = Map();
        var unit = AnnotationPainter.UnitOf(image);
        var family = Family;
        var picture = new Rect(0, 0, image.Width, image.Height);
        var transform = Matrix.CreateScale(map.Scale, map.Scale) * Matrix.CreateTranslation(map.Origin.X, map.Origin.Y);
        using (context.PushTransform(transform))
        using (context.PushClip(picture))
        {
            if (Photo is { } photo)
            {
                var (shown, exact) = PhotoToDraw(photo, image, map);
                // Pixel for pixel when it already is the size it is drawn at (the saved picture, or the copy below);
                // otherwise smooth.
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = exact ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
                    context.DrawImage(shown, new Rect(shown.Size), picture);
            }
            DrawMarks(context, unit, family);
        }

        if (ShowSelection && Tool == AnnotationTool.Select && SelectedId != 0 && Effective(SelectedId) is { } selected)
            DrawChrome(context, selected, map, unit, family);
    }

    /// <summary>
    /// The bitmap to blit and whether it is already the size it is drawn at. A photo shown smaller than its preview is
    /// resampled once to its on-screen size and that copy is blitted every frame: dragging a mark then redraws a
    /// picture-sized blit, not a high-quality resample of it, which matters when the machine renders in software.
    /// </summary>
    private (Bitmap Bitmap, bool Exact) PhotoToDraw(Bitmap photo, Size image, ViewMap map)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var wanted = new PixelSize(Math.Max(1, (int)Math.Round(image.Width * map.Scale * scaling)), Math.Max(1, (int)Math.Round(image.Height * map.Scale * scaling)));
        if (wanted == photo.PixelSize)
            return (photo, true);
        if (wanted.Width >= photo.PixelSize.Width || wanted.Height >= photo.PixelSize.Height)
            return (photo, false);   // enlarged: the renderer interpolates
        if (_scaled is null || !ReferenceEquals(_scaledFrom, photo) || _scaled.PixelSize != wanted)
        {
            _scaled?.Dispose();
            _scaled = photo.CreateScaledBitmap(wanted, BitmapInterpolationMode.HighQuality);
            _scaledFrom = photo;
        }
        return (_scaled, true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scaled?.Dispose();
        _scaled = null;
        _scaledFrom = null;
    }

    private void DrawMarks(DrawingContext context, double unit, FontFamily family)
    {
        if (Document is not { } document)
            return;
        var number = 0;
        foreach (var item in document.Items)
        {
            if (item.Kind == AnnotationKind.Callout)
                number++;
            if (item.Id == _editingId)
                continue;
            var shown = _working is { } edited && edited.Id == item.Id ? edited : item;
            AnnotationPainter.Draw(context, shown, number, unit, family);
        }
        if (_working is { Id: 0 } fresh)
            AnnotationPainter.Draw(context, fresh, document.CalloutCount + 1, unit, family);
    }

    /// <summary>The selection's dashed box and handles: drawn in view pixels so they stay the same size however small the photo is shown.</summary>
    private static void DrawChrome(DrawingContext context, Annotation a, ViewMap map, double unit, FontFamily family)
    {
        var bounds = AnnotationPainter.BoundsOf(a, unit, family);
        if (a.Kind is not (AnnotationKind.Arrow or AnnotationKind.Line))
        {
            var (topLeft, bottomRight) = (map.ToView(bounds.TopLeft), map.ToView(bounds.BottomRight));
            var box = new Rect(topLeft, bottomRight).Inflate(3);
            context.DrawRectangle(null, ChromeHalo, box);   // light under the dashes so they read on any photo
            context.DrawRectangle(null, ChromePen, box);
        }
        foreach (var handle in a.HandlePoints())
        {
            var centre = map.ToView(handle);
            context.DrawRectangle(Brushes.White, HandlePen, new Rect(centre.X - HandleSize / 2, centre.Y - HandleSize / 2, HandleSize, HandleSize), 2, 2);
        }
    }

    private Annotation? Effective(int id) => _working is { } working && working.Id == id ? working : Document?.Find(id);

    // ───────── Changes from outside ─────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            if (change.OldValue is AnnotationDocument old)
                old.Changed -= OnDocumentChanged;
            if (change.NewValue is AnnotationDocument current)
                current.Changed += OnDocumentChanged;
            CancelGesture();
            CancelEditor();
        }
        else if (change.Property == ToolProperty)
        {
            CommitEditor();
            CancelGesture();
            if (Tool != AnnotationTool.Select && SelectedId != 0)
                SelectedId = 0;
            UpdateCursor(_lastView);
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (SelectedId != 0 && Document?.Find(SelectedId) is null)
            SelectedId = 0;
        InvalidateVisual();
    }

    // ───────── Pointer ─────────

    /// <summary>True when <paramref name="source"/> is the text box or part of it.</summary>
    internal bool IsFromEditor(object? source) =>
        source is Visual visual && (ReferenceEquals(visual, _editor) || _editor.IsVisualAncestorOf(visual));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsFromEditor(e.Source) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        if (BeginGesture(e.GetPosition(this), e.KeyModifiers, e.ClickCount))
            e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsFromEditor(e.Source))
            return;
        var view = e.GetPosition(this);
        if (_drag != Drag.None)
            UpdateGesture(view, e.KeyModifiers);
        else
            UpdateCursor(view);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag != Drag.None)
        {
            EndGesture(e.GetPosition(this), e.KeyModifiers);
            e.Handled = true;
        }
        // Also after an Escape that cancelled the drag: the pointer must not stay captured.
        if (ReferenceEquals(e.Pointer.Captured, this))
            e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // The window lost the pointer in the middle of a drag: keep what was drawn so far.
        if (_drag != Drag.None)
            EndGesture(_lastView);
    }

    /// <summary>A cross to draw with, an I-beam for text, and with Select: a cross on a handle, arrows on a mark, else the plain arrow.</summary>
    private void UpdateCursor(Point view)
    {
        if (Tool != AnnotationTool.Select)
        {
            Cursor = Tool == AnnotationTool.Text ? BeamCursor : CrossCursor;
            return;
        }
        var onHandle = SelectedId != 0 && Document?.Find(SelectedId) is { } selected && HandleAt(selected, view) >= 0;
        Cursor = onHandle ? CrossCursor : HitTest(view) is not null ? MoveCursor : ArrowCursor;
    }

    // ───────── Gestures ─────────

    /// <summary>
    /// A press at <paramref name="view"/> (control coordinates). Returns true when a drag has begun, so the caller
    /// captures the pointer; Select and Text may act on the press alone.
    /// </summary>
    internal bool BeginGesture(Point view, KeyModifiers modifiers = KeyModifiers.None, int clickCount = 1)
    {
        CommitEditor();   // clicking away confirms the text being typed
        CancelGesture();
        if (Document is null || Extent.Width <= 0)
            return false;
        var map = Map();
        var at = map.ToImage(view);
        var point = Clamp(at);
        _pressed = _lastView = view;
        _moved = false;

        if (Tool == AnnotationTool.Select)
            return BeginSelect(view, at, clickCount);

        if (Tool == AnnotationTool.Text)
        {
            if (HitTest(view) is { Kind: AnnotationKind.Text } existing)
                OpenEditor(existing, existing.Points[0]);
            else
                OpenEditor(null, point);
            return false;
        }

        var kind = Annotation.KindOf(Tool)!.Value;
        SelectedId = 0;
        _working = new Annotation
        {
            Kind = kind,
            Points = kind is AnnotationKind.Pen or AnnotationKind.Callout ? [point] : [point, point],
            Color = PenColor,
            Thickness = PenThickness,
            Filled = FillShapes && (kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse),
            FontSize = TextSize,
        };
        _drag = Drag.Create;
        InvalidateVisual();
        return true;
    }

    private bool BeginSelect(Point view, Point image, int clickCount)
    {
        if (SelectedId != 0 && Document!.Find(SelectedId) is { } selected && HandleAt(selected, view) is var handle and >= 0)
        {
            _original = _working = selected;
            _handle = handle;
            _drag = Drag.Resize;
            return true;
        }
        if (HitTest(view) is not { } hit)
        {
            SelectedId = 0;
            return false;
        }
        SelectedId = hit.Id;
        if (clickCount >= 2 && hit.Kind == AnnotationKind.Text)
        {
            OpenEditor(hit, hit.Points[0]);
            return false;
        }
        _original = _working = hit;
        _grab = image;
        _drag = Drag.Move;
        return true;
    }

    /// <summary>The pointer moved to <paramref name="view"/> during a gesture.</summary>
    internal void UpdateGesture(Point view, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (_drag == Drag.None || _working is null)
            return;
        _lastView = view;
        var map = Map();
        var extent = Extent;
        switch (_drag)
        {
            case Drag.Create:
                var to = map.ToImage(view);
                switch (_working.Kind)
                {
                    case AnnotationKind.Pen:
                        var last = map.ToView(_working.Points[^1]);
                        if (AnnotationPainter.Distance(last, view) >= PenStep)
                            _working = _working with { Points = _working.Points.Add(Clamp(to)) };
                        break;
                    case AnnotationKind.Callout:
                        _working = _working with { Points = [Clamp(to)] };
                        break;
                    default:
                        _working = _working with { Points = [_working.Points[0], Clamp(Constrain(_working.Kind, _working.Points[0], to, modifiers))] };
                        break;
                }
                break;

            case Drag.Move:
                if (!_moved && AnnotationPainter.Distance(view, _pressed) < 3)
                    return;   // a click that wobbled is still a click
                _moved = true;
                _working = Moved(_original!, new Vector(map.ToImage(view).X - _grab.X, map.ToImage(view).Y - _grab.Y), extent);
                break;

            case Drag.Resize:
                _moved = true;
                _working = _original!.WithHandle(_handle, Clamp(map.ToImage(view)));
                break;
        }
        InvalidateVisual();
    }

    /// <summary>The pointer was released at <paramref name="view"/>: the mark joins the document (one history step).</summary>
    internal void EndGesture(Point view, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (_drag == Drag.None)
            return;
        UpdateGesture(view, modifiers);
        var (drag, working, moved) = (_drag, _working, _moved);
        _drag = Drag.None;
        _working = _original = null;
        if (Document is { } document && working is not null)
        {
            if (drag == Drag.Create && IsMeaningful(working))
                document.Add(working);
            else if (drag is Drag.Move or Drag.Resize && moved)
                document.Replace(working);
        }
        InvalidateVisual();
    }

    /// <summary>Drops the mark being drawn or dragged.</summary>
    internal void CancelGesture()
    {
        if (_drag == Drag.None)
            return;
        _drag = Drag.None;
        _working = _original = null;
        InvalidateVisual();
    }

    private bool IsMeaningful(Annotation a)
    {
        if (a.Kind is AnnotationKind.Pen or AnnotationKind.Callout)
            return true;
        var map = Map();
        return AnnotationPainter.Distance(map.ToView(a.Points[0]), map.ToView(a.Points[1])) >= MinDrag;
    }

    /// <summary>Shift: lines snap to multiples of 45 degrees, rectangles and ellipses to squares and circles.</summary>
    private static Point Constrain(AnnotationKind kind, Point start, Point end, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Shift) == 0)
            return end;
        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        if (kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(start.X + (dx < 0 ? -side : side), start.Y + (dy < 0 ? -side : side));
        }
        if (kind is AnnotationKind.Arrow or AnnotationKind.Line)
        {
            var length = Math.Sqrt(dx * dx + dy * dy);
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
        }
        return end;
    }

    /// <summary>The mark shifted by <paramref name="delta"/>, kept so its middle stays on the photo.</summary>
    private Annotation Moved(Annotation original, Vector delta, Size extent)
    {
        var centre = AnnotationPainter.BoundsOf(original, AnnotationPainter.UnitOf(extent), Family).Center;
        var moved = new Point(centre.X + delta.X, centre.Y + delta.Y);
        return original.Translated(new Vector(
            delta.X + Math.Clamp(moved.X, 0, extent.Width) - moved.X,
            delta.Y + Math.Clamp(moved.Y, 0, extent.Height) - moved.Y));
    }

    // ───────── Hit testing ─────────

    private Annotation? HitTest(Point view)
    {
        if (Document is not { } document)
            return null;
        var map = Map();
        var at = map.ToImage(view);
        var unit = AnnotationPainter.UnitOf(Extent);
        var family = Family;
        var items = document.Items;
        for (var i = items.Length - 1; i >= 0; i--)
            if (items[i].Id != _editingId && AnnotationPainter.HitTest(items[i], at, HitSlop / map.Scale, unit, family))
                return items[i];
        return null;
    }

    /// <summary>The handle of <paramref name="a"/> under <paramref name="view"/>, or -1.</summary>
    private int HandleAt(Annotation a, Point view)
    {
        var map = Map();
        var handles = a.HandlePoints();
        var (best, nearest) = (-1, HandleGrab);
        for (var i = 0; i < handles.Length; i++)
        {
            var distance = AnnotationPainter.Distance(map.ToView(handles[i]), view);
            if (distance <= nearest)
                (best, nearest) = (i, distance);
        }
        return best;
    }

    /// <summary>The topmost mark under a point of the photo (its pixels), or null.</summary>
    internal Annotation? MarkAt(Point image) => HitTest(ToView(image));

    // ───────── Text ─────────

    /// <summary>Opens the text box for a new text at <paramref name="image"/> (a point of the photo).</summary>
    internal void BeginText(Point image) => OpenEditor(null, Clamp(image));

    private void OpenEditor(Annotation? existing, Point at)
    {
        CommitEditor();
        _editingId = existing?.Id ?? 0;
        _editAt = at;
        (_editColor, _editSize) = existing is null ? (PenColor, TextSize) : (existing.Color, existing.FontSize);
        var scale = Map().Scale * AnnotationPainter.UnitOf(Extent);
        _editor.FontFamily = Family;
        _editor.FontSize = Math.Max(8, _editSize * scale);
        _editor.Foreground = new ImmutableSolidColorBrush(_editColor);
        _editor.CaretBrush = _editor.Foreground;
        _editor.Text = existing?.Text ?? "";
        _editorOpen = true;
        _editor.IsVisible = true;
        ShowPending();
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        // After the press that opened it is over, or the canvas would take the focus back.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editorOpen)
                return;
            _editor.Focus();
            _editor.CaretIndex = _editor.Text?.Length ?? 0;
            if (existing is not null)
                _editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Confirms the text being typed: a new text joins the document, an edited one is updated, and an empty one is
    /// dropped (an existing text emptied out is deleted). False when no text was being edited.
    /// </summary>
    public bool CommitEditor()
    {
        if (!_editorOpen)
            return false;
        _editorOpen = false;
        var text = (_editor.Text ?? "").TrimEnd();
        var id = _editingId;
        _editingId = 0;
        var hadFocus = _editor.IsFocused || _editor.IsKeyboardFocusWithin;
        _editor.IsVisible = false;
        if (Document is { } document)
        {
            if (id != 0)
            {
                if (document.Find(id) is { } current)
                {
                    if (text.Length == 0)
                        document.Remove(id);
                    else if (text != current.Text)
                        document.Replace(current with { Text = text });
                }
            }
            else if (text.Length > 0)
            {
                document.Add(new Annotation
                {
                    Kind = AnnotationKind.Text,
                    Points = [_editAt],
                    Color = _editColor,
                    Text = text,
                    FontSize = _editSize,
                });
            }
        }
        ShowPending();
        InvalidateVisual();
        if (hadFocus)
            Focus();
        return true;
    }

    /// <summary>Tells the panel whether a non-empty text is waiting to be confirmed.</summary>
    private void ShowPending() => HasPendingText = _editorOpen && !string.IsNullOrWhiteSpace(_editor.Text);

    /// <summary>Closes the text box without keeping its text.</summary>
    internal void CancelEditor()
    {
        if (!_editorOpen)
            return;
        _editorOpen = false;
        _editingId = 0;
        _editor.IsVisible = false;
        ShowPending();
        InvalidateVisual();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;   // Shift+Enter makes a second line
            CommitEditor();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEditor();
            Focus();
        }
    }

    // ───────── Keys ─────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && !IsFromEditor(e.Source) && HandleKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    /// <summary>Delete (or Backspace), Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z and Escape. True when the key was used.</summary>
    internal bool HandleKey(Key key, KeyModifiers modifiers)
    {
        var control = (modifiers & KeyModifiers.Control) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;
        switch (key)
        {
            case Key.Delete or Key.Back when !control:
                if (SelectedId == 0 || Document is null)
                    return false;
                var id = SelectedId;
                SelectedId = 0;
                Document.Remove(id);
                return true;
            case Key.Z when control && !shift:
                Document?.Undo();
                return true;
            case Key.Y when control:
            case Key.Z when control && shift:
                Document?.Redo();
                return true;
            case Key.Escape:
                if (_drag != Drag.None)
                {
                    CancelGesture();
                    return true;
                }
                if (SelectedId != 0)
                {
                    SelectedId = 0;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    // ───────── The saved picture ─────────

    /// <summary>
    /// The photo with <paramref name="items"/> drawn on it by the same code the canvas uses on screen, as a bitmap of
    /// <paramref name="photo"/>'s own pixels. <paramref name="original"/> is the size the marks were placed against
    /// (<paramref name="photo"/> is a scaled-down copy of it when the original is too large to save in full).
    /// </summary>
    internal static RenderTargetBitmap RenderResult(Bitmap photo, Size original, ImmutableArray<Annotation> items)
    {
        var pixels = photo.PixelSize;
        var canvas = new AnnotationCanvas
        {
            Width = pixels.Width,
            Height = pixels.Height,
            Inset = 0,
            Cover = true,
            ShowSelection = false,
            ImageSize = original,
            Photo = photo,
            Document = AnnotationDocument.From(items),
        };
        canvas.Measure(new Size(pixels.Width, pixels.Height));
        canvas.Arrange(new Rect(0, 0, pixels.Width, pixels.Height));
        var target = new RenderTargetBitmap(pixels, new Vector(96, 96));
        target.Render(canvas);
        return target;
    }
}
