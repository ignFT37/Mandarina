// Mandarina: COMPRESS for photos, audio, video and PDF. Pick how much to compress (a preset, a percentage or a
// size to land on), optionally make photos and videos smaller too, and Apply saves "name compressed.ext" beside every
// original, all or none. The panel owns the options and the files; each kind of file has a compressor that turns one source
// into one output (ImageCompressor, AudioCompressor, VideoCompressor, PdfCompressor; the contract is in CompressOptions.cs).

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>One named level of the Preset mode (Light, Balanced, Strong, Maximum) and what to expect from it.</summary>
public sealed partial class CompressPanelPreset(string key, string label, int quality, string hint) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    /// <summary>The quality the compressors get (85, 70, 50 or 35).</summary>
    public int Quality { get; } = quality;

    public string Hint { get; } = hint;

    [ObservableProperty] private bool _isSelected;
}

[ToolPanel("compress")]
public sealed partial class CompressPanel : ToolPanel
{
    /// <summary>What Percent starts at, and the quality a size search hands the compressors (they ignore it then).</summary>
    private const int DefaultQuality = 70;

    private static readonly int[] PhotoSides = [0, 4096, 2048, 1600, 1280, 1024, 800];

    private static readonly int[] VideoHeights = [0, 1080, 720, 480];

    private enum Kind
    {
        Photo,
        Audio,
        Video,
        Pdf,
    }

    private readonly CancellationTokenSource _loading = new();
    private readonly Kind _kind;
    private readonly MediaTools? _media;
    private readonly int[] _resizeValues;
    private long[] _sizes = [];

    [ObservableProperty] private CompressMode _mode = CompressMode.Preset;
    [ObservableProperty] private double _percent = DefaultQuality;
    [ObservableProperty] private string _targetText = "";

    /// <summary>0 = KB, 1 = MB.</summary>
    [ObservableProperty] private int _targetUnit;

    [ObservableProperty] private int _resizeIndex;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;

    /// <summary>"2.2 MB → 640 KB (−71 %)" (totals with several files) or "Already optimized"; null until Apply has run.</summary>
    [ObservableProperty] private string? _resultText;

    public CompressPanel(PanelContext context) : base(context)
    {
        _kind = context.Files.Count == 0 ? Kind.Photo
            : CompressKinds.IsAudio(context.Files[0]) ? Kind.Audio
            : CompressKinds.IsVideo(context.Files[0]) ? Kind.Video
            : CompressKinds.IsPdf(context.Files[0]) ? Kind.Pdf
            : Kind.Photo;
        if (_kind is Kind.Audio or Kind.Video)
            _media = context.Media;

        Presets =
        [
            new CompressPanelPreset("light", context["panel.compress.preset_light"], 85, context["panel.compress.hint_light"]),
            new CompressPanelPreset("balanced", context["panel.compress.preset_balanced"], 70, context["panel.compress.hint_balanced"]) { IsSelected = true },
            new CompressPanelPreset("strong", context["panel.compress.preset_strong"], 50, context["panel.compress.hint_strong"]),
            new CompressPanelPreset("maximum", context["panel.compress.preset_max"], 35, context["panel.compress.hint_max"]),
        ];

        _resizeValues = _kind switch
        {
            Kind.Photo => PhotoSides,
            Kind.Video => VideoHeights,
            _ => [0],
        };
        ResizeChoices = _resizeValues
            .Select(value => value == 0 ? context["panel.compress.original"] : _kind == Kind.Video ? $"{value} p" : $"{value} px")
            .ToList();

        _ = LoadAsync();
    }

    /// <summary>Photos and videos have the resize card too, so their window is taller than a PDF's or an audio file's.</summary>
    public override Size PreferredSize => new(560, CanResize ? 470 : IsLosslessAudio ? 330 : ShowLosslessSome ? 420 : 400);

    // ───────── What the view shows ─────────

    public IReadOnlyList<CompressPanelPreset> Presets { get; }

    public IReadOnlyList<string> ResizeChoices { get; }

    public bool IsPresetMode => Mode == CompressMode.Preset;

    public bool IsPercentMode => Mode == CompressMode.Percent;

