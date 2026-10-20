// Mandarina: REDACT for a video. A frame of the video (move the slider to see another) carries the areas to
// cover; each area can cover the whole video or just a stretch of it. Faces and text are found on the frame shown, locally.
// Apply re-encodes the video through FFmpeg with one filter per area (a flat box, or a blurred or pixelated copy of the
// area laid over the picture while the area is active) and saves "name redacted.ext" beside the original, audio untouched.

using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("redactVideo", NeedsFfmpeg = true)]
public sealed partial class RedactVideoPanel : RedactPanelBase
{
    /// <summary>The frame the areas are drawn on: no wider than this.</summary>
    private const int PreviewWidth = 960;

    /// <summary>The frame the detectors read: sharper than the preview, so small text is found.</summary>
    private const int DetectionWidth = 1920;

    /// <summary>A longer filter graph goes to a file: the command line has a limit of about 32 000 characters.</summary>
    private const int InlineGraphLimit = 20000;

    /// <summary>Waiting a moment before decoding a frame keeps a dragged slider from starting one decode per pixel.</summary>
    private static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _frameRequest;
    private RedactRegion? _watched;

    [ObservableProperty] private double _duration;

    /// <summary>The second of the frame shown (the slider).</summary>
    [ObservableProperty] private double _framePosition;

    [ObservableProperty] private string? _infoText;

    /// <summary>Why the frame at the slider's position could not be shown (the previous one stays).</summary>
    [ObservableProperty] private string? _frameError;

    public RedactVideoPanel(PanelContext context) : base(context)
    {
        PropertyChanged += OnOwnPropertyChanged;
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(900, 700);

    public override Control CreateView() => new RedactVideoView { DataContext = this };

    public string FrameText => PanelFormat.Time(FramePosition);

    // ───────── The frame ─────────

    private async Task LoadAsync()
    {
        try
        {
            var media = Context.RequireMedia();
            var probe = await media.ProbeAsync(Files[0], Loading.Token);
            if (!probe.HasVideo || probe.Width <= 0 || probe.Height <= 0)
                throw new PanelException(Text("no_video"));
            SourceWidth = probe.Width;
            SourceHeight = probe.Height;
            Duration = probe.Duration;
            InfoText = $"{probe.Width} × {probe.Height} · {PanelFormat.Time(probe.Duration)}";
            // A little in, not frame zero: that one is often black.
            FramePosition = Math.Min(1, probe.Duration / 2);
            await ShowFrameAsync(FramePosition, Loading.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "loading" for ever.
            LoadError = Context.Explain(ex);
        }
        IsLoading = false;
    }

    partial void OnFramePositionChanged(double value)
    {
        OnPropertyChanged(nameof(FrameText));
        if (!IsLoading && Duration > 0)
            RequestFrame();
    }

    /// <summary>The second to ask FFmpeg for: never the very end, where there is no frame left.</summary>
    private double SeekSecond(double seconds) => Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.1));

    private void RequestFrame()
    {
        _frameRequest?.Cancel();
        var request = _frameRequest = CancellationTokenSource.CreateLinkedTokenSource(Loading.Token);
        _ = FrameAfterDelayAsync(FramePosition, request);
    }

    private async Task FrameAfterDelayAsync(double seconds, CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(FrameDelay, request.Token);
            await ShowFrameAsync(seconds, request.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer position replaced this one, or the window closed.
        }
        catch (Exception ex)
        {
            FrameError = Context.Explain(ex);
        }
        finally
        {
            // Not still the newest request (which RequestFrame may cancel): let it go before disposing it.
            if (ReferenceEquals(_frameRequest, request))
                _frameRequest = null;
            request.Dispose();
        }
    }

    private async Task ShowFrameAsync(double seconds, CancellationToken cancel)
    {
        var frame = await Context.RequireMedia().FrameAsync(Files[0], SeekSecond(seconds), PreviewWidth, cancel);
        if (cancel.IsCancellationRequested)
        {
            frame.Dispose();
            cancel.ThrowIfCancellationRequested();
        }
        var previous = Preview as IDisposable;
        Preview = frame;
        previous?.Dispose();
        FrameError = null;
    }

