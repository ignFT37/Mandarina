// Mandarina: the AVIF and HEIC driver of the METADATA tool. Neither can be rewritten block by block, so the picture is decoded and
// encoded again with Magick.NET: an AVIF at its highest quality in its own format, a HEIC (which Magick.NET cannot write) as a
// JPEG at quality 95. Unlike the other panels this one does not go through PanelImages.Save, which would erase the very tags the
// user wants to keep: here the profiles that stay are chosen one by one — the colour profile, the EXIF and the XMP, after the
// edits — and the previews of the original that a camera hides in them (the EXIF thumbnail, the maker note, XMP thumbnails) never
// go along, because the pixels are turned upright and the previews would show the picture the wrong way round.

using Filee.App.Mandarina.Panels;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

internal static class PhotoMetadataMagick
{
    internal static PhotoMetadataTags Load(string path)
    {
        using var image = new MagickImage();
        image.Ping(path);
        return Tags(image);
    }

    private static PhotoMetadataTags Tags(MagickImage image)
    {
        var tags = new PhotoMetadataTags();
        if (image.GetProfile("exif")?.ToByteArray() is { } exif)
            tags.Exif = OpenExif(exif);
        if (image.GetProfile("xmp")?.ToByteArray() is { } xmp)
        {
            tags.Xmp = MetadataXmp.TryParse(xmp);
            tags.XmpUnreadable = tags.Xmp is null;
        }
        return tags;
    }

    private static MetadataTiff? OpenExif(byte[] profile)
    {
        var stream = new MemoryStream();
        stream.Write(profile.AsSpan().StartsWith("Exif\0\0"u8) ? profile.AsSpan(6) : profile);
        try
        {
            return MetadataTiff.Open(stream);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    internal static void Edit(string source, string output, IReadOnlyDictionary<PhotoMetadataField, string?> changes) =>
        Save(source, output, tags => PhotoMetadata.Apply(tags, changes), keepTags: true);

    internal static void Strip(string source, string output) => Save(source, output, _ => { }, keepTags: false);

    private static void Save(string source, string output, Action<PhotoMetadataTags> edit, bool keepTags)
    {
        // An animated AVIF would come out as its first frame: better to say so than to lose the rest silently.
        using (var frames = new MagickImageCollection())
        {
            frames.Ping(source);
            if (frames.Count > 1 && !PhotoMetadata.OutputExtension(source).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                throw new PanelException("This AVIF is animated: its tags cannot be changed without losing the animation.");
        }

        // Upright pixels, first picture of the file.
        using var image = PanelImages.Read(source);
        var colour = image.GetColorProfile();
        var tags = Tags(image);
        if (keepTags && image.GetProfile("exif") is not null && tags.Exif is null)
            throw new PanelException("The EXIF of this picture is damaged: use \"Remove all\" instead.");

        byte[]? exifBytes = null;
        byte[]? xmpBytes = null;
        if (keepTags)
        {
            if (tags.Exif is { } exif)
            {
                // The previews of the picture as it was, and the orientation the pixels have already been given.
                exif.RemoveThumbnail();
                exif.Remove(MetadataTiffIfd.Exif, MetadataTiff.MakerNote);
                if (exif.Has(MetadataTiffIfd.Main, MetadataTiff.Orientation))
                    exif.SetShort(MetadataTiffIfd.Main, MetadataTiff.Orientation, 1);
            }
            if (tags.Xmp is { } xmp)
            {
                xmp.RemoveThumbnails();
                xmp.RemoveMatching(name => name.LocalName == "Orientation");
                tags.XmpChanged = true;
            }
            edit(tags);
            if (tags.Exif is { } edited)
            {
                edited.Commit();
                exifBytes = edited.ToArray();
            }
            if (tags.Xmp is { } xmpAfter)
                xmpBytes = xmpAfter.ToPacket();
        }

        foreach (var name in image.ProfileNames.ToList())
            image.RemoveProfile(name);
        foreach (var name in image.AttributeNames.ToList())
            image.RemoveAttribute(name);
        if (colour is not null)
            image.SetProfile(colour);
        if (exifBytes is not null)
            image.SetProfile(new ImageProfile("exif", [.. "Exif\0\0"u8, .. exifBytes]));
        if (xmpBytes is not null)
            image.SetProfile(new ImageProfile("xmp", xmpBytes));

        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (PhotoMetadata.OutputExtension(source).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
            image.Quality = 95;
            image.Write(stream, MagickFormat.Jpeg);
        }
        else
        {
            image.Quality = 100;
            image.Write(stream, MagickFormat.Avif);
        }
    }
}
