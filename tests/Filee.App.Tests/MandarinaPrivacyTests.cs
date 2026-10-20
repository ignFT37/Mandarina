// Mandarina: a camera hides small copies of the photo in its metadata (the EXIF thumbnail, a preview in the maker note,
// thumbnails in XMP). A tool that changes the pixels must not carry those copies into what it saves, or the copy would
// show what the tool took away: the part a crop cut off, what an edit changed. The camera's own data (make, model) stays.

using Avalonia;
using Avalonia.Headless.XUnit;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaPrivacyTests : IDisposable
{
    private static readonly byte[] JpegStart = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] XmpThumbnail = System.Text.Encoding.ASCII.GetBytes("xmpGImg");

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-privacy").FullName;

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

    private string CameraPhoto()
    {
        var photo = MakePhotoWithHiddenCopies(Path.Combine(_root, "camara.jpg"));
        Assert.Equal(3, CountIn(photo, JpegStart));                 // the picture, the EXIF thumbnail and the maker note preview
        Assert.True(CountIn(photo, XmpThumbnail) > 0);
        return photo;
    }

    private static void AssertOnlyThePicture(string output)
    {
        Assert.Equal(1, CountIn(output, JpegStart));                // no second JPEG inside
        Assert.Equal(0, CountIn(output, XmpThumbnail));
        using var result = new MagickImage(output);
        Assert.Null(result.GetXmpProfile());
        Assert.Equal("Mandarina Cam", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);   // the camera's own data stays
    }

    [AvaloniaFact]
    public void Saving_a_photo_leaves_its_hidden_previews_behind()
    {
        var photo = CameraPhoto();
        using var image = PanelImages.Read(photo);
        image.Crop(new MagickGeometry(100, 100, 300, 200));
        image.ResetPage();
        var output = Path.Combine(_root, "recorte.jpg");

        PanelImages.Save(image, output);

        AssertOnlyThePicture(output);
        using var result = new MagickImage(output);
        Assert.Equal((300u, 200u), (result.Width, result.Height));
    }

    [AvaloniaFact]
    public async Task A_cropped_photo_does_not_carry_the_whole_photo_inside()
    {
        var panel = new CropImagePanel(Context(CameraPhoto()));
        await WaitAsync(() => !panel.IsLoading);
        panel.Selection = new Rect(200, 100, 300, 200);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        AssertOnlyThePicture(Assert.Single(panel.Outputs));
    }

    [AvaloniaFact]
    public async Task An_edited_photo_does_not_carry_the_unedited_thumbnail()
    {
        var panel = new EditImagePanel(Context(CameraPhoto()));
        await WaitAsync(() => !panel.IsLoading);
        panel.Exposure = 0.6;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        AssertOnlyThePicture(Assert.Single(panel.Outputs));
    }
}
