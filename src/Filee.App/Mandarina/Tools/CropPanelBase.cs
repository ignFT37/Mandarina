// Mandarina: what the two CROP panels (photo and video) have in common: the size of the picture, the crop box
// (in pixels of the original), the proportion pills, the exact-size boxes and the text under them. CropImagePanel and
// CropVideoPanel add how the picture is loaded and what Apply writes; CropOptionsView is the shared half of their views.
//
// Two ways to work: with ONE picture the box is dragged on the preview (or typed); with SEVERAL photos nothing is
// dragged: each photo is cut around its centre, either the largest box of the chosen proportion or the typed size cut to
// that photo, and the preview shows the box that would result on the first one.

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

public abstract partial class CropPanelBase : ToolPanel
{
    /// <summary>The most a typed width or height can be (keeps absurd numbers out of the arithmetic).</summary>
    private const int MostPixels = 100_000;

    private readonly CancellationTokenSource _loading = new();
    private readonly CropRatio _custom = new("custom", "", 0, isVisible: false);
    private double _aspectRatio;

    /// <summary>With several photos: the size the user typed, before it is cut to each photo (null while a proportion rules).</summary>
    private Size? _typedSize;

    protected CropPanelBase(PanelContext context) : base(context)
    {
        Ratios =
        [
            new CropRatio("free", context["panel.crop.free"], 0) { IsSelected = true },
            new CropRatio("original", context["panel.crop.original"], 1),
            new CropRatio("1:1", "1:1", 1),
            new CropRatio("4:3", "4:3", 4.0 / 3),
            new CropRatio("3:2", "3:2", 3.0 / 2),
            new CropRatio("16:9", "16:9", 16.0 / 9),
            new CropRatio("4:5", "4:5", 4.0 / 5),
            new CropRatio("9:16", "9:16", 9.0 / 16),
            _custom,
        ];
    }

    /// <summary>The proportion pills, in order; the last one shows a turned proportion that has no pill of its own.</summary>
    public IReadOnlyList<CropRatio> Ratios { get; }

    /// <summary>The picture to show: a downsized photo or a video frame (not the original's pixels).</summary>
    [ObservableProperty] private IImage? _preview;

    /// <summary>Width of the ORIGINAL picture (the unit of <see cref="Selection"/>).</summary>
    [ObservableProperty] private int _pixelWidth;

    [ObservableProperty] private int _pixelHeight;

    /// <summary>The crop box in pixels of the original. With several photos, the box on the first one.</summary>
    [ObservableProperty] private Rect _selection;

    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private string? _loadError;

