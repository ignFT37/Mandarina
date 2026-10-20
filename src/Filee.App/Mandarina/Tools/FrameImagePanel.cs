// Mandarina: ADD BG: a photo placed on a canvas with a colour, gradient or picture background, with padding,
// rounded corners, a soft shadow and a canvas shape (Auto, 1:1, 16:9, 4:5, 9:16). The preview on the left is the same
// picture the final render makes, drawn from a shrunken copy of the photo at no more than 900 px; Apply re-reads the
// original and renders at full size ("name with background.ext" beside it). The picture work lives in ImageCompose.

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Mandarina.Panels;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

/// <summary>What sits behind the photo.</summary>
public enum FrameBackground
{
    Color,
    Gradient,
    Image,
}

/// <summary>The shape of the canvas.</summary>
public enum FrameAspect
{
    /// <summary>The photo's own, plus the padding.</summary>
    Auto,
    Square,
    Wide,
    Portrait,
    Tall,
}

/// <summary>Everything the picture depends on, copied off the panel so a render can run on another thread.</summary>
internal sealed record FrameOptions(
    FrameBackground Background,
    string Primary,
    string Secondary,
    double Angle,
    double Padding,
    double Corners,
    bool Shadow,
    double ShadowBlur,
    double ShadowOpacity,
    double ShadowOffset,
    FrameAspect Aspect)
{
    /// <summary>Canvas width / height, or null for the photo's own shape.</summary>
    public double? Ratio => Aspect switch
    {
        FrameAspect.Square => 1,
        FrameAspect.Wide => 16.0 / 9,
        FrameAspect.Portrait => 4.0 / 5,
        FrameAspect.Tall => 9.0 / 16,
        _ => null,
    };
}

[ToolPanel("frameImage")]
public sealed partial class FrameImagePanel : ToolPanel
{
    /// <summary>The longer side of the live preview's canvas, and of the photo copy it is drawn from.</summary>
    private const int PreviewSide = 900;

    /// <summary>The picked background picture is kept this large (it only has to cover the preview).</summary>
    private const int BackgroundSide = 1600;

    /// <summary>Shadow blur 0..60 and drop 0..60 on their sliders, as a share of the canvas' longer side so preview and result match.</summary>
    private const double BlurPerStep = 0.0005, DropPerStep = 0.001;

