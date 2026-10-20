// Mandarina: FFmpeg for the tools that open a panel. It is found the way the conversion engine finds it (a copy the
// user chose in Settings → Engines, Mandarina's own, one already on the PC), run with live progress, and asked for what
// the panels draw: a file's length and streams, a waveform, a still frame, a few seconds of audio to listen to.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Panels;

/// <summary>What a panel needs to know about a media file.</summary>
/// <param name="Duration">Seconds; 0 when the file doesn't say.</param>
/// <param name="Width">Width of the first video stream as it is shown (a phone video turned 90° is taller than wide); 0 without video.</param>
/// <param name="Height">Height as shown.</param>
/// <param name="FrameRate">Frames per second of the first video stream; 0 without video or when unknown.</param>
/// <param name="Channels">Channels of the first audio stream; 0 without audio.</param>
/// <param name="Tags">The container's tags (title, artist, album, date, comment, …), keys lower-case.</param>
/// <param name="AudioCodec">FFmpeg's name for the first audio stream's codec ("aac", "pcm_s24le", …); null without audio.</param>
/// <param name="VideoCodec">FFmpeg's name for the first video stream's codec ("h264", "hevc", …); null without video.</param>
public sealed record MediaProbe(double Duration, bool HasVideo, bool HasAudio, int Width, int Height, double FrameRate,
    int Channels, int SampleRate, IReadOnlyDictionary<string, string> Tags, string? AudioCodec = null, string? VideoCodec = null);

public sealed class MediaTools
{
    /// <summary>Sample rate of the waveform decode: plenty to draw it, and 16 kB/s keeps an hour of audio under 60 MB.</summary>
    private const int PeakRate = 8000;

    private MediaTools(string ffmpeg, string ffprobe)
    {
        Ffmpeg = ffmpeg;
        Ffprobe = ffprobe;
    }

    public string Ffmpeg { get; }

    public string Ffprobe { get; }

    /// <summary>FFmpeg and ffprobe, or null when FFmpeg is not installed (the petals that need it are greyed out).</summary>
    public static MediaTools? Locate()
    {
        var folder = EngineEnvironment.OwnCopyFolder("ffmpeg")
            ?? (EngineEnvironment.FindBundled("ffmpeg") is { } bundled ? EngineEnvironment.FolderWithPrograms("ffmpeg", bundled) : null)
            ?? EngineEnvironment.SystemCopyFolder("ffmpeg");
        return folder is null
            ? null
            : new MediaTools(Path.Combine(folder, EngineEnvironment.ProgramName("ffmpeg.exe")),
                Path.Combine(folder, EngineEnvironment.ProgramName("ffprobe.exe")));
    }

