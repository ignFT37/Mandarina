// Mandarina: the geometry of the crop box, apart from any control so it can be checked with plain numbers. Every value is
// in pixels of the ORIGINAL picture. A box is always whole pixels, inside the picture and at least 1 px (dragging also
// keeps it above the minimum size). CropSurface turns pointer movement into these calls; the crop panels use them for
// the proportion pills, the exact-size boxes and the centred crop of several photos.

using Avalonia;

namespace Filee.App.Mandarina.Tools;

/// <summary>What the pointer has hold of in a <see cref="CropSurface"/>.</summary>
internal enum CropHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

internal static class CropMath
{
    /// <summary>The box the pointer leaves after moving <paramref name="dx"/>, <paramref name="dy"/> (original pixels) since it took <paramref name="handle"/>.</summary>
    /// <param name="start">The box when the pointer went down.</param>
    /// <param name="aspect">Width / height to keep while resizing; 0 for a free box.</param>
    /// <param name="minimum">The smallest side a resized box may have.</param>
    internal static Rect Drag(Rect start, CropHandle handle, double dx, double dy, double aspect, double width, double height, double minimum)
    {
        if (handle == CropHandle.None || width < 1 || height < 1)
            return start;
        var minWidth = Math.Min(minimum, width);
        var minHeight = Math.Min(minimum, height);
        var box = handle switch
        {
            CropHandle.Move => Moved(start, dx, dy, width, height),
            _ when aspect > 0 => ResizedLocked(start, handle, dx, dy, aspect, width, height, minWidth, minHeight),
            _ => ResizedFree(start, handle, dx, dy, width, height, minWidth, minHeight),
        };
        return Snap(box, width, height);
    }

    private static Rect Moved(Rect start, double dx, double dy, double width, double height)
    {
        var w = Math.Min(start.Width, width);
        var h = Math.Min(start.Height, height);
        return new Rect(Math.Clamp(start.X + dx, 0, width - w), Math.Clamp(start.Y + dy, 0, height - h), w, h);
    }

    /// <summary>Each edge the handle touches follows the pointer; the opposite ones stay where they were.</summary>
    private static Rect ResizedFree(Rect start, CropHandle handle, double dx, double dy, double width, double height, double minWidth, double minHeight)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (handle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft)
            left = Math.Max(0, Math.Min(start.Left + dx, right - minWidth));
        if (handle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight)
            right = Math.Min(width, Math.Max(start.Right + dx, left + minWidth));
        if (handle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight)
            top = Math.Max(0, Math.Min(start.Top + dy, bottom - minHeight));
        if (handle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight)
            bottom = Math.Min(height, Math.Max(start.Bottom + dy, top + minHeight));
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// A corner keeps its opposite corner still and slides along the diagonal of the proportion towards the pointer; a
    /// side keeps the opposite side still and grows the other axis around the box's middle (sliding inside the picture
    /// when it would stick out), so dragging a side never gets stuck for lack of room on one end.
    /// </summary>
    private static Rect ResizedLocked(Rect start, CropHandle handle, double dx, double dy, double aspect, double width, double height,
        double minWidth, double minHeight)
    {
        // Both sides must stay above the minimum: the width is the one number that decides the box (height = width / aspect).
        var smallest = Math.Max(minWidth, minHeight * aspect);
        switch (handle)
        {
            case CropHandle.Left or CropHandle.Right:
            {
                var right = handle == CropHandle.Right;
                var anchor = right ? start.Left : start.Right;
                var wanted = right ? start.Right + dx - anchor : anchor - (start.Left + dx);
                var (w, h) = LockedSize(wanted, smallest, right ? width - anchor : anchor, height, aspect);
                var y = Math.Clamp(start.Y + start.Height / 2 - h / 2, 0, height - h);
                return new Rect(right ? anchor : anchor - w, y, w, h);
            }
            case CropHandle.Top or CropHandle.Bottom:
            {
                var bottom = handle == CropHandle.Bottom;
                var anchor = bottom ? start.Top : start.Bottom;
                var wantedHeight = bottom ? start.Bottom + dy - anchor : anchor - (start.Top + dy);
                var (w, h) = LockedSize(wantedHeight * aspect, smallest, width, bottom ? height - anchor : anchor, aspect);
                var x = Math.Clamp(start.X + start.Width / 2 - w / 2, 0, width - w);
                return new Rect(x, bottom ? anchor : anchor - h, w, h);
            }
            default:
            {
                var sx = handle is CropHandle.TopRight or CropHandle.BottomRight ? 1 : -1;
                var sy = handle is CropHandle.BottomLeft or CropHandle.BottomRight ? 1 : -1;
                var ax = sx > 0 ? start.Left : start.Right;
                var ay = sy > 0 ? start.Top : start.Bottom;
                // The pointer's offset from the fixed corner, positive going outwards; projected on the diagonal (aspect, 1).
                var vx = ((sx > 0 ? start.Right : start.Left) + dx - ax) * sx;
                var vy = ((sy > 0 ? start.Bottom : start.Top) + dy - ay) * sy;
                var wanted = (vx * aspect + vy) * aspect / (aspect * aspect + 1);
                var (w, h) = LockedSize(wanted, smallest, sx > 0 ? width - ax : ax, sy > 0 ? height - ay : ay, aspect);
                return new Rect(sx > 0 ? ax : ax - w, sy > 0 ? ay : ay - h, w, h);
            }
        }
    }

