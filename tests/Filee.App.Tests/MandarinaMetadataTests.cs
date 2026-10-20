// Mandarina: the METADATA tool. The panel is rendered with real files (a phone-like photo with EXIF, XMP, IPTC and GPS; a song
// with tags and a cover; a video; a PDF) and applied against the real engines. The important checks read the bytes of what was
// written: a tag the user removed must not be anywhere in the file (EXIF, XMP, IPTC, text chunks, a thumbnail, even as UTF-16),
// and the picture, the audio or the video must be the original's, byte for byte or frame for frame.

using System.Text;
using Avalonia.Headless.XUnit;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaMetadataTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-metadata").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────── Samples ─────────

    private string Photo(string name = "camara.jpg", bool bigEndian = true)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Samples.PhoneJpeg(bigEndian));
        return path;
    }

    private async Task<string?> SongAsync(string name = "cancion.mp3")
    {
        var cover = Path.Combine(_root, "cover.jpg");
        if (!File.Exists(cover))
            MakePhoto(cover, 300, 300);
        return await MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-i", cover, "-map", "0:a", "-map", "1:v",
            "-c:a", "libmp3lame", "-q:a", "5", "-c:v", "copy", "-id3v2_version", "3", "-disposition:v", "attached_pic",
            "-metadata", "title=Cancion Secreta", "-metadata", "artist=Artista Secreto", "-metadata", "album=Album Secreto", "-metadata", "genre=Rock",
            "-metadata", "date=2023-04-05", "-metadata", "track=3/12", "-metadata", "comment=Comentario Secreto");
    }

    private async Task<string?> VideoAsync(string name = "video.mp4")
    {
        return await MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=330:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-metadata", "title=Pelicula Secreta", "-metadata", "artist=Autor Secreto",
            "-metadata", "comment=Comentario Secreto", "-metadata", "creation_time=2024-05-17T10:20:30Z", "-metadata", "location=+40.4168-003.7038/",
            "-metadata", "location-eng=+40.4168-003.7038/");
    }

    private string Pdf(string name = "documento.pdf")
    {
        var path = Path.Combine(_root, name);
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.AddPage();
        document.AddPage();
        document.Info.Title = "Titulo Secreto";
        document.Info.Author = "Autor Secreto";
        document.Info.Subject = "Asunto Secreto";
        document.Info.Keywords = "clave secreta";
        document.Info.Creator = "Creador Secreto";
        document.Save(path);
        return path;
    }


    // ───────── Helpers ─────────

    private static bool Has(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0
        || data.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(text)) >= 0
        || (text.All(c => c < 256) && text.Any(c => c > 127) && data.AsSpan().IndexOf(Encoding.Latin1.GetBytes(text)) >= 0);

    private static int Count(byte[] data, byte[] pattern)
    {
        var count = 0;
        for (var at = data.AsSpan().IndexOf(pattern); at >= 0; at = data.AsSpan(at + 1).IndexOf(pattern) is var next and >= 0 ? at + 1 + next : -1)
            count++;
        return count;
    }

    private static void AssertNoTrace(string path, params string[] secrets)
    {
        var data = File.ReadAllBytes(path);
        foreach (var secret in secrets)
            Assert.False(Has(data, secret), $"\"{secret}\" is still in {Path.GetFileName(path)}");
    }

    private static void AssertSamePixels(string original, string written)
    {
        using var a = new MagickImage(original);
        using var b = new MagickImage(written);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        Assert.Equal(0d, a.Compare(b, ErrorMetric.Absolute));
    }

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private async Task<MetadataPanel> OpenAsync(params string[] files)
    {
        var panel = new MetadataPanel(Context(files));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private static async Task<string> ApplyAsync(MetadataPanel panel)
    {
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        return Assert.Single(panel.Outputs);
    }

    private static readonly string[] PhoneSecrets =
        ["Autor01", "Secreto", "Secreta", "Atlantis", "España", "Propietario", "SERIE-CUERPO", "ModeloSecreto", "MarcaSecreta", "HostSecreto", "Lente"];

    // ───────── Render ─────────

    [AvaloniaFact]
    public async Task The_photo_panel_renders_with_the_tags_of_the_photo()
    {
        var panel = new MetadataPanel(Context(Photo()));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal("Persona Autora", panel.Field("Author")!.Text);
        Assert.Equal("MarcaSecreta", panel.Field("Make")!.Text);
        Assert.StartsWith("40.4168", panel.Field("Latitude")!.Text);
        Assert.Equal("2024-05-17 10:20:30", panel.Field("Date")!.Text);
        Render(panel, "metadata-photo");
        Assert.Equal("Metadatos", panel.Title);
    }

    [AvaloniaFact]
    public async Task Remove_all_renders_with_the_number_of_tags()
    {
        var panel = new MetadataPanel(Context(Photo()));
        await WaitAsync(() => !panel.IsLoading);
        panel.IsEditMode = false;
        Assert.True(panel.IsRemoveMode);
        Assert.Matches(@"Ahora tiene \d+ etiquetas\.", panel.TagsText);
        Render(panel, "metadata-photo-remove");
    }

    [AvaloniaFact]
    public async Task The_song_panel_renders_with_its_tags_and_cover()
    {
        var song = await SongAsync();
        if (song is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = new MetadataPanel(Context(song));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal("Cancion Secreta", panel.Field("Title")!.Text);
        Assert.Equal("Artista Secreto", panel.Field("Artist")!.Text);
        Assert.Equal("Rock", panel.Field("Genre")!.Text);
        Assert.Equal("2023-04-05", panel.Field("Date")!.Text);
        Assert.Equal("3/12", panel.Field("Track")!.Text);
        Assert.Equal("Comentario Secreto", panel.Field("Comment")!.Text);
        Assert.True(panel.HasCover);
        Render(panel, "metadata-song");
        panel.IsEditMode = false;
        Render(panel, "metadata-song-remove");
    }

    [AvaloniaFact]
    public async Task The_video_panel_renders_with_its_title_date_and_location()
    {
        var video = await VideoAsync();
        if (video is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = new MetadataPanel(Context(video));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal("Pelicula Secreta", panel.Field("Title")!.Text);
        Assert.Equal("Autor Secreto", panel.Field("Author")!.Text);
        Assert.Equal("2024-05-17 10:20:30", panel.Field("Created")!.Text);
        Assert.Equal("+40.4168-003.7038/", panel.Field("Location")!.Text);
        Render(panel, "metadata-video");
    }

    [AvaloniaFact]
    public async Task The_pdf_panel_renders_with_the_document_info()
    {
        var panel = new MetadataPanel(Context(Pdf()));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal("Titulo Secreto", panel.Field("Title")!.Text);
        Assert.Equal("Creador Secreto", panel.Field("Creator")!.Text);
        Render(panel, "metadata-pdf");
    }

    [AvaloniaFact]
    public async Task Several_photos_show_the_common_value_or_several_values()
    {
        var first = Photo("uno.jpg");
        var second = Path.Combine(_root, "dos.jpg");
        // The same camera and author, another model and no GPS.
        PhotoMetadata.Edit(first, second, new Dictionary<PhotoMetadataField, string?> { [PhotoMetadataField.Model] = "Otro Modelo", [PhotoMetadataField.Latitude] = null, [PhotoMetadataField.Longitude] = null });
        var panel = new MetadataPanel(Context(first, second));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal("MarcaSecreta", panel.Field("Make")!.Text);          // common
        Assert.False(panel.Field("Make")!.Differs);
        Assert.True(panel.Field("Model")!.Differs);                       // different
        Assert.Equal("", panel.Field("Model")!.Text);
        Assert.Equal("(varios valores)", panel.Field("Model")!.Placeholder);
        Assert.True(panel.Field("Latitude")!.Differs);                    // only one has it
        Assert.False(panel.CanApply);                                      // nothing touched
        Render(panel, "metadata-photos-many");
    }

    // ───────── JPEG: nothing recoded, nothing old left ─────────

    [AvaloniaFact]
    public async Task Editing_a_photo_changes_the_author_and_drops_the_location_without_touching_the_picture()
    {
        var photo = Photo();
        var before = Hash(photo);
        var panel = await OpenAsync(photo);
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.RemoveLocation();
        Assert.True(panel.CanApply);

        var output = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "camara metadatos.jpg"), output);
        Assert.Equal(before, Hash(photo));                                   // the original is untouched
        AssertSamePixels(photo, output);                                     // not a pixel changed: nothing was recompressed
        var bytes = File.ReadAllBytes(output);
        Assert.False(Has(bytes, "Persona Autora"));                     // not in EXIF, XMP, IPTC or the Windows XP tag
        Assert.False(Has(bytes, "Atlantis Secreta"));                        // the place names go with the position
        Assert.False(Has(bytes, "40,25.08N"));
        Assert.False(Has(bytes, "GPSLatitude"));
        Assert.True(Count(bytes, Encoding.UTF8.GetBytes("Ana Nueva")) >= 3); // EXIF, XMP and IPTC say the new author
        Assert.True(Has(bytes, "MarcaSecreta") && Has(bytes, "Lente Secreta")); // what was not touched is still there
        Assert.Equal(Count(File.ReadAllBytes(photo), [0xFF, 0xD8, 0xFF]), Count(bytes, [0xFF, 0xD8, 0xFF])); // thumbnail and maker note intact

        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.Equal("Ana Nueva", exif.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal("MarcaSecreta", exif.GetValue(ExifTag.Make)!.Value);
        Assert.Null(exif.GetValue(ExifTag.GPSLatitude));
        Assert.Null(exif.GetValue(ExifTag.GPSLongitude));
        Assert.Null(exif.GetValue(ExifTag.GPSAltitude));
        var reread = PhotoMetadata.Read(output);
        Assert.False(reread.HasLocation);
        Assert.Equal("Ana Nueva", reread.Values[PhotoMetadataField.Author]);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_byte_orders_of_exif_are_edited_the_same_way(bool bigEndian)
    {
        var photo = Photo("orden.jpg", bigEndian);
        var panel = await OpenAsync(photo);
        Assert.Equal("Persona Autora", panel.Field("Author")!.Text);
        panel.Field("Model")!.Text = "Modelo Nuevo";
        var output = await ApplyAsync(panel);
        using var result = new MagickImage(output);
        Assert.Equal("Modelo Nuevo", result.GetExifProfile()!.GetValue(ExifTag.Model)!.Value);
        Assert.Equal("Persona Autora", result.GetExifProfile()!.GetValue(ExifTag.Artist)!.Value);
        AssertSamePixels(photo, output);
    }

    [AvaloniaFact]
    public async Task The_maker_note_keeps_its_position_so_its_private_offsets_stay_valid()
    {
        var photo = Photo();
        var panel = await OpenAsync(photo);
        panel.Field("Author")!.Text = "Ana Nueva con un nombre bastante mas largo que el anterior";
        var output = await ApplyAsync(panel);

        MetadataTiff Tiff(string path)
        {
            var stream = new MemoryStream();
            stream.Write(MetadataJpeg.Parse(File.ReadAllBytes(path)).ExifPayload()!);
            return MetadataTiff.Open(stream)!;
        }
        var oldNote = Tiff(photo).Find(MetadataTiffIfd.Exif, MetadataTiff.MakerNote)!;
        var newNote = Tiff(output).Find(MetadataTiffIfd.Exif, MetadataTiff.MakerNote)!;
        Assert.Equal(oldNote.Offset, newNote.Offset);                        // not moved
        Assert.Equal(oldNote.Data, newNote.Data);                            // not changed (its self-pointer still says where it is)
    }

    [AvaloniaFact]
    public async Task Crossing_out_a_field_removes_it_from_every_place_that_holds_it()
    {
        var photo = Photo();
        var panel = await OpenAsync(photo);
        panel.Field("Description")!.ToggleRemoved();
        Assert.True(panel.Field("Description")!.Removed);
        Assert.Equal("(se quitará)", panel.Field("Description")!.Placeholder);
        var output = await ApplyAsync(panel);

        var bytes = File.ReadAllBytes(output);
        Assert.False(Has(bytes, "Paseo secreto"));                           // EXIF ImageDescription, XMP, IPTC caption
        Assert.False(Has(bytes, "Secret walk"));                             // the other language of the XMP
        Assert.False(Has(bytes, "Comentario secreto"));                      // the JPEG comment is the description too
        Assert.True(Has(bytes, "Persona Autora"));                      // the author was not touched
        Assert.False(PhotoMetadata.Read(output).Values.ContainsKey(PhotoMetadataField.Description));
    }

    [AvaloniaFact]
    public async Task Emptying_a_box_removes_the_field_like_the_cross()
    {
        var photo = Photo();
        var panel = await OpenAsync(photo);
        panel.Field("Copyright")!.Text = "";
        Assert.True(panel.Field("Copyright")!.IsRemoval);
        var output = await ApplyAsync(panel);
        Assert.False(Has(File.ReadAllBytes(output), "Copyright Secreto"));
    }

    [AvaloniaFact]
    public async Task Removing_the_camera_takes_the_maker_note_and_the_serial_number_with_it()
    {
        var photo = Photo();
        var panel = await OpenAsync(photo);
        panel.Field("Make")!.ToggleRemoved();
        panel.Field("Model")!.ToggleRemoved();
        panel.Field("Lens")!.ToggleRemoved();
        var output = await ApplyAsync(panel);

        var bytes = File.ReadAllBytes(output);
        foreach (var secret in new[] { "MarcaSecreta", "ModeloSecreto", "SERIE-CUERPO", "Lente Secreta", "HostSecreto" })
            Assert.False(Has(bytes, secret), secret);
        Assert.Equal(Count(File.ReadAllBytes(photo), [0xFF, 0xD8, 0xFF]) - 1, Count(bytes, [0xFF, 0xD8, 0xFF])); // the preview in the maker note is gone
        Assert.True(Has(bytes, "Persona Autora"));
        AssertSamePixels(photo, output);
    }

    [AvaloniaFact]
    public async Task Changing_the_date_leaves_no_trace_of_the_old_one()
    {
        var photo = Photo();
        var panel = await OpenAsync(photo);
        panel.Field("Date")!.Text = "2025-01-02 03:04:05";
        var output = await ApplyAsync(panel);

        var bytes = File.ReadAllBytes(output);
        foreach (var old in new[] { "2024:05:17", "20240517", "2024-05-17", "102030", "10:20:30" })
            Assert.False(Has(bytes, old), old);
        Assert.True(Has(bytes, "2025:01:02 03:04:05"));                      // EXIF
        Assert.True(Has(bytes, "2025-01-02T03:04:05"));                      // XMP
        Assert.True(Has(bytes, "20250102"));                                 // IPTC
        Assert.Equal("2025-01-02 03:04:05", PhotoMetadata.Read(output).Values[PhotoMetadataField.Date]);
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile()!.GetValue(ExifTag.SubsecTimeOriginal));      // the fractions belonged to the old time
        Assert.Null(result.GetExifProfile()!.GetValue(ExifTag.OffsetTimeOriginal));
    }

    [AvaloniaFact]
    public async Task A_new_position_replaces_the_old_one_and_the_place_names()
    {
        var photo = Photo();
        var panel = await OpenAsync(photo);
        panel.Field("Latitude")!.Text = "48.8566";
        panel.Field("Longitude")!.Text = "2,3522";                           // a comma is accepted
        var output = await ApplyAsync(panel);

        var read = PhotoMetadata.Read(output);
        Assert.StartsWith("48.8566", read.Values[PhotoMetadataField.Latitude]);
        Assert.StartsWith("2.3522", read.Values[PhotoMetadataField.Longitude]);
        var bytes = File.ReadAllBytes(output);
        Assert.False(Has(bytes, "Atlantis Secreta"));                        // the old place's name
        Assert.False(Has(bytes, "40,25.08N"));
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile()!.GetValue(ExifTag.GPSAltitude)); // the old altitude does not describe the new place
        Assert.Null(result.GetExifProfile()!.GetValue(ExifTag.GPSDateStamp));
        Assert.Equal("N", result.GetExifProfile()!.GetValue(ExifTag.GPSLatitudeRef)!.Value);
        Assert.Equal("E", result.GetExifProfile()!.GetValue(ExifTag.GPSLongitudeRef)!.Value);
    }

    [AvaloniaFact]
    public async Task Remove_all_leaves_a_photo_with_no_tags_the_same_pixels_the_profile_and_the_orientation()
    {
        var photo = Path.Combine(_root, "foto.jpg");
        File.WriteAllBytes(photo, Samples.PhoneJpeg(true, withProfile: true));
        var before = Hash(photo);
        var panel = await OpenAsync(photo);
        panel.IsEditMode = false;
        var output = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "foto sin metadatos.jpg"), output);
        Assert.Equal(before, Hash(photo));
        AssertSamePixels(photo, output);
        AssertNoTrace(output, [.. PhoneSecrets, "2024", "Software", "Cam", "http://ns.adobe.com", "Photoshop", "xmpGImg", "Comentario"]);
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(1, Count(bytes, [0xFF, 0xD8, 0xFF]));                   // no thumbnail, no preview: one picture only
        var jpeg = MetadataJpeg.Parse(bytes);
        Assert.Contains(jpeg.Segments, s => s.IsIcc);                        // the colours do not change
        Assert.DoesNotContain(jpeg.Segments, s => s.IsXmp || s.IsPhotoshop || s.IsComment);
        using var result = new MagickImage(output);
        Assert.NotNull(result.GetColorProfile());
        // The photo was taken sideways: the orientation is what shows it upright, and it is not personal.
        var read = PhotoMetadata.Read(output);
        Assert.Equal(1, read.Tags);
        Assert.Empty(read.Values);
        Assert.Equal(6, result.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
    }

    [AvaloniaFact]
    public async Task A_photo_that_stands_upright_ends_with_no_exif_at_all()
    {
        var photo = Path.Combine(_root, "recta.jpg");
        File.WriteAllBytes(photo, Samples.Jpeg(300, 200));
        var tagged = Path.Combine(_root, "recta con etiquetas.jpg");
        PhotoMetadata.Edit(photo, tagged, new Dictionary<PhotoMetadataField, string?> { [PhotoMetadataField.Author] = "Alguien Secreto" });
        var panel = await OpenAsync(tagged);
        panel.IsEditMode = false;
        var output = await ApplyAsync(panel);

        Assert.DoesNotContain(MetadataJpeg.Parse(File.ReadAllBytes(output)).Segments, s => s.IsExif);
        AssertNoTrace(output, "Alguien");
        AssertSamePixels(photo, output);
    }

    [AvaloniaFact]
    public async Task A_photo_without_exif_gets_one_when_a_field_is_set()
    {
        var photo = Path.Combine(_root, "limpia.jpg");
        File.WriteAllBytes(photo, Samples.Jpeg());
        var panel = await OpenAsync(photo);
        Assert.Equal("", panel.Field("Author")!.Text);
        Assert.False(panel.Field("Author")!.CanClear);                        // nothing to remove yet
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.Field("Make")!.Text = "Camara";
        var output = await ApplyAsync(panel);

        using var result = new MagickImage(output);
        Assert.Equal("Ana Nueva", result.GetExifProfile()!.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal("Camara", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
        AssertSamePixels(photo, output);
        Assert.Equal(0, PhotoMetadata.Read(photo).Tags);
    }

    [AvaloniaFact]
    public async Task A_progressive_photo_is_rewritten_without_touching_its_scans()
    {
        var photo = Path.Combine(_root, "progresiva.jpg");
        using (var image = new MagickImage("plasma:orange-purple", 500, 400))
        {
            image.Strip();
            image.Settings.Interlace = Interlace.Jpeg;
            image.Write(photo);
        }
        Assert.Contains(MetadataJpeg.Parse(File.ReadAllBytes(photo)).Segments, s => s.Marker == 0xC2);   // SOF2: progressive
        var tagged = Path.Combine(_root, "progresiva etiquetada.jpg");
        PhotoMetadata.Edit(photo, tagged, new Dictionary<PhotoMetadataField, string?> { [PhotoMetadataField.Author] = "Alguien Secreto", [PhotoMetadataField.Latitude] = "40.1", [PhotoMetadataField.Longitude] = "-3.2" });

        var panel = await OpenAsync(tagged);
        panel.IsEditMode = false;
        var output = await ApplyAsync(panel);
        AssertSamePixels(photo, output);
        AssertNoTrace(output, "Alguien");
        Assert.Equal(MetadataJpeg.Parse(File.ReadAllBytes(photo)).Segments.Count(s => s.Marker == 0xDA), MetadataJpeg.Parse(File.ReadAllBytes(output)).Segments.Count(s => s.Marker == 0xDA));
    }

    [AvaloniaFact]
    public async Task What_a_phone_adds_after_the_photo_is_kept_by_edit_and_removed_by_remove_all()
    {
        var path = Path.Combine(_root, "movil.jpg");
        var (file, firstEnd) = Samples.MultiPicture();
        File.WriteAllBytes(path, file);
        var secondary = file.Length - firstEnd;

        var panel = await OpenAsync(path);
        Assert.Contains(panel.Notices, n => n.Contains("Se conservan tal cual"));      // Edit says they stay…
        panel.Field("Author")!.Text = "Ana Nueva con un nombre mucho mas largo";
        var edited = await ApplyAsync(panel);
        var bytes = File.ReadAllBytes(edited);
        var jpeg = MetadataJpeg.Parse(bytes);
        Assert.Equal(secondary, jpeg.Trailer.Length);                         // kept as it was
        // The multi-picture index still finds the second picture, and says how long the first one now is.
        var (entries, headerAt) = Samples.ReadMultiPicture(bytes);
        Assert.Equal((uint)(bytes.Length - jpeg.Trailer.Length), entries[0].Size);
        Assert.Equal(0u, entries[0].Offset);
        Assert.Equal((uint)secondary, entries[1].Size);
        Assert.True(bytes.AsSpan((int)(headerAt + entries[1].Offset), 3).SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }));

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        Assert.Contains(panel.Notices, n => n.Contains("Se quitan también"));          // …and Remove all says they go
        var stripped = await ApplyAsync(panel);
        var clean = MetadataJpeg.Parse(File.ReadAllBytes(stripped));
        Assert.Empty(clean.Trailer);
        Assert.DoesNotContain(clean.Segments, s => s.IsMpf);
        AssertSamePixels(path, stripped);
    }

    [AvaloniaFact]
    public async Task An_exif_that_would_not_fit_in_its_segment_keeps_the_tags_and_loses_the_thumbnail()
    {
        // A 62 KB thumbnail leaves no room for the new IFD0 in a 64 KB segment.
        var path = Path.Combine(_root, "grande.jpg");
        var big = new byte[62000];
        Random.Shared.NextBytes(big);
        var thumbnail = Samples.SmallJpeg().Concat(big).ToArray();
        var main = new IfdSpec { ThumbnailPicture = thumbnail };
        main.Tags.Add(new TagSpec(0x010F, 2, Samples.Ascii("MarcaSecreta")));
        main.Tags.Add(new TagSpec(0x013B, 2, Samples.Ascii("Persona Autora")));
        File.WriteAllBytes(path, Samples.WithSegments(Samples.Jpeg(), [Samples.ExifSegment(Samples.BuildTiff(main, true))]));

        var panel = await OpenAsync(path);
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.Field("Model")!.Text = "Modelo Largo Para Que Crezca";
        var output = await ApplyAsync(panel);

        var jpeg = MetadataJpeg.Parse(File.ReadAllBytes(output));
        Assert.True(jpeg.ExifPayload()!.Length <= MetadataTiff.MaxJpegPayload);
        var read = PhotoMetadata.Read(output);
        Assert.Equal("Ana Nueva", read.Values[PhotoMetadataField.Author]);
        Assert.Equal("MarcaSecreta", read.Values[PhotoMetadataField.Make]);
        AssertSamePixels(path, output);
    }

    [AvaloniaFact]
    public async Task An_xmp_that_cannot_be_read_is_dropped_when_a_field_it_may_hold_changes()
    {
        var path = Path.Combine(_root, "xmp roto.jpg");
        var broken = Samples.Segment(0xE1, [.. Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0"), .. Encoding.UTF8.GetBytes("<x:xmpmeta><dc:creator>Alguien Secreto</dc:creator>")]);
        File.WriteAllBytes(path, Samples.WithSegments(Samples.Jpeg(), [broken]));
        var panel = await OpenAsync(path);
        panel.Field("Author")!.Text = "Ana Nueva";
        var output = await ApplyAsync(panel);
        AssertNoTrace(output, "Alguien");
        Assert.DoesNotContain(MetadataJpeg.Parse(File.ReadAllBytes(output)).Segments, s => s.IsXmp);
    }

    [AvaloniaFact]
    public async Task An_existing_name_gets_a_number_and_the_original_is_never_overwritten()
    {
        var photo = Photo();
        var first = await ApplyAsync(await EditAuthor(photo));
        var second = await ApplyAsync(await EditAuthor(photo));
        Assert.Equal(Path.Combine(_root, "camara metadatos.jpg"), first);
        Assert.Equal(Path.Combine(_root, "camara metadatos 2.jpg"), second);
        var panel = await OpenAsync(photo);
        panel.IsEditMode = false;
        Assert.Equal(Path.Combine(_root, "camara sin metadatos.jpg"), await ApplyAsync(panel));
        panel = await OpenAsync(photo);
        panel.IsEditMode = false;
        Assert.Equal(Path.Combine(_root, "camara sin metadatos 2.jpg"), await ApplyAsync(panel));
    }

    private async Task<MetadataPanel> EditAuthor(string file)
    {
        var panel = await OpenAsync(file);
        panel.Field("Author")!.Text = "Ana Nueva";
        return panel;
    }

    [AvaloniaFact]
    public async Task Several_photos_get_the_same_change_and_a_failure_saves_nothing()
    {
        var one = Photo("uno.jpg");
        var two = Photo("dos.jpg");
        var panel = await OpenAsync(one, two);
        panel.Field("Author")!.Text = "Ana Nueva";
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(2, panel.Outputs.Count);
        foreach (var output in panel.Outputs)
            Assert.Equal("Ana Nueva", PhotoMetadata.Read(output).Values[PhotoMetadataField.Author]);

        // The second file becomes unreadable between opening the panel and Apply: the first copy must not stay.
        var three = Photo("tres.jpg");
        var four = Photo("cuatro.jpg");
        var again = await OpenAsync(three, four);
        again.Field("Author")!.Text = "Ana Nueva";
        File.WriteAllBytes(four, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A]);
        await again.RunAsync();
        Assert.False(again.IsDone);
        Assert.True(again.HasError);
        Assert.False(File.Exists(Path.Combine(_root, "tres metadatos.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "cuatro metadatos.jpg")));
    }

    [AvaloniaFact]
    public async Task Nothing_to_write_or_a_text_that_is_not_valid_keeps_apply_off()
    {
        var panel = await OpenAsync(Photo());
        Assert.False(panel.CanApply);                                        // edit with nothing touched
        panel.Field("Date")!.Text = "ayer";
        Assert.False(panel.CanApply);
        Assert.Equal("Escribe la fecha así: 2024-05-17 10:20:30.", panel.ValidationMessage);
        panel.Field("Date")!.Text = "2024-13-45 99:00:00";
        Assert.False(panel.CanApply);
        panel.Field("Date")!.Restore();
        Assert.True(panel.ValidationMessage is null && !panel.CanApply);

        panel.Field("Latitude")!.Text = "95";                                // beyond the pole
        Assert.False(panel.CanApply);
        panel.Field("Latitude")!.Text = "40.5";
        Assert.True(panel.CanApply);
        panel.IsEditMode = false;                                            // "Remove all" needs nothing typed
        Assert.True(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task A_position_needs_both_numbers()
    {
        var path = Path.Combine(_root, "sin sitio.jpg");
        File.WriteAllBytes(path, Samples.Jpeg());
        var panel = await OpenAsync(path);
        panel.Field("Latitude")!.Text = "40.4";
        Assert.False(panel.CanApply);
        Assert.Equal("Una ubicación necesita latitud y longitud.", panel.ValidationMessage);
        panel.Field("Longitude")!.Text = "-3.7";
        Assert.True(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task The_pills_are_one_choice()
    {
        var panel = await OpenAsync(Photo());
        Assert.True(panel.IsEditMode);
        Assert.False(panel.IsRemoveMode);
        panel.IsRemoveMode = true;
        Assert.False(panel.IsEditMode);
        panel.IsEditMode = true;
        Assert.True(panel.IsEditMode && !panel.IsRemoveMode);
    }

    [AvaloniaFact]
    public async Task A_cancelled_run_leaves_nothing_behind()
    {
        var panel = await OpenAsync(Photo());
        panel.Field("Author")!.Text = "Ana Nueva";
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => panel.ApplyAsync(new Progress<double>(), cancel.Token));
        Assert.Empty(Directory.GetFiles(_root, "* metadatos*"));
    }

    // ───────── The other photo formats ─────────

    private string PngWithTags(string name = "captura.png")
    {
        var path = Path.Combine(_root, name);
        using var image = new MagickImage("plasma:orange-purple", 320, 200);
        image.Strip();
        var png = MetadataPng.Parse(image.ToByteArray(MagickFormat.Png));
        var jpeg = MetadataJpeg.Parse(Samples.PhoneJpeg(true));
        var at = png.Chunks.FindIndex(c => c.Type == "IDAT");
        png.Chunks.Insert(at, MetadataPng.ExifChunk(jpeg.ExifPayload()!));
        png.Chunks.Insert(at, MetadataPng.InternationalText(MetadataPng.XmpKeyword, jpeg.XmpPayload()!));
        png.Chunks.Insert(at, MetadataPng.TextChunk("Author", "Persona Autora"));
        png.Chunks.Insert(at, MetadataPng.TextChunk("Description", "Paseo secreto por Atlantis"));
        png.Chunks.Insert(at, MetadataPng.TextChunk("Software", "Software Secreto 1.0"));
        png.Chunks.Insert(at, MetadataPng.TextChunk("Creation Time", "2024-05-17T10:20:30"));
        png.Chunks.Insert(at, new MetadataPngChunk("tIME", [0x07, 0xE8, 5, 17, 10, 20, 30]));
        png.Chunks.Insert(at, new MetadataPngChunk("prVW", Encoding.ASCII.GetBytes("Preview Secreto")));
        File.WriteAllBytes(path, png.ToBytes());
        return path;
    }

    [AvaloniaFact]
    public async Task A_png_is_edited_and_stripped_chunk_by_chunk()
    {
        var png = PngWithTags();
        var panel = await OpenAsync(png);
        Assert.Equal("Persona Autora", panel.Field("Author")!.Text);
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.Field("Date")!.Text = "2025-01-02 03:04:05";
        panel.RemoveLocation();
        var edited = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "captura metadatos.png"), edited);
        AssertSamePixels(png, edited);
        AssertNoTrace(edited, "Autor01", "Atlantis Secreta", "2024-05-17", "20240517", "40,25.08N");
        Assert.DoesNotContain(MetadataPng.Parse(File.ReadAllBytes(edited)).Chunks, c => c.Type == "tIME");
        Assert.Equal("Ana Nueva", PhotoMetadata.Read(edited).Values[PhotoMetadataField.Author]);
        Assert.Contains(MetadataPng.Parse(File.ReadAllBytes(edited)).Chunks, c => c.Type == "prVW");   // what was not touched stays

        panel = await OpenAsync(png);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        AssertSamePixels(png, stripped);
        AssertNoTrace(stripped, [.. PhoneSecrets, "2024", "Software", "Preview"]);
        var types = MetadataPng.Parse(File.ReadAllBytes(stripped)).Chunks.Select(c => c.Type).ToList();
        Assert.All(types, t => Assert.Contains(t, new[] { "IHDR", "IDAT", "IEND", "eXIf" }));
        Assert.Equal(1, PhotoMetadata.Read(stripped).Tags);                  // only the orientation
    }

    [AvaloniaFact]
    public async Task An_animated_png_keeps_every_frame_when_its_tags_go()
    {
        var path = PngWithTags("animada.png");
        // A made-up animation: control chunks and frame data that no decoder here reads but that must come through untouched.
        var png = MetadataPng.Parse(File.ReadAllBytes(path));
        var first = png.Chunks.FindIndex(c => c.Type == "IDAT");
        png.Chunks.Insert(first, new MetadataPngChunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]));
        png.Chunks.Insert(first + 1, new MetadataPngChunk("fcTL", Enumerable.Range(0, 26).Select(i => (byte)i).ToArray()));
        var iend = png.Chunks.FindIndex(c => c.Type == "IEND");
        png.Chunks.Insert(iend, new MetadataPngChunk("fcTL", Enumerable.Range(26, 26).Select(i => (byte)i).ToArray()));
        png.Chunks.Insert(iend + 1, new MetadataPngChunk("fdAT", Enumerable.Range(100, 300).Select(i => (byte)i).ToArray()));
        File.WriteAllBytes(path, png.ToBytes());

        var panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        var kept = MetadataPng.Parse(File.ReadAllBytes(stripped)).Chunks;
        var original = png.Chunks.Where(c => c.Type is "acTL" or "fcTL" or "fdAT" or "IDAT").ToList();
        var now = kept.Where(c => c.Type is "acTL" or "fcTL" or "fdAT" or "IDAT").ToList();
        Assert.Equal(original.Count, now.Count);
        for (var i = 0; i < original.Count; i++)
            Assert.Equal(original[i].Data, now[i].Data);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_webp_gets_its_exif_without_being_encoded_again(bool lossless)
    {
        var path = Path.Combine(_root, $"web-{lossless}.webp");
        using (var image = new MagickImage("plasma:orange-purple", 320, 200))
        {
            image.Strip();
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", lossless ? "true" : "false");
            image.Write(path);
        }
        var panel = await OpenAsync(path);
        panel.Field("Author")!.Text = "Persona Autora";
        panel.Field("Latitude")!.Text = "40.4168";
        panel.Field("Longitude")!.Text = "-3.7038";
        var tagged = await ApplyAsync(panel);
        Assert.Equal(Path.Combine(_root, $"web-{lossless} metadatos.webp"), tagged);
        AssertSamePixels(path, tagged);
        // The compressed picture is the very same chunk.
        string Picture(string file) => Convert.ToHexString(MetadataWebp.Parse(File.ReadAllBytes(file)).Chunks.First(c => c.Type is "VP8 " or "VP8L").Data);
        Assert.Equal(Picture(path), Picture(tagged));
        using (var result = new MagickImage(tagged))
            Assert.Equal("Persona Autora", result.GetExifProfile()!.GetValue(ExifTag.Artist)!.Value);

        panel = await OpenAsync(tagged);
        panel.IsEditMode = false;
        var clean = await ApplyAsync(panel);
        AssertNoTrace(clean, "Autor01");
        Assert.DoesNotContain(MetadataWebp.Parse(File.ReadAllBytes(clean)).Chunks, c => c.Type is "EXIF" or "XMP ");
        Assert.Equal(Picture(path), Picture(clean));
        AssertSamePixels(path, clean);
    }

    [AvaloniaFact]
    public async Task An_animated_gif_keeps_its_frames_and_timing_when_the_comment_goes()
    {
        var path = Path.Combine(_root, "animado.gif");
        using (var frames = new MagickImageCollection())
        {
            for (var i = 0; i < 3; i++)
            {
                var frame = new MagickImage(i % 2 == 0 ? MagickColors.Red : MagickColors.Blue, 60, 40) { AnimationDelay = (uint)(10 + i) };
                frames.Add(frame);
            }
            frames[0].SetAttribute("comment", "Comentario secreto del autor");
            frames.Write(path);
        }
        var panel = await OpenAsync(path);
        Assert.Equal("Comentario secreto del autor", panel.Field("Description")!.Text);
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.Field("Description")!.Text = "Nueva descripcion";
        var edited = await ApplyAsync(panel);
        AssertNoTrace(edited, "Comentario secreto");
        var read = PhotoMetadata.Read(edited);
        Assert.Equal("Ana Nueva", read.Values[PhotoMetadataField.Author]);
        Assert.Equal("Nueva descripcion", read.Values[PhotoMetadataField.Description]);

        panel = await OpenAsync(edited);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        AssertNoTrace(stripped, "Ana Nueva", "Nueva descripcion", "XMP Data");
        using var a = new MagickImageCollection(path);
        using var b = new MagickImageCollection(stripped);
        Assert.Equal(3, b.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0d, a[i].Compare(b[i], ErrorMetric.Absolute));
            Assert.Equal(a[i].AnimationDelay, b[i].AnimationDelay);
        }
        Assert.Equal(a[0].AnimationIterations, b[0].AnimationIterations);
    }

    [AvaloniaFact]
    public async Task A_multi_page_tiff_is_edited_in_place_and_every_page_is_cleaned()
    {
        var path = Path.Combine(_root, "paginas.tif");
        using (var pages = new MagickImageCollection())
        {
            for (var i = 0; i < 3; i++)
                pages.Add(new MagickImage(new MagickColor((byte)(i * 80), 40, 200), 50, 40));
            pages.Write(path);
        }
        var panel = await OpenAsync(path);
        panel.Field("Author")!.Text = "Persona Autora";
        panel.Field("Make")!.Text = "MarcaSecreta";
        panel.Field("Latitude")!.Text = "40.4168";
        panel.Field("Longitude")!.Text = "-3.7038";
        panel.Field("Date")!.Text = "2024-05-17 10:20:30";
        var tagged = await ApplyAsync(panel);
        Assert.Equal("Persona Autora", PhotoMetadata.Read(tagged).Values[PhotoMetadataField.Author]);
        using (var a = new MagickImageCollection(path))
        using (var b = new MagickImageCollection(tagged))
        {
            Assert.Equal(3, b.Count);
            for (var i = 0; i < 3; i++)
                Assert.Equal(0d, a[i].Compare(b[i], ErrorMetric.Absolute));
            Assert.Equal("Persona Autora", b[0].GetAttribute("tiff:artist"));       // libtiff reads the tags the tool wrote
            Assert.Equal("MarcaSecreta", b[0].GetAttribute("tiff:make"));
        }

        panel = await OpenAsync(tagged);
        panel.IsEditMode = false;
        var clean = await ApplyAsync(panel);
        AssertNoTrace(clean, "Autor01", "MarcaSecreta", "2024:05:17");
        Assert.Equal(0, PhotoMetadata.Read(clean).Tags);
        using var original = new MagickImageCollection(path);
        using var written = new MagickImageCollection(clean);
        Assert.Equal(3, written.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal(0d, original[i].Compare(written[i], ErrorMetric.Absolute));
        Assert.Null(written[0].GetAttribute("tiff:artist"));
        Assert.Null(written[0].GetAttribute("exif:GPSLatitude"));
    }

    [AvaloniaFact]
    public async Task A_bmp_has_no_tags_so_remove_all_copies_it_and_edit_is_off()
    {
        var path = Path.Combine(_root, "mapa.bmp");
        using (var image = new MagickImage("plasma:orange-purple", 100, 80))
            image.Write(path);
        var panel = await OpenAsync(path);
        Assert.True(panel.EditUnavailable);
        Assert.False(panel.CanApply);
        Assert.Contains(panel.Notices, n => n.Contains("BMP"));
        panel.IsEditMode = false;
        Assert.True(panel.CanApply);
        var copy = await ApplyAsync(panel);
        Assert.Equal(Path.Combine(_root, "mapa sin metadatos.bmp"), copy);
        Assert.Equal(Hash(path), Hash(copy));
    }

    [AvaloniaFact]
    public async Task An_avif_is_encoded_again_with_only_the_tags_that_stay()
    {
        var path = Path.Combine(_root, "foto.avif");
        var source = Path.Combine(_root, "origen.jpg");
        File.WriteAllBytes(source, Samples.PhoneJpeg(true));
        using (var image = new MagickImage(source))
            image.Write(path);                                               // Magick keeps the EXIF and XMP as profiles of the AVIF
        var panel = await OpenAsync(path);
        Assert.Contains(panel.Notices, n => n.Contains("AVIF"));
        Assert.Equal("Persona Autora", panel.Field("Author")!.Text);
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.RemoveLocation();
        var edited = await ApplyAsync(panel);
        Assert.Equal(Path.Combine(_root, "foto metadatos.avif"), edited);
        var bytes = File.ReadAllBytes(edited);
        Assert.False(Has(bytes, "Persona Autora"));
        Assert.False(Has(bytes, "Atlantis Secreta"));
        Assert.Equal(0, Count(bytes, [0xFF, 0xD8, 0xFF]));                    // no thumbnail and no maker note preview of the picture as it was
        Assert.Equal("Ana Nueva", PhotoMetadata.Read(edited).Values[PhotoMetadataField.Author]);

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var clean = await ApplyAsync(panel);
        AssertNoTrace(clean, [.. PhoneSecrets, "2024"]);
        Assert.Equal(0, PhotoMetadata.Read(clean).Tags);
    }

    [AvaloniaFact]
    public async Task A_photo_with_the_wrong_extension_is_handled_by_what_it_really_is()
    {
        // A PNG saved as .jpg (it happens with downloads).
        var png = PngWithTags("verdadero.png");
        var disguised = Path.Combine(_root, "disfrazado.jpg");
        File.Copy(png, disguised);
        var panel = await OpenAsync(disguised);
        panel.IsEditMode = false;
        var output = await ApplyAsync(panel);
        Assert.Equal(Path.Combine(_root, "disfrazado sin metadatos.jpg"), output);
        Assert.True(MetadataPng.LooksLikePng(File.ReadAllBytes(output)));
        AssertNoTrace(output, "Autor01", "Secreto");
    }

    // ───────── Audio and video ─────────

    /// <summary>The MD5 of the packets of the streams of a kind, copied as they are: equal MD5 means the same bits.</summary>
    private static async Task<string> PacketsAsync(string path, string streams)
    {
        var media = MediaTools.Locate()!;
        var result = await Filee.Engines.Infrastructure.ProcessRunner.RunAsync(media.Ffmpeg,
            ["-hide_banner", "-v", "error", "-i", path, "-map", streams, "-c", "copy", "-f", "md5", "-"], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput.Trim();
    }

    private static async Task<System.Text.Json.JsonDocument> ProbeAsync(string path)
    {
        var media = MediaTools.Locate()!;
        var result = await Filee.Engines.Infrastructure.ProcessRunner.RunAsync(media.Ffprobe,
            ["-hide_banner", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return System.Text.Json.JsonDocument.Parse(result.StandardOutput);
    }

    /// <summary>Every tag of the file (container, streams, chapters) that says something about people, places or times.</summary>
    private static Dictionary<string, string> UserTags(System.Text.Json.JsonDocument probe)
    {
        var technical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "major_brand", "minor_version", "compatible_brands", "language", "handler_name", "vendor_id", "duration", "number_of_frames", "number_of_bytes", "bps",
            "_statistics_writing_app", "_statistics_writing_date_utc", "_statistics_tags",
        };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Collect(System.Text.Json.JsonElement element, string where)
        {
            if (!element.TryGetProperty("tags", out var tags))
                return;
            foreach (var tag in tags.EnumerateObject())
                if (!technical.Contains(tag.Name) && !string.IsNullOrEmpty(tag.Value.GetString()))
                    result[where + tag.Name] = tag.Value.GetString() ?? "";
        }
        Collect(probe.RootElement.GetProperty("format"), "format:");
        foreach (var stream in probe.RootElement.GetProperty("streams").EnumerateArray())
            Collect(stream, $"stream{stream.GetProperty("index").GetInt32()}:");
        foreach (var chapter in probe.RootElement.GetProperty("chapters").EnumerateArray())
            Collect(chapter, "chapter:");
        return result;
    }

    private static string? Tag(System.Text.Json.JsonDocument probe, string name)
    {
        foreach (var element in new[] { probe.RootElement.GetProperty("format") }.Concat(probe.RootElement.GetProperty("streams").EnumerateArray()))
            if (element.TryGetProperty("tags", out var tags))
                foreach (var tag in tags.EnumerateObject())
                    if (tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return tag.Value.GetString();
        return null;
    }

    private static string[] SongSecrets => ["Cancion Secreta", "Artista Secreto", "Album Secreto", "Comentario Secreto", "Rock", "2023", "Lavf"];

    [AvaloniaFact]
    public async Task Editing_a_song_changes_the_tags_and_copies_the_audio_byte_for_byte()
    {
        var song = await SongAsync();
        if (song is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var before = Hash(song);
        var original = MetadataId3.Read(song);
        var panel = await OpenAsync(song);
        panel.Field("Title")!.Text = "Titulo Nuevo";
        panel.Field("Artist")!.Text = "Artista Nuevo";
        panel.Field("Comment")!.Text = "Comentario Nuevo";
        panel.Field("Genre")!.ToggleRemoved();
        panel.Field("Date")!.Text = "2020-02-03";
        var output = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "cancion metadatos.mp3"), output);
        Assert.Equal(before, Hash(song));
        var written = MetadataId3.Read(output);
        // The audio frames are the original bytes.
        var a = File.ReadAllBytes(song).AsSpan((int)original.AudioStart, (int)(original.AudioEnd - original.AudioStart));
        var b = File.ReadAllBytes(output).AsSpan((int)written.AudioStart, (int)(written.AudioEnd - written.AudioStart));
        Assert.True(a.SequenceEqual(b), "the audio frames changed");
        Assert.Equal("Titulo Nuevo", written.Text("TIT2"));
        Assert.Equal("Artista Nuevo", written.Text("TPE1"));
        Assert.Equal("Album Secreto", written.Text("TALB"));                  // not touched
        Assert.Null(written.Text("TCON"));                                    // removed
        Assert.Equal("Comentario Nuevo", written.Comment());
        Assert.Contains(written.Frames, f => f.Id == "COMM");                 // a real comment frame, the one Windows and iTunes read
        Assert.DoesNotContain(written.Frames, f => f.Id == "TXXX");           // and not FFmpeg's TXXX stand-in for it
        Assert.Equal("2020-02-03", written.Date());
        Assert.Equal("3/12", written.Text("TRCK"));
        Assert.True(written.HasPicture);                                      // the cover stays
        var bytes = File.ReadAllBytes(output);
        foreach (var gone in new[] { "Cancion Secreta", "Artista Secreto", "Comentario Secreto", "Rock", "2023" })
            Assert.False(Has(bytes, gone), gone);

        // ffprobe agrees, and sees the cover as a picture stream.
        using var probe = await ProbeAsync(output);
        Assert.Equal("Titulo Nuevo", Tag(probe, "title"));
        Assert.Contains(probe.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("codec_type").GetString() == "video");
    }

    [AvaloniaFact]
    public async Task A_song_keeps_the_frames_the_tool_does_not_edit()
    {
        var source = await SongAsync("base.mp3");
        if (source is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        // A tag with a rating, lyrics and private data, as a music library writes them.
        var id3 = MetadataId3.Read(source);
        var frames = id3.Frames.Where(f => f.Id != "TXXX").ToList();
        var rating = new MetadataId3Frame("POPM", 0, [.. "user@example.com"u8.ToArray(), 0, 196, 0, 0, 0, 7]);
        var lyrics = new MetadataId3Frame("USLT", 0, [0, .. "eng"u8.ToArray(), 0, .. "Letra de la cancion"u8.ToArray()]);
        var private1 = new MetadataId3Frame("PRIV", 0, [.. "WM/Provider"u8.ToArray(), 0, 1, 2, 3, 4]);
        frames.AddRange([rating, lyrics, private1]);
        var path = Path.Combine(_root, "biblioteca.mp3");
        var audio = File.ReadAllBytes(source).AsSpan((int)id3.AudioStart, (int)(id3.AudioEnd - id3.AudioStart)).ToArray();
        File.WriteAllBytes(path, [.. MetadataId3.Build(3, frames), .. audio]);

        var panel = await OpenAsync(path);
        panel.Field("Title")!.Text = "Otro Titulo";
        var output = await ApplyAsync(panel);
        var written = MetadataId3.Read(output);
        Assert.Equal(rating.Body, written.Frames.First(f => f.Id == "POPM").Body);
        Assert.Equal(lyrics.Body, written.Frames.First(f => f.Id == "USLT").Body);
        Assert.Equal(private1.Body, written.Frames.First(f => f.Id == "PRIV").Body);
        Assert.Equal("Otro Titulo", written.Text("TIT2"));
    }

    [AvaloniaFact]
    public async Task The_old_copies_of_the_tags_at_the_end_of_an_mp3_are_updated_or_removed()
    {
        var source = await SongAsync("base.mp3");
        if (source is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var id3 = MetadataId3.Read(source);
        var audio = File.ReadAllBytes(source).AsSpan((int)id3.AudioStart, (int)(id3.AudioEnd - id3.AudioStart)).ToArray();
        var v1 = new MetadataId3V1 { Title = "Cancion Secreta", Artist = "Artista Secreto", Album = "Album Secreto", Year = "2023", Comment = "Comentario Secreto", Track = 3, Genre = 17 };
        var ape = new MetadataApeTag();
        ape.Set("Title", "Cancion Secreta");
        ape.Set("Artist", "Artista Secreto");
        ape.Set("REPLAYGAIN_TRACK_GAIN", "-6.50 dB");
        var path = Path.Combine(_root, "viejo.mp3");
        File.WriteAllBytes(path, [.. MetadataId3.Build(3, id3.Frames), .. audio, .. ape.ToBytes(), .. v1.ToBytes()]);
        var read = MetadataId3.Read(path);
        Assert.NotNull(read.V1);
        Assert.NotNull(read.Ape);

        var panel = await OpenAsync(path);
        panel.Field("Title")!.Text = "Titulo Nuevo";
        var edited = await ApplyAsync(panel);
        var bytes = File.ReadAllBytes(edited);
        Assert.False(Has(bytes, "Cancion Secreta"));                          // not in the v2 tag, the v1 tag or the APE tag
        var after = MetadataId3.Read(edited);
        Assert.Equal("Titulo Nuevo", after.V1!.Title);
        Assert.Equal("Artista Secreto", after.V1!.Artist);                    // not touched
        Assert.Equal("Titulo Nuevo", after.Ape!.Text("Title"));
        Assert.Equal("-6.50 dB", after.Ape!.Text("REPLAYGAIN_TRACK_GAIN"));   // other items of the APE tag stay
        Assert.Equal(read.AudioEnd - read.AudioStart, after.AudioEnd - after.AudioStart);

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        AssertNoTrace(stripped, SongSecrets);
        var clean = MetadataId3.Read(stripped);
        Assert.Null(clean.V1);
        Assert.Null(clean.Ape);
        Assert.Empty(clean.Frames);
        Assert.Equal(audio, File.ReadAllBytes(stripped));                     // nothing but the audio is left
        Assert.DoesNotContain("ID3", Encoding.ASCII.GetString(File.ReadAllBytes(stripped), 0, 3));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remove_all_leaves_the_audio_of_a_song_and_the_cover_only_if_asked(bool removeCover)
    {
        var song = await SongAsync();
        if (song is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var id3 = MetadataId3.Read(song);
        var panel = await OpenAsync(song);
        panel.IsEditMode = false;
        Assert.True(panel.ShowCoverOption);
        Assert.True(panel.RemoveCover);                                       // marked by default
        panel.RemoveCover = removeCover;
        var output = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "cancion sin metadatos.mp3"), output);
        var written = MetadataId3.Read(output);
        var a = File.ReadAllBytes(song).AsSpan((int)id3.AudioStart, (int)(id3.AudioEnd - id3.AudioStart));
        var b = File.ReadAllBytes(output).AsSpan((int)written.AudioStart, (int)(written.AudioEnd - written.AudioStart));
        Assert.True(a.SequenceEqual(b), "the audio frames changed");
        AssertNoTrace(output, SongSecrets);
        Assert.Equal(!removeCover, written.HasPicture);
        Assert.Equal(removeCover ? 0 : 1, written.Frames.Count);              // the cover is the only frame, or there is no tag
        using var probe = await ProbeAsync(output);
        Assert.DoesNotContain(UserTags(probe), t => !t.Key.StartsWith("stream1:", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_cover_that_stays_loses_the_metadata_of_its_own()
    {
        // A cover that is a camera photo, with the GPS of where it was taken.
        var cover = Path.Combine(_root, "cover-con-gps.jpg");
        File.WriteAllBytes(cover, Samples.PhoneJpeg(true));
        var song = await MakeMediaAsync(Path.Combine(_root, "portada.mp3"), "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-i", cover, "-map", "0:a", "-map", "1:v",
            "-c:a", "libmp3lame", "-c:v", "copy", "-id3v2_version", "3", "-disposition:v", "attached_pic", "-metadata", "title=Portada");
        if (song is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        Assert.True(Has(File.ReadAllBytes(song), "Atlantis Secreta"));
        var panel = await OpenAsync(song);
        panel.IsEditMode = false;
        panel.RemoveCover = false;
        var output = await ApplyAsync(panel);
        AssertNoTrace(output, [.. PhoneSecrets, "Portada"]);
        var picture = MetadataId3.ReadPicture(MetadataId3.Read(output).Frames.Single(f => f.Id == "APIC"))!.Value;
        AssertSamePixels(cover, WriteTemp(picture.Data, ".jpg"));
    }

    private string WriteTemp(byte[] data, string extension)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + extension);
        File.WriteAllBytes(path, data);
        return path;
    }

    [AvaloniaFact]
    public async Task Text_with_accents_and_other_alphabets_survives_in_the_tags()
    {
        var song = await SongAsync();
        if (song is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = await OpenAsync(song);
        panel.Field("Title")!.Text = "Canción de niño";                       // fits Latin-1
        panel.Field("Artist")!.Text = "日本語の歌手";                          // needs UTF-16 in v2.3
        var output = await ApplyAsync(panel);
        var written = MetadataId3.Read(output);
        Assert.Equal("Canción de niño", written.Text("TIT2"));
        Assert.Equal("日本語の歌手", written.Text("TPE1"));
        using var probe = await ProbeAsync(output);
        Assert.Equal("Canción de niño", Tag(probe, "title"));
        Assert.Equal("日本語の歌手", Tag(probe, "artist"));
    }

    [AvaloniaTheory]
    [InlineData("m4a", "-c:a", "aac")]
    [InlineData("flac", "-c:a", "flac")]
    [InlineData("ogg", "-c:a", "libvorbis")]
    [InlineData("opus", "-c:a", "libopus")]
    [InlineData("wav", "-c:a", "pcm_s16le")]
    [InlineData("aiff", "-c:a", "pcm_s16be")]
    public async Task Other_audio_containers_are_edited_and_stripped_with_the_audio_copied(string extension, string codecOption, string codec)
    {
        var path = await MakeMediaAsync(Path.Combine(_root, $"sonido.{extension}"), "-f", "lavfi", "-i", "sine=frequency=440:duration=2", codecOption, codec,
            "-metadata", "title=Titulo Secreto", "-metadata", "artist=Artista Secreto", "-metadata", "album=Album Secreto", "-metadata", "comment=Comentario Secreto",
            "-metadata", "date=2023", "-metadata", "genre=Rock");
        if (path is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var before = Hash(path);
        var audio = await PacketsAsync(path, "0:a");
        var panel = await OpenAsync(path);
        Assert.Equal("Titulo Secreto", panel.Field("Title")!.Text);
        panel.Field("Title")!.Text = "Titulo Nuevo";
        panel.Field("Artist")!.Text = "Artista Nuevo";
        panel.Field("Comment")!.ToggleRemoved();
        var edited = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, $"sonido metadatos.{extension}"), edited);
        Assert.Equal(before, Hash(path));
        Assert.Equal(audio, await PacketsAsync(edited, "0:a"));               // not a bit of the audio changed
        using (var probe = await ProbeAsync(edited))
        {
            Assert.Equal("Titulo Nuevo", Tag(probe, "title"));
            Assert.Equal("Artista Nuevo", Tag(probe, extension is "aiff" ? "author" : "artist"));
            Assert.Null(Tag(probe, "comment"));
        }
        var bytes = File.ReadAllBytes(edited);
        Assert.False(Has(bytes, "Titulo Secreto"));
        Assert.False(Has(bytes, "Comentario Secreto"));

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        Assert.Equal(audio, await PacketsAsync(stripped, "0:a"));
        AssertNoTrace(stripped, "Titulo Secreto", "Artista Secreto", "Album Secreto", "Comentario Secreto", "Lavf");
        using var clean = await ProbeAsync(stripped);
        Assert.Empty(UserTags(clean));
    }

    [AvaloniaTheory]
    [InlineData("m4a")]
    [InlineData("flac")]
    public async Task The_cover_of_other_containers_goes_only_when_asked(string extension)
    {
        var cover = Path.Combine(_root, "portada.jpg");
        MakePhoto(cover, 200, 200);
        var path = await MakeMediaAsync(Path.Combine(_root, $"con portada.{extension}"), "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-i", cover, "-map", "0:a", "-map", "1:v",
            "-c:a", extension == "m4a" ? "aac" : "flac", "-c:v", "copy", "-disposition:v", "attached_pic", "-metadata", "title=Titulo Secreto");
        if (path is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = await OpenAsync(path);
        Assert.True(panel.ShowCoverOption);
        panel.IsEditMode = false;
        panel.RemoveCover = false;
        var kept = await ApplyAsync(panel);
        using (var probe = await ProbeAsync(kept))
            Assert.Equal(2, probe.RootElement.GetProperty("streams").GetArrayLength());

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var removed = await ApplyAsync(panel);                                // marked by default
        using (var probe = await ProbeAsync(removed))
            Assert.Equal(1, probe.RootElement.GetProperty("streams").GetArrayLength());
        Assert.False(Has(File.ReadAllBytes(removed), "Titulo Secreto"));
    }

    [AvaloniaTheory]
    [InlineData("mp4")]
    [InlineData("mov")]
    [InlineData("m4v")]
    [InlineData("mkv")]
    public async Task A_video_is_edited_and_stripped_without_recoding(string extension)
    {
        var path = await VideoAsync($"clip.{extension}");
        if (path is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var before = Hash(path);
        var video = await PacketsAsync(path, "0:v");
        var sound = await PacketsAsync(path, "0:a");
        var panel = await OpenAsync(path);
        Assert.Equal("Pelicula Secreta", panel.Field("Title")!.Text);
        panel.Field("Title")!.Text = "Titulo Nuevo";
        panel.RemoveLocation();
        panel.Field("Created")!.ToggleRemoved();
        panel.Field("Author")!.Text = "Autor Nuevo";
        var edited = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, $"clip metadatos.{extension}"), edited);
        Assert.Equal(before, Hash(path));
        Assert.Equal(video, await PacketsAsync(edited, "0:v"));               // the very same compressed frames
        Assert.Equal(sound, await PacketsAsync(edited, "0:a"));
        using (var probe = await ProbeAsync(edited))
        {
            Assert.Equal("Titulo Nuevo", Tag(probe, "title"));
            Assert.Equal("Autor Nuevo", Tag(probe, "artist"));
            Assert.Null(Tag(probe, "location"));
            Assert.Null(Tag(probe, "creation_time"));
            Assert.Equal("Comentario Secreto", Tag(probe, "comment"));        // not touched
        }
        var bytes = File.ReadAllBytes(edited);
        foreach (var gone in new[] { "Pelicula Secreta", "Autor Secreto", "+40.4168", "40.4168-003" })
            Assert.False(Has(bytes, gone), gone);

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        Assert.Equal(video, await PacketsAsync(stripped, "0:v"));
        Assert.Equal(sound, await PacketsAsync(stripped, "0:a"));
        AssertNoTrace(stripped, "Pelicula Secreta", "Autor Secreto", "Comentario Secreto", "+40.4168", "Lavf");
        using var clean = await ProbeAsync(stripped);
        Assert.Empty(UserTags(clean));
        Assert.Equal(2, clean.RootElement.GetProperty("streams").GetArrayLength());
    }

    [AvaloniaFact]
    public async Task Chapters_and_the_language_of_the_tracks_are_handled_as_expected()
    {
        var chapters = Path.Combine(_root, "capitulos.txt");
        File.WriteAllText(chapters, ";FFMETADATA1\ntitle=Con Capitulos\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Capitulo Secreto\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Otro Capitulo\n");
        var path = await MakeMediaAsync(Path.Combine(_root, "capitulos.mkv"), "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=330:duration=2",
            "-i", chapters, "-map", "0:v", "-map", "1:a", "-map_metadata", "2", "-map_chapters", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac",
            "-metadata:s:a:0", "language=spa", "-metadata:s:a:0", "title=Pista Secreta");
        if (path is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = await OpenAsync(path);
        panel.Field("Title")!.Text = "Titulo Nuevo";
        var edited = await ApplyAsync(panel);
        using (var probe = await ProbeAsync(edited))
            Assert.Equal(2, probe.RootElement.GetProperty("chapters").GetArrayLength());     // editing keeps the chapters

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        using var clean = await ProbeAsync(stripped);
        Assert.Equal(0, clean.RootElement.GetProperty("chapters").GetArrayLength());
        AssertNoTrace(stripped, "Capitulo Secreto", "Otro Capitulo", "Con Capitulos", "Pista Secreta");
        var audio = clean.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal("spa", audio.GetProperty("tags").GetProperty("language").GetString());   // how a player picks the track stays
    }

    // ───────── PDF ─────────

    private string PdfWithXmp(string name = "completo.pdf")
    {
        var path = Path.Combine(_root, name);
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        document.AddPage();
        document.Info.Title = "Titulo Secreto";
        document.Info.Author = "Autor Secreto";
        document.Info.Subject = "Asunto Secreto";
        document.Info.Keywords = "clave secreta";
        document.Info.Creator = "Creador Secreto";
        document.Info.Elements.SetString("/Company", "Empresa Secreta SL");
        var xmp = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Titulo Secreto</rdf:li></rdf:Alt></dc:title><dc:creator><rdf:Seq><rdf:li>Autor Secreto</rdf:li></rdf:Seq></dc:creator><pdf:Producer>Productor Secreto</pdf:Producer><xmp:CreatorTool>Creador Secreto</xmp:CreatorTool><xmp:CreateDate>2020-01-01T00:00:00</xmp:CreateDate></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var stream = new PdfSharp.Pdf.PdfDictionary(document);
        stream.Elements.SetName("/Type", "/Metadata");
        stream.Elements.SetName("/Subtype", "/XML");
        stream.CreateStream(Encoding.UTF8.GetBytes(xmp));
        document.Internals.AddObject(stream);
        document.Internals.Catalog.Elements.SetReference("/Metadata", stream);
        var pageMetadata = new PdfSharp.Pdf.PdfDictionary(document);
        pageMetadata.Elements.SetName("/Type", "/Metadata");
        pageMetadata.Elements.SetName("/Subtype", "/XML");
        pageMetadata.CreateStream(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><note>Pagina Secreta</note></x:xmpmeta>"));
        document.Internals.AddObject(pageMetadata);
        page.Elements.SetReference("/Metadata", pageMetadata);
        page.Elements.SetString("/LastModified", "D:20200101000000Z");
        var annotation = new PdfSharp.Pdf.PdfDictionary(document);
        annotation.Elements.SetName("/Type", "/Annot");
        annotation.Elements.SetName("/Subtype", "/Highlight");
        var rectangle = new PdfSharp.Pdf.PdfArray(document);
        foreach (var number in new[] { 10, 10, 100, 40 })
            rectangle.Elements.Add(new PdfSharp.Pdf.PdfInteger(number));
        annotation.Elements.SetObject("/Rect", rectangle);
        annotation.Elements.SetString("/T", "Comentarista Secreto");
        annotation.Elements.SetString("/Contents", "Texto del comentario");
        document.Internals.AddObject(annotation);
        var annotations = new PdfSharp.Pdf.PdfArray(document);
        annotations.Elements.Add(annotation.Reference!);
        page.Elements.SetObject("/Annots", annotations);
        document.Save(path);
        return path;
    }

    [AvaloniaFact]
    public async Task A_pdf_is_edited_without_the_stamps_of_the_library_that_wrote_it()
    {
        var path = PdfWithXmp();
        var before = Hash(path);
        var panel = await OpenAsync(path);
        Assert.Equal("Autor Secreto", panel.Field("Author")!.Text);
        panel.Field("Title")!.Text = "Título nuevo ñandú";
        panel.Field("Author")!.ToggleRemoved();
        panel.Field("Producer")!.Text = "Mi Productor";
        var output = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "completo metadatos.pdf"), output);
        Assert.Equal(before, Hash(path));
        var read = PdfMetadataEditor.Read(output);
        Assert.Equal("Título nuevo ñandú", read.Values[PdfMetadataField.Title]);
        Assert.False(read.Values.ContainsKey(PdfMetadataField.Author));
        Assert.Equal("Mi Productor", read.Values[PdfMetadataField.Producer]);         // as typed, not "PDFsharp (Original: …)"
        Assert.Equal("Creador Secreto", read.Values[PdfMetadataField.Creator]);       // not touched
        Assert.Equal("Asunto Secreto", read.Values[PdfMetadataField.Subject]);
        var bytes = File.ReadAllBytes(output);
        Assert.False(Has(bytes, "Titulo Secreto"));                           // not in the Info and not in the XMP the original had
        Assert.False(Has(bytes, "Autor Secreto"));
        Assert.False(Has(bytes, "Productor Secreto"));
        Assert.False(Has(bytes, "PDFsharp"));
        Assert.True(Has(bytes, "Empresa Secreta"));                           // custom Info entries stay
        Assert.True(Has(bytes, "Pagina Secreta"));                            // edit does not touch the rest of the document
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
    }

    [AvaloniaFact]
    public async Task Remove_all_leaves_a_pdf_with_no_info_no_xmp_and_no_names_on_the_comments()
    {
        var path = PdfWithXmp();
        var panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var output = await ApplyAsync(panel);

        Assert.Equal(Path.Combine(_root, "completo sin metadatos.pdf"), output);
        AssertNoTrace(output, "Titulo", "Autor", "Secreto", "Secreta", "PDFsharp", "xpacket", "Producer", "Creator", "2020", "LastModified", "Metadata", "Comentarista");
        Assert.True(Has(File.ReadAllBytes(output), "Texto del comentario"));  // the comment itself stays
        Assert.Equal(0, PdfMetadataEditor.Read(output).Tags);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(PdfPage(path), PdfPage(output));                          // the pages look the same
    }

    private static string PdfPage(string path)
    {
        using var stream = File.OpenRead(path);
        using var bitmap = PDFtoImage.Conversion.ToImage(stream, page: 0, options: new PDFtoImage.RenderOptions(Dpi: 40));
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.ToArray()));
    }

    [AvaloniaFact]
    public async Task A_pdf_made_from_a_phone_photo_loses_the_tags_of_the_photo_inside()
    {
        // PDFsharp (like Mandarina's own "PDF from photos") embeds each JPEG as it came, EXIF and GPS included.
        var path = Path.Combine(_root, "con foto.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
            {
                using var image = PdfSharp.Drawing.XImage.FromStream(new MemoryStream(Samples.PhoneJpeg(true)));
                graphics.DrawImage(image, 20, 20, 300, 225);
            }
            document.Save(path);
        }
        Assert.True(Has(File.ReadAllBytes(path), "Persona Autora"));

        var panel = await OpenAsync(path);
        panel.Field("Title")!.Text = "Solo el titulo";
        var edited = await ApplyAsync(panel);
        Assert.True(Has(File.ReadAllBytes(edited), "Persona Autora"));   // editing the document's own tags leaves the photo alone

        panel = await OpenAsync(path);
        panel.IsEditMode = false;
        var stripped = await ApplyAsync(panel);
        AssertNoTrace(stripped, [.. PhoneSecrets, "2024", "xmpGImg", "Comentario", "GPS"]);
        Assert.Equal(PdfPage(path), PdfPage(stripped));                       // the page looks exactly the same
    }

    [AvaloniaFact]
    public async Task A_password_protected_pdf_says_so_and_cannot_be_applied()
    {
        var path = Path.Combine(_root, "protegido.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.OwnerPassword = "dueño";
            document.SecuritySettings.UserPassword = "usuario";
            document.Save(path);
        }
        var panel = new MetadataPanel(Context(path));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal("Este PDF tiene contraseña. Quita la protección e inténtalo de nuevo.", panel.LoadError);
        Assert.False(panel.CanApply);
    }

    // ───────── The engines on their own ─────────

    private static MetadataTiff TiffOf(byte[] block)
    {
        var stream = new MemoryStream();
        stream.Write(block);
        return MetadataTiff.Open(stream)!;
    }

    [Fact]
    public void Removing_tags_from_an_exif_overwrites_their_bytes_with_zeros_in_place()
    {
        var main = new IfdSpec();
        main.Tags.Add(new TagSpec(0x010F, 2, Samples.Ascii("MarcaSecreta")));
        main.Tags.Add(new TagSpec(0x013B, 2, Samples.Ascii("Autor Secreto Largo")));
        var gps = new IfdSpec();
        gps.Tags.Add(new TagSpec(0x0001, 2, Samples.Ascii("N")));
        gps.Tags.Add(new TagSpec(0x0002, 5, Samples.Rational(true, (40, 1), (25, 1), (4800, 10000))));
        var withGps = new IfdSpec { Gps = gps };
        withGps.Tags.AddRange(main.Tags);
        var block = Samples.BuildTiff(withGps, true);
        var tiff = TiffOf(block);
        Assert.True(tiff.HasGpsIfd);

        tiff.Remove(MetadataTiffIfd.Main, MetadataTiff.Artist);
        tiff.RemoveGps();
        tiff.Commit();
        var after = tiff.ToArray();

        Assert.False(Has(after, "Autor Secreto"));
        Assert.True(Has(after, "MarcaSecreta"));                              // what stays stays
        var reread = TiffOf(after);
        Assert.False(reread.HasGpsIfd);
        Assert.Null(reread.GetString(MetadataTiffIfd.Main, MetadataTiff.Artist));
        Assert.Equal("MarcaSecreta", reread.GetString(MetadataTiffIfd.Main, MetadataTiff.Make));
        // Only the IFD that changed was moved, and it fits where it was (it only lost entries).
        Assert.True(after.Length <= block.Length + 24);
    }

    [Fact]
    public void A_gps_pointer_is_removed_even_when_the_ifd_behind_it_cannot_be_read()
    {
        var main = new IfdSpec();
        main.Tags.Add(new TagSpec(0x010F, 2, Samples.Ascii("MarcaSecreta")));
        main.Tags.Add(new TagSpec(0x8825, 4, Samples.Long(true, 100000)));       // points far outside the block
        var tiff = TiffOf(Samples.BuildTiff(main, true));
        Assert.True(tiff.Has(MetadataTiffIfd.Main, MetadataTiff.GpsPointer));
        tiff.RemoveGps();
        tiff.Commit();
        Assert.False(TiffOf(tiff.ToArray()).Has(MetadataTiffIfd.Main, MetadataTiff.GpsPointer));
    }

    [Fact]
    public void An_ifd_with_a_tag_that_points_outside_still_gives_the_other_tags()
    {
        var main = new IfdSpec();
        main.Tags.Add(new TagSpec(0x010F, 2, Samples.Ascii("MarcaSecreta")));
        main.Tags.Add(new TagSpec(0x0110, 2, Samples.Ascii("ModeloSecreto Largo")));
        var block = Samples.BuildTiff(main, true);
        // The second entry (Model) now says its text lives far beyond the end of the block.
        var second = 8 + 2 + 12;
        Assert.Equal(0x0110, (block[second] << 8) | block[second + 1]);
        block[second + 8] = 0x00;
        block[second + 9] = 0xFF;
        block[second + 10] = 0xFF;
        block[second + 11] = 0xFF;
        var tiff = TiffOf(block);
        Assert.Equal("MarcaSecreta", tiff.GetString(MetadataTiffIfd.Main, MetadataTiff.Make));
        Assert.Null(tiff.GetString(MetadataTiffIfd.Main, MetadataTiff.Model));
    }

    [Theory]
    [InlineData(40.4168, -3.7038)]
    [InlineData(0, 0)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(89.99999999, 179.99999999)]
    [InlineData(40.999999999, -3.999999999)]                                   // seconds that round up to 60 must carry
    public void A_position_is_written_as_degrees_minutes_and_seconds_and_read_back(double latitude, double longitude)
    {
        var tiff = MetadataTiff.CreateEmpty();
        tiff.SetGps(latitude, longitude);
        tiff.Commit();
        var read = TiffOf(tiff.ToArray()).GetGps()!.Value;
        Assert.Equal(latitude, read.Latitude, 5);
        Assert.Equal(longitude, read.Longitude, 5);
        var rationals = TiffOf(tiff.ToArray()).GetRationals(MetadataTiffIfd.Gps, MetadataTiff.GpsLatitude)!;
        Assert.True(rationals[1] < 60 && rationals[2] < 60);
    }

    [Fact]
    public void Xmp_properties_are_changed_in_the_shape_they_have()
    {
        var packet = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\" tiff:Make=\"Vieja\">"
            + "<dc:creator><rdf:Seq><rdf:li>Uno</rdf:li><rdf:li>Dos</rdf:li></rdf:Seq></dc:creator>"
            + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Titulo</rdf:li><rdf:li xml:lang=\"fr\">Titre</rdf:li></rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>");
        var xmp = MetadataXmp.TryParse(packet)!;
        Assert.Equal("Uno", xmp.GetText(MetadataXmpNs.Dc, "creator"));
        Assert.Equal("Titulo", xmp.GetText(MetadataXmpNs.Dc, "title"));
        Assert.Equal(3, xmp.PropertyCount);

        Assert.True(xmp.SetText(MetadataXmpNs.Tiff, "Make", "Nueva"));                // an attribute
        Assert.True(xmp.SetText(MetadataXmpNs.Dc, "creator", "Solo Yo"));             // a list: one item, the old ones go
        Assert.True(xmp.SetText(MetadataXmpNs.Dc, "title", "Nuevo"));                 // a language list: the other languages go too
        Assert.False(xmp.SetText(MetadataXmpNs.Dc, "rights", "No estaba"));           // only what exists is changed
        var text = Encoding.UTF8.GetString(xmp.ToPacket());
        Assert.DoesNotContain("Vieja", text);
        Assert.DoesNotContain("Uno", text);
        Assert.DoesNotContain("Dos", text);
        Assert.DoesNotContain("Titre", text);
        Assert.DoesNotContain("rights", text);
        Assert.Contains("Nueva", text);
        Assert.StartsWith("<?xpacket begin=", text);
        Assert.EndsWith("<?xpacket end=\"w\"?>", text);

        Assert.True(xmp.Remove(MetadataXmpNs.Dc, "creator"));
        Assert.False(xmp.Has(MetadataXmpNs.Dc, "creator"));
        Assert.Equal("Nueva", MetadataXmp.TryParse(xmp.ToPacket())!.GetText(MetadataXmpNs.Tiff, "Make"));
    }

    [Fact]
    public void Xmp_with_a_doctype_is_refused_so_a_hostile_file_cannot_expand()
    {
        var hostile = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">&b;</x:xmpmeta>");
        Assert.Null(MetadataXmp.TryParse(hostile));
        Assert.Null(MetadataXmp.TryParse("not xml at all"u8));
    }

    [Theory]
    [InlineData(40.4168, 'N', 'S', "40,25.008N")]
    [InlineData(-3.7038, 'E', 'W', "3,42.228W")]
    public void Gps_coordinates_in_xmp_are_written_and_read_in_its_own_notation(double degrees, char positive, char negative, string text)
    {
        Assert.Equal(text, MetadataXmp.GpsCoordinate(degrees, positive, negative));
        Assert.Equal(degrees, MetadataXmp.ParseGpsCoordinate(text)!.Value, 5);
        Assert.Equal(40.4168, MetadataXmp.ParseGpsCoordinate("40,25,0.48N")!.Value, 5);   // degrees, minutes and seconds
        Assert.Null(MetadataXmp.ParseGpsCoordinate("nonsense"));
    }

    [Fact]
    public void Iptc_text_switches_to_utf8_when_a_character_does_not_fit_latin1()
    {
        var segment = Samples.IptcSegment((2, 80, "José"), (2, 120, "Descripcion"));
        // The sample writes UTF-8 without declaring it: "é" is two bytes. Rebuild the block as Latin-1 to test the conversion.
        var latin1 = Samples.IptcSegment((2, 80, "x"), (2, 120, "y"));
        var iptc = MetadataIptc.FromPhotoshop(latin1.AsSpan(4 + MetadataIptc.PhotoshopHeader.Length))!;
        Assert.True(iptc.SetText(MetadataIptc.ByLine, "日本語"));
        Assert.True(iptc.SetText(MetadataIptc.CaptionAbstract, "Café"));
        var rebuilt = MetadataIptc.FromPhotoshop(iptc.ToPhotoshop())!;
        Assert.Equal("日本語", rebuilt.GetText(MetadataIptc.ByLine));
        Assert.Equal("Café", rebuilt.GetText(MetadataIptc.CaptionAbstract));
        Assert.Contains(0x1B, iptc.ToRaw().Select(b => (int)b));                // the character set is declared (ESC % G)
        _ = segment;
    }

    [Theory]
    [InlineData("2024-05-17 10:20:30", 2024, 5, 17, 10, 20, 30)]
    [InlineData("2024-05-17T10:20", 2024, 5, 17, 10, 20, 0)]
    [InlineData("2024:05:17 10:20:30", 2024, 5, 17, 10, 20, 30)]
    [InlineData("2024-05-17", 2024, 5, 17, 0, 0, 0)]
    public void Dates_typed_in_the_panel_are_read_in_several_forms(string text, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.True(MetadataValues.TryParseDate(text, out var date));
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ayer")]
    [InlineData("2024-13-01")]
    [InlineData("0001-01-01 00:00:00")]
    public void Dates_that_are_not_dates_are_refused(string text) => Assert.False(MetadataValues.TryParseDate(text, out _));

    [Theory]
    [InlineData("2024-05-17T10:20:30+02:00", 10, 20, 30)]                      // the clock of the place, not converted
    [InlineData("2024-05-17T10:20:30.000000Z", 10, 20, 30)]
    [InlineData("20240517", 0, 0, 0)]
    public void Dates_as_files_write_them_keep_their_clock(string text, int hour, int minute, int second)
    {
        Assert.True(MetadataValues.TryParseLooseDate(text, out var date));
        Assert.Equal(new TimeSpan(hour, minute, second), date.TimeOfDay);
    }

    [Theory]
    [InlineData("40.4168", true, 40.4168)]
    [InlineData("-3,7038", false, -3.7038)]
    [InlineData("90", true, 90)]
    public void Coordinates_are_decimal_degrees_with_a_dot_or_a_comma(string text, bool latitude, double expected)
    {
        Assert.True(MetadataValues.TryParseCoordinate(text, latitude, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("91", true)]
    [InlineData("-181", false)]
    [InlineData("abc", true)]
    [InlineData("", false)]
    public void Coordinates_out_of_range_or_not_numbers_are_refused(string text, bool latitude) => Assert.False(MetadataValues.TryParseCoordinate(text, latitude, out _));

    [Theory]
    [InlineData("+40.4168-003.7038/", true)]
    [InlineData("+40.4168-003.7038+650.123/", true)]
    [InlineData("40.4168,-3.7038", false)]
    [InlineData("", false)]
    public void Iso6709_positions_are_recognised(string text, bool valid) => Assert.Equal(valid, MetadataValues.IsIso6709(text));

    [Fact]
    public void An_id3_tag_is_built_and_read_back_in_both_versions()
    {
        foreach (var version in new[] { 3, 4 })
        {
            var frames = new List<MetadataId3Frame>
            {
                MetadataId3.TextFrame("TIT2", "Canción de niño", version),
                MetadataId3.TextFrame("TPE1", "日本語", version),
                MetadataId3.CommentFrame("spa", "Un comentario", version),
                MetadataId3.TextFrame("TCON", "(17)", version),
            };
            var tag = MetadataId3.Build(version, frames);
            var path = Path.Combine(_root, $"v{version}.mp3");
            File.WriteAllBytes(path, [.. tag, 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0]);
            var read = MetadataId3.Read(path);
            Assert.Equal(version, read.Version);
            Assert.Equal("Canción de niño", read.Text("TIT2"));
            Assert.Equal("日本語", read.Text("TPE1"));
            Assert.Equal("Un comentario", read.Comment());
            Assert.Equal("Rock", read.Genre());                                  // "(17)" is a code of the old list
            Assert.Equal(tag.Length, read.AudioStart);
        }
    }

    [Fact]
    public void An_id3v24_tag_with_plain_sizes_as_old_itunes_wrote_it_is_still_read()
    {
        // Frame sizes of 200 bytes written as plain numbers: synchsafe would read them as 0x00 0x00 0x00 0xC8 → not valid (high bit set).
        var body = new byte[200];
        body[0] = 0;
        Encoding.Latin1.GetBytes("Titulo largo").CopyTo(body, 1);
        var frame = new List<byte>();
        frame.AddRange("TIT2"u8.ToArray());
        frame.AddRange([0, 0, 0, 200, 0, 0]);
        frame.AddRange(body);
        var size = frame.Count;
        var tag = new List<byte>(["I"u8[0], "D"u8[0], "3"u8[0], 4, 0, 0, (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
        tag.AddRange(frame);
        var path = Path.Combine(_root, "viejo24.mp3");
        File.WriteAllBytes(path, [.. tag, 0xFF, 0xFB, 0x90, 0x00]);
        Assert.Equal("Titulo largo", MetadataId3.Read(path).Text("TIT2"));
    }

    [Fact]
    public void Text_for_a_pdf_is_written_as_ascii_or_as_utf16_in_hex()
    {
        Assert.Equal("(Hola \\(mundo\\))", PdfMetadataRewriter.TextToken("Hola (mundo)"));
        Assert.Equal("(a\\\\b)", PdfMetadataRewriter.TextToken("a\\b"));
        Assert.Equal("<FEFF00430061006600E9>", PdfMetadataRewriter.TextToken("Café"));
        Assert.StartsWith("D:2024", PdfMetadataRewriter.DateText(new DateTime(2024, 5, 17, 10, 20, 30)));
    }

    [Fact]
    public void A_compressed_png_text_that_would_unpack_to_gigabytes_is_not_read()
    {
        // A zTXt chunk whose zlib stream says "20 MB of zeros" in a few dozen bytes.
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(new byte[20 << 20]);
        var chunk = new MetadataPngChunk("zTXt", [.. "Comment"u8.ToArray(), 0, 0, .. compressed.ToArray()]);
        Assert.Null(MetadataPng.ReadText(chunk));
    }

    [AvaloniaFact]
    public async Task A_heic_is_saved_as_a_jpg_and_the_notices_never_clip_the_options()
    {
        // No HEIC encoder is at hand: an AVIF renamed ".heic" goes through the same libheif reader and the same JPEG writer.
        var source = Path.Combine(_root, "origen.jpg");
        File.WriteAllBytes(source, Samples.PhoneJpeg(true));
        var avif = Path.Combine(_root, "foto.avif");
        using (var image = new MagickImage(source))
            image.Write(avif);
        var heic = Path.Combine(_root, "iphone.heic");
        File.Copy(avif, heic);
        var bmp = Path.Combine(_root, "mapa.bmp");
        using (var image = new MagickImage("plasma:orange-purple", 100, 80))
            image.Write(bmp);

        // Three notices at once (HEIC → JPG, AVIF recoded, BMP without tags): the worst case for the room under the cards.
        var crowded = await OpenAsync(heic, avif, bmp);
        Assert.Equal(3, crowded.Notices.Count);
        Render(crowded, "metadata-notices");

        var panel = await OpenAsync(heic);
        Assert.Contains(panel.Notices, n => n.Contains("JPG"));
        panel.Field("Author")!.Text = "Ana Nueva";
        panel.RemoveLocation();
        var output = await ApplyAsync(panel);
        Assert.Equal(Path.Combine(_root, "iphone metadatos.jpg"), output);
        Assert.True(MetadataJpeg.LooksLikeJpeg(File.ReadAllBytes(output)));
        var read = PhotoMetadata.Read(output);
        Assert.Equal("Ana Nueva", read.Values[PhotoMetadataField.Author]);
        Assert.False(read.HasLocation);
        using var result = new MagickImage(output);
        using var original = new MagickImage(avif);
        Assert.Equal((original.Width, original.Height), (result.Width, result.Height));
        // The pixels were turned upright already: the file must not ask for another turn.
        Assert.True(result.GetExifProfile()?.GetValue(ExifTag.Orientation) is null or { Value: 1 });
        Assert.Equal(0, Count(File.ReadAllBytes(output), [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00]) - 1);   // one picture: no thumbnail of the unturned original
        Assert.Equal(1, Count(File.ReadAllBytes(output), [0xFF, 0xD8, 0xFF]));
    }

    private static class Samples
    {
        /// <summary>A phone-like photo: EXIF (camera, author, lens, GPS, a maker note with a preview, a thumbnail), XMP, IPTC, a comment.</summary>
        public static byte[] PhoneJpeg(bool big, bool withProfile = false)
        {
            var thumb = SmallJpeg();
            var exifIfd = new IfdSpec();
            exifIfd.Tags.Add(new TagSpec(0x9003, 2, Ascii("2024:05:17 10:20:30")));
            exifIfd.Tags.Add(new TagSpec(0x9004, 2, Ascii("2024:05:17 10:20:30")));
            exifIfd.Tags.Add(new TagSpec(0x9291, 2, Ascii("123")));
            exifIfd.Tags.Add(new TagSpec(0x9011, 2, Ascii("+02:00")));
            exifIfd.Tags.Add(new TagSpec(0xA434, 2, Ascii("Lente Secreta 24mm f/1.8")));
            exifIfd.Tags.Add(new TagSpec(0xA431, 2, Ascii("SERIE-CUERPO-123456")));
            exifIfd.Tags.Add(new TagSpec(0xA430, 2, Ascii("Propietario Secreto")));
            exifIfd.Tags.Add(new TagSpec(0x9286, 7, [.. "ASCII\0\0\0"u8.ToArray(), .. "Paseo secreto por Atlantis"u8.ToArray()]));
            exifIfd.Tags.Add(new TagSpec(0x927C, 7, [.. "Cam\0"u8.ToArray(), 0, 0, 0, 0, .. thumb]) { SelfOffset = true });
            var gps = new IfdSpec();
            gps.Tags.Add(new TagSpec(0x0000, 1, [2, 3, 0, 0]));
            gps.Tags.Add(new TagSpec(0x0001, 2, Ascii("N")));
            gps.Tags.Add(new TagSpec(0x0002, 5, Rational(big, (40, 1), (25, 1), (4800, 10000))));
            gps.Tags.Add(new TagSpec(0x0003, 2, Ascii("W")));
            gps.Tags.Add(new TagSpec(0x0004, 5, Rational(big, (3, 1), (42, 1), (1400, 100))));
            gps.Tags.Add(new TagSpec(0x0006, 5, Rational(big, (65012, 100))));
            gps.Tags.Add(new TagSpec(0x0007, 5, Rational(big, (10, 1), (20, 1), (30, 1))));
            gps.Tags.Add(new TagSpec(0x001D, 2, Ascii("2024:05:17")));
            var main = new IfdSpec { Exif = exifIfd, Gps = gps, ThumbnailPicture = thumb };
            main.Tags.Add(new TagSpec(0x010E, 2, Ascii("Paseo secreto por Atlantis")));
            main.Tags.Add(new TagSpec(0x010F, 2, Ascii("MarcaSecreta")));
            main.Tags.Add(new TagSpec(0x0110, 2, Ascii("ModeloSecreto 13")));
            main.Tags.Add(new TagSpec(0x0112, 3, Short(big, 6)));
            main.Tags.Add(new TagSpec(0x0131, 2, Ascii("Software Secreto 1.0")));
            main.Tags.Add(new TagSpec(0x0132, 2, Ascii("2024:05:17 10:20:31")));
            main.Tags.Add(new TagSpec(0x013B, 2, Ascii("Persona Autora")));
            main.Tags.Add(new TagSpec(0x013C, 2, Ascii("HostSecreto")));
            main.Tags.Add(new TagSpec(0x8298, 2, Ascii("(c) 2024 Copyright Secreto")));
            main.Tags.Add(new TagSpec(0x9C9D, 1, Encoding.Unicode.GetBytes("Persona Autora\0")));
            main.Tags.Add(new TagSpec(0x9C9C, 1, Encoding.Unicode.GetBytes("Paseo secreto por Atlantis\0")));       // Windows' "Comments"
            var tiff = BuildTiff(main, big);

            var xmp = XmpSegment(
                "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\" xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\" tiff:Make=\"MarcaSecreta\" tiff:Model=\"ModeloSecreto 13\" exif:GPSLatitude=\"40,25.08N\" exif:GPSLongitude=\"3,42.14W\" exif:GPSAltitude=\"65012/100\" photoshop:City=\"Atlantis Secreta\" xmp:CreatorTool=\"Software Secreto 1.0\" exif:DateTimeOriginal=\"2024-05-17T10:20:30\">"
                + "<dc:creator><rdf:Seq><rdf:li>Persona Autora</rdf:li></rdf:Seq></dc:creator>"
                + "<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">Paseo secreto por Atlantis</rdf:li><rdf:li xml:lang=\"en\">Secret walk in Atlantis</rdf:li></rdf:Alt></dc:description>"
                + "<dc:rights><rdf:Alt><rdf:li xml:lang=\"x-default\">(c) 2024 Copyright Secreto</rdf:li></rdf:Alt></dc:rights>"
                + "<xmp:Thumbnails><rdf:Alt><rdf:li rdf:parseType=\"Resource\"><xmpGImg:image>/9j/4AAQSkZJRgABAQAAAQABAAD/2wBD</xmpGImg:image></rdf:li></rdf:Alt></xmp:Thumbnails>"
                + "</rdf:Description>");
            var iptc = IptcSegment((1, 90, "\u001b%G"), (2, 80, "Persona Autora"), (2, 116, "(c) 2024 Copyright Secreto"), (2, 120, "Paseo secreto por Atlantis"),
                (2, 90, "Atlantis Secreta"), (2, 101, "España"), (2, 55, "20240517"), (2, 60, "102030"), (2, 65, "Software Secreto 1.0"));
            var comment = Segment(0xFE, Encoding.UTF8.GetBytes("Comentario secreto del autor"));
            List<byte[]> segments = [ExifSegment(tiff), xmp, iptc, comment];
            if (withProfile)
                segments.Add(Segment(0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. ColorProfiles.SRGB.ToByteArray()]));
            return WithSegments(Jpeg(), segments);
        }

        /// <summary>A JPEG of a phone that adds a second picture after the first one and describes both in a multi-picture index.</summary>
        public static (byte[] File, int FirstImageEnd) MultiPicture()
        {
            var secondary = SmallJpeg();
            var main = new IfdSpec();
            main.Tags.Add(new TagSpec(0x010F, 2, Ascii("MarcaSecreta")));
            main.Tags.Add(new TagSpec(0x013B, 2, Ascii("Persona Autora")));
            var exif = ExifSegment(BuildTiff(main, true));
            var picture = Jpeg();
            var jfifLength = (picture[4] << 8) | picture[5];

            byte[] Mpf(uint firstSize, uint offset)
            {
                var data = new List<byte>();
                data.AddRange("MPF\0"u8.ToArray());
                data.AddRange("MM\0*"u8.ToArray());
                data.AddRange(Long(true, 8));
                data.AddRange(Short(true, 3));
                void Entry(int tag, int type, int count, byte[] value)
                {
                    data.AddRange(Short(true, tag));
                    data.AddRange(Short(true, type));
                    data.AddRange(Long(true, count));
                    data.AddRange(value);
                }
                Entry(0xB000, 7, 4, "0100"u8.ToArray());
                Entry(0xB001, 4, 1, Long(true, 2));
                Entry(0xB002, 7, 32, Long(true, 50));
                data.AddRange(Long(true, 0));
                data.AddRange(Long(true, 0x20030000));
                data.AddRange(Long(true, firstSize));
                data.AddRange(Long(true, 0));
                data.AddRange(Short(true, 0));
                data.AddRange(Short(true, 0));
                data.AddRange(Long(true, 0));
                data.AddRange(Long(true, secondary.Length));
                data.AddRange(Long(true, offset));
                data.AddRange(Short(true, 0));
                data.AddRange(Short(true, 0));
                return data.ToArray();
            }
            var firstEnd = WithSegments(picture, [exif, Segment(0xE2, Mpf(0, 0))]).Length;
            var mpfAt = 2 + 2 + jfifLength + exif.Length;                         // after SOI, the JFIF segment and the Exif segment
            var header = mpfAt + 4 + 4;                                           // the segment header and "MPF\0"
            var file = WithSegments(picture, [exif, Segment(0xE2, Mpf((uint)firstEnd, (uint)(firstEnd - header)))], secondary);
            return (file, firstEnd);
        }

        /// <summary>The entries of the multi-picture index of a JPEG and where its header sits in the file.</summary>
        public static (List<(uint Size, uint Offset)> Entries, long HeaderAt) ReadMultiPicture(byte[] file)
        {
            var jpeg = MetadataJpeg.Parse(file);
            var at = 0;
            MetadataJpegSegment? mpf = null;
            foreach (var segment in jpeg.Segments)
            {
                if (segment.IsMpf)
                {
                    mpf = segment;
                    break;
                }
                at += segment.Length;
            }
            var headerAt = at + 8;
            var data = mpf!.Payload.AsSpan(4).ToArray();
            uint U32(int pos) => (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            var count = (data[8] << 8) | data[9];
            var entries = new List<(uint Size, uint Offset)>();
            for (var i = 0; i < count; i++)
            {
                var entry = 10 + i * 12;
                if (((data[entry] << 8) | data[entry + 1]) != 0xB002)
                    continue;
                var list = (int)U32(entry + 8);
                for (var k = 0; k < U32(entry + 4) / 16; k++)
                    entries.Add((U32(list + k * 16 + 4), U32(list + k * 16 + 8)));
            }
            return (entries, headerAt);
        }

        public static byte[] Ascii(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

        public static byte[] Short(bool big, params int[] values) =>
            values.SelectMany(v => big ? new[] { (byte)(v >> 8), (byte)v } : new[] { (byte)v, (byte)(v >> 8) }).ToArray();

        public static byte[] Long(bool big, params long[] values) =>
            values.SelectMany(v => big ? new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v } : new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }).ToArray();

        public static byte[] Rational(bool big, params (long N, long D)[] values) => values.SelectMany(v => Long(big, v.N, v.D)).ToArray();

        public static byte[] Jpeg(int width = 400, int height = 300)
        {
            using var image = new MagickImage("plasma:orange-purple", (uint)width, (uint)height);
            image.Strip();
            image.Quality = 85;
            return image.ToByteArray(MagickFormat.Jpeg);
        }

        public static byte[] SmallJpeg()
        {
            using var image = new MagickImage("plasma:orange-purple", 64, 48);
            image.Strip();
            return image.ToByteArray(MagickFormat.Jpeg);
        }

        public static byte[] Segment(byte marker, byte[] payload) => [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

        public static byte[] ExifSegment(byte[] tiff) => Segment(0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);

        public static byte[] XmpSegment(string inner) => Segment(0xE1, [.. Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0"), .. Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + inner + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>")]);

        public static byte[] IptcSegment(params (int Record, int Number, string Value)[] datasets)
        {
            var data = new List<byte>();
            foreach (var (record, number, value) in datasets)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                data.AddRange([0x1C, (byte)record, (byte)number, (byte)(bytes.Length >> 8), (byte)bytes.Length]);
                data.AddRange(bytes);
            }
            var resource = new List<byte>();
            resource.AddRange("8BIM"u8.ToArray());
            resource.AddRange([0x04, 0x04, 0x00, 0x00]);
            resource.AddRange([(byte)(data.Count >> 24), (byte)(data.Count >> 16), (byte)(data.Count >> 8), (byte)data.Count]);
            resource.AddRange(data);
            if (data.Count % 2 == 1)
                resource.Add(0);
            resource.AddRange("8BIM"u8.ToArray());
            resource.AddRange([0x04, 0x03, 0x00, 0x00, 0, 0, 0, 4, 1, 2, 3, 4]);
            return Segment(0xED, [.. Encoding.ASCII.GetBytes("Photoshop 3.0\0"), .. resource]);
        }

        /// <summary>Puts segments after the SOI and the JFIF header of an existing JPEG, and a trailer at its end.</summary>
        public static byte[] WithSegments(byte[] jpeg, IEnumerable<byte[]> segments, byte[]? trailer = null)
        {
            var jfifLength = (jpeg[4] << 8) | jpeg[5];
            var cut = 2 + 2 + jfifLength;
            var result = new List<byte>();
            result.AddRange(jpeg.AsSpan(0, cut).ToArray());
            foreach (var segment in segments)
                result.AddRange(segment);
            result.AddRange(jpeg.AsSpan(cut).ToArray());
            if (trailer is not null)
                result.AddRange(trailer);
            return result.ToArray();
        }

        // An independent writer of EXIF blocks (the tool's own parser is what is being tested).
        public static byte[] BuildTiff(IfdSpec main, bool big)
        {
            var buffer = new List<byte>();
            buffer.AddRange(big ? "MM\0*"u8.ToArray() : "II*\0"u8.ToArray());
            buffer.AddRange(Long(big, 8));

            long Align()
            {
                if (buffer.Count % 2 == 1)
                    buffer.Add(0);
                return buffer.Count;
            }
            void Patch(int at, byte[] bytes)
            {
                for (var i = 0; i < bytes.Length; i++)
                    buffer[at + i] = bytes[i];
            }

            void WriteIfd(IfdSpec ifd, long tableAt, long next)
            {
                var tags = ifd.Tags.ToList();
                var tableSize = 2 + 12 * (tags.Count + (ifd.Exif is null ? 0 : 1) + (ifd.Gps is null ? 0 : 1) + (ifd.Interop is null ? 0 : 1)) + 4;
                while (buffer.Count < tableAt + tableSize)
                    buffer.Add(0);
                var finished = new List<(ushort Tag, ushort Type, uint Count, byte[] Cell)>();
                foreach (var spec in tags)
                {
                    if (spec.Data.Length <= 4)
                    {
                        var cell = new byte[4];
                        spec.Data.CopyTo(cell, 0);
                        finished.Add((spec.Tag, spec.Type, spec.Count, cell));
                        continue;
                    }
                    var valueAt = Align();
                    var data = spec.Data.ToArray();
                    if (spec.SelfOffset)
                    {
                        var pointer = Long(big, valueAt + 8);
                        for (var i = 0; i < 4; i++)
                            data[4 + i] = pointer[i];
                    }
                    buffer.AddRange(data);
                    finished.Add((spec.Tag, spec.Type, spec.Count, Long(big, valueAt)));
                }
                foreach (var (tag, child) in new[] { ((ushort)0x8769, ifd.Exif), ((ushort)0x8825, ifd.Gps), ((ushort)0xA005, ifd.Interop) })
                {
                    if (child is null)
                        continue;
                    var childAt = Align();
                    WriteIfd(child, childAt, 0);
                    finished.Add((tag, 4, 1, Long(big, childAt)));
                }
                finished = finished.OrderBy(r => r.Tag).ToList();
                Patch((int)tableAt, Short(big, finished.Count));
                for (var i = 0; i < finished.Count; i++)
                {
                    var p = (int)tableAt + 2 + i * 12;
                    Patch(p, Short(big, finished[i].Tag));
                    Patch(p + 2, Short(big, finished[i].Type));
                    Patch(p + 4, Long(big, finished[i].Count));
                    Patch(p + 8, finished[i].Cell);
                }
                Patch((int)tableAt + 2 + finished.Count * 12, Long(big, next));
            }

            WriteIfd(main, 8, 0);
            if (main.ThumbnailPicture is { } picture)
            {
                var ifd1At = Align();
                var count = big ? (buffer[8] << 8) | buffer[9] : buffer[8] | (buffer[9] << 8);
                Patch(8 + 2 + count * 12, Long(big, ifd1At));
                var tableSize = 2 + 3 * 12 + 4;
                var pictureAt = ifd1At + tableSize;
                while (buffer.Count < ifd1At + tableSize)
                    buffer.Add(0);
                Patch((int)ifd1At, Short(big, 3));
                void Row(int i, ushort tag, ushort type, byte[] cell)
                {
                    var p = (int)ifd1At + 2 + i * 12;
                    Patch(p, Short(big, tag));
                    Patch(p + 2, Short(big, type));
                    Patch(p + 4, Long(big, 1));
                    Patch(p + 8, cell);
                }
                Row(0, 0x0103, 3, [.. Short(big, 6), 0, 0]);
                Row(1, 0x0201, 4, Long(big, pictureAt));
                Row(2, 0x0202, 4, Long(big, picture.Length));
                buffer.AddRange(picture);
            }
            return buffer.ToArray();
        }
    }

    private sealed class TagSpec(ushort tag, ushort type, byte[] data)
    {
        public ushort Tag { get; } = tag;

        public ushort Type { get; } = type;

        public byte[] Data { get; } = data;

        public uint Count { get; } = (uint)(type switch { 3 => data.Length / 2, 4 => data.Length / 4, 5 => data.Length / 8, _ => data.Length });

        /// <summary>A maker note whose bytes 4..8 hold the absolute position of its own data (like Canon's offsets).</summary>
        public bool SelfOffset { get; init; }
    }

    private sealed class IfdSpec
    {
        public List<TagSpec> Tags { get; } = [];

        public IfdSpec? Exif { get; init; }

        public IfdSpec? Gps { get; init; }

        public IfdSpec? Interop { get; init; }

        public byte[]? ThumbnailPicture { get; init; }
    }
}
