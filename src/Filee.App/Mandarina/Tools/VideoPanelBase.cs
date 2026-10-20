// Mandarina: what the three VIDEO panels (trim, split, snapshots) have in common. The clip is read once (length, frame
// rate, size, sound), a big frame follows the cursor, a film strip is drawn under it, and the cursor, the kept range and the
// cut-point flags are plain numbers that the timeline edits. VideoStageView is the shared half of their views;
// TrimVideoPanel, SplitVideoPanel and VideoSnapshotsPanel add their own options and what Apply writes.

using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

public abstract partial class VideoPanelBase : ToolPanel
{
    /// <summary>Widest frame asked of FFmpeg for the big picture.</summary>
    private const int FrameWidth = 960;

    /// <summary>Roughly the room the track gets in the panel, to pick how many thumbnails the strip has.</summary>
    private const double StripWidth = 540;
    private const double StripTrackHeight = 52;
    private const int StripHeight = 64;

    /// <summary>How long a move of the cursor rests before its frame is fetched, so a quick burst of moves costs one FFmpeg run.</summary>
    private static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(120);

    private readonly CancellationTokenSource _loading = new();
    private MediaTools? _media;
    private double _wanted;
    private bool _fetching;
    private int _frameOnScreen = -1;

    protected VideoPanelBase(PanelContext context) : base(context)
    {
    }

    /// <summary>The clip: what ffprobe says about it. Null until it is read.</summary>
    public MediaProbe? Probe { get; private set; }

    /// <summary>Length of the clip in seconds (0 until it is read).</summary>
    [ObservableProperty] private double _duration;

    /// <summary>The kept range: where it starts and ends, in seconds.</summary>
    [ObservableProperty] private double _start;

    [ObservableProperty] private double _end;

    /// <summary>The cursor, in seconds: the frame shown is the one there.</summary>
    [ObservableProperty] private double _position;

    /// <summary>The big frame (a decoded picture of the clip at the cursor).</summary>
    [ObservableProperty] private IImage? _frame;

    /// <summary>The film strip under the frame, once it is ready.</summary>
    [ObservableProperty] private IImage? _strip;

    /// <summary>The flags on the timeline, in seconds (a new list each time they change).</summary>
    [ObservableProperty] private IReadOnlyList<double>? _markers;

    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private string? _loadError;

    public string FileName => Path.GetFileName(Files[0]);

    /// <summary>Frames per second (25 when the file does not say).</summary>
    public double FrameRate => FrameStep.Rate(Probe?.FrameRate ?? 0);

    /// <summary>Seconds one frame lasts.</summary>
    public double FrameLength => 1 / FrameRate;

    /// <summary>False for a panel whose timeline has no handles.</summary>
    public virtual bool ShowRange => true;

    public string DurationText => PanelFormat.Time(Duration);

    public string PositionText => PanelFormat.Time(Position);

    /// <summary>The clip is read and has a length: the options can be used.</summary>
    public bool IsReady => !IsLoading && LoadError is null && Duration > 0;

    /// <summary>Cancelled when the window closes.</summary>
    protected CancellationToken LoadToken => _loading.Token;

