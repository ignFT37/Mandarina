// Mandarina: the two photo-composition panels, Background (frameImage) and Collage (createCollage), and what they share
// (ImageCompose, CollageLayout, SwatchPicker). The render tests write mandarina-panel-*.png to the screenshot folder for
// review; the apply tests run Magick.NET for real on small generated photos (flat colours, so a cell's content is told
// by one pixel) and check the names, the sizes, what ends up where and that the originals are never touched.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;
using Rectangle = System.Drawing.Rectangle;

namespace Filee.App.Tests;

public sealed class MandarinaFrameTests : IDisposable
{
    private const string Red = "#E53935";
    private const string Green = "#43A047";
    private const string Blue = "#1E88E5";
    private const string Yellow = "#FDD835";
    private const string Purple = "#8E24AA";
    private const string Cyan = "#00ACC1";
    private const string Orange = "#FB8C00";

    /// <summary>Seven flat colours, far apart, for the photos of a collage.</summary>
    private static readonly string[] Palette = [Red, Green, Blue, Yellow, Purple, Cyan, Orange];

    /// <summary>Sizes and orientations that differ: landscape, portrait, square, panorama, tall, wide, small.</summary>
    private static readonly (uint Width, uint Height)[] Shapes = [(1200, 800), (600, 900), (800, 800), (1600, 600), (500, 750), (900, 500), (640, 480)];

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-frame").FullName;

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

    /// <summary>A photo of one flat colour (its format by extension).</summary>
    private string Flat(string name, string hex, uint width = 900, uint height = 600)
    {
        var path = Path.Combine(_root, name);
        using var image = new MagickImage(new MagickColor(hex), width, height);
        image.Write(path);
        return path;
    }

    /// <summary>Photos of the palette's colours in <see cref="Shapes"/>, as p0.jpg, p1.jpg, …</summary>
    private string[] Photos(int count) =>
        Enumerable.Range(0, count).Select(i => Flat($"p{i}.jpg", Palette[i], Shapes[i].Width, Shapes[i].Height)).ToArray();

    private static (int R, int G, int B) Pixel(MagickImage image, int x, int y)
    {
        using var pixels = image.GetPixels();
        var colour = pixels.GetPixel(x, y).ToColor()!;
        return (colour.R, colour.G, colour.B);
    }

    private static (int R, int G, int B) PixelOf(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        return Pixel(image, x, y);
    }

    private static void AssertNear(string hex, (int R, int G, int B) actual, int tolerance, string what)
    {
        Assert.True(ImageCompose.TryParseColor(hex, out var expected));
        var distance = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.True(distance <= tolerance, $"{what}: expected {hex}, got #{actual.R:X2}{actual.G:X2}{actual.B:X2}");
    }

    /// <summary>Waits until the preview shows the options as they are (a change queues a new one at once).</summary>
    private static Task PreviewReadyAsync(Func<bool> isCurrent) => WaitAsync(isCurrent);

    private static string Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private async Task<FrameImagePanel> OpenFrameAsync(string photo, string language = "es")
    {
        var panel = new FrameImagePanel(ContextIn(language, photo));
        await WaitAsync(() => !panel.IsLoading);
        return panel;
    }

    private async Task<CreateCollagePanel> OpenCollageAsync(string language, params string[] photos)
    {
        var panel = new CreateCollagePanel(ContextIn(language, photos));
        await WaitAsync(() => !panel.IsLoading);
        return panel;
    }

    // ───────── Background: look ─────────

    [AvaloniaFact]
    public async Task The_frame_panel_renders_with_a_gradient_padding_and_a_shadow()
    {
        var panel = await OpenFrameAsync(MakePhoto(Path.Combine(_root, "paisaje.jpg")));
        Assert.Null(panel.LoadError);
        Assert.Equal((900u, 600u), panel.PhotoSize);
        panel.Background = FrameBackground.Gradient;
        panel.Primary = "#FF8E3A";
        panel.Secondary = "#A98CEF";
        panel.Angle = 135;
        panel.Padding = 12;
        panel.Corners = 6;
        panel.ShadowBlur = 30;
        panel.ShadowOpacity = 45;
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);

