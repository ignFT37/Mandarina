// Mandarina: the models of the transcription tools. Two packages of Settings → Engines carry one each: "whisper", Whisper
// large-v3-turbo quantized to 5 bits (574 MB), and "whisper-small", Whisper small quantized to 8 bits (264 MB: on a processor
// it is some 10 % faster than the 5-bit one, which is 190 MB, and writes the same text). The large one is the better and, on a
// dedicated graphics card, quick; on the processor it runs only about as fast as real time, and the small one three to four
// times as fast as that. So the tools take the large one for a card and the small one for the processor, and whichever is installed
// when there is only one. A copy next to the program or in the checkout's engines folder is found too (tests, developers), the
// same way the other engines are (EngineEnvironment.FindBundled).

using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

public enum WhisperSize
{
    Large,
    Small,
}

/// <summary>An installed model: where the file is and which of the two it is.</summary>
public sealed record WhisperModelFile(string Path, WhisperSize Size);

/// <summary>The speech models that are installed (null: that one is not).</summary>
public sealed record WhisperModels(string? Large, string? Small)
{
    /// <summary>At least one model is installed: the tools can work, and the petals that need Whisper are not greyed out.</summary>
    public bool Any => Large is not null || Small is not null;

    /// <summary>
    /// The model for a run on a dedicated graphics card (<paramref name="dedicatedGpu"/>) or on the processor: the large one on a
    /// card, the small one on the processor, and the other when the preferred one is not installed. Null when none is.
    /// </summary>
    public WhisperModelFile? For(bool dedicatedGpu)
    {
        var large = Large is null ? null : new WhisperModelFile(Large, WhisperSize.Large);
        var small = Small is null ? null : new WhisperModelFile(Small, WhisperSize.Small);
        return dedicatedGpu ? large ?? small : small ?? large;
    }

    /// <summary>The model to start for a moment to see which devices exist (any will do): the small one loads sooner.</summary>
    public string? ForProbe => Small ?? Large;
}

public static class WhisperModel
{
    public const string LargeFile = "ggml-large-v3-turbo-q5_0.bin";
    public const string SmallFile = "ggml-small-q8_0.bin";

    /// <summary>The models installed now.</summary>
    public static WhisperModels Installed() => Find(EngineEnvironment.FindBundled);

    /// <summary>The models in the folders <paramref name="folderOf"/> gives for the two packages ("whisper", "whisper-small"; null: no folder).</summary>
    internal static WhisperModels Find(Func<string, string?> folderOf) =>
        new(In(folderOf("whisper"), LargeFile), In(folderOf("whisper-small"), SmallFile));

    private static string? In(string? folder, string file) =>
        folder is not null && File.Exists(Path.Combine(folder, file)) ? Path.Combine(folder, file) : null;
}