    /// <summary>Starts reading the clip; each panel calls it at the end of its own constructor, when its fields exist.</summary>
    protected void StartLoading() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var media = _media = Context.RequireMedia();
            var probe = await media.ProbeAsync(Files[0], _loading.Token);
            if (!probe.HasVideo || probe.Width < 1 || probe.Height < 1)
                throw new PanelException(Context["panel.videoStage.no_video"]);
            if (!(probe.Duration > 0))
                throw new PanelException(Context["panel.videoStage.no_duration"]);
            Probe = probe;
            OnPropertyChanged(nameof(FrameRate));
            OnPropertyChanged(nameof(FrameLength));
            Duration = probe.Duration;
            Start = 0;
            End = probe.Duration;
            Position = 0;
            Frame = await FrameAtAsync(media, 0, _loading.Token);
            _frameOnScreen = 0;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "reading" for ever.
            LoadError = Context.Explain(ex);
        }
        IsLoading = false;
        if (LoadError is null)
            _ = LoadStripAsync();
    }

    /// <summary>The frame at <paramref name="seconds"/> (null when FFmpeg cannot make one there: a preview is not worth failing for).</summary>
    private async Task<IImage?> FrameAtAsync(MediaTools media, double seconds, CancellationToken cancel, int width = FrameWidth)
    {
        try
        {
            return await media.FrameAsync(Files[0], SeekTo(seconds), width, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A small picture of the clip at <paramref name="seconds"/>, no wider than <paramref name="width"/> pixels (null when there
    /// is none). The same frame the big picture would show there.
    /// </summary>
    protected Task<IImage?> ThumbnailAsync(double seconds, int width, CancellationToken cancel) =>
        _media is { } media ? FrameAtAsync(media, seconds, cancel, width) : Task.FromResult<IImage?>(null);

    private async Task LoadStripAsync()
    {
        try
        {
            if (_media is not { } media || Probe is not { } probe)
                return;
            var count = VideoStrip.CountFor(probe.Width / (double)probe.Height, StripWidth, StripTrackHeight);
            Strip = await VideoStrip.BuildAsync(media, Files[0], probe.Duration, count, StripHeight, _loading.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The strip is decoration: without it the timeline is a plain track.
        }
    }

    /// <summary>The <c>-ss</c> that lands on the frame at <paramref name="seconds"/>: what previews, cuts and snapshots all use.</summary>
    protected double SeekTo(double seconds) => FrameStep.SeekTime(seconds, FrameRate, Duration);

    // ───────── The cursor and the range ─────────

    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        if (IsReady)
            RequestFrame(value);
    }

    partial void OnStartChanged(double value)
    {
        // The frame follows the handle being dragged (or typed), as in any editor.
        if (IsReady && ShowRange)
            Position = value;
        OnStateChanged();
    }

    partial void OnEndChanged(double value)
    {
        if (IsReady && ShowRange)
            Position = value;
        OnStateChanged();
    }

    /// <summary>The range or whether the clip is ready changed: the texts and whether Apply is possible depend on it.</summary>
    protected virtual void OnStateChanged()
    {
    }

    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        RefreshReady();
    }

    partial void OnIsLoadingChanged(bool value) => RefreshReady();

    partial void OnLoadErrorChanged(string? value) => RefreshReady();

    private void RefreshReady()
    {
        OnPropertyChanged(nameof(IsReady));
        NotifyCanApplyChanged();
        OnStateChanged();
    }

    /// <summary>Moves the cursor one frame forward (<paramref name="direction"/> 1) or back (-1).</summary>
    public void StepFrame(int direction) => Position = FrameStep.Next(Position, FrameRate, direction, Duration);

    // ───────── The big frame ─────────

    /// <summary>
    /// Asks for the frame at <paramref name="seconds"/>. After a short rest the newest request is fetched; while one is being
    /// fetched, newer ones only update what is wanted next, and as soon as the picture arrives the newest is fetched, so a
    /// cursor that keeps moving drags the picture along instead of leaving it behind until it stops.
    /// </summary>
    private void RequestFrame(double seconds)
    {
        _wanted = seconds;
        if (_fetching)
            return;
        _fetching = true;
        _ = FrameLoopAsync();
    }

    private async Task FrameLoopAsync()
    {
        try
        {
            await Task.Delay(FrameDelay, _loading.Token);
            while (true)
            {
                var target = _wanted;
                var index = FrameStep.FrameAt(target, FrameRate, Duration);
                if (index != _frameOnScreen && _media is { } media && await FrameAtAsync(media, target, _loading.Token) is { } frame)
                {
                    var previous = Frame;
                    Frame = frame;
                    _frameOnScreen = index;
                    // Scrubbing makes many frames; the one just replaced is let go a moment later, once the view has surely drawn the new one.
                    if (previous is IDisposable old)
                        DispatcherTimer.RunOnce(old.Dispose, TimeSpan.FromSeconds(1));
                }
                if (_wanted == target)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A frame that cannot be read leaves the one on screen.
        }
        finally
        {
            _fetching = false;
        }
    }

    public override void Dispose()
    {
        _loading.Cancel();
        base.Dispose();
    }
}
