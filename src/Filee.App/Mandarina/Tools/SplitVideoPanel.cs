// Mandarina: SPLIT for video. Cuts are flags on the film strip, added at the cursor (or spread evenly); the stretches
// between them become clips. Apply writes the clips into a folder beside the original, "name 1.ext", "name 2.ext", …,
// each cut like Trim cuts (so together they hold every frame of the video once, with none left out or repeated), and
// removes the folder again if anything fails.

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>A cut point as it is listed: a pill with its time.</summary>
public sealed record SplitCut(double Time, string Text);

/// <summary>A clip the cuts make: its number, "start – end" and its length.</summary>
public sealed record SplitClip(int Number, string Range, string Length, double Start, double End);

[ToolPanel("splitVideo", NeedsFfmpeg = true)]
public sealed partial class SplitVideoPanel : VideoPanelBase
{
    /// <summary>The shortest clip a cut may make, in seconds (cuts closer to each other or to the ends are ignored).</summary>
    internal const double MinClip = 0.2;

    /// <summary>The most clips "split evenly" makes.</summary>
    internal const int MostParts = 20;

    private readonly List<double> _times = [];

    /// <summary>Copy the streams instead of re-encoding: fast, but each clip starts on the key frame before its cut.</summary>
    [ObservableProperty] private bool _noRecode;

    /// <summary>The N of "split into N equal parts".</summary>
    [ObservableProperty] private int _evenParts = 3;

    public SplitVideoPanel(PanelContext context) : base(context)
    {
        StartLoading();
    }

    public override Size PreferredSize => new(980, 640);

    /// <summary>The cuts, in time order.</summary>
    public ObservableCollection<SplitCut> Cuts { get; } = [];

    /// <summary>The clips the cuts make, in time order (none while there are no cuts).</summary>
    public ObservableCollection<SplitClip> Clips { get; } = [];

    public bool HasCuts => Cuts.Count > 0;

    /// <summary>The label of the "split evenly" button, with its N.</summary>
    public string EvenPartsText => Context.Format("panel.splitVideo.even_parts", Math.Clamp(EvenParts, 2, MostParts));

    /// <summary>No handles on this timeline: the flags are the cuts.</summary>
    public override bool ShowRange => false;

    public override bool CanApply => IsReady && _times.Count > 0;

    public override bool HasUnsavedWork => HasCuts;

    public override Control CreateView() => new SplitVideoView { DataContext = this };

    partial void OnEvenPartsChanged(int value) => OnPropertyChanged(nameof(EvenPartsText));

    // ───────── The cuts ─────────

    /// <summary>Adds a cut at the cursor. False when it would leave a clip under 0.2 s (too near another cut or an end of the video).</summary>
    public bool AddCutHere() => IsReady && AddCut(Position);

    internal bool AddCut(double seconds)
    {
        // A cut is kept on the frame it lands on, so the flag, the list and the clips show the very frame where the clip starts.
        var time = FrameStep.TimeOf(FrameStep.FrameAt(seconds, FrameRate, Duration), FrameRate);
        if (time < MinClip - 1e-9 || Duration - time < MinClip - 1e-9 || _times.Any(other => Math.Abs(other - time) < MinClip - 1e-9))
            return false;
        _times.Add(time);
        Refresh();
        return true;
    }

    /// <summary>Takes the cut away.</summary>
    public void RemoveCut(SplitCut cut)
    {
        if (_times.Remove(cut.Time))
            Refresh();
    }

    /// <summary>Replaces the cuts by the ones that split the video into <paramref name="parts"/> (2 to 20) equal clips.</summary>
    public void SplitEvenly(int parts)
    {
        if (!IsReady)
            return;
        // Never so many that a clip would be under the minimum.
        parts = Math.Clamp(parts, 2, Math.Max(2, Math.Min(MostParts, (int)(Duration / MinClip))));
        _times.Clear();
        for (var i = 1; i < parts; i++)
        {
            var time = FrameStep.TimeOf(FrameStep.FrameAt(Duration * i / parts, FrameRate, Duration), FrameRate);
            if (!_times.Contains(time))
                _times.Add(time);
        }
        Refresh();
    }

    /// <summary>Splits into <see cref="EvenParts"/> equal parts.</summary>
    public void SplitEvenly() => SplitEvenly(EvenParts);

    private void Refresh()
    {
        _times.Sort();
        Cuts.Clear();
        foreach (var time in _times)
            Cuts.Add(new SplitCut(time, PanelFormat.Time(time)));
        Markers = [.. _times];

        // Without cuts there is nothing to list: the whole clip is not a result.
        Clips.Clear();
        if (_times.Count > 0)
        {
            var edges = new List<double> { 0 };
            edges.AddRange(_times);
            edges.Add(Duration);
            for (var i = 0; i + 1 < edges.Count; i++)
            {
                var (from, to) = (edges[i], edges[i + 1]);
                Clips.Add(new SplitClip(i + 1, $"{PanelFormat.Time(from)} – {PanelFormat.Time(to)}", PanelFormat.Time(to - from), from, to));
            }
        }
        OnPropertyChanged(nameof(HasCuts));
        NotifyCanApplyChanged();
    }

    // ───────── Apply ─────────

    /// <summary>"name 1.ext", "name 2.ext", … with a zero in front of the single digits when there are ten or more.</summary>
    internal static string ClipName(string source, int number, int count)
    {
        var digits = count.ToString(CultureInfo.InvariantCulture).Length;
        return $"{Path.GetFileNameWithoutExtension(source)} {number.ToString("D" + digits, CultureInfo.InvariantCulture)}{Path.GetExtension(source)}";
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var media = Context.RequireMedia();
        var source = Files[0];
        var probe = Probe ?? await media.ProbeAsync(source, cancel);
        var folder = Context.FolderFor(source, "splitVideo");
        var spans = Clips.Select(clip => VideoCut.Span(clip.Start, clip.End, probe)).ToList();
        var total = spans.Sum(span => span.Length);

        Directory.CreateDirectory(folder);
        try
        {
            var done = 0.0;
            for (var i = 0; i < spans.Count; i++)
            {
                var output = Path.Combine(folder, ClipName(source, i + 1, spans.Count));
                var share = new Share(progress, done / total, spans[i].Length / total);
                await media.RunAsync(VideoCut.Arguments(source, output, spans[i], probe, NoRecode), spans[i].Length, share, cancel);
                done += spans[i].Length;
                progress.Report(done / total);
            }
        }
        catch
        {
            // All or nothing: the clips of a failed run are not left behind.
            await VideoCut.DeleteFolderAsync(folder);
            throw;
        }
        return [folder];
    }

    /// <summary>The part of the whole progress that one clip's 0..1 covers.</summary>
    private sealed class Share(IProgress<double> whole, double from, double size) : IProgress<double>
    {
        public void Report(double value) => whole.Report(from + Math.Clamp(value, 0, 1) * size);
    }
}