    /// <summary>Width / height the box keeps; 0 when it is free.</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        private set => SetProperty(ref _aspectRatio, value);
    }

    /// <summary>True with several photos: the box is centred on each one instead of dragged.</summary>
    public virtual bool IsMulti => false;

    /// <summary>The picture is loaded and its size known.</summary>
    public bool IsReady => !IsLoading && LoadError is null && PixelWidth > 0 && PixelHeight > 0;

    /// <summary>The box may be dragged on the preview.</summary>
    public bool CanDragBox => IsReady && !IsMulti;

    /// <summary>The line under the preview (nothing to say while there is no picture).</summary>
    public string? HintText => LoadError is null ? Hint : null;

    /// <summary>What the tool tells about its preview.</summary>
    protected virtual string? Hint => null;

    /// <summary>A note under the size boxes, if the tool has one.</summary>
    public virtual string? NoteText => null;

    public string OriginalSizeText => PixelWidth > 0 ? Pixels(PixelWidth, PixelHeight) : "";

    public string WidthText => IsReady ? Whole(DisplayedSize.Width) : "";

    public string HeightText => IsReady ? Whole(DisplayedSize.Height) : "";

    /// <summary>"1200 × 800 px": what Apply will write (for several photos, for the first one).</summary>
    public string ResultText => IsReady ? Pixels(OutputBox.Width, OutputBox.Height) : "";

    /// <summary>The pill that is on.</summary>
    public CropRatio? SelectedRatio => Ratios.FirstOrDefault(ratio => ratio.IsSelected);

    public override bool CanApply => IsReady && Selection.Width >= 1 && Selection.Height >= 1;

    /// <summary>The box was moved or resized off the whole picture (with several photos nothing is dragged: only pills were chosen).</summary>
    public override bool HasUnsavedWork => IsReady && !IsMulti && Selection != new Rect(0, 0, PixelWidth, PixelHeight);

    /// <summary>The box Apply cuts for the (first) picture: whole pixels, inside it. Video narrows it to even numbers.</summary>
    public Rect OutputBox => OutputBoxCore();

    protected virtual Rect OutputBoxCore() => CropMath.Snap(Selection, PixelWidth, PixelHeight);

    /// <summary>With several photos: the proportion and typed size that rule each photo's centred box.</summary>
    protected (double Aspect, Size? TypedSize) CentredPlan => (AspectRatio, _typedSize);

    /// <summary>Cancelled when the window closes.</summary>
    protected CancellationToken LoadToken => _loading.Token;

    private Size DisplayedSize => IsMulti && _typedSize is { } typed ? typed : new Size(Selection.Width, Selection.Height);

    private double OriginalRatio => PixelHeight > 0 ? PixelWidth / (double)PixelHeight : 1;

    /// <summary>The loaded picture's size: the box starts as the whole picture, free.</summary>
    protected void SetPicture(int width, int height)
    {
        (PixelWidth, PixelHeight) = (width, height);
        _typedSize = null;
        Ratios[1].Value = OriginalRatio;
        Mark(Ratios[0]);
        AspectRatio = 0;
        Selection = new Rect(0, 0, width, height);
        RefreshTexts();
    }

    partial void OnSelectionChanged(Rect value) => RefreshTexts();

    partial void OnPixelWidthChanged(int value) => RefreshState();

    partial void OnPixelHeightChanged(int value) => RefreshState();

    partial void OnIsLoadingChanged(bool value) => RefreshState();

    partial void OnLoadErrorChanged(string? value) => RefreshState();

    private void RefreshState()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanDragBox));
        OnPropertyChanged(nameof(HintText));
        RefreshTexts();
    }

    /// <summary>Raises the texts that depend on the box (the pixel boxes, the result line) and whether Apply is possible.</summary>
    protected void RefreshTexts()
    {
        OnPropertyChanged(nameof(OriginalSizeText));
        OnPropertyChanged(nameof(WidthText));
        OnPropertyChanged(nameof(HeightText));
        OnPropertyChanged(nameof(ResultText));
        NotifyCanApplyChanged();
    }

    // ───────── Proportion ─────────

    /// <summary>The pill called <paramref name="key"/> ("free", "original", "1:1", "4:3", "3:2", "16:9", "4:5", "9:16").</summary>
    public void SelectRatio(string key)
    {
        if (Ratios.FirstOrDefault(ratio => ratio.Key == key) is { } found)
            SelectRatio(found);
    }

    /// <summary>
    /// Chooses a proportion: the box becomes the largest one with it around the same centre (the whole picture for
    /// "original"); "free" keeps the box and lets it be dragged any way.
    /// </summary>
    public void SelectRatio(CropRatio ratio)
    {
        if (!IsReady || ratio.IsCustom || !Ratios.Contains(ratio))
            return;
        _typedSize = null;
        Mark(ratio);
        UseAspect(ratio.IsFree ? 0 : ratio.Value);
    }

    /// <summary>Swaps the width and the height: a 4:3 box becomes 3:4, a free box turns on its side.</summary>
    public void RotateRatio()
    {
        if (!IsReady)
            return;
        var aspect = AspectRatio > 0 ? 1 / AspectRatio : 0;
        if (IsMulti)
        {
            // A typed size turns with it; so does a free box, which is the size of the first photo's box.
            if (aspect <= 0 || _typedSize is not null)
            {
                var size = _typedSize ?? new Size(Selection.Width, Selection.Height);
                _typedSize = new Size(size.Height, size.Width);
            }
            if (aspect > 0)
                MarkTurned(aspect);
            Selection = CropMath.CenteredCrop(PixelWidth, PixelHeight, aspect, _typedSize);
        }
        else
        {
            if (aspect > 0)
                MarkTurned(aspect);
            Selection = CropMath.Swapped(Selection, aspect, PixelWidth, PixelHeight);
        }
        AspectRatio = aspect;
        RefreshTexts();
    }

    private void UseAspect(double aspect)
    {
        // The box first, then the proportion: the surface sees a box that already has it and leaves it alone.
        if (IsMulti)
            Selection = CropMath.CenteredCrop(PixelWidth, PixelHeight, aspect, _typedSize);
        else if (aspect > 0)
            Selection = CropMath.FitAspect(Selection, aspect, PixelWidth, PixelHeight);
        AspectRatio = aspect;
        RefreshTexts();
    }

    /// <summary>Turns <paramref name="selected"/> on and the others off; the custom pill shows only while it is the one on.</summary>
    private void Mark(CropRatio selected)
    {
        foreach (var ratio in Ratios)
            ratio.IsSelected = ratio == selected;
        _custom.IsVisible = selected == _custom;
    }

    /// <summary>After turning: the listed pill with the new proportion (16:9 → 9:16), or the custom pill saying it ("3:4").</summary>
    private void MarkTurned(double aspect)
    {
        var match = Ratios.FirstOrDefault(ratio => !ratio.IsFree && !ratio.IsOriginal && !ratio.IsCustom && Same(ratio.Value, aspect))
            ?? Ratios.FirstOrDefault(ratio => ratio.IsOriginal && Same(ratio.Value, aspect));
        if (match is null)
        {
            _custom.Value = aspect;
            _custom.Label = RatioText(aspect);
            match = _custom;
        }
        Mark(match);
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 0.002 * Math.Max(a, b);

    /// <summary>"3:4" for 0.75: the simplest whole-number pair (up to 20) within a fifth of a percent, else "0.62".</summary>
    internal static string RatioText(double aspect)
    {
        for (var q = 1; q <= 20; q++)
        {
            var p = (int)Math.Round(aspect * q);
            if (p >= 1 && Math.Abs(p / (double)q - aspect) <= 0.0015 * aspect)
                return $"{p}:{q}";
        }
        return aspect.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ───────── Exact size ─────────

    /// <summary>A width typed in its box (digits only); an unreadable one puts the old number back.</summary>
    public void CommitWidth(string? text) => CommitSize(text, isWidth: true);

    /// <summary>A height typed in its box.</summary>
    public void CommitHeight(string? text) => CommitSize(text, isWidth: false);

    private void CommitSize(string? text, bool isWidth)
    {
        if (!IsReady || !int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            RefreshTexts();
            return;
        }
        value = Math.Min(value, MostPixels);
        if (IsMulti)
        {
            // Kept as typed: it is cut to each photo, and a photo bigger than the first one can take more than it.
            var size = _typedSize ?? new Size(Selection.Width, Selection.Height);
            double w = size.Width, h = size.Height;
            if (AspectRatio > 0)
            {
                if (isWidth)
                    (w, h) = (value, Math.Max(1, Math.Round(value / AspectRatio, MidpointRounding.AwayFromZero)));
                else
                    (w, h) = (Math.Max(1, Math.Round(value * AspectRatio, MidpointRounding.AwayFromZero)), value);
            }
            else if (isWidth)
            {
                w = value;
            }
            else
            {
                h = value;
            }
            _typedSize = new Size(w, h);
            Selection = CropMath.CenteredCrop(PixelWidth, PixelHeight, AspectRatio, _typedSize);
        }
        else
        {
            Selection = CropMath.WithSize(Selection, isWidth ? value : null, isWidth ? null : value, AspectRatio, PixelWidth, PixelHeight);
        }
        RefreshTexts();
    }

    private static string Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Pixels(double width, double height) => $"{Whole(width)} × {Whole(height)} px";

    public override void Dispose()
    {
        _loading.Cancel();
        base.Dispose();
    }
}
