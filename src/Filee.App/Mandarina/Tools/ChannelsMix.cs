// Mandarina: the arithmetic of the CHANNELS tool: which FFmpeg filter turns a file into mono or stereo with a level for each
// side. It lives apart from the panel so the tests can read the filter without running FFmpeg.

namespace Filee.App.Mandarina.Tools;

internal static class ChannelsMix
{
    /// <summary>
    /// A level above full scale is caught by a limiter a little under it (a lossy encoder overshoots a bit). Latency compensation
    /// keeps the sound where it was (the lookahead would shift it by 5 ms) and auto level is off: it would lift quiet files.
    /// </summary>
    private const string Limiter = "alimiter=limit=0.97:latency=1:level=0";

    /// <summary>
    /// The <c>-af</c> chain that makes a file of <paramref name="sourceChannels"/> channels mono or stereo, with <paramref name="left"/>
    /// and <paramref name="right"/> (1 = unchanged) as the level of each side. Stereo to mono mixes the two sides (each weighs
    /// half of its level, the way FFmpeg's own downmix does); a mono source counts as the same sound on both sides.
    /// </summary>
    internal static string Filter(int sourceChannels, bool toMono, double left, double right)
    {
        List<string> chain = [];
        // 5.1 and the like are mixed down to two sides first (FFmpeg's own matrix, centre and surrounds included): reading c0
        // and c1 of a six-channel file would leave out the dialogue.
        if (sourceChannels > 2)
            chain.Add("aformat=channel_layouts=stereo");
        var single = sourceChannels == 1;
        chain.Add(toMono
            ? single
                ? $"pan=mono|c0={AudioEncoding.Number((left + right) / 2)}*c0"
                : $"pan=mono|c0={AudioEncoding.Number(left / 2)}*c0+{AudioEncoding.Number(right / 2)}*c1"
            : $"pan=stereo|c0={AudioEncoding.Number(left)}*c0|c1={AudioEncoding.Number(right)}*{(single ? "c0" : "c1")}");
        if (left > 1 || right > 1)
            chain.Add(Limiter);
        return string.Join(',', chain);
    }
}
