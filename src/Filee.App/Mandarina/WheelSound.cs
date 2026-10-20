// Mandarina: the soft tick when the pointer moves onto another slice. Synthesised here,
// so the app ships no sound file: a short 2.6 kHz blip with a fast decay, 16-bit mono WAV.

namespace Filee.App.Mandarina;

internal static class WheelSound
{
    /// <summary>The whole WAV file.</summary>
    public static byte[] Tick { get; } = Synthesize(frequency: 2600, milliseconds: 28, volume: 0.22);

    private static byte[] Synthesize(double frequency, int milliseconds, double volume)
    {
        const int rate = 44100;
        var samples = rate * milliseconds / 1000;
        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);             // fmt chunk size
        writer.Write((short)1);       // PCM
        writer.Write((short)1);       // mono
        writer.Write(rate);
        writer.Write(rate * 2);       // bytes per second
        writer.Write((short)2);       // block align
        writer.Write((short)16);      // bits per sample
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            var attack = Math.Min(1, i / (rate * 0.0015));   // 1.5 ms fade-in: no click
            var decay = Math.Exp(-t * 180);
            var value = Math.Sin(2 * Math.PI * frequency * t) * attack * decay * volume;
            writer.Write((short)Math.Round(value * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }
}
