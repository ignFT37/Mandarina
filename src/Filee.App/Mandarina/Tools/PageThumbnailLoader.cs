// Mandarina: draws the thumbnails of the ORGANIZE panel. One background task renders the pages with PDFium one after
// another, the pages the panel says are on screen first and the rest in order, and hands each finished picture to the
// interface thread. A 300-page PDF opens at once and fills in as the pictures arrive.

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PDFtoImage;
using SkiaSharp;

// PDFtoImage marks its API for the platforms PDFium ships for; Filee only targets desktop platforms, all covered.
#pragma warning disable CA1416

namespace Filee.App.Mandarina.Tools;

internal sealed class PageThumbnailLoader : IDisposable
{
    /// <summary>Height in pixels the pages are drawn at: a little more than the 150 they are shown at.</summary>
    internal const int PixelHeight = 220;

    private static readonly RenderOptions Options =
        new(Height: PixelHeight, WithAnnotations: true, WithAspectRatio: true, BackgroundColor: SKColors.White);

    private readonly byte[] _pdf;
    private readonly bool[] _done;
    private readonly Action<int, Bitmap?> _deliver;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private int[] _priority = [];
    private int _cursor;

    /// <param name="deliver">Called on the interface thread with a page and its picture (null when PDFium could not draw it).</param>
    public PageThumbnailLoader(byte[] pdf, int pageCount, Action<int, Bitmap?> deliver)
    {
        _pdf = pdf;
        _done = new bool[pageCount];
        _deliver = deliver;
        _ = Task.Run(Work);
    }

    /// <summary>The pages to draw first, in this order (the ones on screen). Later calls replace earlier ones.</summary>
    public void Prioritize(IReadOnlyList<int> pages)
    {
        lock (_gate)
            _priority = pages.Where(page => (uint)page < (uint)_done.Length).Distinct().ToArray();
    }

    private void Work()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var page = PickNext();
                if (page < 0)
                    return;
                Bitmap? bitmap = null;
                try
                {
                    bitmap = Render(page);
                }
                catch (Exception)
                {
                    // A page PDFium cannot draw keeps its grey stand-in; the others still come.
                }
                lock (_gate)
                    _done[page] = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_stop.IsCancellationRequested)
                        _deliver(page, bitmap);
                    else
                        bitmap?.Dispose();
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception)
        {
            // Thumbnails are a courtesy: whatever goes wrong here must not take the app down.
        }
    }

    /// <summary>The next page to draw: a prioritized one that is still missing, else the next missing one after the last drawn.</summary>
    private int PickNext()
    {
        lock (_gate)
        {
            foreach (var page in _priority)
            {
                if (!_done[page])
                {
                    _cursor = page + 1;
                    return page;
                }
            }
            for (var step = 0; step < _done.Length; step++)
            {
                var page = (_cursor + step) % _done.Length;
                if (!_done[page])
                {
                    _cursor = page + 1;
                    return page;
                }
            }
            return -1;
        }
    }

    private Bitmap Render(int page)
    {
        SKBitmap bitmap;
        lock (PdfPageOrganizer.PdfiumLock)
            bitmap = Conversion.ToImage(_pdf, page, null, Options);
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = new MemoryStream())
        {
            data.SaveTo(stream);
            stream.Position = 0;
            return new Bitmap(stream);
        }
    }

    public void Dispose() => _stop.Cancel();
}
