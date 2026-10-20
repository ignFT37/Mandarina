// Mandarina: the PDF side of the ORGANIZE panel. Read opens the PDF once to learn how many pages it has and what shape
// they are (PdfSharp for the pages, PDFium for the size the PDF shows them at) and tells a password-protected file from an
// unreadable one; Write builds the new PDF from the chosen pages (PdfSharp), never touching the original.

using System.Drawing;
using Filee.App.Mandarina.Panels;
using PDFtoImage;
using PDFtoImage.Exceptions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// PDFtoImage marks its API for the platforms PDFium ships for; Filee only targets desktop platforms, all covered.
#pragma warning disable CA1416

namespace Filee.App.Mandarina.Tools;

/// <summary>The PDF asks for a password (or is protected in a way the engines do not open).</summary>
internal sealed class PdfPasswordException(Exception inner) : Exception("The PDF is password-protected.", inner);

/// <summary>The file on disk has other pages than when the panel opened it.</summary>
internal sealed class PdfChangedException() : Exception("The PDF changed since it was opened.");

/// <summary>What the panel needs to know about the PDF: its bytes, page count and the shape of each page (width ÷ height as shown).</summary>
internal sealed record PdfOverview(byte[] Bytes, int PageCount, double[] Aspects, bool CanRender);

internal static class PdfPageOrganizer
{
    /// <summary>An A4 page, for the pages whose size is not known.</summary>
    internal const double DefaultAspect = 0.7071;

    /// <summary>PDFium is not thread-safe: everything that calls it in this panel takes this lock first.</summary>
    internal static readonly object PdfiumLock = new();

    /// <summary>
    /// Reads the PDF at <paramref name="path"/>. Throws <see cref="PdfPasswordException"/> for a protected file and the
    /// engine's own exception (IO, InvalidOperation, …) for one that is not a readable PDF.
    /// </summary>
    internal static PdfOverview Read(string path, CancellationToken cancel)
    {
        var bytes = File.ReadAllBytes(path);
        cancel.ThrowIfCancellationRequested();

        int count;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            count = document.PageCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsPasswordProblem(ex, bytes))
                throw new PdfPasswordException(ex);
            throw;
        }
        cancel.ThrowIfCancellationRequested();

        // The size PDFium draws each page at (crop box and rotation applied). Without it the pages can still be organized,
        // only without pictures.
        IList<SizeF> sizes;
        try
        {
            lock (PdfiumLock)
                sizes = Conversion.GetPageSizes(bytes, null);
        }
        catch (Exception)
        {
            sizes = [];
        }
        var aspects = new double[count];
        for (var i = 0; i < count; i++)
        {
            aspects[i] = i < sizes.Count && sizes[i].Width > 0 && sizes[i].Height > 0
                ? Math.Clamp(sizes[i].Width / (double)sizes[i].Height, 0.1, 10)
                : DefaultAspect;
        }
        return new PdfOverview(bytes, count, aspects, CanRender: sizes.Count > 0);
    }

    /// <summary>
    /// Saves <paramref name="pages"/> of <paramref name="source"/> as a new PDF at <paramref name="output"/> (a path that
    /// does not exist yet; it is never overwritten). A page chosen twice is imported twice. Whatever fails or is cancelled
    /// leaves no file behind.
    /// </summary>
    /// <param name="expectedPages">The page count the panel saw: a PDF that changed since then would make the choice meaningless.</param>
    internal static void Write(string source, string output, IReadOnlyList<PageEdit> pages, int expectedPages, IProgress<double>? progress, CancellationToken cancel)
    {
        var created = false;
        try
        {
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            if (input.PageCount != expectedPages)
                throw new PdfChangedException();

            using var result = new PdfDocument();
            result.Version = Math.Max(result.Version, input.Version);
            result.Info.Title = input.Info.Title;
            result.Info.Author = input.Info.Author;
            result.Info.Subject = input.Info.Subject;
            result.Info.Keywords = input.Info.Keywords;

            for (var i = 0; i < pages.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var edit = pages[i];
                var original = input.Pages[edit.Source];
                var page = result.AddPage(original);
                // The turn adds to the page's own rotation (inherited from the page tree or not: PdfSharp reports both).
                if (edit.Turn != 0)
                    page.Rotate = Quarter(original.Rotate + edit.Turn);
                progress?.Report((i + 1.0) / pages.Count * 0.9);
            }

            cancel.ThrowIfCancellationRequested();
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            result.Save(file, closeStream: false);
        }
        catch
        {
            if (created)
                MediaTools.TryDelete(output);
            throw;
        }
        progress?.Report(1);
    }

    /// <summary>A rotation as PDF wants it, a multiple of 90 from 0 to 270 (PDFs in the wild carry -90 or 450).</summary>
    internal static int Quarter(int degrees) => (degrees / 90 % 4 + 4) % 4 * 90;

    private static bool IsPasswordProblem(Exception ex, byte[] bytes)
    {
        if (ex is PdfReaderException && ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            lock (PdfiumLock)
                Conversion.GetPageCount(bytes, null);
            return false;
        }
        catch (Exception probe) when (probe is PdfPasswordProtectedException or PdfUnsupportedSecuritySchemeException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
