// Mandarina: REDACT for a photo. Drag over the photo to mark what to hide (or let Windows find the faces or
// the text, locally), choose a flat colour, a blur or a pixelation, and Apply saves "name redacted.ext" beside the
// original, at full resolution. The preview is a reduced copy; the areas are kept in pixels of the original.

using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("redactImage")]
public sealed partial class RedactImagePanel : RedactPanelBase
{
    /// <summary>The longer side of the preview the areas are drawn on.</summary>
    private const int PreviewSide = 1400;

    /// <summary>The longer side of the copy the detectors read.</summary>
    private const int DetectionSide = 2000;

    /// <summary>
    /// Remove the camera, date and place. Ticked by default. Whatever it says, the copies of the photo that metadata can
    /// hold (the EXIF thumbnail, XMP and Photoshop previews) never go along: they would show what was covered.
    /// </summary>
    [ObservableProperty] private bool _stripMetadata = true;

    [ObservableProperty] private string? _sizeText;

    public RedactImagePanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(900, 690);

    public override Control CreateView() => new RedactImageView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var preview = await PanelImages.LoadAsync(Files[0], PreviewSide, Loading.Token);
            SourceWidth = preview.Width;
            SourceHeight = preview.Height;
            SizeText = $"{preview.Width} × {preview.Height}";
            Preview = preview.Bitmap;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "loading" for ever.
            // Magick's own message names a temporary path and a line of its source: say it plainly instead.
            LoadError = ex is MagickException ? Text("cannot_read") : Context.Explain(ex);
        }
        IsLoading = false;
    }

    protected override Task<DetectionInput> PrepareDetectionAsync(CancellationToken cancel) =>
        Task.Run(() =>
        {
            using var image = PanelImages.Read(Files[0]);
            double width = image.Width, height = image.Height;
            PanelImages.Fit(image, DetectionSide);
            // Windows reads transparent pixels as black: put the picture on white.
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
            var png = OutputPaths.Temp(".png");
            try
            {
                image.Write(png, MagickFormat.Png);
            }
            catch
            {
                MediaTools.TryDelete(png);
                throw;
            }
            return new DetectionInput(png, width / image.Width, height / image.Height);
        }, cancel);

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var source = Files[0];
        var output = Context.OutputFor(source, "redactImage", PanelImages.OutputExtension(source));
        // The window stays usable while this runs: work on a copy of the choices.
        var areas = SnapshotRegions().Select(region => region.Rect).ToArray();
        var (effect, fill, strength, block, strip) = (Effect, FillColor, Strength, BlockSize, StripMetadata);
        try
        {
            await Task.Run(() =>
            {
                using var image = PanelImages.Read(source);
                for (var i = 0; i < areas.Length; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    RedactEffects.Cover(image, areas[i], effect, fill, strength, block);
                    progress.Report((i + 1.0) / (areas.Length + 1));
                }
                CleanMetadata(image, strip);
                PanelImages.Save(image, output);
            }, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        return [output];
    }

    /// <summary>What the copy carries besides the picture: see <see cref="PanelImages.CleanMetadata"/>.</summary>
    internal static void CleanMetadata(MagickImage image, bool strip) => PanelImages.CleanMetadata(image, strip);
}