    /// <summary>The whole-pixel width and height (height = width / aspect) nearest to <paramref name="wanted"/> that fit the room and the minimum.</summary>
    private static (double Width, double Height) LockedSize(double wanted, double smallest, double roomX, double roomY, double aspect)
    {
        var most = Math.Floor(Math.Min(roomX, roomY * aspect));
        var w = Math.Max(1, Math.Min(most, Math.Max(Math.Ceiling(smallest), Math.Round(wanted, MidpointRounding.AwayFromZero))));
        var h = Math.Min(Math.Max(1, Math.Floor(roomY)), Math.Max(1, Math.Round(w / aspect, MidpointRounding.AwayFromZero)));
        return (w, h);
    }

    /// <summary>
    /// The largest box with proportion <paramref name="aspect"/> (width / height) that fits the picture, centred where
    /// <paramref name="current"/> is centred (slid back inside the picture when that sticks out). A free box (aspect 0)
    /// is returned as it is.
    /// </summary>
    internal static Rect FitAspect(Rect current, double aspect, double width, double height)
    {
        if (aspect <= 0 || width < 1 || height < 1)
            return current;
        double w, h;
        if (width / aspect <= height)
        {
            w = Math.Floor(width);
            h = Math.Round(w / aspect, MidpointRounding.AwayFromZero);
        }
        else
        {
            h = Math.Floor(height);
            w = Math.Round(h * aspect, MidpointRounding.AwayFromZero);
        }
        var middle = current.Width > 0 && current.Height > 0 ? current.Center : new Point(width / 2, height / 2);
        return Snap(new Rect(middle.X - w / 2, middle.Y - h / 2, w, h), width, height);
    }

    /// <summary>True when <paramref name="box"/> already has the proportion (within the rounding of whole pixels).</summary>
    internal static bool HasAspect(Rect box, double aspect) =>
        aspect > 0 && box.Height > 0 && Math.Abs(box.Width - box.Height * aspect) <= Math.Max(1, aspect);

