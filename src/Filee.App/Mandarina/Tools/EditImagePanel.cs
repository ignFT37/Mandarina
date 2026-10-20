// Mandarina: EDIT for photos. A big preview on the left that follows the sliders on the right (light, colour,
// detail, effects), "hold to compare" to see the original, and Apply saves "name edited.ext" beside the photo. All the
// picture work is EditPipeline's: the preview runs it on a reduced copy of the photo and Apply on the full one, so what
// you see is what is saved.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("editImage")]
public sealed partial class EditImagePanel : ToolPanel
{
    /// <summary>The longest side of the copy the preview is worked on.</summary>
    private const int PreviewSide = EditPipeline.ReferenceSide;

    /// <summary>Wait for the slider to settle before working: a drag moves it many times a second.</summary>
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(60);

    private static readonly HashSet<string> SettingNames =
    [
        nameof(Exposure), nameof(Brightness), nameof(Contrast), nameof(Highlights), nameof(Shadows), nameof(Whites), nameof(Blacks),
        nameof(Gamma), nameof(Saturation), nameof(Vibrance), nameof(Temperature), nameof(Tint), nameof(Hue), nameof(Sharpness),
        nameof(Clarity), nameof(NoiseReduction), nameof(BlackAndWhite), nameof(Sepia), nameof(Vignette), nameof(Grain), nameof(Auto),
    ];

    private readonly CancellationTokenSource _loading = new();
    private readonly object _gate = new();
    private MagickImage? _working;
    private CancellationTokenSource? _inFlight;
    private int _version;
    private int _bulk;
    private bool _looping;
    private bool _disposed;

    // Light
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _highlights;
    [ObservableProperty] private double _shadows;
    [ObservableProperty] private double _whites;
    [ObservableProperty] private double _blacks;
    [ObservableProperty] private double _gamma = 1;

    // Colour
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _vibrance;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _hue;

    // Detail
    [ObservableProperty] private double _sharpness;
    [ObservableProperty] private double _clarity;
    [ObservableProperty] private double _noiseReduction;

    // Effects
    [ObservableProperty] private bool _blackAndWhite;
    [ObservableProperty] private double _sepia;
    [ObservableProperty] private double _vignette;
    [ObservableProperty] private double _grain;

    [ObservableProperty] private bool _auto;

