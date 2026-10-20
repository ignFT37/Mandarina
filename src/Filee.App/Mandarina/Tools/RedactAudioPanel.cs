// Mandarina: BLEEP for audio: mark the stretches to cover on the waveform (drag the two handles over a word, press Add)
// and Apply replaces each of them with a 1 kHz tone, as on television. The rest of the file is untouched. One FFmpeg run
// re-encodes the file in its own format (BleepGraph builds the filter graph) and saves "name bleeped.ext" beside the original.

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>A stretch of the file that will be covered: one row of the list.</summary>
public sealed record BleepRange(double Start, double End)
{
    public string RangeText => $"{PanelFormat.Time(Start)} – {PanelFormat.Time(End)}";

    public string LengthText => LengthOf(End - Start);

    /// <summary>"1.20 s" up to a minute, "1:05.30" beyond.</summary>
    internal static string LengthOf(double seconds) =>
        seconds < 60 ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.00} s") : PanelFormat.Time(seconds);
}

[ToolPanel("redactAudio", NeedsFfmpeg = true)]
public sealed partial class RedactAudioPanel : ToolPanel
{
    /// <summary>Slices of the waveform. More than the control ever draws bars for.</summary>
    private const int WaveBuckets = 1600;

    /// <summary>The handles are never closer than this (seconds); a selection shorter than that cannot be added.</summary>
    private const double MinimumSelection = 0.05;

    /// <summary>A longer filter graph goes to a file: the command line has a limit of about 32 000 characters.</summary>
    internal const int InlineGraphLimit = 20000;

    private readonly CancellationTokenSource _loading = new();
    private DispatcherTimer? _playTimer;
    private DateTime _playedAt;
    private double _playedFrom;
    private double _playedLength;

    [ObservableProperty] private float[]? _peaks;
    [ObservableProperty] private double _duration;

    /// <summary>The left handle: the stretch Add will cover, in seconds.</summary>
    [ObservableProperty] private double _start;

    /// <summary>The right handle.</summary>
    [ObservableProperty] private double _end;

    [ObservableProperty] private double _position = -1;

    /// <summary>The loudness of the tone, in percent of full scale.</summary>
    [ObservableProperty] private double _volume = 35;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _loadError;

    public RedactAudioPanel(PanelContext context) : base(context)
    {
        Ranges.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Marks));
            OnPropertyChanged(nameof(HasRanges));
            OnPropertyChanged(nameof(RangesTitle));
            OnPropertyChanged(nameof(CanAdd));
            NotifyCanApplyChanged();
        };
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(720, 604);

    /// <summary>What will be covered, in order. Add keeps it free of overlaps.</summary>
    public ObservableCollection<BleepRange> Ranges { get; } = [];

    public string FileName => Path.GetFileName(Files[0]);

    public string DurationText => PanelFormat.Time(Duration);

    /// <summary>The handles' stretch: "0:05.10 – 0:06.30 · 1.20 s".</summary>
    public string SelectionText => $"{PanelFormat.Time(Start)} – {PanelFormat.Time(End)}  ·  {BleepRange.LengthOf(Math.Max(0, End - Start))}";

    /// <summary>The covered stretches as the waveform paints them (in red).</summary>
    public IReadOnlyList<WaveRange> Marks => [.. Ranges.Select(range => new WaveRange(range.Start, range.End))];

    public bool HasRanges => Ranges.Count > 0;

    public string RangesTitle => Context.Format("panel.redactAudio.list_title", Ranges.Count);

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    /// <summary>Add works when the handles hold a real stretch that is not already covered whole.</summary>
    public bool CanAdd => Duration > 0 && End - Start >= MinimumSelection
        && !Ranges.Any(range => range.Start <= Start + 0.001 && range.End >= End - 0.001);

    public override bool CanApply => Duration > 0 && Ranges.Count > 0;

    public override bool HasUnsavedWork => Ranges.Count > 0;

    public override Control CreateView() => new RedactAudioView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var media = Context.RequireMedia();
            var probe = await media.ProbeAsync(Files[0], _loading.Token);
            if (!probe.HasAudio || probe.Duration <= 0)
                throw new PanelException(Context["panel.redactAudio.no_audio"]);
            Duration = probe.Duration;
            // A stretch in the middle to begin with (a tenth of the file, half a second at least): handles that start on top of each
            // other, or on the edges of a long file, are hard to see and to grab.
            var length = Math.Min(probe.Duration, Math.Max(0.5, probe.Duration * 0.1));
            Start = (probe.Duration - length) / 2;
            End = Start + length;
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
        OnPropertyChanged(nameof(CanAdd));
        NotifyCanApplyChanged();
    }

    partial void OnStartChanged(double value)
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnEndChanged(double value)
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoadError));

    /// <summary>Covers the stretch between the handles; it is joined to every range it overlaps or touches.</summary>
    public void AddSelection()
    {
        if (!CanAdd)
            return;
        var joined = BleepGraph.Normalize([.. Ranges.Select(range => new WaveRange(range.Start, range.End)), new WaveRange(Start, End)], Duration);
        Ranges.Clear();
        foreach (var range in joined)
            Ranges.Add(new BleepRange(range.Start, range.End));
    }

    /// <summary>Takes a stretch off the list.</summary>
    public void Remove(BleepRange range) => Ranges.Remove(range);

    /// <summary>Plays the stretch between the handles from the playhead (or its start); a second call stops.</summary>
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
        // The options as they are now: the window stays editable while this runs.
        List<WaveRange> marked = [.. Ranges.Select(range => new WaveRange(range.Start, range.End))];
        var volume = Volume / 100;
        var media = Context.RequireMedia();
        var source = Files[0];
        var probe = await media.ProbeAsync(source, cancel);
        if (!probe.HasAudio)
            throw new PanelException(Context["panel.redactAudio.no_audio"]);

        var graph = BleepGraph.Build(marked, probe.Duration, probe.SampleRate, volume);
        if (graph.Length == 0)
            throw new PanelException(Context["panel.saved_none"]);
        var output = Context.OutputFor(source, "redactAudio");

        // The file's own format again at high quality; the tags stay, the cover picture does not.
        List<string> tail = ["-map", "[out]", "-map_metadata", "0", .. await AudioEncoding.ArgumentsAsync(media, source, probe, cancel), output];
        string? script = null;
        try
        {
            if (graph.Length <= InlineGraphLimit)
            {
                await media.RunAsync(["-i", source, "-filter_complex", graph, .. tail], probe.Duration, progress, cancel);
            }
            else
            {
                // Dozens of ranges make a graph too long for the command line: it goes to a file (FFmpeg 7 and later read it with
                // -/filter_complex; older ones know it as -filter_complex_script).
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

    public override void Dispose()
    {
        _loading.Cancel();
        Stop();
        base.Dispose();
    }
}