    public bool IsTargetMode => Mode == CompressMode.TargetSize;

    /// <summary>Photos and videos can be made smaller as well; audio and PDF have no size to change.</summary>
    public bool CanResize => _kind is Kind.Photo or Kind.Video;

    public string ResizeLabel => Context[_kind == Kind.Video ? "panel.compress.resize_video" : "panel.compress.resize_photo"];

    /// <summary>WAV, AIFF and FLAC only: they are saved as FLAC, so there is nothing to choose.</summary>
    public bool IsLosslessAudio => _kind == Kind.Audio && Files.All(IsLossless);

    public bool ShowModes => !IsLosslessAudio;

    /// <summary>Lossless and lossy audio together: the modes apply to the lossy ones only.</summary>
    public bool ShowLosslessSome => _kind == Kind.Audio && !IsLosslessAudio && Files.Any(IsLossless);

    public string TargetLabel => Context[Files.Count > 1 ? "panel.compress.target_each" : "panel.compress.target"];

    public string PresetHint => SelectedPreset.Hint;

    /// <summary>"foto.jpg · 2.2 MB" or "3 files · 14.2 MB".</summary>
    public string SummaryText
    {
        get
        {
            var name = Files.Count == 1 ? Path.GetFileName(Files[0]) : Context.Format("panel.compress.files_many", Files.Count);
            return _sizes.Length == 0 ? name : $"{name} · {PanelFormat.Size(_sizes.Sum())}";
        }
    }

    /// <summary>Audio and video need FFmpeg: the reason Apply is off when it is missing.</summary>
    public string? WarningText => HasWarning ? Context.Format("engine.reason.needs_package", "FFmpeg") : null;

    public bool HasWarning => _kind is Kind.Audio or Kind.Video && _media is null;

    public bool HasResult => ResultText is not null;

    public bool HasLoadError => LoadError is not null;

    private CompressPanelPreset SelectedPreset => Presets.First(preset => preset.IsSelected);

    /// <summary>The wanted size in bytes (KB and MB are thousands, as <see cref="PanelFormat.Size"/> writes them); 0 when the box holds no usable number.</summary>
    public long TargetBytes
    {
        get
        {
            var text = TargetText.Trim().Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                return 0;
            return (long)Math.Round(Math.Min(value * (TargetUnit == 1 ? 1_000_000d : 1000d), 1e12));
        }
    }

    public override bool CanApply =>
        !IsLoading && LoadError is null && !HasWarning && (Mode != CompressMode.TargetSize || IsLosslessAudio || TargetBytes > 0);

    public override Control CreateView() => new CompressView { DataContext = this };

    // ───────── Choosing ─────────

    public void SelectMode(CompressMode mode) => Mode = mode;

    public void SelectPreset(CompressPanelPreset preset)
    {
        if (!Presets.Contains(preset))
            return;
        foreach (var other in Presets)
            other.IsSelected = other == preset;
        OnPropertyChanged(nameof(PresetHint));
    }

    partial void OnModeChanged(CompressMode value)
    {
        OnPropertyChanged(nameof(IsPresetMode));
        OnPropertyChanged(nameof(IsPercentMode));
        OnPropertyChanged(nameof(IsTargetMode));
        NotifyCanApplyChanged();
    }

    partial void OnTargetTextChanged(string value) => NotifyCanApplyChanged();