    // What the window shows
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Shown))] private Bitmap? _original;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Shown))] private Bitmap? _edited;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Shown))] private bool _showOriginal;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private string? _previewError;

    /// <summary>The group of controls on view: 0 light, 1 colour, 2 detail, 3 effects.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightTab))]
    [NotifyPropertyChangedFor(nameof(IsColorTab))]
    [NotifyPropertyChangedFor(nameof(IsDetailTab))]
    [NotifyPropertyChangedFor(nameof(IsEffectsTab))]
    private int _tab;

    public EditImagePanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(1020, 640);

    public string FileName => Path.GetFileName(Files[0]);

    /// <summary>The photo as edited so far, or the original while "compare" is held (or nothing has been changed).</summary>
    public Bitmap? Shown => ShowOriginal ? Original : Edited ?? Original;

    // The tab buttons are two-way: pressing the one that is already on must not turn it off.
    public bool IsLightTab
    {
        get => Tab == 0;
        set => SelectTab(0, value);
    }

    public bool IsColorTab
    {
        get => Tab == 1;
        set => SelectTab(1, value);
    }

    public bool IsDetailTab
    {
        get => Tab == 2;
        set => SelectTab(2, value);
    }

    public bool IsEffectsTab
    {
        get => Tab == 3;
        set => SelectTab(3, value);
    }

    /// <summary>
    /// The controls as the picture work gets them. Each is rounded to what its slider shows (a drag never lands exactly on
    /// zero), so "nothing changed" means what it says.
    /// </summary>
    public EditSettings Settings => new()
    {
        Exposure = Math.Round(Exposure, 2),
        Brightness = Math.Round(Brightness),
        Contrast = Math.Round(Contrast),
        Highlights = Math.Round(Highlights),
        Shadows = Math.Round(Shadows),
        Whites = Math.Round(Whites),
        Blacks = Math.Round(Blacks),
        Gamma = Math.Round(Gamma, 2),
        Saturation = Math.Round(Saturation),
        Vibrance = Math.Round(Vibrance),
        Temperature = Math.Round(Temperature),
        Tint = Math.Round(Tint),
        Hue = Math.Round(Hue),
        Sharpness = Math.Round(Sharpness),
        Clarity = Math.Round(Clarity),
        NoiseReduction = Math.Round(NoiseReduction),
        BlackAndWhite = BlackAndWhite,
        Sepia = Math.Round(Sepia),
        Vignette = Math.Round(Vignette),
        Grain = Math.Round(Grain),
        Auto = Auto,
    };

    /// <summary>True when something differs from the untouched photo.</summary>
    public bool IsEdited => !Settings.IsDefault;

    /// <summary>The controls the preview on view was made with (null while the photo is shown untouched).</summary>
    internal EditSettings? PreviewSettings { get; private set; }

    public override bool CanApply => !IsLoading && LoadError is null && IsEdited;

    public override bool HasUnsavedWork => IsEdited;

    public override Control CreateView() => new EditImageView { DataContext = this };

    private void SelectTab(int tab, bool on)
    {
        if (on)
            Tab = tab;
        else
            OnPropertyChanged(tab switch { 0 => nameof(IsLightTab), 1 => nameof(IsColorTab), 2 => nameof(IsDetailTab), _ => nameof(IsEffectsTab) });
    }

    // ───────── Loading ─────────

    private async Task LoadAsync()
    {
        try
        {
            var source = Files[0];
            var loaded = await Task.Run(() =>
            {
                _loading.Token.ThrowIfCancellationRequested();
                var image = PanelImages.Read(source);
                try
                {
                    PanelImages.Fit(image, PreviewSide);
                    EditPipeline.EnsureRgb(image);
                    return (Image: image, Bitmap: ToBitmap(image));
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            }, _loading.Token);

            if (_disposed)
            {
                loaded.Image.Dispose();
                loaded.Bitmap.Dispose();
                return;
            }
            lock (_gate)
                _working = loaded.Image;
            Original = loaded.Bitmap;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "loading" for ever.
            // Magick's own message is a file name and a source line; the user needs to know the photo is the problem.
            LoadError = Context["panel.editImage.cannot_open"];
        }
        IsLoading = false;
        // Sliders may have been moved while the photo was loading.
        RequestPreview();
    }

    partial void OnIsLoadingChanged(bool value) => NotifyCanApplyChanged();

    partial void OnLoadErrorChanged(string? value) => NotifyCanApplyChanged();

    // ───────── The controls ─────────

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_bulk == 0 && e.PropertyName is { } name && SettingNames.Contains(name))
            SettingsChanged();
    }

    private void SettingsChanged()
    {
        OnPropertyChanged(nameof(IsEdited));
        NotifyCanApplyChanged();
        RequestPreview();
    }

    /// <summary>Puts every control at the values of <paramref name="settings"/> (one preview for all of them).</summary>
    public void SetSettings(EditSettings settings)
    {
        _bulk++;
        try
        {
            Exposure = settings.Exposure;
            Brightness = settings.Brightness;
            Contrast = settings.Contrast;
            Highlights = settings.Highlights;
            Shadows = settings.Shadows;
            Whites = settings.Whites;
            Blacks = settings.Blacks;
            Gamma = settings.Gamma;
            Saturation = settings.Saturation;
            Vibrance = settings.Vibrance;
            Temperature = settings.Temperature;
            Tint = settings.Tint;
            Hue = settings.Hue;
            Sharpness = settings.Sharpness;
            Clarity = settings.Clarity;
            NoiseReduction = settings.NoiseReduction;
            BlackAndWhite = settings.BlackAndWhite;
            Sepia = settings.Sepia;
            Vignette = settings.Vignette;
            Grain = settings.Grain;
            Auto = settings.Auto;
        }
        finally
        {
            _bulk--;
        }
        SettingsChanged();
    }

    /// <summary>Every control back to its factory value.</summary>
    public void ResetAll() => SetSettings(EditSettings.Default);

    /// <summary>"Hold to compare": the original is shown while <paramref name="held"/>.</summary>
    public void HoldCompare(bool held) => ShowOriginal = held;

    // ───────── The preview ─────────

    // One loop works the preview at a time. A change while it is busy does not cancel the work in hand: the loop goes round
    // again with the latest controls as soon as it is free, so the picture keeps up with a drag (cancelling on every move
    // would show nothing until the hand stops). Only a reset cancels it, and so does closing the panel.

    /// <summary>Asks for the preview to follow the controls.</summary>
    private void RequestPreview()
    {
        if (_disposed || IsLoading || LoadError is not null)
            return;
        _version++;
        if (Settings.IsDefault)
        {
            // Nothing to show but the photo itself.
            _inFlight?.Cancel();
            PreviewError = null;
            PreviewSettings = null;
            SetEdited(null);
            IsRendering = false;
            return;
        }
        IsRendering = true;
        if (!_looping)
        {
            _looping = true;
            _ = RenderLoopAsync();
        }
    }

    private async Task RenderLoopAsync()
    {
        try
        {
            // The first change after a pause waits a moment, so a burst of changes (a typed value, a reset) is worked once.
            await Task.Delay(PreviewDelay, _loading.Token);
            while (!_disposed)
            {
                var version = _version;
                var settings = Settings;
                if (settings.IsDefault)
                    break;
                var work = _inFlight = CancellationTokenSource.CreateLinkedTokenSource(_loading.Token);
                try
                {
                    var bitmap = await Task.Run(() => RenderPreview(settings, work.Token), work.Token);
                    if (_disposed || Settings.IsDefault)
                    {
                        bitmap.Dispose();
                        break;
                    }
                    PreviewError = null;
                    PreviewSettings = settings;
                    SetEdited(bitmap);
                }
                catch (OperationCanceledException) when (!_disposed)
                {
                    // A reset cancelled it: the next round finds the controls at their factory values.
                }
                finally
                {
                    if (ReferenceEquals(_inFlight, work))
                        _inFlight = null;
                    work.Dispose();
                }
                if (version == _version)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget: show what went wrong instead of a preview that has stopped following the sliders.
            PreviewError = Context.Explain(ex);
        }
        finally
        {
            _looping = false;
            if (!_disposed)
                IsRendering = false;
        }
    }

    private Bitmap RenderPreview(EditSettings settings, CancellationToken cancel)
    {
        MagickImage copy;
        lock (_gate)
        {
            if (_working is null)
                throw new OperationCanceledException(cancel);
            copy = (MagickImage)_working.Clone();
        }
        using (copy)
        {
            EditPipeline.Apply(copy, settings, cancel);
            cancel.ThrowIfCancellationRequested();
            return ToBitmap(copy);
        }
    }

    /// <summary>
    /// The picture as an Avalonia bitmap, its pixels copied straight over. (PanelImages.ToBitmap encodes a PNG when the
    /// picture has transparency, which takes about a second at 1200 px: too slow to follow a slider.)
    /// </summary>
    private static Bitmap ToBitmap(MagickImage image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        byte[]? bgra;
        using (var pixels = image.GetPixels())
            bgra = pixels.ToByteArray(PixelMapping.BGRA);
        if (bgra is null)
            throw new InvalidOperationException("The picture has no pixels.");
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var frame = bitmap.Lock();
        for (var y = 0; y < height; y++)
            Marshal.Copy(bgra, y * width * 4, frame.Address + y * frame.RowBytes, width * 4);
        return bitmap;
    }

    private void SetEdited(Bitmap? bitmap)
    {
        var old = Edited;
        Edited = bitmap;
        old?.Dispose();
    }

    // ───────── Apply ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var source = Files[0];
        var settings = Settings;
        var output = Context.OutputFor(source, "editImage", PanelImages.OutputExtension(source));
        progress.Report(0);
        try
        {
            await Task.Run(() =>
            {
                // The whole photo, not the preview's copy.
                using var image = PanelImages.Read(source);
                cancel.ThrowIfCancellationRequested();
                progress.Report(0.3);
                EditPipeline.Apply(image, settings, cancel);
                progress.Report(0.9);
                PanelImages.Save(image, output);
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
        _inFlight?.Cancel();
        MagickImage? working;
        lock (_gate)
        {
            working = _working;
            _working = null;
        }
        working?.Dispose();
        Original?.Dispose();
        Edited?.Dispose();
        base.Dispose();
    }
}
