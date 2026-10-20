// Mandarina: the ANNOTATE panel (annotateImage). The model and its history are checked on their own; the
// panel renders with a real photo and a set of marks to mandarina-panel-annotate*.png for review; Apply runs the real
// drawing code and Magick.NET reads the PNG back; the canvas is driven with the headless mouse and keyboard.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaAnnotateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-annotate").FullName;

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

    private static Annotation Mark(AnnotationKind kind, Color color, double thickness, params Point[] points) =>
        new() { Kind = kind, Color = color, Thickness = thickness, Points = [.. points] };

    private static Annotation Text(string text, Point at, Color color, double size = 48) =>
        new() { Kind = AnnotationKind.Text, Text = text, Color = color, FontSize = size, Points = [at] };

    private static Annotation Callout(Point at, Color color, double size = 32) =>
        new() { Kind = AnnotationKind.Callout, Color = color, FontSize = size, Points = [at] };

    /// <summary>The marks of the render test, placed for a 1200 × 800 photo: every kind, three callouts.</summary>
    private static Annotation[] SampleMarks() =>
    [
        Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 6, new Point(160, 690), new Point(470, 400)),
        Mark(AnnotationKind.Rectangle, AnnotationPalette.Blue, 5, new Point(640, 110), new Point(1010, 330)) with { Filled = true },
        Mark(AnnotationKind.Ellipse, AnnotationPalette.Green, 5, new Point(700, 450), new Point(1060, 660)),
        Mark(AnnotationKind.Line, AnnotationPalette.White, 4, new Point(110, 750), new Point(640, 770)),
        Mark(AnnotationKind.Pen, AnnotationPalette.Violet, 6, [.. Enumerable.Range(0, 40).Select(i => new Point(120 + i * 8, 560 + Math.Sin(i / 3.0) * 36))]),
        Text("Hola", new Point(110, 80), AnnotationPalette.Black, 96),
        Callout(new Point(300, 280), AnnotationPalette.Orange),
        Callout(new Point(560, 300), AnnotationPalette.Yellow),
        Callout(new Point(830, 390), AnnotationPalette.Red),
    ];

    private static byte[] Rgb(MagickImage image, int x, int y)
    {
        var pixel = image.GetPixels().GetPixel(x, y).ToArray()!;
        return [pixel[0], pixel[1], pixel[2]];
    }

    private static async Task<AnnotateImagePanel> OpenAsync(string photo, string language = "es")
    {
        var panel = new AnnotateImagePanel(ContextIn(language, photo));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    // ───────── The model and its history ─────────

    [Fact]
    public void The_history_adds_moves_deletes_undoes_and_redoes()
    {
        var document = new AnnotationDocument();
        Assert.False(document.CanUndo);
        var arrow = document.Add(Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 4, new Point(10, 10), new Point(100, 50)));
        var box = document.Add(Mark(AnnotationKind.Rectangle, AnnotationPalette.Blue, 4, new Point(20, 20), new Point(80, 90)));
        Assert.Equal([arrow.Id, box.Id], document.Items.Select(item => item.Id));
        Assert.NotEqual(arrow.Id, box.Id);

        // Move the rectangle by (5, 7): one step, and the old position is one undo away.
        document.Replace(box.Translated(new Vector(5, 7)));
        Assert.Equal(new Point(25, 27), document.Find(box.Id)!.Points[0]);
        Assert.True(document.Undo());
        Assert.Equal(new Point(20, 20), document.Find(box.Id)!.Points[0]);
        Assert.True(document.Redo());
        Assert.Equal(new Point(25, 27), document.Find(box.Id)!.Points[0]);

        // Delete one: gone, back with undo, gone again with redo.
        document.Remove(arrow.Id);
        Assert.Null(document.Find(arrow.Id));
        document.Undo();
        Assert.NotNull(document.Find(arrow.Id));
        Assert.Equal([arrow.Id, box.Id], document.Items.Select(item => item.Id));   // back in its place
        document.Redo();
        Assert.Null(document.Find(arrow.Id));

        // Clear everything, then take it all back, step by step.
        document.Clear();
        Assert.Equal(0, document.Count);
        document.Undo();
        Assert.Equal(1, document.Count);
        while (document.Undo())
        {
        }
        Assert.Equal(0, document.Count);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);

        // A new edit drops what could have been redone.
        document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Black, 3, new Point(0, 0), new Point(9, 9)));
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Edits_of_the_same_thing_in_a_row_are_one_history_step()
    {
        var document = new AnnotationDocument();
        var mark = document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Red, 4, new Point(0, 0), new Point(50, 50)));
        for (var thickness = 5; thickness <= 12; thickness++)
            document.Replace(document.Find(mark.Id)! with { Thickness = thickness }, $"thickness:{mark.Id}");
        Assert.Equal(12, document.Find(mark.Id)!.Thickness);

        document.Undo();   // the whole slider drag at once
        Assert.Equal(4, document.Find(mark.Id)!.Thickness);

        // Another kind of edit is its own step.
        document.Replace(mark with { Color = AnnotationPalette.Blue }, $"color:{mark.Id}");
        document.Replace(document.Find(mark.Id)! with { Thickness = 9 }, $"thickness:{mark.Id}");
        document.Undo();
        Assert.Equal(AnnotationPalette.Blue, document.Find(mark.Id)!.Color);
        Assert.Equal(4, document.Find(mark.Id)!.Thickness);
    }

    [Fact]
    public void Callouts_are_numbered_by_order_and_the_rest_are_renumbered_when_one_goes()
    {
        var document = new AnnotationDocument();
        var first = document.Add(Callout(new Point(10, 10), AnnotationPalette.Red));
        var arrow = document.Add(Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 4, new Point(0, 0), new Point(30, 30)));
        var second = document.Add(Callout(new Point(50, 10), AnnotationPalette.Red));
        var third = document.Add(Callout(new Point(90, 10), AnnotationPalette.Red));
        Assert.Equal([1, 0, 2, 3], new[] { first, arrow, second, third }.Select(item => document.NumberOf(item.Id)));
        Assert.Equal(3, document.CalloutCount);

        document.Remove(second.Id);
        Assert.Equal(1, document.NumberOf(first.Id));
        Assert.Equal(2, document.NumberOf(third.Id));   // was 3
        Assert.Equal(2, document.CalloutCount);          // the next one drawn is the 3rd

        document.Undo();
        Assert.Equal(3, document.NumberOf(third.Id));
    }

    [Fact]
    public void Handles_resize_a_shape_from_the_opposite_corner_even_across_it()
    {
        var box = Mark(AnnotationKind.Rectangle, AnnotationPalette.Red, 4, new Point(100, 100), new Point(300, 200));
        Assert.Equal([new Point(100, 100), new Point(300, 100), new Point(300, 200), new Point(100, 200)], box.HandlePoints().ToArray());

        var dragged = box.WithHandle(0, new Point(60, 40));   // top-left pulled out
        Assert.Equal(new Rect(60, 40, 240, 160), dragged.Corners);

        var flipped = box.WithHandle(0, new Point(400, 260));  // dragged across the opposite corner
        Assert.Equal(new Rect(300, 200, 100, 60), flipped.Corners);

        var line = Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 4, new Point(0, 0), new Point(10, 10));
        Assert.Equal(new Point(50, 70), line.WithHandle(1, new Point(50, 70)).Points[1]);
        Assert.Empty(Callout(new Point(1, 1), AnnotationPalette.Red).HandlePoints());
    }

    [Theory]
    [InlineData(0xFF, 0x3B, 0x30, true)]    // red: white digit
    [InlineData(0xFF, 0x52, 0x00, true)]    // orange: white digit
    [InlineData(0x0A, 0x84, 0xFF, true)]    // blue
    [InlineData(0xFF, 0xCC, 0x00, false)]   // yellow: black digit
    [InlineData(0x34, 0xC7, 0x59, false)]   // green
    [InlineData(0xFF, 0xFF, 0xFF, false)]   // white
    [InlineData(0x1C, 0x1C, 0x1E, true)]    // black
    public void A_callout_digit_is_white_or_black_to_stand_out_from_its_disc(byte r, byte g, byte b, bool white)
    {
        var ink = AnnotationPainter.OnColor(Color.FromRgb(r, g, b));
        Assert.Equal(white ? Colors.White : Colors.Black, ink);
    }

    // ───────── The panel ─────────

    [AvaloniaFact]
    public async Task The_annotate_panel_renders_with_marks_on_a_real_photo()
    {
        var photo = MakePhoto(Path.Combine(_root, "playa.jpg"), 1200, 800);
        var panel = await OpenAsync(photo);
        Assert.Equal(new Size(1200, 800), panel.ImageSize);
        Assert.False(panel.CanApply);

        var added = SampleMarks().Select(mark => panel.Document.Add(mark)).ToArray();
        Assert.True(panel.CanApply);
        panel.Tool = AnnotationTool.Select;
        panel.SelectedId = added[1].Id;   // the rectangle, with its handles

        Render(panel, "annotate");
        Assert.Equal("Anotar", panel.Title);

        // The same marks saved: the picture to look at next to the window's.
        var saved = await OpenAsync(photo);
        foreach (var mark in SampleMarks())
            saved.Document.Add(mark);
        await saved.RunAsync();
        Assert.True(saved.IsDone, saved.Status);
        File.Copy(Assert.Single(saved.Outputs), Path.Combine(TestServices.ScreenshotDirectory, "mandarina-annotate-sample.png"), overwrite: true);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_annotate_panel_fits_its_options_in_every_language(string language)
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.png"), 900, 600);
        var panel = await OpenAsync(photo, language);
        foreach (var mark in SampleMarks().Take(3))
            panel.Document.Add(mark with { Points = [.. mark.Points.Select(p => new Point(p.X * 0.75, p.Y * 0.75))] });
        panel.Tool = AnnotationTool.Text;
        Render(panel, $"annotate-{language}");
    }

    [AvaloniaFact]
    public async Task A_photo_that_cannot_be_read_says_so_and_cannot_be_applied()
    {
        var broken = Path.Combine(_root, "roto.jpg");
        File.WriteAllText(broken, "this is not a picture");
        var panel = new AnnotateImagePanel(Context(broken));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(string.IsNullOrWhiteSpace(panel.LoadError));
        panel.Document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Red, 4, new Point(0, 0), new Point(5, 5)));
        Assert.False(panel.CanApply);
        Render(panel, "annotate-broken");
    }

    // ───────── Apply ─────────

    [AvaloniaFact]
    public async Task Annotate_saves_the_marks_on_a_same_size_png_and_leaves_the_original_alone()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.png"));   // 900 × 600
        var originalBytes = File.ReadAllBytes(photo);
        var panel = await OpenAsync(photo);
        Assert.False(panel.CanApply);   // nothing drawn yet

        var red = AnnotationPalette.Red;
        panel.Document.Add(Mark(AnnotationKind.Arrow, red, 8, new Point(60, 520), new Point(300, 330)));
        panel.Document.Add(Mark(AnnotationKind.Rectangle, red, 8, new Point(400, 100), new Point(760, 300)));
        panel.Document.Add(Text("Hola", new Point(80, 60), AnnotationPalette.Black, 90));
        panel.Document.Add(Callout(new Point(600, 450), AnnotationPalette.Blue, 40));
        panel.Document.Add(Callout(new Point(720, 450), AnnotationPalette.Blue, 40));
        Assert.True(panel.CanApply);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "foto anotado.png"), output);
        Assert.Equal(originalBytes, File.ReadAllBytes(photo));   // the original is untouched
        File.Copy(output, Path.Combine(TestServices.ScreenshotDirectory, "mandarina-annotate-result.png"), overwrite: true);

        using var before = new MagickImage(photo);
        using var after = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, after.Format);
        Assert.Equal((900u, 600u), (after.Width, after.Height));

        // Far from every mark the pixels are the photo's own, bit for bit.
        foreach (var (x, y) in new[] { (880, 580), (20, 300), (450, 560), (840, 40), (330, 450) })
            Assert.Equal(Rgb(before, x, y), Rgb(after, x, y));

        // On the rectangle's outline (its top edge, 400..760 at y = 100) the pixel is the mark's red.
        var edge = Rgb(after, 580, 100);
        Assert.InRange(edge[0], red.R - 3, red.R + 3);
        Assert.InRange(edge[1], red.G - 3, red.G + 3);
        Assert.InRange(edge[2], red.B - 3, red.B + 3);
        Assert.NotEqual(Rgb(before, 580, 100), edge);
        // Inside the rectangle (not filled) the photo shows through.
        Assert.Equal(Rgb(before, 580, 200), Rgb(after, 580, 200));

        // The callout's disc (centre of the first one) is blue, the arrow's shaft red.
        Assert.Equal(new byte[] { 0x0A, 0x84, 0xFF }, Rgb(after, 600, 460));   // inside the disc, below the digit
        var shaft = Rgb(after, 180, 425);   // the middle of the arrow from (60,520) to (300,330)
        Assert.InRange(shaft[0], red.R - 3, red.R + 3);

        // Text and digits left their marks: something changed in each of their boxes.
        Assert.True(Changed(before, after, new Rect(80, 60, 200, 100)), "text");
        Assert.True(Changed(before, after, new Rect(580, 430, 40, 40)), "first callout");
        Assert.True(Changed(before, after, new Rect(700, 430, 40, 40)), "second callout");

        // A second run does not overwrite: " 2".
        var again = await OpenAsync(photo);
        again.Document.Add(Mark(AnnotationKind.Line, red, 4, new Point(0, 0), new Point(100, 100)));
        await again.RunAsync();
        Assert.Equal(Path.Combine(_root, "foto anotado 2.png"), Assert.Single(again.Outputs));
        Assert.Equal(originalBytes, File.ReadAllBytes(photo));
    }

    private static bool Changed(MagickImage before, MagickImage after, Rect box)
    {
        for (var y = (int)box.Top; y < (int)box.Bottom; y++)
            for (var x = (int)box.Left; x < (int)box.Right; x++)
                if (!Rgb(before, x, y).SequenceEqual(Rgb(after, x, y)))
                    return true;
        return false;
    }

    [AvaloniaFact]
    public async Task A_filled_shape_tints_the_photo_inside_and_keeps_its_outline_solid()
    {
        var photo = MakePhoto(Path.Combine(_root, "plano.png"), 800, 500);
        var panel = await OpenAsync(photo);
        panel.Document.Add(Mark(AnnotationKind.Ellipse, AnnotationPalette.Blue, 8, new Point(100, 100), new Point(500, 400)) with { Filled = true });
        await panel.RunAsync();

        using var before = new MagickImage(photo);
        using var after = new MagickImage(Assert.Single(panel.Outputs));
        var inside = (before: Rgb(before, 300, 250), after: Rgb(after, 300, 250));
        Assert.NotEqual(inside.before, inside.after);
        // Translucent: blue is mixed with what was there, it does not replace it.
        Assert.NotEqual(new byte[] { 0x0A, 0x84, 0xFF }, inside.after);
        Assert.True(inside.after[2] > inside.before[2] || inside.after[2] == 255);
        var rim = Rgb(after, 300, 100);   // the top of the outline
        Assert.InRange(rim[2], 0xF0, 0xFF);
        Assert.InRange(rim[0], 0, 0x1A);
        Assert.Equal(Rgb(before, 700, 450), Rgb(after, 700, 450));
    }

    [AvaloniaFact]
    public async Task Any_photo_format_comes_out_as_a_png_with_the_orientation_applied()
    {
        // A 600 × 300 JPEG whose EXIF orientation says "turn it": it is a 300 × 600 picture to anyone who opens it.
        var path = Path.Combine(_root, "movil.jpg");
        using (var image = new MagickImage(new MagickColor("#336699"), 600, 300))
            image.Write(path);
        // An EXIF segment with just Orientation = 6 (rotate 90° clockwise), right after the JPEG's start marker.
        byte[] exif = [0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
                       (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0,
                       1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0,
                       0, 0, 0, 0];
        var jpeg = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. jpeg.Take(2), .. exif, .. jpeg.Skip(2)]);
        var panel = await OpenAsync(path);
        Assert.Equal(new Size(300, 600), panel.ImageSize);
        panel.Document.Add(Mark(AnnotationKind.Arrow, AnnotationPalette.Orange, 6, new Point(40, 500), new Point(250, 100)));
        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "movil anotado.png"), output);
        using var result = new MagickImage(output);
        Assert.Equal((300u, 600u), (result.Width, result.Height));
        using var upright = PanelImages.Read(path);
        Assert.Equal(Rgb(upright, 280, 580), Rgb(result, 280, 580));   // the same picture as everywhere else: turned the right way
    }

    [AvaloniaFact]
    public async Task A_photo_with_transparency_keeps_it_around_the_marks()
    {
        var path = Path.Combine(_root, "logo.png");
        using (var image = new MagickImage(MagickColors.Transparent, 400, 300))
        {
            using var patch = new MagickImage(MagickColors.SeaGreen, 200, 140);
            image.Composite(patch, 100, 80, CompositeOperator.Over);
            image.Write(path);
        }
        var panel = await OpenAsync(path);
        panel.Document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Red, 6, new Point(120, 150), new Point(280, 150)));
        await panel.RunAsync();

        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.True(result.HasAlpha);
        Assert.Equal(0, result.GetPixels().GetPixel(20, 20).ToArray()![3]);       // still see-through away from the marks
        Assert.Equal(255, result.GetPixels().GetPixel(150, 100).ToArray()![3]);   // and solid on the logo
    }

    [AvaloniaFact]
    public async Task A_photo_larger_than_the_limit_is_saved_scaled_down_with_the_marks_in_place()
    {
        var path = Path.Combine(_root, "panoramica.png");
        using (var image = new MagickImage(new MagickColor("#888888"), 8600, 300))
            image.Write(path);
        var panel = await OpenAsync(path);
        Assert.Equal(new Size(8600, 300), panel.ImageSize);
        // A red bar over the right quarter of the photo, from x = 6450 to 8600.
        panel.Document.Add(Mark(AnnotationKind.Rectangle, AnnotationPalette.Red, 6, new Point(6450, 20), new Point(8580, 280)) with { Filled = true });
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal(8000u, result.Width);
        Assert.Equal(279u, result.Height);
        var scale = 8000.0 / 8600;
        Assert.Equal(new byte[] { 0x88, 0x88, 0x88 }, Rgb(result, (int)(6000 * scale), 140));   // left of the box
        var inside = Rgb(result, (int)(7500 * scale), 140);                                                                    // inside it: tinted
        Assert.True(inside[0] > inside[2] + 20, $"inside the box: {string.Join(",", inside)}");
    }

    // ───────── The canvas ─────────

    private sealed record Rig(ToolPanelWindow Window, AnnotateImagePanel Panel, AnnotationCanvas Canvas)
    {
        /// <summary>A point of the photo (its pixels) as a point of the window.</summary>
        public Point At(double x, double y) => Canvas.TranslatePoint(Canvas.ToView(new Point(x, y)), Window)!.Value;

        public void Drag(Point from, Point to, int steps = 6)
        {
            Window.MouseDown(At(from.X, from.Y), MouseButton.Left);
            for (var i = 1; i <= steps; i++)
                Window.MouseMove(At(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            Window.MouseUp(At(to.X, to.Y), MouseButton.Left);
        }

        public void Click(double x, double y)
        {
            Window.MouseDown(At(x, y), MouseButton.Left);
            Window.MouseUp(At(x, y), MouseButton.Left);
        }

        /// <summary>Saves what the window shows as screenshots\mandarina-panel-{name}.png.</summary>
        public void Capture(string name)
        {
            Pump();
            using var frame = Window.CaptureRenderedFrame(); // disposed, not left to the finalizer
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-panel-{name}.png"));
            frame.Save(stream, new PngBitmapEncoderOptions());
        }
    }

    private async Task<Rig> ShowAsync(string name = "lienzo.png")
    {
        var photo = MakePhoto(Path.Combine(_root, name), 900, 600);
        var panel = await OpenAsync(photo);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var canvas = window.GetVisualDescendants().OfType<AnnotationCanvas>().Single();
        Assert.True(canvas.Bounds.Width > 100 && canvas.Bounds.Height > 100, $"canvas is {canvas.Bounds.Size}");
        return new Rig(window, panel, canvas);
    }

    [AvaloniaFact]
    public async Task Dragging_with_the_rectangle_tool_draws_one_and_Select_moves_it_and_Delete_removes_it()
    {
        var rig = await ShowAsync();
        var (panel, document) = (rig.Panel, rig.Panel.Document);

        // Rectangle tool: a diagonal drag makes a rectangle in the photo's own pixels.
        panel.Tool = AnnotationTool.Rectangle;
        rig.Drag(new Point(200, 150), new Point(500, 350));
        var box = Assert.Single(document.Items);
        Assert.Equal(AnnotationKind.Rectangle, box.Kind);
        Assert.Equal(200, box.Points[0].X, 1.5);
        Assert.Equal(150, box.Points[0].Y, 1.5);
        Assert.Equal(500, box.Points[1].X, 1.5);
        Assert.Equal(350, box.Points[1].Y, 1.5);

        // Select tool: clicking the outline picks it (its handles show), dragging it moves it by what the pointer moved.
        panel.Tool = AnnotationTool.Select;
        rig.Click(350, 150);
        Assert.Equal(box.Id, panel.SelectedId);
        rig.Drag(new Point(350, 150), new Point(400, 250));
        var moved = document.Find(box.Id)!;
        Assert.Equal(250, moved.Points[0].X, 1.5);
        Assert.Equal(250, moved.Points[0].Y, 1.5);
        Assert.Equal(550, moved.Points[1].X, 1.5);
        Assert.Equal(450, moved.Points[1].Y, 1.5);
        Assert.Equal(box.Id, panel.SelectedId);

        // A handle resizes: the bottom-right corner is now at (550, 450).
        rig.Drag(new Point(550, 450), new Point(650, 500));
        var resized = document.Find(box.Id)!.Corners;
        Assert.Equal(250, resized.Left, 1.5);
        Assert.Equal(250, resized.Top, 1.5);
        Assert.Equal(650, resized.Right, 1.5);
        Assert.Equal(500, resized.Bottom, 1.5);

        // Ctrl+Z steps back through resize and move; Delete removes the mark; Ctrl+Z brings it back.
        rig.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal(550, document.Find(box.Id)!.Corners.Right, 1.5);
        rig.Window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.Equal(650, document.Find(box.Id)!.Corners.Right, 1.5);
        rig.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Empty(document.Items);
        Assert.Equal(0, panel.SelectedId);
        Assert.False(panel.CanApply);
        rig.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Single(document.Items);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task The_pointer_lands_on_the_right_pixel_of_the_photo_at_any_display_scaling(double scaling)
    {
        var rig = await ShowAsync();
        rig.Window.SetRenderScaling(scaling);
        Pump();
        rig.Panel.Tool = AnnotationTool.Rectangle;
        rig.Drag(new Point(120, 90), new Point(620, 410));
        var box = Assert.Single(rig.Panel.Document.Items);
        Assert.Equal(120, box.Points[0].X, 1.5);
        Assert.Equal(90, box.Points[0].Y, 1.5);
        Assert.Equal(620, box.Points[1].X, 1.5);
        Assert.Equal(410, box.Points[1].Y, 1.5);
        rig.Capture(FormattableString.Invariant($"annotate-scaling-{scaling}"));   // the picture to look at: sharp, marks on the photo
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Each_drawing_tool_makes_its_mark_and_a_stray_click_makes_none()
    {
        var rig = await ShowAsync();
        var document = rig.Panel.Document;

        rig.Panel.Tool = AnnotationTool.Arrow;
        rig.Drag(new Point(100, 100), new Point(300, 200));
        rig.Click(500, 500);   // a click is not an arrow
        rig.Panel.Tool = AnnotationTool.Line;
        rig.Drag(new Point(100, 300), new Point(400, 300));
        rig.Panel.Tool = AnnotationTool.Ellipse;
        rig.Drag(new Point(450, 100), new Point(700, 250));
        rig.Panel.Tool = AnnotationTool.Pen;
        rig.Drag(new Point(100, 450), new Point(400, 520), steps: 12);

        Assert.Equal([AnnotationKind.Arrow, AnnotationKind.Line, AnnotationKind.Ellipse, AnnotationKind.Pen], document.Items.Select(item => item.Kind));
        Assert.True(document.Items[3].Points.Length >= 10, "the pen follows the pointer");

        // Callouts: each click drops the next number; they follow the order they were put down.
        rig.Panel.Tool = AnnotationTool.Callout;
        rig.Click(600, 400);
        rig.Click(700, 450);
        rig.Click(800, 500);
        var callouts = document.Items.Where(item => item.Kind == AnnotationKind.Callout).ToArray();
        Assert.Equal([1, 2, 3], callouts.Select(item => document.NumberOf(item.Id)));
        Assert.Equal(700, callouts[1].Points[0].X, 1.5);

        // The new marks take the style of the card.
        rig.Panel.Color = AnnotationPalette.Green;
        rig.Panel.Thickness = 11;
        rig.Panel.Tool = AnnotationTool.Rectangle;
        rig.Panel.Filled = true;
        rig.Drag(new Point(50, 50), new Point(150, 120));
        var styled = document.Items[^1];
        Assert.Equal((AnnotationPalette.Green, 11d, true), (styled.Color, styled.Thickness, styled.Filled));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Changing_the_style_with_a_mark_selected_changes_that_mark_and_undoes_in_one_step()
    {
        var rig = await ShowAsync();
        var (panel, document) = (rig.Panel, rig.Panel.Document);
        var arrow = document.Add(Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 4, new Point(100, 100), new Point(400, 300)));
        var text = document.Add(Text("Hola", new Point(120, 400), AnnotationPalette.Black, 40));

        panel.Tool = AnnotationTool.Select;
        rig.Click(250, 200);   // on the arrow
        Assert.Equal(arrow.Id, panel.SelectedId);
        Assert.Equal(AnnotationPalette.Red, panel.Color);   // the card shows the mark's colour
        Assert.Equal(4, panel.Thickness);

        panel.Color = AnnotationPalette.Blue;
        for (var thickness = 5; thickness <= 14; thickness++)
            panel.Thickness = thickness;
        var changed = document.Find(arrow.Id)!;
        Assert.Equal(AnnotationPalette.Blue, changed.Color);
        Assert.Equal(14, changed.Thickness);
        Assert.Equal(AnnotationPalette.Black, document.Find(text.Id)!.Color);   // nothing else moved

        panel.Undo();   // the slider drag
        Assert.Equal(AnnotationPalette.Blue, document.Find(arrow.Id)!.Color);
        Assert.Equal(4, document.Find(arrow.Id)!.Thickness);
        Assert.Equal(4, panel.Thickness);               // and the card follows
        panel.Undo();   // the colour
        Assert.Equal(AnnotationPalette.Red, document.Find(arrow.Id)!.Color);
        Assert.Equal(AnnotationPalette.Red, panel.Color);

        // A text mark has no outline: the thickness slider is dimmed for it and leaves it alone.
        rig.Click(130, 410);
        Assert.Equal(text.Id, panel.SelectedId);
        Assert.False(panel.ThicknessEnabled);
        Assert.True(panel.FontSizeEnabled);
        panel.FontSize = 70;
        Assert.Equal(70, document.Find(text.Id)!.FontSize);
        Assert.Equal(4, document.Find(arrow.Id)!.Thickness);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task The_text_tool_types_in_place_and_empty_or_cancelled_text_is_dropped()
    {
        var rig = await ShowAsync();
        var (panel, canvas, document) = (rig.Panel, rig.Canvas, rig.Panel.Document);
        panel.Tool = AnnotationTool.Text;
        panel.Color = AnnotationPalette.Orange;
        panel.FontSize = 50;

        // A click opens the box; Enter (here: committing) puts the text on the photo.
        rig.Click(200, 220);
        Assert.True(canvas.IsEditing);
        canvas.EditorText = "Mira aquí";
        Pump();
        canvas.CommitEditor();
        var text = Assert.Single(document.Items);
        Assert.Equal(("Mira aquí", AnnotationPalette.Orange, 50d), (text.Text, text.Color, text.FontSize));
        Assert.Equal(200, text.Points[0].X, 1.5);
        Assert.Equal(220, text.Points[0].Y, 1.5);

        // An empty box leaves nothing behind.
        rig.Click(300, 400);
        Assert.True(canvas.IsEditing);
        canvas.EditorText = "   ";
        canvas.CommitEditor();
        Assert.Single(document.Items);

        // Clicking the text again edits it; emptying it deletes it; Escape keeps it as it was.
        rig.Click(210, 230);
        Assert.True(canvas.IsEditing);
        Assert.Equal("Mira aquí", canvas.EditorText);
        canvas.EditorText = "Otra cosa";
        canvas.CommitEditor();
        Assert.Equal("Otra cosa", Assert.Single(document.Items).Text);

        rig.Click(210, 230);
        canvas.EditorText = "descartado";
        canvas.CancelEditor();
        Assert.Equal("Otra cosa", Assert.Single(document.Items).Text);

        rig.Click(210, 230);
        canvas.EditorText = "";
        canvas.CommitEditor();
        Assert.Empty(document.Items);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task The_text_box_sits_where_the_text_will_be_and_the_selection_shows_its_handles()
    {
        var rig = await ShowAsync();
        var (panel, document) = (rig.Panel, rig.Panel.Document);
        document.Add(Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 6, new Point(120, 520), new Point(380, 330)));
        document.Add(Callout(new Point(700, 120), AnnotationPalette.Orange, 36));
        document.Add(Mark(AnnotationKind.Pen, AnnotationPalette.Violet, 6, [.. Enumerable.Range(0, 30).Select(i => new Point(560 + i * 9, 460 + Math.Sin(i / 2.5) * 30))]));

        // Typing: the box shows the text in the mark's colour and size, at the click.
        panel.Tool = AnnotationTool.Text;
        panel.Color = AnnotationPalette.Blue;
        panel.FontSize = 64;
        rig.Click(180, 140);
        rig.Canvas.EditorText = "Hola mundo";
        rig.Capture("annotate-text-editing");
        rig.Canvas.CommitEditor();
        rig.Capture("annotate-text-committed");   // the same text, in the same place

        // Selected marks of each kind: a dashed box (or handles on the ends of a line).
        panel.Tool = AnnotationTool.Select;
        rig.Click(250, 425);
        Assert.Equal(AnnotationKind.Arrow, document.Find(panel.SelectedId)!.Kind);
        rig.Capture("annotate-select-arrow");
        rig.Click(700, 120);
        Assert.Equal(AnnotationKind.Callout, document.Find(panel.SelectedId)!.Kind);
        rig.Click(260, 160);
        Assert.Equal(AnnotationKind.Text, document.Find(panel.SelectedId)!.Kind);
        rig.Capture("annotate-select-text");
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_in_the_text_box_and_pressing_Enter_confirms_it()
    {
        var rig = await ShowAsync();
        rig.Panel.Tool = AnnotationTool.Text;
        rig.Click(250, 300);
        Pump();   // the box takes the focus once the press is over
        Assert.True(rig.Canvas.IsEditing);
        rig.Window.KeyTextInput("Hola");
        Assert.Equal("Hola", rig.Canvas.EditorText);
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.False(rig.Canvas.IsEditing);
        Assert.Equal("Hola", Assert.Single(rig.Panel.Document.Items).Text);

        // Escape in the box closes it without the window closing and without a mark.
        rig.Click(250, 400);
        Pump();
        rig.Window.KeyTextInput("no");
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(rig.Canvas.IsEditing);
        Assert.Single(rig.Panel.Document.Items);
        Assert.True(rig.Window.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_double_click_edits_a_text_and_Shift_keeps_shapes_square_and_lines_at_45_degrees()
    {
        var rig = await ShowAsync();
        var (panel, canvas, document) = (rig.Panel, rig.Canvas, rig.Panel.Document);
        var text = document.Add(Text("Hola", new Point(300, 300), AnnotationPalette.Red, 60));

        // Select tool, double click on the text: the box opens with its text.
        panel.Tool = AnnotationTool.Select;
        Assert.False(canvas.BeginGesture(canvas.ToView(new Point(310, 310)), KeyModifiers.None, clickCount: 2));
        Assert.True(canvas.IsEditing);
        Assert.Equal("Hola", canvas.EditorText);
        Assert.Equal(text.Id, panel.SelectedId);
        canvas.EditorText = "Adiós";
        canvas.CommitEditor();
        Assert.Equal("Adiós", document.Find(text.Id)!.Text);

        // Shift while dragging: a rectangle becomes a square.
        panel.Tool = AnnotationTool.Rectangle;
        DragWith(canvas, new Point(100, 100), new Point(400, 220), KeyModifiers.Shift);
        var square = document.Items[^1].Corners;
        Assert.Equal(square.Width, square.Height, 1);
        Assert.Equal(300, square.Width, 1);

        // ...and a line is held to a multiple of 45 degrees: a drag meant at about 8 degrees goes flat.
        panel.Tool = AnnotationTool.Line;
        DragWith(canvas, new Point(100, 500), new Point(500, 556), KeyModifiers.Shift);
        var line = document.Items[^1];
        Assert.Equal(line.Points[0].Y, line.Points[1].Y, 1);
        panel.Tool = AnnotationTool.Arrow;
        DragWith(canvas, new Point(600, 100), new Point(700, 230), KeyModifiers.Shift);   // about 52 degrees: snaps to 45
        var arrow = document.Items[^1];
        Assert.Equal(Math.Abs(arrow.Points[1].X - arrow.Points[0].X), Math.Abs(arrow.Points[1].Y - arrow.Points[0].Y), 1);
        rig.Window.Close();

        static void DragWith(AnnotationCanvas canvas, Point from, Point to, KeyModifiers modifiers)
        {
            canvas.BeginGesture(canvas.ToView(from), modifiers);
            canvas.UpdateGesture(canvas.ToView(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2)), modifiers);
            canvas.EndGesture(canvas.ToView(to), modifiers);
        }
    }

    [AvaloniaFact]
    public async Task The_ends_of_an_arrow_resize_it_and_only_a_filled_shape_is_picked_from_inside()
    {
        var rig = await ShowAsync();
        var (panel, document) = (rig.Panel, rig.Panel.Document);
        var arrow = document.Add(Mark(AnnotationKind.Arrow, AnnotationPalette.Red, 6, new Point(100, 100), new Point(300, 200)));
        var hollow = document.Add(Mark(AnnotationKind.Rectangle, AnnotationPalette.Blue, 6, new Point(500, 100), new Point(800, 300)));
        var solid = document.Add(Mark(AnnotationKind.Ellipse, AnnotationPalette.Green, 6, new Point(150, 350), new Point(450, 550)) with { Filled = true });
        panel.Tool = AnnotationTool.Select;

        // Pick the arrow, then drag its tip: the start stays, the end follows.
        rig.Click(200, 150);
        Assert.Equal(arrow.Id, panel.SelectedId);
        rig.Drag(new Point(300, 200), new Point(420, 260));
        var resized = document.Find(arrow.Id)!;
        Assert.Equal(100, resized.Points[0].X, 1.5);
        Assert.Equal(100, resized.Points[0].Y, 1.5);
        Assert.Equal(420, resized.Points[1].X, 1.5);
        Assert.Equal(260, resized.Points[1].Y, 1.5);

        // Inside an outline-only rectangle there is nothing to pick; inside a filled ellipse there is.
        rig.Click(650, 200);
        Assert.Equal(0, panel.SelectedId);
        rig.Click(300, 450);
        Assert.Equal(solid.Id, panel.SelectedId);
        rig.Click(500, 100);   // the rectangle's corner
        Assert.Equal(hollow.Id, panel.SelectedId);

        // A mark cannot be dragged off the photo: its middle stays on it.
        rig.Drag(new Point(650, 100), new Point(1500, -400));
        var middle = document.Find(hollow.Id)!.Corners.Center;
        Assert.InRange(middle.X, 0, 900);
        Assert.InRange(middle.Y, 0, 600);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Pressing_Apply_while_still_typing_confirms_the_text_and_saves_it()
    {
        var rig = await ShowAsync();
        var window = rig.Window;
        rig.Panel.Tool = AnnotationTool.Text;
        rig.Click(200, 300);
        Pump();
        window.KeyTextInput("Mira");
        Assert.True(rig.Canvas.IsEditing);
        Assert.True(rig.Panel.CanApply);    // a text on its way counts: the button must not look dead
        Assert.Empty(rig.Panel.Document.Items);   // though it is not on the photo until it is confirmed
        rig.Canvas.EditorText = "   ";
        Assert.False(rig.Panel.CanApply);   // an empty box does not
        rig.Canvas.EditorText = "Mira";

        // Pressing Apply (outside the photo) ends the text first.
        var apply = window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("papply"));
        var centre = apply.TranslatePoint(new Point(apply.Bounds.Width / 2, apply.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Assert.False(rig.Canvas.IsEditing);
        Assert.Equal("Mira", Assert.Single(rig.Panel.Document.Items).Text);

        await WaitAsync(() => rig.Panel.IsDone || rig.Panel.HasError);
        Assert.True(rig.Panel.IsDone, rig.Panel.Status);
        Assert.True(File.Exists(Path.Combine(_root, "lienzo anotado.png")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_in_the_middle_of_a_drag_cancels_it()
    {
        var rig = await ShowAsync();
        rig.Panel.Tool = AnnotationTool.Rectangle;
        rig.Window.MouseDown(rig.At(100, 100), MouseButton.Left);
        rig.Window.MouseMove(rig.At(300, 250));
        Assert.True(rig.Canvas.IsGesturing);
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(rig.Canvas.IsGesturing);
        rig.Window.MouseMove(rig.At(400, 300));
        rig.Window.MouseUp(rig.At(400, 300), MouseButton.Left);
        Assert.Empty(rig.Panel.Document.Items);
        Assert.True(rig.Window.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task The_buttons_pick_the_tool_and_the_colour_and_the_shortcuts_work_from_any_button()
    {
        var rig = await ShowAsync();
        var (panel, window) = (rig.Panel, rig.Window);
        Point Centre(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        void Press(Control control)
        {
            window.MouseDown(Centre(control), MouseButton.Left);
            window.MouseUp(Centre(control), MouseButton.Left);
        }

        // One tool is down at a time, the one in use; pressing it again does not lift it.
        var tools = window.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Tag is AnnotationTool).ToArray();
        Assert.Equal(8, tools.Length);
        Press(tools.Single(button => (AnnotationTool)button.Tag! == AnnotationTool.Ellipse));
        Assert.Equal(AnnotationTool.Ellipse, panel.Tool);
        Assert.Equal([AnnotationTool.Ellipse], tools.Where(button => button.IsChecked == true).Select(button => (AnnotationTool)button.Tag!));
        Press(tools.Single(button => (AnnotationTool)button.Tag! == AnnotationTool.Ellipse));
        Assert.Equal([AnnotationTool.Ellipse], tools.Where(button => button.IsChecked == true).Select(button => (AnnotationTool)button.Tag!));

        // Eight colours; the one in use is ringed.
        var swatches = window.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Tag is Color).ToArray();
        Assert.Equal(AnnotationPalette.All.Select(entry => entry.Color), swatches.Select(button => (Color)button.Tag!));
        Press(swatches[5]);
        Assert.Equal(AnnotationPalette.Violet, panel.Color);
        Assert.Equal([5], swatches.Select((button, index) => (button, index)).Where(pair => pair.button.IsChecked == true).Select(pair => pair.index));

        // The buttons above the photo: undo, redo, delete selected, clear all, each only when it can act.
        var actions = window.GetVisualDescendants().OfType<Button>().Where(button => button is not ToggleButton && button.Classes.Contains("pchip")).ToArray();
        Assert.Equal(4, actions.Length);
        Assert.All(actions, button => Assert.False(button.IsEnabled));
        var first = panel.Document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Red, 4, new Point(10, 10), new Point(200, 80)));
        panel.Document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Red, 4, new Point(10, 90), new Point(200, 160)));
        Pump();
        Assert.True(actions[0].IsEnabled);    // undo
        Assert.False(actions[1].IsEnabled);   // redo
        Assert.False(actions[2].IsEnabled);   // delete selected: nothing selected
        Assert.True(actions[3].IsEnabled);    // clear all
        Press(actions[0]);
        Assert.Single(panel.Document.Items);
        Assert.True(actions[1].IsEnabled);
        Press(actions[1]);
        Assert.Equal(2, panel.Document.Count);

        // Ctrl+Z / Ctrl+Y / Delete with the focus on a button rather than on the photo.
        actions[3].Focus();
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Single(panel.Document.Items);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.Equal(2, panel.Document.Count);
        panel.Tool = AnnotationTool.Select;
        panel.SelectedId = first.Id;
        Pump();
        Assert.True(actions[2].IsEnabled);
        actions[3].Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Null(panel.Document.Find(first.Id));
        Press(actions[3]);   // clear all
        Assert.Equal(0, panel.Document.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Apply_waits_for_the_photo_and_a_photo_that_vanished_or_changed_fails_cleanly()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.png"), 600, 400);
        var panel = new AnnotateImagePanel(Context(photo));
        panel.Document.Add(Mark(AnnotationKind.Line, AnnotationPalette.Red, 4, new Point(0, 0), new Point(100, 100)));
        Assert.False(panel.CanApply);   // still opening the photo
        await WaitAsync(() => !panel.IsLoading);
        Assert.True(panel.CanApply);

        // The file is replaced by a different picture before Apply: no half-written PNG, a readable message.
        MakePhoto(photo, 300, 200);
        await panel.RunAsync();
        Assert.True(panel.HasError, panel.Status);
        Assert.Contains("cambiado", panel.Status);
        Assert.Empty(Directory.GetFiles(_root, "*anotado*"));

        // Or deleted altogether.
        File.Delete(photo);
        await panel.RunAsync();
        Assert.True(panel.HasError);
        Assert.Empty(Directory.GetFiles(_root, "*anotado*"));
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task Clear_all_and_delete_selected_are_undoable_and_Escape_deselects()
    {
        var rig = await ShowAsync();
        var (panel, document) = (rig.Panel, rig.Panel.Document);
        foreach (var mark in SampleMarks().Take(4))
            document.Add(mark with { Points = [.. mark.Points.Select(p => new Point(p.X * 0.7, p.Y * 0.7))] });
        Assert.True(panel.HasMarks);

        panel.Tool = AnnotationTool.Select;
        var top = document.Items[1];
        panel.SelectedId = top.Id;
        Assert.True(panel.HasSelection);
        rig.Canvas.Focus();
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal(0, panel.SelectedId);
        Assert.True(rig.Window.IsVisible);   // the first Escape only let go of the selection

        panel.SelectedId = top.Id;
        panel.DeleteSelected();
        Assert.Equal(3, document.Count);
        panel.ClearAll();
        Assert.Equal(0, document.Count);
        Assert.False(panel.CanApply);
        panel.Undo();
        Assert.Equal(3, document.Count);
        panel.Undo();
        Assert.Equal(4, document.Count);
        Assert.True(panel.CanApply);
        rig.Window.Close();
    }
}
