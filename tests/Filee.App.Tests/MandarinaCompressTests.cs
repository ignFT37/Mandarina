// Mandarina: COMPRESS for photos and PDFs. The two compressors (ImageCompressor on Magick.NET, PdfCompressor on
// PDFsharp + Magick.NET) run against real files, and the panel is driven the way the wheel drives it: it renders to
// mandarina-panel-compress*.png in the screenshot folder for review, and Apply writes "name compressed.ext" beside the
// original. Audio and video go through AudioCompressor / VideoCompressor (FFmpeg), which have tests of their own.

using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using static Filee.App.Tests.MandarinaTestKit;

// PDFtoImage marks its API for the platforms PDFium ships for; the tests run on desktop platforms, which are all covered.
#pragma warning disable CA1416

namespace Filee.App.Tests;

public sealed class MandarinaCompressTests : IDisposable
{
    /// <summary>PDFium is not thread-safe: the tests of this file that render a PDF take turns.</summary>
    private static readonly object Pdfium = new();

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-compress").FullName;

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

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private string Out(string name) => Path.Combine(_root, name);

    /// <summary>A 1600 × 1067 plasma photo (4:2:0-friendly, a few hundred KB as JPG) unless said otherwise.</summary>
    private string Photo(string name, uint width = 1600, uint height = 1067) => MakePhoto(Out(name), width, height);

    private static CompressOptions Percent(int quality, int maxSize = 0) => new(CompressMode.Percent, quality, 0, maxSize);

    private static CompressOptions Target(long bytes, int maxSize = 0) => new(CompressMode.TargetSize, 70, bytes, maxSize);

    private Task<string> CompressPhoto(string source, string name, CompressOptions options) =>
        ImageCompressor.CompressAsync(source, Out(name), options, null, Cancel);

    private Task<string> CompressPdf(string source, string name, CompressOptions options) =>
        PdfCompressor.CompressAsync(source, Out(name), options, null, Cancel);

    private static long Length(string path) => new FileInfo(path).Length;

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

    private async Task<CompressPanel> OpenAsync(params string[] files)
    {
        var panel = new CompressPanel(Context(files));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    /// <summary>A PDF with one page per picture (spread over the page's width), a title and an author.</summary>
    private string PdfOf(string name, params string[] pictures)
    {
        var path = Out(name);
        using var document = new PdfDocument();
        document.Info.Title = "Informe anual";
        document.Info.Author = "Autor01";
        document.Info.Subject = "Pruebas";
        document.Info.Keywords = "comprimir";
        foreach (var picture in pictures)
        {
            var page = document.AddPage();
            using var graphics = XGraphics.FromPdfPage(page);
            using var image = XImage.FromFile(picture);
            var width = page.Width.Point;
            graphics.DrawImage(image, 0, 0, width, width * image.PixelHeight / image.PixelWidth);
        }
        document.Save(path);
        return path;
    }

    /// <summary>What the PDF says about each picture it holds (forms included): enough to see what was changed.</summary>
    private sealed record PdfPicture(string Filter, string ColorSpace, int Width, int Height, bool HasSoftMask);

    private static List<PdfPicture> PicturesIn(string pdf)
    {
        var found = new List<PdfPicture>();
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);

        void Collect(PdfDictionary? resources)
        {
            if (resources?.Elements.GetDictionary("/XObject") is not { } xobjects)
                return;
            foreach (var entry in xobjects.Elements)
            {
                if (entry.Value is not PdfReference { Value: PdfDictionary xobject })
                    continue;
                var elements = xobject.Elements;
                switch (elements.GetName("/Subtype"))
                {
                    case "/Form":
                        Collect(elements.GetDictionary("/Resources"));
                        break;
                    case "/Image":
                        found.Add(new PdfPicture(elements.GetName("/Filter"), elements.GetName("/ColorSpace"), elements.GetInteger("/Width"),
                            elements.GetInteger("/Height"), elements.ContainsKey("/SMask")));
                        break;
                }
            }
        }

        foreach (var page in document.Pages)
            Collect(page.Elements.GetDictionary("/Resources"));
        return found;
    }

    /// <summary>Renders every page of both PDFs with PDFium and asserts they look the same (a mean difference per channel below 12 of 255).</summary>
    private static void AssertSameLook(string original, string compressed, int pages)
    {
        var before = File.ReadAllBytes(original);
        var after = File.ReadAllBytes(compressed);
        lock (Pdfium)
        {
            Assert.Equal(pages, PDFtoImage.Conversion.GetPageCount(after));
            for (var page = 0; page < pages; page++)
            {
                using var a = PDFtoImage.Conversion.ToImage(before, page, null, new PDFtoImage.RenderOptions(Dpi: 36));
                using var b = PDFtoImage.Conversion.ToImage(after, page, null, new PDFtoImage.RenderOptions(Dpi: 36));
                Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
                double total = 0;
                var samples = 0;
                for (var y = 4; y < a.Height; y += Math.Max(1, a.Height / 40))
                {
                    for (var x = 4; x < a.Width; x += Math.Max(1, a.Width / 30))
                    {
                        SKColor c1 = a.GetPixel(x, y), c2 = b.GetPixel(x, y);
                        total += Math.Abs(c1.Red - c2.Red) + Math.Abs(c1.Green - c2.Green) + Math.Abs(c1.Blue - c2.Blue);
                        samples += 3;
                    }
                }
                Assert.True(total / samples < 12, $"page {page + 1} looks different after compressing: mean difference {total / samples:0.0}");
            }
        }
    }

    // ───────── Photos: the compressor ─────────

