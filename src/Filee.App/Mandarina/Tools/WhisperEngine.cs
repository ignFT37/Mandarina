// Mandarina: speech to text with the `whisper` filter of FFmpeg 9 (whisper.cpp inside FFmpeg) and the models of WhisperModel.
// The filter listens to the audio as it flows through it and writes SRT; the engine reads that back as cues (Subtitles).
// It runs on the graphics card when there is a dedicated one (Vulkan, about 18 times faster than real time on a laptop GPU with
// 4 GB) and on the processor otherwise. Integrated graphics are not used: on one AMD integrated GPU the model ran at a
// quarter of the speed and wrote garbage. The model follows the device: the large one on a card, the small one on the processor
// (WhisperModels.For). Nothing leaves the PC. (The voice-activity model that whisper.cpp offers to cut the
// audio at its pauses was left out: in this FFmpeg build it made a nine-minute file 23 times slower, 11 minutes instead of 30
// seconds.)

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.App.Mandarina.Panels;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Mandarina.Tools;

/// <summary>Where Whisper runs: the Vulkan graphics card with this number, or (<paramref name="Gpu"/> null) the processor.</summary>
public sealed record WhisperDevice(int? Gpu, string Name)
{
    /// <summary>The processor: no card (or the user asked not to use it).</summary>
    public static WhisperDevice Processor { get; } = new(null, "CPU");
}

/// <summary>The FFmpeg in use has no <c>whisper</c> filter (a build without it, or an old one).</summary>
public sealed class WhisperFilterMissingException() : Exception("This FFmpeg has no whisper filter.");

public sealed partial class WhisperEngine
{
    /// <summary>
    /// Seconds of audio the filter collects before it gives them to the model. Longer is better and faster: the model cuts
    /// the text where the speech pauses, and keeps what it has read as context. With 3 s (the filter's default) words were cut
    /// in two at every seam and one was repeated; 25 s still cut "paseo" into "pase" and "o"; with 120 s a nine-minute file
    /// comes out as the speaker said it, in 20 s instead of 30 (laptop GPU). The small model on the processor says the same:
    /// three minutes of audio took 96 s with 30 s, 87 s with 60 s and 76 s with 120 s, and the 30 s text had twice the mistakes.
    /// </summary>
    internal const int QueueSeconds = 120;

    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static WhisperDevice? _device;

    private readonly MediaTools _media;
    private readonly WhisperModels _models;
    private readonly ILogger _log;

    private WhisperEngine(MediaTools media, WhisperModels models, ILogger log)
    {
        _media = media;
        _models = models;
        _log = log;
    }

    /// <summary>The engine, or null while FFmpeg or every speech model is missing.</summary>
    public static WhisperEngine? Locate(MediaTools? media, ILogger? log = null) =>
        media is not null && WhisperModel.Installed() is { Any: true } models ? For(media, models, log) : null;

    /// <summary>The engine for these models (at least one of them is a file).</summary>
    internal static WhisperEngine For(MediaTools media, WhisperModels models, ILogger? log = null) =>
        models.Any ? new(media, models, log ?? NullLogger.Instance) : throw new ArgumentException("No speech model.", nameof(models));

    /// <summary>
    /// The model <paramref name="device"/> runs: the large one on a dedicated graphics card, the small one on the processor
    /// (the other one when only that is installed).
    /// </summary>
    public WhisperModelFile ModelFor(WhisperDevice device) => _models.For(dedicatedGpu: device.Gpu is not null)!;

    /// <summary>
    /// The folder FFmpeg runs in and the name the model goes by in the graph. whisper.cpp cannot open a model whose path has a
    /// letter outside ASCII ("failed to open …/ñandú/modelo.bin": a Windows user called José or Nuño has them in the profile
    /// folder the model is kept in), so the program starts in the model's folder and is given the file name alone.
    /// </summary>
    private static (string Folder, string Name) Location(string model) => (Path.GetDirectoryName(model)!, Path.GetFileName(model));

    [GeneratedRegex(@"ggml_vulkan: (?<index>\d+) = (?<name>.+?) \([^)]*\) \| uma: (?<uma>\d)")]
    private static partial Regex VulkanDevice();

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageCode();

    /// <summary>
    /// The device to use, from the list of Vulkan devices ggml prints when the filter starts: the first graphics card that has
    /// memory of its own. None (an integrated GPU only, or no Vulkan at all) means the processor.
    /// </summary>
    internal static WhisperDevice ChooseDevice(string log)
    {
        foreach (Match device in VulkanDevice().Matches(log))
        {
            if (device.Groups["uma"].Value == "0")
                return new WhisperDevice(int.Parse(device.Groups["index"].Value, CultureInfo.InvariantCulture), device.Groups["name"].Value.Trim());
        }
        return WhisperDevice.Processor;
    }

