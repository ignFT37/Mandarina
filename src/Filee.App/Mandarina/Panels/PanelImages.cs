// Mandarina: pictures for the photo panels. Previews are decoded with Magick.NET (HEIC, TIFF, AVIF, … which Avalonia
// can't show), upright and no larger than the preview needs; results are written with Magick.NET in the original's
// format (HEIC can't be written by the bundled Magick.NET, so a HEIC original is saved as JPG).

using Avalonia.Media.Imaging;
using Filee.Core.Formats;
using ImageMagick;

namespace Filee.App.Mandarina.Panels;

/// <summary>A picture ready to show: <see cref="Bitmap"/> is scaled for the preview, <see cref="Width"/> × <see cref="Height"/> are the original's pixels.</summary>
public sealed record PreviewImage(Bitmap Bitmap, uint Width, uint Height);

public static class PanelImages
{
    /// <summary>
    /// Reads <paramref name="path"/> (first frame, upright: the EXIF orientation is applied and cleared) at full size.
    /// Dispose the result. Runs synchronously: call it from Task.Run for big files.
    /// </summary>
    public static MagickImage Read(string path)
    {
        var image = new MagickImage(path);
        image.AutoOrient();
        return image;
    }

    /// <summary>A preview of <paramref name="path"/> no larger than <paramref name="maxSide"/> pixels on its longer side.</summary>
    public static Task<PreviewImage> LoadAsync(string path, int maxSide = 1600, CancellationToken cancel = default) =>
        Task.Run(() =>
        {
            cancel.ThrowIfCancellationRequested();
            using var image = Read(path);
            var (width, height) = (image.Width, image.Height);
            Fit(image, maxSide);
            return new PreviewImage(ToBitmap(image), width, height);
        }, cancel);

    /// <summary>Shrinks <paramref name="image"/> in place so its longer side is at most <paramref name="maxSide"/>; never enlarges.</summary>
    public static void Fit(MagickImage image, int maxSide)
    {
        if (Math.Max(image.Width, image.Height) > maxSide)
            image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
    }

    /// <summary>An Avalonia bitmap from a Magick image (PNG when it has transparency, BMP otherwise: much faster to encode).</summary>
    public static Bitmap ToBitmap(MagickImage image)
    {
        using var stream = new MemoryStream();
        image.Write(stream, image.HasAlpha ? MagickFormat.Png : MagickFormat.Bmp3);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    /// <summary>
    /// True for the extensions Filee reads as HEIC: ".heic", ".heif" and ".hif" (what Fujifilm cameras write). Asking the format
    /// registry keeps this list and the wheel's idea of a photo the same.
    /// </summary>
    public static bool IsHeif(string extension) => FormatRegistry.FindByExtension(extension) is { Id: "heic" };

    /// <summary>
    /// The extension a result of <paramref name="source"/> is saved with: its own, except HEIC/HEIF/HIF (Magick.NET can't
    /// write them) which become ".jpg". A ".hif" left as it is would be written in whatever Magick makes of the name
    /// (a MIFF, an AVIF…) behind a name that says HEIF.
    /// </summary>
    public static string OutputExtension(string source)
    {
        var extension = Path.GetExtension(source);
        return IsHeif(extension) ? ".jpg" : extension;
    }

    /// <summary>
    /// What a photo carries besides its pixels once a tool has changed them. Everything goes (EXIF, XMP, IPTC, Photoshop
    /// blocks, comments, text chunks) except the colour profile, without which a wide-gamut phone photo (Display P3) looks
    /// washed out; and, when <paramref name="strip"/> is false, the camera's EXIF block. Even then the previews in it never
    /// survive: a camera stores a whole second JPEG of the original there (the EXIF thumbnail, and sometimes a preview in
    /// the maker note), and writing the image as it is would carry it into the copy. <see cref="Save"/> calls this with
    /// <c>strip: false</c> for every photo.
    /// </summary>
    public static void CleanMetadata(MagickImage image, bool strip)
    {
        var colour = image.GetColorProfile();
        var exif = strip ? null : image.GetExifProfile();
        image.Strip();
        // Text chunks and tags (a PNG's "Thumbnail", a TIFF's description) are attributes, which Strip leaves in place.
        foreach (var name in image.AttributeNames.ToList())
            image.RemoveAttribute(name);
        if (colour is not null)
            image.SetProfile(colour);
        if (exif?.ToByteArray() is { } bytes)
        {
            // Magick.NET writes back the tags it knows and leaves the thumbnail (the next IFD) out.
            var clean = new ExifProfile(bytes);
            // The maker note is the camera's private block (some brands keep a full-size preview of the photo in it), and
            // any other big opaque block can hold a copy as well: none of them stays.
            foreach (var value in clean.Values.ToList())
                if (value.Tag == ExifTag.MakerNote || value.GetValue() is byte[] { Length: > 512 })
                    clean.RemoveValue(value.Tag);
            // The pixels are upright already (Read turned them): the tag must not turn them a second time.
            if (clean.GetValue(ExifTag.Orientation) is not null)
                clean.SetValue(ExifTag.Orientation, (ushort)1);
            image.SetProfile(clean);
        }
    }

    /// <summary>
    /// Writes <paramref name="image"/> to <paramref name="path"/> in the format of the file's extension (JPEG and WebP at
    /// quality 92, JPEG without transparency), keeping the colour profile and the camera's EXIF but not the previews of the
    /// original hidden in the metadata (see <see cref="CleanMetadata"/>).
    /// </summary>
    public static void Save(MagickImage image, string path)
    {
        // The picture has changed (cropped, edited, covered…): the small copies of the ORIGINAL a camera hides in the metadata
        // must not go along, or they would show what the tool took away.
        CleanMetadata(image, strip: false);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg")
        {
            // JPEG has no transparency: flatten onto white.
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
            image.Quality = 92;
        }
        else if (extension == ".webp")
        {
            image.Quality = 92;
        }
        image.Write(path);
    }
}
