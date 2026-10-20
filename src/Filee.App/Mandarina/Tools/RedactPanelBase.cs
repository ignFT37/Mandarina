// Mandarina: what the two REDACT panels (photo and video) have in common: the list of areas to cover, the effect that
// covers them (flat colour, blur or pixelation) and the buttons that find faces or text on this PC. Each panel adds its
// own picture (a photo, a video frame) and its own Apply.

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

public abstract partial class RedactPanelBase : ToolPanel
{
    /// <summary>A rectangle a detector finds is made at least this big, so it can still be picked and dragged.</summary>
    private const double MinimumDetected = RegionEditor.MinimumPixels;

    protected RedactPanelBase(PanelContext context) : base(context)
    {
        Regions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRegions));
            NotifyCanApplyChanged();
        };
    }

    /// <summary>Cancelled when the window closes: loading the picture and detecting stop.</summary>
    protected CancellationTokenSource Loading { get; } = new();

    /// <summary>What to cover, in pixels of the original.</summary>
    public ObservableCollection<RedactRegion> Regions { get; } = [];

    [ObservableProperty] private RedactRegion? _selectedRegion;

    // ───────── The picture ─────────

    /// <summary>What the editor shows (a preview of the photo, a frame of the video).</summary>
    [ObservableProperty] private IImage? _preview;

    /// <summary>Size in pixels of the ORIGINAL photo or video: the unit of every region.</summary>
    [ObservableProperty] private double _sourceWidth;

    [ObservableProperty] private double _sourceHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public string FileName => Path.GetFileName(Files[0]);

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    // ───────── The effect ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFill))]
    [NotifyPropertyChangedFor(nameof(IsBlur))]
    [NotifyPropertyChangedFor(nameof(IsPixelate))]
    [NotifyPropertyChangedFor(nameof(CoverBrush))]
    private RedactEffect _effect = RedactEffect.Fill;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverBrush))]
    private Color _fillColor = RedactEffects.Swatches[0];

    /// <summary>Blur strength, 1..100 (see <see cref="RedactEffects.BlurSigma"/>): even 1 leaves text unreadable.</summary>
    [ObservableProperty] private double _strength = 25;

    /// <summary>Pixelation block, 4..64 (see <see cref="RedactEffects.BlockSize"/>): even 4 leaves text unreadable.</summary>
    [ObservableProperty] private double _blockSize = 8;

    public IReadOnlyList<Color> Swatches => RedactEffects.Swatches;

    public bool IsFill => Effect == RedactEffect.Fill;

    public bool IsBlur => Effect == RedactEffect.Blur;

    public bool IsPixelate => Effect == RedactEffect.Pixelate;

    /// <summary>What the editor draws the rectangles with: the flat colour that will cover them, or null for blur and pixelation.</summary>
    public IBrush? CoverBrush => Effect == RedactEffect.Fill ? new SolidColorBrush(FillColor) : null;

    // ───────── Detecting ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    [NotifyPropertyChangedFor(nameof(HasDetectionNote))]
    private bool _isDetecting;

    /// <summary>What the last detection did: how many areas it added, that it found nothing, or why it could not run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectionNote))]
    private string? _detectionNote;

    public bool HasDetectionNote => !string.IsNullOrEmpty(DetectionNote);

    public bool CanDetect => !IsDetecting && !IsLoading && !HasLoadError;

    public bool HasRegions => Regions.Count > 0;

    public override bool CanApply => Regions.Count > 0;

    public override bool HasUnsavedWork => Regions.Count > 0;

    // ───────── Texts of the shared options (this tool's own keys: panel.<id>.…) ─────────

    public string DetectFacesText => Text("detect_faces");

    public string DetectTextText => Text("detect_text");

    public string ClearText => Text("clear");

    public string DetectingText => Text("detecting");

    public string EffectText => Text("effect");

    public string FillText => Text("fill");

    public string BlurText => Text("blur");

    public string PixelateText => Text("pixelate");

    public string ColorText => Text("color");

    public string StrengthText => Text("strength");

    public string BlockSizeText => Text("block_size");

    protected string Text(string key) => Context[$"panel.{Id}.{key}"];

    // ───────── Working on the regions ─────────

    /// <summary>Removes every area (the ones drawn by hand and the detected ones).</summary>
    public void ClearRegions()
    {
        Regions.Clear();
        SelectedRegion = null;
        DetectionNote = null;
    }

    /// <summary>Finds the faces of the picture shown and adds an area for each one that is not marked yet.</summary>
    public Task DetectFacesAsync() => DetectAsync(RedactRegionSource.Faces);

    /// <summary>Finds the text of the picture shown and adds an area for each phrase that is not marked yet.</summary>
    public Task DetectTextAsync() => DetectAsync(RedactRegionSource.Text);

    /// <summary>
    /// A PNG of what is shown, no larger than 2000 px on its longer side and upright, for the detectors; the caller deletes
    /// it. Also how its pixels relate to the original's, to scale what they find back.
    /// </summary>
    protected abstract Task<DetectionInput> PrepareDetectionAsync(CancellationToken cancel);

    /// <summary>The picture a detector reads, and the factors from its pixels to the original's.</summary>
    protected readonly record struct DetectionInput(string Path, double ScaleX, double ScaleY);

    private async Task DetectAsync(RedactRegionSource kind)
    {
        if (!CanDetect)
            return;
        IsDetecting = true;
        DetectionNote = null;
        string? png = null;
        try
        {
            var input = await PrepareDetectionAsync(Loading.Token);
            png = input.Path;
            var found = kind == RedactRegionSource.Faces
                ? await LocalDetector.FindFacesAsync(png, Loading.Token)
                : await LocalDetector.FindTextAsync(png, Loading.Token);
            DetectionNote = found.Succeeded ? AddFound(found, kind, input) : Context.Format($"panel.{Id}.detect_failed", ReasonText(found.Failure));
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (Exception ex)
        {
            // Called from a button: whatever goes wrong must end up as a note, not as a crash.
            DetectionNote = Context.Format($"panel.{Id}.detect_failed", ex is PanelException ? Context.Describe(ex) : Text("reason_failed"));
        }
        finally
        {
            if (png is not null)
                MediaTools.TryDelete(png);
            IsDetecting = false;
        }
    }

    private string ReasonText(RedactDetectionFailure failure) => failure switch
    {
        RedactDetectionFailure.Unavailable => Text("reason_unavailable"),
        RedactDetectionFailure.NoLanguage => Text("reason_language"),
        RedactDetectionFailure.NoFaceDetector => Text("reason_faces"),
        RedactDetectionFailure.TimedOut => Text("reason_timeout"),
        _ => Text("reason_failed"),
    };

    /// <summary>Adds the rectangles the detector found (scaled to the original) and says what happened.</summary>
    private string AddFound(RedactDetectionResult found, RedactRegionSource kind, DetectionInput input)
    {
        var added = 0;
        foreach (var box in found)
        {
            var rect = Inside(new Rect(box.X * input.ScaleX, box.Y * input.ScaleY, box.Width * input.ScaleX, box.Height * input.ScaleY));
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            // A second detection of the same kind finds the same things: those are not added again.
            if (Regions.Any(region => region.Source == kind && Overlap(region.Rect, rect) >= 0.5))
                continue;
            Regions.Add(new RedactRegion(rect, kind));
            added++;
        }
        if (found.Count == 0)
        {
            // Text in a script Windows has no recognizer for (Korean on a PC set up for Spanish) is simply not seen: say what it reads.
            return kind == RedactRegionSource.Faces || found.Language is null
                ? Text(kind == RedactRegionSource.Faces ? "none_faces" : "none_text")
                : $"{Text("none_text")} {Context.Format("panel.redact.languages_hint", found.Language)}";
        }
        return added == 0 ? Text("already") : Context.Format($"panel.{Id}.found", added);
    }

    /// <summary>The rectangle in whole pixels, at least 8 × 8, kept inside the picture.</summary>
    private Rect Inside(Rect rect)
    {
        double width = SourceWidth, height = SourceHeight;
        if (width <= 0 || height <= 0)
            return default;
        var left = Math.Floor(rect.X);
        var top = Math.Floor(rect.Y);
        var right = Math.Ceiling(rect.Right);
        var bottom = Math.Ceiling(rect.Bottom);
        if (right - left < MinimumDetected)
        {
            var grow = (MinimumDetected - (right - left)) / 2;
            (left, right) = (Math.Floor(left - grow), Math.Ceiling(right + grow));
        }
        if (bottom - top < MinimumDetected)
        {
            var grow = (MinimumDetected - (bottom - top)) / 2;
            (top, bottom) = (Math.Floor(top - grow), Math.Ceiling(bottom + grow));
        }
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(width, right);
        bottom = Math.Min(height, bottom);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : default;
    }

    /// <summary>The share of the smaller rectangle that the other covers (1 = one lies inside the other).</summary>
    internal static double Overlap(Rect a, Rect b)
    {
        var common = a.Intersect(b);
        if (common.Width <= 0 || common.Height <= 0)
            return 0;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 ? common.Width * common.Height / smaller : 0;
    }

    // ───────── Covering ─────────

    /// <summary>The regions as they are now (Apply works on this copy while the user may still draw).</summary>
    protected IReadOnlyList<RedactRegion> SnapshotRegions() => [.. Regions];

    public override void Dispose()
    {
        Loading.Cancel();
        base.Dispose();
    }
}
