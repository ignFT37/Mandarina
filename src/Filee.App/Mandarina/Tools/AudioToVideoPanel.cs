// Mandarina: VISUALIZE: turns a piece of audio into an MP4 (horizontal, vertical or square) with its wave, or bars, drawn
// in the colours you pick over a flat background, so it can be posted where only video goes. The preview is real: FFmpeg draws one
// frame from the middle of the audio through the same graph Apply uses (VisualGraph). Apply writes "name visualized.mp4" beside
// the original; the audio is the file's, encoded as AAC.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>Everything the picture depends on, copied off the panel so a preview can be drawn while the options change.</summary>
internal sealed record VisualOptions(VisualFormat Format, VisualStyle Style, string WaveColor, string BackgroundColor);

[ToolPanel("audioToVideo", NeedsFfmpeg = true)]
public sealed partial class AudioToVideoPanel : ToolPanel
{
    /// <summary>
    /// Waiting a moment after a change keeps a slider-like run of clicks (swatch after swatch, a colour typed digit by digit) from
    /// starting one FFmpeg run each.
    /// </summary>
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The preview picture is half the size of the video: the box it sits in is 440 px wide at most, so it stays sharp on a screen of 2×.</summary>
    private const int PreviewShrink = 2;

    /// <summary>The preview is shown inside this box (the panel's pixels), whatever the shape of the video.</summary>
    private const double PreviewBoxWidth = 440, PreviewBoxHeight = 300;

    private readonly CancellationTokenSource _loading = new();
    private CancellationTokenSource? _previewing;
    private volatile bool _disposed;

