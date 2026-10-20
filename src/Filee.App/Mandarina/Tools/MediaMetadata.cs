// Mandarina: reads and writes the tags of audio and video files for the METADATA panel, without recoding anything. MP3 has its own
// driver (MediaMetadataMp3: the ID3 tags are rewritten and the audio frames copied as they are). The other containers go through
// FFmpeg with "-c copy": every stream is copied bit for bit, only the tags, the chapters and the streams that carry no sound or
// picture (GPS and telemetry tracks) change. "-fflags +bitexact" keeps FFmpeg from adding its own name and the time of the writing.

using System.Globalization;
using System.Text.Json;
using Filee.App.Mandarina.Panels;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

internal enum MediaMetadataKind
{
    Audio,
    Video,
}

/// <summary>The fields of the audio and video panels (audio: Title…Comment; video: Title, Author, Comment, Created, Location).</summary>
internal enum MediaMetadataField
{
    Title,
    Artist,
    Album,
    AlbumArtist,
    Genre,
    Date,
    Track,
    Comment,
    Author,
    Created,
    Location,
}

/// <summary>What the panel needs to know about one audio or video file.</summary>
/// <param name="HasCover">A cover picture is stored as a stream or an attachment (the panel offers to remove it).</param>
/// <param name="DroppedStreams">Streams that carry neither sound, picture nor subtitles (GPS, telemetry, timecode): they are not copied.</param>
/// <param name="LosesCustomTags">Editing will not keep every tag (Apple's own keys, frames the tool does not know).</param>
internal sealed record MediaMetadataInfo(MediaMetadataKind Kind, IReadOnlyDictionary<MediaMetadataField, string> Values, bool HasCover, bool HasChapters, int DroppedStreams, bool LosesCustomTags);

internal static class MediaMetadata
{
    public static MediaMetadataKind KindOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".m4v" ? MediaMetadataKind.Video : MediaMetadataKind.Audio;

    /// <summary>True when the file needs FFmpeg (everything but MP3).</summary>
    public static bool NeedsFfmpeg(string path) => !MediaMetadataMp3.Handles(path);

    // ───────── Probing ─────────

    private sealed record StreamInfo(int Index, string Type, string? Codec, bool AttachedPicture, IReadOnlyDictionary<string, string> Tags);

    private sealed record Probe(Dictionary<string, string> Tags, List<StreamInfo> Streams, int Chapters, double Duration)
    {
        public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : Streams.FirstOrDefault(s => s.Type == "audio" && s.Tags.ContainsKey(key))?.Tags[key];

        public bool HasTag(string key) => Tag(key) is not null;
    }

    private static async Task<Probe> ProbeAsync(MediaTools media, string path, CancellationToken cancel)
    {
        var result = await ProcessRunner.RunAsync(media.Ffprobe,
            ["-hide_banner", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path], TimeSpan.FromMinutes(2), cancel);
        if (result.ExitCode != 0)
            throw MediaTools.Failure(result.StandardError, "ffprobe cannot read this file.");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        double duration = 0;
        if (root.TryGetProperty("format", out var format))
        {
            Collect(format, tags);
            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                duration = seconds;
        }
        var streams = new List<StreamInfo>();
        if (root.TryGetProperty("streams", out var list))
        {
            foreach (var stream in list.EnumerateArray())
            {
                var streamTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Collect(stream, streamTags);
                var attached = stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                streams.Add(new StreamInfo(
                    stream.TryGetProperty("index", out var index) ? index.GetInt32() : streams.Count,
                    stream.TryGetProperty("codec_type", out var type) ? type.GetString() ?? "" : "",
                    stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null,
                    attached,
                    streamTags));
            }
        }
        var chapters = root.TryGetProperty("chapters", out var chapterList) ? chapterList.GetArrayLength() : 0;
        return new Probe(tags, streams, chapters, duration);
    }