    /// <summary>Seconds as FFmpeg reads them ("12.5"), whatever the culture.</summary>
    public static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A path inside an FFmpeg filter argument (<c>movie=</c>, <c>subtitles=</c>, <c>drawtext</c> font files): slashes
    /// forward and the characters the filter syntax uses escaped.
    /// </summary>
    public static string FilterPath(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    /// <summary>Reads the file's streams and tags with ffprobe.</summary>
    public async Task<MediaProbe> ProbeAsync(string file, CancellationToken cancel = default)
    {
        var result = await ProcessRunner.RunAsync(Ffprobe,
            ["-hide_banner", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file],
            TimeSpan.FromMinutes(1), cancel);
        if (result.ExitCode != 0)
            throw Failure(result.StandardError, "ffprobe cannot read this file.");
        return ParseProbe(result.StandardOutput);
    }

    internal static MediaProbe ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int width = 0, height = 0, channels = 0, rate = 0;
        double fps = 0, longest = 0;
        bool video = false, audio = false;
        string? audioCodec = null, videoCodec = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                longest = Math.Max(longest, Number(stream, "duration"));
                var attached = stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "attached_pic") == 1;
                switch (Text(stream, "codec_type"))
                {
                    case "video" when !video && !attached:
                        video = true;
                        videoCodec = Text(stream, "codec_name");
                        width = (int)Number(stream, "width");
                        height = (int)Number(stream, "height");
                        if (Math.Abs(Rotation(stream) % 180) == 90)
                            (width, height) = (height, width);
                        fps = Ratio(Text(stream, "avg_frame_rate")) is > 0 and < 1000 and var average ? average : Ratio(Text(stream, "r_frame_rate"));
                        break;
                    case "audio" when !audio:
                        audio = true;
                        audioCodec = Text(stream, "codec_name");
                        channels = (int)Number(stream, "channels");
                        rate = (int)Number(stream, "sample_rate");
                        break;
                }
            }
        }

        var duration = root.TryGetProperty("format", out var format) ? Number(format, "duration") : 0;
        if (duration <= 0)
            duration = longest;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("format", out format) && format.TryGetProperty("tags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Object)
            foreach (var tag in tagElement.EnumerateObject())
                if (tag.Value.ValueKind == JsonValueKind.String)
                    tags[tag.Name.ToLowerInvariant()] = tag.Value.GetString() ?? "";
        return new MediaProbe(duration, video, audio, width, height, fps, channels, rate, tags, audioCodec, videoCodec);
    }

    /// <summary>
    /// Runs ffmpeg with <paramref name="arguments"/> (the output file included; <c>-y</c> is already set) and waits.
    /// Reports 0..1 to <paramref name="progress"/> from the output position when <paramref name="durationSeconds"/> (the
    /// length of the result) is known. Throws <see cref="PanelException"/> with FFmpeg's own message when it fails.
    /// </summary>
    public async Task RunAsync(IEnumerable<string> arguments, double durationSeconds = 0, IProgress<double>? progress = null,
        CancellationToken cancel = default, string? workingDirectory = null)
    {
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-nostats", "-y"];
        args.AddRange(arguments);
        var result = await ProcessRunner.RunAsync(Ffmpeg, args, TimeSpan.FromHours(6), cancel, workingDirectory, onOutput: line =>
        {
            if (durationSeconds > 0 && progress is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro) && micro >= 0)
                progress.Report(Math.Clamp(micro / 1_000_000.0 / durationSeconds, 0, 1));
        });
        if (result.ExitCode != 0)
            throw Failure(result.StandardError, "FFmpeg failed.");
    }

    /// <summary>
    /// The loudness of the first audio stream over time, for a waveform: <paramref name="buckets"/> values 0..1 (the
    /// largest sample in each slice of the file, relative to full scale).
    /// </summary>
    public async Task<float[]> PeaksAsync(string file, double duration, int buckets, CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo(Ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Ffmpeg) ?? "",
        };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", file, "-vn", "-ac", "1", "-ar", PeakRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-" })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new PanelException("FFmpeg did not start.");
        var errors = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            var perBucket = duration > 0 ? Math.Max(1, (long)Math.Ceiling(duration * PeakRate / buckets)) : PeakRate / 50;
            var peaks = new List<float>(buckets + 1);
            var buffer = new byte[1 << 16];
            var held = 0;                       // a sample can straddle two reads
            long inBucket = 0;
            var top = 0;
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(held, buffer.Length - held), cancel)) > 0)
            {
                var bytes = held + read;
                var samples = bytes / 2;
                for (var i = 0; i < samples; i++)
                {
                    var value = Math.Abs((int)BitConverter.ToInt16(buffer, i * 2));
                    top = Math.Max(top, value);
                    if (++inBucket == perBucket)
                    {
                        peaks.Add(top / 32768f);
                        top = 0;
                        inBucket = 0;
                    }
                }
                held = bytes - samples * 2;
                if (held > 0)
                    buffer[0] = buffer[bytes - 1];
            }
            if (inBucket > 0)
                peaks.Add(top / 32768f);
            await process.WaitForExitAsync(cancel);
            if (process.ExitCode != 0 && peaks.Count == 0)
                throw Failure(await errors, "FFmpeg cannot read the audio of this file.");
            return [.. peaks];
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>One video frame at <paramref name="seconds"/>, no wider than <paramref name="maxWidth"/> pixels (0 = as it is).</summary>
    public async Task<Bitmap> FrameAsync(string file, double seconds, int maxWidth = 960, CancellationToken cancel = default)
    {
        var temp = OutputPaths.Temp(".jpg");
        try
        {
            List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-ss", Seconds(seconds), "-i", file, "-an", "-frames:v", "1"];
            if (maxWidth > 0)
                args.AddRange(["-vf", $"scale='min({maxWidth.ToString(CultureInfo.InvariantCulture)},iw)':-2"]);
            args.AddRange(["-q:v", "3", "-y", temp]);
            var result = await ProcessRunner.RunAsync(Ffmpeg, args, TimeSpan.FromMinutes(2), cancel);
            if (result.ExitCode != 0 || !File.Exists(temp))
                throw Failure(result.StandardError, "FFmpeg could not read a frame there.");
            await using var stream = File.OpenRead(temp);
            return new Bitmap(stream);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// A WAV of <paramref name="start"/>..<paramref name="end"/> seconds to listen to (mono, 22 kHz, at most five minutes)
    /// for <c>IPlatformServices.PlaySound</c>.
    /// </summary>
    public async Task<byte[]> PreviewWavAsync(string file, double start, double end, CancellationToken cancel = default)
    {
        var temp = OutputPaths.Temp(".wav");
        try
        {
            var length = Math.Clamp(end - start, 0.05, 300);
            await RunAsync(["-ss", Seconds(start), "-t", Seconds(length), "-i", file, "-vn", "-ac", "1", "-ar", "22050", "-c:a", "pcm_s16le", temp],
                cancel: cancel);
            return await File.ReadAllBytesAsync(temp, cancel);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>The last distinct lines of FFmpeg's stderr without its "[aac @ 000001]" prefixes; <paramref name="fallback"/> when empty.</summary>
    public static string Summarize(string stderr, string fallback)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripPrefix)
            .Where(line => line.Length > 0)
            .Distinct()
            .TakeLast(3);
        var text = string.Join(" ", lines);
        return text.Length > 0 ? text : fallback;
    }

    /// <summary>
    /// What a failed FFmpeg or ffprobe run throws: <see cref="Summarize"/>'s short English as the message, the cause (see
    /// <see cref="PanelErrors"/>) as a key the panel says in the user's language, and FFmpeg's own text for the log.
    /// </summary>
    internal static PanelException Failure(string stderr, string fallback) => PanelErrors.FromEngine(stderr, Summarize(stderr, fallback), fallback);

    private static string StripPrefix(string line)
    {
        while (line.StartsWith('[') && line.IndexOf("] ", StringComparison.Ordinal) is var end and > 0)
            line = line[(end + 2)..].TrimStart();
        return line;
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>ffprobe writes most numbers as strings ("2.000000"), some as JSON numbers.</summary>
    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        return value.ValueKind == JsonValueKind.String
               && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
               && double.IsFinite(number)
            ? number
            : 0;
    }

    /// <summary>"30000/1001" → 29.97; 0 for missing or invalid values.</summary>
    private static double Ratio(string? text)
    {
        var parts = text?.Split('/');
        return parts is [var n, var d]
               && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
               && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
               && den > 0
            ? num / den
            : 0;
    }

    /// <summary>The picture's turn from the display matrix (phones record portrait video this way) or the old "rotate" tag.</summary>
    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            foreach (var item in sideData.EnumerateArray())
                if (item.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(rotation.GetDouble());
        return stream.TryGetProperty("tags", out var tags) && int.TryParse(Text(tags, "rotate"), out var rotate) ? rotate : 0;
    }
}
