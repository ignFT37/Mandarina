// Mandarina: CROP for photos. Pick a proportion or type an exact size and drag the box on the preview; with
// several photos each one is cut around its centre instead. Apply saves "name cropped.ext" beside every original, all or
// none (CropPanelBase has the box, the pills and the size boxes; CropSurface draws and drags it).

using Avalonia;
using Avalonia.Controls;
using Filee.App.Mandarina.Panels;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("cropImage")]
public sealed class CropImagePanel : CropPanelBase
{
    /// <summary>Longest side of the preview: plenty for a box to be placed by eye, and quick to decode.</summary>
    private const int PreviewSide = 1400;

    public CropImagePanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(880, 540);

    public override bool IsMulti => Files.Count > 1;

    public string FileName => Path.GetFileName(Files[0]);

    protected override string? Hint => IsMulti
        ? Context.Format("panel.cropImage.hint_many", Files.Count)
        : Context["panel.cropImage.hint"];

    public override Control CreateView() => new CropImageView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var picture = await PanelImages.LoadAsync(Files[0], PreviewSide, LoadToken);
            Preview = picture.Bitmap;
            SetPicture((int)picture.Width, (int)picture.Height);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (MagickException)
        {
            // Magick's own message ("insufficient image data in file … @ error/jpeg.c/ReadOneJPEGImage/1554") says nothing to a person.
            LoadError = Context["panel.cropImage.cannot_open"];
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "opening" for ever.
            LoadError = Context.Explain(ex);
        }
        IsLoading = false;
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var centred = IsMulti;
        var box = Selection;
        var (aspect, typedSize) = CentredPlan;
        var written = new List<string>();
        try
        {
            for (var i = 0; i < Files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var source = Files[i];
                // Chosen one by one: two sources that would share a name (a.heic and a.jpg → "a cropped.jpg") get " 2".
                var output = Context.OutputFor(source, "cropImage", PanelImages.OutputExtension(source));
                await Task.Run(() => CropOne(source, output, box, centred, aspect, typedSize), cancel);
                written.Add(output);
                progress.Report((i + 1) / (double)Files.Count);
            }
        }
        catch
        {
            // All or nothing.
            foreach (var path in written)
                MediaTools.TryDelete(path);
            throw;
        }
        return written;
    }

    /// <summary>Cuts <paramref name="source"/> to the box (or to its centred box) and saves it as <paramref name="output"/>.</summary>
    private static void CropOne(string source, string output, Rect box, bool centred, double aspect, Size? typedSize)
    {
        try
        {
            using var image = PanelImages.Read(source);
            int width = (int)image.Width, height = (int)image.Height;
            var cut = centred ? CropMath.CenteredCrop(width, height, aspect, typedSize) : CropMath.Snap(box, width, height);
            image.Crop(new MagickGeometry((int)cut.X, (int)cut.Y, (uint)cut.Width, (uint)cut.Height));
            image.ResetPage();
            PanelImages.Save(image, output);
        }
        catch (MagickException ex)
        {
            MediaTools.TryDelete(output);
            // The cause in the user's language and the file it is about (Magick's own text goes to the log).
            throw PanelErrors.FromPicture(ex, Path.GetFileName(source));
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
    }
}