    /// <summary>Starts a model for a moment to see which devices it finds. Once per run of the program.</summary>
    public async Task<WhisperDevice> DeviceAsync(CancellationToken cancel = default)
    {
        if (_device is { } known)
            return known;
        await ProbeGate.WaitAsync(cancel);
        try
        {
            if (_device is { } again)
                return again;
            var (folder, name) = Location(_models.ForProbe!);
            string[] args = ["-hide_banner", "-nostdin", "-v", "trace", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.2",
                "-af", $"whisper=model={Quote(name)}:use_gpu=true:gpu_device=0:queue=1:format=text", "-f", "null", "-"];
            var result = await ProcessRunner.RunAsync(_media.Ffmpeg, args, TimeSpan.FromMinutes(2), cancel, folder);
            if (result.ExitCode != 0 && result.StandardError.Contains("No such filter", StringComparison.Ordinal))
                throw new WhisperFilterMissingException();
            var device = ChooseDevice(result.StandardError);
            _log.LogInformation("Whisper runs on {Device}", device.Name);
            return _device = device;
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    /// <summary>
    /// The words of <paramref name="source"/>'s first audio stream, as cues. <paramref name="language"/> is Whisper's code
    /// ("es") or null to detect it; <paramref name="useGpu"/> false forces the processor. Progress is 0..1 of the audio.
    /// </summary>
    public async Task<List<Cue>> TranscribeAsync(string source, double durationSeconds, string? language, bool useGpu,
        IProgress<double>? progress, CancellationToken cancel)
    {
        var device = useGpu ? await DeviceAsync(cancel) : WhisperDevice.Processor;
        var model = ModelFor(device);
        _log.LogInformation("Whisper uses the {Size} model on {Device}", model.Size, device.Name);
        try
        {
            return await RunAsync(model.Path, source, durationSeconds, language, device.Gpu, progress, cancel);
        }
        catch (PanelException ex) when (device.Gpu is not null)
        {
            // A card that cannot run the model (driver, memory) must not stop the tool: the processor does it, only slower
            // (and with the model that suits it).
            _log.LogWarning(ex, "Whisper failed on {Device}; trying the processor", device.Name);
            return await RunAsync(ModelFor(WhisperDevice.Processor).Path, source, durationSeconds, language, null, progress, cancel);
        }
    }

    private async Task<List<Cue>> RunAsync(string model, string source, double durationSeconds, string? language, int? gpu,
        IProgress<double>? progress, CancellationToken cancel)
    {
        var destination = OutputPaths.Temp(".srt");
        var graphFile = OutputPaths.Temp(".txt");
        using var watching = new CancellationTokenSource();
        Task? watcher = null;
        try
        {
            var (folder, name) = Location(model);
            File.WriteAllText(graphFile, Graph(name, destination, language, gpu, QueueSeconds)); // UTF-8 without a BOM: a user name with an accent is in the paths
            // FFmpeg's own progress is how much audio has gone INTO the filter, which is ahead of the work by a whole queue (and
            // done at once for a short file): the progress is how far the written subtitles have got instead.
            if (progress is not null && durationSeconds > 0)
                watcher = Task.Run(() => WatchAsync(destination, durationSeconds, progress, watching.Token));
            await _media.RunAsync(Arguments(source, graphFile, gpu, ProcessorCores.Count), cancel: cancel, workingDirectory: folder);
            return File.Exists(destination) ? Subtitles.Tidy(Subtitles.Parse(File.ReadAllText(destination, Encoding.UTF8))) : [];
        }
        finally
        {
            await watching.CancelAsync();
            if (watcher is not null)
                await watcher;
            MediaTools.TryDelete(destination);
            MediaTools.TryDelete(graphFile);
        }
    }

    /// <summary>
    /// FFmpeg's arguments: the first audio stream of <paramref name="source"/> through the graph in <paramref name="graphFile"/>, and
    /// nowhere else (the filter writes the subtitles itself). On the processor (<paramref name="gpu"/> null) the filter gets one
    /// thread per physical core, <paramref name="cores"/>, instead of one per logical processor: with two threads per core the
    /// second only waits for the first. Three minutes of audio with the small model took 46 s with 8 threads on 8 cores and 64 s
    /// with the 16 FFmpeg starts by default (the large model gains nothing: 175 s against 183 s).
    /// </summary>
    internal static List<string> Arguments(string source, string graphFile, int? gpu, int cores)
    {
        List<string> arguments = [];
        if (gpu is null)
            arguments.AddRange(["-filter_threads", cores.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", source, "-vn", "-map", "0:a:0", "-/af", graphFile, "-f", "null", "-"]);
        return arguments;
    }

    /// <summary>Reports how far into the audio the subtitles written so far reach, twice a second, until cancelled.</summary>
    private static async Task WatchAsync(string destination, double durationSeconds, IProgress<double> progress, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, stop);
                var reached = WrittenSoFar(destination);
                if (reached > 0)
                    progress.Report(Math.Min(0.99, reached / durationSeconds));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Seconds of audio the SRT FFmpeg is writing covers (0 when there is nothing yet or it cannot be read).</summary>
    internal static double WrittenSoFar(string srt)
    {
        try
        {
            using var stream = new FileStream(srt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Subtitles.Parse(reader.ReadToEnd()) is { Count: > 0 } cues ? cues.Max(cue => cue.End).TotalSeconds : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The filter graph FFmpeg reads from a file (<c>-/af</c>): the model, the language, the queue and where the SRT goes.</summary>
    internal static string Graph(string model, string destination, string? language, int? gpu, int queue)
    {
        var code = string.IsNullOrEmpty(language) ? "auto" : language;
        if (code != "auto" && !LanguageCode().IsMatch(code))
            throw new ArgumentException("Not a language code: " + code, nameof(language));
        var graph = new StringBuilder("whisper=model=").Append(Quote(model))
            .Append(":language=").Append(code)
            .Append(":queue=").Append(queue.ToString(CultureInfo.InvariantCulture))
            .Append(":use_gpu=").Append(gpu is null ? "false" : "true");
        if (gpu is { } device)
            graph.Append(":gpu_device=").Append(device.ToString(CultureInfo.InvariantCulture));
        return graph.Append(":format=srt:destination=").Append(Quote(destination)).ToString();
    }

    /// <summary>
    /// A path as one value of a filter option: slashes forward, the characters of the option syntax (backslash, colon, quote)
    /// escaped and the whole in single quotes for the filter graph (FFmpeg's "filtergraph escaping", two levels).
    /// </summary>
    internal static string Quote(string path)
    {
        var option = path.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        return "'" + option.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
