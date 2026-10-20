// Mandarina: TRIM for video. Two handles on a film strip choose what to keep; the frame under the cursor is shown big,
// the cursor steps frame by frame, "mark start/end here" take the cursor's time, and (when ffplay sits beside FFmpeg) the kept
// stretch can be played. Apply saves "name trimmed.ext" beside the original: re-encoded to start on the exact frame and
// last exactly the frames chosen, or, with "no re-encode", copied (fast, starting at the key frame before the start).

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("trimVideo", NeedsFfmpeg = true)]
public sealed partial class TrimVideoPanel : VideoPanelBase
{
    private readonly string? _ffplay;
    private CancellationTokenSource? _player;

    /// <summary>Copy the streams instead of re-encoding: fast, but the cut falls on the nearest key frame before the start.</summary>
    [ObservableProperty] private bool _noRecode;

    [ObservableProperty] private bool _isPlaying;

    public TrimVideoPanel(PanelContext context) : base(context)
    {
        _ffplay = FindPlayer(context);
        StartLoading();
    }

    public override Size PreferredSize => new(980, 640);

    public string StartText => PanelFormat.Time(Start);

    public string EndText => PanelFormat.Time(End);

    /// <summary>How much is kept.</summary>
    public string SelectionText => PanelFormat.Time(Math.Max(0, End - Start));

    /// <summary>The kept range holds at least one frame.</summary>
    public override bool CanApply => IsReady && End - Start >= FrameLength - 1e-6;

    /// <summary>A handle was moved in from the whole clip.</summary>
    public override bool HasUnsavedWork => IsReady && (Start > 0.001 || End < Duration - 0.001);

    /// <summary>ffplay was found beside FFmpeg: the clip can be played here.</summary>
    public bool CanPlay => _ffplay is not null;

    public override Control CreateView() => new TrimVideoView { DataContext = this };

    protected override void OnStateChanged()
    {
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(SelectionText));
        NotifyCanApplyChanged();
    }

    // ───────── Setting the range ─────────

    /// <summary>A time typed in the Start box ("1:05.5"); an unreadable one puts the old value back.</summary>
    public void CommitStart(string? text)
    {
        if (IsReady && PanelFormat.TryParseTime(text, out var seconds))
            Start = Math.Clamp(seconds, 0, Math.Max(0, End - FrameLength));
        OnPropertyChanged(nameof(StartText));
    }

    /// <summary>A time typed in the End box.</summary>
    public void CommitEnd(string? text)
    {
        if (IsReady && PanelFormat.TryParseTime(text, out var seconds))
            End = Math.Clamp(seconds, Math.Min(Duration, Start + FrameLength), Duration);
        OnPropertyChanged(nameof(EndText));
    }

    /// <summary>The kept part starts at the cursor (never at or after the end).</summary>
    public void MarkStart()
    {
        if (IsReady)
            Start = Math.Clamp(Position, 0, Math.Max(0, End - FrameLength));
    }

    /// <summary>The kept part ends at the cursor (never at or before the start).</summary>
    public void MarkEnd()
    {
        if (IsReady)
            End = Math.Clamp(Position, Math.Min(Duration, Start + FrameLength), Duration);
    }

    // ───────── Playing the kept part ─────────

    private static string? FindPlayer(PanelContext context)
    {
        if (context.Media is not { } media || Path.GetDirectoryName(media.Ffmpeg) is not { } folder)
            return null;
        var player = Path.Combine(folder, EngineEnvironment.ProgramName("ffplay.exe"));
        return File.Exists(player) ? player : null;
    }

    /// <summary>Starts the player and waits for it; cancelling its token kills it. Replaced in tests, so no window opens.</summary>
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task> LaunchPlayer { get; set; } =
        (player, arguments, cancel) => ProcessRunner.RunAsync(player, arguments, TimeSpan.FromHours(6), cancel);

    /// <summary>ffplay's arguments to play <paramref name="length"/> seconds of <paramref name="file"/> from <paramref name="start"/>.</summary>
    internal static List<string> PlayArguments(string file, double start, double length, string title) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostats", "-autoexit",
        "-ss", VideoCut.Seconds(start), "-t", VideoCut.Seconds(length),
        // A 4K clip, or one shot upright, must not open a window bigger than the screen: the picture is fitted into 960 × 600.
        "-x", "960", "-y", "600",
        "-window_title", title, file,
    ];

    /// <summary>Plays the kept stretch in ffplay's own window; it ends with the stretch, or with <see cref="StopPlayback"/>.</summary>
    public async Task PlayAsync()
    {
        if (IsPlaying || _ffplay is not { } player || !IsReady)
            return;
        var source = new CancellationTokenSource();
        _player = source;
        IsPlaying = true;
        try
        {
            var title = $"{FileName} · {Title}";
            await LaunchPlayer(player, PlayArguments(Files[0], Start, End - Start, title), source.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception or IOException or TimeoutException)
        {
            // Stopped, or the player did not start: nothing to report, the button simply goes back to "Play".
        }
        finally
        {
            _player = null;
            source.Dispose();
            IsPlaying = false;
        }
    }

    /// <summary>Closes the player.</summary>
    public void StopPlayback() => _player?.Cancel();

    // ───────── Apply ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        StopPlayback();
        var media = Context.RequireMedia();
        var source = Files[0];
        var probe = Probe ?? await media.ProbeAsync(source, cancel);
        var span = VideoCut.Span(Start, End, probe);
        var output = Context.OutputFor(source, "trimVideo");
        try
        {
            await media.RunAsync(VideoCut.Arguments(source, output, span, probe, NoRecode), span.Length, progress, cancel);
        }
        catch
        {
            await VideoCut.DeleteFileAsync(output);
            throw;
        }
        return [output];
    }

    public override void Dispose()
    {
        StopPlayback();
        base.Dispose();
    }
}
