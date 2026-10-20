// Mandarina: TRANSCRIBE, the one-click tool for audio and video. Its window opens on the petal and starts at once: the language
// is detected and "name.txt" (the words) and "name.srt" (the subtitles) are saved beside the original. The window only shows
// the progress and what was saved; for the choices (language, formats, burning the subtitles in) there is SUBTITLES.

using Avalonia;
using Avalonia.Controls;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

// Not sealed: the render test shows the window of a copy that does not start by itself.
[ToolPanel("transcribe", NeedsFfmpeg = true, NeedsWhisper = true)]
public class TranscribePanel(PanelContext context) : TranscribePanelBase(context)
{
    // Tall enough for the long line about the model (two lines) under a two-line "what it is doing" in the longest languages.
    public override Size PreferredSize => new(520, 360);

    public override bool AutoRun => true;

    public override bool CanApply => HasModel;

    public override Control CreateView() => new TranscribeView { DataContext = this };

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel) =>
        await TranscribeRunner.RunAsync(Context, Files, TranscribeJob.Quick, progress, new Progress<string>(text => Phase = text), cancel);
}
