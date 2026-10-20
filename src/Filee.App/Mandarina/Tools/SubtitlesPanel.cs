// Mandarina: SUBTITLES, the transcription tool with choices: the language (or detect it), which files to save ("name.txt" the
// words, "name.srt" and "name.vtt" the subtitles) and, for a video, burning the subtitles into the picture ("name subtitled.mp4").
// The work is TranscribeRunner's; the quick version, one click for the defaults, is TRANSCRIBE.

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Mandarina.Panels;
using Microsoft.Extensions.Logging;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("subtitles", NeedsFfmpeg = true, NeedsWhisper = true)]
public sealed partial class SubtitlesPanel : TranscribePanelBase
{
    private readonly CancellationTokenSource _probing = new();

    /// <summary>The language of the speech; the first entry of <see cref="Languages"/> (code "") means "detect it".</summary>
    [ObservableProperty] private WhisperLanguage _selectedLanguage;

    [ObservableProperty] private bool _saveText = true;
    [ObservableProperty] private bool _saveSrt = true;
    [ObservableProperty] private bool _saveVtt;

    /// <summary>Burn the subtitles into the video (only for files that have a picture).</summary>
    [ObservableProperty] private bool _burn;

    [ObservableProperty] private SubtitleSize _size = SubtitleSize.Medium;

    /// <summary>Use the graphics card when there is a dedicated one (a switch for the case the card misbehaves).</summary>
    [ObservableProperty] private bool _useGpu = true;

    /// <summary>Every file has a picture: the subtitles can be burned in.</summary>
    [ObservableProperty] private bool _hasVideo;

    public SubtitlesPanel(PanelContext context) : base(context)
    {
        Languages = [new WhisperLanguage("", Context["panel.subtitles.language_auto"]), .. WhisperLanguages.All];
        _selectedLanguage = Languages[0];
        _ = ProbeAsync();
    }

    public override Size PreferredSize => new(600, 520);

    public ReadOnlyCollection<WhisperLanguage> Languages { get; }

    /// <summary>The burn-in choices are shown: the files are videos and burning is on.</summary>
    public bool ShowSize => HasVideo && Burn;

    public bool IsSmall => Size == SubtitleSize.Small;

    public bool IsMedium => Size == SubtitleSize.Medium;

    public bool IsLarge => Size == SubtitleSize.Large;

    public override bool CanApply => HasModel && (SaveText || SaveSrt || SaveVtt || (Burn && HasVideo));

    protected override bool WantsGpu => UseGpu;

    public override Control CreateView() => new SubtitlesView { DataContext = this };

    // Without the card the small model runs, when it is installed: the line says which one and where.
    partial void OnUseGpuChanged(bool value) => ShowDevice();

    partial void OnSaveTextChanged(bool value) => NotifyCanApplyChanged();

    partial void OnSaveSrtChanged(bool value) => NotifyCanApplyChanged();

    partial void OnSaveVttChanged(bool value) => NotifyCanApplyChanged();

    partial void OnBurnChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSize));
        NotifyCanApplyChanged();
    }

    partial void OnHasVideoChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSize));
        NotifyCanApplyChanged();
    }

    partial void OnSizeChanged(SubtitleSize value)
    {
        OnPropertyChanged(nameof(IsSmall));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsLarge));
    }

    [RelayCommand]
    private void SetSmall() => Size = SubtitleSize.Small;

    [RelayCommand]
    private void SetMedium() => Size = SubtitleSize.Medium;

    [RelayCommand]
    private void SetLarge() => Size = SubtitleSize.Large;

    /// <summary>Looks at the files: only videos can have the subtitles burned in.</summary>
    private async Task ProbeAsync()
    {
        try
        {
            if (Context.Media is not { } media)
                return;
            var all = true;
            foreach (var file in Files)
                all &= (await media.ProbeAsync(file, _probing.Token)).HasVideo;
            HasVideo = all;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget: without the answer the burn-in choice simply stays hidden.
            Context.Log.LogWarning(ex, "Could not look at the files of the subtitles panel");
        }
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        // The options as they are now: the window stays editable while this runs.
        var job = new TranscribeJob(string.IsNullOrEmpty(SelectedLanguage.Code) ? null : SelectedLanguage.Code, SaveText, SaveSrt, SaveVtt,
            Burn && HasVideo, Size, UseGpu);
        return await TranscribeRunner.RunAsync(Context, Files, job, progress, new Progress<string>(text => Phase = text), cancel);
    }

    public override void Dispose()
    {
        _probing.Cancel();
        base.Dispose();
    }
}