        Render(panel, "frameimage");
        Assert.Equal("Fondo", panel.Title);
        Assert.NotNull(panel.Preview);
        Assert.Null(panel.PreviewError);
    }

    [AvaloniaFact]
    public async Task The_frame_panel_renders_a_tall_canvas_on_a_flat_colour()
    {
        var panel = await OpenFrameAsync(MakePhoto(Path.Combine(_root, "retrato.png"), 600, 900));
        panel.Background = FrameBackground.Color;
        panel.Primary = "#1E2A4A";
        panel.Aspect = FrameAspect.Tall;
        panel.Padding = 8;
        panel.Corners = 50;
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Assert.NotNull(panel.Preview);
        Assert.True(panel.Preview.PixelSize.Height > panel.Preview.PixelSize.Width, "a 9:16 canvas is taller than wide");

        Render(panel, "frameimage-tall");
    }

    [AvaloniaFact]
    public async Task The_frame_panel_renders_a_picture_background()
    {
        var picture = Path.Combine(_root, "montanas.jpg");
        using (var image = new MagickImage("gradient:#1E2A4A-#2EC4B6", 800, 600))
            image.Write(picture);
        var panel = await OpenFrameAsync(MakePhoto(Path.Combine(_root, "foto.jpg")));
        panel.Background = FrameBackground.Image;
        await panel.SetPictureAsync(picture);
        panel.Aspect = FrameAspect.Square;
        panel.Padding = 14;
        panel.Corners = 10;
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Assert.Null(panel.PreviewError);
        Assert.Equal("montanas.jpg", panel.PictureName);

        Render(panel, "frameimage-picture");
    }

    [AvaloniaTheory]
    [InlineData(FrameAspect.Auto)]
    [InlineData(FrameAspect.Square)]
    [InlineData(FrameAspect.Wide)]
    [InlineData(FrameAspect.Portrait)]
    [InlineData(FrameAspect.Tall)]
    public async Task The_preview_has_the_shape_of_the_result_and_is_no_larger_than_900_px(FrameAspect aspect)
    {
        var panel = await OpenFrameAsync(Flat("foto.jpg", Red, 1600, 900));
        panel.Aspect = aspect;
        panel.Padding = 12;
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Assert.Null(panel.PreviewError);

        var ratio = aspect switch { FrameAspect.Square => 1.0, FrameAspect.Wide => 16.0 / 9, FrameAspect.Portrait => 0.8, FrameAspect.Tall => 9.0 / 16, _ => (double?)null };
        var final = ImageCompose.FrameFor(1600, 900, 12, ratio, ImageCompose.FinalSide(1600, 900, 12, ratio));
        var preview = panel.Preview!.PixelSize;
        Assert.True(Math.Max(preview.Width, preview.Height) <= 900, $"{preview}");
        Assert.Equal((double)final.CanvasWidth / final.CanvasHeight, (double)preview.Width / preview.Height, 2);
    }

    [AvaloniaFact]
    public async Task The_shadow_of_a_cut_out_follows_its_outline()
    {
        // A red disc on a transparent 900 × 600 photo.
        var cutout = Path.Combine(_root, "recorte.png");
        using (var image = new MagickImage(MagickColors.Transparent, 900, 600))
        {
            new ImageMagick.Drawing.Drawables().FillColor(new MagickColor(Red)).Circle(450, 300, 450, 50).Draw(image);
            image.Write(cutout);
        }
        var panel = await OpenFrameAsync(cutout);
        panel.Background = FrameBackground.Color;
        panel.Primary = "#FFFFFF";
        panel.Padding = 10;
        panel.Corners = 0;
        panel.Shadow = true;
        panel.ShadowBlur = 20;
        panel.ShadowOpacity = 70;
        panel.ShadowOffset = 20;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal((1200u, 867u), (result.Width, result.Height));
        // The disc is 556 px across, centred at 600, 433, and its shadow drops 24 px.
        AssertNear(Red, Pixel(result, 600, 433), 4, "the disc");
        var under = Pixel(result, 600, 722);
        Assert.True(under.R < 190 && under.G < 190 && under.B < 190, $"under the disc: {under}");
        // Where a rectangle's shadow would be (below the photo's frame) and in the empty corners of the photo: clean.
        AssertNear("#FFFFFF", Pixel(result, 600, 785), 3, "below the photo's frame");
        AssertNear("#FFFFFF", Pixel(result, 120, 120), 3, "the photo's transparent corner");
    }

    [AvaloniaFact]
    public async Task The_frame_result_is_saved_for_review()
    {
        var panel = await OpenFrameAsync(MakePhoto(Path.Combine(_root, "paisaje.jpg")));
        panel.Padding = 12;
        panel.Corners = 6;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var copy = Path.Combine(TestServices.ScreenshotDirectory, "mandarina-result-frameimage.jpg");
        File.Copy(Assert.Single(panel.Outputs), copy, overwrite: true);
        using var image = new MagickImage(copy);
        Assert.Equal((1200u, 877u), (image.Width, image.Height)); // 900 × 600 plus 12 % each side is 1116 × 816, enlarged to a 1200 canvas
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_frame_panel_fits_its_options_in_every_language(string language)
    {
        var panel = await OpenFrameAsync(MakePhoto(Path.Combine(_root, "foto.jpg")), language);
        // The tallest state: a gradient has two colours and an angle.
        panel.Background = FrameBackground.Gradient;
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Render(panel, $"frameimage-{language}");
    }

    // ───────── Background: what it saves ─────────

    [AvaloniaFact]
    public async Task A_square_frame_puts_the_photo_in_the_middle_of_the_background_colour()
    {
        var photo = Flat("foto.jpg", Red);
        var original = Hex(photo);
        var panel = await OpenFrameAsync(photo);
        panel.Background = FrameBackground.Color;
        panel.Primary = "#1E90FF";
        panel.Aspect = FrameAspect.Square;
        panel.Padding = 10;
        panel.Corners = 0;
        panel.Shadow = false;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "foto con fondo.jpg"), output);
        using var image = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        // 900 × 600 plus 10 % of the long side each way is 1080 wide: the canvas grows to 1200, the photo to 1000 × 667.
        Assert.Equal((1200u, 1200u), (image.Width, image.Height));
        foreach (var (x, y) in new[] { (0, 0), (1199, 0), (0, 1199), (1199, 1199), (600, 40), (40, 600), (600, 1160) })
            AssertNear("#1E90FF", Pixel(image, x, y), 6, $"the background at {x},{y}");
        AssertNear(Red, Pixel(image, 600, 600), 8, "the photo's centre");
        AssertNear(Red, Pixel(image, 110, 600), 8, "just inside the photo's left edge");
        AssertNear("#1E90FF", Pixel(image, 90, 600), 8, "just outside the photo's left edge");
        Assert.Equal(original, Hex(photo));
    }

    [AvaloniaFact]
    public async Task Rounded_corners_leave_the_photos_corner_in_the_background_colour()
    {
        var panel = await OpenFrameAsync(Flat("foto.png", Red));
        panel.Background = FrameBackground.Color;
        panel.Primary = "#1E90FF";
        panel.Padding = 10;
        panel.Corners = 30;
        panel.Shadow = false;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var image = new MagickImage(Assert.Single(panel.Outputs));
        // Auto: the canvas is the photo plus padding (1080 × 780), enlarged to 1200 × 867; the photo is 1000 × 667 at 100, 100.
        Assert.Equal((1200u, 867u), (image.Width, image.Height));
        AssertNear("#1E90FF", Pixel(image, 102, 102), 4, "the photo's top left corner");
        AssertNear("#1E90FF", Pixel(image, 1097, 764), 4, "the photo's bottom right corner");
        AssertNear(Red, Pixel(image, 600, 102), 4, "the middle of the photo's top edge");
        AssertNear(Red, Pixel(image, 102, 433), 4, "the middle of the photo's left edge");
        AssertNear(Red, Pixel(image, 600, 433), 4, "the centre");
    }

    [AvaloniaFact]
    public async Task The_shadow_is_darker_than_the_background_under_the_photo()
    {
        var withShadow = await OpenFrameAsync(Flat("a.png", Red));
        withShadow.Background = FrameBackground.Color;
        withShadow.Primary = "#FFFFFF";
        withShadow.Padding = 10;
        withShadow.Corners = 0;
        withShadow.Shadow = true;
        withShadow.ShadowBlur = 20;
        withShadow.ShadowOpacity = 70;
        withShadow.ShadowOffset = 20;
        await withShadow.RunAsync();
        Assert.True(withShadow.IsDone, withShadow.Status);

        var without = await OpenFrameAsync(Flat("b.png", Red));
        without.Background = FrameBackground.Color;
        without.Primary = "#FFFFFF";
        without.Padding = 10;
        without.Corners = 0;
        without.Shadow = false;
        await without.RunAsync();
        Assert.True(without.IsDone, without.Status);

        // The photo spans y 100 … 767 on both; its shadow drops 24 px below it.
        var shadowed = PixelOf(Assert.Single(withShadow.Outputs), 600, 777);
        var flat = PixelOf(Assert.Single(without.Outputs), 600, 777);
        Assert.True(shadowed.R < 150 && shadowed.G < 150 && shadowed.B < 150, $"under the photo: {shadowed}");
        AssertNear("#FFFFFF", flat, 2, "no shadow, no darkening");
        AssertNear("#FFFFFF", PixelOf(withShadow.Outputs[0], 5, 5), 2, "far from the photo the background is untouched");
        AssertNear("#FFFFFF", PixelOf(withShadow.Outputs[0], 600, 860), 2, "the shadow fades out");
    }

    [AvaloniaFact]
    public async Task A_picture_can_be_the_background()
    {
        // Blue on the left half, green on the right.
        var picture = Path.Combine(_root, "fondo.png");
        using (var half = new MagickImage(new MagickColor("#0000FF"), 800, 400))
        using (var other = new MagickImage(new MagickColor("#00FF00"), 400, 400))
        {
            half.Composite(other, 400, 0);
            half.Write(picture);
        }
        var panel = await OpenFrameAsync(Flat("foto.png", Red));
        panel.Padding = 10;
        panel.Corners = 0;
        panel.Shadow = false;
        panel.Background = FrameBackground.Image;
        Assert.False(panel.CanApply, "nothing to put behind the photo yet");

        await panel.SetPictureAsync(picture);
        await WaitAsync(() => panel.CanApply);
        Assert.Equal("fondo.png", panel.PictureName);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var image = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal((1200u, 867u), (image.Width, image.Height));
        AssertNear("#0000FF", Pixel(image, 20, 40), 4, "the picture's left half");
        AssertNear("#00FF00", Pixel(image, 1180, 40), 4, "the picture's right half");
        AssertNear(Red, Pixel(image, 600, 433), 4, "the photo on top");
    }

    [AvaloniaFact]
    public async Task A_gradient_background_runs_between_its_two_colours()
    {
        var panel = await OpenFrameAsync(Flat("foto.png", Red));
        panel.Background = FrameBackground.Gradient;
        panel.Primary = "#000000";
        panel.Secondary = "#FFFFFF";
        panel.Angle = 90;
        panel.Padding = 20;
        panel.Corners = 0;
        panel.Shadow = false;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var image = new MagickImage(Assert.Single(panel.Outputs));
        var left = Pixel(image, 2, 20).R;
        var right = Pixel(image, (int)image.Width - 3, 20).R;
        Assert.True(left < 20 && right > 235, $"left {left}, right {right}");
    }

    [AvaloniaTheory]
    [InlineData(FrameAspect.Auto, 1200, 867)]
    [InlineData(FrameAspect.Square, 1200, 1200)]
    [InlineData(FrameAspect.Wide, 1387, 780)]
    [InlineData(FrameAspect.Portrait, 1080, 1350)]
    [InlineData(FrameAspect.Tall, 1080, 1920)]
    public async Task The_canvas_takes_the_shape_asked_for_and_holds_the_photo_with_its_padding(FrameAspect aspect, int width, int height)
    {
        var panel = await OpenFrameAsync(Flat("foto.jpg", Green));
        panel.Background = FrameBackground.Color;
        panel.Primary = "#FFFFFF";
        panel.Aspect = aspect;
        panel.Padding = 10;
        panel.Corners = 0;
        panel.Shadow = false;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var image = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal(((uint)width, (uint)height), (image.Width, image.Height));
        // The photo is centred, with at least the padding (90 px of its 900) all round.
        AssertNear(Green, Pixel(image, width / 2, height / 2), 8, "the centre");
        AssertNear("#FFFFFF", Pixel(image, 80, height / 2), 4, "the left margin");
        AssertNear("#FFFFFF", Pixel(image, width / 2, 80), 4, "the top margin");
        AssertNear("#FFFFFF", Pixel(image, width - 81, height / 2), 4, "the right margin");
        AssertNear("#FFFFFF", Pixel(image, width / 2, height - 81), 4, "the bottom margin");
    }

    [AvaloniaTheory]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".tiff")]
    [InlineData(".bmp")]
    [InlineData(".avif")]
    public async Task The_result_keeps_the_originals_format(string extension)
    {
        var photo = Flat("foto" + extension, Blue, 300, 200);
        var panel = await OpenFrameAsync(photo);
        panel.Corners = 10;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("foto con fondo" + extension, Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal(1200u, Math.Max(image.Width, image.Height)); // a small photo is enlarged to a 1200 px canvas
        Assert.False(image.HasAlpha, "the canvas is opaque");
    }

    [AvaloniaFact]
    public async Task A_second_run_does_not_overwrite_the_first()
    {
        var photo = Flat("foto.jpg", Red);
        var first = await OpenFrameAsync(photo);
        await first.RunAsync();
        var second = await OpenFrameAsync(photo);
        await second.RunAsync();

        Assert.Equal(Path.Combine(_root, "foto con fondo.jpg"), Assert.Single(first.Outputs));
        Assert.Equal(Path.Combine(_root, "foto con fondo 2.jpg"), Assert.Single(second.Outputs));
        Assert.True(File.Exists(Path.Combine(_root, "foto con fondo.jpg")));
    }

    [AvaloniaFact]
    public async Task A_photo_that_cannot_be_read_is_reported_instead_of_loading_for_ever()
    {
        var broken = Path.Combine(_root, "roto.jpg");
        File.WriteAllText(broken, "this is not a picture");
        var panel = new FrameImagePanel(Context(broken));
        await WaitAsync(() => !panel.IsLoading);

        Assert.NotNull(panel.LoadError);
        Assert.Contains("roto.jpg", panel.LoadError);
        Assert.False(panel.CanApply);
        Render(panel, "frameimage-broken");
    }

    [AvaloniaFact]
    public async Task A_background_picture_that_cannot_be_read_is_reported_and_ignored()
    {
        var panel = await OpenFrameAsync(Flat("foto.png", Red));
        var broken = Path.Combine(_root, "malo.png");
        File.WriteAllText(broken, "nope");
        panel.Background = FrameBackground.Image;

        await panel.SetPictureAsync(broken);

        Assert.NotNull(panel.PictureError);
        Assert.False(panel.CanApply);
        Assert.Null(panel.PicturePath);
    }

    // ───────── The canvas of a framed photo ─────────

    [Fact]
    public void The_canvas_is_at_least_1200_px_and_at_most_6000_px_on_its_long_side()
    {
        Assert.Equal(1200, ImageCompose.FinalSide(100, 100, 0, null));
        Assert.Equal(1200, ImageCompose.FinalSide(900, 600, 10, 1));
        Assert.Equal(1440, ImageCompose.FinalSide(1200, 800, 10, null));  // the photo's 1200 plus 10 % (120) each side
        Assert.Equal(6000, ImageCompose.FinalSide(8000, 6000, 10, null));
        Assert.Equal(6000, ImageCompose.FinalSide(6000, 6000, 0, null));
    }

    [Theory]
    [InlineData(900u, 600u, 10.0, null, 1200)]
    [InlineData(900u, 600u, 0.0, null, 900)]
    [InlineData(900u, 600u, 25.0, 1.0, 1000)]
    [InlineData(600u, 900u, 5.0, 16.0 / 9, 1600)]
    [InlineData(4000u, 3000u, 15.0, 9.0 / 16, 1400)]
    public void The_photo_is_centred_and_never_closer_to_the_edge_than_the_padding(uint photoWidth, uint photoHeight, double padding, double? ratio, int longSide)
    {
        var layout = ImageCompose.FrameFor(photoWidth, photoHeight, padding, ratio, longSide);
        Assert.Equal(longSide, (int)Math.Max(layout.CanvasWidth, layout.CanvasHeight));
        if (ratio is { } shape)
            Assert.Equal(shape, (double)layout.CanvasWidth / layout.CanvasHeight, 2);
        // The same shape as the photo, centred.
        Assert.Equal((double)photoWidth / photoHeight, (double)layout.PhotoWidth / layout.PhotoHeight, 2);
        Assert.InRange(layout.PhotoX - (layout.CanvasWidth - layout.PhotoWidth - layout.PhotoX), -1, 1);
        Assert.InRange(layout.PhotoY - (layout.CanvasHeight - layout.PhotoHeight - layout.PhotoY), -1, 1);
        // Padding is a share of the photo's long side, kept at the canvas' scale.
        var scale = (double)layout.PhotoWidth / photoWidth;
        var margin = Math.Min(layout.PhotoX, layout.PhotoY);
        Assert.True(margin >= Math.Max(photoWidth, photoHeight) * padding / 100 * scale - 1.5, $"margin {margin}");
    }

    // ───────── Colours, gradients, corners ─────────

    [Theory]
    [InlineData("#FF8E3A", "#FF8E3A")]
    [InlineData("ff8e3a", "#FF8E3A")]
    [InlineData("  #0a0  ", "#00AA00")]
    [InlineData("#FFF", "#FFFFFF")]
    [InlineData("abc", "#AABBCC")]
    public void Hex_colours_are_read_with_or_without_the_hash_and_in_short_form(string text, string expected)
    {
        Assert.True(ImageCompose.TryParseColor(text, out var colour));
        Assert.Equal(expected, ImageCompose.ToHex(colour));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGHHII")]
    [InlineData("red")]
    public void Unreadable_hex_colours_are_refused_and_fall_back(string text)
    {
        Assert.False(ImageCompose.TryParseColor(text, out _));
        Assert.Equal("#123456", ImageCompose.NormalizeHex(text, "#123456"));
        Assert.Equal("#FFFFFF", ImageCompose.ToHex(ImageCompose.ParseColor(text)));
    }

    [Theory]
    [InlineData(90.0, 0, 50, 199, 50)]      // left → right: black at the left, white at the right
    [InlineData(270.0, 199, 50, 0, 50)]     // right → left
    [InlineData(180.0, 100, 0, 100, 99)]    // top → bottom
    [InlineData(0.0, 100, 99, 100, 0)]      // bottom → top
    public void A_gradient_runs_the_way_its_angle_says(double angle, int darkX, int darkY, int lightX, int lightY)
    {
        using var gradient = ImageCompose.Gradient(200, 100, MagickColors.Black, MagickColors.White, angle);
        var dark = Pixel(gradient, darkX, darkY).R;
        var light = Pixel(gradient, lightX, lightY).R;
        Assert.True(dark < 12 && light > 243, $"dark end {dark}, light end {light}");
        Assert.False(gradient.HasAlpha);
    }

    [Fact]
    public void A_picture_with_its_own_colour_profile_is_converted_to_srgb()
    {
        using var wide = new MagickImage(new MagickColor(200, 100, 50), 20, 20);
        wide.SetProfile(ColorProfiles.AdobeRGB1998);
        ImageCompose.ToSrgb(wide);
        // Adobe RGB's (200, 100, 50) is a more saturated orange: about (227, 100, 42) in sRGB.
        var converted = Pixel(wide, 5, 5);
        Assert.InRange(converted.R, 215, 235);
        Assert.InRange(converted.B, 36, 48);

        using var plain = new MagickImage(new MagickColor(200, 100, 50), 20, 20);
        ImageCompose.ToSrgb(plain);
        Assert.Equal((200, 100, 50), Pixel(plain, 5, 5)); // without a profile it is sRGB already
    }

    /// <summary>A CMYK JPEG of one orange (#C86432), with or without its profile embedded.</summary>
    private string CmykPhoto(string name, bool tagged)
    {
        var path = Path.Combine(_root, name);
        using var image = new MagickImage(new MagickColor(200, 100, 50), 300, 200);
        image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
        if (!tagged)
            image.RemoveProfile("icc");
        image.Write(path);
        return path;
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cmyk_photo_keeps_its_colours_in_the_frame(bool tagged)
    {
        var panel = await OpenFrameAsync(CmykPhoto("imprenta.jpg", tagged));
        panel.Background = FrameBackground.Color;
        panel.Primary = "#FFFFFF";
        panel.Corners = 0;
        panel.Shadow = false;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        AssertNear("#C86432", PixelOf(Assert.Single(panel.Outputs), 600, 433), 18, "the orange of a CMYK photo");
    }

    [AvaloniaFact]
    public async Task A_cmyk_photo_keeps_its_colours_in_the_collage()
    {
        var panel = await OpenCollageAsync("es", CmykPhoto("imprenta.jpg", tagged: true), Flat("verde.jpg", Green));
        panel.Corners = 0;
        panel.Spacing = 0;
        panel.Layout = CollageStyle.Row;
        panel.Size = CollageSize.Small;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        AssertNear("#C86432", PixelOf(output, 270, 270), 18, "the CMYK photo's cell");
        AssertNear(Green, PixelOf(output, 810, 270), 14, "the other cell");
    }

    [Fact]
    public void A_diagonal_gradient_puts_its_colours_on_the_corners_it_runs_between()
    {
        using var down = ImageCompose.Gradient(300, 120, MagickColors.Black, MagickColors.White, 135);
        Assert.True(Pixel(down, 0, 0).R < 6 && Pixel(down, 299, 119).R > 249, "135°: top left → bottom right");
        Assert.InRange(Pixel(down, 150, 60).R, 120, 135);
        using var up = ImageCompose.Gradient(300, 120, MagickColors.Black, MagickColors.White, 45);
        Assert.True(Pixel(up, 0, 119).R < 6 && Pixel(up, 299, 0).R > 249, "45°: bottom left → top right");
    }

    [Fact]
    public void Rounding_clears_the_four_corners_alike_and_leaves_the_edges()
    {
        using var image = new MagickImage(new MagickColor("#FF0000"), 400, 300);
        ImageCompose.RoundCorners(image, 60);

        using var pixels = image.GetPixels();
        byte Alpha(int x, int y) => pixels.GetPixel(x, y).ToColor()!.A;
        Assert.True(image.HasAlpha);
        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(0, Alpha(399, 0));
        Assert.Equal(0, Alpha(0, 299));
        Assert.Equal(0, Alpha(399, 299));
        Assert.Equal(255, Alpha(200, 0));
        Assert.Equal(255, Alpha(0, 150));
        Assert.Equal(255, Alpha(399, 150));
        Assert.Equal(255, Alpha(200, 299));
        Assert.Equal(255, Alpha(200, 150));
        // Symmetric: the same arc pixel in each corner has the same coverage.
        Assert.Equal(Alpha(25, 25), Alpha(374, 25));
        Assert.Equal(Alpha(25, 25), Alpha(25, 274));
        Assert.Equal(Alpha(25, 25), Alpha(374, 274));
        // Inside the arc, opaque; outside, clear.
        Assert.Equal(255, Alpha(60, 60));
        Assert.Equal(0, Alpha(10, 10));
    }

    [Fact]
    public void A_radius_beyond_half_the_shorter_side_makes_a_pill_and_a_tiny_one_changes_nothing()
    {
        using var pill = new MagickImage(new MagickColor("#00FF00"), 200, 100);
        ImageCompose.RoundCorners(pill, 500);
        using (var pixels = pill.GetPixels())
        {
            Assert.Equal(0, pixels.GetPixel(0, 0).ToColor()!.A);
            Assert.Equal(255, pixels.GetPixel(100, 50).ToColor()!.A);
            Assert.Equal(255, pixels.GetPixel(100, 0).ToColor()!.A);
        }

        using var square = new MagickImage(new MagickColor("#00FF00"), 100, 100);
        ImageCompose.RoundCorners(square, 0.3);
        Assert.False(square.HasAlpha);
    }

    [Fact]
    public void Rounding_keeps_a_photos_own_transparency()
    {
        using var image = new MagickImage(MagickColor.FromRgba(255, 0, 0, 128), 200, 200);
        ImageCompose.RoundCorners(image, 50);
        using var pixels = image.GetPixels();
        Assert.InRange(pixels.GetPixel(100, 100).ToColor()!.A, 126, 130);
        Assert.Equal(0, pixels.GetPixel(1, 1).ToColor()!.A);
        Assert.True(ImageCompose.HasTransparency(image));
    }

    [Fact]
    public void Transparency_is_told_from_a_merely_present_alpha_channel()
    {
        using var opaque = new MagickImage(new MagickColor("#336699"), 20, 20);
        Assert.False(ImageCompose.HasTransparency(opaque));
        opaque.Alpha(AlphaOption.Set);
        Assert.True(opaque.HasAlpha);
        Assert.False(ImageCompose.HasTransparency(opaque), "an alpha channel that is fully opaque is not transparency");
        using var clear = new MagickImage(MagickColors.Transparent, 20, 20);
        Assert.True(ImageCompose.HasTransparency(clear));
    }

    [Fact]
    public void Cover_fills_the_box_around_the_centre_and_contain_keeps_the_whole_picture()
    {
        // Red on the left half, blue on the right half: a centre crop shows both halves, a corner crop only one.
        using var photo = new MagickImage(new MagickColor("#FF0000"), 400, 200);
        using (var right = new MagickImage(new MagickColor("#0000FF"), 200, 200))
            photo.Composite(right, 200, 0);

        using var cover = ImageCompose.Cover(photo, 100, 100);
        Assert.Equal((100u, 100u), (cover.Width, cover.Height));
        Assert.True(Pixel(cover, 10, 50).R > 240 && Pixel(cover, 90, 50).B > 240, "both halves are in the cell");
        Assert.Equal((400u, 200u), (photo.Width, photo.Height)); // the source is untouched

        var contained = (MagickImage)photo.Clone();
        ImageCompose.Resize(contained, 100, 100, ComposeFit.Contain);
        Assert.Equal((100u, 50u), (contained.Width, contained.Height));
        contained.Dispose();

        // Any odd box is filled exactly.
        foreach (var (w, h) in new[] { (317u, 211u), (7u, 300u), (1u, 1u), (2000u, 50u) })
        {
            var odd = (MagickImage)photo.Clone();
            ImageCompose.Resize(odd, w, h, ComposeFit.Cover);
            Assert.Equal((w, h), (odd.Width, odd.Height));
            odd.Dispose();
        }
    }

    // ───────── SwatchPicker ─────────

    [AvaloniaFact]
    public void The_swatch_picker_takes_a_typed_colour_and_shows_the_one_it_is_given()
    {
        var picker = new SwatchPicker { Hex = "#FF8E3A" };
        var window = new Window { Content = picker, Width = 400, Height = 60 };
        window.Show();
        Pump();
        var box = picker.GetVisualDescendants().OfType<TextBox>().Single();

        Assert.Equal("#FF8E3A", box.Text);
        box.Text = "#102030";
        Assert.Equal("#102030", picker.Hex);
        box.Text = "ABCDEF";                     // six digits are taken as soon as they are typed, hash or not
        Assert.Equal("#ABCDEF", picker.Hex);
        box.Text = "#12";                        // half typed: the colour stays
        Assert.Equal("#ABCDEF", picker.Hex);
        picker.Hex = "#00FF00";                  // set from outside: the box follows
        Assert.Equal("#00FF00", box.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_swatch_sets_the_colour_and_rings_it()
    {
        var picker = new SwatchPicker { Hex = "#FFFFFF" };
        var window = new Window { Content = picker, Width = 420, Height = 60 };
        window.Show();
        Pump();
        var swatches = picker.GetVisualDescendants().OfType<Border>().Where(border => border.Cursor is not null).ToList();
        Assert.Equal(SwatchPicker.Palette.Count, swatches.Count);

        var target = swatches[5];
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);

        Assert.Equal(SwatchPicker.Palette[5], picker.Hex);
        Assert.Equal(1, swatches.Count(border => border.BorderBrush is Avalonia.Media.ISolidColorBrush { Color.A: > 0 }));
        window.Close();
    }

    [Fact]
    public void The_swatches_are_a_dozen_or_so_distinct_valid_colours()
    {
        Assert.InRange(SwatchPicker.Palette.Count, 12, 16);
        Assert.Equal(SwatchPicker.Palette.Count, SwatchPicker.Palette.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SwatchPicker.Palette, hex => Assert.True(ImageCompose.TryParseColor(hex, out _), hex));
    }

    // ───────── Collage layout (pure) ─────────

    public static TheoryData<CollageStyle, int> StylesAndCounts()
    {
        var data = new TheoryData<CollageStyle, int>();
        foreach (var style in Enum.GetValues<CollageStyle>())
            foreach (var count in new[] { 2, 3, 5, 7 })
                data.Add(style, count);
        return data;
    }

    /// <summary>The gap between two cells: how far apart they are along the axis that separates them.</summary>
    private static int Gap(Rectangle a, Rectangle b) =>
        Math.Max(Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right), Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom));

    [Theory]
    [MemberData(nameof(StylesAndCounts))]
    public void Cells_stay_inside_the_canvas_never_overlap_and_keep_the_spacing_asked_for(CollageStyle style, int count)
    {
        foreach (var (width, height) in new[] { (1080, 1080), (1080, 810), (1080, 608), (608, 1080), (2048, 683), (3000, 3000) })
            foreach (var spacing in new[] { 0, 1, 12, 37, 60 })
            {
                var cells = CollageLayout.Compute(count, style, width, height, spacing);
                var where = $"{style} × {count} on {width} × {height}, spacing {spacing}";
                Assert.Equal(count, cells.Count);
                foreach (var cell in cells)
                {
                    Assert.True(cell.Width > 0 && cell.Height > 0, where);
                    Assert.True(cell.Left >= spacing && cell.Top >= spacing && cell.Right <= width - spacing && cell.Bottom <= height - spacing,
                        $"{where}: {cell} leaves the margin");
                }
                var closest = int.MaxValue;
                for (var i = 0; i < cells.Count; i++)
                    for (var j = i + 1; j < cells.Count; j++)
                    {
                        Assert.False(cells[i].IntersectsWith(cells[j]), $"{where}: cells {i} and {j} overlap");
                        closest = Math.Min(closest, Gap(cells[i], cells[j]));
                    }
                Assert.Equal(spacing, closest);
            }
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(7, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(30, 6)]
    public void The_grid_has_the_square_root_of_the_count_rounded_up_in_columns(int count, int columns)
    {
        Assert.Equal(columns, CollageLayout.Columns(count));
        var cells = CollageLayout.Compute(count, CollageStyle.Grid, 1200, 1200, 10);
        var rows = (count + columns - 1) / columns;
        Assert.Equal(rows, cells.Select(cell => cell.Y).Distinct().Count());
        Assert.Equal(columns, cells.Count(cell => cell.Y == cells[0].Y));
        // Equal cells.
        Assert.Single(cells.Select(cell => (cell.Width, cell.Height)).Distinct());
    }

    [Fact]
    public void A_short_last_row_of_the_grid_is_centred()
    {
        var cells = CollageLayout.Compute(5, CollageStyle.Grid, 1000, 750, 10);
        var first = cells.Take(3).ToArray();
        var last = cells.Skip(3).ToArray();
        var left = first.Min(cell => cell.Left);
        var right = first.Max(cell => cell.Right);
        Assert.InRange((last.Min(cell => cell.Left) - left) - (right - last.Max(cell => cell.Right)), -1, 1);
    }

    [Fact]
    public void A_row_is_one_line_of_equal_cells_in_order_and_a_column_is_the_same_turned()
    {
        var row = CollageLayout.Compute(4, CollageStyle.Row, 1200, 300, 8);
        Assert.Single(row.Select(cell => cell.Y).Distinct());
        Assert.Equal(row.OrderBy(cell => cell.X), row);
        Assert.Single(row.Select(cell => cell.Width).Distinct());

        var column = CollageLayout.Compute(4, CollageStyle.Column, 300, 1200, 8);
        Assert.Single(column.Select(cell => cell.X).Distinct());
        Assert.Equal(column.OrderBy(cell => cell.Y), column);
        Assert.Single(column.Select(cell => cell.Height).Distinct());
    }

    [Fact]
    public void The_featured_layout_gives_the_first_photo_about_60_percent_beside_the_others()
    {
        // A landscape canvas: the first photo on the left, the others in a grid on the right.
        var cells = CollageLayout.Compute(5, CollageStyle.Featured, 1200, 900, 12);
        Assert.InRange((double)cells[0].Width / 1200, 0.5, 0.62);
        Assert.Equal(900 - 2 * 12, cells[0].Height);
        Assert.All(cells.Skip(1), cell => Assert.True(cell.Left >= cells[0].Right + 12));
        Assert.Equal(4, cells.Skip(1).Count());

        // A tall canvas: the first photo on top.
        var tall = CollageLayout.Compute(5, CollageStyle.Featured, 900, 1200, 12);
        Assert.InRange((double)tall[0].Height / 1200, 0.5, 0.62);
        Assert.Equal(900 - 2 * 12, tall[0].Width);
        Assert.All(tall.Skip(1), cell => Assert.True(cell.Top >= tall[0].Bottom + 12));

        // Two photos: the other one takes the whole rest.
        var two = CollageLayout.Compute(2, CollageStyle.Featured, 1200, 900, 12);
        Assert.Equal(two[0].Height, two[1].Height);
        Assert.Equal(12, two[1].Left - two[0].Right);
    }

    [Fact]
    public void An_absurd_spacing_is_reduced_instead_of_squeezing_the_cells_out()
    {
        foreach (var style in Enum.GetValues<CollageStyle>())
        {
            var cells = CollageLayout.Compute(30, style, 1080, 1080, 60);
            Assert.Equal(30, cells.Count);
            Assert.All(cells, cell => Assert.True(cell.Width >= 1 && cell.Height >= 1 && cell.Left >= 0 && cell.Right <= 1080 && cell.Bottom <= 1080, $"{style}: {cell}"));
        }
        Assert.Empty(CollageLayout.Compute(0, CollageStyle.Grid, 100, 100, 5));
        Assert.Single(CollageLayout.Compute(1, CollageStyle.Featured, 100, 100, 5));
    }

    [Theory]
    [InlineData(1.0, 1080, 1080, 1080)]
    [InlineData(16.0 / 9, 1920, 1920, 1080)]
    [InlineData(9.0 / 16, 1920, 1080, 1920)]
    [InlineData(4.0 / 3, 2048, 2048, 1536)]
    public void The_canvas_has_the_longer_side_asked_for_in_the_shape_asked_for(double ratio, int longSide, int width, int height)
    {
        var size = CollageLayout.CanvasSize(ratio, longSide);
        Assert.Equal((width, height), (size.Width, size.Height));
    }

    [Fact]
    public void Auto_shapes_follow_the_layout()
    {
        Assert.Equal(1.0, CollageLayout.AutoRatio(CollageStyle.Grid, 4));
        Assert.Equal(1.5, CollageLayout.AutoRatio(CollageStyle.Grid, 5));
        Assert.Equal(2.0, CollageLayout.AutoRatio(CollageStyle.Row, 2));
        Assert.Equal(4.0, CollageLayout.AutoRatio(CollageStyle.Row, 9));
        Assert.Equal(0.25, CollageLayout.AutoRatio(CollageStyle.Column, 9));
        Assert.True(CollageLayout.AutoRatio(CollageStyle.Featured, 5) > 1);
    }

    // ───────── Collage: look ─────────

    /// <summary>Five plasma photos of different shapes and orientations, made with Magick.NET.</summary>
    private string[] FivePhotos()
    {
        var sources = new[] { "plasma:red-blue", "plasma:green-yellow", "plasma:orange-purple", "plasma:cyan-navy", "plasma:pink-gold" };
        var files = new string[5];
        for (var i = 0; i < 5; i++)
        {
            files[i] = Path.Combine(_root, $"foto{i + 1}.jpg");
            // Plasma is slow at size: make a small one and stretch it to the photo's shape.
            using var image = new MagickImage(sources[i], 240, 240);
            image.Resize(new MagickGeometry(Shapes[i].Width, Shapes[i].Height) { IgnoreAspectRatio = true });
            image.Write(files[i]);
        }
        return files;
    }

    [AvaloniaFact]
    public async Task The_collage_panel_renders_five_photos_of_different_shapes()
    {
        var panel = await OpenCollageAsync("es", FivePhotos());
        Assert.Equal(5, panel.Items.Count);
        Assert.All(panel.Items, item => Assert.NotNull(item.Thumbnail));
        panel.Spacing = 14;
        panel.Corners = 14;
        panel.BackgroundColor = "#F2EDE6";
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Assert.Null(panel.PreviewError);

        Render(panel, "createcollage");
        Assert.Equal("Collage", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_collage_panel_renders_the_featured_layout_on_a_dark_background()
    {
        var panel = await OpenCollageAsync("es", FivePhotos());
        panel.Layout = CollageStyle.Featured;
        panel.Spacing = 18;
        panel.Corners = 24;
        panel.BackgroundColor = "#1E2A4A";
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Assert.Null(panel.PreviewError);

        Render(panel, "createcollage-featured");
    }

    [AvaloniaFact]
    public async Task The_collage_panel_renders_a_wide_row()
    {
        var panel = await OpenCollageAsync("es", FivePhotos());
        panel.Layout = CollageStyle.Row;
        panel.Aspect = CollageAspect.Wide;
        panel.Spacing = 8;
        panel.Corners = 0;
        panel.BackgroundColor = "#FF8E3A";
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Assert.Null(panel.PreviewError);

        Render(panel, "createcollage-row");
    }

    [AvaloniaFact]
    public async Task The_collage_result_is_saved_for_review()
    {
        var panel = await OpenCollageAsync("es", FivePhotos());
        panel.Layout = CollageStyle.Featured;
        panel.Spacing = 24;
        panel.Corners = 28;
        panel.BackgroundColor = "#F2EDE6";
        panel.Size = CollageSize.Small;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var copy = Path.Combine(TestServices.ScreenshotDirectory, "mandarina-result-createcollage.jpg");
        File.Copy(Assert.Single(panel.Outputs), copy, overwrite: true);
        using var image = new MagickImage(copy);
        Assert.Equal((1080u, 810u), (image.Width, image.Height));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_collage_panel_fits_its_options_in_every_language(string language)
    {
        var panel = await OpenCollageAsync(language, Photos(7));
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Render(panel, $"createcollage-{language}");
    }

    [AvaloniaFact]
    public async Task The_collage_panel_asks_for_a_second_photo()
    {
        var panel = await OpenCollageAsync("es", Photos(1));
        Assert.False(panel.CanApply);
        Assert.NotNull(panel.Hint);
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);
        Render(panel, "createcollage-one");
    }

    // ───────── Collage: what it saves ─────────

    [AvaloniaTheory]
    [MemberData(nameof(StylesAndCounts))]
    public async Task Every_layout_puts_each_photo_in_its_own_cell(CollageStyle style, int count)
    {
        var files = Photos(count);
        var panel = await OpenCollageAsync("es", files);
        panel.Layout = style;
        panel.Spacing = 20;
        panel.Corners = 0;
        panel.BackgroundColor = "#202020";
        panel.Size = CollageSize.Small;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "p0 collage.jpg"), output);
        var (width, height) = CollageLayout.CanvasSize(CollageLayout.AutoRatio(style, count), 1080);
        using var image = new MagickImage(output);
        Assert.Equal(((uint)width, (uint)height), (image.Width, image.Height));
        var cells = CollageLayout.Compute(count, style, width, height, 20);
        for (var i = 0; i < count; i++)
            AssertNear(Palette[i], Pixel(image, cells[i].X + cells[i].Width / 2, cells[i].Y + cells[i].Height / 2), 14, $"the middle of cell {i}");
        AssertNear(Palette[0], Pixel(image, cells[0].X + 3, cells[0].Y + 3), 14, "the first photo reaches the corner of its cell");
        AssertNear("#202020", Pixel(image, 3, 3), 8, "the margin");
        AssertNear("#202020", Pixel(image, width - 4, height - 4), 8, "the margin, bottom right");
        foreach (var file in files)
            Assert.True(File.Exists(file));
    }

    [AvaloniaFact]
    public async Task The_size_asked_for_is_the_size_made()
    {
        var files = Photos(3);
        foreach (var (size, side) in new[] { (CollageSize.Small, 1080), (CollageSize.Medium, 2048), (CollageSize.Large, 3000) })
        {
            var panel = await OpenCollageAsync("es", files);
            panel.Size = size;
            panel.Aspect = CollageAspect.Wide;
            await panel.RunAsync();

            Assert.True(panel.IsDone, panel.Status);
            using var image = new MagickImage(Assert.Single(panel.Outputs));
            Assert.Equal((uint)side, image.Width);
            Assert.InRange(image.Height, (uint)Math.Floor(side * 9 / 16.0), (uint)Math.Ceiling(side * 9 / 16.0));
        }
    }

    [AvaloniaTheory]
    [InlineData(CollageAspect.Square, 1.0)]
    [InlineData(CollageAspect.Standard, 4.0 / 3)]
    [InlineData(CollageAspect.Wide, 16.0 / 9)]
    [InlineData(CollageAspect.Tall, 9.0 / 16)]
    public async Task The_canvas_proportion_is_the_one_chosen(CollageAspect aspect, double ratio)
    {
        var panel = await OpenCollageAsync("es", Photos(4));
        panel.Aspect = aspect;
        panel.Size = CollageSize.Small;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var image = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal(1080u, Math.Max(image.Width, image.Height));
        Assert.Equal(ratio, (double)image.Width / image.Height, 2);
    }

    [AvaloniaFact]
    public async Task Changing_the_order_changes_the_content_and_the_name_follows_the_first_photo()
    {
        var files = Photos(3);
        var panel = await OpenCollageAsync("es", files);
        panel.Layout = CollageStyle.Row;
        panel.Spacing = 10;
        panel.Corners = 0;
        panel.Size = CollageSize.Small;

        // The red photo goes from first to second.
        panel.Move(panel.Items[0], 1);
        Assert.Equal(new[] { files[1], files[0], files[2] }, panel.Items.Select(item => item.Path));
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "p1 collage.jpg"), output);
        var (width, height) = CollageLayout.CanvasSize(CollageLayout.AutoRatio(CollageStyle.Row, 3), 1080);
        var cells = CollageLayout.Compute(3, CollageStyle.Row, width, height, 10);
        using var image = new MagickImage(output);
        AssertNear(Green, Pixel(image, cells[0].X + cells[0].Width / 2, height / 2), 14, "first cell");
        AssertNear(Red, Pixel(image, cells[1].X + cells[1].Width / 2, height / 2), 14, "second cell");
        AssertNear(Blue, Pixel(image, cells[2].X + cells[2].Width / 2, height / 2), 14, "third cell");
    }

    [AvaloniaFact]
    public async Task The_move_buttons_stop_at_the_ends_and_a_photo_can_be_taken_out()
    {
        var files = Photos(4);
        var panel = await OpenCollageAsync("es", files);
        Assert.False(panel.Items[0].MoveUpCommand.CanExecute(null));
        Assert.True(panel.Items[0].MoveDownCommand.CanExecute(null));
        Assert.True(panel.Items[3].MoveUpCommand.CanExecute(null));
        Assert.False(panel.Items[3].MoveDownCommand.CanExecute(null));

        panel.Items[1].MoveDownCommand.Execute(null);
        Assert.Equal(new[] { files[0], files[2], files[1], files[3] }, panel.Items.Select(item => item.Path));
        panel.Items[0].MoveUpCommand.Execute(null);                        // at the top already: nothing happens
        Assert.Equal(files[0], panel.Items[0].Path);

        panel.Items[1].RemoveCommand.Execute(null);                        // the blue one, now second
        Assert.Equal(new[] { files[0], files[1], files[3] }, panel.Items.Select(item => item.Path));
        Assert.True(panel.CanApply);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.True(File.Exists(files[2]), "taking a photo out of the collage does not delete the file");
        Assert.Equal("3 fotos", panel.PhotoCountText);
    }

    [AvaloniaFact]
    public async Task A_collage_needs_two_photos()
    {
        var one = await OpenCollageAsync("es", Photos(1));
        Assert.False(one.CanApply);
        Assert.False(one.CanRun);

        var three = await OpenCollageAsync("es", Photos(3));
        Assert.True(three.CanApply);
        three.Remove(three.Items[2]);
        Assert.True(three.CanApply);
        three.Remove(three.Items[1]);
        Assert.False(three.CanApply, "one photo left");
        Assert.NotNull(three.Hint);
        await three.RunAsync();                                              // the Apply button is off, so nothing runs
        Assert.False(three.IsDone);
        Assert.Empty(three.Outputs);
    }

    [AvaloniaFact]
    public async Task A_photo_with_transparency_makes_the_collage_a_png_otherwise_a_jpg()
    {
        var opaque = Flat("opaca.jpg", Red, 400, 300);
        var clear = Path.Combine(_root, "recorte.png");
        using (var image = new MagickImage(MagickColor.FromRgba(255, 255, 255, 100), 400, 300))
            image.Write(clear);

        var plain = await OpenCollageAsync("es", opaque, Flat("otra.png", Blue, 400, 300));
        await plain.RunAsync();
        Assert.Equal(Path.Combine(_root, "opaca collage.jpg"), Assert.Single(plain.Outputs));

        var withClear = await OpenCollageAsync("es", opaque, clear);
        Assert.True(withClear.Items[1].HasTransparency);
        Assert.False(withClear.Items[0].HasTransparency);
        withClear.Layout = CollageStyle.Row;
        withClear.Spacing = 0;
        withClear.Corners = 0;
        withClear.BackgroundColor = "#000000";
        withClear.Size = CollageSize.Small;
        await withClear.RunAsync();

        Assert.True(withClear.IsDone, withClear.Status);
        var output = Assert.Single(withClear.Outputs);
        Assert.Equal(Path.Combine(_root, "opaca collage.png"), output);
        using var png = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, png.Format);
        // The see-through photo lets the black background show: white at 100/255 over black is a dark grey.
        var cells = CollageLayout.Compute(2, CollageStyle.Row, (int)png.Width, (int)png.Height, 0);
        var grey = Pixel(png, cells[1].X + cells[1].Width / 2, cells[1].Y + cells[1].Height / 2);
        Assert.InRange(grey.R, 85, 115);
    }

    [AvaloniaFact]
    public async Task A_second_collage_does_not_overwrite_the_first()
    {
        var files = Photos(2);
        var first = await OpenCollageAsync("es", files);
        await first.RunAsync();
        var second = await OpenCollageAsync("es", files);
        await second.RunAsync();

        Assert.Equal(Path.Combine(_root, "p0 collage.jpg"), Assert.Single(first.Outputs));
        Assert.Equal(Path.Combine(_root, "p0 collage 2.jpg"), Assert.Single(second.Outputs));
    }

    [AvaloniaFact]
    public async Task The_originals_are_never_changed()
    {
        var files = Photos(4);
        var before = files.Select(Hex).ToArray();
        var panel = await OpenCollageAsync("es", files);
        panel.Corners = 20;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(before, files.Select(Hex));
        Assert.Equal(files.Length + 1, Directory.GetFiles(_root).Length);
    }

    [AvaloniaFact]
    public async Task Each_photo_is_cropped_around_its_centre_to_fill_its_cell()
    {
        // Red left half, blue right half, in a 2:1 photo placed in a square cell: the crop shows both halves.
        var split = Path.Combine(_root, "partida.png");
        using (var image = new MagickImage(new MagickColor("#FF0000"), 800, 400))
        using (var right = new MagickImage(new MagickColor("#0000FF"), 400, 400))
        {
            image.Composite(right, 400, 0);
            image.Write(split);
        }
        var panel = await OpenCollageAsync("es", split, Flat("otra.png", Green, 400, 400));
        panel.Layout = CollageStyle.Row;
        panel.Aspect = CollageAspect.Square;      // two cells, each about 1:2: the 2:1 photo is cut to a tall slice of its middle
        panel.Spacing = 0;
        panel.Corners = 0;
        panel.Size = CollageSize.Small;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        var cell = CollageLayout.Compute(2, CollageStyle.Row, 1080, 1080, 0)[0];
        Assert.True(Pixel(result, cell.X + 5, cell.Y + cell.Height / 2).R > 235, "the left of the cell is the photo's red half");
        Assert.True(Pixel(result, cell.X + cell.Width - 6, cell.Y + cell.Height / 2).B > 235, "the right of the cell is the photo's blue half");
    }

    [AvaloniaFact]
    public async Task Rounded_corners_cut_each_cell_and_show_the_background()
    {
        var panel = await OpenCollageAsync("es", Photos(2));
        panel.Layout = CollageStyle.Row;
        panel.Spacing = 0;
        panel.Corners = 40;
        panel.BackgroundColor = "#FFFFFF";
        panel.Aspect = CollageAspect.Wide;
        panel.Size = CollageSize.Small;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var image = new MagickImage(Assert.Single(panel.Outputs));
        var cell = CollageLayout.Compute(2, CollageStyle.Row, (int)image.Width, (int)image.Height, 0)[0];
        AssertNear("#FFFFFF", Pixel(image, cell.X + 2, cell.Y + 2), 8, "the first cell's top left corner");
        AssertNear(Palette[0], Pixel(image, cell.X + cell.Width / 2, cell.Y + 2), 14, "the middle of its top edge");
    }

    [AvaloniaFact]
    public async Task Only_the_first_thirty_photos_are_used()
    {
        var files = Enumerable.Range(0, 33).Select(i => Flat($"m{i:00}.png", Palette[i % Palette.Length], 120, 90)).ToArray();
        var panel = await OpenCollageAsync("es", files);
        Assert.Equal(30, panel.Items.Count);
        Assert.Equal(files[0], panel.Items[0].Path);
        Assert.NotNull(panel.Hint);
        Assert.True(panel.CanApply);
        panel.Size = CollageSize.Small;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        Render(panel, "createcollage-thirty");
    }

    [AvaloniaFact]
    public async Task A_photo_that_cannot_be_read_fails_the_collage_and_leaves_nothing_behind()
    {
        var good = Flat("buena.jpg", Red);
        var broken = Path.Combine(_root, "rota.jpg");
        File.WriteAllText(broken, "not a picture");
        var panel = await OpenCollageAsync("es", good, broken);
        Assert.True(panel.Items[1].IsFailed);
        Assert.True(panel.Items[0].IsLoaded);
        await PreviewReadyAsync(() => panel.PreviewIsCurrent);

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.Contains("rota.jpg", panel.Status);
        Assert.False(panel.IsDone);
        Assert.Equal(2, Directory.GetFiles(_root).Length); // nothing was written
        Render(panel, "createcollage-broken");
    }
}
