// Mandarina: the floating shelf (the Float action) sticks to the edge of the screen it is let go near and keeps to it as it grows.
// The geometry is checked with plain numbers (a 1080p laptop at 125 %: 1032 px of usable height after the taskbar); then the
// real shelf window is grabbed by its title, moved and checked against the screen it opened on (it docks 250 ms after it stops).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Mandarina;
using Filee.App.Services;
using Filee.Core.Localization;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaShelfTests : IDisposable
{
    private static readonly PixelRect Area = new(0, 0, 1920, 1032);

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-shelf").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────── The numbers ─────────

    [Fact]
    public void A_shelf_let_go_near_the_right_edge_sticks_flush_to_it()
    {
        // Margin 12 DIP at 125 % = 15 px; the card is the window minus that on each side. Its right edge is 35 px (28 DIP) from the screen's.
        var (position, edges) = ShelfDock.Snap(new PixelRect(1600, 500, 300, 200), Area, 1.25, 12);

        Assert.Equal(ShelfEdges.Right, edges);
        Assert.Equal(new PixelPoint(1920 + 15 - 300, 500), position);
    }

    [Fact]
    public void A_shelf_in_the_middle_of_the_screen_stays_where_it_is()
    {
        var (position, edges) = ShelfDock.Snap(new PixelRect(800, 400, 300, 200), Area, 1.25, 12);

        Assert.Equal(ShelfEdges.None, edges);
        Assert.Equal(new PixelPoint(800, 400), position);
    }

    [Fact]
    public void A_shelf_just_out_of_reach_of_the_edge_does_not_stick()
    {
        // 36 px from the edge is over the 35 px a 125 % screen allows.
        var (_, edges) = ShelfDock.Snap(new PixelRect(1599, 500, 300, 200), Area, 1.25, 12);

        Assert.Equal(ShelfEdges.None, edges);
    }

    [Fact]
    public void A_corner_sticks_on_both_sides()
    {
        var (position, edges) = ShelfDock.Snap(new PixelRect(-10, 3, 300, 200), Area, 1.25, 12);
        Assert.Equal(ShelfEdges.Left | ShelfEdges.Top, edges);
        Assert.Equal(new PixelPoint(-15, -15), position);

        (position, edges) = ShelfDock.Snap(new PixelRect(1610, 840, 300, 200), Area, 1.0, 12);
        Assert.Equal(ShelfEdges.Right | ShelfEdges.Bottom, edges);
        Assert.Equal(new PixelPoint(1920 + 12 - 300, 1032 + 12 - 200), position);
    }

    [Fact]
    public void A_shelf_hanging_over_an_edge_is_brought_back_and_docked()
    {
        var (position, edges) = ShelfDock.Snap(new PixelRect(1800, 500, 300, 200), Area, 1.0, 12);

        Assert.Equal(ShelfEdges.Right, edges);
        Assert.Equal(1920 + 12 - 300, position.X);
    }

    [Fact]
    public void A_docked_shelf_that_grows_keeps_to_its_edge()
    {
        // It was 300 px wide against the right edge and became 420 px wide: it grows to the left.
        var grown = new PixelRect(1920 + 15 - 300, 500, 420, 200);

        Assert.Equal(new PixelPoint(1920 + 15 - 420, 500), ShelfDock.Keep(grown, Area, ShelfEdges.Right, 1.25, 12));

        var bottom = new PixelRect(700, 1032 + 15 - 200, 300, 330);
        Assert.Equal(new PixelPoint(700, 1032 + 15 - 330), ShelfDock.Keep(bottom, Area, ShelfEdges.Bottom, 1.25, 12));

        var corner = new PixelRect(-15, -15, 500, 500);
        Assert.Equal(new PixelPoint(-15, -15), ShelfDock.Keep(corner, Area, ShelfEdges.Left | ShelfEdges.Top, 1.25, 12));
    }

    [Fact]
    public void An_undocked_shelf_that_grows_past_the_screen_is_pulled_back_in()
    {
        // Floated next to the right edge without touching it, then more files arrive and make it wider.
        var wide = new PixelRect(1700, 400, 420, 200);

        var position = ShelfDock.Keep(wide, Area, ShelfEdges.None, 1.0, 12);

        Assert.Equal(1920 + 12 - 420, position.X);
        Assert.Equal(400, position.Y);
    }

    [Fact]
    public void A_shelf_bigger_than_the_screen_keeps_its_top_left_corner_in_view()
    {
        var huge = new PixelRect(300, 300, 2400, 1400);

        Assert.Equal(new PixelPoint(-12, -12), ShelfDock.Keep(huge, Area, ShelfEdges.None, 1.0, 12));
    }

    // ───────── The window ─────────

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Opens the shelf with a photo on it, as the Float action does.</summary>
    private FloatShelfWindow OpenShelf(string language)
    {
        TestServices.EnsureInitialized(language);
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"), 600, 400);
        FloatShelfWindow.Open([photo], AppHost.Get<ILocalizer>(), new PixelPoint(300, 300));
        Pump();
        return FloatShelfWindow.Current ?? throw new InvalidOperationException("the shelf did not open");
    }

    private static PixelRect Card(FloatShelfWindow shelf)
    {
        var size = PixelSize.FromSize(shelf.ClientSize, shelf.RenderScaling);
        var margin = (int)Math.Round(12 * shelf.RenderScaling);
        return new PixelRect(shelf.Position.X + margin, shelf.Position.Y + margin, size.Width - 2 * margin, size.Height - 2 * margin);
    }

    [AvaloniaFact]
    public async Task Dragging_the_shelf_by_its_title_and_letting_go_near_the_edge_docks_it()
    {
        var shelf = OpenShelf("es");
        try
        {
            var area = (shelf.Screens.ScreenFromWindow(shelf) ?? shelf.Screens.Primary!).WorkingArea;
            var width = PixelSize.FromSize(shelf.ClientSize, shelf.RenderScaling).Width;
            Assert.Equal(ShelfEdges.None, shelf.Docked);

            // Grab the title; the system then moves the window (here: the test does) to within a few pixels of the right edge.
            shelf.MouseDown(new Point(60, 32), MouseButton.Left);
            shelf.Position = new PixelPoint(area.Right - width - 4, area.Y + 200);
            await WaitAsync(() => shelf.Docked == ShelfEdges.Right, 3000);

            Assert.Equal(area.Right, Card(shelf).Right);          // the card is flush with the edge
            Assert.Equal(area.Y + 200, shelf.Position.Y);         // and did not move any other way
        }
        finally
        {
            shelf.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_docked_shelf_that_gets_wider_keeps_its_edge_flush()
    {
        var shelf = OpenShelf("es");
        try
        {
            var area = (shelf.Screens.ScreenFromWindow(shelf) ?? shelf.Screens.Primary!).WorkingArea;
            var width = PixelSize.FromSize(shelf.ClientSize, shelf.RenderScaling).Width;
            shelf.MouseDown(new Point(60, 32), MouseButton.Left);
            shelf.Position = new PixelPoint(area.Right - width - 4, area.Y + 200);
            await WaitAsync(() => shelf.Docked == ShelfEdges.Right, 3000);

            // More files make it wider (here the test sets the width: the shelf grows to the right, and must come back to its edge).
            shelf.SizeToContent = SizeToContent.Manual;
            shelf.Width = shelf.ClientSize.Width + 150;
            await WaitAsync(() => PixelSize.FromSize(shelf.ClientSize, shelf.RenderScaling).Width > width + 100, 3000);
            await WaitAsync(() => Card(shelf).Right == area.Right, 3000);

            Assert.Equal(ShelfEdges.Right, shelf.Docked);
            Assert.True(Card(shelf).Width > PixelSize.FromSize(new Size(shelf.ClientSize.Width, 0), shelf.RenderScaling).Width - 2 * 15 - 1);
        }
        finally
        {
            shelf.Close();
        }
    }

    [AvaloniaFact]
    public async Task Letting_go_in_the_middle_of_the_screen_does_not_dock_it()
    {
        var shelf = OpenShelf("en");
        try
        {
            var area = (shelf.Screens.ScreenFromWindow(shelf) ?? shelf.Screens.Primary!).WorkingArea;
            var middle = new PixelPoint(area.X + area.Width / 2, area.Y + area.Height / 2);

            shelf.MouseDown(new Point(60, 32), MouseButton.Left);
            shelf.Position = middle;
            await Task.Delay(600);        // longer than the settling time: it would have docked by now
            Pump();

            Assert.Equal(ShelfEdges.None, shelf.Docked);
            Assert.Equal(middle, shelf.Position);
        }
        finally
        {
            shelf.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_shelf_nobody_has_grabbed_stays_where_the_wheel_put_it_even_next_to_an_edge()
    {
        var shelf = OpenShelf("en");
        try
        {
            var area = (shelf.Screens.ScreenFromWindow(shelf) ?? shelf.Screens.Primary!).WorkingArea;
            var width = PixelSize.FromSize(shelf.ClientSize, shelf.RenderScaling).Width;
            var near = new PixelPoint(area.Right - width - 4, area.Y + 200);

            shelf.Position = near;       // not by the user: no title was pressed
            await Task.Delay(600);
            Pump();

            Assert.Equal(ShelfEdges.None, shelf.Docked);
            Assert.Equal(near, shelf.Position);
        }
        finally
        {
            shelf.Close();
        }
    }
}
