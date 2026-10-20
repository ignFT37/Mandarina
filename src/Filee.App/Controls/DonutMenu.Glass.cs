// Mandarina: look for the live wheel (Glass = true). A frosted ring (the blurred screen behind it, see
// Mandarina/Backdrop.cs) under rounded petals filled with pale glass; the petal under the pointer turns orange with a
// white label; tool and action petals carry an icon above a small upper-case label; the hole shows a pill with the
// files' size, or the name of the petal under the pointer. The whole wheel blooms open from the centre.

using Avalonia;
using Avalonia.Media;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    private const double GlassMargin = 6;      // the frosted ring reaches this far beyond the petals
    private const double PetalGap = 4;
    private const double OuterCorner = 18;
    private const double InnerCorner = 7;

    private static readonly IBrush PetalFill = new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(204, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(148, 243, 246, 247), 0.68),
            new GradientStop(Color.FromArgb(191, 244, 246, 246), 1),
        },
    };
    private static readonly IPen PetalStroke = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0x5D, 0x66, 0x66)), 0.75);
    private static readonly IBrush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x00));
    private static readonly IPen OrangeStroke = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xC2, 0x65, 0x34)), 0.75);
    private static readonly IBrush GlassTint = new SolidColorBrush(Color.FromArgb(0x40, 0xC0, 0xC9, 0xCD));
    private static readonly IPen GlassEdge = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush PillFill = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly IPen PillStroke = new Pen(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush HoleCatch = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
    private static readonly IBrush Ink = Brushes.Black;
    private static readonly IBrush InkDisabled = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
    private static readonly IBrush CardFill = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly IPen CardStroke = new Pen(new SolidColorBrush(Color.FromRgb(0xD9, 0xDF, 0xDA)), 1);
    private static readonly IBrush CardText = new SolidColorBrush(Color.FromRgb(0x82, 0x87, 0x82));

    private void RenderGlass(DrawingContext context, Point center, double r1, double r2, Typeface bold, Typeface regular)
    {
        var n = Count;
        var opening = _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1);

        // A wheel without petals (no tools for these files): a small card with the hint instead of an empty ring.
        if (n == 0)
        {
            if (!string.IsNullOrEmpty(CenterSubtitle))
                DrawHintCard(context, center, r2, regular);
            else
                context.DrawEllipse(HoleCatch, null, center, r2, r2);
            return;
        }

        // The whole wheel blooms from 90 % with the petals (CSS: .2s cubic-bezier(.18,.82,.24,1)).
        var bloom = 0.9 + 0.1 * opening;
        // A flip (Filee's back face of the conversion wheel) squeezes the ring about the vertical axis; the hub stays.
        var flip = FlipTransform(center);
        using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(bloom, bloom) *
                                     Matrix.CreateTranslation(center.X, center.Y) * flip))
        using (context.PushOpacity(opening))
        {
            DrawSoftShadow(context, center, r2 + 2);
            var ring = CreateRing(center, Math.Max(4, r1 - GlassMargin), r2 + GlassMargin);
            using (context.PushGeometryClip(ring))
            {
                if (Backdrop is { } backdrop)
                    context.DrawImage(backdrop, new Rect(Bounds.Size));
                context.FillRectangle(GlassTint, new Rect(Bounds.Size));
            }
            context.DrawGeometry(null, GlassEdge, ring);
            // The hole stays (barely) opaque: a drop there lands on the wheel, which cancels, not on the window below.
            context.DrawEllipse(HoleCatch, null, center, r1, r1);

            for (var i = 0; i < n; i++)
            {
                var item = Items![i];
                var open = i < _open.Length ? Math.Clamp(_open[i], 0, 1) : 1;
                if (open <= 0.001)
                    continue;
                var mid = DonutGeometry.MidAngle(i, n);
                var span = DonutGeometry.Span(n);
                var petal = CreatePetal(center, r1, r2, mid, span);
                using (context.PushOpacity(open * (item.IsEnabled ? 1 : 0.55)))
                {
                    context.DrawGeometry(PetalFill, PetalStroke, petal);
                    var hover = i < _hover.Length ? _hover[i].Value : 0;
                    var emphasis = i == _dropTargetIndex ? 1.0 : Math.Clamp(hover, 0, 1);
                    if (emphasis > 0.01 && item.IsEnabled)
                    {
                        using (context.PushOpacity(emphasis))
                            context.DrawGeometry(Orange, OrangeStroke, petal);
                    }
                    var hot = (i == ActiveIndex || i == _dropTargetIndex) && item.IsEnabled;
                    DrawGlassLabel(context, item, DonutGeometry.PointAt(center, (r1 + r2) / 2, mid), span * (r1 + r2) / 2 - 8, hot, bold);
                }
            }
        }

        var reaching = _expanded >= 0 ? _expanded : _pendingGroup;
        if (reaching >= 0 && reaching < n && Items![reaching].Children is { Count: > 0 } reachable)
            DrawHitBackdrop(context, center, reaching, reachable.Count);
        if (_shownExpanded >= 0 && _shownExpanded < n && _expand.Value > 0.01)
        {
            using (context.PushTransform(flip))
                DrawSubRing(context, center, bold, regular);
        }

        DrawGlassCenter(context, center, r1, bold, regular);
    }

    private static void DrawGlassLabel(DrawingContext context, DonutItem item, Point at, double maxWidth, bool hot, Typeface bold)
    {
        var ink = hot ? Brushes.White : item.IsEnabled ? Ink : InkDisabled;
        if (item.Icon is { } icon)
        {
            // Icon above a small upper-case title, as on the tool and action wheels.
            const double size = 18;
            var scale = size / Mandarina.WheelIcons.Size;
            var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X - size / 2, at.Y - 16.5);
            using (context.PushTransform(matrix))
                context.DrawGeometry(null, new Pen(ink, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), icon);
            var room = Math.Clamp(maxWidth, 40, 90);
            var titleSize = 9.0;
            if (!item.Label.Contains(' '))
            {
                // A single word wider than its petal (nine petals on a video's wheel: "ENCUADRAR"; eleven with the transcription
                // ones: "TRANSCRIBIR"): a little smaller type, not the word broken in two.
                var natural = MakeText(item.Label, bold, titleSize, ink, 1000).Width;
                if (natural > room)
                    titleSize = Math.Max(6.2, titleSize * room / natural);
            }
            var title = MakeText(item.Label, bold, titleSize, ink, room, maxLines: 2);
            DrawCentered(context, title, at.X, at.Y + 5);
            return;
        }
        var label = MakeText(item.Label, bold, 14, ink, Math.Clamp(maxWidth, 30, 110), maxLines: 2);
        DrawCentered(context, label, at.X, at.Y - label.Height / 2);
    }

    private void DrawGlassCenter(DrawingContext context, Point center, double r1, Typeface bold, Typeface regular)
    {
        if (string.IsNullOrEmpty(CenterTitle))
            return;
        var tool = Items?.Any(i => i.Icon is not null) == true;
        var text = MakeText(CenterTitle, bold, tool ? 9 : 12, Ink, Math.Min(r1 * 1.5, 86), maxLines: 2);
        var width = Math.Max(70, Math.Min(r1 * 1.7, text.Width + 22));
        var height = Math.Max(tool ? 36 : 32, text.Height + 12);
        var pill = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
        context.DrawRectangle(PillFill, PillStroke, pill, height / 2, height / 2);
        DrawCentered(context, text, center.X, center.Y - text.Height / 2);
        // The flip hint ("Tab ↻ back") under the pill, while the other face has something for these files.
        if (!string.IsNullOrEmpty(CenterHint))
        {
            var hint = MakeText(CenterHint, regular, 10, Ink, Math.Min(r1 * 1.6, 96));
            using (context.PushOpacity(0.6))
                DrawCentered(context, hint, center.X, pill.Bottom + 3);
        }
    }

    private void DrawHintCard(DrawingContext context, Point center, double r2, Typeface regular)
    {
        var width = Math.Min(Bounds.Width - 8, r2 * 2);
        var text = MakeText(CenterSubtitle ?? "", regular, 13, CardText, width - 40, maxLines: 5);
        var height = text.Height + 40;
        var card = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
        context.DrawRectangle(CardFill, CardStroke, card, 18, 18);
        DrawCentered(context, text, center.X, center.Y - text.Height / 2);
    }

    /// <summary>
    /// A petal: the slice between <paramref name="r1"/> and <paramref name="r2"/> centred on <paramref name="mid"/>,
    /// <see cref="PetalGap"/> pixels from its neighbours, with rounded corners (larger outside).
    /// </summary>
    private static StreamGeometry CreatePetal(Point c, double r1, double r2, double mid, double span)
    {
        if (span >= 2 * Math.PI - 0.001)
            return CreateRing(c, r1, r2);

        var outerHalf = span / 2 - Math.Asin(Math.Min(0.95, PetalGap / 2 / r2));
        var innerHalf = span / 2 - Math.Asin(Math.Min(0.95, PetalGap / 2 / r1));
        var side = r2 - r1;
        var ko = Math.Min(OuterCorner, Math.Min(side * 0.35, outerHalf * r2 * 0.45));
        var ki = Math.Min(InnerCorner, Math.Min(side * 0.25, innerHalf * r1 * 0.45));

        // Corner points on the outer and inner arcs, and where the straight sides meet them.
        var outLeft = DonutGeometry.PointAt(c, r2, mid - outerHalf);
        var outRight = DonutGeometry.PointAt(c, r2, mid + outerHalf);
        var inLeft = DonutGeometry.PointAt(c, r1, mid - innerHalf);
        var inRight = DonutGeometry.PointAt(c, r1, mid + innerHalf);
        Point Toward(Point from, Point to, double distance)
        {
            var length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            var t = length < 0.001 ? 0 : distance / length;
            return new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }
        var outArcStart = DonutGeometry.PointAt(c, r2, mid - outerHalf + ko / r2);
        var outArcEnd = DonutGeometry.PointAt(c, r2, mid + outerHalf - ko / r2);
        var inArcStart = DonutGeometry.PointAt(c, r1, mid + innerHalf - ki / r1);
        var inArcEnd = DonutGeometry.PointAt(c, r1, mid - innerHalf + ki / r1);

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(Toward(inLeft, outLeft, ki), true);
        ctx.LineTo(Toward(outLeft, inLeft, ko));
        ctx.QuadraticBezierTo(outLeft, outArcStart);
        ctx.ArcTo(outArcEnd, new Size(r2, r2), 0, outerHalf * 2 - 2 * ko / r2 > Math.PI, SweepDirection.Clockwise);
        ctx.QuadraticBezierTo(outRight, Toward(outRight, inRight, ko));
        ctx.LineTo(Toward(inRight, outRight, ki));
        ctx.QuadraticBezierTo(inRight, inArcStart);
        ctx.ArcTo(inArcEnd, new Size(r1, r1), 0, innerHalf * 2 - 2 * ki / r1 > Math.PI, SweepDirection.CounterClockwise);
        ctx.QuadraticBezierTo(inLeft, Toward(inLeft, outLeft, ki));
        ctx.EndFigure(true);
        return geometry;
    }
}
