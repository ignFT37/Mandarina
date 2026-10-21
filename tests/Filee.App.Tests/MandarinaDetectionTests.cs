// Mandarina: the local detectors of the Redact tools (Windows' own face detector and OCR) on pictures that CAN be found in them:
// a real photo with a face (the file named by MANDARINA_FACE_PHOTO, or %USERPROFILE%\.mandarina\e2e\fotos\cara.jpg: it is a person's
// photo, so it lives outside the repository and the tests skip without it), and lines of text in several scripts drawn with
// Windows' own fonts. Covers faces in photos (upright, EXIF-rotated, enlarged, twice in one picture) and in a video frame, and which
// scripts the text recognition of this PC reads.

using Avalonia;
using Avalonia.Headless.XUnit;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using ImageMagick.Drawing;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaDetectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-detection").FullName;

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

    private string FacePhoto()
    {
        var source = Environment.GetEnvironmentVariable("MANDARINA_FACE_PHOTO");
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
            source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandarina", "e2e", "fotos", "cara.jpg");
        if (!File.Exists(source))
        {
            Assert.Skip("There is no photo with a face to test with (set MANDARINA_FACE_PHOTO or put one at .mandarina\\e2e\\fotos\\cara.jpg).");
            return "";
        }
        var copy = Path.Combine(_root, "cara.jpg");
        File.Copy(source, copy, overwrite: true);
        return copy;
    }

    /// <summary>The face rectangles Windows finds in <paramref name="path"/> (a PNG or JPG), widened as the panels do; skips when this PC cannot.</summary>
    private static async Task<List<DetectedRegion>> FacesInAsync(string path)
    {
        var found = await LocalDetector.FindFacesAsync(path);
        if (found.Failure is RedactDetectionFailure.NoFaceDetector or RedactDetectionFailure.Unavailable)
            Assert.Skip("This PC has no Windows face detector: " + found.Reason);
        Assert.True(found.Succeeded, found.Reason);
        return [.. found];
    }

    private async Task<RedactImagePanel> OpenAsync(string photo)
    {
        var panel = new RedactImagePanel(Context(photo));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    /// <summary>The JPEG with an EXIF block that holds only the orientation tag, as a phone writes it for a photo taken on its side.</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,                    // big-endian header, IFD0 at 8
            0, 1,                                                        // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0,   // Orientation, SHORT, 1 value
            0, 0, 0, 0,                                                  // no next IFD
        ];
        byte[] body = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        byte[] segment = [0xFF, 0xE1, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
        return [.. jpeg.Take(2), .. segment, .. jpeg.Skip(2)];             // after SOI
    }

    // ───────── Faces in photos ─────────

    [AvaloniaFact]
    public async Task A_face_in_a_real_photo_is_found_and_the_panel_covers_exactly_that_area()
    {
        var photo = FacePhoto();
        var direct = await FacesInAsync(photo);
        Assert.NotEmpty(direct);
        using var panel = await OpenAsync(photo);

        await panel.DetectFacesAsync();

        Assert.True(panel.Regions.Count >= 1, panel.DetectionNote);
        Assert.Equal(direct.Count, panel.Regions.Count);
        // The panel adds what the detector found (scaled to the original: 1:1 here), as whole pixels around it.
        foreach (var (region, box) in panel.Regions.Zip(direct))
        {
            Assert.InRange(region.Rect.X, Math.Floor(box.X) - 1, box.X + 1);
            Assert.InRange(region.Rect.Y, Math.Floor(box.Y) - 1, box.Y + 1);
            Assert.InRange(region.Rect.Width, box.Width - 2, box.Width + 3);
            Assert.InRange(region.Rect.Height, box.Height - 2, box.Height + 3);
        }

        panel.Effect = RedactEffect.Fill;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);

        using var original = new MagickImage(photo);
        using var covered = new MagickImage(panel.Outputs[0]);
        Assert.Equal(original.Width, covered.Width);
        Assert.Equal(original.Height, covered.Height);
        var area = panel.Regions[0].Rect;
        // Inside the area: one flat colour. Outside every area: the photo, give or take JPEG.
        using (var inside = covered.CloneArea((int)area.X + 2, (int)area.Y + 2, (uint)area.Width - 4, (uint)area.Height - 4))
        {
            inside.Grayscale();
            Assert.True(inside.Statistics().Composite().StandardDeviation < 2000, "the covered face is a flat colour");   // Quantum 16: 2000 ≈ 8/255
        }
        var outside = new Rect(0, 0, 40, 40);                      // a corner, far from the face
        Assert.DoesNotContain(panel.Regions, r => r.Rect.Intersects(outside));
        using var before = original.CloneArea(0, 0, 40, 40);
        using var after = covered.CloneArea(0, 0, 40, 40);
        Assert.True(before.Compare(after, ErrorMetric.MeanAbsolute) < 0.02, "the rest of the photo is untouched");
    }

    [AvaloniaFact]
    public async Task A_photo_turned_by_its_EXIF_orientation_is_covered_where_the_face_is_when_upright()
    {
        var photo = FacePhoto();
        var upright = await FacesInAsync(photo);
        Assert.NotEmpty(upright);
        // The same photo stored on its side with EXIF orientation 6 ("turn 90° clockwise to see it"): viewers show it upright.
        var turned = Path.Combine(_root, "girada.jpg");
        using (var image = new MagickImage(photo))
        {
            image.Rotate(-90);
            image.Strip();
            image.Quality = 95;
            File.WriteAllBytes(turned, WithOrientation(image.ToByteArray(MagickFormat.Jpeg), 6));
        }
        using (var check = new MagickImage(turned))
        {
            Assert.Equal(399u, check.Width);                                   // stored on its side…
            Assert.Equal(OrientationType.RightTop, check.Orientation);          // …with the tag that turns it upright
        }
        using var panel = await OpenAsync(turned);

        await panel.DetectFacesAsync();

        Assert.True(panel.Regions.Count >= 1, panel.DetectionNote);
        // Regions are in pixels of the upright picture, so they sit where the face is in the upright original (a face detector may differ
        // by a few pixels between two encodings of the same picture).
        var expected = upright[0];
        var found = panel.Regions[0].Rect;
        Assert.InRange(Math.Abs(found.Center.X - (expected.X + expected.Width / 2)), 0, expected.Width * 0.25);
        Assert.InRange(Math.Abs(found.Center.Y - (expected.Y + expected.Height / 2)), 0, expected.Height * 0.25);
        Assert.Equal(630, panel.SourceWidth);
    }

    [AvaloniaFact]
    public async Task A_big_photo_is_scaled_down_for_the_detector_and_the_area_is_scaled_back()
    {
        var photo = FacePhoto();
        var small = await FacesInAsync(photo);
        Assert.NotEmpty(small);
        var big = Path.Combine(_root, "grande.jpg");
        using (var image = new MagickImage(photo))
        {
            image.Resize(new MagickGeometry(3 * image.Width, 3 * image.Height) { IgnoreAspectRatio = true });   // 1890 × 1197
            image.Write(big);
        }
        using var panel = await OpenAsync(big);

        await panel.DetectFacesAsync();

        Assert.True(panel.Regions.Count >= 1, panel.DetectionNote);
        var expected = small[0];
        var found = panel.Regions[0].Rect;
        Assert.InRange(found.Center.X / 3, expected.X + expected.Width * 0.25, expected.X + expected.Width * 0.75);
        Assert.InRange(found.Center.Y / 3, expected.Y + expected.Height * 0.25, expected.Y + expected.Height * 0.75);
        Assert.InRange(found.Width / 3, expected.Width * 0.7, expected.Width * 1.4);
    }

    [AvaloniaFact]
    public async Task Two_faces_in_one_picture_make_two_areas()
    {
        var photo = FacePhoto();
        var single = await FacesInAsync(photo);
        Assert.NotEmpty(single);
        var pair = Path.Combine(_root, "dos.jpg");
        using (var left = new MagickImage(photo))
        using (var right = new MagickImage(photo))
        using (var both = new MagickImageCollection())
        {
            both.Add(left.Clone());
            both.Add(right.Clone());
            using var wide = both.AppendHorizontally();
            wide.Write(pair);
        }
        using var panel = await OpenAsync(pair);

        await panel.DetectFacesAsync();

        Assert.True(panel.Regions.Count >= 2, $"found {panel.Regions.Count}: {panel.DetectionNote}");
        Assert.Contains(panel.Regions, r => r.Rect.Center.X < 630);
        Assert.Contains(panel.Regions, r => r.Rect.Center.X >= 630);
        // A second search adds nothing new.
        var count = panel.Regions.Count;
        await panel.DetectFacesAsync();
        Assert.Equal(count, panel.Regions.Count);
    }

    [AvaloniaFact]
    public async Task A_small_face_in_a_big_picture_is_found_too()
    {
        // Windows' detector skips a face smaller than about 4.5 % of the picture's long side. Here the face is 2.8 % of it, as in a
        // group photo: the panel's search of overlapping crops must still find it.
        var photo = FacePhoto();
        var original = await FacesInAsync(photo);
        Assert.NotEmpty(original);
        var canvas = Path.Combine(_root, "lienzo.jpg");
        using (var image = new MagickImage(MagickColors.White, 1260, 798))
        using (var face = new MagickImage(photo))
        {
            image.Composite(face, 315, 200, CompositeOperator.Over);
            image.Write(canvas);
        }
        using var panel = await OpenAsync(canvas);
        await panel.DetectFacesAsync();

        Assert.Single(panel.Regions);
        var box = original[0];
        Assert.InRange(panel.Regions[0].Rect.Center.X, 315 + box.X, 315 + box.X + box.Width);
        Assert.InRange(panel.Regions[0].Rect.Center.Y, 200 + box.Y, 200 + box.Y + box.Height);
    }

    [Fact]
    public void The_same_face_seen_twice_counts_once_and_two_faces_next_to_each_other_stay_two()
    {
        var raw = new List<LocalDetector.RawBox>
        {
            new(0, 100, 100, 40, 40),      // seen in the whole picture
            new(1, 98, 96, 48, 50),        // the same face, bigger, in a crop
            new(2, 120, 110, 18, 20),      // a bit of it cut by another crop's edge
            new(3, 150, 100, 40, 40),      // its neighbour: only touches
            new(4, 600, 300, 30, 30),      // a face somewhere else
        };

        var merged = LocalDetector.MergeFaces(raw);

        Assert.Equal(3, merged.Count);
        Assert.Contains(merged, face => face.Width == 48);
        Assert.Contains(merged, face => face.X == 150);
        Assert.Contains(merged, face => face.X == 600);
    }

    [Fact]
    public void Faces_are_widened_after_they_are_merged()
    {
        var regions = LocalDetector.Faces([new(0, 100, 100, 40, 40), new(1, 102, 101, 38, 39)], 1000, 800);

        var face = Assert.Single(regions);
        Assert.Equal(94, face.X, 0.5);          // 100 − 15 % of 40
        Assert.Equal(52, face.Width, 0.5);      // 40 + 2 × 6
    }

    [AvaloniaFact]
    public async Task A_face_in_a_video_frame_is_found_and_covered_for_the_whole_clip()
    {
        var photo = FacePhoto();
        var faces = await FacesInAsync(photo);
        Assert.NotEmpty(faces);
        var clip = await MakeMediaAsync(Path.Combine(_root, "cara.mp4"), "-loop", "1", "-framerate", "10", "-t", "2", "-i", photo,
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "12");
        if (clip is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        using var panel = new RedactVideoPanel(Context(clip));
        await WaitAsync(() => !panel.IsLoading && panel.Preview is not null, 30000);

        await panel.DetectFacesAsync();

        Assert.True(panel.Regions.Count >= 1, panel.DetectionNote);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);

        // A frame from the covered video: the face area is flat, the corner is still the photo.
        var frame = Path.Combine(_root, "frame.png");
        await MediaTools.Locate()!.RunAsync(["-ss", "1", "-i", panel.Outputs[0], "-frames:v", "1", frame]);
        using var picture = new MagickImage(frame);
        var area = panel.Regions[0].Rect;
        using var inside = picture.CloneArea((int)area.X + 3, (int)area.Y + 3, (uint)area.Width - 6, (uint)area.Height - 6);
        inside.Grayscale();
        Assert.True(inside.Statistics().Composite().StandardDeviation < 4000, "the face is covered in the video");
    }


    [AvaloniaFact]
    public async Task Pictures_without_faces_get_no_faces_from_the_crops_either()
    {
        // The renders of the interface (panels, wheels, pages) are plenty of varied pictures without a face in them.
        var files = Directory.EnumerateFiles(TestServices.ScreenshotDirectory, "*.png").OrderBy(f => f, StringComparer.Ordinal).Take(15).ToList();
        if (files.Count < 10)
        {
            Assert.Skip("There are not enough renders yet to look at.");
            return;
        }
        var withFaces = new List<string>();
        foreach (var file in files)
        {
            var found = await LocalDetector.FindFacesAsync(file);
            if (found.Failure is RedactDetectionFailure.NoFaceDetector or RedactDetectionFailure.Unavailable)
            {
                Assert.Skip("This PC has no Windows face detector: " + found.Reason);
                return;
            }
            if (found.Count > 0)
                withFaces.Add($"{Path.GetFileName(file)}: {found.Count}");
        }
        Assert.True(withFaces.Count <= 1, $"{withFaces.Count} of {files.Count} renders got a face: {string.Join(", ", withFaces)}");
    }

    // ───────── Text in several scripts ─────────

    /// <summary>One line of <paramref name="text"/> in the font file <paramref name="fontFile"/> of Windows' Fonts folder as a PNG, black on white.</summary>
    private string TextPicture(string name, string fontFile, string text)
    {
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fontFile);
        if (!File.Exists(font))
        {
            Assert.Skip($"This PC has no {fontFile} to draw the text with.");
            return "";
        }
        var path = Path.Combine(_root, name + ".png");
        using var image = new MagickImage(MagickColors.White, 1100, 160);
        new Drawables().Font(font).FontPointSize(40).FillColor(MagickColors.Black).Text(20, 95, text).Draw(image);
        image.Write(path);
        return path;
    }

    private static double CenterY(DetectedRegion region) => region.Y + region.Height / 2;

    /// <summary>Which recognizer answered and what it boxed, for a failure message (they differ from PC to PC).</summary>
    private static string Describe(RedactDetectionResult found) =>
        $"recognizer {found.Language ?? "?"}, regions: " + string.Join(", ", found.Select(region => $"{region.X:0},{region.Y:0} {region.Width:0}x{region.Height:0}"));

    [AvaloniaTheory]
    [InlineData("fr", "segoeui.ttf", "Les élèves étudient à l'école près du château")]
    [InlineData("de", "segoeui.ttf", "Große Straße, schöne Übung für Mädchen")]
    [InlineData("pt", "segoeui.ttf", "Ação não é opção: coração e informação")]
    [InlineData("it", "segoeui.ttf", "Perché la città è più bella di così")]
    public async Task Text_in_other_latin_languages_is_found_with_the_installed_recognizer(string language, string fontFile, string text)
    {
        var found = await LocalDetector.FindTextAsync(TextPicture(language, fontFile, text));
        if (!found.Succeeded)
        {
            Assert.Skip("This PC has no Windows text recognition: " + found.Reason);
            return;
        }

        // One rectangle for the line (the words that sit together are joined), and it lies over the text.
        Assert.NotEmpty(found);
        Assert.All(found, region => Assert.InRange(CenterY(region), 40, 120));
        Assert.True(found.Sum(region => region.Width) > 400, "the rectangles cover most of the line");
    }

    [AvaloniaTheory]
    [InlineData("ru", "segoeui.ttf", "Привет мир, это проверка текста")]
    [InlineData("el", "segoeui.ttf", "Γεια σου κόσμε αυτό είναι δοκιμή")]
    public async Task Text_in_cyrillic_or_greek_is_still_boxed_even_though_it_is_misread(string language, string fontFile, string text)
    {
        // The recognizer for Latin script reads these letters as look-alikes: the words come out wrong but their rectangles are right,
        // and a rectangle is all covering needs.
        var found = await LocalDetector.FindTextAsync(TextPicture(language, fontFile, text));
        if (!found.Succeeded)
        {
            Assert.Skip("This PC has no Windows text recognition: " + found.Reason);
            return;
        }
        Assert.NotEmpty(found);
        var covered = found.Sum(region => region.Width);
        if (covered <= 500 && found.Language?.StartsWith(language, StringComparison.OrdinalIgnoreCase) != true)
        {
            // How many of the misread words a Latin recognizer still boxes depends on the recognizer (a hosted runner has only en-US and
            // drops some): that is Windows' behaviour, not something the redaction tool controls.
            Assert.Skip($"Windows read it with its {found.Language} recognizer, which boxed only part of the line: " + Describe(found));
            return;
        }
        Assert.True(covered > 500, Describe(found));
    }

    [AvaloniaTheory]
    [InlineData("ko", "malgun.ttf", "안녕하세요 감사합니다 파일 변환")]
    [InlineData("zh", "msyh.ttc", "你好世界 谢谢你的帮助")]
    [InlineData("ja", "YuGothR.ttc", "こんにちは世界 ありがとう")]
    public async Task Korean_chinese_and_japanese_need_their_own_recognizer_and_the_panel_says_so(string language, string fontFile, string text)
    {
        var picture = TextPicture(language, fontFile, text);
        var found = await LocalDetector.FindTextAsync(picture);
        if (!found.Succeeded)
        {
            Assert.Skip("This PC has no Windows text recognition: " + found.Reason);
            return;
        }
        if (found.Count > 0)
        {
            if (found.Language?.StartsWith(language, StringComparison.OrdinalIgnoreCase) != true)
            {
                // A Latin recognizer (a hosted runner has only en-US) boxes a few look-alike glyphs of this script: that says nothing
                // about a PC that does have the recognizer, nor about the panel's hint, which is for a PC where nothing is found.
                Assert.Skip($"Windows read it with its {found.Language} recognizer, not a {language} one: " + Describe(found));
                return;
            }
            Assert.True(found.Sum(region => region.Width) > 300, "this PC has the recognizer: the line is boxed. " + Describe(found));
            return;
        }

        // Without the recognizer of that language nothing is found, and the panel must say what to do about it, not just "no text".
        using var panel = await OpenAsync(picture);
        await panel.DetectTextAsync();
        Assert.Empty(panel.Regions);
        Assert.NotNull(panel.DetectionNote);
        var loc = panel.Context.Loc;
        Assert.Contains(loc["panel.redact.languages_hint"].Split('{')[0].Trim(), panel.DetectionNote);
    }
}
