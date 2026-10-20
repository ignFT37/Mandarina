// Mandarina: CROP, for photos and for video, and the crop box control (CropSurface) they share. The geometry is
// checked with plain numbers; the panels run against the real Magick.NET and FFmpeg (engines\ffmpeg next to the test
// binaries), write mandarina-panel-crop*.png to the screenshot folder for review, and are driven through a real window
// for the wiring between the surface and the panel.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using ImageMagick.Drawing;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaCropTests : IDisposable
{
    private const string NoFfmpeg = "FFmpeg is not installed next to the tests (engines\\ffmpeg).";

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-crop").FullName;

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

    // ───────── Helpers ─────────

    private string Photo(string name, uint width = 900, uint height = 600) => MakePhoto(Path.Combine(_root, name), width, height);

    private static (uint Width, uint Height) SizeOf(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }

    private static byte[] PixelAt(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToArray()!;
    }

    private static void AssertSimilar(byte[] expected, byte[] actual, int tolerance, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"{what}: channel {i} is {actual[i]}, expected about {expected[i]}");
    }

    private async Task<CropImagePanel> OpenPhotosAsync(params string[] files)
    {
        var panel = new CropImagePanel(Context(files));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private async Task<CropVideoPanel> OpenVideoAsync(string file)
    {
        var panel = new CropVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        return panel;
    }

    /// <summary>320×240, 3 s, 25 fps, with a 440 Hz tone: a picture with structure everywhere, so a wrong offset shows.</summary>
    private Task<string?> MakeVideoAsync(string name, bool withAudio = true) =>
        withAudio
            ? MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=25", "-f", "lavfi", "-i",
                "sine=frequency=440:duration=3", "-c:v", "libx264", "-c:a", "aac", "-shortest")
            : MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=25", "-c:v", "libx264");

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private static CropSurface Surface(Rect selection, double aspect = 0)
    {
        var surface = new CropSurface { PixelWidth = 900, PixelHeight = 600, Selection = selection, AspectRatio = aspect };
        surface.Measure(new Size(424, 324));
        surface.Arrange(new Rect(0, 0, 424, 324));
        return surface;
    }

    // ───────── The geometry of the box ─────────

    [Fact]
    public void Dragging_any_handle_with_a_fixed_proportion_keeps_it_inside_the_picture()
    {
        var random = new Random(7);
        foreach (var handle in Enum.GetValues<CropHandle>().Where(h => h > CropHandle.Move))
        foreach (var aspect in new[] { 0.5625, 0.8, 1, 4.0 / 3, 16.0 / 9 })
        foreach (var wanted in new[] { 120.0, 300.0, 520.0 })
        foreach (var place in new[] { 0, 1, 2 })       // top left, middle, bottom right
        {
            // A box that already has the proportion, as the surface makes them.
            var width = Math.Min(wanted, Math.Floor(600 * aspect));
            var height = Math.Round(width / aspect);
            var from = place switch
            {
                0 => new Rect(0, 0, width, height),
                1 => new Rect(Math.Round((900 - width) / 2), Math.Round((600 - height) / 2), width, height),
                _ => new Rect(900 - width, 600 - height, width, height),
            };
            for (var i = 0; i < 100; i++)
            {
                var box = CropMath.Drag(from, handle, random.Next(-1300, 1300), random.Next(-1300, 1300), aspect, 900, 600, 16);
                var what = $"{handle} {aspect:0.##} from {from} to {box}";
                Assert.True(box.Left >= 0 && box.Top >= 0 && box.Right <= 900 && box.Bottom <= 600, what + ": leaves the picture");
                Assert.True(box.Width >= 16 && box.Height >= 16, what + ": below the minimum");
                Assert.True(Math.Abs(box.Width - box.Height * aspect) <= Math.Max(1, aspect), what + ": proportion lost");
                Assert.True(box.Width == Math.Round(box.Width) && box.Height == Math.Round(box.Height)
                            && box.X == Math.Round(box.X) && box.Y == Math.Round(box.Y), what + ": not whole pixels");
            }
        }
    }

    [Fact]
    public void A_corner_keeps_the_opposite_corner_and_a_side_the_opposite_side_when_the_proportion_is_fixed()
    {
        var start = new Rect(200, 150, 300, 200);       // 3:2
        var corner = CropMath.Drag(start, CropHandle.TopLeft, -60, -40, 1.5, 900, 600, 16);
        Assert.Equal(start.Right, corner.Right);
        Assert.Equal(start.Bottom, corner.Bottom);
        Assert.True(corner.Width > start.Width);

        var side = CropMath.Drag(start, CropHandle.Right, 90, 0, 1.5, 900, 600, 16);
        Assert.Equal(start.Left, side.Left);
        Assert.Equal(390, side.Width);
        Assert.Equal(260, side.Height);
        Assert.Equal(start.Y + start.Height / 2, side.Y + side.Height / 2);     // grows around the middle

        var top = CropMath.Drag(start, CropHandle.Top, 0, -30, 1.5, 900, 600, 16);
        Assert.Equal(start.Bottom, top.Bottom);
        Assert.Equal(230, top.Height);
        Assert.Equal(345, top.Width);
    }

    [Fact]
    public void A_free_box_follows_the_edges_it_is_dragged_by_and_never_leaves_the_picture_or_the_minimum()
    {
        var start = new Rect(100, 100, 300, 200);
        var left = CropMath.Drag(start, CropHandle.Left, -40, 999, 0, 900, 600, 16);
        Assert.Equal(new Rect(60, 100, 340, 200), left);                        // only the left edge moved
        var squeeze = CropMath.Drag(start, CropHandle.Right, -5000, 0, 0, 900, 600, 16);
        Assert.Equal(new Rect(100, 100, 16, 200), squeeze);                     // stops at the minimum
        var out1 = CropMath.Drag(start, CropHandle.BottomRight, 5000, 5000, 0, 900, 600, 16);
        Assert.Equal(new Rect(100, 100, 800, 500), out1);                       // stops at the picture
        var out2 = CropMath.Drag(start, CropHandle.TopLeft, -5000, -5000, 0, 900, 600, 16);
        Assert.Equal(new Rect(0, 0, 400, 300), out2);
    }

    [Fact]
    public void Moving_keeps_the_size_and_stops_at_the_edges()
    {
        var start = new Rect(100, 100, 300, 200);
        Assert.Equal(new Rect(130, 80, 300, 200), CropMath.Drag(start, CropHandle.Move, 30, -20, 1.5, 900, 600, 16));
        Assert.Equal(new Rect(600, 400, 300, 200), CropMath.Drag(start, CropHandle.Move, 9999, 9999, 0, 900, 600, 16));
        Assert.Equal(new Rect(0, 0, 300, 200), CropMath.Drag(start, CropHandle.Move, -9999, -9999, 0, 900, 600, 16));
    }

    [Fact]
    public void The_largest_box_of_a_proportion_keeps_the_centre_as_far_as_the_picture_allows()
    {
        var whole = new Rect(0, 0, 900, 600);
        Assert.Equal(new Rect(150, 0, 600, 600), CropMath.FitAspect(whole, 1, 900, 600));
        Assert.Equal(new Rect(50, 0, 800, 600), CropMath.FitAspect(whole, 4.0 / 3, 900, 600));
        Assert.Equal(new Rect(0, 47, 900, 506), CropMath.FitAspect(whole, 16.0 / 9, 900, 600));
        Assert.Equal(new Rect(281, 0, 338, 600), CropMath.FitAspect(whole, 9.0 / 16, 900, 600));
        Assert.Equal(whole, CropMath.FitAspect(whole, 1.5, 900, 600));

        // Around the box's own centre, slid back inside when the largest box would stick out.
        Assert.Equal(new Rect(0, 0, 600, 600), CropMath.FitAspect(new Rect(100, 100, 300, 200), 1, 900, 600));
        Assert.Equal(new Rect(300, 0, 600, 600), CropMath.FitAspect(new Rect(700, 300, 150, 100), 1, 900, 600));
        Assert.Equal(new Rect(100, 100, 300, 200), CropMath.FitAspect(new Rect(100, 100, 300, 200), 0, 900, 600));    // free: untouched
    }

    [Fact]
    public void Typed_sizes_follow_the_proportion_and_are_cut_to_the_picture()
    {
        var box = new Rect(250, 150, 400, 300);
        Assert.Equal(new Rect(250, 100, 400, 400), CropMath.WithSize(box, null, 400, 0, 900, 600));          // free: only the height
        Assert.Equal(new Rect(150, 150, 600, 300), CropMath.WithSize(box, 600, null, 0, 900, 600));
        Assert.Equal(new Rect(0, 0, 900, 600), CropMath.WithSize(box, 5000, 5000, 0, 900, 600));            // bigger than the picture
        var locked = CropMath.WithSize(box, 600, null, 1.5, 900, 600);
        Assert.Equal((600, 400), (locked.Width, locked.Height));
        locked = CropMath.WithSize(box, null, 200, 1.5, 900, 600);
        Assert.Equal((300, 200), (locked.Width, locked.Height));
        locked = CropMath.WithSize(box, 5000, null, 1.5, 900, 600);
        Assert.Equal((900, 600), (locked.Width, locked.Height));
    }

    [Fact]
    public void A_box_turned_a_quarter_keeps_its_centre_and_shrinks_to_fit()
    {
        Assert.Equal(new Rect(200, 0, 200, 400), CropMath.Swapped(new Rect(100, 100, 400, 200), 0, 900, 600));
        Assert.Equal(new Rect(281, 0, 338, 600), CropMath.Swapped(new Rect(0, 47, 900, 506), 9.0 / 16, 900, 600));
        var tall = CropMath.Swapped(new Rect(0, 0, 900, 300), 0, 900, 600);                                  // 300×900 does not fit
        Assert.Equal((200, 600), (tall.Width, tall.Height));
    }

    [Fact]
    public void Video_boxes_are_rounded_down_to_even_numbers()
    {
        Assert.Equal(new Rect(10, 6, 100, 74), CropMath.Even(new Rect(11, 7, 101, 75), 320, 240));
        Assert.Equal(new Rect(0, 0, 320, 240), CropMath.Even(new Rect(0, 0, 320, 240), 320, 240));
        Assert.Equal(new Rect(0, 0, 320, 240), CropMath.Even(new Rect(0, 0, 321, 241), 321, 241));
    }

    // ───────── The control ─────────

    [AvaloniaFact]
    public void Dragging_a_handle_of_the_surface_with_a_fixed_proportion_keeps_it_and_stays_in_the_picture()
    {
        var surface = Surface(new Rect(100, 100, 300, 200), aspect: 1.5);
        var corner = surface.SelectionRect.BottomRight;
        Assert.Equal(CropHandle.BottomRight, surface.HitTest(corner));
        Assert.True(surface.BeginDrag(corner));

        surface.DragTo(corner + new Vector(900, 900));          // far beyond the picture
        var box = surface.Selection;
        Assert.Equal((100, 100), (box.X, box.Y));                // the opposite corner stayed
        Assert.True(box.Right <= 900 && box.Bottom <= 600);
        Assert.Equal(1.5, box.Width / box.Height, 2);

        surface.DragTo(corner + new Vector(-40, -10));           // and back in
        Assert.Equal(1.5, surface.Selection.Width / surface.Selection.Height, 2);
        Assert.True(surface.Selection.Width < 300);
        surface.EndDrag();

        // Once released, the pointer no longer moves the box.
        var kept = surface.Selection;
        surface.DragTo(corner + new Vector(100, 100));
        Assert.Equal(kept, surface.Selection);
    }

    [AvaloniaFact]
    public void Dragging_inside_the_surface_moves_the_box_and_a_side_resizes_it_one_way()
    {
        var surface = Surface(new Rect(100, 100, 300, 200));
        var middle = surface.SelectionRect.Center;
        Assert.Equal(CropHandle.Move, surface.HitTest(middle));
        Assert.True(surface.BeginDrag(middle));
        surface.DragTo(middle + new Vector(5000, 5000));
        Assert.Equal(new Rect(600, 400, 300, 200), surface.Selection);       // pinned to the corner, same size
        surface.EndDrag();

        var edge = surface.SelectionRect;
        var rightSide = new Point(edge.Right, edge.Center.Y);
        Assert.Equal(CropHandle.Right, surface.HitTest(rightSide));
        Assert.True(surface.BeginDrag(rightSide));
        surface.DragTo(rightSide - new Vector(60, 0));
        Assert.True(surface.Selection.Width < 300);
        Assert.Equal(200, surface.Selection.Height);
        Assert.Equal(600, surface.Selection.X);
    }

    [AvaloniaFact]
    public void The_surface_can_grab_a_handle_on_the_very_edge_of_the_picture_and_ignores_the_outside()
    {
        var surface = Surface(new Rect(0, 0, 900, 600));
        var image = surface.ImageRect;
        Assert.Equal(CropHandle.TopLeft, surface.HitTest(image.TopLeft));
        Assert.Equal(CropHandle.BottomRight, surface.HitTest(image.BottomRight));
        Assert.Equal(CropHandle.None, surface.HitTest(new Point(2, 2)));          // in the margin, away from every handle

        var small = Surface(new Rect(100, 100, 300, 200));
        Assert.Equal(CropHandle.None, small.HitTest(small.SelectionRect.BottomRight + new Vector(40, 40)));
        Assert.False(small.BeginDrag(small.SelectionRect.BottomRight + new Vector(40, 40)));
    }

    [AvaloniaFact]
    public void A_surface_that_is_not_editable_cannot_be_dragged()
    {
        var surface = Surface(new Rect(100, 100, 300, 200));
        surface.IsEditable = false;
        Assert.False(surface.BeginDrag(surface.SelectionRect.Center));
        surface.DragTo(new Point(300, 300));
        Assert.Equal(new Rect(100, 100, 300, 200), surface.Selection);
    }

    [AvaloniaFact]
    public void Changing_the_proportion_of_the_surface_refits_the_box_around_its_centre()
    {
        var surface = Surface(new Rect(300, 200, 300, 200));       // centre 450, 300
        surface.AspectRatio = 1;
        Assert.Equal(new Rect(150, 0, 600, 600), surface.Selection);
        surface.AspectRatio = 16.0 / 9;
        Assert.Equal(new Rect(0, 47, 900, 506), surface.Selection);
        surface.AspectRatio = 0;                                    // free: the box stays as it is
        Assert.Equal(new Rect(0, 47, 900, 506), surface.Selection);
    }

    // ───────── The panel without a window ─────────

    [AvaloniaFact]
    public async Task The_photo_panel_opens_on_the_whole_picture_and_says_how_big_it_is()
    {
        var panel = await OpenPhotosAsync(Photo("foto.jpg"));
        Assert.Equal((900, 600), (panel.PixelWidth, panel.PixelHeight));
        Assert.Equal(new Rect(0, 0, 900, 600), panel.Selection);
        Assert.Equal("free", panel.SelectedRatio!.Key);
        Assert.Equal(("900", "600", "900 × 600 px"), (panel.WidthText, panel.HeightText, panel.ResultText));
        Assert.Equal("900 × 600 px", panel.OriginalSizeText);
        Assert.NotNull(panel.Preview);
        Assert.True(panel.CanApply);
        Assert.Equal("Recortar", panel.Title);
        Assert.True(PanelRegistry.Has("cropImage"));
    }

    [AvaloniaFact]
    public void The_registry_builds_both_panels_and_only_the_video_one_needs_ffmpeg()
    {
        var context = Context(Photo("foto.jpg"));
        Assert.Null(PanelRegistry.Unavailable("cropImage", context.Loc));
        Assert.Equal(MediaTools.Locate() is null, PanelRegistry.Unavailable("cropVideo", context.Loc) is not null);

        using var photo = (CropImagePanel)PanelRegistry.Create("cropImage", context.Files, context.Loc, context.Platform);
        using var video = (CropVideoPanel)PanelRegistry.Create("cropVideo", context.Files, context.Loc, context.Platform);
        Assert.Equal(("cropImage", "cropVideo"), (photo.Id, video.Id));
    }

    [AvaloniaFact]
    public async Task A_photo_that_cannot_be_read_shows_why_and_cannot_be_applied()
    {
        var bad = Path.Combine(_root, "rota.jpg");
        File.WriteAllText(bad, "this is not a photo");
        var panel = new CropImagePanel(Context(bad));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal("No se puede abrir esta foto.", panel.LoadError);              // not Magick's technical message
        Assert.False(panel.CanApply);
        Assert.False(panel.IsReady);
        Assert.Null(panel.HintText);
        Assert.Equal(("", ""), (panel.WidthText, panel.HeightText));
        panel.SelectRatio("1:1");                      // nothing happens, nothing breaks
        panel.CommitWidth("100");
        Assert.False(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task A_photo_turned_by_its_exif_orientation_is_cropped_as_it_looks()
    {
        var file = Path.Combine(_root, "girada.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 300, 200))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);       // "rotate 90° clockwise to see it": shown 200 wide, 300 tall
            image.SetProfile(exif);
            image.Orientation = OrientationType.RightTop;        // Magick writes the tag from this property when it saves
            image.Write(file);
        }
        using (var stored = new MagickImage(file))
            Assert.Equal(OrientationType.RightTop, stored.Orientation);       // the fixture really is turned
        var panel = await OpenPhotosAsync(file);
        Assert.Equal((200, 300), (panel.PixelWidth, panel.PixelHeight));

        panel.Selection = new Rect(20, 40, 100, 150);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal((100u, 150u), SizeOf(output));
        using var result = new MagickImage(output);
        Assert.True(result.Orientation is OrientationType.TopLeft or OrientationType.Undefined,    // upright: no viewer turns it again
            $"orientation {result.Orientation}");
    }

    [AvaloniaFact]
    public async Task The_pills_set_the_box_and_the_boxes_set_each_other()
    {
        var panel = await OpenPhotosAsync(Photo("foto.jpg"));

        panel.SelectRatio("4:3");
        Assert.Equal(new Rect(50, 0, 800, 600), panel.Selection);
        Assert.Equal(4.0 / 3, panel.AspectRatio, 6);
        Assert.Equal("4:3", panel.SelectedRatio!.Key);
        Assert.Single(panel.Ratios, ratio => ratio.IsSelected);

        panel.SelectRatio("1:1");
        Assert.Equal(new Rect(150, 0, 600, 600), panel.Selection);

        panel.SelectRatio("3:2");                       // the box is the whole picture again: 900 × 600
        Assert.Equal(new Rect(0, 0, 900, 600), panel.Selection);
        panel.CommitWidth("600");                       // the height follows (3:2)
        Assert.Equal(("600", "400", "600 × 400 px"), (panel.WidthText, panel.HeightText, panel.ResultText));
        Assert.Equal(new Rect(150, 100, 600, 400), panel.Selection);
        panel.CommitHeight("200");                      // and the width follows the height
        Assert.Equal(("300", "200"), (panel.WidthText, panel.HeightText));
        panel.CommitWidth("5000");                      // more than the picture has: the most that fits, in proportion
        Assert.Equal(("900", "600"), (panel.WidthText, panel.HeightText));

        panel.SelectRatio("free");
        panel.CommitWidth("400");                       // free: only the typed side changes
        Assert.Equal(("400", "600"), (panel.WidthText, panel.HeightText));
        panel.CommitHeight("nonsense");                 // unreadable: the old number stays
        Assert.Equal("600", panel.HeightText);
        panel.CommitHeight("0");
        Assert.Equal("600", panel.HeightText);
        panel.CommitHeight("-5");
        Assert.Equal("600", panel.HeightText);

        panel.SelectRatio("original");
        Assert.Equal(new Rect(0, 0, 900, 600), panel.Selection);
    }

    [AvaloniaFact]
    public async Task Turning_the_proportion_swaps_width_and_height_and_names_a_proportion_nobody_listed()
    {
        var panel = await OpenPhotosAsync(Photo("foto.jpg"));
        var custom = panel.Ratios[^1];
        Assert.False(custom.IsVisible);

        panel.SelectRatio("4:3");
        panel.RotateRatio();                            // 3:4: no pill has it, so the extra pill says so
        Assert.Equal(0.75, panel.AspectRatio, 6);
        Assert.True(custom.IsVisible && custom.IsSelected);
        Assert.Equal("3:4", custom.Label);
        Assert.Equal(new Rect(225, 0, 450, 600), panel.Selection);
        Assert.Single(panel.Ratios, ratio => ratio.IsSelected);

        panel.RotateRatio();                            // and back: the 4:3 pill takes over from the extra one
        Assert.Equal("4:3", panel.SelectedRatio!.Key);
        Assert.False(custom.IsVisible);
        Assert.Equal(new Rect(150, 75, 600, 450), panel.Selection);

        panel.SelectRatio("16:9");
        panel.RotateRatio();                            // 9:16 exists as a pill
        Assert.Equal("9:16", panel.SelectedRatio!.Key);
        Assert.False(custom.IsVisible);

        panel.SelectRatio("free");
        panel.Selection = new Rect(100, 100, 400, 200);
        panel.RotateRatio();                            // a free box turns on its side
        Assert.Equal(new Rect(200, 0, 200, 400), panel.Selection);
        Assert.Equal(0, panel.AspectRatio);

        panel.SelectRatio("1:1");
        var square = panel.Selection;
        panel.RotateRatio();                            // a square stays a square
        Assert.Equal(square, panel.Selection);
        Assert.Equal("1:1", panel.SelectedRatio!.Key);
    }

    [AvaloniaFact]
    public async Task With_several_photos_turning_the_proportion_also_turns_the_size_that_was_typed()
    {
        var panel = await OpenPhotosAsync(Photo("a.jpg"), Photo("b.jpg", 600, 400));
        panel.SelectRatio("16:9");
        panel.RotateRatio();                                          // 9:16: the largest one on each photo
        Assert.Equal("9:16", panel.SelectedRatio!.Key);
        Assert.Equal(new Rect(281, 0, 338, 600), panel.Selection);

        panel.CommitWidth("300");                                     // typed: 300 wide, and 533 tall to keep 9:16
        Assert.Equal(("300", "533"), (panel.WidthText, panel.HeightText));
        panel.RotateRatio();                                          // back to 16:9, and the typed size turns with it
        Assert.Equal("16:9", panel.SelectedRatio!.Key);
        Assert.Equal(("533", "300"), (panel.WidthText, panel.HeightText));
        Assert.Equal(new Rect(184, 150, 533, 300), panel.Selection);
    }

    [Theory]
    [InlineData(0.75, "3:4")]
    [InlineData(1.25, "5:4")]
    [InlineData(2.0 / 3, "2:3")]
    [InlineData(9.0 / 16, "9:16")]
    [InlineData(21.0 / 9, "7:3")]
    [InlineData(0.618, "0.62")]
    public void A_proportion_is_written_with_the_simplest_whole_numbers(double aspect, string text) =>
        Assert.Equal(text, CropPanelBase.RatioText(aspect));

    // ───────── Photos: Apply ─────────

    [AvaloniaFact]
    public async Task Crop_saves_the_box_as_a_new_photo_beside_the_original()
    {
        var file = Photo("foto.jpg");
        var original = await File.ReadAllBytesAsync(file);
        var panel = await OpenPhotosAsync(file);
        panel.Selection = new Rect(120, 80, 400, 300);
        Assert.Equal("400 × 300 px", panel.ResultText);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "foto recortado.jpg"), output);
        Assert.Equal((400u, 300u), SizeOf(output));
        Assert.Equal(original, await File.ReadAllBytesAsync(file));                       // the original is untouched
        foreach (var (x, y) in new[] { (0, 0), (399, 0), (0, 299), (399, 299), (200, 150), (37, 211) })
            AssertSimilar(PixelAt(file, 120 + x, 80 + y), PixelAt(output, x, y), 24, $"pixel {x},{y}");
    }

    [AvaloniaFact]
    public async Task A_png_is_cut_exactly_pixel_for_pixel()
    {
        var file = Photo("lienzo.png");
        var panel = await OpenPhotosAsync(file);
        panel.CommitWidth("200");
        panel.CommitHeight("100");                      // centred: 350, 250
        Assert.Equal(new Rect(350, 250, 200, 100), panel.Selection);
        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "lienzo recortado.png"), output);
        Assert.Equal((200u, 100u), SizeOf(output));
        foreach (var (x, y) in new[] { (0, 0), (199, 0), (0, 99), (199, 99), (100, 50) })
            Assert.Equal(PixelAt(file, 350 + x, 250 + y), PixelAt(output, x, y));
    }

    [AvaloniaFact]
    public async Task A_square_proportion_cuts_the_largest_square_from_the_middle()
    {
        var file = Photo("foto.jpg");
        var panel = await OpenPhotosAsync(file);
        panel.SelectRatio("1:1");

        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal("foto recortado.jpg", Path.GetFileName(output));
        Assert.Equal((600u, 600u), SizeOf(output));
        AssertSimilar(PixelAt(file, 150, 0), PixelAt(output, 0, 0), 24, "top left");
        AssertSimilar(PixelAt(file, 749, 599), PixelAt(output, 599, 599), 24, "bottom right");
        AssertSimilar(PixelAt(file, 450, 300), PixelAt(output, 300, 300), 24, "middle");
    }

    [AvaloniaFact]
    public async Task A_second_crop_does_not_overwrite_the_first()
    {
        var file = Photo("foto.jpg");
        var first = await OpenPhotosAsync(file);
        first.Selection = new Rect(0, 0, 100, 100);
        await first.RunAsync();
        var second = await OpenPhotosAsync(file);
        second.Selection = new Rect(0, 0, 50, 50);
        await second.RunAsync();

        Assert.Equal(Path.Combine(_root, "foto recortado.jpg"), Assert.Single(first.Outputs));
        var output = Assert.Single(second.Outputs);
        Assert.Equal(Path.Combine(_root, "foto recortado 2.jpg"), output);
        Assert.Equal((100u, 100u), SizeOf(first.Outputs[0]));
        Assert.Equal((50u, 50u), SizeOf(output));
    }

    [AvaloniaFact]
    public async Task A_png_keeps_its_transparency()
    {
        var file = Path.Combine(_root, "logo.png");
        using (var image = new MagickImage(MagickColors.Transparent, 300, 200))
        {
            new Drawables().FillColor(MagickColors.Red).Rectangle(150, 0, 299, 199).Draw(image);   // clear left half, red right half
            image.Write(file);
        }
        var panel = await OpenPhotosAsync(file);
        panel.Selection = new Rect(100, 50, 100, 100);
        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal("logo recortado.png", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.True(result.HasAlpha);
        Assert.Equal((100u, 100u), (result.Width, result.Height));
        Assert.Equal(0, PixelAt(output, 10, 10)[3]);                   // still clear where it was
        var red = PixelAt(output, 90, 10);
        Assert.Equal(255, red[3]);
        Assert.True(red[0] > 200 && red[1] < 40 && red[2] < 40);
        Assert.Equal((0, 0), (result.Page.X, result.Page.Y));            // the crop leaves no page offset behind
    }

    [AvaloniaFact]
    public async Task A_photo_keeps_its_colour_profile()
    {
        var file = Path.Combine(_root, "perfil.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 400, 300))
        {
            image.SetProfile(ColorProfiles.SRGB);
            image.Write(file);
        }
        var panel = await OpenPhotosAsync(file);
        panel.Selection = new Rect(10, 10, 200, 100);
        await panel.RunAsync();
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.NotNull(result.GetColorProfile());
    }

    [AvaloniaFact]
    public async Task Several_photos_are_each_cut_around_their_centre_to_the_chosen_proportion()
    {
        var a = Photo("a.jpg");                         // 900 × 600
        var b = Photo("b.png", 600, 900);
        var c = Photo("c.jpg", 1000, 1000);
        var panel = await OpenPhotosAsync(a, b, c);
        Assert.True(panel.IsMulti);
        Assert.False(panel.CanDragBox);
        panel.SelectRatio("16:9");
        Assert.Equal(new Rect(0, 47, 900, 506), panel.Selection);        // the preview shows it on the first one
        Assert.Contains("3", panel.HintText);

        var progress = new Recorder();
        var written = await panel.ApplyAsync(progress, CancellationToken.None);

        Assert.Equal(["a recortado.jpg", "b recortado.png", "c recortado.jpg"], written.Select(Path.GetFileName));
        Assert.Equal((900u, 506u), SizeOf(written[0]));
        Assert.Equal((600u, 338u), SizeOf(written[1]));
        Assert.Equal((1000u, 563u), SizeOf(written[2]));
        AssertSimilar(PixelAt(a, 0, 47), PixelAt(written[0], 0, 0), 24, "a, top left");
        Assert.Equal(PixelAt(b, 0, 281), PixelAt(written[1], 0, 0));        // the png is cut exactly: (900 - 338) / 2
        Assert.Equal(3, progress.Values.Count);                             // (i + 1) / n after each photo
        Assert.Equal(1 / 3.0, progress.Values[0], 6);
        Assert.Equal(2 / 3.0, progress.Values[1], 6);
        Assert.Equal(1.0, progress.Values[2], 6);
    }

    [AvaloniaFact]
    public async Task Several_photos_cut_an_exact_size_from_the_middle_and_only_as_far_as_each_one_reaches()
    {
        var a = Photo("a.jpg");                         // 900 × 600
        var small = Photo("pequena.jpg", 600, 400);
        var big = Photo("grande.jpg", 1500, 1000);
        var panel = await OpenPhotosAsync(a, small, big);

        panel.CommitWidth("700");
        panel.CommitHeight("500");
        Assert.Equal(("700", "500"), (panel.WidthText, panel.HeightText));
        Assert.Equal(new Rect(100, 50, 700, 500), panel.Selection);

        panel.CommitWidth("1200");                      // more than the first photo has, but not the big one
        Assert.Equal(("1200", "500"), (panel.WidthText, panel.HeightText));        // what was asked is what is shown
        Assert.Equal(new Rect(0, 50, 900, 500), panel.Selection);                  // the first photo's box is cut to it
        panel.CommitWidth("700");

        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal((700u, 500u), SizeOf(panel.Outputs[0]));
        Assert.Equal((600u, 400u), SizeOf(panel.Outputs[1]));                      // the small one is cut to its own limits
        Assert.Equal((700u, 500u), SizeOf(panel.Outputs[2]));
        AssertSimilar(PixelAt(big, 400, 250), PixelAt(panel.Outputs[2], 0, 0), 24, "big, top left");
    }

    [AvaloniaFact]
    public async Task Choosing_a_proportion_after_typing_a_size_goes_back_to_the_proportion()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg", 600, 400);
        var panel = await OpenPhotosAsync(a, b);
        panel.CommitWidth("300");
        panel.CommitHeight("300");
        panel.SelectRatio("1:1");
        Assert.Equal(new Rect(150, 0, 600, 600), panel.Selection);                 // the largest square, not 300 × 300
        await panel.RunAsync();
        Assert.Equal((600u, 600u), SizeOf(panel.Outputs[0]));
        Assert.Equal((400u, 400u), SizeOf(panel.Outputs[1]));
    }

    [AvaloniaFact]
    public async Task If_one_photo_fails_none_is_kept()
    {
        var good = Photo("buena.jpg");
        var bad = Path.Combine(_root, "mala.jpg");
        File.WriteAllText(bad, "not a photo");
        var last = Photo("ultima.jpg");
        var panel = await OpenPhotosAsync(good, bad, last);
        panel.SelectRatio("1:1");

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.False(panel.IsDone);
        Assert.Contains("mala.jpg", panel.Status);
        Assert.Empty(panel.Outputs);
        Assert.Empty(Directory.GetFiles(_root, "* recortado*"));                    // not even the first one that worked
        Assert.True(File.Exists(good) && File.Exists(bad) && File.Exists(last));
    }

    // ───────── Video ─────────

    [AvaloniaFact]
    public async Task Video_crop_saves_the_box_as_a_new_clip_with_the_same_length_and_sound()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = await OpenVideoAsync(file);
        Assert.Null(panel.LoadError);
        Assert.Equal((320, 240), (panel.PixelWidth, panel.PixelHeight));
        Assert.InRange(panel.Duration, 2.9, 3.2);
        Assert.NotNull(panel.Preview);
        Assert.Equal("Encuadrar", panel.Title);
        Assert.True(PanelRegistry.Has("cropVideo"));
        var original = await media.ProbeAsync(file);

        panel.Selection = new Rect(80, 60, 160, 120);
        Assert.Equal("160 × 120 px", panel.ResultText);
        var progress = new Recorder();
        var written = await panel.ApplyAsync(progress, CancellationToken.None);

        var output = Assert.Single(written);
        Assert.Equal(Path.Combine(_root, "clip encuadrado.mp4"), output);
        var probe = await media.ProbeAsync(output);
        Assert.Equal((160, 120), (probe.Width, probe.Height));
        Assert.InRange(probe.Duration, original.Duration - 0.2, original.Duration + 0.2);
        Assert.True(probe.HasAudio);
        Assert.Equal("aac", probe.AudioCodec);                                      // the sound is copied, not re-encoded
        Assert.Equal("h264", probe.VideoCodec);
        Assert.NotEmpty(progress.Values);

        var untouched = await media.ProbeAsync(file);                               // the original is untouched
        Assert.Equal((320, 240), (untouched.Width, untouched.Height));

        // The picture is the right part of the original: the cut frame matches the same region of the original frame.
        var whole = await MakeMediaAsync(Path.Combine(_root, "entero.png"), "-ss", "1", "-i", file, "-frames:v", "1");
        var cut = await MakeMediaAsync(Path.Combine(_root, "recorte.png"), "-ss", "1", "-i", output, "-frames:v", "1");
        using var expected = new MagickImage(whole!);
        using var wrong = new MagickImage(whole!);
        using var actual = new MagickImage(cut!);
        expected.Crop(new MagickGeometry(80, 60, 160, 120));
        expected.ResetPage();
        wrong.Crop(new MagickGeometry(0, 0, 160, 120));
        wrong.ResetPage();
        var right = expected.Compare(actual, ErrorMetric.MeanAbsolute);
        Assert.True(right < 0.03, $"the cut is not the box: error {right:0.000}");
        Assert.True(wrong.Compare(actual, ErrorMetric.MeanAbsolute) > right * 2, "the check cannot tell the box from another part");
    }

    [AvaloniaFact]
    public async Task Video_boxes_with_odd_numbers_are_rounded_down_to_even_ones()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(file);
        panel.Selection = new Rect(11, 7, 101, 75);
        Assert.Equal("100 × 74 px", panel.ResultText);
        Assert.Equal(new Rect(10, 6, 100, 74), panel.OutputBox);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var probe = await MediaTools.Locate()!.ProbeAsync(Assert.Single(panel.Outputs));
        Assert.Equal((100, 74), (probe.Width, probe.Height));

        panel = await OpenVideoAsync(file);                                      // a sliver is widened to the smallest video there is
        panel.Selection = new Rect(0, 0, 1, 1);
        Assert.True(panel.CanApply);
        Assert.Equal("2 × 2 px", panel.ResultText);
        panel.Selection = new Rect(0, 0, 3, 3);
        Assert.Equal("2 × 2 px", panel.ResultText);
        panel.Selection = new Rect(0, 0, 0, 0);
        Assert.False(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task Video_crop_keeps_the_container_of_the_original_and_copes_without_sound()
    {
        var mkv = await MakeVideoAsync("clip.mkv");
        var silent = await MakeVideoAsync("mudo.mp4", withAudio: false);
        if (mkv is null || silent is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;

        var panel = await OpenVideoAsync(mkv);
        panel.SelectRatio("1:1");                       // 240 × 240 from 320 × 240
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("clip encuadrado.mkv", Path.GetFileName(output));
        var probe = await media.ProbeAsync(output);
        Assert.Equal((240, 240), (probe.Width, probe.Height));
        Assert.True(probe.HasAudio);

        panel = await OpenVideoAsync(silent);
        panel.Selection = new Rect(0, 0, 200, 100);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        probe = await media.ProbeAsync(Assert.Single(panel.Outputs));
        Assert.Equal((200, 100), (probe.Width, probe.Height));
        Assert.False(probe.HasAudio);
    }

    [AvaloniaFact]
    public async Task A_phone_video_turned_by_its_rotation_tag_is_cropped_as_it_looks()
    {
        var plain = await MakeVideoAsync("recto.mp4");
        var turned = plain is null
            ? null
            : await MakeMediaAsync(Path.Combine(_root, "girado.mp4"), "-display_rotation", "90", "-i", plain, "-c", "copy");
        if (turned is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = await OpenVideoAsync(turned);
        Assert.Null(panel.LoadError);
        Assert.Equal((240, 320), (panel.PixelWidth, panel.PixelHeight));            // 320 × 240 stored, 240 × 320 shown

        panel.Selection = new Rect(40, 60, 160, 200);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var probe = await media.ProbeAsync(output);
        Assert.Equal((160, 200), (probe.Width, probe.Height));

        // What is cut is that part of the picture as it looks upright.
        var whole = await MakeMediaAsync(Path.Combine(_root, "girado.png"), "-ss", "1", "-i", turned, "-frames:v", "1");
        var cut = await MakeMediaAsync(Path.Combine(_root, "girado recorte.png"), "-ss", "1", "-i", output, "-frames:v", "1");
        using var expected = new MagickImage(whole!);
        using var actual = new MagickImage(cut!);
        Assert.Equal((240u, 320u), (expected.Width, expected.Height));
        expected.Crop(new MagickGeometry(40, 60, 160, 200));
        expected.ResetPage();
        var error = expected.Compare(actual, ErrorMetric.MeanAbsolute);
        Assert.True(error < 0.03, $"the cut is not the box: error {error:0.000}");
    }

    [AvaloniaFact]
    public async Task A_file_without_video_says_so_and_cannot_be_applied()
    {
        var file = await MakeMediaAsync(Path.Combine(_root, "solo audio.mp3"), "-f", "lavfi", "-i", "sine=frequency=440:duration=1");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(file);
        Assert.Equal("Este archivo no tiene vídeo.", panel.LoadError);
        Assert.False(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task The_frame_slider_brings_another_frame_of_the_video()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(file);
        var first = panel.Preview;
        Assert.NotNull(first);
        Assert.InRange(panel.FrameTime, 0.2, 0.4);                 // a tenth of the way in

        // A burst of moves: only the last one is fetched, after a short rest.
        panel.FrameTime = 0.5;
        panel.FrameTime = 1.0;
        panel.FrameTime = 2.0;
        await WaitAsync(() => !ReferenceEquals(panel.Preview, first));
        Assert.NotNull(panel.Preview);
        Assert.Equal((320, 240), (panel.PixelWidth, panel.PixelHeight));           // the box still counts in the video's pixels
        var second = panel.Preview;
        panel.FrameTime = panel.Duration;                           // the very end has no frame of its own: the last one is shown
        await WaitAsync(() => !ReferenceEquals(panel.Preview, second));
        Assert.Null(panel.LoadError);
    }

    // ───────── The wiring between the surface and the panel ─────────

    [AvaloniaFact]
    public async Task Dragging_on_the_preview_updates_the_panel_and_choosing_a_pill_updates_the_preview()
    {
        var panel = await OpenPhotosAsync(Photo("foto.jpg"));
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            var surface = window.GetVisualDescendants().OfType<CropSurface>().Single();
            Assert.True(surface.IsEditable);
            Assert.Equal(panel.Selection, surface.Selection);
            Assert.Equal((900.0, 600.0), (surface.PixelWidth, surface.PixelHeight));

            // Through real pointer events: take the bottom right corner and drag it up and left.
            var corner = surface.TranslatePoint(surface.SelectionRect.BottomRight, window)!.Value;
            window.MouseDown(corner, MouseButton.Left);
            window.MouseMove(corner - new Vector(100, 60));
            window.MouseUp(corner - new Vector(100, 60), MouseButton.Left);
            Pump();
            Assert.True(panel.Selection.Width < 900 && panel.Selection.Height < 600, panel.Selection.ToString());
            Assert.Equal(panel.Selection, surface.Selection);
            Assert.Equal(panel.WidthText, ((int)panel.Selection.Width).ToString());
            Assert.Equal((0, 0), (panel.Selection.X, panel.Selection.Y));

            // A pill: press the real button.
            var pill = window.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Content, "1:1"));
            Assert.False(pill.IsChecked);
            var at = pill.TranslatePoint(new Point(pill.Bounds.Width / 2, pill.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.Equal("1:1", panel.SelectedRatio!.Key);
            Assert.True(pill.IsChecked);
            Assert.Equal(1.0, surface.AspectRatio);
            Assert.Equal(panel.Selection, surface.Selection);
            Assert.Equal(panel.Selection.Width, panel.Selection.Height);

            // Pressing the pill that is already on leaves it on.
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.True(pill.IsChecked);
            Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true);

            // The turn button.
            var rotate = window.GetVisualDescendants().OfType<Button>().Single(button => button is not ToggleButton && button.Classes.Contains("pchip"));
            var turn = rotate.TranslatePoint(new Point(rotate.Bounds.Width / 2, rotate.Bounds.Height / 2), window)!.Value;
            panel.SelectRatio("16:9");
            window.MouseDown(turn, MouseButton.Left);
            window.MouseUp(turn, MouseButton.Left);
            Pump();
            Assert.Equal("9:16", panel.SelectedRatio!.Key);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task With_several_photos_the_preview_cannot_be_dragged()
    {
        var panel = await OpenPhotosAsync(Photo("a.jpg"), Photo("b.jpg", 600, 400));
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            var surface = window.GetVisualDescendants().OfType<CropSurface>().Single();
            Assert.False(surface.IsEditable);
            panel.SelectRatio("1:1");
            Pump();
            Assert.Equal(panel.Selection, surface.Selection);
            Assert.False(surface.BeginDrag(surface.SelectionRect.Center));
        }
        finally
        {
            window.Close();
        }
    }

    // ───────── Renders ─────────

    [AvaloniaFact]
    public async Task The_photo_panel_renders_with_its_box_and_options()
    {
        var panel = await OpenPhotosAsync(Photo("paisaje.jpg", 1200, 800));
        panel.SelectRatio("1:1");
        panel.Selection = new Rect(330, 0, 800, 800);                 // moved off the middle, as dragged

        Render(panel, "cropimage");
    }

    [AvaloniaFact]
    public async Task The_photo_panel_renders_with_a_turned_proportion_in_english()
    {
        var file = Photo("portrait.jpg", 800, 1200);
        var panel = new CropImagePanel(ContextIn("en", file));
        await WaitAsync(() => !panel.IsLoading);
        panel.SelectRatio("4:3");
        panel.RotateRatio();                                          // 3:4, which only the extra pill has

        Render(panel, "cropimage-en");
        Assert.Equal("Crop", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_photo_panel_renders_with_several_photos()
    {
        var panel = await OpenPhotosAsync(Photo("uno.jpg", 1200, 800), Photo("dos.jpg", 600, 900), Photo("tres.jpg", 1000, 1000));
        panel.SelectRatio("16:9");

        Render(panel, "cropimage-many");
    }

    [AvaloniaFact]
    public async Task The_photo_panel_renders_the_reason_when_the_photo_cannot_be_opened()
    {
        var bad = Path.Combine(_root, "rota.jpg");
        File.WriteAllText(bad, "this is not a photo");
        var panel = new CropImagePanel(Context(bad));
        await WaitAsync(() => !panel.IsLoading);

        Render(panel, "cropimage-error");
    }

    [AvaloniaTheory]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_photo_panel_fits_in_the_other_languages(string language)
    {
        var file = Photo("foto-" + language + ".jpg", 1000, 700);
        var panel = new CropImagePanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        panel.SelectRatio("3:2");
        panel.RotateRatio();

        Render(panel, "cropimage-" + language);
    }

    [AvaloniaFact]
    public async Task The_video_panel_renders_with_a_frame_and_its_box()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(file);
        Assert.Null(panel.LoadError);
        panel.SelectRatio("1:1");
        panel.Selection = new Rect(60, 0, 240, 240);

        Render(panel, "cropvideo");
    }

    [AvaloniaFact]
    public async Task The_video_panel_renders_in_english_with_a_free_box()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new CropVideoPanel(ContextIn("en", file));
        await WaitAsync(() => !panel.IsLoading);
        panel.Selection = new Rect(41, 31, 201, 151);

        Render(panel, "cropvideo-en");
    }
}
