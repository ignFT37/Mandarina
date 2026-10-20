// Mandarina: where the floating shelf sits against the edges of the screen. Let go of the shelf near an edge of the usable area and
// it sticks flush to it (corners too); while it stays docked it keeps to that edge as it grows with every file added, so a shelf at
// the right edge grows to the left instead of off the screen. The window is bigger than the card it shows: a transparent margin
// around the card holds its shadow, so "flush" is measured on the card, not on the window.

using Avalonia;

namespace Filee.App.Mandarina;

[Flags]
internal enum ShelfEdges
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
}

internal static class ShelfDock
{
    /// <summary>How close, in DIP, the card has to be to an edge of the usable area to stick to it.</summary>
    public const double StickDistance = 28;

    /// <summary>
    /// Where the window of a shelf let go at <paramref name="window"/> (pixels) goes: its card flush against each edge of
    /// <paramref name="area"/> that it is within <see cref="StickDistance"/> of, and the edges it docked to. A card that hangs
    /// over an edge is brought back inside. <paramref name="margin"/> is the transparent margin around the card, in DIP.
    /// </summary>
    public static (PixelPoint Position, ShelfEdges Edges) Snap(PixelRect window, PixelRect area, double scaling, double margin)
    {
        var space = (int)Math.Round(margin * scaling);
        var stick = StickDistance * scaling;
        var edges = ShelfEdges.None;
        var (x, y) = (window.X, window.Y);

        var card = Inset(window, space);
        // Horizontally: the left edge wins a tie (a card wider than the area is docked to it on both sides).
        if (card.X - area.X <= stick)
        {
            edges |= ShelfEdges.Left;
            x = area.X - space;
        }
        else if (area.Right - card.Right <= stick)
        {
            edges |= ShelfEdges.Right;
            x = area.Right + space - window.Width;
        }
        if (card.Y - area.Y <= stick)
        {
            edges |= ShelfEdges.Top;
            y = area.Y - space;
        }
        else if (area.Bottom - card.Bottom <= stick)
        {
            edges |= ShelfEdges.Bottom;
            y = area.Bottom + space - window.Height;
        }
        return (new PixelPoint(x, y), edges);
    }

    /// <summary>
    /// Where the window goes after it changed size (a file was added to the shelf): still flush against the edges it was docked to,
    /// and never hanging over the usable area, docked or not.
    /// </summary>
    public static PixelPoint Keep(PixelRect window, PixelRect area, ShelfEdges docked, double scaling, double margin)
    {
        var space = (int)Math.Round(margin * scaling);
        var (x, y) = (window.X, window.Y);

        if (docked.HasFlag(ShelfEdges.Left))
            x = area.X - space;
        else if (docked.HasFlag(ShelfEdges.Right))
            x = area.Right + space - window.Width;
        if (docked.HasFlag(ShelfEdges.Top))
            y = area.Y - space;
        else if (docked.HasFlag(ShelfEdges.Bottom))
            y = area.Bottom + space - window.Height;

        // Whatever the card still hangs over comes back inside (the left and top edges win when the card is bigger than the area).
        var card = Inset(new PixelRect(x, y, window.Width, window.Height), space);
        if (card.Right > area.Right)
            x -= card.Right - area.Right;
        if (card.Bottom > area.Bottom)
            y -= card.Bottom - area.Bottom;
        if (x + space < area.X)
            x = area.X - space;
        if (y + space < area.Y)
            y = area.Y - space;
        return new PixelPoint(x, y);
    }

    private static PixelRect Inset(PixelRect window, int space) =>
        new(window.X + space, window.Y + space, Math.Max(0, window.Width - 2 * space), Math.Max(0, window.Height - 2 * space));
}
