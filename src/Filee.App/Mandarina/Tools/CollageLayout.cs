// Mandarina: where the photos of a collage go. A pure function of the photo count, the layout, the canvas size and the
// spacing (all in canvas pixels), so it is tested apart from any picture work. The canvas keeps a margin as wide as the
// gap between photos, cells of one grid are exactly equal (the few spare pixels widen the margin), and a short last row
// is centred.

using System.Drawing;

namespace Filee.App.Mandarina.Tools;

/// <summary>The arrangements of the COLLAGE tool.</summary>
public enum CollageStyle
{
    /// <summary>⌈√n⌉ columns, as many rows as needed, equal cells.</summary>
    Grid,

    /// <summary>Side by side, one row.</summary>
    Row,

    /// <summary>One above the other, one column.</summary>
    Column,

    /// <summary>The first photo large (left, or on top when the canvas is tall), the rest in a grid beside it.</summary>
    Featured,
}

public static class CollageLayout
{
    /// <summary>Fewest photos that make a collage.</summary>
    public const int MinPhotos = 2;

    /// <summary>Most photos a collage takes: beyond that the cells are stamps.</summary>
    public const int MaxPhotos = 30;

    /// <summary>The smallest a cell gets whatever spacing is asked for.</summary>
    private const int MinCell = 4;

    /// <summary>The share of the canvas the large photo of <see cref="CollageStyle.Featured"/> takes.</summary>
    private const double FeaturedShare = 0.6;