    private static void Collect(JsonElement element, Dictionary<string, string> into)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return;
        foreach (var tag in tags.EnumerateObject())
            if (tag.Value.ValueKind == JsonValueKind.String)
                into[tag.Name] = tag.Value.GetString() ?? "";
    }

    // ───────── Reading ─────────

    public static async Task<MediaMetadataInfo> ReadAsync(string path, MediaTools? media, CancellationToken cancel)
    {
        if (MediaMetadataMp3.Handles(path))
            return await Task.Run(() => MediaMetadataMp3.Read(path), cancel);
        if (media is null)
            throw new PanelException("FFmpeg is needed to read this file.") { Key = "engine.reason.needs_package", Args = ["FFmpeg"] };
        var probe = await ProbeAsync(media, path, cancel);
        var kind = KindOf(path);
        var values = new Dictionary<MediaMetadataField, string>();
        void Put(MediaMetadataField field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values[field] = value.Trim();
        }
        Put(MediaMetadataField.Title, probe.Tag("title"));
        Put(MediaMetadataField.Comment, probe.Tag("comment") ?? probe.Tag("description"));
        if (kind == MediaMetadataKind.Audio)
        {
            Put(MediaMetadataField.Artist, probe.Tag("artist") ?? probe.Tag("author"));
            Put(MediaMetadataField.Album, probe.Tag("album"));
            Put(MediaMetadataField.AlbumArtist, probe.Tag("album_artist"));
            Put(MediaMetadataField.Genre, probe.Tag("genre"));
            Put(MediaMetadataField.Date, probe.Tag("date") ?? probe.Tag("year"));
            Put(MediaMetadataField.Track, probe.Tag("track"));
        }
        else
        {
            Put(MediaMetadataField.Author, probe.Tag("artist") ?? probe.Tag("author"));
            Put(MediaMetadataField.Created, CreationText(probe.Tag("creation_time")));
            Put(MediaMetadataField.Location, probe.Tag("location") ?? probe.Tag("location-eng") ?? probe.Tag("com.apple.quicktime.location.ISO6709"));
        }
        var hasCover = probe.Streams.Any(s => s.AttachedPicture || (s.Type == "attachment" && s.Tags.TryGetValue("mimetype", out var mime) && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
        var dropped = probe.Streams.Count(s => s.Type is not ("video" or "audio" or "subtitle" or "attachment"));
        var foreign = probe.Tags.Keys.Any(k => k.StartsWith("com.apple.quicktime.", StringComparison.OrdinalIgnoreCase));
        return new MediaMetadataInfo(kind, values, hasCover, probe.Chapters > 0, dropped, foreign);
    }

    /// <summary>"2024-05-17T10:20:30.000000Z" as the panel shows a date; the first one when the file lists several.</summary>
    private static string? CreationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var first = text.Split(';')[0].Trim();
        return MetadataValues.TryParseLooseDate(first, out var date) ? MetadataValues.ForUser(date) : first;
    }

    // ───────── Writing ─────────

    /// <summary>Writes a copy of <paramref name="source"/> at <paramref name="output"/> with <paramref name="changes"/> applied (empty text removes a field).</summary>
    public static async Task EditAsync(string source, string output, IReadOnlyDictionary<MediaMetadataField, string?> changes, MediaTools? media,
        IProgress<double>? progress, CancellationToken cancel)
    {
        if (MediaMetadataMp3.Handles(source))
        {
            await MediaMetadataMp3.EditAsync(source, output, changes, progress, cancel);
            return;
        }
        var ffmpeg = media ?? throw new PanelException("FFmpeg is needed to write this file.") { Key = "engine.reason.needs_package", Args = ["FFmpeg"] };
        var probe = await ProbeAsync(ffmpeg, source, cancel);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        List<string> args = ["-i", source];
        args.AddRange(Maps(extension, removeCover: false));
        args.AddRange(["-c", "copy", "-map_metadata", "0", "-map_chapters", "0", "-fflags", "+bitexact"]);
        var perStream = extension is ".ogg" or ".oga" or ".opus";
        foreach (var (field, value) in changes)
        {
            foreach (var key in KeysFor(field, extension, probe))
            {
                var text = field == MediaMetadataField.Created && !string.IsNullOrEmpty(value) ? CreationValue(value) : value ?? "";
                args.AddRange(["-metadata", $"{key}={text}"]);
                // Ogg keeps its comments with the stream, which wins over the file's own: both must say the same.
                if (perStream && probe.Streams.Any(s => s.Type == "audio"))
                    args.AddRange(["-metadata:s:a:0", $"{key}={text}"]);
            }
        }
        args.AddRange(ContainerFlags(extension, probe, writeTags: true));
        args.Add(output);
        await RunAsync(ffmpeg, args, probe.Duration, output, progress, cancel);
    }

    /// <summary>Writes a copy of <paramref name="source"/> at <paramref name="output"/> with no tags, chapters or tracks of data.</summary>
    public static async Task StripAsync(string source, string output, bool removeCover, MediaTools? media, IProgress<double>? progress, CancellationToken cancel)
    {
        if (MediaMetadataMp3.Handles(source))
        {
            await MediaMetadataMp3.StripAsync(source, output, keepCover: !removeCover, progress, cancel);
            return;
        }
        var ffmpeg = media ?? throw new PanelException("FFmpeg is needed to write this file.") { Key = "engine.reason.needs_package", Args = ["FFmpeg"] };
        var probe = await ProbeAsync(ffmpeg, source, cancel);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        List<string> args = ["-i", source];
        args.AddRange(Maps(extension, removeCover));
        args.AddRange(["-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact"]);
        // The language of a track is how a player picks the right one, not information about the person: it stays.
        var counters = new Dictionary<string, int>();
        foreach (var stream in probe.Streams)
        {
            var letter = stream.Type switch { "video" when !(stream.AttachedPicture && removeCover) => "v", "audio" => "a", "subtitle" => "s", _ => null };
            if (letter is null)
                continue;
            var rank = counters.GetValueOrDefault(letter);
            counters[letter] = rank + 1;
            if (stream.Tags.TryGetValue("language", out var language) && language.Length > 0 && language != "und")
                args.AddRange([$"-metadata:s:{letter}:{rank}", $"language={language}"]);
        }
        args.AddRange(ContainerFlags(extension, probe, writeTags: false));
        args.Add(output);
        await RunAsync(ffmpeg, args, probe.Duration, output, progress, cancel);
    }

    private static async Task RunAsync(MediaTools media, List<string> args, double duration, string output, IProgress<double>? progress, CancellationToken cancel)
    {
        try
        {
            try
            {
                await media.RunAsync(args, duration, progress, cancel);
            }
            catch (PanelException) when (Path.GetExtension(output).ToLowerInvariant() is ".m4v" or ".m4a")
            {
                // FFmpeg writes these through its iPod muxer, which only takes H.264 and AAC: the plain MP4 muxer takes the rest.
                await DeleteAsync(output);
                args.Insert(args.Count - 1, "-f");
                args.Insert(args.Count - 1, "mp4");
                await media.RunAsync(args, duration, progress, cancel);
            }
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new PanelException("FFmpeg did not write the file.");
            if (Path.GetExtension(output).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                BlankMuxerName(output);
        }
        catch
        {
            await DeleteAsync(output);
            throw;
        }
    }

    /// <summary>
    /// A Matroska file always names the program that wrote it; with "-fflags +bitexact" that is just "Lavf". The four letters are
    /// overwritten with zeros where they stand (the same length, so no offset in the file moves) and the player sees an empty name.
    /// </summary>
    private static void BlankMuxerName(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var head = new byte[(int)Math.Min(stream.Length, 1 << 16)];
        stream.ReadExactly(head);
        // MuxingApp (4D 80) and WritingApp (57 41) with a four-byte value (84).
        foreach (var id in new byte[][] { [0x4D, 0x80, 0x84], [0x57, 0x41, 0x84] })
        {
            var at = head.AsSpan().IndexOf([.. id, (byte)'L', (byte)'a', (byte)'v', (byte)'f']);
            if (at < 0)
                continue;
            stream.Position = at + 3;
            stream.Write(new byte[4]);
        }
    }

    /// <summary>Deletes <paramref name="path"/>; a killed FFmpeg may hold it a moment longer, so a refused delete is retried for a second or two.</summary>
    private static async Task DeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 40 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    /// <summary>
    /// Which streams are copied: sound, picture (the cover too, unless asked to drop it) and subtitles, and the attachments a
    /// Matroska file keeps (fonts, not pictures when the cover goes). Streams of data — a GoPro's GPS, Apple's timed metadata — are
    /// left out: most cannot be written to the same container, and they are where a location hides.
    /// </summary>
    private static List<string> Maps(string extension, bool removeCover)
    {
        List<string> maps = ["-map", removeCover ? "0:V?" : "0:v?", "-map", "0:a?", "-map", "0:s?"];
        if (extension == ".mkv")
        {
            maps.AddRange(["-map", "0:t?"]);
            if (removeCover)
                foreach (var mime in new[] { "image/jpeg", "image/png", "image/webp", "image/gif", "image/bmp" })
                    maps.AddRange(["-map", $"-0:t:m:mimetype:{mime}"]);
        }
        return maps;
    }

    private static List<string> ContainerFlags(string extension, Probe probe, bool writeTags)
    {
        List<string> flags = [];
        switch (extension)
        {
            case ".mp4" or ".mov" or ".m4v" or ".m4a":
                // The index at the front, so the file starts playing before it is all loaded (as the files of phones and editors do).
                flags.AddRange(["-movflags", "+faststart"]);
                break;
            case ".wav" or ".aif" or ".aiff":
                // RIFF and AIFF have a handful of tag slots; an ID3 chunk carries all of the fields.
                if (writeTags)
                    flags.AddRange(["-write_id3v2", "1", "-id3v2_version", "3"]);
                if (writeTags && extension == ".wav" && probe.Tags.Keys.Any(k => k is "time_reference" or "origination_date" or "originator" or "coding_history" or "umid"))
                    flags.AddRange(["-write_bext", "1"]);
                break;
        }
        return flags;
    }

    /// <summary>The FFmpeg keys a panel field is stored under in this kind of file.</summary>
    private static IEnumerable<string> KeysFor(MediaMetadataField field, string extension, Probe probe)
    {
        switch (field)
        {
            case MediaMetadataField.Title:
                yield return "title";
                break;
            case MediaMetadataField.Artist:
                yield return extension is ".aif" or ".aiff" ? "author" : "artist";
                break;
            case MediaMetadataField.Author:
                yield return "artist";
                if (probe.HasTag("author"))
                    yield return "author";
                break;
            case MediaMetadataField.Album:
                yield return "album";
                break;
            case MediaMetadataField.AlbumArtist:
                yield return "album_artist";
                break;
            case MediaMetadataField.Genre:
                yield return "genre";
                break;
            case MediaMetadataField.Date:
                yield return "date";
                break;
            case MediaMetadataField.Track:
                yield return "track";
                break;
            case MediaMetadataField.Comment:
                yield return "comment";
                if (probe.HasTag("description"))
                    yield return "description";
                break;
            case MediaMetadataField.Created:
                yield return "creation_time";
                break;
            case MediaMetadataField.Location:
                yield return "location";
                foreach (var key in probe.Tags.Keys.Where(k => k.StartsWith("location", StringComparison.OrdinalIgnoreCase) && !k.Equals("location", StringComparison.OrdinalIgnoreCase)))
                    yield return key;
                break;
        }
    }

    private static string CreationValue(string text) =>
        MetadataValues.TryParseDate(text, out var date) ? MetadataValues.ForVideo(date) : text;
}
