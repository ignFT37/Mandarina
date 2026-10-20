// Mandarina: SNAPSHOT for video: save frames of the clip as PNG. Two ways to choose them: pick frames one by one
// (the frame under the cursor is added to a list of small thumbnails), or take one every N seconds between the two handles.
// Apply writes one PNG per frame into a folder beside the original, "name 00-05.30.png", at the video's real resolution and
// upright; the frame saved is the very one the big picture shows there.

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>A frame picked to be saved, listed as a small thumbnail with its time.</summary>
public sealed partial class SnapshotItem(int frame, double time) : ObservableObject
{
    /// <summary>Index of the frame in the video (the identity of the capture: a frame is picked once).</summary>
    public int Frame { get; } = frame;

    /// <summary>Where the frame starts, in seconds.</summary>
    public double Time { get; } = time;

    public string Text { get; } = PanelFormat.Time(time);

    /// <summary>The small picture, once FFmpeg has made it.</summary>
    [ObservableProperty] private IImage? _thumb;
}

[ToolPanel("videoSnapshots", NeedsFfmpeg = true)]
public sealed partial class VideoSnapshotsPanel : VideoPanelBase
{
    /// <summary>Width of the thumbnails in the list.</summary>
    private const int ThumbWidth = 160;

    /// <summary>Choose frames one by one (true) or take one every N seconds (false).</summary>
    [ObservableProperty] private bool _isPickMode = true;

    /// <summary>The N of "every N seconds" (1 to 60).</summary>
    [ObservableProperty] private int _interval = 5;

    public VideoSnapshotsPanel(PanelContext context) : base(context)
    {
        StartLoading();
    }

    public override Size PreferredSize => new(980, 640);

    /// <summary>The frames picked, in time order.</summary>
    public ObservableCollection<SnapshotItem> Captures { get; } = [];

    public bool IsIntervalMode => !IsPickMode;

    public bool HasCaptures => Captures.Count > 0;

    /// <summary>The handles only mean something when frames are taken at an interval between them.</summary>
    public override bool ShowRange => !IsPickMode;

    /// <summary>How many PNGs Apply would write.</summary>
    public int Count => IsPickMode ? Captures.Count : IntervalFrames().Count;

    /// <summary>"6 snapshots will be saved" (interval mode).</summary>
    public string CountText => !IsReady || IsPickMode ? "" : Count == 1
        ? Context["panel.videoSnapshots.count_one"]
        : Context.Format("panel.videoSnapshots.count_many", Count);

    public override bool CanApply => IsReady && Count > 0;

    public override Control CreateView() => new VideoSnapshotsView { DataContext = this };

    // ───────── Mode and interval ─────────

    /// <summary>Picks frames one by one (true) or takes one every N seconds (false).</summary>
    public void SelectMode(bool pick) => IsPickMode = pick;

    partial void OnIsPickModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIntervalMode));
        OnPropertyChanged(nameof(ShowRange));
        Refresh();
    }

    partial void OnIntervalChanged(int value) => Refresh();

    protected override void OnStateChanged() => Refresh();

    // ───────── Picked frames ─────────

    /// <summary>Adds the frame under the cursor to the list. False when it is already there.</summary>
    public bool CaptureHere()
    {
        if (!IsReady)
            return false;
        var frame = FrameStep.FrameAt(Position, FrameRate, Duration);
        if (Captures.Any(capture => capture.Frame == frame))
            return false;
        var item = new SnapshotItem(frame, FrameStep.TimeOf(frame, FrameRate));
        Captures.Insert(Captures.TakeWhile(capture => capture.Frame < frame).Count(), item);
        _ = LoadThumbAsync(item);
        Refresh();
        return true;
    }

    /// <summary>Takes the frame off the list.</summary>
    public void RemoveCapture(SnapshotItem item)
    {
        if (Captures.Remove(item))
            Refresh();
    }

    private async Task LoadThumbAsync(SnapshotItem item)
    {
        try
        {
            item.Thumb = await ThumbnailAsync(item.Time, ThumbWidth, LoadToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A thumbnail that cannot be made leaves its tile empty; the capture is still saved.
        }
    }

    private void Refresh()
    {
        Markers = IsPickMode ? [.. Captures.Select(capture => capture.Time)] : null;
        OnPropertyChanged(nameof(HasCaptures));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        NotifyCanApplyChanged();
    }

    // ───────── What Apply writes ─────────

    /// <summary>The frames an interval of N seconds takes between the handles: the one at the start, then one every N seconds, up to the end.</summary>
    internal List<int> IntervalFrames()
    {
        var frames = new List<int>();
        if (!IsReady)
            return frames;
        var step = Math.Clamp(Interval, 1, 60);
        var (from, to) = (Math.Min(Start, End), Math.Max(Start, End));
        for (var k = 0; from + k * step < to - 1e-6; k++)
        {
            var frame = FrameStep.FrameAt(from + k * step, FrameRate, Duration);
            if (frames.Count == 0 || frame > frames[^1])
                frames.Add(frame);
        }
        if (frames.Count == 0)
            frames.Add(FrameStep.FrameAt(from, FrameRate, Duration));
        return frames;
    }

    /// <summary>"00-05.30" for 5.3 s, "1-02-03.40" for an hour and more: the time without colons, so it can be part of a file name.</summary>
    internal static string Stamp(double seconds)
    {
        var hundredths = (long)Math.Round(Math.Max(0, seconds) * 100);
        var (hours, minutes, secs, rest) = (hundredths / 360_000, hundredths / 6000 % 60, hundredths / 100 % 60, hundredths % 100);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}-{minutes:00}-{secs:00}.{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}-{secs:00}.{rest:00}");
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var media = Context.RequireMedia();
        var source = Files[0];
        var probe = Probe ?? await media.ProbeAsync(source, cancel);
        var frames = IsPickMode ? Captures.Select(capture => capture.Frame).ToList() : IntervalFrames();
        var stem = Path.GetFileNameWithoutExtension(source);
        var folder = Context.FolderFor(source, "videoSnapshots");

        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var time = FrameStep.TimeOf(frames[i], probe.FrameRate);
                var output = Path.Combine(folder, $"{stem} {Stamp(time)}.png");
                // No scaling: the video's own resolution, upright (FFmpeg turns a phone video by itself).
                await media.RunAsync(["-ss", VideoCut.Seconds(FrameStep.SeekTime(time, probe.FrameRate, probe.Duration)), "-i", source,
                    "-frames:v", "1", "-an", output], cancel: cancel);
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                    throw new PanelException(Context.Format("panel.videoSnapshots.no_frame", PanelFormat.Time(time)));
                progress.Report((i + 1) / (double)frames.Count);
            }
        }
        catch
        {
            // All or nothing: the pictures of a failed run are not left behind.
            await VideoCut.DeleteFolderAsync(folder);
            throw;
        }
        return [folder];
    }
}