    private readonly CancellationTokenSource _loading = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _previewing;
    private MagickImage? _photo;
    private MagickImage? _picture;
    private uint _photoWidth;
    private uint _photoHeight;
    private int _requested;
    private int _settled;
    private volatile bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColor))]
    [NotifyPropertyChangedFor(nameof(IsGradient))]
    [NotifyPropertyChangedFor(nameof(IsImage))]
    [NotifyPropertyChangedFor(nameof(ShowsPrimary))]
    private FrameBackground _background = FrameBackground.Gradient;

    [ObservableProperty] private string _primary = "#FF8E3A";
    [ObservableProperty] private string _secondary = "#A98CEF";
    [ObservableProperty] private double _angle = 135;
    [ObservableProperty] private double _padding = 10;
    [ObservableProperty] private double _corners = 4;
    [ObservableProperty] private bool _shadow = true;
    [ObservableProperty] private double _shadowBlur = 24;
    [ObservableProperty] private double _shadowOpacity = 40;
    [ObservableProperty] private double _shadowOffset = 14;
    [ObservableProperty] private FrameAspect _aspect = FrameAspect.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureName))]
    private string? _picturePath;

    [ObservableProperty] private string? _pictureError;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string? _previewError;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;

    public FrameImagePanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(900, 596);

    public bool IsColor => Background == FrameBackground.Color;

    public bool IsGradient => Background == FrameBackground.Gradient;

    public bool IsImage => Background == FrameBackground.Image;

    /// <summary>The first colour is shown for a flat colour and for a gradient.</summary>
    public bool ShowsPrimary => Background != FrameBackground.Image;

    /// <summary>The file name of the picked background picture, or a hint while none is picked.</summary>
    public string PictureName => PicturePath is { } path ? Path.GetFileName(path) : Context["panel.frameImage.no_picture"];

    /// <summary>True when the preview on screen shows the options as they are now (none is waiting or being drawn); tests wait on it.</summary>
    public bool PreviewIsCurrent => _settled == _requested;

    /// <summary>The photo's real size (after EXIF rotation), known once it has loaded.</summary>
    public (uint Width, uint Height) PhotoSize => (_photoWidth, _photoHeight);

    public override bool CanApply => _photo is not null && LoadError is null && (Background != FrameBackground.Image || _picture is not null);

    public override Control CreateView() => new FrameImageView { DataContext = this };

    [RelayCommand]
    private void SetBackground(FrameBackground kind) => Background = kind;

    [RelayCommand]
    private void SetAspect(FrameAspect aspect) => Aspect = aspect;

    private async Task LoadAsync()
    {
        try
        {
            var (photo, width, height) = await Task.Run(() =>
            {
                var image = PanelImages.Read(Files[0]);
                var size = (image.Width, image.Height);
                PanelImages.Fit(image, PreviewSide);
                return (image, size.Width, size.Height);
            }, _loading.Token);
            lock (_gate)
            {
                if (_disposed)
                {
                    photo.Dispose();
                    return;
                }
                _photo = photo;
            }
            (_photoWidth, _photoHeight) = (width, height);
            IsLoading = false;
            NotifyCanApplyChanged();
            RequestPreview();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "loading" for ever.
            LoadError = Context.Format("panel.frameImage.unreadable", Path.GetFileName(Files[0]));
            IsLoading = false;
            NotifyCanApplyChanged();
        }
    }

    /// <summary>Takes <paramref name="path"/> as the background picture (the "Image" background); an unreadable file is reported and ignored.</summary>
    public async Task SetPictureAsync(string path)
    {
        try
        {
            var picture = await Task.Run(() =>
            {
                var image = PanelImages.Read(path);
                PanelImages.Fit(image, BackgroundSide);
                return image;
            }, _loading.Token);
            MagickImage? old;
            lock (_gate)
            {
                if (_disposed)
                {
                    picture.Dispose();
                    return;
                }
                old = _picture;
                _picture = picture;
            }
            old?.Dispose();
            PicturePath = path;
            PictureError = null;
            NotifyCanApplyChanged();
            RequestPreview();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            PictureError = Context.Format("panel.frameImage.unreadable", Path.GetFileName(path));
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Background):
                NotifyCanApplyChanged();
                RequestPreview();
                break;
            case nameof(Primary) or nameof(Secondary) or nameof(Angle) or nameof(Padding) or nameof(Corners) or nameof(Shadow)
                or nameof(ShadowBlur) or nameof(ShadowOpacity) or nameof(ShadowOffset) or nameof(Aspect):
                RequestPreview();
                break;
        }
    }

    private FrameOptions Snapshot() =>
        new(Background, Primary, Secondary, Angle, Padding, Corners, Shadow, ShadowBlur, ShadowOpacity, ShadowOffset, Aspect);

    // ───────── Live preview ─────────

    /// <summary>Redraws the preview shortly: each call cancels the one before, so dragging a slider only draws where it rests.</summary>
    private void RequestPreview()
    {
        if (_disposed || _photo is null)
            return;
        _previewing?.Cancel();
        var cancel = (_previewing = new CancellationTokenSource()).Token;
        _ = ShowPreviewAsync(++_requested, Snapshot(), cancel);
    }

    private async Task ShowPreviewAsync(int generation, FrameOptions options, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(60, cancel);
            var bitmap = await Task.Run(() => DrawPreview(options, cancel), cancel);
            if (cancel.IsCancellationRequested || _disposed)
            {
                bitmap.Dispose();
                return;
            }
            var old = Preview;
            Preview = bitmap;
            PreviewError = null;
            _settled = generation;
            if (old is not null)
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget: say what went wrong instead of leaving a stale preview with no explanation.
            PreviewError = Context.Explain(ex);
            _settled = generation;
        }
    }

    private Bitmap DrawPreview(FrameOptions options, CancellationToken cancel)
    {
        // The gate keeps Dispose from freeing the copies under a render; renders queue behind each other (the cancelled one stops fast).
        lock (_gate)
        {
            if (_photo is null)
                throw new ObjectDisposedException(nameof(FrameImagePanel));
            using var photo = new MagickImage(_photo);
            var side = Math.Min(PreviewSide, ImageCompose.FinalSide(_photoWidth, _photoHeight, options.Padding, options.Ratio));
            using var canvas = Render(photo, _photoWidth, _photoHeight, options, _picture, side, cancel);
            return PanelImages.ToBitmap(canvas);
        }
    }

    // ───────── The picture ─────────

    /// <summary>
    /// Draws the framed canvas whose longer side is <paramref name="longSide"/>: the background, the photo's shadow, the
    /// photo with its corners. <paramref name="photo"/> is consumed (resized and rounded in place) and may be a smaller copy
    /// of the original, whose real size is <paramref name="originalWidth"/> × <paramref name="originalHeight"/>: the layout
    /// follows the original's shape.
    /// </summary>
    internal static MagickImage Render(MagickImage photo, uint originalWidth, uint originalHeight, FrameOptions options,
        MagickImage? picture, int longSide, CancellationToken cancel = default)
    {
        var layout = ImageCompose.FrameFor(originalWidth, originalHeight, options.Padding, options.Ratio, longSide);
        ImageCompose.Resize(photo, layout.PhotoWidth, layout.PhotoHeight, ComposeFit.Contain);
        ImageCompose.ToSrgb(photo);
        var radius = options.Corners / 100 * Math.Min(photo.Width, photo.Height);
        ImageCompose.RoundCorners(photo, radius);
        cancel.ThrowIfCancellationRequested();

        var canvas = NewBackground(options, picture, layout.CanvasWidth, layout.CanvasHeight);
        try
        {
            cancel.ThrowIfCancellationRequested();
            int x = (int)((layout.CanvasWidth - photo.Width) / 2), y = (int)((layout.CanvasHeight - photo.Height) / 2);
            if (options.Shadow && options.ShadowOpacity > 0)
            {
                var side = Math.Max(layout.CanvasWidth, layout.CanvasHeight);
                using var shadow = ImageCompose.ShadowLayer(layout.CanvasWidth, layout.CanvasHeight, photo, x, y + options.ShadowOffset * DropPerStep * side,
                    options.ShadowBlur * BlurPerStep * side, options.ShadowOpacity / 100);
                canvas.Composite(shadow, 0, 0, CompositeOperator.Over);
            }
            cancel.ThrowIfCancellationRequested();
            canvas.Composite(photo, x, y, CompositeOperator.Over);
            canvas.Alpha(AlphaOption.Off);
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    private static MagickImage NewBackground(FrameOptions options, MagickImage? picture, uint width, uint height)
    {
        var first = ImageCompose.ParseColor(options.Primary);
        return options.Background switch
        {
            FrameBackground.Gradient => ImageCompose.Gradient(width, height, first, ImageCompose.ParseColor(options.Secondary), options.Angle),
            FrameBackground.Image when picture is not null => ImageCompose.Cover(picture, width, height),
            _ => ImageCompose.Solid(width, height, first),
        };
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var source = Files[0];
        var options = Snapshot();
        var picture = options.Background == FrameBackground.Image ? PicturePath : null;
        // The canvas is opaque, so the original's own format does (HEIC aside: PanelImages saves it as JPG).
        var output = Context.OutputFor(source, "frameImage", PanelImages.OutputExtension(source));
        try
        {
            await Task.Run(() =>
            {
                progress.Report(0.05);
                using var photo = PanelImages.Read(source);
                var (width, height) = (photo.Width, photo.Height);
                using var background = picture is null ? null : PanelImages.Read(picture);
                cancel.ThrowIfCancellationRequested();
                progress.Report(0.3);
                using var canvas = Render(photo, width, height, options, background, ImageCompose.FinalSide(width, height, options.Padding, options.Ratio), cancel);
                progress.Report(0.8);
                PanelImages.Save(canvas, output);
            }, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        progress.Report(1);
        return [output];
    }

    public override void Dispose()
    {
        _disposed = true;
        _loading.Cancel();
        _previewing?.Cancel();
        // The copies go once a render in flight lets go of them.
        Task.Run(() =>
        {
            lock (_gate)
            {
                _photo?.Dispose();
                _picture?.Dispose();
                _photo = _picture = null;
            }
        });
        Preview?.Dispose();
        base.Dispose();
    }
}