    /// <summary>
    /// <paramref name="current"/> with a typed width and/or height. With a fixed proportion the other side follows (and the
    /// box shrinks to fit the picture); otherwise only the typed side changes. The box keeps its centre as far as the
    /// picture allows.
    /// </summary>
    internal static Rect WithSize(Rect current, int? newWidth, int? newHeight, double aspect, double width, double height)
    {
        double w = current.Width, h = current.Height;
        if (aspect > 0)
        {
            if (newWidth is { } typedWidth)
            {
                w = Math.Clamp(typedWidth, 1, Math.Max(1, Math.Floor(Math.Min(width, height * aspect))));
                h = Math.Max(1, Math.Round(w / aspect, MidpointRounding.AwayFromZero));
            }
            else if (newHeight is { } typedHeight)
            {
                h = Math.Clamp(typedHeight, 1, Math.Max(1, Math.Floor(Math.Min(height, width / aspect))));
                w = Math.Max(1, Math.Round(h * aspect, MidpointRounding.AwayFromZero));
            }
        }
        else
        {
            if (newWidth is { } typedWidth)
                w = Math.Clamp(typedWidth, 1, Math.Max(1, width));
            if (newHeight is { } typedHeight)
                h = Math.Clamp(typedHeight, 1, Math.Max(1, height));
        }
        var middle = current.Width > 0 && current.Height > 0 ? current.Center : new Point(width / 2, height / 2);
        return Snap(new Rect(middle.X - w / 2, middle.Y - h / 2, w, h), width, height);
    }

    /// <summary>
    /// <paramref name="box"/> turned a quarter: width and height swapped around the same centre. When the turned box does
    /// not fit the picture it is the largest one that does (with the new proportion <paramref name="aspect"/> when fixed).
    /// </summary>
    internal static Rect Swapped(Rect box, double aspect, double width, double height)
    {
        double w = box.Height, h = box.Width;
        if (w > width || h > height)
        {
            if (aspect > 0)
                return FitAspect(box, aspect, width, height);
            var scale = Math.Min(width / w, height / h);
            w = Math.Max(1, Math.Round(w * scale, MidpointRounding.AwayFromZero));
            h = Math.Max(1, Math.Round(h * scale, MidpointRounding.AwayFromZero));
        }
        var middle = box.Center;
        return Snap(new Rect(middle.X - w / 2, middle.Y - h / 2, w, h), width, height);
    }

    /// <summary>
    /// What the crop of a picture of <paramref name="imageWidth"/> × <paramref name="imageHeight"/> is when it is not
    /// dragged but centred: <paramref name="exact"/> (clipped to the picture) when a size was typed, otherwise the largest
    /// box of the proportion, otherwise the whole picture.
    /// </summary>
    internal static Rect CenteredCrop(int imageWidth, int imageHeight, double aspect, Size? exact)
    {
        var whole = new Rect(0, 0, imageWidth, imageHeight);
        if (exact is { } size)
        {
            var w = Math.Max(1, Math.Min(size.Width, imageWidth));
            var h = Math.Max(1, Math.Min(size.Height, imageHeight));
            return Snap(new Rect((imageWidth - w) / 2, (imageHeight - h) / 2, w, h), imageWidth, imageHeight);
        }
        return aspect > 0 ? FitAspect(whole, aspect, imageWidth, imageHeight) : whole;
    }

    /// <summary>The box on whole pixels, at least 1 × 1 and wholly inside a picture of <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal static Rect Snap(Rect box, double width, double height)
    {
        var maxWidth = Math.Max(1, Math.Floor(width));
        var maxHeight = Math.Max(1, Math.Floor(height));
        var w = Math.Clamp(Math.Round(box.Width, MidpointRounding.AwayFromZero), 1, maxWidth);
        var h = Math.Clamp(Math.Round(box.Height, MidpointRounding.AwayFromZero), 1, maxHeight);
        var x = Math.Clamp(Math.Round(box.X, MidpointRounding.AwayFromZero), 0, maxWidth - w);
        var y = Math.Clamp(Math.Round(box.Y, MidpointRounding.AwayFromZero), 0, maxHeight - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>The box with even position and size (rounded down): what yuv420p video can be cropped to.</summary>
    internal static Rect Even(Rect box, double width, double height)
    {
        static double Down(double value) => Math.Floor(value / 2) * 2;
        var w = Math.Max(2, Down(box.Width));
        var h = Math.Max(2, Down(box.Height));
        var x = Math.Min(Down(box.X), Math.Max(0, Down(width) - w));
        var y = Math.Min(Down(box.Y), Math.Max(0, Down(height) - h));
        return new Rect(x, y, Math.Min(w, Down(width)), Math.Min(h, Down(height)));
    }
}