    /// <summary>The shape the picture in <see cref="Preview"/> was drawn for.</summary>
    private VisualFormat _previewFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPreview))]
    [NotifyPropertyChangedFor(nameof(ShowsHint))]
    private VisualFormat _format = VisualFormat.Horizontal;
    [ObservableProperty] private VisualStyle _style = VisualStyle.Wave;

    /// <summary>The colour of the wave or the bars, "#RRGGBB".</summary>
    [ObservableProperty] private string _waveColor = VisualColors.DefaultWave;

    /// <summary>The colour of the background, "#RRGGBB".</summary>
    [ObservableProperty] private string _backgroundColor = VisualColors.DefaultBackground;

    [ObservableProperty] private double _duration;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;

    /// <summary>A frame of the video as the options draw it; after a change it stays until the new one is ready (see <see cref="ShowsPreview"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPreview))]
    [NotifyPropertyChangedFor(nameof(ShowsHint))]
    private Bitmap? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewError))]
    [NotifyPropertyChangedFor(nameof(ShowsHint))]
    private string? _previewError;

    public AudioToVideoPanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(900, 548);

    public string FileName => Path.GetFileName(Files[0]);

    public string DurationText => PanelFormat.Time(Duration);

    public bool IsHorizontal => Format == VisualFormat.Horizontal;

    public bool IsVertical => Format == VisualFormat.Vertical;

    public bool IsSquare => Format == VisualFormat.Square;

    public bool IsWave => Style == VisualStyle.Wave;

    public bool IsBars => Style == VisualStyle.Bars;

    /// <summary>The label over the first colour: it is the wave's or the bars'.</summary>
    public string ColorText => Context[IsBars ? "panel.audioToVideo.color_bars" : "panel.audioToVideo.color_wave"];

    /// <summary>The video as it will be: "1920 × 1080 · 30 fps · MP4".</summary>
    public string SizeText
    {
        get
        {
            var (width, height) = VisualGraph.SizeOf(Format);
            return string.Create(CultureInfo.InvariantCulture, $"{width} × {height} · {VisualGraph.FrameRate} fps · MP4");
        }
    }

    /// <summary>The box the preview sits in: the shape of the video, as large as fits.</summary>
    public double PreviewWidth => PreviewBoxSize.Width;

    public double PreviewHeight => PreviewBoxSize.Height;

    private Size PreviewBoxSize
    {
        get
        {
            var (width, height) = VisualGraph.SizeOf(Format);
            var scale = Math.Min(PreviewBoxWidth / width, PreviewBoxHeight / height);
            return new Size(Math.Round(width * scale), Math.Round(height * scale));
        }
    }

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    public bool HasPreviewError => !string.IsNullOrEmpty(PreviewError);

    /// <summary>
    /// The picture is shown while it has the shape of the video: a change of colour or style leaves the old one up until the new one
    /// is drawn, but a change of shape would squeeze it into the wrong box.
    /// </summary>
    public bool ShowsPreview => Preview is not null && _previewFormat == Format;

    /// <summary>"Drawing a preview…": nothing is on show and nothing has gone wrong.</summary>
    public bool ShowsHint => !ShowsPreview && !HasPreviewError && !HasLoadError;

    public override bool CanApply => Duration > 0 && !HasLoadError;

    public override Control CreateView() => new AudioToVideoView { DataContext = this };

    [RelayCommand]
    private void SetFormat(VisualFormat format) => Format = format;

    [RelayCommand]
    private void SetStyle(VisualStyle style) => Style = style;

    private async Task LoadAsync()
    {
        try
        {
            var probe = await Context.RequireMedia().ProbeAsync(Files[0], _loading.Token);
            if (!probe.HasAudio || probe.Duration <= 0)
                throw new PanelException(Context["panel.audioToVideo.no_audio"]);
            Duration = probe.Duration;
            IsLoading = false;
            NotifyCanApplyChanged();
            RequestPreview();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "reading" for ever.
            LoadError = Context.Explain(ex);
            IsLoading = false;
            OnPropertyChanged(nameof(HasLoadError));
            OnPropertyChanged(nameof(ShowsHint));
            NotifyCanApplyChanged();
        }
    }

    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));

    partial void OnFormatChanged(VisualFormat value)
    {
        OnPropertyChanged(nameof(IsHorizontal));
        OnPropertyChanged(nameof(IsVertical));
        OnPropertyChanged(nameof(IsSquare));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(PreviewWidth));
        OnPropertyChanged(nameof(PreviewHeight));
        RequestPreview();
    }

    partial void OnStyleChanged(VisualStyle value)
    {
        OnPropertyChanged(nameof(IsWave));
        OnPropertyChanged(nameof(IsBars));
        OnPropertyChanged(nameof(ColorText));
        RequestPreview();
    }

    partial void OnWaveColorChanged(string value) => RequestPreview();

    partial void OnBackgroundColorChanged(string value) => RequestPreview();

    // ───────── The preview ─────────

    private VisualOptions Snapshot() => new(Format, Style, WaveColor, BackgroundColor);

    /// <summary>Draws the preview shortly: each call cancels the one before, so a run of changes draws only where it rests.</summary>
    private void RequestPreview()
    {
        if (_disposed || Duration <= 0)
            return;
        _previewing?.Cancel();
        var request = _previewing = CancellationTokenSource.CreateLinkedTokenSource(_loading.Token);
        _ = ShowPreviewAsync(Snapshot(), request);
    }

    /// <summary>
    /// The stretch of the file the preview is drawn from: two seconds around the middle, and the moment inside it that is taken
    /// (a little way in: the first frame of the graph is the bare background, the wave comes with the second).
    /// </summary>
    internal static (double Start, double Length, double Frame) PreviewWindow(double duration)
    {
        var start = Math.Max(0, duration / 2 - 1);
        var length = Math.Min(2, duration - start);
        return (start, length, Math.Min(0.5, length / 2));
    }

    private async Task ShowPreviewAsync(VisualOptions options, CancellationTokenSource request)
    {
        string? png = null;
        try
        {
            await Task.Delay(PreviewDelay, request.Token);
            var media = Context.RequireMedia();
            var (start, length, frame) = PreviewWindow(Duration);
            png = OutputPaths.Temp(".png");
            var graph = VisualGraph.Build(options.Format, options.Style, options.WaveColor, options.BackgroundColor, VisualGraph.SizeOf(options.Format).Width / PreviewShrink);
            await media.RunAsync(["-ss", MediaTools.Seconds(start), "-t", MediaTools.Seconds(length), "-i", Files[0], "-filter_complex", graph,
                "-map", "[v]", "-ss", MediaTools.Seconds(frame), "-frames:v", "1", png], cancel: request.Token);
            var path = png;
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }, request.Token);
            if (request.IsCancellationRequested || _disposed)
            {
                bitmap.Dispose();
                return;
            }
            var old = Preview;
            _previewFormat = options.Format;
            Preview = bitmap;
            PreviewError = null;
            // The surface may still be drawing the old picture: it goes a moment later.
            if (old is not null)
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // A newer change replaced this one, or the window closed.
        }
        catch (Exception ex)
        {
            // Fire-and-forget: say what went wrong instead of leaving a stale picture with no explanation.
            PreviewError = Context.Explain(ex);
        }
        finally
        {
            if (png is not null)
                MediaTools.TryDelete(png);
            // Not still the newest request (which RequestPreview may cancel): let it go before disposing it.
            if (ReferenceEquals(_previewing, request))
                _previewing = null;
            request.Dispose();
        }
    }

    // ───────── Apply ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        // The options as they are now: the window stays editable while this runs.
        var options = Snapshot();
        var media = Context.RequireMedia();
        var source = Files[0];
        var probe = await media.ProbeAsync(source, cancel);
        if (!probe.HasAudio)
            throw new PanelException(Context["panel.audioToVideo.no_audio"]);

        var graph = VisualGraph.Build(options.Format, options.Style, options.WaveColor, options.BackgroundColor);
        var output = Context.OutputFor(source, "audioToVideo", ".mp4");
        List<string> args =
        [
            "-i", source, "-filter_complex", graph, "-map", "[v]", "-map", "0:a:0",
            "-c:v", "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p", "-r", VisualGraph.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", "-map_metadata", "0", output,
        ];
        try
        {
            await media.RunAsync(args, probe.Duration, progress, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        return [output];
    }

    public override void Dispose()
    {
        _disposed = true;
        _loading.Cancel();
        _previewing?.Cancel();
        Preview?.Dispose();
        base.Dispose();
    }
}
