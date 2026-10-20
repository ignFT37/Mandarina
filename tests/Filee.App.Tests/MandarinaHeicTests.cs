// Mandarina: HEIC through everything that touches a photo. The pictures are real HEIC files (HeicSamples): the sample checked in
// next to the tests, ones written by Windows' own HEIF encoder (skipped on a PC without it), and ones assembled from the same
// HEVC picture with the structures phones write (irot together with an Exif orientation, a grid of tiles, a thumbnail and a
// depth map, an ICC profile, 10 bits…). Each is run through the panels and the conversion pipeline and the result is looked at
// (size, which corner is which colour, profile, tags, name and extension), with the original left as it was.
// libheif (inside Magick.NET) decodes them; Windows' encoder flags no colour matrix, so its saturated colours come out a few
// percent off; the checks use names of colours (red, green…), not exact values.

using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

// PDFtoImage marks its API for the platforms PDFium ships for; the tests run on desktop platforms, which are all covered.
#pragma warning disable CA1416

namespace Filee.App.Tests;

public sealed class MandarinaHeicTests : IDisposable
{
    /// <summary>PDFium is not thread-safe: the tests of this file that render a PDF take turns.</summary>
    private static readonly object Pdfium = new();

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-heic").FullName;

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

    // ───────── Samples ─────────

    /// <summary>The sample checked in, copied here under <paramref name="name"/> (the tools write beside the original).</summary>
    private string Fixture(string name = "foto.heic")
    {
        var path = Path.Combine(_root, name);
        File.Copy(HeicSamples.Fixture(), path);
        return path;
    }

    /// <summary>A picture written by Windows' encoder (made once per run), copied here. Skips on a PC that cannot write HEIC.</summary>
    private string Encoded(string key, int width, int height, int orientation = 0, HeicMetadata metadata = HeicMetadata.None, bool lookUpright = false,
        string? name = null)
    {
        if (!HeicSamples.EncoderAvailable)
            Assert.Skip("This PC cannot write HEIC (no HEIF encoder): install the HEIF Image Extensions from the Microsoft Store.");
        var cached = HeicSamples.Cached(key, path => HeicSamples.TryEncode(path, width, height, orientation, metadata, 0.8, lookUpright));
        Assert.NotNull(cached);
        var copy = Path.Combine(_root, name ?? key + ".heic");
        File.Copy(cached, copy);
        return copy;
    }

    /// <summary>The coded (landscape, markers upright) picture inside the checked-in sample.</summary>
    private static HeicSamples.Coded Donor() => HeicSamples.ReadCoded(File.ReadAllBytes(HeicSamples.Fixture()));

