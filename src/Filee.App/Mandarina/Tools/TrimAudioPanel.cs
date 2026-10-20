// Mandarina: TRIM for audio. Drag the two handles over the waveform to choose what to keep, listen to the
// range, strip the silence at both ends, and Apply saves "name trimmed.ext" beside the original. This panel is the
// pattern for the others: a ToolPanel with observable options, a view (TrimAudioView.axaml) bound to it, ApplyAsync
// running FFmpeg through MediaTools, tests that drive the panel without a window.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("trimAudio", NeedsFfmpeg = true)]
public sealed partial class TrimAudioPanel : ToolPanel
{
    /// <summary>Slices of the waveform. More than the control ever draws bars for.</summary>
    private const int WaveBuckets = 1600;

    private readonly CancellationTokenSource _loading = new();
    private DispatcherTimer? _playTimer;
    private DateTime _playedAt;
    private double _playedFrom;
    private double _playedLength;

    [ObservableProperty] private float[]? _peaks;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _start;
    [ObservableProperty] private double _end;
    [ObservableProperty] private double _position = -1;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _loadError;

    public TrimAudioPanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(720, 520);

    public string FileName => Path.GetFileName(Files[0]);

    public string DurationText => PanelFormat.Time(Duration);

    public string StartText => PanelFormat.Time(Start);

    public string EndText => PanelFormat.Time(End);

    public string SelectionText => PanelFormat.Time(Math.Max(0, End - Start));

    public override bool CanApply => Duration > 0 && End - Start >= 0.05;

    /// <summary>A handle was moved in from the whole file.</summary>
    public override bool HasUnsavedWork => Duration > 0 && (Start > 0.001 || End < Duration - 0.001);

    public override Control CreateView() => new TrimAudioView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var media = Context.RequireMedia();
            var probe = await media.ProbeAsync(Files[0], _loading.Token);
            if (!probe.HasAudio || probe.Duration <= 0)
                throw new PanelException(Context["panel.trimAudio.no_audio"]);
            Duration = probe.Duration;
            Start = 0;
            End = probe.Duration;
            Peaks = await media.PeaksAsync(Files[0], probe.Duration, WaveBuckets, _loading.Token);
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

    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        NotifyCanApplyChanged();
    }

    partial void OnStartChanged(double value)
    {
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(SelectionText));
        NotifyCanApplyChanged();
    }

    partial void OnEndChanged(double value)
    {
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(SelectionText));
        NotifyCanApplyChanged();
    }

    /// <summary>A time typed in the Start box ("1:05.5"); an unreadable one puts the old value back.</summary>
    public void CommitStart(string? text)
    {
        if (PanelFormat.TryParseTime(text, out var seconds))
            Start = Math.Clamp(seconds, 0, Math.Max(0, End - 0.05));
        OnPropertyChanged(nameof(StartText));
    }

    /// <summary>A time typed in the End box.</summary>
    public void CommitEnd(string? text)
    {
        if (PanelFormat.TryParseTime(text, out var seconds))
            End = Math.Clamp(seconds, Math.Min(Duration, Start + 0.05), Duration);
        OnPropertyChanged(nameof(EndText));
    }

    /// <summary>Back to the whole file.</summary>
    public void Reset()
    {
        Start = 0;
        End = Duration;
    }

    /// <summary>Moves the handles in to the first and last audible stretch (never into it).</summary>
    public void TrimSilence()
    {
        if (Peaks is not { Length: > 0 } peaks || Duration <= 0)
            return;
        var loudest = peaks.Max();
        var threshold = Math.Max(0.01f, loudest * 0.04f);
        var first = Array.FindIndex(peaks, peak => peak > threshold);
        var last = Array.FindLastIndex(peaks, peak => peak > threshold);
        if (first < 0)
            return;
        var slice = Duration / peaks.Length;
        Start = Math.Max(0, first * slice - 0.05);
        End = Math.Min(Duration, (last + 1) * slice + 0.05);
    }

    /// <summary>Plays the chosen range from the playhead (or its start); a second call stops.</summary>
    public async Task TogglePlayAsync()
    {
        if (IsPlaying)
        {
            Stop();
            return;
        }
        if (Duration <= 0 || Context.Media is not { } media)
            return;
        var from = Position >= Start && Position < End - 0.1 ? Position : Start;
        try
        {
            var wav = await media.PreviewWavAsync(Files[0], from, End, _loading.Token);
            Context.Platform.PlaySound(wav);
        }
        catch (Exception ex) when (ex is PanelException or IOException or OperationCanceledException)
        {
            return;
        }
        _playedFrom = from;
        _playedLength = Math.Min(End - from, 300);
        _playedAt = DateTime.UtcNow;
        IsPlaying = true;
        _playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Normal, (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - _playedAt).TotalSeconds;
            if (elapsed >= _playedLength)
                Stop();
            else
                Position = _playedFrom + elapsed;
        });
        _playTimer.Start();
    }

    public void Stop()
    {
        _playTimer?.Stop();
        _playTimer = null;
        if (IsPlaying)
            Context.Platform.StopSound();
        IsPlaying = false;
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        Stop();
        var media = Context.RequireMedia();
        var source = Files[0];
        var output = Context.OutputFor(source, "trimAudio");
        var probe = await media.ProbeAsync(source, cancel);

        // Re-encoded in the file's own format at a high quality, so the cut is exact; tags are kept, cover art is not.
        List<string> args = ["-ss", MediaTools.Seconds(Start), "-t", MediaTools.Seconds(End - Start), "-i", source, "-vn", "-map", "0:a:0", "-map_metadata", "0"];
        args.AddRange(EncoderFor(Path.GetExtension(source), probe.AudioCodec));
        args.Add(output);
        try
        {
            await media.RunAsync(args, End - Start, progress, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        return [output];
    }

    /// <summary>FFmpeg's audio encoder options that keep <paramref name="extension"/>'s format at high quality.</summary>
    internal static string[] EncoderFor(string extension, string? sourceCodec)
    {
        var pcm = sourceCodec is { } codec && codec.StartsWith("pcm_", StringComparison.Ordinal);
        return extension.ToLowerInvariant() switch
        {
            ".mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
            ".m4a" or ".aac" => ["-c:a", "aac", "-b:a", "256k"],
            ".flac" => ["-c:a", "flac"],
            ".wav" => ["-c:a", pcm ? sourceCodec! : "pcm_s16le"],
            ".aif" or ".aiff" => ["-c:a", pcm && sourceCodec!.EndsWith("be", StringComparison.Ordinal) ? sourceCodec : "pcm_s16be"],
            ".ogg" or ".oga" => ["-c:a", "libvorbis", "-q:a", "6"],
            ".opus" => ["-c:a", "libopus", "-b:a", "160k"],
            _ => [],
        };
    }

    public override void Dispose()
    {
        _loading.Cancel();
        Stop();
        base.Dispose();
    }
}
