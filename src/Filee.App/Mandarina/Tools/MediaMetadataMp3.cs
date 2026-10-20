// Mandarina: the MP3 driver of the METADATA tool. It reads the tags (ID3v2, then the APEv2 and ID3v1 at the end) into the
// fields of the panel, and writes a copy whose audio frames are the original bytes: edited tags go in a new ID3v2 tag (frames
// the tool does not edit are kept as they are) and the old ID3v1/APEv2 copies are brought up to date, so no old value stays in
// any of them; "strip" leaves the audio alone and, when asked, the cover picture without the metadata of its own.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class MediaMetadataMp3
{
    /// <summary>True when <paramref name="path"/> looks like an MP3 (an ID3 tag or an MPEG audio frame at the start).</summary>
    public static bool Handles(string path)
    {
        if (!Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            return false;
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return stream.Read(head) == 3 && ((head[0] == 'I' && head[1] == 'D' && head[2] == '3') || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0));
    }

    // ───────── Reading ─────────

    public static MediaMetadataInfo Read(string path)
    {
        var id3 = MetadataId3.Read(path);
        var values = new Dictionary<MediaMetadataField, string>();
        void Put(MediaMetadataField field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values[field] = value.Trim();
        }
        Put(MediaMetadataField.Title, id3.Text("TIT2") ?? id3.Ape?.Text("Title") ?? Blank(id3.V1?.Title));
        Put(MediaMetadataField.Artist, id3.Text("TPE1") ?? id3.Ape?.Text("Artist") ?? Blank(id3.V1?.Artist));
        Put(MediaMetadataField.Album, id3.Text("TALB") ?? id3.Ape?.Text("Album") ?? Blank(id3.V1?.Album));
        Put(MediaMetadataField.AlbumArtist, id3.Text("TPE2") ?? id3.Ape?.Text("Album Artist") ?? id3.Ape?.Text("Albumartist"));
        Put(MediaMetadataField.Genre, id3.Genre() ?? id3.Ape?.Text("Genre") ?? (id3.V1 is { } g ? MetadataId3.NameOf(g.Genre) : null));
        Put(MediaMetadataField.Date, id3.Date() ?? id3.Ape?.Text("Year") ?? Blank(id3.V1?.Year));
        Put(MediaMetadataField.Track, id3.Text("TRCK") ?? id3.Ape?.Text("Track") ?? (id3.V1 is { Track: > 0 } t ? t.Track.ToString() : null));
        Put(MediaMetadataField.Comment, id3.Comment() ?? id3.Ape?.Text("Comment") ?? Blank(id3.V1?.Comment));
        return new MediaMetadataInfo(MediaMetadataKind.Audio, values, id3.HasPicture || id3.Ape?.Items.Any(i => i.Key.StartsWith("Cover Art", StringComparison.OrdinalIgnoreCase)) == true,
            HasChapters: id3.Frames.Any(f => f.Id == "CHAP"), DroppedStreams: 0, LosesCustomTags: id3.Converted);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ───────── Edit ─────────

    public static async Task EditAsync(string source, string output, IReadOnlyDictionary<MediaMetadataField, string?> changes, IProgress<double>? progress, CancellationToken cancel)
    {
        var id3 = MetadataId3.Read(source);
        var version = id3.Version is 3 or 4 ? id3.Version : 3;
        var frames = id3.Frames.ToList();
        foreach (var (field, value) in changes)
            Apply(frames, field, value, version);

        // The new tag keeps the room the old one had, so a small edit does not move the audio.
        var tag = MetadataId3.Build(version, frames, id3.Version == version ? id3.TagSize : 0);

        var v1 = id3.V1;
        if (v1 is not null)
            foreach (var (field, value) in changes)
                ApplyV1(v1, field, value);
        var ape = id3.Ape;
        if (ape is not null)
            foreach (var (field, value) in changes)
                ApplyApe(ape, field, value);

        await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await target.WriteAsync(tag, cancel);
        await CopyAudioAsync(source, id3, target, progress, cancel);
        if (ape is not null)
            await target.WriteAsync(ape.ToBytes(), cancel);
        if (v1 is not null && !v1.IsEmpty)
            await target.WriteAsync(v1.ToBytes(), cancel);
    }

    private static void Apply(List<MetadataId3Frame> frames, MediaMetadataField field, string? value, int version)
    {
        switch (field)
        {
            case MediaMetadataField.Title:
                SetText(frames, "TIT2", value, version);
                break;
            case MediaMetadataField.Artist:
                SetText(frames, "TPE1", value, version);
                break;
            case MediaMetadataField.Album:
                SetText(frames, "TALB", value, version);
                break;
            case MediaMetadataField.AlbumArtist:
                SetText(frames, "TPE2", value, version);
                break;
            case MediaMetadataField.Genre:
                SetText(frames, "TCON", value, version);
                break;
            case MediaMetadataField.Track:
                SetText(frames, "TRCK", value, version);
                break;
            case MediaMetadataField.Date:
                SetDate(frames, value, version);
                break;
            case MediaMetadataField.Comment:
                SetComment(frames, value, version);
                break;
        }
    }

    /// <summary>Replaces every frame <paramref name="id"/> by one with <paramref name="value"/> (none when it is empty), where the first one was.</summary>
    private static void SetText(List<MetadataId3Frame> frames, string id, string? value, int version)
    {
        var at = frames.FindIndex(f => f.Id == id);
        frames.RemoveAll(f => f.Id == id);
        if (string.IsNullOrEmpty(value))
            return;
        frames.Insert(at < 0 ? frames.Count : Math.Min(at, frames.Count), MetadataId3.TextFrame(id, value, version));
    }

    private static void SetDate(List<MetadataId3Frame> frames, string? value, int version)
    {
        var at = frames.FindIndex(f => f.Id is "TYER" or "TDRC" or "TDAT" or "TIME" or "TRDA");
        frames.RemoveAll(f => f.Id is "TYER" or "TDRC" or "TDAT" or "TIME" or "TRDA");
        if (string.IsNullOrEmpty(value))
            return;
        at = at < 0 ? frames.Count : Math.Min(at, frames.Count);
        if (version == 4)
        {
            frames.Insert(at, MetadataId3.TextFrame("TDRC", value, version));
            return;
        }
        // v2.3 keeps the year, the day and month, and the time in three frames.
        var year = value.Length >= 4 ? value[..4] : value;
        var added = new List<MetadataId3Frame> { MetadataId3.TextFrame("TYER", year, version) };
        if (value.Length >= 10 && value[4] == '-' && value[7] == '-')
            added.Add(MetadataId3.TextFrame("TDAT", value[8..10] + value[5..7], version));
        if (value.Length >= 16 && value[10] is 'T' or ' ' && value[13] == ':')
            added.Add(MetadataId3.TextFrame("TIME", value[11..13] + value[14..16], version));
        frames.InsertRange(at, added);
    }

    private static void SetComment(List<MetadataId3Frame> frames, string? value, int version)
    {
        var language = "eng";
        var at = -1;
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            // A comment lives in a COMM frame (or, as FFmpeg writes it, in a TXXX frame described "comment").
            if (frames[i].Id == "TXXX" && MetadataId3.ReadUserText(frames[i]) is { } user && user.Description.Equals("comment", StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                frames.RemoveAt(i);
                continue;
            }
            if (frames[i].Id != "COMM" || MetadataId3.ReadComment(frames[i]) is not { } comment || comment.Description.StartsWith("iTun", StringComparison.OrdinalIgnoreCase))
                continue;
            language = comment.Language;
            at = i;
            frames.RemoveAt(i);
        }
        if (!string.IsNullOrEmpty(value))
            frames.Insert(at < 0 ? frames.Count : Math.Min(at, frames.Count), MetadataId3.CommentFrame(language, value, version));
    }

    private static void ApplyV1(MetadataId3V1 v1, MediaMetadataField field, string? value)
    {
        value ??= "";
        switch (field)
        {
            case MediaMetadataField.Title:
                v1.Title = value;
                break;
            case MediaMetadataField.Artist:
                v1.Artist = value;
                break;
            case MediaMetadataField.Album:
                v1.Album = value;
                break;
            case MediaMetadataField.Date:
                v1.Year = value.Length >= 4 ? value[..4] : value;
                break;
            case MediaMetadataField.Comment:
                v1.Comment = value;
                break;
            case MediaMetadataField.Track:
                v1.Track = int.TryParse(value.Split('/')[0], out var track) && track is > 0 and < 256 ? track : 0;
                break;
            case MediaMetadataField.Genre:
                v1.Genre = value.Length == 0 ? 255 : MetadataId3.CodeOf(value);
                break;
        }
    }

    private static void ApplyApe(MetadataApeTag ape, MediaMetadataField field, string? value)
    {
        string[] keys = field switch
        {
            MediaMetadataField.Title => ["Title"],
            MediaMetadataField.Artist => ["Artist"],
            MediaMetadataField.Album => ["Album"],
            MediaMetadataField.AlbumArtist => ["Album Artist", "Albumartist"],
            MediaMetadataField.Genre => ["Genre"],
            MediaMetadataField.Date => ["Year"],
            MediaMetadataField.Track => ["Track"],
            MediaMetadataField.Comment => ["Comment"],
            _ => [],
        };
        foreach (var key in keys)
        {
            var existed = ape.Text(key) is not null;
            ape.Remove(key);
            if (existed && !string.IsNullOrEmpty(value))
                ape.Set(key, value);
        }
    }

    // ───────── Strip ─────────

    /// <summary>Writes the audio of <paramref name="source"/> and nothing else, or with only its cover picture (cleaned of its own metadata).</summary>
    public static async Task StripAsync(string source, string output, bool keepCover, IProgress<double>? progress, CancellationToken cancel)
    {
        var id3 = MetadataId3.Read(source);
        var tag = Array.Empty<byte>();
        if (keepCover)
        {
            var pictures = new List<MetadataId3Frame>();
            foreach (var frame in id3.Frames.Where(f => f.Id == "APIC"))
            {
                if (MetadataId3.ReadPicture(frame) is not { } picture)
                    continue;
                // The description of a picture may say who made it, and the file may carry its own EXIF.
                pictures.Add(MetadataId3.PictureFrame(picture.Mime, picture.Type, CleanPicture(picture.Data)));
            }
            tag = MetadataId3.Build(3, pictures);
        }
        await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await target.WriteAsync(tag, cancel);
        await CopyAudioAsync(source, id3, target, progress, cancel);
    }

    private static byte[] CleanPicture(byte[] data)
    {
        try
        {
            return PhotoMetadata.Sniff(data) switch
            {
                PhotoMetadataKind.Jpeg => PhotoMetadataJpeg.Strip(data),
                PhotoMetadataKind.Png => PhotoMetadataPng.Strip(data),
                _ => data,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or PanelException)
        {
            return data;
        }
    }

    /// <summary>Copies the audio frames (everything between the tags) to <paramref name="target"/>, reporting progress.</summary>
    private static async Task CopyAudioAsync(string source, MetadataId3 id3, Stream target, IProgress<double>? progress, CancellationToken cancel)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        input.Position = id3.AudioStart;
        var remaining = id3.AudioEnd - id3.AudioStart;
        var total = Math.Max(1, remaining);
        var buffer = new byte[1 << 20];
        while (remaining > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancel);
            if (read <= 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
            remaining -= read;
            progress?.Report(1 - (double)remaining / total);
        }
    }
}
