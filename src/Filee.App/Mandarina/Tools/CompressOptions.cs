// Mandarina: what the Compress panel asks of a compressor, whatever the kind of file. The panel (CompressPanel) owns the
// window and the options; each kind of file has a compressor that turns one source into one output:
//   photos  → ImageCompressor (Magick.NET)        PDFs  → PdfCompressor (PdfSharp + Magick.NET)
//   audio   → AudioCompressor (FFmpeg)            video → VideoCompressor (FFmpeg)

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>How the user chose to say how much to compress.</summary>
public enum CompressMode
{
    /// <summary>A named level: Light 85, Balanced 70, Strong 50, Maximum 35 (see <see cref="CompressOptions.Quality"/>).</summary>
    Preset,

    /// <summary>The "quality" slider, 10..100 %.</summary>
    Percent,

    /// <summary>A wanted size: the result should be at or just under <see cref="CompressOptions.TargetBytes"/>.</summary>
    TargetSize,
}

/// <summary>What the Compress panel asks of a compressor for one file.</summary>
/// <param name="Mode">How the quality was chosen. Compressors only need <see cref="Quality"/> and <see cref="TargetBytes"/>.</param>
/// <param name="Quality">1..100, higher keeps more (Light 85, Balanced 70, Strong 50, Maximum 35 or the slider). Used unless Mode is TargetSize.</param>
/// <param name="TargetBytes">The wanted size of the result in bytes when Mode is TargetSize (best effort: at or just under it); 0 otherwise.</param>
/// <param name="MaxSize">Resize: 0 keeps the size; for photos the longer side in pixels, for videos the height in pixels (1080, 720, 480). Never enlarges.</param>
public sealed record CompressOptions(CompressMode Mode, int Quality, long TargetBytes = 0, int MaxSize = 0);

/// <summary>
/// The compressor of audio files (FFmpeg). Lossy formats (mp3, m4a, ogg, opus) are re-encoded at a bitrate taken from
/// <see cref="CompressOptions"/>; lossless ones (wav, aiff, flac) are saved as FLAC, which is lossless too
/// (a WAV becomes a FLAC).
/// </summary>
public static partial class AudioCompressor
{
    /// <summary>The extension the compressed file gets: the source's, except WAV, AIFF and FLAC which become ".flac".</summary>
    public static partial string OutputExtension(string source);

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="output"/> (a free path, with <see cref="OutputExtension"/>).
    /// Reports 0..1; deletes a half-written output when it fails or is cancelled and throws <see cref="PanelException"/>
    /// with a readable message. With TargetSize the result is at or just under the target when the content allows it.
    /// </summary>
    public static partial Task<string> CompressAsync(MediaTools media, string source, string output, CompressOptions options,
        IProgress<double>? progress, CancellationToken cancel);
}

/// <summary>The compressor of video files (FFmpeg, H.264 + AAC, same container as the source).</summary>
public static partial class VideoCompressor
{
    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="output"/> (a free path with the source's extension), H.264 +
    /// AAC, optionally scaled down to <see cref="CompressOptions.MaxSize"/> pixels of height (never up). With TargetSize it
    /// plans the bitrate from the duration (two passes). Same contract as <see cref="AudioCompressor.CompressAsync"/>.
    /// </summary>
    public static partial Task<string> CompressAsync(MediaTools media, string source, string output, CompressOptions options,
        IProgress<double>? progress, CancellationToken cancel);
}

internal static class CompressKinds
{
    public static bool IsAudio(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".wav" or ".flac" or ".aiff" or ".aif" or ".ogg" or ".opus";

    public static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".m4v";

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
}
