// Mandarina: the picture side of the VISUALIZE tool, apart from the panel so the tests can read the graph without running FFmpeg.
// The graph draws the audio on a flat background: the wave (an oscilloscope line, 30 times a second) or mirrored frequency bars,
// in the colours chosen. The preview in the panel and the final video come out of the same graph, so what the preview shows is
// what Apply writes; only the last step differs (a smaller RGB picture for the panel, H.264's YUV for the file).

using System.Globalization;
using System.Text;

namespace Filee.App.Mandarina.Tools;

/// <summary>The shape of the video.</summary>
public enum VisualFormat
{
    Horizontal,
    Vertical,
    Square,
}

/// <summary>What is drawn.</summary>
public enum VisualStyle
{
    Wave,
    Bars,
}

internal static class VisualGraph
{
    internal const int FrameRate = 30;

    /// <summary>A bar and the gap after it are this many pixels wide.</summary>
    private const int BarPitch = 20;

    private const int BarGap = 4;

    /// <summary>The wave is drawn in a band this share of the frame's height, in the middle: a loud file does not touch the edges.</summary>
    private const double WaveBand = 0.6;

    /// <summary>The frame in pixels (what a phone, a feed and a screen take as they are).</summary>
    internal static (int Width, int Height) SizeOf(VisualFormat format) => format switch
    {
        VisualFormat.Vertical => (1080, 1920),
        VisualFormat.Square => (1080, 1080),
        _ => (1920, 1080),
    };

    /// <summary>
    /// The graph from <c>[0:a]</c> to <c>[v]</c>. With <paramref name="previewWidth"/> 0 the end is the video's: BT.709 YUV 4:2:0, tagged,
    /// so every player shows the colours as chosen (H.264 assumes BT.709 for HD, whatever the encoder used). Otherwise it is an RGB
    /// picture <paramref name="previewWidth"/> pixels wide.
    /// </summary>
    internal static string Build(VisualFormat format, VisualStyle style, string waveHex, string backgroundHex, int previewWidth = 0)
    {
        var (width, height) = SizeOf(format);
        var wave = VisualColors.ForFilter(waveHex, VisualColors.DefaultWave);
        var background = VisualColors.ForFilter(backgroundHex, VisualColors.DefaultBackground);
        var graph = new StringBuilder(FormattableString.Invariant($"color=c={background}:s={width}x{height}:r={FrameRate}[bg];"));
        if (style == VisualStyle.Wave)
        {
            // One audio sample per pixel column: a whole number of them (the file is resampled to 30 × width Hz for this) leaves no
            // column unpainted, and 1920 samples are exactly a frame. Channels are mixed to one; sqrt keeps a quiet file readable.
            var band = (int)(height * WaveBand) & ~1;
            graph.Append(FormattableString.Invariant(
                $"[0:a]aformat=channel_layouts=mono,aresample={FrameRate * width},showwaves=s={width}x{band}:mode=cline:colors={wave}:rate={FrameRate}:draw=full:scale=sqrt[wave];"));
            graph.Append(FormattableString.Invariant($"[bg][wave]overlay=x=0:y={(height - band) / 2}:shortest=1:format=auto"));
        }
        else
        {
            // One pixel column per bar, then every column is made 20 pixels wide. The upper half is mirrored below the middle line,
            // and the bars are parted by gaps of the background's colour (half a gap at each edge, so the row is symmetrical).
            graph.Append(FormattableString.Invariant(
                $"[0:a]aformat=channel_layouts=mono,aresample=44100,showfreqs=s={width / BarPitch}x{height / 4}:mode=bar:fscale=log:ascale=log:win_size=2048:colors={wave}:rate={FrameRate}:averaging=3,split[t][b];"));
            graph.Append(FormattableString.Invariant($"[b]vflip[f];[t][f]vstack,scale={width}:{height}:flags=neighbor[wave];"));
            graph.Append(FormattableString.Invariant(
                $"[bg][wave]overlay=shortest=1:format=auto,drawgrid=x={-BarGap / 2}:y=-10:w={BarPitch}:h={2 * height}:t={BarGap}:c={background}"));
        }

        if (previewWidth > 0)
            graph.Append(string.Create(CultureInfo.InvariantCulture, $",scale={previewWidth}:-2:flags=area[v]"));
        else
            graph.Append(",scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv[v]");
        return graph.ToString();
    }
}
