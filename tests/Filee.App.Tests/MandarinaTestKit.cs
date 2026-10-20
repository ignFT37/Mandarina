// Mandarina: helpers shared by the tool panels' tests: a panel context, waiting for async work, rendering a panel window
// to a PNG (and checking its options are not clipped), and generating small real media with FFmpeg / Magick.NET.

using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Mandarina.Panels;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using ImageMagick;

namespace Filee.App.Tests;

internal static class MandarinaTestKit
{
    /// <summary>A panel context for <paramref name="files"/>, in Spanish (the app's default language here).</summary>
    public static PanelContext Context(params string[] files) => ContextIn("es", files);

    /// <summary>A panel context for <paramref name="files"/> in <paramref name="language"/> ("en", "es", "ko", "zh-CN").</summary>
    public static PanelContext ContextIn(string language, params string[] files)
    {
        TestServices.EnsureInitialized(language);
        return new PanelContext(files, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
    }

    /// <summary>Lets the dispatcher run the panel's async continuations until <paramref name="done"/> (fails after the timeout).</summary>
    public static async Task WaitAsync(Func<bool> done, int milliseconds = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(done(), "timed out waiting for the panel");
    }

    /// <summary>Runs queued UI work and render ticks so a shown window is drawn.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Shows the panel in its window, saves a screenshot as <c>screenshots\mandarina-panel-{name}.png</c> next to the test
    /// binaries, asserts the options are not clipped, and closes the window. Returns the screenshot's path.
    /// </summary>
    public static string Render(ToolPanel panel, string name)
    {
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        using var frame = window.CaptureRenderedFrame(); // native memory: disposed, not left to the finalizer
        Assert.NotNull(frame);
        var path = Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-panel-{name}.png");
        using (var stream = File.Create(path))
            frame.Save(stream, new PngBitmapEncoderOptions());
        var (needed, available) = window.ContentRoom();
        window.Close();
        Assert.True(needed <= available + 1, $"the options need {needed:0} px and the window gives them {available:0}: raise PreferredSize");
        return path;
    }

    /// <summary>
    /// Runs FFmpeg with <paramref name="ffmpegArguments"/> (the output file at the end) to make a small test file at
    /// <paramref name="path"/>. Null when FFmpeg is not installed next to the tests (the test should <c>Assert.Skip</c>).
    /// </summary>
    public static async Task<string?> MakeMediaAsync(string path, params string[] ffmpegArguments)
    {
        if (MediaTools.Locate() is not { } media)
            return null;
        await media.RunAsync([.. ffmpegArguments, path]);
        return path;
    }

    /// <summary>A coloured test pattern with a gradient and some structure: a 4:3 JPG or PNG by extension.</summary>
    public static string MakePhoto(string path, uint width = 900, uint height = 600)
    {
        using var image = new MagickImage("plasma:orange-purple", width, height);
        image.Write(path);
        return path;
    }

    /// <summary>
    /// A PDF with <paramref name="pages"/> pages. Page <c>n</c> is filled with its own colour (no fonts needed) and
    /// carries <c>n</c> small black squares, so pages can be told apart in the file and when rendered.
    /// </summary>
    public static string MakePdf(string path, int pages)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (var i = 1; i <= pages; i++)
        {
            var page = document.AddPage();
            using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            var colour = PdfSharp.Drawing.XColor.FromArgb(255, (i * 70) % 200 + 40, (i * 110) % 200 + 40, (i * 150) % 200 + 40);
            graphics.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(colour), 0, 0, page.Width.Point, page.Height.Point);
            for (var square = 0; square < i; square++)
                graphics.DrawRectangle(PdfSharp.Drawing.XBrushes.Black, 40 + square * 50, 40, 30, 30);
        }
        document.Save(path);
        return path;
    }

    /// <summary>How many times <paramref name="pattern"/> occurs in the file.</summary>
    public static int CountIn(string path, params byte[] pattern)
    {
        var data = File.ReadAllBytes(path);
        var count = 0;
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            var same = true;
            for (var j = 0; j < pattern.Length && same; j++)
                same = data[i + j] == pattern[j];
            if (same)
                count++;
        }
        return count;
    }


    /// <summary>
    /// A JPEG the way a camera writes it, with every hidden copy of the picture it can hold: a thumbnail in the EXIF
    /// (a whole second JPEG, in IFD1), a preview in the maker note, and an XMP block with a thumbnail. Also the camera make
    /// and, when <paramref name="orientation"/> is not 1, the orientation.
    /// </summary>
    public static string MakePhotoWithHiddenCopies(string path, uint width = 900, uint height = 600, ushort orientation = 1)
    {
        using var picture = new MagickImage("plasma:orange-purple", width, height);
        picture.Strip();
        using var small = (MagickImage)picture.Clone();
        small.Resize(new MagickGeometry(64, 64));
        small.Strip();
        var preview = small.ToByteArray(MagickFormat.Jpeg);
        var main = picture.ToByteArray(MagickFormat.Jpeg);

        var make = System.Text.Encoding.ASCII.GetBytes("Mandarina Cam\0");            // 14 bytes
        var note = System.Text.Encoding.ASCII.GetBytes("Cam\0").Concat(preview).ToArray();
        using var tiff = new MemoryStream();
        void Short(int value) { tiff.WriteByte((byte)(value >> 8)); tiff.WriteByte((byte)value); }
        void Long(int value) { Short(value >> 16); Short(value & 0xFFFF); }
        void Entry(int tag, int type, int count, int value) { Short(tag); Short(type); Long(count); if (type == 3) { Short(value); Short(0); } else Long(value); }
        void Pad() { if (tiff.Length % 2 == 1) tiff.WriteByte(0); }

        var makeAt = 8 + 2 + 3 * 12 + 4;                                              // after IFD0 (3 entries)
        var exifIfdAt = makeAt + make.Length;
        var noteAt = exifIfdAt + 2 + 12 + 4;                                          // after the Exif IFD (1 entry)
        var ifd1At = noteAt + note.Length + note.Length % 2;
        var thumbnailAt = ifd1At + 2 + 2 * 12 + 4;
        tiff.Write("MM"u8);
        Short(0x2A);
        Long(8);                                                                      // IFD0 follows the header
        Short(3);
        Entry(0x010F, 2, make.Length, makeAt);                                        // Make
        Entry(0x0112, 3, 1, orientation);                                             // Orientation
        Entry(0x8769, 4, 1, exifIfdAt);                                               // the Exif IFD
        Long(ifd1At);                                                                 // the next IFD is the thumbnail
        tiff.Write(make);
        Short(1);
        Entry(0x927C, 7, note.Length, noteAt);                                        // MakerNote, with a preview inside
        Long(0);
        tiff.Write(note);
        Pad();
        Short(2);
        Entry(0x0201, 4, 1, thumbnailAt);                                             // JPEGInterchangeFormat
        Entry(0x0202, 4, 1, preview.Length);                                          // JPEGInterchangeFormatLength
        Long(0);
        tiff.Write(preview);

        byte[] Segment(byte[] body)
        {
            var bytes = new List<byte> { 0xFF, 0xE1, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2) };
            bytes.AddRange(body);
            return [.. bytes];
        }
        var exif = Segment([.. "Exif\0\0"u8.ToArray(), .. tiff.ToArray()]);
        var xmp = Segment(System.Text.Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\"><xmpGImg:image>/9j/4AAQSkZJRgABAQAAAQABAAD/2wBD</xmpGImg:image></rdf:Description></rdf:RDF></x:xmpmeta>"));

        using var file = File.Create(path);
        file.Write(main, 0, 2);                                                       // SOI
        file.Write(exif);
        file.Write(xmp);
        file.Write(main, 2, main.Length - 2);
        return path;
    }
}
