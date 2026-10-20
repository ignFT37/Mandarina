// Mandarina: the work the two transcription tools do. For every file: transcribe its audio, save the texts the user asked for
// beside it ("name.txt", "name.srt", "name.vtt": a pair that belongs together keeps its numbers together when the names are
// taken) and, for a video when asked, burn the subtitles into "name subtitled.mp4". The original is never touched; with
// several files it is all or nothing: when one fails, what this run wrote is deleted.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>What a transcription saves.</summary>
/// <param name="Language">Whisper's code ("es"), or null to detect it.</param>
/// <param name="Text">Save "name.txt", the text alone.</param>
/// <param name="Srt">Save "name.srt".</param>
/// <param name="Vtt">Save "name.vtt".</param>
/// <param name="Burn">Burn the subtitles into the picture of a video, in "name subtitled.mp4".</param>
/// <param name="UseGpu">False forces the processor.</param>
public sealed record TranscribeJob(string? Language, bool Text, bool Srt, bool Vtt, bool Burn, SubtitleSize Size, bool UseGpu)
{
    /// <summary>What a click on the petal does: the language is detected, the text and the subtitles are saved.</summary>
    public static TranscribeJob Quick { get; } = new(null, Text: true, Srt: true, Vtt: false, Burn: false, SubtitleSize.Medium, UseGpu: true);

    /// <summary>The job saves something.</summary>
    public bool SavesAnything => Text || Srt || Vtt || Burn;
}

public static class TranscribeRunner
{
    /// <summary>How much of a video's progress is the transcription when the subtitles are burned in afterwards (the rest is the burn).</summary>
    private const double TranscribeShare = 0.6;

    /// <summary>
    /// Runs <paramref name="job"/> on <paramref name="files"/> and returns the files written. <paramref name="progress"/> is
    /// 0..1 of everything; <paramref name="phase"/> says what is going on ("Transcribing «clip.mp4» (2 of 3)…").
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunAsync(PanelContext context, IReadOnlyList<string> files, TranscribeJob job,
        IProgress<double> progress, IProgress<string>? phase, CancellationToken cancel)
    {
        var media = context.RequireMedia();
        var engine = WhisperEngine.Locate(media, context.Log) ?? throw new PanelException(context.Format("engine.reason.needs_package", "Whisper"));
        var written = new List<string>();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var source = files[i];
                var name = Path.GetFileName(source);
                var (from, width) = ((double)i / files.Count, 1.0 / files.Count);
                phase?.Report(Describe(context, "panel.transcribe.transcribing", name, i, files.Count));

                var probe = await media.ProbeAsync(source, cancel);
                if (!probe.HasAudio)
                    throw new PanelException(context.Format("panel.transcribe.no_audio", name));
                var burn = job.Burn && probe.HasVideo;
                var share = burn ? TranscribeShare : 1;
                List<Cue> cues;
                try
                {
                    cues = await engine.TranscribeAsync(source, probe.Duration, job.Language, job.UseGpu,
                        new Progress<double>(p => progress.Report(from + width * share * p)), cancel);
                }
                catch (WhisperFilterMissingException)
                {
                    throw new PanelException(context["panel.transcribe.no_filter"]);
                }
                if (cues.Count == 0)
                    throw new PanelException(context.Format("panel.transcribe.no_speech", name));

                Save(source, job, cues, written);
                if (burn)
                {
                    phase?.Report(Describe(context, "panel.transcribe.burning", name, i, files.Count));
                    var output = context.OutputFor(source, "subtitles", ".mp4");
                    written.Add(output);
                    await SubtitleBurner.BurnAsync(media, source, cues, output, job.Size, probe,
                        new Progress<double>(p => progress.Report(from + width * (share + (1 - share) * p))), cancel);
                }
                progress.Report(from + width);
            }
        }
        catch
        {
            foreach (var path in written)
                MediaTools.TryDelete(path);
            throw;
        }
        return written;
    }

    /// <summary>"Transcribing «clip.mp4»…", and "(2 of 3)" after it when there are several files.</summary>
    private static string Describe(PanelContext context, string key, string name, int index, int count) =>
        count == 1 ? context.Format(key, name) : context.Format(key, name) + " " + context.Format("panel.transcribe.of", index + 1, count);

    /// <summary>Writes the texts the job asks for beside the original, with one name for all of them.</summary>
    private static void Save(string source, TranscribeJob job, List<Cue> cues, List<string> written)
    {
        var formats = new List<(string Extension, Func<string> Content)>();
        if (job.Text)
            formats.Add((".txt", () => Subtitles.ToText(cues)));
        if (job.Srt)
            formats.Add((".srt", () => Subtitles.ToSrt(Subtitles.Fit(cues))));
        if (job.Vtt)
            formats.Add((".vtt", () => Subtitles.ToVtt(Subtitles.Fit(cues))));
        if (formats.Count == 0)
            return;
        var paths = OutputPaths.BesideAll(source, "", [.. formats.Select(format => format.Extension)]);
        for (var i = 0; i < paths.Length; i++)
        {
            written.Add(paths[i]);
            File.WriteAllText(paths[i], formats[i].Content(), Subtitles.FileEncoding);
        }
    }
}