    partial void OnTargetUnitChanged(int value) => NotifyCanApplyChanged();

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLoadError));
        NotifyCanApplyChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(SummaryText));
        NotifyCanApplyChanged();
    }

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    // ───────── Loading ─────────

    private async Task LoadAsync()
    {
        try
        {
            var files = Files.ToArray();
            _sizes = await Task.Run(() => files.Select(file => new FileInfo(file).Length).ToArray(), _loading.Token);
            SuggestTarget();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would be "loading" for ever.
            LoadError = Context.Explain(ex);
        }
        IsLoading = false;
    }

    /// <summary>The Target size box starts at about half of the file (of the average file when there are several).</summary>
    private void SuggestTarget()
    {
        var half = _sizes.Length == 0 ? 0 : _sizes.Sum() / (double)_sizes.Length / 2;
        if (half >= 1_000_000)
        {
            TargetUnit = 1;
            TargetText = Math.Round(half / 1_000_000, 1).ToString("0.#", CultureInfo.CurrentCulture);
        }
        else
        {
            TargetUnit = 0;
            TargetText = Math.Max(1, TwoDigits(half / 1000)).ToString("0", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>47 160 → 47 000, 640.4 → 640, 83.7 → 84: a number that looks chosen, not measured.</summary>
    private static double TwoDigits(double value)
    {
        if (value < 100)
            return Math.Round(value);
        var step = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        return Math.Round(value / step) * step;
    }

    // ───────── Applying ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        ResultText = null;
        var media = _kind is Kind.Audio or Kind.Video ? Context.RequireMedia() : null;
        var resize = _resizeValues[Math.Clamp(ResizeIndex, 0, _resizeValues.Length - 1)];
        var options = OptionsFor(resize);
        var count = Files.Count;
        var written = new List<string>();
        string? current = null;
        long before = 0, after = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var index = i;
                var source = Files[i];
                before += new FileInfo(source).Length;
                // Chosen one by one: two sources that would share a name (a.heic and a.jpg → "a compressed.jpg") get " 2".
                var output = current = Context.OutputFor(source, "compress", OutputExtension(source));
                var step = new Progress<double>(fraction => progress.Report((index + fraction) / count));
                string path;
                try
                {
                    path = await CompressOneAsync(media, source, output, options, step, cancel);
                }
                catch (PanelException ex) when (ex.Subject is null)
                {
                    // A batch says which file it was: the generic causes ("damaged", "no space") name nothing themselves.
                    ex.Subject = Path.GetFileName(source);
                    throw;
                }
                written.Add(path);
                current = null;
                after += new FileInfo(path).Length;
                progress.Report((i + 1.0) / count);
            }
        }
        catch
        {
            // All or nothing (the compressors remove their own half-written file; this is the belt to their braces).
            foreach (var path in written)
                MediaTools.TryDelete(path);
            if (current is not null)
                MediaTools.TryDelete(current);
            throw;
        }
        ResultText = Describe(before, after);
        return written;
    }

    private CompressOptions OptionsFor(int resize) => Mode switch
    {
        CompressMode.Percent => new CompressOptions(CompressMode.Percent, (int)Math.Round(Percent), 0, resize),
        CompressMode.TargetSize => new CompressOptions(CompressMode.TargetSize, DefaultQuality, TargetBytes, resize),
        _ => new CompressOptions(CompressMode.Preset, SelectedPreset.Quality, 0, resize),
    };

    private string OutputExtension(string source) => _kind switch
    {
        Kind.Photo => ImageCompressor.OutputExtension(source),
        Kind.Audio => AudioCompressor.OutputExtension(source),
        _ => Path.GetExtension(source),
    };

    private Task<string> CompressOneAsync(MediaTools? media, string source, string output, CompressOptions options, IProgress<double> progress,
        CancellationToken cancel) => _kind switch
    {
        Kind.Photo => ImageCompressor.CompressAsync(source, output, options, progress, cancel),
        Kind.Pdf => PdfCompressor.CompressAsync(source, output, options, progress, cancel),
        Kind.Audio => AudioCompressor.CompressAsync(media!, source, output, options, progress, cancel),
        _ => VideoCompressor.CompressAsync(media!, source, output, options, progress, cancel),
    };

    /// <summary>"2.2 MB → 640 KB (−71 %)", or "Already optimized" when nothing was saved.</summary>
    private string Describe(long before, long after)
    {
        if (before <= 0 || after >= before)
            return Context["panel.compress.already"];
        var saved = Math.Max(1, (int)Math.Round((1 - after / (double)before) * 100));
        return Context.Format("panel.compress.result", PanelFormat.Size(before), PanelFormat.Size(after), saved);
    }

    private static bool IsLossless(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".aiff" or ".aif" or ".flac";

    public override void Dispose()
    {
        _loading.Cancel();
        base.Dispose();
    }
}
