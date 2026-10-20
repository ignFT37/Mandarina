// Mandarina: the REDACT tools (photo and video). The local detector runs against the real Windows OCR and face detector;
// the photo is covered with the real Magick.NET and the video with the real FFmpeg (engines\ffmpeg next to the test
// binaries); the region editor is driven with synthetic mouse and keyboard input. The panels are rendered to
// mandarina-panel-redact*.png in the screenshot folder for review.

using System.Collections.ObjectModel;
using System.Security.Cryptography;
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

public sealed class MandarinaRedactTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-redact").FullName;

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

    private string PathIn(string name) => Path.Combine(_root, name);

    // ───────── Test material ─────────

    private static readonly string[] Lines = ["Hello World 12345", "Contraseña secreta", "Madrid, España"];

    /// <summary>A PNG of big black text on white, one line under the other, drawn the way the app draws text.</summary>
    private string MakeTextPicture(string name, int width = 1000, int height = 500, params string[] lines)
    {
        var stack = new StackPanel { Spacing = 36, Margin = new Thickness(50, 40) };
        foreach (var line in lines.Length > 0 ? lines : Lines)
            stack.Children.Add(new TextBlock { Text = line, FontSize = 60, Foreground = Brushes.Black });
        // The border, not the stack, is what fills the whole picture with white (a panel only paints what its children use).
        var sheet = new Border { Background = Brushes.White, Child = stack };
        sheet.Measure(new Size(width, height));
        sheet.Arrange(new Rect(0, 0, width, height));
        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        target.Render(sheet);
        var path = PathIn(name);
        using (var stream = File.Create(path))
            target.Save(stream, new PngBitmapEncoderOptions());
        return path;
    }

    /// <summary>The bytes R, G, B of every pixel of a rectangle of the image.</summary>
    private static byte[] Pixels(IMagickImage<byte> image, int x, int y, int width, int height) =>
        image.GetPixels().ToByteArray(x, y, (uint)width, (uint)height, PixelMapping.RGB)!;

    /// <summary>The standard deviation of the brightness inside a rectangle: how much is going on in it.</summary>
    private static double Detail(IMagickImage<byte> image, int x, int y, int width, int height)
    {
        using var area = image.CloneArea(x, y, (uint)width, (uint)height);
        area.Grayscale();
        return area.Statistics().Composite().StandardDeviation;
    }

    /// <summary>How rough a rectangle is: the mean difference of brightness between neighbouring pixels (0 = flat, high = fine detail).</summary>
    private static double Roughness(IMagickImage<byte> image, int x, int y, int width, int height)
    {
        using var area = image.CloneArea(x, y, (uint)width, (uint)height);
        area.Grayscale();
        var data = area.GetPixels().ToByteArray(PixelMapping.RGB)!;
        long total = 0;
        var count = 0;
        for (var row = 0; row < height; row++)
        {
            for (var column = 1; column < width; column++)
            {
                total += Math.Abs(data[(row * width + column) * 3] - data[(row * width + column - 1) * 3]);
                count++;
            }
        }
        return (double)total / count;
    }

    /// <summary>A photo with fine detail all over it: the test pattern with noise on top.</summary>
    private string MakeNoisyPhoto(string name, uint width = 900, uint height = 600)
    {
        var path = PathIn(name);
        using var image = new MagickImage("plasma:orange-purple", width, height);
        image.AddNoise(NoiseType.Gaussian);
        image.Write(path);
        return path;
    }

    /// <summary>The mean absolute difference per channel (0..255) between the same rectangle of two images.</summary>
    private static double Difference(IMagickImage<byte> a, IMagickImage<byte> b, int x, int y, int width, int height)
    {
        var first = Pixels(a, x, y, width, height);
        var second = Pixels(b, x, y, width, height);
        long total = 0;
        for (var i = 0; i < first.Length; i++)
            total += Math.Abs(first[i] - second[i]);
        return (double)total / first.Length;
    }

    /// <summary>The share of the dark pixels of <paramref name="png"/> that lie inside one of <paramref name="rects"/>.</summary>
    private static double InkCovered(string png, IReadOnlyList<Rect> rects, out int rectsWithoutInk)
    {
        using var image = new MagickImage(png);
        var data = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        int width = (int)image.Width, height = (int)image.Height, ink = 0, covered = 0;
        var inside = new int[rects.Count];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (data[(y * width + x) * 3] >= 100)
                    continue;
                ink++;
                var any = false;
                for (var i = 0; i < rects.Count; i++)
                {
                    if (rects[i].Contains(new Point(x, y)))
                    {
                        any = true;
                        inside[i]++;
                    }
                }
                if (any)
                    covered++;
            }
        }
        rectsWithoutInk = inside.Count(count => count == 0);
        return ink == 0 ? 0 : (double)covered / ink;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private async Task<RedactImagePanel> OpenPhotoAsync(string file)
    {
        var panel = new RedactImagePanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private string MakePhotoWithHiddenCopies(string name, uint width = 900, uint height = 600, ushort orientation = 1) =>
        MandarinaTestKit.MakePhotoWithHiddenCopies(PathIn(name), width, height, orientation);

    // ───────── The local detector ─────────

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_copies_of_the_photo_hidden_in_its_metadata_never_go_along_with_what_was_covered(bool strip)
    {
        var photo = MakePhotoWithHiddenCopies("camara.jpg");
        var jpegs = new byte[] { 0xFF, 0xD8, 0xFF };
        Assert.Equal(3, CountIn(photo, jpegs));                                           // the picture, the thumbnail and the maker note preview
        Assert.Equal(1, CountIn(photo, System.Text.Encoding.ASCII.GetBytes("xmpGImg:image>/9j")));
        var panel = await OpenPhotoAsync(photo);
        panel.StripMetadata = strip;
        panel.Regions.Add(new RedactRegion(new Rect(100, 100, 400, 300)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(1, CountIn(output, jpegs));                                          // only the picture itself
        Assert.Equal(0, CountIn(output, System.Text.Encoding.ASCII.GetBytes("xmpGImg")));
        using var result = new MagickImage(output);
        Assert.Null(result.GetXmpProfile());
        if (strip)
            Assert.Null(result.GetExifProfile());
        else
            Assert.Equal("Mandarina Cam", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);     // the camera data stays when asked
        Assert.True(Pixels(result, 120, 120, 360, 260).Max() <= 3, "and the region is black");
    }

    [AvaloniaFact]
    public async Task Text_detection_finds_rectangles_that_cover_the_text()
    {
        var picture = MakeTextPicture("texto.png");

        var found = await LocalDetector.FindTextAsync(picture);

        if (found.Failure is RedactDetectionFailure.NoLanguage or RedactDetectionFailure.Unavailable)
        {
            Assert.Skip("This PC has no Windows text recognition: " + found.Reason);
            return;
        }
        Assert.True(found.Succeeded, found.Reason);
        Assert.NotEmpty(found);
        var rects = found.Select(region => new Rect(region.X, region.Y, region.Width, region.Height)).ToList();
        var share = InkCovered(picture, rects, out var withoutInk);
        Assert.True(share >= 0.97, $"only {share:P0} of the text lies inside the rectangles");
        Assert.Equal(0, withoutInk);                       // no rectangle over blank paper
        Assert.InRange(found.Count, 3, 8);                 // about one per line: words that sit together are joined
        Assert.All(found, region => Assert.True(region.X >= 0 && region.Y >= 0 && region.Right <= 1000 && region.Bottom <= 500));
    }

    [AvaloniaFact]
    public async Task Text_detection_reads_english_and_spanish_with_the_installed_language()
    {
        // One line per language, so each can be checked on its own.
        var picture = MakeTextPicture("idiomas.png", 1000, 300, "The quick brown fox", "El veloz zorro marrón");

        var found = await LocalDetector.FindTextAsync(picture);

        if (!found.Succeeded)
        {
            Assert.Skip("This PC has no Windows text recognition: " + found.Reason);
            return;
        }
        var rects = found.Select(region => new Rect(region.X, region.Y, region.Width, region.Height)).ToList();
        Assert.Contains(rects, rect => rect.Center.Y < 150);            // the English line
        Assert.Contains(rects, rect => rect.Center.Y >= 150);           // the Spanish line, with its ó
        Assert.True(InkCovered(picture, rects, out _) >= 0.95);
    }

    [AvaloniaFact]
    public async Task Face_detection_returns_nothing_for_a_picture_without_faces()
    {
        var found = await LocalDetector.FindFacesAsync(MakeTextPicture("sin caras.png"));

        if (found.Failure is RedactDetectionFailure.NoFaceDetector or RedactDetectionFailure.Unavailable)
        {
            Assert.Skip("This PC has no Windows face detector: " + found.Reason);
            return;
        }
        Assert.True(found.Succeeded, found.Reason);
        Assert.Empty(found);
    }

    [Fact]
    public void The_detector_knows_whether_it_has_what_it_needs()
    {
        Assert.Equal(OperatingSystem.IsWindows(), LocalDetector.IsAvailable);        // Windows brings its own PowerShell
    }

    [AvaloniaFact]
    public async Task A_detector_that_cannot_run_says_why_and_does_not_throw()
    {
        var missing = await LocalDetector.FindTextAsync(PathIn("no existe.png"));
        Assert.Empty(missing);
        Assert.Equal(RedactDetectionFailure.Failed, missing.Failure);
        Assert.False(string.IsNullOrWhiteSpace(missing.Reason));

        // A file that is not a picture reaches Windows and is refused there.
        var notAPicture = PathIn("roto.png");
        File.WriteAllText(notAPicture, "this is not a PNG");
        var broken = await LocalDetector.FindFacesAsync(notAPicture);
        Assert.Empty(broken);
        Assert.False(broken.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(broken.Reason));
    }

    [AvaloniaFact]
    public async Task A_cancelled_detection_stops()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalDetector.FindTextAsync(MakeTextPicture("cancelar.png"), cancel.Token));
    }

    [Fact]
    public void Words_that_follow_each_other_become_one_rectangle_and_far_apart_ones_do_not()
    {
        LocalDetector.RawBox[] words =
        [
            new(0, 100, 50, 80, 40),
            new(0, 190, 50, 90, 40),    // 10 px after the first: the same phrase
            new(0, 700, 50, 60, 40),    // far away: another phrase
            new(1, 100, 120, 200, 40),  // the next line
        ];

        var phrases = LocalDetector.Phrases(words, 1000, 500);

        Assert.Equal(3, phrases.Count);
        var first = phrases[0];
        Assert.True(first.X <= 100 && first.Right >= 280, "the two words are one rectangle");
        Assert.True(first.X >= 90 && first.Right <= 290, "with a small margin");
        Assert.All(phrases, phrase => Assert.True(phrase.X >= 0 && phrase.Right <= 1000 && phrase.Bottom <= 500));
    }

    [Fact]
    public void Faces_get_a_margin_of_fifteen_percent_on_every_side_inside_the_picture()
    {
        // No photo of a face is at hand to try the real detector on, so the shaping of what it returns is checked on its own.
        LocalDetector.RawBox[] faces = [new(0, 100, 100, 100, 120), new(1, 5, 5, 60, 60)];

        var shaped = LocalDetector.Faces(faces, 400, 300);

        Assert.Equal(new DetectedRegion(85, 82, 130, 156), shaped[0]);         // 15 px each side, 18 px above and below
        Assert.Equal(0, shaped[1].X);                                          // would start at -4: kept inside
        Assert.Equal(0, shaped[1].Y);
        Assert.Equal(74, shaped[1].Right, 6);
        Assert.Equal(74, shaped[1].Bottom, 6);
    }

    // ───────── The region editor ─────────

    /// <summary>An editor filling a 400 × 300 window, showing a 400 × 200 picture at 1:1 (it sits 50 px from the top).</summary>
    private (Window Window, RegionEditor Editor, ObservableCollection<RedactRegion> Regions) EditorOf(double pixelWidth = 400, double pixelHeight = 200)
    {
        var regions = new ObservableCollection<RedactRegion>();
        var editor = new RegionEditor
        {
            Source = new Bitmap(MakePhoto(PathIn($"editor{Guid.NewGuid():N}.png"), 400, 200)),
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            Regions = regions,
        };
        var window = new Window { Width = 400, Height = 300, Content = editor };
        window.Show();
        Pump();
        return (window, editor, regions);
    }

    /// <summary>A point of the picture as a point of the window (the editor fills it).</summary>
    private static Point At(RegionEditor editor, double x, double y) => editor.ToControl(new Point(x, y));

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public void Dragging_over_empty_space_draws_a_region_and_selects_it()
    {
        var (window, editor, regions) = EditorOf();
        Assert.Equal(1, editor.Scale, 6);

        Drag(window, At(editor, 100, 60), At(editor, 300, 160));

        var region = Assert.Single(regions);
        Assert.Equal(new Rect(100, 60, 200, 100), region.Rect);
        Assert.Same(region, editor.SelectedRegion);
        Assert.False(region.IsAuto);
        Assert.True(region.IsWholeVideo);

        // Dragging the other way round gives the same kind of rectangle.
        Drag(window, At(editor, 380, 190), At(editor, 340, 150));
        Assert.Equal(new Rect(340, 150, 40, 40), regions[1].Rect);

        // A drag that leaves the picture stops at its edge.
        Drag(window, At(editor, 20, 120), At(editor, -300, 900));
        Assert.Equal(new Rect(0, 120, 20, 80), regions[2].Rect);
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_region_moves_it_and_it_stays_inside_the_picture()
    {
        var (window, editor, regions) = EditorOf();
        Drag(window, At(editor, 100, 60), At(editor, 300, 160));
        var region = regions[0];

        Drag(window, At(editor, 200, 110), At(editor, 230, 130));
        Assert.Equal(new Rect(130, 80, 200, 100), region.Rect);

        // Far to the right and below: it stops at the edges, whole.
        Drag(window, At(editor, 230, 130), At(editor, 700, 600));
        Assert.Equal(new Rect(200, 100, 200, 100), region.Rect);
        Drag(window, At(editor, 300, 150), At(editor, -500, -500));
        Assert.Equal(new Rect(0, 0, 200, 100), region.Rect);
        Assert.Single(regions);
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_handle_resizes_a_region_within_the_picture_and_a_minimum()
    {
        var (window, editor, regions) = EditorOf();
        Drag(window, At(editor, 100, 60), At(editor, 300, 160));
        var region = regions[0];

        Drag(window, At(editor, 300, 160), At(editor, 340, 180));        // bottom-right corner
        Assert.Equal(new Rect(100, 60, 240, 120), region.Rect);

        Drag(window, At(editor, 100, 60), At(editor, 90, 50));           // top-left corner
        Assert.Equal(new Rect(90, 50, 250, 130), region.Rect);

        Drag(window, At(editor, 340, 115), At(editor, 395, 115));        // right edge only
        Assert.Equal(new Rect(90, 50, 305, 130), region.Rect);

        Drag(window, At(editor, 395, 115), At(editor, 900, 115));        // past the edge of the picture
        Assert.Equal(400, region.Rect.Right);

        Drag(window, At(editor, 90, 50), At(editor, 900, 900));          // top-left corner dragged down and right past the other one
        Assert.Equal(new Rect(400 - RegionEditor.MinimumPixels, 180 - RegionEditor.MinimumPixels, RegionEditor.MinimumPixels, RegionEditor.MinimumPixels), region.Rect);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_selects_a_region_clicking_elsewhere_lets_go_and_a_tiny_drag_makes_nothing()
    {
        var (window, editor, regions) = EditorOf();
        Drag(window, At(editor, 50, 40), At(editor, 150, 100));
        Drag(window, At(editor, 250, 40), At(editor, 350, 100));
        var (first, second) = (regions[0], regions[1]);
        Assert.Same(second, editor.SelectedRegion);

        window.MouseDown(At(editor, 100, 70), MouseButton.Left);          // a click, no movement
        window.MouseUp(At(editor, 100, 70), MouseButton.Left);
        Assert.Same(first, editor.SelectedRegion);
        Assert.Equal(new Rect(50, 40, 100, 60), first.Rect);              // a click does not move it

        window.MouseDown(At(editor, 200, 150), MouseButton.Left);         // empty space
        window.MouseUp(At(editor, 200, 150), MouseButton.Left);
        Assert.Null(editor.SelectedRegion);

        Drag(window, At(editor, 200, 150), At(editor, 204, 153));          // smaller than the minimum
        Assert.Equal(2, regions.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Delete_removes_the_selected_region_only()
    {
        var (window, editor, regions) = EditorOf();
        Drag(window, At(editor, 50, 40), At(editor, 150, 100));
        Drag(window, At(editor, 250, 40), At(editor, 350, 100));
        var (first, second) = (regions[0], regions[1]);

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Pump();

        Assert.Equal([first], regions);
        Assert.Null(editor.SelectedRegion);

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);               // nothing selected: nothing happens
        Assert.Equal([first], regions);
        Assert.DoesNotContain(second, regions);
        window.Close();
    }

    [AvaloniaFact]
    public void Regions_are_in_pixels_of_the_original_whatever_the_size_of_the_view()
    {
        // The original is 1600 × 800 and is shown at a quarter of its size.
        var (window, editor, regions) = EditorOf(1600, 800);
        Assert.Equal(0.25, editor.Scale, 6);
        Assert.Equal(32, editor.MinimumSize, 6);                           // 8 screen pixels are 32 of the original

        Drag(window, At(editor, 400, 200), At(editor, 800, 400));

        Assert.Equal(new Rect(400, 200, 400, 200), Assert.Single(regions).Rect);
        // 20 × 10 pixels of the original are 5 × 2.5 on screen: under the minimum.
        Drag(window, At(editor, 1000, 500), At(editor, 1020, 510));
        Assert.Single(regions);
        window.Close();
    }

    [AvaloniaFact]
    public void The_editor_follows_the_list_it_is_given()
    {
        var (window, editor, regions) = EditorOf();
        var region = new RedactRegion(new Rect(10, 10, 50, 50), RedactRegionSource.Text);
        regions.Add(region);
        editor.SelectedRegion = region;
        Pump();

        regions.Clear();                                                   // "Clear all"
        Pump();

        Assert.Null(editor.SelectedRegion);
        Assert.True(region.IsAuto);
        window.Close();
    }

    // ───────── The effects ─────────

    [Fact]
    public void Blur_and_pixelation_are_measured_against_the_area_and_even_the_weakest_is_past_legible()
    {
        // Blur: a share of the shorter side, so it scales with the area and does not care which side is longer.
        Assert.Equal(RedactEffects.BlurSigma(25, 400, 50), RedactEffects.BlurSigma(25, 50, 400), 6);
        Assert.Equal(2 * RedactEffects.BlurSigma(25, 100, 80), RedactEffects.BlurSigma(25, 200, 160), 6);
        Assert.True(RedactEffects.BlurSigma(50, 400, 100) > RedactEffects.BlurSigma(20, 400, 100));
        Assert.Equal(1.5, RedactEffects.BlurSigma(1, 3, 3), 6);                    // never a blur too small to be one
        // The weakest setting, on an area tight around a line of text (its height is the shorter side): text stops being
        // readable to a person from 35 % of that height, so 40 % is the floor.
        Assert.Equal(0.40 * 50, RedactEffects.BlurSigma(RedactEffects.MinStrength, 400, 50), 6);
        Assert.Equal(0.80 * 50, RedactEffects.BlurSigma(RedactEffects.MaxStrength, 400, 50), 6);
        Assert.True(RedactEffects.BlurSigma(-5, 400, 50) >= 0.40 * 50);            // out of range values do not go under the floor

        // Pixelation: blocks of a share of the shorter side, from 40 % of it to all of it.
        Assert.Equal(20, RedactEffects.BlockSize(RedactEffects.MinBlock, 400, 50));
        Assert.Equal(50, RedactEffects.BlockSize(RedactEffects.MaxBlock, 400, 50));
        Assert.Equal(22, RedactEffects.BlockSize(8, 400, 50));                     // the default: 44 %
        Assert.Equal(2, RedactEffects.BlockSize(4, 4, 4));                         // never under 2 px
        Assert.True(RedactEffects.BlockSize(-5, 400, 50) >= 0.40 * 50);
        Assert.True(RedactEffects.BlockSize(30, 300, 200) > RedactEffects.BlockSize(10, 300, 200));

        // Whole pixels, outwards, inside the picture.
        Assert.Equal((10, 20, 101, 52), RedactEffects.Snap(new Rect(10.4, 20.2, 100.2, 50.9), 320, 240));
        Assert.Equal((0, 0, 320, 240), RedactEffects.Snap(new Rect(-10, -10, 1000, 1000), 320, 240));
        Assert.Equal(0, RedactEffects.Snap(new Rect(400, 10, 20, 20), 320, 240).Width);
    }

    // ───────── The photo panel ─────────

    [AvaloniaFact]
    public async Task The_photo_panel_renders_with_its_regions()
    {
        var panel = await OpenPhotoAsync(MakePhoto(PathIn("paisaje.jpg"), 900, 600));
        panel.Regions.Add(new RedactRegion(new Rect(140, 80, 220, 260), RedactRegionSource.Faces));
        panel.Regions.Add(new RedactRegion(new Rect(520, 360, 300, 70), RedactRegionSource.Text));
        var chosen = new RedactRegion(new Rect(330, 200, 240, 150));
        panel.Regions.Add(chosen);
        panel.SelectedRegion = chosen;

        var png = Render(panel, "redactimage");

        Assert.True(File.Exists(png));
        Assert.Equal("Tapar", panel.Title);
        Assert.Equal("900 × 600", panel.SizeText);
    }

    [AvaloniaFact]
    public async Task The_photo_panel_renders_a_blur_over_text_found_by_the_detector()
    {
        var panel = await OpenPhotoAsync(MakeTextPicture("carta.png", 900, 500));
        await panel.DetectTextAsync();
        if (panel.Regions.Count == 0)
        {
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }
        panel.Effect = RedactEffect.Blur;
        panel.SelectedRegion = panel.Regions[1];

        Render(panel, "redactimage-blur");

        Assert.Equal(panel.Context.Format("panel.redactImage.found", panel.Regions.Count), panel.DetectionNote);
    }

    [AvaloniaFact]
    public async Task The_panels_fit_the_longest_detection_note_while_they_work()
    {
        var photo = await OpenPhotoAsync(MakePhoto(PathIn("nota.jpg"), 900, 600));
        photo.Effect = RedactEffect.Pixelate;
        photo.IsDetecting = true;
        photo.DetectionNote = photo.Context.Format("panel.redactImage.detect_failed", photo.Context["panel.redactImage.reason_language"]);
        Render(photo, "redactimage-note");

        var video = await MakeVideoAsync("nota.mp4");
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        panel.IsDetecting = true;
        panel.DetectionNote = panel.Context.Format("panel.redactVideo.detect_failed", panel.Context["panel.redactVideo.reason_language"]);
        Render(panel, "redactvideo-note");
    }

    private static T Find<T>(Visual root, Func<T, bool> where) where T : Visual => root.GetVisualDescendants().OfType<T>().First(where);

    private static Point CentreOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public async Task The_buttons_of_the_photo_panel_do_what_they_say()
    {
        var panel = await OpenPhotoAsync(MakeTextPicture("botones.png", 900, 500));
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var detectText = Find<Button>(window, button => button.Content as string == "Detectar texto");
        var clear = Find<Button>(window, button => button.Content as string == "Borrar todo");
        var fill = Find<ToggleButton>(window, button => button.Content as string == "Relleno");
        var blur = Find<ToggleButton>(window, button => button.Content as string == "Desenfoque");
        var pixelate = Find<ToggleButton>(window, button => button.Content as string == "Pixelado");
        Assert.False(clear.IsEnabled);                                       // nothing to clear yet
        Assert.True(fill.IsChecked);

        // The effect buttons choose the effect, one at a time; pressing the chosen one again keeps it.
        Click(window, CentreOf(window, blur));
        Assert.Equal(RedactEffect.Blur, panel.Effect);
        Assert.True(blur.IsChecked);
        Assert.False(fill.IsChecked);
        Click(window, CentreOf(window, blur));
        Assert.Equal(RedactEffect.Blur, panel.Effect);
        Assert.True(blur.IsChecked);
        Click(window, CentreOf(window, pixelate));
        Assert.Equal(RedactEffect.Pixelate, panel.Effect);
        Assert.False(blur.IsChecked);
        Click(window, CentreOf(window, fill));
        Assert.Equal(RedactEffect.Fill, panel.Effect);

        // The swatches choose the colour.
        var swatches = Find<RedactSwatchPicker>(window, _ => true);
        var second = swatches.TranslatePoint(new Point(4 + 13 + 38, swatches.Bounds.Height / 2), window)!.Value;
        Click(window, second);
        Assert.Equal(RedactEffects.Swatches[1], panel.FillColor);

        // "Detect text" works and says so; "Clear all" empties the list.
        Click(window, CentreOf(window, detectText));
        Assert.True(panel.IsDetecting);
        Assert.False(detectText.IsEnabled);                                  // disabled while it works
        await WaitAsync(() => !panel.IsDetecting);
        Pump();
        if (panel.Regions.Count == 0)
        {
            window.Close();
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }
        Assert.True(detectText.IsEnabled);
        Assert.True(clear.IsEnabled);
        Click(window, CentreOf(window, clear));
        Assert.Empty(panel.Regions);
        Assert.False(clear.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_times_boxes_of_the_video_panel_edit_the_selected_region()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        var region = new RedactRegion(new Rect(40, 40, 100, 60));
        panel.Regions.Add(region);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var from = Find<TextBox>(window, box => box.Name == "FromBox");
        var to = Find<TextBox>(window, box => box.Name == "ToBox");
        var whole = Find<CheckBox>(window, box => box.Content as string == "Todo el vídeo");
        Assert.False(from.IsEffectivelyVisible);                             // nothing selected: no times to show

        panel.SelectedRegion = region;
        Pump();
        Assert.True(from.IsEffectivelyVisible);
        Assert.True(whole.IsChecked);
        Assert.False(from.IsEnabled);                                        // the whole video: no times to type

        Click(window, CentreOf(window, whole));                              // "from here to the end"
        Assert.False(region.IsWholeVideo);
        Assert.True(from.IsEnabled);
        Assert.Equal("0:01.00", from.Text);                                  // the frame shown
        Assert.Equal(PanelFormat.Time(panel.Duration), to.Text);

        from.Focus();
        from.Text = "0:00.25";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(0.25, region.Start);
        to.Focus();                                                          // leaving the box takes what was typed too
        to.Text = "0:02.5";
        from.Focus();
        Pump();
        Assert.Equal(2.5, region.End);
        Assert.Equal("0:02.50", to.Text);

        from.Text = "garbage";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);   // Escape puts the old time back
        Assert.Equal("0:00.25", from.Text);

        Click(window, CentreOf(window, whole));
        Assert.True(region.IsWholeVideo);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_photo_panel_says_when_the_photo_cannot_be_read()
    {
        var notAPhoto = PathIn("roto.jpg");
        File.WriteAllText(notAPhoto, "not a photo");

        var panel = new RedactImagePanel(Context(notAPhoto));
        await WaitAsync(() => !panel.IsLoading);

        Assert.False(string.IsNullOrEmpty(panel.LoadError));
        Assert.False(panel.CanDetect);
        Assert.False(panel.CanApply);
        Render(panel, "redactimage-error");
    }

    [AvaloniaFact]
    public async Task A_flat_cover_blackens_the_region_and_leaves_the_rest_of_the_photo_as_it_was()
    {
        var photo = MakePhoto(PathIn("foto.jpg"), 900, 600);
        var before = Hash(photo);
        var panel = await OpenPhotoAsync(photo);
        Assert.False(panel.CanApply);                                       // nothing to cover yet

        panel.Regions.Add(new RedactRegion(new Rect(200, 150, 300, 200)));
        Assert.True(panel.CanApply);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("foto tapado.jpg"), output);
        Assert.Equal(before, Hash(photo));                                  // the original is untouched
        using var original = new MagickImage(photo);
        using var result = new MagickImage(output);
        Assert.Equal((900u, 600u), (result.Width, result.Height));
        Assert.True(Pixels(result, 208, 158, 284, 184).Max() <= 3, "the region is black");
        Assert.True(Pixels(result, 200, 150, 300, 200).Select(b => (int)b).Average() < 8, "the whole region, edges included");
        Assert.True(Difference(original, result, 0, 0, 900, 140) < 3, "above");
        Assert.True(Difference(original, result, 520, 0, 380, 600) < 3, "to the right");
        Assert.True(Difference(original, result, 0, 360, 900, 240) < 3, "below");
        Assert.True(Difference(original, result, 0, 140, 190, 230) < 3, "to the left, right up to the region");
    }

    [AvaloniaFact]
    public async Task The_cover_uses_the_colour_chosen_and_each_region_is_covered()
    {
        var photo = MakePhoto(PathIn("naranja.png"), 900, 600);
        var panel = await OpenPhotoAsync(photo);
        panel.FillColor = RedactEffects.Swatches[2];                        // Mandarina's orange
        panel.Regions.Add(new RedactRegion(new Rect(100, 100, 150, 100)));
        panel.Regions.Add(new RedactRegion(new Rect(600, 400, 200, 150)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        foreach (var (x, y) in new[] { (150, 140), (700, 470) })
            Assert.Equal(new byte[] { 0xFF, 0x52, 0x00 }, Pixels(result, x, y, 1, 1));
    }

    [AvaloniaFact]
    public async Task A_region_beyond_the_edge_or_with_fractions_is_covered_inside_the_photo()
    {
        var photo = MakePhoto(PathIn("borde.png"), 900, 600);
        var panel = await OpenPhotoAsync(photo);
        panel.Regions.Add(new RedactRegion(new Rect(850, 550, 200, 200)));          // runs past the corner
        panel.Regions.Add(new RedactRegion(new Rect(10.5, 10.5, 20.2, 20.2)));      // not on a pixel

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var original = new MagickImage(photo);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal((900u, 600u), (result.Width, result.Height));
        Assert.Equal(0, Pixels(result, 899, 599, 1, 1).Max());
        Assert.Equal(0, Pixels(result, 850, 550, 1, 1).Max());
        Assert.Equal(0, Pixels(result, 10, 10, 1, 1).Max());                        // rounded outwards: 10..31
        Assert.Equal(0, Pixels(result, 30, 30, 1, 1).Max());
        Assert.Equal(0, Difference(original, result, 100, 100, 500, 300));          // PNG: the rest is exactly as it was
    }

    [AvaloniaFact]
    public async Task Blur_flattens_the_detail_inside_the_region_and_nowhere_else()
    {
        var photo = MakeNoisyPhoto("borroso.png");
        var panel = await OpenPhotoAsync(photo);
        panel.Effect = RedactEffect.Blur;
        panel.Regions.Add(new RedactRegion(new Rect(200, 150, 300, 200)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(PathIn("borroso tapado.png"), Assert.Single(panel.Outputs));
        using var original = new MagickImage(photo);
        using var result = new MagickImage(panel.Outputs[0]);
        var (before, after) = (Roughness(original, 208, 158, 284, 184), Roughness(result, 208, 158, 284, 184));
        Assert.True(after < before * 0.1, $"the fine detail inside went from {before:0.0} to {after:0.0}");
        Assert.True(Detail(result, 208, 158, 284, 184) < Detail(original, 208, 158, 284, 184), "and the contrast went down too");
        Assert.True(Difference(original, result, 200, 150, 300, 200) > 5, "something changed inside");
        Assert.Equal(0, Difference(original, result, 0, 0, 900, 150));             // above the region, and right up to it
        Assert.Equal(0, Difference(original, result, 0, 150, 200, 200));           // to its left
        Assert.Equal(0, Difference(original, result, 500, 150, 400, 450));        // to its right and below
    }

    [AvaloniaFact]
    public async Task Pixelation_turns_the_region_into_flat_blocks()
    {
        var photo = MakePhoto(PathIn("mosaico.png"), 900, 600);
        var panel = await OpenPhotoAsync(photo);
        panel.Effect = RedactEffect.Pixelate;
        panel.Regions.Add(new RedactRegion(new Rect(200, 150, 300, 200)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var original = new MagickImage(photo);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        var size = RedactEffects.BlockSize(panel.BlockSize, 300, 200);
        var (across, down) = ((int)Math.Ceiling(300.0 / size), (int)Math.Ceiling(200.0 / size));
        using var after = result.CloneArea(200, 150, 300, 200);
        using var before = original.CloneArea(200, 150, 300, 200);
        Assert.True(before.TotalColors > 1000, "the original has no flat areas");
        Assert.True(after.TotalColors <= (uint)(across * down), $"{after.TotalColors} colours in {across} × {down} blocks");
        Assert.Equal(0, Difference(original, result, 0, 0, 900, 150));
        Assert.Equal(0, Difference(original, result, 500, 0, 400, 600));
    }

    [AvaloniaTheory]
    [InlineData(RedactEffect.Blur)]
    [InlineData(RedactEffect.Pixelate)]
    public async Task Text_stays_unreadable_with_the_default_blur_and_pixelation(RedactEffect effect)
    {
        // The detector is also the judge: Windows reads the picture again after it was covered.
        var picture = MakeTextPicture("secreto.png", 1000, 500, "Contraseña: Mandarina2026", "IBAN ES91 2100 0418 4502", "Persona de Ejemplo");
        var panel = await OpenPhotoAsync(picture);
        await panel.DetectTextAsync();
        if (panel.Regions.Count == 0)
        {
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }
        Assert.True(panel.Regions.Count >= 3);
        panel.Effect = effect;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var covered = Assert.Single(panel.Outputs);
        var again = await LocalDetector.FindTextAsync(covered);
        Assert.True(again.Succeeded, again.Reason);
        Assert.Empty(again);
    }

    [AvaloniaFact]
    public async Task The_metadata_goes_unless_the_box_is_unticked_and_the_colour_profile_stays()
    {
        var photo = PathIn("camara.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 900, 600))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Mandarina Cam");
            image.SetProfile(exif);
            image.SetProfile(ColorProfiles.SRGB);
            image.Write(photo);
        }
        using (var check = new MagickImage(photo))
        {
            Assert.NotNull(check.GetExifProfile());                         // the test material has it
            Assert.NotNull(check.GetColorProfile());
        }

        var stripped = await OpenPhotoAsync(photo);
        Assert.True(stripped.StripMetadata);                                // on by default
        stripped.Regions.Add(new RedactRegion(new Rect(100, 100, 200, 100)));
        await stripped.RunAsync();
        Assert.True(stripped.IsDone, stripped.Status);
        using (var result = new MagickImage(Assert.Single(stripped.Outputs)))
        {
            Assert.Null(result.GetExifProfile());
            Assert.NotNull(result.GetColorProfile());                       // without it a phone's wide-gamut photo looks washed out
        }

        var kept = await OpenPhotoAsync(photo);
        kept.StripMetadata = false;
        kept.Regions.Add(new RedactRegion(new Rect(100, 100, 200, 100)));
        await kept.RunAsync();
        Assert.True(kept.IsDone, kept.Status);
        using var keptResult = new MagickImage(Assert.Single(kept.Outputs));
        Assert.Equal("Mandarina Cam", keptResult.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
    }

    [AvaloniaTheory]
    [InlineData("png")]
    [InlineData("tiff")]
    [InlineData("webp")]
    public async Task Text_chunks_and_tags_of_other_formats_do_not_carry_a_copy_either(string extension)
    {
        var photo = PathIn($"etiquetas.{extension}");
        using (var image = new MagickImage("plasma:orange-purple", 400, 300))
        {
            image.SetAttribute("Thumbnail", "SECRET-THUMBNAIL-BASE64");
            image.SetAttribute("comment", "SECRET-COMMENT");
            image.Write(photo);
        }
        if (extension is "png" or "tiff")
            Assert.True(CountIn(photo, System.Text.Encoding.ASCII.GetBytes("SECRET-")) > 0, "the test material carries them");
        var panel = await OpenPhotoAsync(photo);
        panel.StripMetadata = false;
        panel.Regions.Add(new RedactRegion(new Rect(50, 50, 200, 150)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(0, CountIn(Assert.Single(panel.Outputs), System.Text.Encoding.ASCII.GetBytes("SECRET-")));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_photo_turned_by_its_orientation_is_covered_where_it_is_seen(bool strip)
    {
        // Stored as 600 x 300 and shown turned a quarter, so 300 x 600: the regions are in what is shown.
        var photo = MakePhotoWithHiddenCopies("giro.jpg", 600, 300, orientation: 6);
        using var seen = PanelImages.Read(photo);
        Assert.Equal((300u, 600u), (seen.Width, seen.Height));

        var panel = await OpenPhotoAsync(photo);
        panel.StripMetadata = strip;
        Assert.Equal((300d, 600d), (panel.SourceWidth, panel.SourceHeight));
        panel.Regions.Add(new RedactRegion(new Rect(100, 400, 150, 150)));
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal((300u, 600u), (result.Width, result.Height));
        result.AutoOrient();                                                // whatever the tag says must not turn it again
        Assert.Equal((300u, 600u), (result.Width, result.Height));
        Assert.True(Pixels(result, 108, 408, 134, 134).Max() <= 3, "black where it was drawn");
        // A second JPEG generation of a photo full of noise: a few levels of difference, nothing like a turned picture (which is ~60).
        Assert.True(Difference(seen, result, 0, 0, 300, 380) < 6, "the rest is the photo as it is seen");
    }

    [AvaloniaTheory]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("bmp")]
    [InlineData("png")]
    public async Task Every_format_is_saved_in_its_own_format(string extension)
    {
        var photo = MakePhoto(PathIn($"formato.{extension}"), 600, 400);
        var panel = await OpenPhotoAsync(photo);
        panel.Regions.Add(new RedactRegion(new Rect(100, 100, 200, 150)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn($"formato tapado.{extension}"), output);
        using var result = new MagickImage(output);
        Assert.Equal((600u, 400u), (result.Width, result.Height));
        Assert.True(Pixels(result, 120, 120, 160, 110).Max() <= 12);
    }

    [AvaloniaTheory]
    [InlineData(RedactEffect.Fill)]
    [InlineData(RedactEffect.Blur)]
    [InlineData(RedactEffect.Pixelate)]
    public async Task A_picture_with_transparency_is_covered_where_marked_and_stays_transparent_elsewhere(RedactEffect effect)
    {
        // A sticker: mostly transparent, with a red square in the middle.
        var photo = PathIn("pegatina.png");
        using (var image = new MagickImage(MagickColors.Transparent, 400, 300))
        {
            using var square = new MagickImage(MagickColors.Red, 100, 100);
            image.Composite(square, 150, 100, CompositeOperator.Over);
            image.Write(photo);
        }
        var panel = await OpenPhotoAsync(photo);
        panel.Effect = effect;
        panel.Regions.Add(new RedactRegion(new Rect(100, 50, 200, 200)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = new MagickImage(Assert.Single(panel.Outputs));
        Assert.True(result.HasAlpha);
        var inside = result.GetPixels().ToByteArray(110, 60, 180, 180, PixelMapping.RGBA)!;
        if (effect == RedactEffect.Fill)
            Assert.All(Enumerable.Range(0, inside.Length / 4), i => Assert.Equal(255, inside[i * 4 + 3]));   // opaque black over the sticker
        var outside = result.GetPixels().ToByteArray(0, 0, 90, 300, PixelMapping.RGBA)!;
        Assert.All(Enumerable.Range(0, outside.Length / 4), i => Assert.Equal(0, outside[i * 4 + 3]));      // the rest is still transparent
    }

    [AvaloniaFact]
    public async Task A_name_that_is_taken_gets_a_number_and_the_original_is_never_touched()
    {
        var photo = MakePhoto(PathIn("foto.jpg"), 600, 400);
        var before = Hash(photo);
        foreach (var expected in new[] { "foto tapado.jpg", "foto tapado 2.jpg", "foto tapado 3.jpg" })
        {
            var panel = await OpenPhotoAsync(photo);
            panel.Regions.Add(new RedactRegion(new Rect(50, 50, 100, 100)));
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            Assert.Equal(PathIn(expected), Assert.Single(panel.Outputs));
        }
        Assert.Equal(before, Hash(photo));
    }

    [AvaloniaFact]
    public async Task A_failure_leaves_no_half_written_file_and_says_what_went_wrong()
    {
        var photo = MakePhoto(PathIn("foto.jpg"), 600, 400);
        var panel = await OpenPhotoAsync(photo);
        panel.Regions.Add(new RedactRegion(new Rect(50, 50, 100, 100)));
        File.Delete(photo);                                                 // gone between loading and Apply

        await panel.RunAsync();

        Assert.False(panel.IsDone);
        Assert.True(panel.HasError);
        Assert.False(File.Exists(PathIn("foto tapado.jpg")));
    }

    [AvaloniaFact]
    public async Task Detecting_text_marks_each_phrase_once_and_says_what_it_did()
    {
        var picture = MakeTextPicture("carta.png");
        var panel = await OpenPhotoAsync(picture);
        Assert.True(panel.CanDetect);

        var detecting = panel.DetectTextAsync();
        Assert.True(panel.IsDetecting);                                     // the indicator is on …
        Assert.False(panel.CanDetect);                                      // … and the buttons are off while it works
        await detecting;
        Assert.False(panel.IsDetecting);
        Assert.True(panel.CanDetect);
        if (panel.Regions.Count == 0)
        {
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }

        Assert.InRange(panel.Regions.Count, 3, 8);
        Assert.All(panel.Regions, region => Assert.True(region.IsAuto && region.Source == RedactRegionSource.Text && region.IsWholeVideo));
        var rects = panel.Regions.Select(region => region.Rect).ToList();
        Assert.True(InkCovered(picture, rects, out var withoutInk) >= 0.97);
        Assert.Equal(0, withoutInk);
        Assert.Equal(panel.Context.Format("panel.redactImage.found", rects.Count), panel.DetectionNote);

        // The same detection again finds the same things: nothing is added.
        await panel.DetectTextAsync();
        Assert.Equal(rects.Count, panel.Regions.Count);
        Assert.Equal(panel.Context["panel.redactImage.already"], panel.DetectionNote);

        // A picture without faces: nothing added, and it says so.
        await panel.DetectFacesAsync();
        Assert.Equal(rects.Count, panel.Regions.Count);
        Assert.Equal(panel.Context["panel.redactImage.none_faces"], panel.DetectionNote);

        // "Clear all" takes everything, the detected areas and the ones drawn by hand.
        var drawn = new RedactRegion(new Rect(700, 400, 100, 60));
        panel.Regions.Add(drawn);
        panel.SelectedRegion = drawn;
        panel.ClearRegions();
        Assert.Empty(panel.Regions);
        Assert.Null(panel.SelectedRegion);
        Assert.False(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task Detection_on_a_large_photo_is_scaled_back_to_the_photo()
    {
        // 2400 × 1200: the detector reads a copy of 2000 px, so what it finds has to be scaled back by 1.2.
        var small = MakeTextPicture("letrero.png");
        var large = PathIn("letrero grande.png");
        using (var image = new MagickImage(small))
        {
            image.Resize(new MagickGeometry(2400, 1200) { IgnoreAspectRatio = true });
            image.Write(large);
        }
        var panel = await OpenPhotoAsync(large);

        await panel.DetectTextAsync();

        if (panel.Regions.Count == 0)
        {
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }
        Assert.Equal((2400d, 1200d), (panel.SourceWidth, panel.SourceHeight));
        var rects = panel.Regions.Select(region => region.Rect).ToList();
        Assert.True(InkCovered(large, rects, out var withoutInk) >= 0.97);
        Assert.Equal(0, withoutInk);
    }

    [AvaloniaFact]
    public async Task Detection_that_cannot_run_ends_as_a_note_not_an_error()
    {
        var panel = await OpenPhotoAsync(MakePhoto(PathIn("foto.jpg"), 600, 400));
        File.Delete(panel.Files[0]);                                        // the detectors cannot read it any more

        await panel.DetectTextAsync();

        Assert.False(panel.IsDetecting);
        Assert.Empty(panel.Regions);
        Assert.NotNull(panel.DetectionNote);
        Assert.StartsWith("No se pudo detectar automáticamente", panel.DetectionNote);
        Assert.EndsWith("Márcalo a mano.", panel.DetectionNote);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Every_text_of_both_panels_exists_in_every_language(string language)
    {
        var photo = MakePhoto(PathIn("foto.jpg"), 300, 200);
        var image = new RedactImagePanel(ContextIn(language, photo));
        var video = new RedactVideoPanel(ContextIn(language, PathIn("no hay.mp4")));
        try
        {
            foreach (var panel in new RedactPanelBase[] { image, video })
            {
                string[] texts =
                [
                    panel.DetectFacesText, panel.DetectTextText, panel.ClearText, panel.DetectingText, panel.EffectText, panel.FillText,
                    panel.BlurText, panel.PixelateText, panel.ColorText, panel.StrengthText, panel.BlockSizeText,
                ];
                Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text) || text.StartsWith("panel.", StringComparison.Ordinal), text));
                foreach (var key in new[] { "found", "none_faces", "none_text", "already", "detect_failed", "reason_unavailable", "reason_language",
                             "reason_faces", "reason_timeout", "reason_failed", "loading", "hint" })
                {
                    var full = $"panel.{panel.Id}.{key}";
                    Assert.NotEqual(full, panel.Context[full]);
                }
            }
            foreach (var key in new[] { "panel.redactImage.strip", "panel.redactImage.strip_hint", "panel.redactVideo.no_video", "panel.redactVideo.frame",
                         "panel.redactVideo.from", "panel.redactVideo.to", "panel.redactVideo.whole" })
                Assert.NotEqual(key, image.Context[key]);
        }
        finally
        {
            image.Dispose();
            video.Dispose();
        }
    }

    [AvaloniaFact]
    public void Both_panels_are_registered_and_the_video_one_needs_ffmpeg()
    {
        TestServices.EnsureInitialized("es");
        var loc = Filee.App.Services.AppHost.Get<Filee.Core.Localization.ILocalizer>();
        Assert.True(PanelRegistry.Has("redactImage"));
        Assert.True(PanelRegistry.Has("redactVideo"));
        Assert.Null(PanelRegistry.Unavailable("redactImage", loc));
        Assert.Equal(MediaTools.Locate() is null, PanelRegistry.Unavailable("redactVideo", loc) is not null);
    }

    // ───────── The video panel ─────────

    private Task<string?> MakeVideoAsync(string name = "clip.mp4", string size = "320x240") =>
        MakeMediaAsync(PathIn(name), "-f", "lavfi", "-i", $"testsrc=duration=3:size={size}:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:v", "libx264", "-c:a", "aac", "-shortest");

    private async Task<RedactVideoPanel> OpenVideoAsync(string file)
    {
        var panel = new RedactVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    /// <summary>A frame of the video as the panel's own FFmpeg reads it, as a Magick image to measure.</summary>
    private async Task<MagickImage> FrameOfAsync(string video, double seconds)
    {
        using var bitmap = await MediaTools.Locate()!.FrameAsync(video, seconds, 0);
        var png = PathIn($"fotograma-{Guid.NewGuid():N}.png");
        using (var stream = File.Create(png))
            bitmap.Save(stream, new PngBitmapEncoderOptions());
        return new MagickImage(png);
    }

    private const string NoFfmpeg = "FFmpeg is not installed next to the tests (engines\\ffmpeg).";

    [AvaloniaFact]
    public async Task The_video_panel_renders_a_frame_with_the_times_of_the_selected_region()
    {
        var video = await MakeVideoAsync("paseo.mp4", "960x540");
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        Assert.Equal((960d, 540d), (panel.SourceWidth, panel.SourceHeight));
        Assert.InRange(panel.Duration, 2.9, 3.2);
        Assert.NotNull(panel.Preview);
        panel.Regions.Add(new RedactRegion(new Rect(600, 300, 260, 160), RedactRegionSource.Faces));
        var chosen = new RedactRegion(new Rect(120, 90, 340, 190)) { Start = 1, End = 2 };
        panel.Regions.Add(chosen);
        panel.SelectedRegion = chosen;
        Assert.True(panel.HasSelection);
        Assert.False(panel.SelectedWholeVideo);
        Assert.Equal("0:01.00", panel.SelectedStartText);
        Assert.Equal("0:02.00", panel.SelectedEndText);

        Render(panel, "redactvideo");

        Assert.Equal("Tapar", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_video_panel_renders_with_nothing_selected_and_with_a_blur_in_every_language()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        foreach (var language in new[] { "en", "es", "ko", "zh-CN" })
        {
            var panel = new RedactVideoPanel(ContextIn(language, video));
            await WaitAsync(() => !panel.IsLoading);
            panel.Effect = RedactEffect.Pixelate;
            panel.Regions.Add(new RedactRegion(new Rect(40, 30, 120, 80)) { Start = 0.5 });
            panel.SelectedRegion = panel.Regions[0];
            Render(panel, $"redactvideo-{language}");
        }
    }

    [AvaloniaFact]
    public async Task The_photo_panel_fits_in_every_language_with_a_detection_note()
    {
        var picture = MakeTextPicture("rotulo.png", 900, 500);
        foreach (var language in new[] { "en", "es", "ko", "zh-CN" })
        {
            var panel = new RedactImagePanel(ContextIn(language, picture));
            await WaitAsync(() => !panel.IsLoading);
            await panel.DetectTextAsync();
            panel.Effect = RedactEffect.Blur;
            Render(panel, $"redactimage-{language}");
        }
    }

    [AvaloniaFact]
    public async Task A_file_without_video_says_so()
    {
        var sound = await MakeMediaAsync(PathIn("solo sonido.m4a"), "-f", "lavfi", "-i", "sine=frequency=440:duration=1");
        if (sound is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactVideoPanel(Context(sound));
        await WaitAsync(() => !panel.IsLoading);

        Assert.Equal(panel.Context["panel.redactVideo.no_video"], panel.LoadError);
        Assert.False(panel.CanApply);
        Assert.False(panel.CanDetect);
    }

    [AvaloniaFact]
    public async Task The_slider_shows_the_frame_it_points_at_and_a_drag_does_not_decode_every_step()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        var first = panel.Preview;
        Assert.Equal(1, panel.FramePosition, 3);                            // a little in, not the black first frame

        panel.FramePosition = 2.5;
        await WaitAsync(() => !ReferenceEquals(panel.Preview, first));
        var shown = panel.Preview;

        // A drag of the slider: only the last position is decoded. Even the very end gives a frame.
        panel.FramePosition = 0.2;
        panel.FramePosition = 0.4;
        panel.FramePosition = 3.0;
        await WaitAsync(() => !ReferenceEquals(panel.Preview, shown));
        Assert.Null(panel.FrameError);
        Assert.Equal("0:03.00", panel.FrameText);
    }

    [AvaloniaFact]
    public async Task The_times_of_a_region_are_edited_in_the_panel_and_kept_inside_the_video()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        Assert.False(panel.HasSelection);
        var region = new RedactRegion(new Rect(10, 10, 50, 50));
        panel.Regions.Add(region);

        panel.SelectedRegion = region;
        Assert.True(panel.HasSelection);
        Assert.True(panel.SelectedWholeVideo);                              // a new region covers the whole video

        panel.FramePosition = 2;
        panel.SelectedWholeVideo = false;                                   // "from here to the end"
        Assert.Equal(2, region.Start);
        Assert.Equal(panel.Duration, region.End);

        panel.CommitSelectedStart("0:01.5");
        panel.CommitSelectedEnd("0:02.25");
        Assert.Equal((1.5, 2.25), (region.Start, region.End));
        Assert.Equal("0:01.50", panel.SelectedStartText);

        panel.CommitSelectedEnd("99");                                      // beyond the end of the video
        Assert.Equal(panel.Duration, region.End);
        panel.CommitSelectedStart("not a time");                            // unreadable: the old value stays
        Assert.Equal(1.5, region.Start);
        panel.CommitSelectedStart("5");                                     // after the end of the region
        Assert.True(region.Start < region.End);

        panel.SelectedWholeVideo = true;
        Assert.True(region.IsWholeVideo);
        panel.SelectedRegion = null;
        Assert.False(panel.HasSelection);
    }

    [AvaloniaFact]
    public async Task Detecting_text_on_a_frame_marks_it_for_the_whole_video_in_the_pixels_of_the_video()
    {
        // 2400 × 1200: the detector reads a frame of 1920 px, so what it finds is scaled back by 1.25.
        var rotulo = MakeTextPicture("rotulo.png");
        var video = await MakeMediaAsync(PathIn("rotulo.mp4"), "-loop", "1", "-framerate", "25", "-t", "2", "-i", rotulo, "-vf", "scale=2400:1200",
            "-c:v", "libx264", "-pix_fmt", "yuv420p");
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        Assert.Equal((2400d, 1200d), (panel.SourceWidth, panel.SourceHeight));

        await panel.DetectTextAsync();

        if (panel.Regions.Count == 0)
        {
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }
        Assert.All(panel.Regions, region => Assert.True(region.IsAuto && region.IsWholeVideo));
        using var frame = await FrameOfAsync(video, 1);
        var png = PathIn("fotograma-rotulo.png");
        frame.Write(png);
        Assert.True(InkCovered(png, panel.Regions.Select(region => region.Rect).ToList(), out var withoutInk) >= 0.95);
        Assert.Equal(0, withoutInk);

        await panel.DetectFacesAsync();                                     // no faces on a sign
        Assert.Equal(panel.Context["panel.redactVideo.none_faces"], panel.DetectionNote);
    }

    [AvaloniaFact]
    public async Task A_flat_cover_over_a_stretch_of_the_video_blackens_only_that_stretch()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var before = Hash(video);
        var original = await media.ProbeAsync(video);
        var panel = await OpenVideoAsync(video);
        Assert.False(panel.CanApply);                                       // nothing to cover yet

        panel.Regions.Add(new RedactRegion(new Rect(40, 40, 160, 100)) { Start = 1, End = 2 });
        Assert.True(panel.CanApply);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("clip tapado.mp4"), output);
        Assert.Equal(before, Hash(video));                                  // the original is untouched
        var probe = await media.ProbeAsync(output);
        Assert.InRange(probe.Duration, original.Duration - 0.2, original.Duration + 0.2);
        Assert.True(probe.HasAudio, "the audio is kept");
        Assert.Equal(original.AudioCodec, probe.AudioCodec);                // copied, not re-encoded
        Assert.Equal((320, 240), (probe.Width, probe.Height));

        using var covered = await FrameOfAsync(output, 1.5);
        using var uncovered = await FrameOfAsync(video, 1.5);
        Assert.True(Pixels(covered, 48, 48, 144, 84).Max() <= 40, "black inside the region at 1.5 s");
        Assert.True(Difference(uncovered, covered, 0, 0, 320, 36) < 5, "the rest of the frame is as it was (above)");
        Assert.True(Difference(uncovered, covered, 0, 150, 320, 90) < 5, "the rest of the frame is as it was (below)");

        using var early = await FrameOfAsync(output, 0.5);
        using var earlyOriginal = await FrameOfAsync(video, 0.5);
        Assert.True(Difference(earlyOriginal, early, 0, 0, 320, 240) < 5, "the whole frame is as it was at 0.5 s");
        using var late = await FrameOfAsync(output, 2.6);
        using var lateOriginal = await FrameOfAsync(video, 2.6);
        Assert.True(Difference(lateOriginal, late, 0, 0, 320, 240) < 5, "and at 2.6 s");
    }

    [AvaloniaFact]
    public async Task Two_regions_with_their_own_times_cover_each_their_own_stretch()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        panel.FillColor = RedactEffects.Swatches[0];
        panel.Regions.Add(new RedactRegion(new Rect(20, 20, 100, 80)) { Start = 1, End = 2 });
        panel.Regions.Add(new RedactRegion(new Rect(200, 140, 100, 80)));                 // the whole video

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        using var early = await FrameOfAsync(output, 0.5);
        using var earlyOriginal = await FrameOfAsync(video, 0.5);
        Assert.True(Pixels(early, 208, 148, 84, 64).Max() <= 40, "the second region from the start");
        Assert.True(Difference(earlyOriginal, early, 20, 20, 100, 80) < 6, "the first one has not started");
        using var middle = await FrameOfAsync(output, 1.5);
        Assert.True(Pixels(middle, 28, 28, 84, 64).Max() <= 40, "the first region at 1.5 s");
        Assert.True(Pixels(middle, 208, 148, 84, 64).Max() <= 40, "the second one still");
        using var late = await FrameOfAsync(output, 2.7);
        using var lateOriginal = await FrameOfAsync(video, 2.7);
        Assert.True(Difference(lateOriginal, late, 20, 20, 100, 80) < 6, "the first one has ended");
        Assert.True(Pixels(late, 208, 148, 84, 64).Max() <= 40, "the second one never ends");
    }

    [AvaloniaTheory]
    [InlineData(RedactEffect.Blur)]
    [InlineData(RedactEffect.Pixelate)]
    public async Task Blur_and_pixelation_change_the_picture_inside_the_region_only(RedactEffect effect)
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        panel.Effect = effect;
        var area = new Rect(40, 20, 160, 100);
        panel.Regions.Add(new RedactRegion(area) { Start = 1, End = 2 });

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        using var original = await FrameOfAsync(video, 1.5);
        using var covered = await FrameOfAsync(output, 1.5);
        Assert.True(Difference(original, covered, 40, 20, 160, 100) > 8, "the region changed");
        Assert.True(Difference(original, covered, 0, 140, 320, 100) < 5, "below it nothing did");
        Assert.True(Difference(original, covered, 210, 0, 110, 140) < 5, "to its right neither");
        if (effect == RedactEffect.Blur)
        {
            Assert.True(Roughness(covered, 40, 20, 160, 100) < Roughness(original, 40, 20, 160, 100) * 0.3, "blurred: smooth");
        }
        else
        {
            // Pixelated: the first block is one flat colour (4 blocks across 160 px, 3 down 100 px: 40 x 33 px each).
            Assert.True(Detail(covered, 50, 30, 12, 12) < 8, "the block is flat");
            Assert.True(Detail(covered, 40, 20, 160, 100) > 8, "but the region is not");
        }
        using var before = await FrameOfAsync(output, 0.5);
        using var beforeOriginal = await FrameOfAsync(video, 0.5);
        Assert.True(Difference(beforeOriginal, before, 0, 0, 320, 240) < 5, "outside its time it is untouched");
    }

    [AvaloniaFact]
    public async Task A_phone_video_stored_turned_is_covered_where_it_is_seen()
    {
        var flat = await MakeVideoAsync("horizontal.mp4");
        if (flat is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        // The pixels are 320 × 240 and the file says "show it turned a quarter": 240 × 320 on screen, like a phone's portrait video.
        string? phone;
        try
        {
            phone = await MakeMediaAsync(PathIn("movil.mp4"), "-display_rotation", "90", "-i", flat, "-c", "copy");
        }
        catch (PanelException ex)
        {
            Assert.Skip("This FFmpeg cannot write a rotation: " + ex.Message);
            return;
        }
        var media = MediaTools.Locate()!;
        var turned = await media.ProbeAsync(phone!);
        Assert.Equal((240, 320), (turned.Width, turned.Height));
        var panel = await OpenVideoAsync(phone!);
        Assert.Equal((240d, 320d), (panel.SourceWidth, panel.SourceHeight));
        panel.Regions.Add(new RedactRegion(new Rect(40, 200, 120, 80)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var seen = await media.ProbeAsync(output);
        Assert.Equal((240, 320), (seen.Width, seen.Height));
        using var covered = await FrameOfAsync(output, 1);
        using var uncovered = await FrameOfAsync(phone!, 1);
        Assert.Equal((240u, 320u), (covered.Width, covered.Height));
        Assert.True(Pixels(covered, 48, 208, 104, 64).Max() <= 40, "black where it was drawn");
        Assert.True(Difference(uncovered, covered, 40, 200, 120, 80) > 20, "and that place was not black before");
        Assert.True(Difference(uncovered, covered, 0, 0, 240, 190) < 6, "the rest is as it is seen");
    }

    [AvaloniaTheory]
    [InlineData(RedactEffect.Blur)]
    [InlineData(RedactEffect.Pixelate)]
    public async Task Text_in_a_video_stays_unreadable_with_the_default_blur_and_pixelation(RedactEffect effect)
    {
        var picture = MakeTextPicture("secreto video.png", 1000, 500, "Contraseña: Mandarina2026", "IBAN ES91 2100 0418 4502", "Persona de Ejemplo");
        var video = await MakeMediaAsync(PathIn("secreto.mp4"), "-loop", "1", "-framerate", "25", "-t", "2", "-i", picture, "-c:v", "libx264", "-pix_fmt", "yuv420p");
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        await panel.DetectTextAsync();
        if (panel.Regions.Count == 0)
        {
            Assert.Skip("This PC has no Windows text recognition: " + panel.DetectionNote);
            return;
        }
        panel.Effect = effect;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var frame = await FrameOfAsync(Assert.Single(panel.Outputs), 1);
        var png = PathIn("fotograma-tapado.png");
        frame.Write(png);
        var again = await LocalDetector.FindTextAsync(png);
        Assert.True(again.Succeeded, again.Reason);
        Assert.Empty(again);
    }

    [AvaloniaFact]
    public async Task A_video_with_many_regions_goes_through_a_filter_file()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        // 400 small squares over the frame: too long a graph for the command line.
        for (var i = 0; i < 400; i++)
            panel.Regions.Add(new RedactRegion(new Rect(i % 32 * 10, i / 32 * 18, 8, 8)));
        var graph = RedactVideoPanel.BuildGraph(panel.Regions.ToList(), 320, 240, 3, RedactEffect.Fill, Colors.Black, 70, 48);
        Assert.True(graph.Length > 20000, $"{graph.Length} characters");

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var frame = await FrameOfAsync(Assert.Single(panel.Outputs), 1);
        Assert.True(Pixels(frame, 1, 1, 6, 6).Max() <= 40, "the first square");
        Assert.True(Pixels(frame, 151, 217, 6, 6).Max() <= 40, "the last one");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Path.GetTempPath(), "Mandarina", "panels"), "*.txt"));   // the filter file is gone
    }

    [AvaloniaFact]
    public async Task A_name_that_is_taken_gets_a_number_and_a_failure_deletes_the_partial_file()
    {
        var video = await MakeVideoAsync();
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        foreach (var expected in new[] { "clip tapado.mp4", "clip tapado 2.mp4" })
        {
            var panel = await OpenVideoAsync(video);
            panel.Regions.Add(new RedactRegion(new Rect(40, 40, 100, 60)));
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            Assert.Equal(PathIn(expected), Assert.Single(panel.Outputs));
        }

        var broken = await OpenVideoAsync(video);
        broken.Regions.Add(new RedactRegion(new Rect(40, 40, 100, 60)));
        File.Delete(video);                                                 // gone between loading and Apply
        await broken.RunAsync();
        Assert.False(broken.IsDone);
        Assert.True(broken.HasError);
        Assert.False(File.Exists(PathIn("clip tapado 3.mp4")));
    }

    [Fact]
    public void The_filter_graph_has_one_step_per_region_on_whole_even_pixels_inside_the_frame()
    {
        var regions = new List<RedactRegion>
        {
            new(new Rect(10.4, 21.7, 100.2, 50.9)) { Start = 1, End = 2 },
            new(new Rect(-20, -5, 5000, 5000)),                              // far beyond the frame: all of it
            new(new Rect(900, 900, 10, 10)),                                 // outside: left out
            new(new Rect(100, 100, 40, 40)) { Start = 0.5 },
            new(new Rect(150, 150, 40, 40)) { End = 2.5 },
            new(new Rect(160, 160, 30, 30)) { Start = 0, End = 3 },          // all of a 3 s video
        };

        var graph = RedactVideoPanel.BuildGraph(regions, 320, 240, 3, RedactEffect.Fill, Color.FromRgb(0xFF, 0x52, 0x00), 70, 48);

        Assert.StartsWith("[0:v]format=yuv420p[v0];", graph);
        Assert.Contains("[v0]drawbox=x=10:y=20:w=102:h=54:color=0xFF5200@1:t=fill:enable='between(t,1,2)'[v1]", graph);
        Assert.Contains("[v1]drawbox=x=0:y=0:w=320:h=240:color=0xFF5200@1:t=fill[v2]", graph);
        Assert.Contains("[v2]drawbox=x=100:y=100:w=40:h=40:color=0xFF5200@1:t=fill:enable='gte(t,0.5)'[v3]", graph);
        Assert.Contains("[v3]drawbox=x=150:y=150:w=40:h=40:color=0xFF5200@1:t=fill:enable='lte(t,2.5)'[v4]", graph);
        Assert.Contains("[v4]drawbox=x=160:y=160:w=30:h=30:color=0xFF5200@1:t=fill[v5]", graph);
        Assert.EndsWith("[v5]null[vout]", graph);

        Assert.Equal("", RedactVideoPanel.BuildGraph([regions[2]], 320, 240, 3, RedactEffect.Fill, Colors.Black, 70, 48));
        Assert.Equal("", RedactVideoPanel.BuildGraph([], 320, 240, 3, RedactEffect.Fill, Colors.Black, 70, 48));
    }

    [Fact]
    public void A_blur_or_a_pixelation_is_a_cropped_copy_laid_back_over_the_picture()
    {
        List<RedactRegion> regions = [new(new Rect(40, 20, 160, 100)) { Start = 1, End = 2 }];

        var blur = RedactVideoPanel.BuildGraph(regions, 320, 240, 3, RedactEffect.Blur, Colors.Black, 25, 8);
        var sigma = RedactEffects.BlurSigma(25, 160, 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("49.7", sigma);                                          // 100 x (0.40 + 0.40 x 24 / 99)
        Assert.Contains("[v0]split[a1][b1];[b1]crop=160:100:40:20,gblur=sigma=" + sigma + ":steps=3[r1];[a1][r1]overlay=x=40:y=20:enable='between(t,1,2)'[v1]", blur);

        var pixelate = RedactVideoPanel.BuildGraph(regions, 320, 240, 3, RedactEffect.Pixelate, Colors.Black, 25, 8);
        Assert.Equal(44, RedactEffects.BlockSize(8, 160, 100));               // 100 x 0.44: four blocks across 160 px, three down 100 px
        Assert.Contains("[b1]crop=160:100:40:20,scale=4:3:flags=area,scale=160:100:flags=neighbor[r1]", pixelate);
        Assert.Contains("overlay=x=40:y=20:enable='between(t,1,2)'[v1]", pixelate);
    }

    [Theory]
    [InlineData(".mp4", new[] { "aac" }, true)]
    [InlineData(".mp4", new[] { "aac", "ac3" }, true)]
    [InlineData(".mov", new[] { "mp3" }, true)]
    [InlineData(".mp4", new[] { "vorbis" }, false)]
    [InlineData(".mp4", new[] { "aac", "opus" }, false)]
    [InlineData(".m4v", new[] { "ac3" }, false)]
    [InlineData(".m4v", new[] { "aac" }, true)]
    [InlineData(".mkv", new[] { "vorbis", "opus" }, true)]
    [InlineData(".mp4", new string[0], false)]                               // unknown: encoded, which always works
    public void The_audio_is_copied_when_the_container_takes_it_as_it_is(string extension, string[] codecs, bool copied)
    {
        var arguments = RedactVideoPanel.AudioArguments(extension, codecs);

        Assert.Equal(copied ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"], arguments);
    }

    [AvaloniaFact]
    public async Task A_matroska_video_keeps_its_audio_untouched_and_its_container()
    {
        var video = await MakeMediaAsync(PathIn("peli.mkv"), "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=25", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=2", "-c:v", "libx264", "-c:a", "libvorbis", "-shortest");
        if (video is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenVideoAsync(video);
        panel.Regions.Add(new RedactRegion(new Rect(40, 40, 100, 60)));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("peli tapado.mkv"), output);
        var probe = await MediaTools.Locate()!.ProbeAsync(output);
        Assert.Equal("vorbis", probe.AudioCodec);
    }
}
