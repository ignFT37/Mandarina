// Mandarina: SPEED for video: one clip played faster or slower. Apply re-encodes the picture at the speed chosen
// and stretches the sound to match with its pitch kept, then saves "name speed.ext" beside the original. The pills give the
// usual speeds; the slider reaches any speed in between, in steps of 0.05.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("changeVideoSpeed", NeedsFfmpeg = true)]
public sealed partial class ChangeVideoSpeedPanel : ToolPanel
{
    /// <summary>The pills, in order.</summary>
    private static readonly double[] PresetSpeeds = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>The slider moves in steps of this much (the speed it gives is always on one of them).</summary>
    private const double Step = 0.05;

    private readonly CancellationTokenSource _loading = new();

    [ObservableProperty] private double _speed = 1;

    [ObservableProperty] private double _duration;

    [ObservableProperty] private bool _hasAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private string? _loadError;

    public ChangeVideoSpeedPanel(PanelContext context) : base(context)
    {
        Presets = [.. PresetSpeeds.Select(speed => new SpeedPreset(speed, speed.ToString("0.##", CultureInfo.CurrentCulture) + "×"))];
        MarkPreset(Speed);
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(600, 360);

    /// <summary>The pills: one per usual speed, the one that matches the speed on.</summary>
    public IReadOnlyList<SpeedPreset> Presets { get; }

    public string FileName => Path.GetFileName(Files[0]);

    /// <summary>The length before and after the change: "Duración: 0:06.00 → 0:03.00".</summary>
    public string DurationText => Duration > 0
        ? Context.Format("panel.changeVideoSpeed.length", PanelFormat.Time(Duration), PanelFormat.Time(Duration / Speed))
        : "";

    /// <summary>The controls are usable once the video is read.</summary>
    public bool IsReady => !IsLoading && LoadError is null;

    public override bool CanApply => Duration > 0 && SpeedFilter.Changes(Speed);

    public override Control CreateView() => new ChangeVideoSpeedView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var probe = await Context.RequireMedia().ProbeAsync(Files[0], _loading.Token);
            if (!probe.HasVideo)
                throw new PanelException(Context["panel.changeVideoSpeed.no_video"]);
            if (probe.Duration <= 0)
                throw new PanelException(Context["panel.changeVideoSpeed.no_length"]);
            HasAudio = probe.HasAudio;
            Duration = probe.Duration;
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

    partial void OnSpeedChanged(double value)
    {
        // The slider moves freely; the speed kept is the nearest step, and never beyond the range.
        var snapped = Math.Clamp(Math.Round(value / Step) * Step, SpeedFilter.Slowest, SpeedFilter.Fastest);
        if (Math.Abs(snapped - value) > 1e-9)
        {
            Speed = snapped; // raises this again, with the step
            return;
        }
        MarkPreset(value);
        OnPropertyChanged(nameof(DurationText));
        NotifyCanApplyChanged();
    }

    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        NotifyCanApplyChanged();
    }

    /// <summary>A pill was pressed: the slider goes to its speed.</summary>
    public void SelectSpeed(SpeedPreset preset) => Speed = preset.Value;

    private void MarkPreset(double speed)
    {
        foreach (var preset in Presets)
            preset.IsSelected = Math.Abs(preset.Value - speed) < 1e-9;
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var media = Context.RequireMedia();
        var source = Files[0];
        var speed = Speed;
        var output = Context.OutputFor(source, "changeVideoSpeed");
        var probe = await media.ProbeAsync(source, cancel);

        // The picture is re-encoded to keep it sharp (CRF 18); the sound is re-encoded too, since it has to be stretched.
        List<string> args = ["-i", source, .. SpeedFilter.Build(speed, probe.HasAudio), "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"];
        if (probe.HasAudio)
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        // Only MP4-family containers take this option (FFmpeg refuses it for the others, e.g. MKV).
        if (Path.GetExtension(source).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov")
            args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);
        try
        {
            await media.RunAsync(args, probe.Duration / speed, progress, cancel);
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
        _loading.Cancel();
        base.Dispose();
    }
}

/// <summary>One speed pill of the panel ("1.5×"), on when the speed is its own.</summary>
public sealed partial class SpeedPreset(double value, string label) : ObservableObject
{
    public double Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected;
}
