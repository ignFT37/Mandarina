// Mandarina: JOIN for video: two to thirty clips become one mp4, in the order they are listed, at the size chosen
// (the first clip's by default) and at the frame rate of the first. Apply saves "first joined.mp4" beside the first clip of
// the list; the originals are not touched. The arrows reorder the list and the cross takes a clip out of it.

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("joinVideos", NeedsFfmpeg = true)]
public sealed partial class JoinVideosPanel : ToolPanel
{
    /// <summary>The fewest clips a join takes, and the most (the first ones are used when more were chosen).</summary>
    private const int MinClips = 2;
    private const int MostClips = 30;

    /// <summary>Width of a row's thumbnail, in pixels.</summary>
    private const int ThumbnailWidth = 160;

    /// <summary>A filter graph longer than this goes to FFmpeg in a file, so the command line stays short.</summary>
    private const int InlineGraphLimit = 4000;

    private readonly CancellationTokenSource _loading = new();
    private readonly List<Bitmap> _thumbnails = [];
    private readonly string[] _initialOrder;
    private JoinSize _size = JoinSize.AsFirst;

    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private string? _loadError;

    public JoinVideosPanel(PanelContext context) : base(context)
    {
        foreach (var file in Files.Take(MostClips))
            Clips.Add(new JoinRow(file));
        _initialOrder = [.. Clips.Select(clip => clip.FilePath)];
        Clips.CollectionChanged += (_, _) => Refresh();
        Sizes =
        [
            new JoinSizeOption(JoinSize.AsFirst, context["panel.joinVideos.size_first"]) { IsSelected = true },
            new JoinSizeOption(JoinSize.Hd1080, "1080p"),
            new JoinSizeOption(JoinSize.Hd720, "720p"),
        ];
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(760, 550);

    /// <summary>The clips, in the order they are joined.</summary>
    public ObservableCollection<JoinRow> Clips { get; } = [];

    /// <summary>The size pills: the first clip's size (on by default), 1080p and 720p.</summary>
    public IReadOnlyList<JoinSizeOption> Sizes { get; }

    /// <summary>The length of the clips read so far, which is the length of the result.</summary>
    public string TotalDurationText => PanelFormat.Time(Clips.Sum(clip => clip.Probe?.Duration ?? 0));

    /// <summary>What to know before joining: too few clips, or that only the first thirty are used.</summary>
    public string? NoteText => Clips.Count < MinClips
        ? Context["panel.joinVideos.need_two"]
        : Files.Count > MostClips ? Context.Format("panel.joinVideos.truncated", MostClips) : null;

    /// <summary>Two clips or more, and every one of them read: a clip that cannot be read has to leave the list first.</summary>
    public override bool CanApply => Clips.Count >= MinClips && Clips.All(clip => clip.Probe is not null);

    /// <summary>The clips were reordered or some were taken out.</summary>
    public override bool HasUnsavedWork => !Clips.Select(clip => clip.FilePath).SequenceEqual(_initialOrder);

    public override Control CreateView() => new JoinVideosView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var media = Context.RequireMedia();
            foreach (var clip in Clips.ToArray())
            {
                // A clip taken out of the list while the others are read is not read at all.
                if (Clips.Contains(clip))
                    await ReadClipAsync(media, clip);
            }
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
        Refresh();
    }

    /// <summary>Reads one clip: its length and sound, and a thumbnail from its middle. A clip that will not read is marked.</summary>
    private async Task ReadClipAsync(MediaTools media, JoinRow clip)
    {
        MediaProbe? probe;
        try
        {
            probe = await media.ProbeAsync(clip.FilePath, _loading.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            probe = null;
        }
        if (probe is not { HasVideo: true })
        {
            clip.Unreadable = true;
            Refresh();
            return;
        }
        clip.Probe = probe;
        Refresh();

        try
        {
            var frame = await media.FrameAsync(clip.FilePath, probe.Duration / 2, ThumbnailWidth, _loading.Token);
            _thumbnails.Add(frame);
            clip.Thumbnail = frame;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A clip whose frame cannot be read keeps a blank thumbnail; the join does not need it.
        }
    }

    /// <summary>Raises what depends on the clips and their reading: the total, the note and whether Apply can run.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(NoteText));
        NotifyCanApplyChanged();
    }