    /// <summary>Columns of the grid layout: ⌈√count⌉.</summary>
    public static int Columns(int count) => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, count)) - 1e-9));

    /// <summary>
    /// The cells (x, y, width, height) of <paramref name="count"/> photos on a canvas of <paramref name="canvasWidth"/> ×
    /// <paramref name="canvasHeight"/>, in the order of the photos. Neighbouring cells are exactly <paramref name="spacing"/>
    /// apart, as is the canvas edge from the cells next to it (spacing is only reduced when it would leave a cell
    /// smaller than a few pixels). Cells never overlap and never leave the canvas.
    /// </summary>
    public static IReadOnlyList<Rectangle> Compute(int count, CollageStyle style, int canvasWidth, int canvasHeight, int spacing)
    {
        if (count <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
            return [];
        spacing = Math.Max(0, spacing);
        var margin = Math.Min(spacing, Math.Max(0, (Math.Min(canvasWidth, canvasHeight) - MinCell) / 2));
        var inner = Rectangle.FromLTRB(margin, margin, canvasWidth - margin, canvasHeight - margin);
        return style switch
        {
            CollageStyle.Row => GridIn(inner, count, count, spacing),
            CollageStyle.Column => GridIn(inner, count, 1, spacing),
            CollageStyle.Featured when count > 1 => FeaturedIn(inner, count, spacing),
            CollageStyle.Featured => [inner],
            _ => GridIn(inner, count, Columns(count), spacing),
        };
    }

    /// <summary>
    /// The shape (width / height) a layout gets while the canvas proportion is left on Auto: square cells for the grid, a
    /// strip of up to four squares for a row or column, 4:3 for the featured layout.
    /// </summary>
    public static double AutoRatio(CollageStyle style, int count)
    {
        count = Math.Max(1, count);
        return style switch
        {
            CollageStyle.Row => Math.Clamp(count, 1, 4),
            CollageStyle.Column => 1.0 / Math.Clamp(count, 1, 4),
            CollageStyle.Featured => 4.0 / 3,
            _ => (double)Columns(count) / ((count + Columns(count) - 1) / Columns(count)),
        };
    }

    /// <summary>The pixel size of a canvas of shape <paramref name="ratio"/> (width / height) whose longer side is <paramref name="longSide"/>.</summary>
    public static (int Width, int Height) CanvasSize(double ratio, int longSide)
    {
        longSide = Math.Max(1, longSide);
        if (ratio <= 0)
            ratio = 1;
        return ratio >= 1
            ? (longSide, Math.Max(1, (int)Math.Round(longSide / ratio)))
            : (Math.Max(1, (int)Math.Round(longSide * ratio)), longSide);
    }

    /// <summary>Equal cells in <paramref name="columns"/> columns, row after row; a short last row is centred.</summary>
    private static List<Rectangle> GridIn(Rectangle region, int count, int columns, int gap)
    {
        var rows = (count + columns - 1) / columns;
        var across = Divide(region.Left, region.Width, columns, gap);
        var down = Divide(region.Top, region.Height, rows, gap);
        var cells = new List<Rectangle>(count);
        for (var i = 0; i < count; i++)
        {
            int row = i / columns, column = i % columns;
            var missing = columns - Math.Min(columns, count - row * columns);
            var shift = missing * (across.Size + across.Gap) / 2;
            cells.Add(new Rectangle(across.Starts[column] + shift, down.Starts[row], across.Size, down.Size));
        }
        return cells;
    }

    /// <summary>The large photo, then the others in the grid whose cells come closest to square in what is left.</summary>
    private static List<Rectangle> FeaturedIn(Rectangle region, int count, int gap)
    {
        var horizontal = region.Width >= region.Height;
        var main = horizontal ? region.Width : region.Height;
        var cross = horizontal ? region.Height : region.Width;
        gap = Math.Min(gap, Math.Max(0, main - 2 * MinCell));
        var large = Math.Clamp((int)Math.Round((main - gap) * FeaturedShare), MinCell, Math.Max(MinCell, main - gap - MinCell));
        var rest = Math.Max(1, main - gap - large);
        var first = horizontal ? new Rectangle(region.Left, region.Top, large, cross) : new Rectangle(region.Left, region.Top, cross, large);
        var others = horizontal
            ? new Rectangle(region.Left + large + gap, region.Top, rest, cross)
            : new Rectangle(region.Left, region.Top + large + gap, cross, rest);
        var cells = new List<Rectangle>(count) { first };
        cells.AddRange(GridIn(others, count - 1, BestColumns(count - 1, others.Width, others.Height, gap), gap));
        return cells;
    }

    /// <summary>How many columns make <paramref name="count"/> cells in a <paramref name="width"/> × <paramref name="height"/> box the closest to square.</summary>
    private static int BestColumns(int count, int width, int height, int gap)
    {
        int best = 1;
        var bestScore = double.MaxValue;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;
            double cellWidth = (width - (columns - 1) * gap) / (double)columns, cellHeight = (height - (rows - 1) * gap) / (double)rows;
            if (cellWidth < 1 || cellHeight < 1)
                continue;
            // Empty cells count against a shape: 4 photos prefer a tidy 2 × 2 of slightly wide cells to 3 + 1.
            var score = Math.Abs(Math.Log(cellWidth / cellHeight)) + 0.25 * (columns * rows - count);
            if (score < bestScore - 1e-9)
            {
                best = columns;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// Splits <paramref name="extent"/> pixels from <paramref name="start"/> into <paramref name="parts"/> equal cells with
    /// <paramref name="gap"/> between them; the pixels left over go half to each end. The gap shrinks only when the cells
    /// would get smaller than <see cref="MinCell"/>.
    /// </summary>
    private static (int[] Starts, int Size, int Gap) Divide(int start, int extent, int parts, int gap)
    {
        if (parts > 1)
            gap = Math.Min(gap, Math.Max(0, (extent - parts * MinCell) / (parts - 1)));
        else
            gap = 0;
        var size = Math.Max(1, (extent - (parts - 1) * gap) / parts);
        var used = parts * size + (parts - 1) * gap;
        var first = start + Math.Max(0, (extent - used) / 2);
        var starts = new int[parts];
        for (var i = 0; i < parts; i++)
            starts[i] = first + i * (size + gap);
        return (starts, size, gap);
    }
}