    // ───────── The selected area's time ─────────

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectedRegion))
            return;
        if (_watched is not null)
            _watched.PropertyChanged -= OnWatchedRegionChanged;
        _watched = SelectedRegion;
        if (_watched is not null)
            _watched.PropertyChanged += OnWatchedRegionChanged;
        RaiseSelectionChanged();
    }

    /// <summary>Only a change of time matters here: moving the rectangle does not touch the boxes under the frame.</summary>
    private void OnWatchedRegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RedactRegion.Start) or nameof(RedactRegion.End) or nameof(RedactRegion.IsWholeVideo))
            RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedWholeVideo));
        OnPropertyChanged(nameof(SelectedStartText));
        OnPropertyChanged(nameof(SelectedEndText));
    }

    /// <summary>True while an area is selected: its times are shown under the frame.</summary>
    public bool HasSelection => SelectedRegion is not null;

    /// <summary>The selected area covers the video from start to end (no times); turning it off starts a stretch at the frame shown.</summary>
    public bool SelectedWholeVideo
    {
        get => SelectedRegion?.IsWholeVideo ?? true;
        set
        {
            if (SelectedRegion is { } region)
            {
                if (value)
                {
                    region.Start = null;
                    region.End = null;
                }
                else
                {
                    region.Start = Math.Clamp(FramePosition, 0, Math.Max(0, Duration - 0.05));
                    region.End = Duration;
                }
            }
            RaiseSelectionChanged();
        }
    }

    public string SelectedStartText => PanelFormat.Time(SelectedRegion?.Start ?? 0);

    public string SelectedEndText => PanelFormat.Time(SelectedRegion?.End ?? Duration);

    /// <summary>A time typed in the "From" box ("1:05.5"); an unreadable one puts the old value back.</summary>
    public void CommitSelectedStart(string? text)
    {
        if (SelectedRegion is { } region && PanelFormat.TryParseTime(text, out var seconds))
            region.Start = Math.Clamp(seconds, 0, Math.Max(0, (region.End ?? Duration) - 0.05));
        OnPropertyChanged(nameof(SelectedStartText));
    }

    /// <summary>A time typed in the "To" box.</summary>
    public void CommitSelectedEnd(string? text)
    {
        if (SelectedRegion is { } region && PanelFormat.TryParseTime(text, out var seconds))
            region.End = Math.Clamp(seconds, Math.Min(Duration, (region.Start ?? 0) + 0.05), Duration);
        OnPropertyChanged(nameof(SelectedEndText));
    }

    // ───────── Detecting ─────────

    protected override async Task<DetectionInput> PrepareDetectionAsync(CancellationToken cancel)
    {
        using var frame = await Context.RequireMedia().FrameAsync(Files[0], SeekSecond(FramePosition), DetectionWidth, cancel);
        var png = OutputPaths.Temp(".png");
        try
        {
            await using var stream = File.Create(png);
            frame.Save(stream, new PngBitmapEncoderOptions());
        }
        catch
        {
            MediaTools.TryDelete(png);
            throw;
        }
        return new DetectionInput(png, SourceWidth / frame.PixelSize.Width, SourceHeight / frame.PixelSize.Height);
    }

    // ───────── Applying ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var media = Context.RequireMedia();
        var source = Files[0];
        var output = Context.OutputFor(source, "redactVideo");
        var probe = await media.ProbeAsync(source, cancel);
        if (!probe.HasVideo)
            throw new PanelException(Text("no_video"));

        var graph = BuildGraph(SnapshotRegions(), probe.Width, probe.Height, probe.Duration, Effect, FillColor, Strength, BlockSize);
        if (graph.Length == 0)
            throw new PanelException(Context["panel.saved_none"]);

        List<string> tail = ["-map", "[vout]", "-map", "0:a?", "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-pix_fmt", "yuv420p"];
        if (probe.HasAudio)
            tail.AddRange(AudioArguments(Path.GetExtension(output), await AudioCodecsAsync(media, source, cancel)));
        tail.AddRange(["-map_metadata", "0", "-movflags", "+faststart", output]);

        string? script = null;
        try
        {
            if (graph.Length <= InlineGraphLimit)
            {
                await media.RunAsync(["-i", source, "-filter_complex", graph, .. tail], probe.Duration, progress, cancel);
            }
            else
            {
                // Too long for the command line: the graph goes to a file (FFmpeg 7 and later read it with -/filter_complex;
                // older ones know it as -filter_complex_script).
                script = OutputPaths.Temp(".txt");
                await File.WriteAllTextAsync(script, graph, cancel);
                try
                {
                    await media.RunAsync(["-i", source, "-/filter_complex", script, .. tail], probe.Duration, progress, cancel);
                }
                catch (PanelException ex) when (ex.Message.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase))
                {
                    await media.RunAsync(["-i", source, "-filter_complex_script", script, .. tail], probe.Duration, progress, cancel);
                }
            }
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        finally
        {
            if (script is not null)
                MediaTools.TryDelete(script);
        }
        return [output];
    }

    /// <summary>The audio codecs of the file's audio streams, in order; empty when ffprobe cannot say.</summary>
    private static async Task<IReadOnlyList<string>> AudioCodecsAsync(MediaTools media, string source, CancellationToken cancel)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(media.Ffprobe,
                ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name", "-of", "csv=p=0", source],
                TimeSpan.FromMinutes(1), cancel);
            return result.ExitCode == 0
                ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception or TimeoutException)
        {
            return [];
        }
    }

    /// <summary>
    /// The audio goes through untouched (<c>-c:a copy</c>) when the output's container takes every stream's codec as it
    /// is; otherwise it is encoded to AAC, which every container takes. Matroska takes anything.
    /// </summary>
    internal static string[] AudioArguments(string extension, IReadOnlyList<string> codecs)
    {
        string[] fits = extension.ToLowerInvariant() switch
        {
            ".mkv" => [],
            ".m4v" => ["aac", "alac"],
            _ => ["aac", "alac", "mp3", "ac3", "eac3"],
        };
        var copy = codecs.Count > 0 && (extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || codecs.All(codec => fits.Contains(codec)));
        return copy ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"];
    }

    // ───────── The filter graph ─────────

    /// <summary>
    /// The FFmpeg filter graph that covers <paramref name="regions"/>, ending in the stream <c>[vout]</c>; empty when no
    /// region lies inside the picture. One step per region, in order: a flat colour is a <c>drawbox</c>; a blur or a
    /// pixelation is a cropped copy of the area, changed, and laid back over the picture. Each step is limited to the
    /// region's time with <c>enable</c>. Coordinates are whole pixels inside the frame, on even values (chroma is
    /// half-size) so nothing at the edge of the area is left showing.
    /// </summary>
    internal static string BuildGraph(IReadOnlyList<RedactRegion> regions, int width, int height, double duration, RedactEffect effect,
        Color fill, double strength, double block)
    {
        var steps = new List<string> { "[0:v]format=yuv420p[v0]" };
        var current = "v0";
        var count = 0;
        foreach (var region in regions)
        {
            var (left, top, w, h) = RedactEffects.Snap(region.Rect, width, height);
            var x = left & ~1;
            var y = top & ~1;
            w = Math.Min(width, (left + w + 1) & ~1) - x;
            h = Math.Min(height, (top + h + 1) & ~1) - y;
            if (w < 2 || h < 2)
                continue;

            count++;
            var next = "v" + count.ToString(CultureInfo.InvariantCulture);
            var n = count.ToString(CultureInfo.InvariantCulture);
            var when = Enable(region, duration);
            var enable = when is null ? "" : $":enable='{when}'";
            if (effect == RedactEffect.Fill)
            {
                steps.Add($"[{current}]drawbox=x={x}:y={y}:w={w}:h={h}:color=0x{fill.R:X2}{fill.G:X2}{fill.B:X2}@1:t=fill{enable}[{next}]");
            }
            else
            {
                string change;
                if (effect == RedactEffect.Blur)
                {
                    var sigma = RedactEffects.BlurSigma(strength, w, h);
                    change = $"gblur=sigma={sigma.ToString("0.##", CultureInfo.InvariantCulture)}:steps=3";
                }
                else
                {
                    // Averaged down to one pixel per block, then enlarged without smoothing.
                    var size = RedactEffects.BlockSize(block, w, h);
                    var across = Math.Max(1, (int)Math.Ceiling((double)w / size));
                    var down = Math.Max(1, (int)Math.Ceiling((double)h / size));
                    change = $"scale={across}:{down}:flags=area,scale={w}:{h}:flags=neighbor";
                }
                steps.Add($"[{current}]split[a{n}][b{n}]");
                steps.Add($"[b{n}]crop={w}:{h}:{x}:{y},{change}[r{n}]");
                steps.Add($"[a{n}][r{n}]overlay=x={x}:y={y}{enable}[{next}]");
            }
            current = next;
        }
        if (count == 0)
            return "";
        steps.Add($"[{current}]null[vout]");
        return string.Join(';', steps);
    }

    /// <summary>The <c>enable</c> expression of a region's time, or null when it covers the whole video.</summary>
    private static string? Enable(RedactRegion region, double duration)
    {
        double? start = region.Start is > 0.0005 ? region.Start : null;
        double? end = region.End is { } stop && (duration <= 0 || stop < duration - 0.0005) ? stop : null;
        return (start, end) switch
        {
            (null, null) => null,
            ({ } a, { } b) => $"between(t,{MediaTools.Seconds(a)},{MediaTools.Seconds(b)})",
            ({ } a, null) => $"gte(t,{MediaTools.Seconds(a)})",
            (null, { } b) => $"lte(t,{MediaTools.Seconds(b)})",
        };
    }

    public override void Dispose()
    {
        _frameRequest?.Cancel();
        if (_watched is not null)
            _watched.PropertyChanged -= OnWatchedRegionChanged;
        base.Dispose();
    }
}