    /// <summary>The up arrow: the clip swaps places with the one before it (the first stays first).</summary>
    public void MoveUp(JoinRow clip)
    {
        var index = Clips.IndexOf(clip);
        if (index > 0)
            Clips.Move(index, index - 1);
    }

    /// <summary>The down arrow: the clip swaps places with the one after it (the last stays last).</summary>
    public void MoveDown(JoinRow clip)
    {
        var index = Clips.IndexOf(clip);
        if (index >= 0 && index < Clips.Count - 1)
            Clips.Move(index, index + 1);
    }

    /// <summary>The cross: the clip leaves the list; its file stays where it is.</summary>
    public void Remove(JoinRow clip) => Clips.Remove(clip);

    /// <summary>A size pill was pressed.</summary>
    public void SelectSize(JoinSizeOption option)
    {
        _size = option.Size;
        foreach (var size in Sizes)
            size.IsSelected = size == option;
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var media = Context.RequireMedia();
        var inputs = Clips.Select(clip => new JoinInput(clip.FilePath, clip.Probe ?? throw new InvalidOperationException("A clip is not read."))).ToList();
        var plan = JoinPlan.Build(inputs, _size);
        var output = Context.OutputFor(plan.Inputs[0], "joinVideos", ".mp4");

        List<string> args = [];
        foreach (var input in plan.Inputs)
            args.AddRange(["-i", input]);
        string? graphFile = null;
        try
        {
            if (plan.Graph.Length <= InlineGraphLimit)
            {
                args.AddRange(["-filter_complex", plan.Graph]);
            }
            else
            {
                // FFmpeg 9 reads a long graph from a file with -/filter_complex (-filter_complex_script is gone).
                graphFile = OutputPaths.Temp(".txt");
                await File.WriteAllTextAsync(graphFile, plan.Graph, cancel);
                args.AddRange(["-/filter_complex", graphFile]);
            }
            // The graph already leaves yuv420p, so the picture needs no pixel format of its own.
            args.AddRange(["-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output]);
            await media.RunAsync(args, plan.Duration, progress, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        finally
        {
            if (graphFile is not null)
                MediaTools.TryDelete(graphFile);
        }
        return [output];
    }

    public override void Dispose()
    {
        _loading.Cancel();
        // The window may still draw its last frame: the thumbnails go a moment later, as the crop panel's previews do.
        foreach (var thumbnail in _thumbnails)
            DispatcherTimer.RunOnce(thumbnail.Dispose, TimeSpan.FromSeconds(1));
        base.Dispose();
    }
}

/// <summary>One clip of the list: its file, what the probe found in it, and its thumbnail.</summary>
public sealed partial class JoinRow(string filePath) : ObservableObject
{
    public string FilePath { get; } = filePath;

    public string Name { get; } = Path.GetFileName(filePath);

    /// <summary>What the probe found; null until the clip is read, and for one that cannot be read.</summary>
    [ObservableProperty] private MediaProbe? _probe;

    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>FFmpeg finds no video in the file (or cannot read it at all).</summary>
    [ObservableProperty] private bool _unreadable;

    /// <summary>The length of the clip, "0:05.00"; empty until it is read.</summary>
    public string DurationText => Probe is { } probe ? PanelFormat.Time(probe.Duration) : "";

    partial void OnProbeChanged(MediaProbe? value) => OnPropertyChanged(nameof(DurationText));
}

/// <summary>One size pill of the join: "Like the first", "1080p" or "720p".</summary>
public sealed partial class JoinSizeOption(JoinSize size, string label) : ObservableObject
{
    public JoinSize Size { get; } = size;

    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected;
}
