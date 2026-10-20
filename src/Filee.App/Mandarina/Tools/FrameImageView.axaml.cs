using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Filee.App.Mandarina.Tools;

public partial class FrameImageView : UserControl
{
    /// <summary>What the background picture picker lists: the formats Magick.NET reads that make sense as a backdrop.</summary>
    private static readonly string[] PicturePatterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.heic", "*.heif", "*.hif", "*.tif", "*.tiff", "*.avif", "*.bmp", "*.gif"];

    public FrameImageView()
    {
        InitializeComponent();
        TightSliders.Apply(this);
    }

    private FrameImagePanel? Panel => DataContext as FrameImagePanel;

    private async void OnPickPicture(object? sender, RoutedEventArgs e)
    {
        if (Panel is not { } panel || TopLevel.GetTopLevel(this) is not { } top)
            return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = panel.Context["panel.frameImage.choose"],
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(panel.Context["panel.frameImage.pictures"]) { Patterns = PicturePatterns }],
            });
            if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
                await panel.SetPictureAsync(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            panel.PictureError = ex.Message;
        }
    }
}
