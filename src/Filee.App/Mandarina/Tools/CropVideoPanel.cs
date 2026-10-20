// Mandarina: CROP for video: choose which part of the picture to keep, on a real frame of the video you can
// scrub through. Apply re-encodes with FFmpeg's crop filter and saves "name framed.ext" beside the original; the audio
// is copied. H.264 in yuv420p needs even numbers, so the box is rounded down to them (the result line says what comes out).

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("cropVideo", NeedsFfmpeg = true)]
public sealed partial class CropVideoPanel : CropPanelBase
{
    /// <summary>Widest frame asked of FFmpeg for the preview.</summary>
    private const int FrameWidth = 960;

    /// <summary>How long the slider rests before its frame is fetched, so dragging does not start an FFmpeg run per pixel.</summary>
    private static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _frameRequest;

    [ObservableProperty] private double _duration;

    /// <summary>Where in the video the preview frame is taken, in seconds.</summary>
    [ObservableProperty] private double _frameTime;

    public CropVideoPanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(880, 580);

    public string FileName => Path.GetFileName(Files[0]);

    protected override string? Hint => Context["panel.cropVideo.hint"];

    public override string NoteText => Context["panel.cropVideo.even_note"];

    /// <summary>Even position and size, rounded down (yuv420p cannot be cut anywhere else).</summary>
    protected override Rect OutputBoxCore() =>
        CropMath.Even(CropMath.Snap(Selection, PixelWidth, PixelHeight), PixelWidth, PixelHeight);

    public override Control CreateView() => new CropVideoView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var media = Context.RequireMedia();
            var probe = await media.ProbeAsync(Files[0], LoadToken);
            if (!probe.HasVideo || probe.Width < 1 || probe.Height < 1)
                throw new PanelException(Context["panel.cropVideo.no_video"]);
            Duration = probe.Duration;
            FrameTime = Math.Round(probe.Duration * 0.1, 1);
            Preview = await FirstFrameAsync(media);
            SetPicture(probe.Width, probe.Height);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "reading" for ever.
            LoadError = Context.Explain(ex);
        }
        IsLoading = false;
    }

    /// <summary>The frame at <see cref="FrameTime"/>; when FFmpeg cannot seek there (a damaged index) the first frame of the video instead.</summary>
    private async Task<Bitmap> FirstFrameAsync(MediaTools media)
    {
        try
        {
            return await media.FrameAsync(Files[0], LastSecond(FrameTime), FrameWidth, LoadToken);
        }
        catch (PanelException) when (FrameTime > 0)
        {
            FrameTime = 0;
            return await media.FrameAsync(Files[0], 0, FrameWidth, LoadToken);
        }
    }

    /// <summary>A time FFmpeg can still show a frame at: asking for the very end finds none.</summary>
    private double LastSecond(double seconds) => Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.1));

    partial void OnFrameTimeChanged(double value)
    {
        if (!IsLoading)
            _ = ShowFrameAsync(value);
    }

    /// <summary>Fetches the frame at <paramref name="seconds"/> after a short rest; a newer request cancels this one.</summary>
    private async Task ShowFrameAsync(double seconds)
    {
        _frameRequest?.Cancel();
        var request = _frameRequest = CancellationTokenSource.CreateLinkedTokenSource(LoadToken);
        try
        {
            await Task.Delay(FrameDelay, request.Token);
            var frame = await Context.RequireMedia().FrameAsync(Files[0], LastSecond(seconds), FrameWidth, request.Token);
            if (request.IsCancellationRequested)
            {
                frame.Dispose();
                return;
            }
            var previous = Preview;
            Preview = frame;
            // Scrubbing makes many frames; the one just replaced is let go a moment later, once the surface has surely drawn the new one.
            if (previous is IDisposable old)
                DispatcherTimer.RunOnce(old.Dispose, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is OperationCanceledException or PanelException or IOException)
        {
            // A frame that cannot be read leaves the one on screen; the crop does not depend on it.
        }
        finally
        {
            if (ReferenceEquals(_frameRequest, request))
                _frameRequest = null;
            request.Dispose();
        }
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var media = Context.RequireMedia();
        var source = Files[0];
        var box = OutputBox;
        var output = Context.OutputFor(source, "cropVideo");

        // The picture is re-encoded to keep it sharp (CRF 18); the sound is copied untouched and the tags kept.
        List<string> args =
        [
            "-i", source,
            "-vf", string.Create(CultureInfo.InvariantCulture, $"crop={box.Width:0}:{box.Height:0}:{box.X:0}:{box.Y:0}"),
            "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "copy",
        ];
        // Only MP4-family containers take this option (FFmpeg refuses it for the others, e.g. MKV).
        if (Path.GetExtension(source).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov")
            args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);

        var length = Duration > 0 ? Duration : (await media.ProbeAsync(source, cancel)).Duration;
        try
        {
            await media.RunAsync(args, length, progress, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        return [output];
    }

    public override void Dispose()
    {
        _frameRequest?.Cancel();
        base.Dispose();
    }
}