    /// <summary>A HEIC assembled from the sample's HEVC picture with the structure <paramref name="shape"/> adds.</summary>
    private string Built(string name, Func<HeicBuilder, HeicBuilder> shape)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, shape(HeicBuilder.Of(Donor())).ToBytes());
        return path;
    }

    /// <summary>A flat-colour 320 × 240 HEIC (8 or 10 bits, with a profile if given) made by x265 through FFmpeg; null without it.</summary>
    private async Task<string?> FlatAsync(string name, MagickColor colour, int bitDepth = 8, byte[]? icc = null)
    {
        var png = Path.Combine(_root, name + ".png");
        using (var image = new MagickImage(colour, 320, 240))
            image.Write(png);
        if (await HeicSamples.FromPngAsync(png, bitDepth, icc) is not { } bytes)
            return null;
        var path = Path.Combine(_root, name + ".heic");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    // ───────── Looking at results ─────────

    private static void AssertCorners(string path, string[] expected)
    {
        using var image = new MagickImage(path);
        Assert.Equal(expected, HeicSamples.CornerColours(image));
    }

    private static (uint Width, uint Height) SizeOf(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>True when the file starts like a JPEG (and so is not a HEIC, an AVIF or a MIFF that merely has a ".jpg" name).</summary>
    private static bool IsJpeg(string path) => File.ReadAllBytes(path) is [0xFF, 0xD8, 0xFF, ..];

    /// <summary>The orientation tag a viewer would read in <paramref name="path"/>: 1 when the file says nothing.</summary>
    private static int OrientationTag(string path)
    {
        using var image = new MagickImage(path);
        return image.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value ?? 1;
    }

    /// <summary>The share of <paramref name="path"/>'s pixels within <paramref name="tolerance"/> of <paramref name="colour"/> on every channel.</summary>
    private static double Share(string path, IMagickColor<byte> colour, int tolerance)
    {
        using var image = new MagickImage(path);
        image.Alpha(AlphaOption.Off);
        var data = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var near = 0;
        for (var i = 0; i < data.Length; i += 3)
        {
            if (Math.Abs(data[i] - colour.R) <= tolerance && Math.Abs(data[i + 1] - colour.G) <= tolerance && Math.Abs(data[i + 2] - colour.B) <= tolerance)
                near++;
        }
        return near / (double)(data.Length / 3);
    }

    private static IMagickColor<byte> Centre(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel((int)image.Width / 2, (int)image.Height / 2).ToColor()!;
    }

    private static async Task<ConversionJob> ConvertAsync(IReadOnlyList<string> files, Preset preset, string data)
    {
        // Built like AppHost builds it: with the PDF merger and the archive combiner.
        var converters = EngineRegistry.CreateAll(new EngineEnvironment(data));
        var catalog = new ConverterCatalog(converters) { Priority = new AppSettings().EnginePriority };
        await using var queue = new JobQueue(catalog, EngineRegistry.FindPdfMerger(converters),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueue>.Instance,
            EngineRegistry.FindFileCombiner(converters), EngineRegistry.FindTiffMerger(converters));
        var job = new ConversionJob(files, preset, preset.Name);
        await queue.RunAsync(job);
        return job;
    }

    private async Task<ConversionJob> ConvertAsync(IReadOnlyList<string> files, string target)
    {
        var job = await ConvertAsync(files, new Preset { TargetFormat = target }, Path.Combine(_root, "data"));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job;
    }

    /// <summary>Page 1 of a PDF as a picture.</summary>
    private string RasterOf(string pdf)
    {
        var png = Path.Combine(_root, Path.GetFileNameWithoutExtension(pdf) + "-page.png");
        lock (Pdfium)
        {
            using var stream = File.OpenRead(pdf);
            using var bitmap = PDFtoImage.Conversion.ToImage(stream, page: 0, options: new PDFtoImage.RenderOptions(Dpi: 40));
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(png, data.ToArray());
        }
        return png;
    }

    // ───────── The sample and how the pictures are stored ─────────

    [Fact]
    public void The_sample_is_a_real_heic_written_by_windows_and_reads_upright_with_its_tags()
    {
        var bytes = File.ReadAllBytes(HeicSamples.Fixture());
        Assert.True(bytes.Length <= 40 * 1024, $"the checked-in sample is {bytes.Length} bytes");
        Assert.Equal("ftypheic", Encoding.ASCII.GetString(bytes, 4, 8));

        // The picture is stored 320 × 240; an irot box makes it 240 × 320. The Exif has no orientation of its own.
        Assert.Equal((320, 240), (HeicSamples.ReadCoded(bytes).Width, HeicSamples.ReadCoded(bytes).Height));
        using var image = PanelImages.Read(HeicSamples.Fixture());
        Assert.Equal((240u, 320u), (image.Width, image.Height));
        Assert.Equal(HeicSamples.Clockwise, HeicSamples.CornerColours(image));
        Assert.Equal(OrientationType.TopLeft, image.Orientation);
        var exif = image.GetExifProfile()!;
        Assert.Equal("Mandarina Test", exif.GetValue(ExifTag.Make)!.Value);
        Assert.Equal("Synthetic Cam 1", exif.GetValue(ExifTag.Model)!.Value);
        Assert.NotNull(exif.GetValue(ExifTag.GPSLatitude));
        Assert.Null(exif.GetValue(ExifTag.Orientation));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void A_portrait_stored_sideways_with_the_orientation_comes_out_upright(int orientation)
    {
        // How a phone stores a portrait: sideways, plus the orientation that turns it. Windows writes the orientation as irot.
        var file = Encoded($"portrait-{orientation}", 480, 640, orientation, lookUpright: true);
        using var image = PanelImages.Read(file);
        Assert.Equal((480u, 640u), (image.Width, image.Height));
        Assert.Equal(HeicSamples.Upright, HeicSamples.CornerColours(image));
        Assert.Equal(OrientationType.TopLeft, image.Orientation);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void A_landscape_marked_to_be_turned_shows_as_a_portrait_with_its_corners_moved(int orientation)
    {
        var file = Encoded($"turned-{orientation}", 640, 480, orientation);
        using var image = PanelImages.Read(file);
        Assert.Equal((480u, 640u), (image.Width, image.Height));
        Assert.Equal(orientation == 6 ? HeicSamples.Clockwise : HeicSamples.CounterClockwise, HeicSamples.CornerColours(image));
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(8, 1)]
    public void An_iphone_writes_irot_and_also_the_exif_orientation_and_the_tag_must_not_turn_it_a_second_time(int orientation, int quarterTurns)
    {
        var phone = Built($"iphone-{orientation}.heic", b => b.Rotate(quarterTurns).Exif(HeicSamples.PhoneExif((ushort)orientation)));
        var expected = orientation == 6 ? HeicSamples.Clockwise : HeicSamples.CounterClockwise;
        using (var image = new MagickImage(phone))
            Assert.Equal(orientation, image.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);   // the tag is really there
        using var upright = PanelImages.Read(phone);
        Assert.Equal((240u, 320u), (upright.Width, upright.Height));
        Assert.Equal(expected, HeicSamples.CornerColours(upright));

        // Saved, the copy is upright and says so, whatever the tool.
        var jpg = Path.Combine(_root, "guardada.jpg");
        PanelImages.Save(upright, jpg);
        Assert.Equal(1, OrientationTag(jpg));
        AssertCorners(jpg, expected);
    }

    [AvaloniaFact]
    public async Task A_phone_photo_with_irot_and_an_exif_orientation_is_not_turned_twice_by_compress_or_the_conversion()
    {
        var phone = Built("iphone.heic", b => b.Rotate(3).Exif(HeicSamples.PhoneExif(6)));

        var compress = new CompressPanel(Context(phone));
        await WaitAsync(() => !compress.IsLoading);
        await compress.RunAsync();
        Assert.True(compress.IsDone, compress.Status);
        var compressed = Assert.Single(compress.Outputs);
        Assert.Equal(Path.Combine(_root, "iphone comprimido.jpg"), compressed);
        Assert.Equal((240u, 320u), SizeOf(compressed));
        Assert.Equal(1, OrientationTag(compressed));          // 6 here would turn it again in every viewer
        AssertCorners(compressed, HeicSamples.Clockwise);

        foreach (var target in new[] { "jpg", "png", "webp" })
        {
            var converted = Assert.Single((await ConvertAsync([phone], target)).Outputs);
            Assert.Equal((240u, 320u), SizeOf(converted));
            Assert.Equal(1, OrientationTag(converted));
            AssertCorners(converted, HeicSamples.Clockwise);
        }
    }

    [Fact]
    public void A_picture_stored_as_a_grid_of_tiles_is_read_whole()
    {
        // 3 × 3 tiles of 320 × 240, cropped to 900 × 700, like an iPhone's 512 × 512 tiles cropped to the photo.
        var grid = Path.Combine(_root, "rejilla.heic");
        File.WriteAllBytes(grid, HeicBuilder.GridOf(Donor(), 3, 3, 900, 700).ToBytes());
        using var image = PanelImages.Read(grid);
        Assert.Equal((900u, 700u), (image.Width, image.Height));
        using var pixels = image.GetPixels();
        // The red marker is the top-left corner of every tile: tiles (0, 0), (1, 1) and (2, 1).
        foreach (var (x, y) in new[] { (12, 9), (320 + 12, 240 + 9), (640 + 12, 240 + 9) })
            Assert.Equal("red", HeicSamples.NameOf(pixels.GetPixel(x, y).ToArray()!));
        // And the green one is the top-right corner of the tiles, which the crop cuts off in the last column.
        Assert.Equal("green", HeicSamples.NameOf(pixels.GetPixel(320 + 308, 9).ToArray()!));
    }

    [AvaloniaFact]
    public async Task A_12_megapixel_photo_stored_as_a_grid_is_read_cropped_and_compressed_whole()
    {
        // 4032 × 3024 as 8 × 6 tiles of 512, turned a quarter by irot: what an iPhone writes.
        var big = Encoded("phone-12mp", 4032, 3024, 6, HeicMetadata.CameraAndGps, lookUpright: false, name: "iphone.heic");
        using (var image = PanelImages.Read(big))
        {
            Assert.Equal((3024u, 4032u), (image.Width, image.Height));
            Assert.Equal(HeicSamples.Clockwise, HeicSamples.CornerColours(image));
        }

        var crop = new CropImagePanel(Context(big));
        await WaitAsync(() => !crop.IsLoading);
        Assert.Equal((3024, 4032), (crop.PixelWidth, crop.PixelHeight));
        crop.Selection = new Rect(0, 0, 1512, 2016);
        await crop.RunAsync();
        Assert.True(crop.IsDone, crop.Status);
        Assert.Equal((1512u, 2016u), SizeOf(Assert.Single(crop.Outputs)));

        var compress = new CompressPanel(Context(big));
        await WaitAsync(() => !compress.IsLoading);
        await compress.RunAsync();
        Assert.True(compress.IsDone, compress.Status);
        var compressed = Assert.Single(compress.Outputs);
        Assert.Equal((3024u, 4032u), SizeOf(compressed));
        AssertCorners(compressed, HeicSamples.Clockwise);
        using var exif = new MagickImage(compressed);
        Assert.NotNull(exif.GetExifProfile()!.GetValue(ExifTag.GPSLatitude));     // the camera's tags travel with it
        Assert.Equal("Mandarina Test", exif.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
    }

    [Fact]
    public void Odd_sizes_are_kept_as_they_are()
    {
        // Windows pads 321 × 241 to even sides and says so with a clean-aperture box.
        var odd = Built("impar.heic", b => b.Crop(301, 201));
        using var image = PanelImages.Read(odd);
        Assert.Equal((301u, 201u), (image.Width, image.Height));
    }

    [Fact]
    public void The_other_ways_a_heic_can_describe_or_arrange_its_picture_are_read_as_they_say()
    {
        // A colour description in nclx instead of an ICC profile: sRGB, Display P3 and HDR (BT.2020 with the PQ curve) all open and save.
        foreach (var (name, primaries, transfer, matrix) in new[] { ("srgb", 1, 13, 6), ("p3", 12, 13, 6), ("hdr", 9, 16, 9) })
        {
            using var image = PanelImages.Read(Built($"nclx-{name}.heic", b => b.Nclx(primaries, transfer, matrix, fullRange: true)));
            Assert.Equal((320u, 240u), (image.Width, image.Height));
            if (name != "hdr")
                Assert.Equal(HeicSamples.Upright, HeicSamples.CornerColours(image));
            var jpg = Path.Combine(_root, $"nclx-{name}.jpg");
            PanelImages.Save(image, jpg);
            Assert.True(IsJpeg(jpg));
        }

        // Mirrored with imir: left and right swap.
        using (var image = PanelImages.Read(Built("espejo.heic", b => b.Mirror(vertical: false))))
            Assert.Equal(["green", "red", "yellow", "blue"], HeicSamples.CornerColours(image));

        // Two pictures shown (a burst): the first one is the photo.
        using (var image = PanelImages.Read(Built("dos.heic", b => b.AlsoShown(Donor()))))
        {
            Assert.Equal((320u, 240u), (image.Width, image.Height));
            Assert.Equal(HeicSamples.Upright, HeicSamples.CornerColours(image));
        }
    }

    // ───────── Bit depth and colour ─────────

    [Fact]
    public async Task A_10_bit_heic_is_read_and_saved_as_8_bit_with_its_colours()
    {
        var orange = new MagickColor(200, 100, 50);
        var path = await FlatAsync("diez", orange, bitDepth: 10);
        if (path is null)
        {
            Assert.Skip("FFmpeg with x265 is not installed next to the tests.");
            return;
        }
        using var wide = new MagickImage(path);
        Assert.Equal(10u, wide.Depth);
        using var image = PanelImages.Read(path);
        var jpg = Path.Combine(_root, "diez.jpg");
        PanelImages.Save(image, jpg);
        Assert.True(IsJpeg(jpg));
        using var saved = new MagickImage(jpg);
        Assert.Equal(8u, saved.Depth);
        Assert.True(Share(jpg, orange, 8) > 0.95, "the orange of a 10-bit picture comes out as the same orange");
    }

    [AvaloniaFact]
    public async Task A_display_p3_heic_keeps_its_profile_and_its_pixels_in_the_tools_that_edit_the_photo_itself()
    {
        var p3 = HeicSamples.DisplayP3();
        var orange = new MagickColor(200, 100, 50);
        var path = await FlatAsync("p3", orange, icc: p3);
        if (path is null)
        {
            Assert.Skip("FFmpeg with x265 is not installed next to the tests.");
            return;
        }
        using (var image = new MagickImage(path))
            Assert.Equal("Display P3 (test)", image.GetColorProfile()!.Description);

        void AssertP3(string output, bool samePixels)
        {
            using var result = new MagickImage(output);
            Assert.Equal("Display P3 (test)", result.GetColorProfile()?.Description);     // without it a phone's wide-gamut photo looks washed out
            if (samePixels)
                Assert.True(Share(output, orange, 8) > 0.95, $"{Path.GetFileName(output)} changed the pixels");
        }

        var compress = new CompressPanel(Context(path));
        await WaitAsync(() => !compress.IsLoading);
        await compress.RunAsync();
        AssertP3(Assert.Single(compress.Outputs), samePixels: true);

        var crop = new CropImagePanel(Context(path));
        await WaitAsync(() => !crop.IsLoading);
        crop.Selection = new Rect(0, 0, 160, 120);
        await crop.RunAsync();
        AssertP3(Assert.Single(crop.Outputs), samePixels: true);

        var edit = new EditImagePanel(Context(path));
        await WaitAsync(() => !edit.IsLoading);
        edit.Sharpness = 10;
        await edit.RunAsync();
        AssertP3(Assert.Single(edit.Outputs), samePixels: false);

        var redact = new RedactImagePanel(Context(path));
        await WaitAsync(() => !redact.IsLoading);
        redact.Regions.Add(new RedactRegion(new Rect(0, 0, 40, 40)));
        await redact.RunAsync();
        AssertP3(Assert.Single(redact.Outputs), samePixels: false);

        var metadata = new MetadataPanel(Context(path));
        await WaitAsync(() => !metadata.IsLoading);
        metadata.Field("Author")!.Text = "Ana";
        await metadata.RunAsync();
        AssertP3(Assert.Single(metadata.Outputs), samePixels: true);

        // The conversion keeps the profile as well.
        AssertP3(Assert.Single((await ConvertAsync([path], "jpg")).Outputs), samePixels: true);
        AssertP3(Assert.Single((await ConvertAsync([path], "png")).Outputs), samePixels: true);
    }

    [AvaloniaFact]
    public async Task A_display_p3_heic_is_converted_to_srgb_where_the_result_has_no_profile_to_carry_it()
    {
        // Annotate saves a PNG drawn by Avalonia, Frame and Collage compose a new canvas: none of them can say "this is P3", so
        // the photo must reach them already in sRGB, or its saturated colours come out washed out.
        var p3 = HeicSamples.DisplayP3();
        var orange = new MagickColor(200, 100, 50);
        var path = await FlatAsync("p3", orange, icc: p3);
        if (path is null)
        {
            Assert.Skip("FFmpeg with x265 is not installed next to the tests.");
            return;
        }
        IMagickColor<byte> raw, srgb;
        using (var image = new MagickImage(path))
        {
            raw = Centre(image);
            image.TransformColorSpace(ColorProfiles.SRGB);
            srgb = Centre(image);
        }
        var apart = Math.Max(Math.Abs(raw.R - srgb.R), Math.Max(Math.Abs(raw.G - srgb.G), Math.Abs(raw.B - srgb.B)));
        Assert.True(apart >= 12, $"P3 {raw.R},{raw.G},{raw.B} and sRGB {srgb.R},{srgb.G},{srgb.B} must differ enough to tell them apart");

        var annotate = new AnnotateImagePanel(Context(path));
        await WaitAsync(() => !annotate.IsLoading);
        annotate.Document.Add(new Annotation { Kind = AnnotationKind.Line, Color = AnnotationPalette.White, Thickness = 3, Points = [new Point(2, 2), new Point(30, 2)] });
        await annotate.RunAsync();
        Assert.True(annotate.IsDone, annotate.Status);
        var annotated = Assert.Single(annotate.Outputs);
        Assert.True(Share(annotated, srgb, 8) > 0.9, "the annotated photo is in sRGB");
        Assert.True(Share(annotated, raw, 4) < 0.01, "and not the P3 numbers read as sRGB");

        var frame = new FrameImagePanel(Context(path));
        await WaitAsync(() => !frame.IsLoading);
        frame.Background = FrameBackground.Color;
        frame.Primary = "#FFFFFF";
        frame.Corners = 0;
        frame.Shadow = false;
        await frame.RunAsync();
        Assert.True(frame.IsDone, frame.Status);
        var framed = Assert.Single(frame.Outputs);
        Assert.True(Share(framed, srgb, 8) > 0.4, "the framed photo is in sRGB");
        Assert.True(Share(framed, raw, 4) < 0.01);

        var second = Path.Combine(_root, "segunda.heic");
        File.Copy(path, second);
        var collage = new CreateCollagePanel(Context(path, second));
        await WaitAsync(() => !collage.IsLoading);
        collage.Corners = 0;
        await collage.RunAsync();
        Assert.True(collage.IsDone, collage.Status);
        var collaged = Assert.Single(collage.Outputs);
        Assert.True(Share(collaged, srgb, 8) > 0.3, "the collage is in sRGB");
        Assert.True(Share(collaged, raw, 4) < 0.01);
    }

    // ───────── Pictures inside the picture ─────────

    [AvaloniaFact]
    public async Task The_thumbnail_the_depth_map_and_the_alpha_plane_of_a_heic_do_not_change_the_copies()
    {
        var plain = Built("lisa.heic", b => b);
        var busy = Built("ocupada.heic", b => b.Thumbnail(Donor()).Auxiliary(Donor(), HeicBuilder.AppleDepthUrn));
        using (var a = PanelImages.Read(plain))
        using (var b = PanelImages.Read(busy))
        {
            Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
            Assert.Equal(0d, a.Compare(b, ErrorMetric.RootMeanSquared));      // the same picture, the extras are not pixels
        }

        var crop = new CropImagePanel(Context(busy));
        await WaitAsync(() => !crop.IsLoading);
        crop.Selection = new Rect(0, 0, 160, 120);
        await crop.RunAsync();
        var cropped = Assert.Single(crop.Outputs);
        Assert.Equal((160u, 120u), SizeOf(cropped));
        Assert.Equal(1, CountIn(cropped, 0xFF, 0xD8, 0xFF));                  // one JPEG: no second copy of the photo inside it
        Assert.Single((await ConvertAsync([busy], "jpg")).Outputs);

        // An alpha plane makes the picture transparent where it says so; a JPEG cannot be, so it is flattened onto white.
        var cut = Built("recortada.heic", b => b.Auxiliary(Donor(), HeicBuilder.AlphaUrn));
        using (var image = PanelImages.Read(cut))
            Assert.True(image.HasAlpha);
        var png = Assert.Single((await ConvertAsync([cut], "png")).Outputs);
        using (var image = new MagickImage(png))
            Assert.True(image.HasAlpha);
        var compress = new CompressPanel(Context(cut));
        await WaitAsync(() => !compress.IsLoading);
        await compress.RunAsync();
        var jpg = Assert.Single(compress.Outputs);
        Assert.True(IsJpeg(jpg));
        using (var image = new MagickImage(jpg))
            Assert.False(image.HasAlpha);
    }

    [AvaloniaFact]
    public async Task The_hidden_copies_of_the_photo_in_the_exif_and_xmp_of_a_heic_do_not_reach_the_copies()
    {
        // The camera's own Exif: a thumbnail of the photo (IFD1) and a preview in the maker note; the XMP holds a thumbnail too.
        var jpeg = MakePhotoWithHiddenCopies(Path.Combine(_root, "camara.jpg"), 400, 300, orientation: 6);
        var bytes = File.ReadAllBytes(jpeg);
        var at = bytes.AsSpan().IndexOf("Exif\0\0"u8);
        var length = (bytes[at - 2] << 8) | bytes[at - 1];
        var exif = bytes[(at + 6)..(at - 2 + length)];
        const string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description xmlns:xmpGImg='http://ns.adobe.com/xap/1.0/g/img/'><xmpGImg:image>/9j/4AAQSkZJRgABAQAAAQABAAD/2wBD</xmpGImg:image></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>";
        var heic = Built("oculto.heic", b => b.Rotate(3).Exif(exif).Xmp(xmp).Thumbnail(Donor()));
        using (var source = new MagickImage(heic))
        {
            Assert.True(source.GetExifProfile()!.ThumbnailLength > 0, "the test photo should carry its Exif thumbnail");
            Assert.NotNull(source.GetXmpProfile());
        }

        var outputs = new List<string>();
        var edit = new EditImagePanel(Context(heic));
        await WaitAsync(() => !edit.IsLoading);
        edit.Exposure = 0.3;
        await edit.RunAsync();
        outputs.AddRange(edit.Outputs);
        var crop = new CropImagePanel(Context(heic));
        await WaitAsync(() => !crop.IsLoading);
        crop.Selection = new Rect(0, 0, 100, 100);
        await crop.RunAsync();
        outputs.AddRange(crop.Outputs);
        var redact = new RedactImagePanel(Context(heic));
        await WaitAsync(() => !redact.IsLoading);
        redact.Regions.Add(new RedactRegion(new Rect(0, 0, 30, 30)));
        await redact.RunAsync();
        outputs.AddRange(redact.Outputs);
        var frame = new FrameImagePanel(Context(heic));
        await WaitAsync(() => !frame.IsLoading);
        await frame.RunAsync();
        outputs.AddRange(frame.Outputs);
        var remove = new MetadataPanel(Context(heic));
        await WaitAsync(() => !remove.IsLoading);
        remove.IsEditMode = false;
        await remove.RunAsync();
        outputs.AddRange(remove.Outputs);

        Assert.Equal(5, outputs.Count);
        foreach (var output in outputs)
        {
            Assert.Equal(1, CountIn(output, 0xFF, 0xD8, 0xFF));               // the photo and nothing else that is a JPEG
            Assert.Equal(0, CountIn(output, "xmpGImg"u8.ToArray()));
        }
    }

    // ───────── Every tool ─────────

    [AvaloniaFact]
    public async Task Every_photo_tool_saves_a_heic_as_an_upright_copy_beside_it_and_leaves_the_original_alone()
    {
        var photo = Fixture();
        var original = Hash(photo);
        var twin = Path.Combine(_root, "otra.jpg");
        MakePhoto(twin, 400, 300);

        async Task<string> Apply<T>(T panel, Func<T, bool> loaded, Action<T>? set = null) where T : ToolPanel
        {
            await WaitAsync(() => loaded(panel));
            set?.Invoke(panel);
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            return Assert.Single(panel.Outputs);
        }

        var compressed = await Apply(new CompressPanel(Context(photo)), p => !p.IsLoading);
        var described = await Apply(new MetadataPanel(Context(photo)), p => !p.IsLoading, p => p.Field("Author")!.Text = "Ana Nueva");
        var edited = await Apply(new EditImagePanel(Context(photo)), p => !p.IsLoading, p => p.Exposure = 0.3);
        var cropped = await Apply(new CropImagePanel(Context(photo)), p => !p.IsLoading, p => p.Selection = new Rect(0, 0, 120, 160));
        var framed = await Apply(new FrameImagePanel(Context(photo)), p => !p.IsLoading);
        var covered = await Apply(new RedactImagePanel(Context(photo)), p => !p.IsLoading, p => p.Regions.Add(new RedactRegion(new Rect(0, 0, 60, 60))));
        var annotated = await Apply(new AnnotateImagePanel(Context(photo)), p => !p.IsLoading,
            p => p.Document.Add(new Annotation { Kind = AnnotationKind.Rectangle, Color = AnnotationPalette.Red, Thickness = 4, Points = [new Point(100, 100), new Point(200, 200)] }));
        var collaged = await Apply(new CreateCollagePanel(Context(photo, twin)), p => !p.IsLoading);

        Assert.Equal(
            ["foto comprimido.jpg", "foto metadatos.jpg", "foto editado.jpg", "foto recortado.jpg", "foto con fondo.jpg", "foto tapado.jpg", "foto anotado.png", "foto collage.jpg"],
            new[] { compressed, described, edited, cropped, framed, covered, annotated, collaged }.Select(Path.GetFileName));
        Assert.All(new[] { compressed, described, edited, cropped, framed, covered, collaged }, output => Assert.True(IsJpeg(output), output));
        Assert.True(File.ReadAllBytes(annotated) is [0x89, (byte)'P', (byte)'N', (byte)'G', ..]);

        // Upright, at the size the photo looks (240 × 320), not the 320 × 240 it is stored in.
        foreach (var output in new[] { compressed, described, edited })
        {
            Assert.Equal((240u, 320u), SizeOf(output));
            AssertCorners(output, HeicSamples.Clockwise);
            Assert.Equal(1, OrientationTag(output));
        }
        Assert.Equal((240u, 320u), SizeOf(annotated));
        AssertCorners(annotated, HeicSamples.Clockwise);
        Assert.Equal((120u, 160u), SizeOf(cropped));
        using (var piece = new MagickImage(cropped))
            Assert.Equal("blue", HeicSamples.CornerColours(piece).First());          // the top left of what the photo looks like
        using (var piece = new MagickImage(covered))
        {
            using var pixels = piece.GetPixels();
            Assert.True(pixels.GetPixel(20, 20).ToArray()!.All(channel => channel < 10), "the covered corner is black");
        }
        using (var picture = new MagickImage(framed))
            Assert.True(picture.Height > 320 && picture.Width > 240);

        // The camera's tags travel with the photo where the tool keeps them, and the GPS goes where the tool covers things up.
        foreach (var output in new[] { compressed, edited, cropped })
        {
            using var result = new MagickImage(output);
            Assert.Equal("Mandarina Test", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
            Assert.NotNull(result.GetExifProfile()!.GetValue(ExifTag.GPSLatitude));
        }
        using (var result = new MagickImage(covered))
            Assert.Null(result.GetExifProfile());

        Assert.Equal(original, Hash(photo));
    }

    [AvaloniaFact]
    public async Task The_metadata_panel_reads_the_tags_of_a_heic_removes_the_location_or_everything()
    {
        var photo = Fixture();
        var panel = new MetadataPanel(Context(photo));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal("Autor Test", panel.Field("Author")!.Text);
        Assert.Equal("Mandarina Test", panel.Field("Make")!.Text);
        Assert.Equal("Synthetic Cam 1", panel.Field("Model")!.Text);
        Assert.Equal("40.416667", panel.Field("Latitude")!.Text);
        Assert.Equal("-3.7", panel.Field("Longitude")!.Text);
        Assert.Contains(panel.Notices, notice => notice.Contains("JPG"));         // "HEIC can't be written: it is saved as a JPG"

        panel.RemoveLocation();
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var withoutPlace = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "foto metadatos.jpg"), withoutPlace);
        var info = PhotoMetadata.Read(withoutPlace);
        Assert.False(info.HasLocation);
        Assert.Equal("Autor Test", info.Values[PhotoMetadataField.Author]);
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(File.ReadAllBytes(withoutPlace)));
        AssertCorners(withoutPlace, HeicSamples.Clockwise);

        var everything = new MetadataPanel(Context(photo));
        await WaitAsync(() => !everything.IsLoading);
        everything.IsEditMode = false;
        await everything.RunAsync();
        Assert.True(everything.IsDone, everything.Status);
        var bare = Assert.Single(everything.Outputs);
        Assert.Equal(Path.Combine(_root, "foto sin metadatos.jpg"), bare);
        Assert.Equal(0, PhotoMetadata.Read(bare).Tags);
        AssertCorners(bare, HeicSamples.Clockwise);
        Assert.DoesNotContain("Mandarina", Encoding.Latin1.GetString(File.ReadAllBytes(bare)));
    }

    [AvaloniaFact]
    public async Task A_collage_of_a_heic_and_a_jpeg_shows_both_the_same_way_up()
    {
        // The same upright picture twice: once in a HEIC stored sideways, once as a JPEG. Their cells must look alike.
        var heic = Encoded("collage-portrait", 480, 640, 6, lookUpright: true, name: "uno.heic");
        var jpg = Path.Combine(_root, "dos.jpg");
        using (var image = PanelImages.Read(heic))
            image.Write(jpg);

        var panel = new CreateCollagePanel(Context(heic, jpg));
        await WaitAsync(() => !panel.IsLoading);
        panel.Layout = CollageStyle.Row;
        panel.Corners = 0;
        panel.Spacing = 10;
        panel.Size = CollageSize.Small;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);

        using var canvas = new MagickImage(output);
        var cells = CollageLayout.Compute(2, CollageStyle.Row, (int)canvas.Width, (int)canvas.Height, 10);
        using var left = (MagickImage)canvas.Clone();
        left.Crop(new MagickGeometry(cells[0].X, cells[0].Y, (uint)cells[0].Width, (uint)cells[0].Height));
        left.ResetPage();
        using var right = (MagickImage)canvas.Clone();
        right.Crop(new MagickGeometry(cells[1].X, cells[1].Y, (uint)cells[1].Width, (uint)cells[1].Height));
        right.ResetPage();
        Assert.Equal((left.Width, left.Height), (right.Width, right.Height));
        Assert.True(left.Compare(right, ErrorMetric.RootMeanSquared) < 0.06, "the HEIC cell and the JPEG cell show the same picture");
    }

    [AvaloniaFact]
    public async Task A_heic_works_as_the_background_picture_of_the_frame_and_a_broken_one_is_reported()
    {
        // The same upright picture as a HEIC stored sideways and as a JPEG: the two backgrounds must look alike.
        var heic = Fixture("fondo.heic");
        var jpg = Path.Combine(_root, "fondo.jpg");
        using (var image = PanelImages.Read(heic))
            image.Write(jpg);
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"), 400, 300);

        async Task<string> FrameWithAsync(string background)
        {
            var panel = new FrameImagePanel(Context(photo));
            await WaitAsync(() => !panel.IsLoading);
            await panel.SetPictureAsync(background);
            Assert.Null(panel.PictureError);
            panel.Background = FrameBackground.Image;
            panel.Padding = 40;
            panel.Shadow = false;
            panel.Corners = 0;
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            return Assert.Single(panel.Outputs);
        }

        var withHeic = await FrameWithAsync(heic);
        var withJpeg = await FrameWithAsync(jpg);
        using (var a = new MagickImage(withHeic))
        using (var b = new MagickImage(withJpeg))
        {
            Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
            Assert.True(a.Compare(b, ErrorMetric.RootMeanSquared) < 0.05, "the HEIC background looks like its JPEG twin");
        }

        var broken = Path.Combine(_root, "rota.heic");
        File.WriteAllBytes(broken, Broken("cut in the pixels"));
        var frame = new FrameImagePanel(Context(photo));
        await WaitAsync(() => !frame.IsLoading);
        await frame.SetPictureAsync(broken);
        Assert.Equal("No se puede abrir «rota.heic»", frame.PictureError);
        Assert.Null(frame.PicturePath);
    }

    [AvaloniaFact]
    public async Task The_crop_panel_shows_a_turned_heic_upright()
    {
        var panel = new CropImagePanel(Context(Fixture()));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal((240, 320), (panel.PixelWidth, panel.PixelHeight));
        Assert.Equal(new Size(240, 320), panel.Preview!.Size);
        Render(panel, "heic-crop");
    }

    [AvaloniaFact]
    public async Task Text_is_found_in_a_portrait_stored_sideways_and_placed_where_it_looks()
    {
        var photo = Encoded("ocr-portrait", 1600, 1200, 6, lookUpright: true, name: "texto.heic");
        // Windows reads text in the languages it has: when it cannot read this plain picture, there is nothing to test.
        var png = Path.Combine(_root, "referencia.png");
        using (var image = PanelImages.Read(photo))
            image.Write(png);
        var reference = await LocalDetector.FindTextAsync(png);
        if (reference.Failure != RedactDetectionFailure.None || reference.Count == 0)
        {
            Assert.Skip("Windows cannot read the text of the test picture here: " + reference.Reason);
            return;
        }

        using var panel = new RedactImagePanel(Context(photo));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal((1600u, 1200u), (panel.SourceWidth, panel.SourceHeight));
        await panel.DetectTextAsync();
        Assert.NotEmpty(panel.Regions);
        // "MANDARINA HEIC TEST" is drawn from 20 % of the width, at 55 % of the height.
        var found = panel.Regions.Select(region => region.Rect).Aggregate((a, b) => a.Union(b));
        Assert.InRange(found.Center.X, 1600 * 0.25, 1600 * 0.75);
        Assert.InRange(found.Center.Y, 1200 * 0.5, 1200 * 0.7);
        Assert.True(found.Width > 1600 * 0.3, $"the text spans {found.Width:0} px");
    }

    // ───────── Names ─────────

    [AvaloniaFact]
    public async Task Names_with_accents_and_other_scripts_work_in_every_tool()
    {
        var photo = Fixture("Fotografía ñandú 日本.heic");

        var compress = new CompressPanel(Context(photo));
        await WaitAsync(() => !compress.IsLoading);
        await compress.RunAsync();
        Assert.Equal(Path.Combine(_root, "Fotografía ñandú 日本 comprimido.jpg"), Assert.Single(compress.Outputs));

        var crop = new CropImagePanel(Context(photo));
        await WaitAsync(() => !crop.IsLoading);
        crop.Selection = new Rect(0, 0, 100, 100);
        await crop.RunAsync();
        Assert.Equal(Path.Combine(_root, "Fotografía ñandú 日本 recortado.jpg"), Assert.Single(crop.Outputs));

        var metadata = new MetadataPanel(Context(photo));
        await WaitAsync(() => !metadata.IsLoading);
        metadata.IsEditMode = false;
        await metadata.RunAsync();
        Assert.Equal(Path.Combine(_root, "Fotografía ñandú 日本 sin metadatos.jpg"), Assert.Single(metadata.Outputs));

        var annotate = new AnnotateImagePanel(Context(photo));
        await WaitAsync(() => !annotate.IsLoading);
        annotate.Document.Add(new Annotation { Kind = AnnotationKind.Line, Color = AnnotationPalette.Red, Thickness = 4, Points = [new Point(0, 0), new Point(50, 50)] });
        await annotate.RunAsync();
        Assert.Equal(Path.Combine(_root, "Fotografía ñandú 日本 anotado.png"), Assert.Single(annotate.Outputs));

        Assert.Equal(Path.Combine(_root, "Fotografía ñandú 日本.png"), Assert.Single((await ConvertAsync([photo], "png")).Outputs));
        AssertCorners(Assert.Single(compress.Outputs), HeicSamples.Clockwise);
        AssertCorners(Assert.Single(metadata.Outputs), HeicSamples.Clockwise);
    }

    [Theory]
    [InlineData(".heic", true)]
    [InlineData(".HEIC", true)]
    [InlineData(".heif", true)]
    [InlineData(".hif", true)]
    [InlineData(".HIF", true)]
    [InlineData(".jpg", false)]
    [InlineData(".avif", false)]
    [InlineData("", false)]
    public void The_heic_family_is_what_filee_reads_as_heic(string extension, bool heic)
    {
        Assert.Equal(heic, PanelImages.IsHeif(extension));
        var expected = heic ? ".jpg" : extension;
        Assert.Equal(expected, PanelImages.OutputExtension("a" + extension));
        Assert.Equal(expected, PhotoMetadata.OutputExtension("a" + extension));
        Assert.Equal(expected, ImageCompressor.OutputExtension("a" + extension));
    }

    [AvaloniaTheory]
    [InlineData("foto.hif")]
    [InlineData("FOTO.HIF")]
    [InlineData("foto.heif")]
    [InlineData("foto.HEIC")]
    public async Task Fujifilms_hif_and_the_other_heic_extensions_are_saved_as_real_jpegs(string name)
    {
        // A ".hif" used to be written as whatever Magick made of the name: a MIFF, or an AVIF behind a name that says HEIF.
        var photo = Fixture(name);
        async Task<string> Apply<T>(T panel, Func<T, bool> loaded, Action<T>? set = null) where T : ToolPanel
        {
            await WaitAsync(() => loaded(panel));
            set?.Invoke(panel);
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            return Assert.Single(panel.Outputs);
        }

        var outputs = new[]
        {
            await Apply(new CompressPanel(Context(photo)), p => !p.IsLoading),
            await Apply(new MetadataPanel(Context(photo)), p => !p.IsLoading, p => p.Field("Author")!.Text = "Ana"),
            await Apply(new MetadataPanel(Context(photo)), p => !p.IsLoading, p => p.IsEditMode = false),
            await Apply(new EditImagePanel(Context(photo)), p => !p.IsLoading, p => p.Exposure = 0.3),
            await Apply(new CropImagePanel(Context(photo)), p => !p.IsLoading, p => p.Selection = new Rect(0, 0, 100, 100)),
            await Apply(new FrameImagePanel(Context(photo)), p => !p.IsLoading),
            await Apply(new RedactImagePanel(Context(photo)), p => !p.IsLoading, p => p.Regions.Add(new RedactRegion(new Rect(0, 0, 50, 50)))),
        };
        Assert.All(outputs, output =>
        {
            Assert.Equal(".jpg", Path.GetExtension(output));
            Assert.True(IsJpeg(output), $"{Path.GetFileName(output)} is not a JPEG");
        });
    }

    // ───────── The conversion pipeline ─────────

    [AvaloniaFact]
    public async Task The_conversion_turns_a_heic_into_jpg_png_webp_and_pdf_upright_with_its_profile()
    {
        var phone = Built("iphone.heic", b => b.Rotate(3).Exif(HeicSamples.PhoneExif(6)).Icc(HeicSamples.DisplayP3()).Thumbnail(Donor()));
        foreach (var target in new[] { "jpg", "png", "webp" })
        {
            var output = Assert.Single((await ConvertAsync([phone], target)).Outputs);
            Assert.Equal(Path.Combine(_root, "iphone." + target), output);
            Assert.Equal((240u, 320u), SizeOf(output));
            AssertCorners(output, HeicSamples.Clockwise);
            using var result = new MagickImage(output);
            Assert.Equal("Display P3 (test)", result.GetColorProfile()?.Description);
            Assert.Equal("Mandarina Phone", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
        }

        var pdf = Assert.Single((await ConvertAsync([phone], "pdf")).Outputs);
        AssertCorners(RasterOf(pdf), HeicSamples.Clockwise);
    }

    [AvaloniaFact]
    public async Task Create_pdf_puts_heic_hif_and_jpeg_photos_in_one_document()
    {
        var one = Fixture("uno.heic");
        var two = Fixture("dos.hif");
        var three = MakePhoto(Path.Combine(_root, "tres.jpg"), 400, 300);
        TestServices.EnsureInitialized("es");
        var preset = WheelCatalog.ToolsFor([one, two, three], AppHost.Get<ILocalizer>()).Single(t => t.Id == "createPDF").Preset!;
        var job = await ConvertAsync([one, two, three], preset, Path.Combine(_root, "data"));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var pdf = Assert.Single(job.Outputs);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(pdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(3, document.Pages.Count);
        AssertCorners(RasterOf(pdf), HeicSamples.Clockwise);                       // page 1 is the turned HEIC, upright
    }

    // ───────── Files that cannot be read ─────────

    /// <summary>What each kind of broken file is made of.</summary>
    private byte[] Broken(string kind)
    {
        var good = File.ReadAllBytes(HeicSamples.Fixture());
        return kind switch
        {
            "empty" => [],
            "text" => Encoding.ASCII.GetBytes("this is not a picture at all, it is only text"),
            "cut in the header" => good[..300],
            "cut in the pixels" => good[..(good.Length / 2)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [AvaloniaTheory]
    [InlineData("empty")]
    [InlineData("text")]
    [InlineData("cut in the header")]
    [InlineData("cut in the pixels")]
    public async Task A_heic_that_cannot_be_read_gets_a_clear_message_in_every_tool_and_nothing_hangs(string kind)
    {
        var broken = Path.Combine(_root, "rota.heic");
        File.WriteAllBytes(broken, Broken(kind));
        var clock = Stopwatch.StartNew();

        // Plain words, in the user's language, with no path and no line of Magick's source.
        void AssertClear(string? message)
        {
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("@ error", message);
            Assert.DoesNotContain(_root, message);
            Assert.DoesNotContain("heic.c", message);
            Assert.DoesNotContain("Invalid input", message);
        }

        var annotate = new AnnotateImagePanel(Context(broken));
        await WaitAsync(() => !annotate.IsLoading);
        AssertClear(annotate.LoadError);
        Assert.Equal("El archivo está dañado o no tiene un formato que se pueda leer.", annotate.LoadError);
        Assert.False(annotate.CanApply);

        var metadata = new MetadataPanel(Context(broken));
        await WaitAsync(() => !metadata.IsLoading);
        AssertClear(metadata.LoadError);
        Assert.Equal("No se puede leer el archivo: El archivo está dañado o no tiene un formato que se pueda leer.", metadata.LoadError);

        var edit = new EditImagePanel(Context(broken));
        await WaitAsync(() => !edit.IsLoading);
        AssertClear(edit.LoadError);
        var frame = new FrameImagePanel(Context(broken));
        await WaitAsync(() => !frame.IsLoading);
        AssertClear(frame.LoadError);
        var crop = new CropImagePanel(Context(broken));
        await WaitAsync(() => !crop.IsLoading);
        AssertClear(crop.LoadError);
        var redact = new RedactImagePanel(Context(broken));
        await WaitAsync(() => !redact.IsLoading);
        AssertClear(redact.LoadError);
        Assert.All(new ToolPanel[] { edit, frame, crop, redact }, panel => Assert.False(panel.CanRun));

        var compress = new CompressPanel(Context(broken));
        await WaitAsync(() => !compress.IsLoading);
        await compress.RunAsync();
        Assert.True(compress.HasError);
        AssertClear(compress.Status);
        Assert.Empty(Directory.GetFiles(_root, "rota *"));

        var collage = new CreateCollagePanel(Context(broken, Fixture("buena.heic")));
        await WaitAsync(() => !collage.IsLoading);
        await collage.RunAsync();
        Assert.True(collage.HasError);
        AssertClear(collage.Status);
        Assert.Empty(Directory.GetFiles(_root, "rota *"));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [AvaloniaTheory]
    [InlineData("empty")]
    [InlineData("cut in the pixels")]
    public async Task A_photo_that_breaks_after_it_was_opened_fails_with_a_clear_message_and_leaves_nothing_behind(string kind)
    {
        var photo = Fixture("viva.heic");
        var edit = new EditImagePanel(Context(photo)) { Exposure = 0.3 };
        var crop = new CropImagePanel(Context(photo));
        var annotate = new AnnotateImagePanel(Context(photo));
        var metadata = new MetadataPanel(Context(photo));
        await WaitAsync(() => !edit.IsLoading && !crop.IsLoading && !annotate.IsLoading && !metadata.IsLoading);
        crop.Selection = new Rect(0, 0, 100, 100);
        annotate.Document.Add(new Annotation { Kind = AnnotationKind.Line, Color = AnnotationPalette.Red, Thickness = 4, Points = [new Point(0, 0), new Point(50, 50)] });
        metadata.Field("Author")!.Text = "Ana";
        File.WriteAllBytes(photo, Broken(kind));

        foreach (var panel in new ToolPanel[] { edit, crop, annotate, metadata })
        {
            await panel.RunAsync();
            Assert.True(panel.HasError, panel.Id);
            Assert.EndsWith("El archivo está dañado o no tiene un formato que se pueda leer.", panel.Status);
            Assert.DoesNotContain("@ error", panel.Status);
        }
        Assert.Equal(["viva.heic"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [AvaloniaFact]
    public void Magick_failures_are_described_in_plain_words_and_the_log_keeps_theirs()
    {
        TestServices.EnsureInitialized("es");
        var loc = AppHost.Get<ILocalizer>();
        var empty = Path.Combine(_root, "vacia.heic");
        File.WriteAllBytes(empty, []);
        var failure = Assert.ThrowsAny<MagickException>(() => new MagickImage(empty));

        Assert.Equal(loc["panel.error.damaged"], PanelErrors.Describe(failure, loc));
        Assert.Equal(loc["panel.error.damaged"], PanelErrors.Describe(new InvalidOperationException("outer", failure), loc));   // wrapped by a panel
        Assert.Equal("panel.error.damaged", PanelErrors.PictureCause(failure));
        Assert.Equal("panel.error.out_of_memory", PanelErrors.PictureCause(new MagickResourceLimitErrorException("memory allocation failed")));
        Assert.Equal("panel.error.codec", PanelErrors.PictureCause(new MagickMissingDelegateErrorException("no decode delegate for this image format")));
        Assert.Equal("panel.error.no_permission", PanelErrors.PictureCause(new MagickBlobErrorException("unable to open image: Permission denied")));
        Assert.Equal("panel.error.not_found", PanelErrors.PictureCause(new MagickBlobErrorException("unable to open image: No such file or directory")));

        // The panel's exception names the file and keeps Magick's own words for the log.
        var named = PanelErrors.FromPicture(failure, "vacia.heic");
        Assert.Equal("«vacia.heic»: " + loc["panel.error.damaged"], PanelErrors.Describe(named, loc));
        Assert.Equal(failure.Message, PanelErrors.Original(named));
    }

}
