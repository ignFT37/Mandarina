// Mandarina: the GIF driver of the METADATA tool. A GIF has no EXIF: its description is the comment block and everything else
// goes in XMP (an application extension), which is created when a field needs it. The frames are not touched.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class PhotoMetadataGif
{
    /// <summary>Application extensions that say how to play the GIF (loop count) or show it (colour profile): they stay.</summary>
    private static readonly HashSet<string> KeptApplications = ["NETSCAPE2.0", "ANIMEXTS1.0", "ICCRGBG1012"];

    internal static PhotoMetadataTags Load(MetadataGif gif)
    {
        var tags = new PhotoMetadataTags { CanStoreExif = false };
        foreach (var block in gif.Blocks)
        {
            if (block.IsComment)
            {
                tags.Texts.Add(new PhotoMetadataText(null, MetadataGif.CommentText(block), block));
            }
            else if (block.ApplicationId == MetadataGif.XmpApplication && tags.Xmp is null && !tags.XmpUnreadable)
            {
                tags.Xmp = MetadataGif.XmpPacket(block) is { } packet ? MetadataXmp.TryParse(packet) : null;
                tags.XmpUnreadable = tags.Xmp is null;
            }
        }
        return tags;
    }

    internal static byte[] Edit(byte[] original, IReadOnlyDictionary<PhotoMetadataField, string?> changes)
    {
        var gif = MetadataGif.Parse(original);
        var tags = Load(gif);
        PhotoMetadata.Apply(tags, changes);

        var blocks = new List<MetadataGifBlock>();
        var placedXmp = false;
        foreach (var block in gif.Blocks)
        {
            if (block.IsComment)
            {
                var text = tags.Texts.First(t => ReferenceEquals(t.Source, block));
                if (text.Removed)
                    continue;
                blocks.Add(text.Changed ? MetadataGif.Comment(text.Value) : block);
                continue;
            }
            if (block.ApplicationId == MetadataGif.XmpApplication)
            {
                if (!tags.XmpChanged)
                {
                    blocks.Add(block);
                }
                else if (tags.Xmp is { } xmp && !placedXmp)
                {
                    blocks.Add(MetadataGif.Xmp(xmp.ToPacket()));
                }
                placedXmp = true;
                continue;
            }
            blocks.Add(block);
        }
        if (!placedXmp && tags.XmpChanged && tags.Xmp is { } created)
            blocks.Insert(FirstPictureBlock(blocks), MetadataGif.Xmp(created.ToPacket()));

        var result = new MetadataGif(blocks).ToBytes();
        EnsureSamePicture(gif, MetadataGif.Parse(result));
        return result;
    }

    internal static byte[] Strip(byte[] original)
    {
        var gif = MetadataGif.Parse(original);
        var blocks = gif.Blocks.Where(b => !b.IsComment && !(b.IsApplication && !KeptApplications.Contains(b.ApplicationId))).ToList();
        var result = new MetadataGif(blocks).ToBytes();
        EnsureSamePicture(gif, MetadataGif.Parse(result));
        return result;
    }

    /// <summary>Where a new application extension goes: before the first graphic control or image, after the loop extension.</summary>
    private static int FirstPictureBlock(List<MetadataGifBlock> blocks)
    {
        var index = blocks.FindIndex(b => b.Kind == 0x2C || (b.Kind == 0x21 && b.Label == 0xF9));
        return index < 0 ? blocks.Count - 1 : index;
    }

    private static void EnsureSamePicture(MetadataGif original, MetadataGif written)
    {
        static List<MetadataGifBlock> Picture(MetadataGif gif) => gif.Blocks.Where(b => b.Kind is 0 or 0x2C || (b.Kind == 0x21 && b.Label is 0xF9 or 0x01) || b.ApplicationId is "NETSCAPE2.0" or "ANIMEXTS1.0" or "ICCRGBG1012").ToList();
        var a = Picture(original);
        var b = Picture(written);
        var same = a.Count == b.Count;
        for (var i = 0; same && i < a.Count; i++)
            same = a[i].Raw.AsSpan().SequenceEqual(b[i].Raw);
        if (!same)
            throw new PanelException("The picture changed while its tags were rewritten; nothing was saved.");
    }
}
