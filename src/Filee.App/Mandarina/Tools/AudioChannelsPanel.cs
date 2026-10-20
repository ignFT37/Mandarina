// Mandarina: CHANNELS for audio: make a file mono or stereo and set the level of its left and right sides (0 to 200 %).
// Apply runs FFmpeg's pan filter, writes the result in the file's own format (the tags stay) and saves "name mono.ext" or
// "name stereo.ext" beside the original. The filter itself is built in ChannelsMix.

using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("audioChannels", NeedsFfmpeg = true)]
public sealed partial class AudioChannelsPanel : ToolPanel
{
    private readonly CancellationTokenSource _loading = new();

    /// <summary>The channels the file has now; 0 until it has been read.</summary>
    [ObservableProperty] private int _sourceChannels;

    /// <summary>The result is mono (otherwise stereo).</summary>
    [ObservableProperty] private bool _toMono = true;

    /// <summary>Level of the left side, in percent (100 = as it is).</summary>
    [ObservableProperty] private double _left = 100;

    /// <summary>Level of the right side, in percent.</summary>
    [ObservableProperty] private double _right = 100;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;

    public AudioChannelsPanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(560, 396);

    public string FileName => Path.GetFileName(Files[0]);

    public bool IsStereo => !ToMono;

    public bool HasSource => SourceChannels > 0;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    /// <summary>What the file is now: "Now: stereo".</summary>
    public string SourceText => IsLoading
        ? Context["panel.audioChannels.loading"]
        : SourceChannels switch
        {
            0 => "",
            1 => Context["panel.audioChannels.now_mono"],
            2 => Context["panel.audioChannels.now_stereo"],
            _ => Context.Format("panel.audioChannels.now_multi", SourceChannels),
        };

    /// <summary>A mono file made mono again: only its level can change (said in the accent colour).</summary>
    public bool IsNotice => SourceChannels == 1 && ToMono;

    /// <summary>One line saying what comes out: "Stereo → mono: both channels are mixed together".</summary>
    public string ResultText => SourceChannels switch
    {
        0 => "",
        1 when ToMono => Context["panel.audioChannels.already_mono"],
        1 => Context["panel.audioChannels.result_mono_to_stereo"],
        2 when ToMono => Context["panel.audioChannels.result_stereo_to_mono"],
        2 => Context["panel.audioChannels.result_stereo"],
        _ when ToMono => Context.Format("panel.audioChannels.result_multi_to_mono", SourceChannels),
        _ => Context.Format("panel.audioChannels.result_multi_to_stereo", SourceChannels),
    };

    private int OutputChannels => ToMono ? 1 : 2;

    /// <summary>Something has to change: the number of channels or the level of a side.</summary>
    public override bool CanApply => HasSource && !HasLoadError
        && (OutputChannels != SourceChannels || Math.Abs(Left - 100) > 0.001 || Math.Abs(Right - 100) > 0.001);

    public override Control CreateView() => new AudioChannelsView { DataContext = this };

    [RelayCommand]
    private void SetMono() => ToMono = true;

    [RelayCommand]
    private void SetStereo() => ToMono = false;

    private async Task LoadAsync()
    {
        try
        {
            var probe = await Context.RequireMedia().ProbeAsync(Files[0], _loading.Token);
            if (!probe.HasAudio || probe.Channels < 1)
                throw new PanelException(Context["panel.audioChannels.no_audio"]);
            SourceChannels = probe.Channels;
            // The other layout as a start: stereo becomes mono; mono (and surround) becomes stereo.
            ToMono = probe.Channels == 2;
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
    }

    partial void OnSourceChannelsChanged(int value) => Refresh();

    partial void OnToMonoChanged(bool value) => Refresh();

    partial void OnIsLoadingChanged(bool value) => Refresh();

    partial void OnLoadErrorChanged(string? value) => Refresh();

    // A slider drags through fractions; the percentage shown is a whole number, so the level used is the one shown.
    partial void OnLeftChanged(double value)
    {
        if (Math.Round(value) != value)
            Left = Math.Round(value);
        else
            Refresh();
    }

    partial void OnRightChanged(double value)
    {
        if (Math.Round(value) != value)
            Right = Math.Round(value);
        else
            Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsStereo));
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(IsNotice));
        OnPropertyChanged(nameof(ResultText));
        NotifyCanApplyChanged();
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        // The options as they are now: the window stays editable while this runs.
        var toMono = ToMono;
        var (left, right) = (Left / 100, Right / 100);
        var media = Context.RequireMedia();
        var source = Files[0];
        var probe = await media.ProbeAsync(source, cancel);
        if (!probe.HasAudio)
            throw new PanelException(Context["panel.audioChannels.no_audio"]);

        var suffix = Context[toMono ? "wheel.audioChannels.suffix" : "panel.audioChannels.suffix_stereo"];
        var output = OutputPaths.Beside(source, suffix);

        // The file's own format again at high quality; the tags stay, the cover picture does not.
        List<string> args = ["-i", source, "-vn", "-map", "0:a:0", "-af", ChannelsMix.Filter(probe.Channels, toMono, left, right), "-map_metadata", "0"];
        args.AddRange(await AudioEncoding.ArgumentsAsync(media, source, probe, cancel));
        args.Add(output);
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
        _loading.Cancel();
        base.Dispose();
    }
}