    [Fact]
    public async Task A_lower_quality_makes_a_smaller_jpeg()
    {
        var source = Photo("foto.jpg");
        var q85 = await CompressPhoto(source, "q85.jpg", Percent(85));
        var q50 = await CompressPhoto(source, "q50.jpg", Percent(50));

        Assert.True(Length(q50) < Length(q85), $"q50 {Length(q50)} B, q85 {Length(q85)} B");
        Assert.True(Length(q85) < Length(source), $"q85 {Length(q85)} B, source {Length(source)} B");
        Assert.Equal((1600u, 1067u), SizeOf(q50));
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(q50).Format);
    }

    [Fact]
    public async Task A_target_size_is_met_and_not_overshot_by_much()
    {
        var source = Photo("foto.jpg");

        var output = await CompressPhoto(source, "cien.jpg", Target(100_000));

        var size = Length(output);
        Assert.True(size <= 100_000, $"{size} B is over the 100 KB target");
        Assert.True(size >= 85_000, $"{size} B is far under the 100 KB target");
        Assert.Equal((1600u, 1067u), SizeOf(output));              // the quality alone was enough: the picture keeps its size
    }

    [Fact]
    public async Task A_target_too_small_for_any_quality_shrinks_the_picture()
    {
        var source = Photo("foto.jpg");

        var output = await CompressPhoto(source, "diminuta.jpg", Target(8_000));

        Assert.InRange(Length(output), 1, 8_000);
        var (width, height) = SizeOf(output);
        Assert.True(width < 1600 && width >= 1600 * Math.Pow(0.85, 8) - 1, $"width {width}");
        Assert.True(Math.Abs(width / (double)height - 1600 / 1067.0) < 0.01, "the proportion changed");
    }

    [Fact]
    public async Task A_target_above_the_original_keeps_the_original()
    {
        var source = Photo("foto.jpg");

        var output = await CompressPhoto(source, "holgado.jpg", Target(Length(source) + 1000));

        Assert.Equal(await File.ReadAllBytesAsync(source, Cancel), await File.ReadAllBytesAsync(output, Cancel));
    }

    [Fact]
    public async Task The_longest_side_is_reduced_with_the_proportion_kept_and_never_enlarged()
    {
        var source = Photo("foto.jpg");

        var small = await CompressPhoto(source, "ochocientos.jpg", Percent(70, maxSize: 800));
        var (width, height) = SizeOf(small);
        Assert.Equal(800u, width);
        Assert.True(Math.Abs(width / (double)height - 1600 / 1067.0) < 0.01, $"{width} × {height}");

        var tall = MakePhoto(Out("alta.jpg"), 600, 900);
        var kept = await CompressPhoto(tall, "alta comprimida.jpg", Percent(70, maxSize: 4096));
        Assert.Equal((600u, 900u), SizeOf(kept));                   // 4096 is larger than the picture: nothing to reduce
        var vertical = await CompressPhoto(tall, "alta pequena.jpg", Percent(70, maxSize: 450));
        Assert.Equal((300u, 450u), SizeOf(vertical));
    }

    [Fact]
    public async Task A_picture_that_is_already_small_is_kept_as_it_is()
    {
        var source = Out("pobre.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 800, 600))
        {
            image.Quality = 30;
            image.Write(source);
        }

        var output = await CompressPhoto(source, "pobre comprimida.jpg", Percent(85));

        Assert.Equal(await File.ReadAllBytesAsync(source, Cancel), await File.ReadAllBytesAsync(output, Cancel));
    }

    [Fact]
    public async Task The_colour_profile_survives()
    {
        var source = Out("perfil.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 800, 600))
        {
            image.SetProfile(ColorProfiles.SRGB);
            image.Write(source);
        }

        var output = await CompressPhoto(source, "perfil comprimido.jpg", Percent(60));

        using var result = new MagickImage(output);
        Assert.NotNull(result.GetColorProfile());
    }

    /// <summary>A JPEG whose EXIF block says make "Test" and the given orientation and holds a small JPEG thumbnail, built byte by byte.</summary>
    private string PhotoWithExif(string name, int orientation, uint width, uint height)
    {
        byte[] thumbnail;
        using (var small = new MagickImage("plasma:orange-purple", 32, 24))
            thumbnail = small.ToByteArray(MagickFormat.Jpeg);
        var plain = File.ReadAllBytes(Photo(name + " plano.jpg", width, height));

        using var tiff = new MemoryStream();
        using var writer = new BinaryWriter(tiff);                  // little-endian, like the "II" header below
        writer.Write("II"u8.ToArray());
        writer.Write((ushort)42);
        writer.Write(8);                                            // IFD0 follows
        writer.Write((ushort)2);                                    // IFD0: make, orientation
        writer.Write((ushort)0x010F);
        writer.Write((ushort)2);
        writer.Write(5);
        writer.Write(68);
        writer.Write((ushort)0x0112);
        writer.Write((ushort)3);
        writer.Write(1);
        writer.Write((ushort)orientation);
        writer.Write((ushort)0);
        writer.Write(38);                                           // IFD1 follows
        writer.Write((ushort)2);                                    // IFD1: where the thumbnail is and how long
        writer.Write((ushort)0x0201);
        writer.Write((ushort)4);
        writer.Write(1);
        writer.Write(74);
        writer.Write((ushort)0x0202);
        writer.Write((ushort)4);
        writer.Write(1);
        writer.Write(thumbnail.Length);
        writer.Write(0);
        writer.Write("Test\0\0"u8.ToArray());                       // the make, padded to an even length: offset 74 comes next
        writer.Write(thumbnail);
        writer.Flush();

        var exif = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 }.Concat(tiff.ToArray()).ToArray();
        var segment = new byte[] { 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)((exif.Length + 2) & 0xFF) }.Concat(exif);
        var path = Out(name);
        File.WriteAllBytes(path, plain[..2].Concat(segment).Concat(plain[2..]).ToArray());          // right after the SOI marker
        return path;
    }

    [Fact]
    public async Task Exif_is_kept_except_its_thumbnail_and_the_orientation_is_applied()
    {
        var source = PhotoWithExif("girada.jpg", orientation: 6, width: 800, height: 600);        // 6: show it turned 90° clockwise
        using (var before = new MagickImage(source))
        {
            Assert.Equal((800u, 600u), (before.Width, before.Height));
            Assert.Equal(OrientationType.RightTop, before.Orientation);
            Assert.True(before.GetExifProfile()!.ThumbnailLength > 0, "the test picture should carry a thumbnail");
        }

        var output = await CompressPhoto(source, "girada comprimida.jpg", Percent(60));

        using var result = new MagickImage(output);
        Assert.Equal((600u, 800u), (result.Width, result.Height));                                  // upright now
        Assert.NotEqual(OrientationType.RightTop, result.Orientation);
        var exif = result.GetExifProfile()!;
        Assert.Equal(0u, exif.ThumbnailLength);
        Assert.Equal("Test", exif.GetValue(ExifTag.Make)!.Value);
        // The top-left corner of the original is now the top-right corner.
        var expected = PixelAt(source, 0, 0);
        var actual = PixelAt(output, 599, 0);
        for (var channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(expected[channel] - actual[channel]), 0, 24);
    }

    [Fact]
    public async Task Grey_and_grey_looking_colour_jpegs_keep_their_number_of_components()
    {
        static int Components(string path)
        {
            Assert.True(PdfCompressor.TryReadJpegHeader(File.ReadAllBytes(path), out _, out _, out var components));
            return components;
        }

        // Colour, but with R = G = B everywhere: Magick would quietly write it as a one-component JPEG unless told not to.
        var lookalike = Out("casi gris.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 600, 400))
        {
            image.Grayscale();
            image.ColorSpace = ColorSpace.sRGB;
            image.Settings.ColorType = ColorType.TrueColor;
            image.Write(lookalike);
        }
        var grey = Out("gris.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 600, 400))
        {
            image.ColorType = ColorType.Grayscale;
            image.Write(grey);
        }
        Assert.Equal(3, Components(lookalike));
        Assert.Equal(1, Components(grey));

        Assert.Equal(3, Components(await CompressPhoto(lookalike, "casi gris comprimido.jpg", Percent(60))));
        Assert.Equal(1, Components(await CompressPhoto(grey, "gris comprimido.jpg", Percent(60))));
    }

    [Fact]
    public async Task A_png_keeps_its_transparency_at_every_setting()
    {
        var source = Out("sello.png");
        using (var image = new MagickImage("plasma:orange-purple", 600, 400))
        using (var mask = new MagickImage(MagickColors.Black, 600, 400))
        {
            new Drawables().FillColor(MagickColors.White).Circle(300, 200, 300, 40).Draw(mask);
            mask.Alpha(AlphaOption.Off);                           // the mask's grey becomes the picture's opacity
            image.Alpha(AlphaOption.Set);
            image.Composite(mask, CompositeOperator.CopyAlpha);
            image.Write(source);
        }

        foreach (var (name, options) in new[]
        {
            ("sin perdida", Percent(95)), ("paleta 256", Percent(80)), ("paleta 128", Percent(55)), ("paleta 64", Percent(20)),
            ("objetivo", Target(Length(source) / 4)),
        })
        {
            var output = await CompressPhoto(source, $"sello {name}.png", options);

            using var result = new MagickImage(output);
            Assert.True(result.HasAlpha, name);
            Assert.Equal(0, PixelAt(output, 3, 3)[3]);              // still clear at the corner
            Assert.Equal(255, PixelAt(output, 300, 200)[3]);        // still solid in the middle
            Assert.True(Length(output) <= Length(source), name);
        }
    }

    [Fact]
    public async Task A_palette_png_is_much_smaller_than_the_lossless_one()
    {
        var source = Photo("captura.png", 1200, 800);

        var lossless = await CompressPhoto(source, "sin perdida.png", Percent(95));
        var palette = await CompressPhoto(source, "paleta.png", Percent(70));

        Assert.True(Length(palette) < Length(lossless) * 0.6, $"palette {Length(palette)} B, lossless {Length(lossless)} B");
        // Lossless means the same pixels.
        foreach (var (x, y) in new[] { (0, 0), (600, 400), (1199, 799), (37, 511) })
            Assert.Equal(PixelAt(source, x, y), PixelAt(lossless, x, y));
    }

    [Fact]
    public async Task A_bmp_comes_out_as_a_png()
    {
        var source = Photo("captura.bmp", 800, 600);
        Assert.Equal(".png", ImageCompressor.OutputExtension(source));

        var output = await CompressPhoto(source, "captura comprimido.png", Percent(70));

        Assert.Equal(MagickFormat.Png, new MagickImageInfo(output).Format);
        Assert.Equal((800u, 600u), SizeOf(output));
        Assert.True(Length(output) < Length(source) / 3, $"{Length(output)} B from {Length(source)} B");
    }

    [Fact]
    public async Task A_webp_gets_smaller_and_stays_a_webp()
    {
        var source = Photo("foto.webp");

        var output = await CompressPhoto(source, "foto comprimido.webp", Percent(30));

        Assert.Equal(MagickFormat.WebP, new MagickImageInfo(output).Format);
        Assert.Equal((1600u, 1067u), SizeOf(output));
        Assert.True(Length(output) < Length(source), $"{Length(output)} B from {Length(source)} B");

        var wanted = Length(output) / 2;
        var fitted = await CompressPhoto(source, "foto objetivo.webp", Target(wanted));
        Assert.InRange(Length(fitted), 1, wanted);
    }

    [Fact]
    public async Task An_avif_stays_an_avif_and_a_target_is_met()
    {
        var source = Photo("foto.avif", 900, 600);

        var output = await CompressPhoto(source, "foto comprimido.avif", Percent(30));
        Assert.Equal(MagickFormat.Avif, new MagickImageInfo(output).Format);
        Assert.True(Length(output) <= Length(source));

        var wanted = Length(source) / 2;
        var fitted = await CompressPhoto(source, "foto objetivo.avif", Target(wanted));
        Assert.InRange(Length(fitted), 1, wanted);
        Assert.Equal(MagickFormat.Avif, new MagickImageInfo(fitted).Format);
    }

    [Fact]
    public async Task A_tiff_is_lossless_at_high_quality_and_jpeg_compressed_below()
    {
        var source = Photo("escaneo.tiff", 900, 600);

        var lossless = await CompressPhoto(source, "escaneo sin perdida.tiff", Percent(95));
        var lossy = await CompressPhoto(source, "escaneo jpeg.tiff", Percent(50));

        Assert.Equal(CompressionMethod.Zip, new MagickImageInfo(lossless).Compression);
        Assert.Equal(CompressionMethod.JPEG, new MagickImageInfo(lossy).Compression);
        Assert.True(Length(lossy) < Length(lossless) / 2, $"lossy {Length(lossy)} B, lossless {Length(lossless)} B");
        Assert.Equal((900u, 600u), SizeOf(lossy));
        foreach (var (x, y) in new[] { (0, 0), (450, 300), (899, 599) })
            Assert.Equal(PixelAt(source, x, y), PixelAt(lossless, x, y));

        var wanted = Length(lossy);
        var fitted = await CompressPhoto(source, "escaneo objetivo.tiff", Target(wanted));
        Assert.InRange(Length(fitted), 1, wanted);
    }

    [Fact]
    public async Task A_picture_with_several_pages_is_refused_instead_of_losing_them()
    {
        var source = Out("paginas.tiff");
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.White, 200, 100));
            pages.Add(new MagickImage(MagickColors.Black, 200, 100));
            pages.Write(source);
        }

        await Assert.ThrowsAsync<PanelException>(() => CompressPhoto(source, "paginas comprimido.tiff", Percent(70)));

        Assert.False(File.Exists(Out("paginas comprimido.tiff")));
    }

    [Fact]
    public async Task A_picture_that_is_not_a_picture_throws_and_leaves_nothing()
    {
        var source = Out("roto.jpg");
        await File.WriteAllTextAsync(source, "this is not a jpeg", Cancel);

        await Assert.ThrowsAsync<PanelException>(() => CompressPhoto(source, "roto comprimido.jpg", Percent(70)));

        Assert.False(File.Exists(Out("roto comprimido.jpg")));
    }

    [Fact]
    public async Task A_cancelled_compression_leaves_nothing()
    {
        var source = Photo("foto.jpg");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageCompressor.CompressAsync(source, Out("cancelada.jpg"), Percent(70), null, cancelled.Token));

        Assert.False(File.Exists(Out("cancelada.jpg")));
    }

    [Theory]
    [InlineData("a.jpg", ".jpg")]
    [InlineData("a.jpeg", ".jpeg")]
    [InlineData("a.PNG", ".PNG")]
    [InlineData("a.webp", ".webp")]
    [InlineData("a.tif", ".tif")]
    [InlineData("a.avif", ".avif")]
    [InlineData("a.heic", ".jpg")]
    [InlineData("a.HEIC", ".jpg")]
    [InlineData("a.heif", ".jpg")]
    [InlineData("a.bmp", ".png")]
    public void The_output_extension_follows_the_format(string source, string expected) =>
        Assert.Equal(expected, ImageCompressor.OutputExtension(source));

    // ───────── PDF: the compressor ─────────

    [Fact]
    public async Task A_pdf_with_big_pictures_comes_out_much_smaller_and_still_renders()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var source = PdfOf("informe.pdf", picture, picture);
        var original = await File.ReadAllBytesAsync(source, Cancel);

        var output = await CompressPdf(source, "informe comprimido.pdf", Percent(70));

        Assert.True(Length(output) < Length(source) * 0.6, $"{Length(output)} B from {Length(source)} B");
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Cancel));    // the original is untouched
        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, document.PageCount);
            Assert.Equal("Informe anual", document.Info.Title);
            Assert.Equal("Autor01", document.Info.Author);
            Assert.Equal("Pruebas", document.Info.Subject);
            Assert.Equal("comprimir", document.Info.Keywords);
        }
        var pictures = PicturesIn(output);
        Assert.NotEmpty(pictures);
        foreach (var found in pictures)
        {
            Assert.Equal("/DCTDecode", found.Filter);
            Assert.Equal("/DeviceRGB", found.ColorSpace);
            Assert.InRange(found.Width, 1000, 1240);                  // 150 dpi over a 595 pt page, at most
            Assert.InRange(Math.Abs(2000 * found.Width - 3000 * found.Height), 0, 3000);   // 3 : 2 to the pixel
        }
        AssertSameLook(source, output, pages: 2);
    }

    [Fact]
    public async Task Stronger_settings_make_a_smaller_pdf()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var source = PdfOf("informe.pdf", picture);

        var light = await CompressPdf(source, "ligero.pdf", Percent(85));
        var maximum = await CompressPdf(source, "maximo.pdf", Percent(35));

        Assert.True(Length(maximum) < Length(light), $"maximum {Length(maximum)} B, light {Length(light)} B");
        Assert.True(Length(light) < Length(source));
        Assert.InRange(Assert.Single(PicturesIn(maximum)).Width, 594, 596);       // 72 dpi over a 595 pt page
        Assert.InRange(Assert.Single(PicturesIn(light)).Width, 1650, 1654);       // 200 dpi
    }

    [Fact]
    public async Task Flate_pictures_rgb_and_grey_become_jpegs_and_a_transparent_one_is_left_alone()
    {
        var rgb = Out("color.png");
        using (var image = new MagickImage("plasma:orange-purple", 1200, 800))
        {
            image.Alpha(AlphaOption.Remove);
            image.Write(rgb);
        }
        var grey = Out("gris.png");
        using (var image = new MagickImage("plasma:orange-purple", 1200, 800))
        {
            image.ColorType = ColorType.Grayscale;
            image.Write(grey);
        }
        var translucent = Out("translucido.png");
        using (var image = new MagickImage("plasma:orange-purple", 600, 400))
        {
            image.Alpha(AlphaOption.Set);
            image.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.5);
            image.Write(translucent);
        }
        var source = PdfOf("mixto.pdf", rgb, grey, translucent);
        Assert.All(PicturesIn(source), found => Assert.Equal("/FlateDecode", found.Filter));

        var output = await CompressPdf(source, "mixto comprimido.pdf", Percent(70));

        Assert.True(Length(output) < Length(source) * 0.5, $"{Length(output)} B from {Length(source)} B");
        var pictures = PicturesIn(output);
        Assert.Equal(3, pictures.Count);
        Assert.Equal(("/DCTDecode", "/DeviceRGB"), (pictures[0].Filter, pictures[0].ColorSpace));
        Assert.Equal(("/DCTDecode", "/DeviceGray"), (pictures[1].Filter, pictures[1].ColorSpace));
        Assert.Equal("/FlateDecode", pictures[2].Filter);           // its transparency mask would not survive a JPEG
        Assert.True(pictures[2].HasSoftMask);
        AssertSameLook(source, output, pages: 3);
    }

    [Fact]
    public async Task A_picture_inside_a_form_is_compressed_too()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var source = Out("formulario.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            var form = new XForm(document, XUnit.FromPoint(400), XUnit.FromPoint(267));
            using (var formGraphics = XGraphics.FromForm(form))
            using (var image = XImage.FromFile(picture))
                formGraphics.DrawImage(image, 0, 0, 400, 267);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(form, 20, 20);
            document.Save(source);
        }

        var output = await CompressPdf(source, "formulario comprimido.pdf", Percent(70));

        Assert.True(Length(output) < Length(source) * 0.6, $"{Length(output)} B from {Length(source)} B");
        var found = Assert.Single(PicturesIn(output));
        Assert.True(found.Width < 3000);
        AssertSameLook(source, output, pages: 1);
    }

    /// <summary>The concatenated IDAT data of a PNG: the zlib stream of its filtered rows, what a PDF holds for a picture with /Predictor 15.</summary>
    private static byte[] PngIdat(string path)
    {
        var png = File.ReadAllBytes(path);
        using var idat = new MemoryStream();
        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IHDR")
                Assert.Equal((8, 2, 0), (png[at + 16], png[at + 17], png[at + 20]));   // 8 bits, RGB, not interlaced
            if (type == "IDAT")
                idat.Write(png, at + 8, length);
            at += 12 + length;
        }
        return idat.ToArray();
    }

    /// <summary>A one-page PDF built object by object: a 1200 × 800 RGB picture with the given stream, filter and decode parameters.</summary>
    private string PdfWithRawPicture(string name, byte[] stream, string? filter, Action<PdfDictionary>? decodeParameters)
    {
        var path = Out(name);
        using var document = new PdfDocument();
        var page = document.AddPage();
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", 1200);
        image.Elements.SetInteger("/Height", 800);
        image.Elements.SetName("/ColorSpace", "/DeviceRGB");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        if (filter is not null)
            image.Elements.SetName("/Filter", filter);
        if (decodeParameters is not null)
        {
            var parameters = new PdfDictionary(document);
            decodeParameters(parameters);
            image.Elements["/DecodeParms"] = parameters;
        }
        image.CreateStream(stream);
        document.Internals.AddObject(image);
        var xobjects = new PdfDictionary(document);
        xobjects.Elements.SetReference("/Im0", image);
        page.Resources.Elements.SetObject("/XObject", xobjects);
        page.Contents.AppendContent().CreateStream("q 520 0 0 347 40 400 cm /Im0 Do Q"u8.ToArray());
        document.Save(path);
        return path;
    }

    [Fact]
    public async Task Pictures_with_a_png_predictor_or_no_filter_at_all_are_read_back_right()
    {
        // What Word and many other programs write for a PNG picture: its own compressed rows and the predictor that undoes them.
        var png = Out("original.png");
        using (var image = new MagickImage("plasma:orange-purple", 1200, 800))
        {
            image.Alpha(AlphaOption.Remove);
            image.Write(png, MagickFormat.Png24);
        }
        var withPredictor = PdfWithRawPicture("predictor.pdf", PngIdat(png), "/FlateDecode", parameters =>
        {
            parameters.Elements.SetInteger("/Predictor", 15);
            parameters.Elements.SetInteger("/Colors", 3);
            parameters.Elements.SetInteger("/BitsPerComponent", 8);
            parameters.Elements.SetInteger("/Columns", 1200);
        });

        // The same pixels, stored as they are.
        byte[] pixels;
        using (var image = new MagickImage(png))
        using (var collection = image.GetPixels())
            pixels = collection.ToByteArray(PixelMapping.RGB)!;
        var unfiltered = PdfWithRawPicture("sin filtro.pdf", pixels, null, null);

        foreach (var (source, filter) in new[] { (withPredictor, "/FlateDecode"), (unfiltered, "") })
        {
            Assert.Equal(filter, Assert.Single(PicturesIn(source)).Filter);

            var output = await CompressPdf(source, Path.GetFileNameWithoutExtension(source) + " comprimido.pdf", Percent(70));

            var found = Assert.Single(PicturesIn(output));
            Assert.Equal(("/DCTDecode", "/DeviceRGB"), (found.Filter, found.ColorSpace));
            Assert.Equal((1200, 800), (found.Width, found.Height));
            Assert.True(Length(output) < Length(source) / 8, $"{Length(output)} B from {Length(source)} B");
            AssertSameLook(source, output, pages: 1);
        }
    }

    [Fact]
    public async Task A_picture_whose_data_does_not_add_up_is_left_as_it_is()
    {
        // The dictionary says 1200 × 800 RGB but the stream is a few KB of noise: nothing to decode, nothing to touch.
        var noise = new byte[20_000];
        new Random(3).NextBytes(noise);
        var broken = PdfWithRawPicture("raro.pdf", noise, "/FlateDecode", null);
        var shortStream = PdfWithRawPicture("corto.pdf", noise, null, null);

        foreach (var source in new[] { broken, shortStream })
        {
            var output = await CompressPdf(source, Path.GetFileNameWithoutExtension(source) + " comprimido.pdf", Percent(70));

            var found = Assert.Single(PicturesIn(output));
            Assert.Equal((1200, 800), (found.Width, found.Height));
            Assert.NotEqual("/DCTDecode", found.Filter);
            Assert.True(Length(output) <= Length(source));
        }
    }

    [Fact]
    public async Task A_pdf_lands_under_its_target_size()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var source = PdfOf("informe.pdf", picture, picture, picture);
        Assert.True(Length(source) > 800_000);

        foreach (var target in new long[] { 400_000, 150_000 })
        {
            var output = await CompressPdf(source, $"objetivo {target}.pdf", Target(target));

            var size = Length(output);
            Assert.True(size <= target, $"{size} B is over the {target} B target");
            Assert.True(size >= target * 0.8, $"{size} B is far under the {target} B target");
            AssertSameLook(source, output, pages: 3);
        }
    }

    [Fact]
    public async Task An_impossible_target_gives_the_smallest_pdf_it_can()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var source = PdfOf("informe.pdf", picture);

        var output = await CompressPdf(source, "imposible.pdf", Target(500));

        Assert.True(Length(output) < Length(source) / 10);
        AssertSameLook(source, output, pages: 1);
    }

    [Fact]
    public async Task A_pdf_without_pictures_is_saved_without_trouble()
    {
        var source = MakePdf(Out("texto.pdf"), 3);

        var output = await CompressPdf(source, "texto comprimido.pdf", Percent(70));

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, document.PageCount);
        Assert.True(Length(output) <= Length(source));
    }

    [Fact]
    public async Task Uncompressed_content_streams_are_compressed()
    {
        var source = Out("sin comprimir.pdf");
        using (var document = new PdfDocument())
        {
            document.Options.NoCompression = true;
            document.Options.CompressContentStreams = false;
            for (var i = 0; i < 3; i++)
            {
                var page = document.AddPage();
                using var graphics = XGraphics.FromPdfPage(page);
                for (var k = 0; k < 300; k++)
                    graphics.DrawRectangle(XBrushes.Orange, k % 50 * 10, k / 50 * 20, 8, 15);
            }
            document.Save(source);
        }

        var output = await CompressPdf(source, "comprimido.pdf", Percent(70));

        Assert.True(Length(output) < Length(source) / 2, $"{Length(output)} B from {Length(source)} B");
        AssertSameLook(source, output, pages: 3);
    }

    [Fact]
    public async Task A_damaged_or_protected_pdf_throws_a_readable_error_and_leaves_nothing()
    {
        var broken = Out("roto.pdf");
        await File.WriteAllTextAsync(broken, "%PDF-1.4 this is not really a pdf", Cancel);
        var protectedPdf = Out("protegido.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "secreto";
            document.SecuritySettings.OwnerPassword = "dueno";
            document.Save(protectedPdf);
        }

        foreach (var source in new[] { broken, protectedPdf, Out("vacio.pdf") })
        {
            if (!File.Exists(source))
                await File.WriteAllBytesAsync(source, [], Cancel);
            var ex = await Assert.ThrowsAsync<PanelException>(() => CompressPdf(source, Path.GetFileNameWithoutExtension(source) + " comprimido.pdf", Percent(70)));
            Assert.Contains(Path.GetFileName(source), ex.Message);
            Assert.False(File.Exists(Out(Path.GetFileNameWithoutExtension(source) + " comprimido.pdf")));
        }
    }

    // ───────── Pieces the PDF compressor stands on ─────────

    [Fact]
    public void PNG_predictor_rows_from_a_real_encoder_are_undone_exactly()
    {
        // The IDAT data of a PNG made by Magick (a different filter on each row, chosen per row) is exactly what a PDF holds
        // for a picture with /Predictor 15.
        const int Width = 97, Height = 61;
        var path = Out("filtros.png");
        using (var image = new MagickImage("plasma:orange-purple", Width, Height))
        {
            image.Alpha(AlphaOption.Remove);
            image.Write(path, MagickFormat.Png24);
        }
        using var zlib = new ZLibStream(new MemoryStream(PngIdat(path)), CompressionMode.Decompress);
        using var filtered = new MemoryStream();
        zlib.CopyTo(filtered);
        Assert.Equal((Width * 3 + 1) * Height, filtered.Length);

        var plain = PdfCompressor.UndoPngFilters(filtered.ToArray(), Width * 3, 3, Height);

        using var expected = new MagickImage(path);
        using var pixels = expected.GetPixels();
        Assert.Equal(pixels.ToByteArray(PixelMapping.RGB), plain);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(4, 1)]
    [InlineData(3, 1)]
    public void PNG_predictor_rows_of_every_filter_type_are_undone_exactly(int filter, int bytesPerPixel)
    {
        const int Height = 9;
        var rowBytes = 13 * bytesPerPixel;
        var plain = new byte[rowBytes * Height];
        new Random(7).NextBytes(plain);

        // The PNG specification's forward filters, written out again here.
        var filtered = new byte[(rowBytes + 1) * Height];
        for (var y = 0; y < Height; y++)
        {
            filtered[y * (rowBytes + 1)] = (byte)filter;
            for (var x = 0; x < rowBytes; x++)
            {
                int a = x >= bytesPerPixel ? plain[y * rowBytes + x - bytesPerPixel] : 0;
                int b = y > 0 ? plain[(y - 1) * rowBytes + x] : 0;
                int c = x >= bytesPerPixel && y > 0 ? plain[(y - 1) * rowBytes + x - bytesPerPixel] : 0;
                var distanceA = Math.Abs(b - c);
                var distanceB = Math.Abs(a - c);
                var distanceC = Math.Abs(a + b - 2 * c);
                var predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => distanceA <= distanceB && distanceA <= distanceC ? a : distanceB <= distanceC ? b : c,
                };
                filtered[y * (rowBytes + 1) + 1 + x] = (byte)(plain[y * rowBytes + x] - predicted);
            }
        }

        Assert.Equal(plain, PdfCompressor.UndoPngFilters(filtered, rowBytes, bytesPerPixel, Height));
        filtered[0] = 9;                                              // not a PNG filter type
        Assert.Null(PdfCompressor.UndoPngFilters(filtered, rowBytes, bytesPerPixel, Height));
    }

    [Fact]
    public void A_jpeg_header_gives_its_size_and_components()
    {
        var colour = Out("color.jpg");
        MakePhoto(colour, 321, 123);
        var grey = Out("gris.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 64, 48))
        {
            image.ColorType = ColorType.Grayscale;
            image.Write(grey);
        }

        Assert.True(PdfCompressor.TryReadJpegHeader(File.ReadAllBytes(colour), out var width, out var height, out var components));
        Assert.Equal((321, 123, 3), (width, height, components));
        Assert.True(PdfCompressor.TryReadJpegHeader(File.ReadAllBytes(grey), out width, out height, out components));
        Assert.Equal((64, 48, 1), (width, height, components));
        Assert.False(PdfCompressor.TryReadJpegHeader("not a jpeg at all"u8, out _, out _, out _));
        Assert.False(PdfCompressor.TryReadJpegHeader(File.ReadAllBytes(colour).AsSpan(0, 10), out _, out _, out _));
    }

    // ───────── The panel ─────────

    [AvaloniaFact]
    public async Task The_panel_renders_in_each_mode_with_a_photo()
    {
        var photo = Photo("foto.jpg");
        var preset = await OpenAsync(photo);
        Render(preset, "compress-preset");
        Assert.Equal("Comprimir", preset.Title);

        var percent = await OpenAsync(photo);
        percent.SelectMode(CompressMode.Percent);
        percent.Percent = 55;
        Render(percent, "compress-percent");

        var target = await OpenAsync(photo);
        target.SelectMode(CompressMode.TargetSize);
        Render(target, "compress-target");
        // The box starts at about half of the photo, in KB (the photo is some hundreds of KB).
        Assert.Equal(0, target.TargetUnit);
        Assert.InRange(target.TargetBytes, Length(photo) * 0.45, Length(photo) * 0.55);
    }

    [AvaloniaFact]
    public async Task The_panel_renders_a_pdf_several_photos_and_the_result_line()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var pdf = PdfOf("informe.pdf", picture, picture);
        Render(await OpenAsync(pdf), "compress-pdf");
        var pdfPercent = await OpenAsync(pdf);
        pdfPercent.SelectMode(CompressMode.Percent);
        Render(pdfPercent, "compress-pdf-percent");
        var pdfTarget = await OpenAsync(pdf);
        pdfTarget.SelectMode(CompressMode.TargetSize);
        Render(pdfTarget, "compress-pdf-target");

        var many = await OpenAsync(Photo("a.jpg"), Photo("b.jpg", 1200, 800), Photo("c.png", 900, 600));
        many.SelectMode(CompressMode.TargetSize);
        Assert.Equal("3 archivos", many.SummaryText[..10]);
        Render(many, "compress-many");

        var done = await OpenAsync(Photo("final.jpg"));
        await done.RunAsync();
        Assert.True(done.IsDone, done.Status);
        Render(done, "compress-done");
    }

    [AvaloniaFact]
    public async Task The_panel_renders_the_audio_and_video_options()
    {
        var lossless = new[] { Out("a.wav"), Out("b.flac") };
        foreach (var path in lossless)
            await File.WriteAllBytesAsync(path, new byte[64_000], Cancel);
        var audio = await OpenAsync(lossless);
        Assert.True(audio.IsLosslessAudio);
        Assert.False(audio.ShowModes);
        Render(audio, "compress-audio-lossless");

        var mixed = new[] { Out("c.wav"), Out("d.mp3") };
        foreach (var path in mixed)
            await File.WriteAllBytesAsync(path, new byte[64_000], Cancel);
        var some = await OpenAsync(mixed);
        Assert.False(some.IsLosslessAudio);
        Assert.True(some.ShowLosslessSome);
        Assert.False(some.CanResize);
        Render(some, "compress-audio-mixed");

        var clip = Out("clip.mp4");
        await File.WriteAllBytesAsync(clip, new byte[900_000], Cancel);
        var video = await OpenAsync(clip);
        Assert.True(video.CanResize);
        Assert.Equal(new[] { "Original", "1080 p", "720 p", "480 p" }, video.ResizeChoices.ToArray());
        Render(video, "compress-video");
    }

    [AvaloniaFact]
    public async Task The_panel_renders_in_english_with_a_missing_file_error()
    {
        var panel = new CompressPanel(ContextIn("en", Out("no existe.jpg")));
        await WaitAsync(() => !panel.IsLoading);
        Assert.NotNull(panel.LoadError);
        Assert.False(panel.CanApply);
        Render(panel, "compress-error-en");
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_panel_fits_in_every_language(string language)
    {
        var photo = Photo("foto.jpg");
        var panel = new CompressPanel(ContextIn(language, photo));
        await WaitAsync(() => !panel.IsLoading);
        panel.SelectMode(CompressMode.TargetSize);                 // the widest of the three modes
        Render(panel, $"compress-target-{language}");

        var done = new CompressPanel(ContextIn(language, photo));
        await WaitAsync(() => !done.IsLoading);
        await done.RunAsync();
        Assert.True(done.IsDone, done.Status);
        Assert.Contains("→", done.ResultText);
        Render(done, $"compress-done-{language}");
    }

    [AvaloniaFact]
    public async Task Compressing_a_photo_saves_a_smaller_copy_beside_the_original()
    {
        var file = Photo("foto.jpg");
        var original = await File.ReadAllBytesAsync(file, Cancel);
        var panel = await OpenAsync(file);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Out("foto comprimido.jpg"), output);
        Assert.Equal(original, await File.ReadAllBytesAsync(file, Cancel));            // the original is untouched
        Assert.True(Length(output) < Length(file) / 2, $"{Length(output)} B from {Length(file)} B");
        Assert.Equal((1600u, 1067u), SizeOf(output));
        Assert.Matches(@"^[\d.,]+ (KB|MB) → [\d.,]+ (KB|MB) \(−\d+ %\)$", panel.ResultText);
        Assert.True(panel.HasResult);

        // Another run does not overwrite: "foto comprimido 2.jpg".
        var again = await OpenAsync(file);
        await again.RunAsync();
        Assert.Equal(Out("foto comprimido 2.jpg"), Assert.Single(again.Outputs));
        Assert.True(File.Exists(output));
    }

    [AvaloniaFact]
    public async Task The_percentage_and_the_resize_options_reach_the_compressor()
    {
        var file = Photo("foto.jpg");
        var gentle = await OpenAsync(file);
        gentle.SelectMode(CompressMode.Percent);
        gentle.Percent = 90;
        await gentle.RunAsync();
        var harsh = await OpenAsync(file);
        harsh.SelectMode(CompressMode.Percent);
        harsh.Percent = 20;
        harsh.ResizeIndex = 6;                                      // 800 px
        await harsh.RunAsync();

        Assert.True(gentle.IsDone && harsh.IsDone, harsh.Status);
        Assert.True(Length(Assert.Single(harsh.Outputs)) < Length(Assert.Single(gentle.Outputs)) / 5);
        Assert.Equal(800u, SizeOf(harsh.Outputs[0]).Width);
        Assert.Equal(1600u, SizeOf(gentle.Outputs[0]).Width);
        Assert.Equal(new[] { "Original", "4096 px", "2048 px", "1600 px", "1280 px", "1024 px", "800 px" }, harsh.ResizeChoices.ToArray());
    }

    [AvaloniaFact]
    public async Task A_target_size_typed_in_the_panel_is_respected()
    {
        var file = Photo("foto.jpg");
        var panel = await OpenAsync(file);
        panel.SelectMode(CompressMode.TargetSize);
        panel.TargetUnit = 0;
        panel.TargetText = "120";
        Assert.Equal(120_000, panel.TargetBytes);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.InRange(Length(Assert.Single(panel.Outputs)), 40_000, 120_000);

        var megabytes = await OpenAsync(file);
        megabytes.SelectMode(CompressMode.TargetSize);
        megabytes.TargetUnit = 1;
        megabytes.TargetText = "0,2";                               // a decimal comma is a decimal point
        Assert.Equal(200_000, megabytes.TargetBytes);
    }

    [AvaloniaFact]
    public async Task Apply_needs_a_usable_target_in_target_mode()
    {
        var panel = await OpenAsync(Photo("foto.jpg"));
        Assert.True(panel.CanApply);
        panel.SelectMode(CompressMode.TargetSize);
        Assert.True(panel.CanApply);                                // starts at about half of the photo
        foreach (var text in new[] { "", "0", "abc", "-5", "." })
        {
            panel.TargetText = text;
            Assert.False(panel.CanApply, $"'{text}'");
        }
        panel.TargetText = "1.5";
        Assert.True(panel.CanApply);
        Assert.Equal(1500, panel.TargetBytes);
        panel.SelectMode(CompressMode.Preset);
        panel.TargetText = "";
        Assert.True(panel.CanApply);                                // the other modes do not look at it
    }

    [AvaloniaFact]
    public async Task Several_photos_are_each_compressed_beside_their_originals()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.png", 1200, 800);
        var panel = await OpenAsync(a, b);
        Assert.StartsWith("2 archivos · ", panel.SummaryText);
        Assert.Equal("Tamaño objetivo (por archivo)", panel.TargetLabel);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(new[] { Out("a comprimido.jpg"), Out("b comprimido.png") }, panel.Outputs.ToArray());
        Assert.Equal("Guardados 2 archivos", panel.Status);
        Assert.Matches(@"→", panel.ResultText);
    }

    [AvaloniaFact]
    public async Task If_one_photo_fails_none_is_kept()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var broken = Out("c.jpg");
        await File.WriteAllTextAsync(broken, "not a picture", Cancel);
        var panel = await OpenAsync(a, b, broken);

        await panel.RunAsync();

        Assert.True(panel.HasError, panel.Status);
        Assert.False(panel.IsDone);
        Assert.Contains("«c.jpg» está dañado", panel.Status);            // said in the user's language, not Magick's
        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray());
        Assert.Null(panel.ResultText);
    }

    [AvaloniaFact]
    public async Task A_damaged_pdf_is_reported_in_the_users_language()
    {
        var broken = Out("roto.pdf");
        await File.WriteAllTextAsync(broken, "%PDF-1.4 not really a pdf", Cancel);
        var panel = await OpenAsync(broken);

        await panel.RunAsync();

        Assert.True(panel.HasError, panel.Status);
        Assert.Contains("«roto.pdf» está dañado", panel.Status);
        Assert.Equal(new[] { "roto.pdf" }, Directory.GetFiles(_root).Select(Path.GetFileName).ToArray());
    }

    [AvaloniaFact]
    public async Task Something_already_small_says_it_was_already_optimized()
    {
        var file = Out("pobre.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 800, 600))
        {
            image.Quality = 30;
            image.Write(file);
        }
        var panel = await OpenAsync(file);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal("Ya estaba optimizado", panel.ResultText);
        Assert.Equal(await File.ReadAllBytesAsync(file, Cancel), await File.ReadAllBytesAsync(panel.Outputs[0], Cancel));
    }

    [AvaloniaFact]
    public async Task A_pdf_goes_through_the_panel_too()
    {
        var picture = Photo("grande.jpg", 3000, 2000);
        var pdf = PdfOf("informe.pdf", picture, picture);
        var panel = await OpenAsync(pdf);
        panel.SelectPreset(panel.Presets[2]);                       // Strong
        Assert.Equal(panel.Presets[2].Hint, panel.PresetHint);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(Out("informe comprimido.pdf"), Assert.Single(panel.Outputs));
        Assert.True(Length(panel.Outputs[0]) < Length(pdf) / 4);
        Assert.False(panel.CanResize);
        AssertSameLook(pdf, panel.Outputs[0], pages: 2);
    }

    [AvaloniaFact]
    public async Task Audio_and_video_need_ffmpeg_and_the_panel_says_so_without_it()
    {
        // With FFmpeg next to the tests the panel is ready; without it Apply is off and the reason is shown.
        var clip = Out("clip.mp4");
        await File.WriteAllBytesAsync(clip, new byte[10_000], Cancel);
        var panel = await OpenAsync(clip);

        Assert.Equal(MediaTools.Locate() is null, panel.HasWarning);
        Assert.Equal(MediaTools.Locate() is not null, panel.CanApply);
        Assert.Equal(!panel.HasWarning, panel.WarningText is null);
    }

    [AvaloniaFact]
    public void The_wheel_opens_this_panel_for_every_kind_of_file()
    {
        var loc = Context(Out("a.jpg")).Loc;
        var platform = Context(Out("a.jpg")).Platform;
        Assert.True(PanelRegistry.Has("compress"));
        Assert.Null(PanelRegistry.Unavailable("compress", loc));            // photos and PDFs need no FFmpeg, so it is never greyed out

        string[][] selections = [[@"C:\a.jpg"], [@"C:\a.png", @"C:\b.png"], [@"C:\a.pdf"], [@"C:\a.mp3"], [@"C:\a.mp4", @"C:\b.mov"]];
        foreach (var files in selections)
        {
            var entry = Assert.Single(WheelCatalog.ToolsFor(files, loc), tool => tool.Id == "compress");
            Assert.True(entry.OpensPanel, string.Join(", ", files));
        }

        var file = Photo("foto.jpg");
        using var panel = PanelRegistry.Create("compress", [file], loc, platform);
        Assert.IsType<CompressPanel>(panel);
    }
}
