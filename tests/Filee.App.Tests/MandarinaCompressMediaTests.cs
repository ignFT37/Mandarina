// Mandarina: the audio and video compressors behind the Compress panel, run against the real FFmpeg (engines\ffmpeg next to
// the test binaries) on small files made on the spot. No window is involved, so these are plain facts, not Avalonia ones.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.Engines.Infrastructure;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaCompressMediaTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-compress-media").FullName;

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

    // ───────── Helpers ─────────

    private static CompressOptions Percent(int quality, int maxSize = 0) => new(CompressMode.Percent, quality, MaxSize: maxSize);

    private static CompressOptions Target(long bytes, int maxSize = 0) => new(CompressMode.TargetSize, 70, bytes, maxSize);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MediaTools Media()
    {
        var media = MediaTools.Locate();
        if (media is null)
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
        return media!;
    }

    private async Task<string> MakeAsync(string name, params string[] ffmpegArguments)
    {
        var file = await MakeMediaAsync(Path.Combine(_root, name), ffmpegArguments);
        if (file is null)
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
        return file!;
    }

    /// <summary>A real 1280x720 clip of 6 s with a tone (AAC, 64 kbps) and a title tag, or without sound.</summary>
    private Task<string> MakeClipAsync(string name, bool withAudio = true) => withAudio
        ? MakeAsync(name, "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k", "-metadata", "title=Mi vídeo")
        : MakeAsync(name, "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=6", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-metadata", "title=Mi vídeo");

    /// <summary>Pink noise at 44.1 kHz (incompressible, so the bitrate decides the size) in the format the extension names.</summary>
    private Task<string> MakeNoiseAsync(string name, double seconds, params string[] encoderArguments) =>
        MakeAsync(name, ["-f", "lavfi", "-i", $"anoisesrc=d={seconds.ToString(CultureInfo.InvariantCulture)}:c=pink:r=44100:a=0.3", .. encoderArguments]);

    private async Task<string> AudioAsync(string source, CompressOptions options, IProgress<double>? progress = null)
    {
        var output = OutputPaths.Beside(source, "comprimido", AudioCompressor.OutputExtension(source));
        Assert.Equal(output, await AudioCompressor.CompressAsync(Media(), source, output, options, progress, Token));
        Assert.True(File.Exists(output));
        return output;
    }

    private async Task<string> VideoAsync(string source, CompressOptions options, IProgress<double>? progress = null)
    {
        var output = OutputPaths.Beside(source, "comprimido", Path.GetExtension(source));
        Assert.Equal(output, await VideoCompressor.CompressAsync(Media(), source, output, options, progress, Token));
        Assert.True(File.Exists(output));
        return output;
    }

    private static long SizeOf(string file) => new FileInfo(file).Length;

    private static string Fingerprint(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>ffprobe's whole answer as JSON, for what <see cref="MediaProbe"/> leaves out (bit depth, dispositions, stream tags).</summary>
    private static async Task<JsonElement> ProbeJsonAsync(string file)
    {
        var result = await ProcessRunner.RunAsync(Media().Ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file],
            TimeSpan.FromMinutes(1), Token);
        Assert.Equal(0, result.ExitCode);
        return JsonDocument.Parse(result.StandardOutput).RootElement.Clone();
    }

    private static IEnumerable<JsonElement> StreamsOf(JsonElement probe, string type) =>
        probe.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == type);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : null;

    /// <summary>A tag from the container or, as Ogg and Opus keep them, from the audio stream.</summary>
    private static async Task<string?> TagAsync(string file, string key)
    {
        var probe = await ProbeJsonAsync(file);
        IEnumerable<JsonElement> holders = [probe.GetProperty("format"), .. probe.GetProperty("streams").EnumerateArray()];
        foreach (var holder in holders)
            if (holder.TryGetProperty("tags", out var tags))
                foreach (var tag in tags.EnumerateObject())
                    if (tag.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return tag.Value.GetString();
        return null;
    }

    /// <summary>MD5 of the first audio stream decoded as <paramref name="pcm"/> samples: equal hashes, equal sound.</summary>
    private async Task<string> PcmHashAsync(string file, string pcm)
    {
        var hash = Path.Combine(_root, $"hash-{Guid.NewGuid():N}.txt");
        await Media().RunAsync(["-i", file, "-map", "0:a:0", "-c:a", pcm, "-f", "md5", hash]);
        var text = (await File.ReadAllTextAsync(hash)).Trim();
        File.Delete(hash);
        return text;
    }

    /// <summary>MD5 of the first audio stream's packets as stored (no decoding): equal hashes, an untouched track.</summary>
    private async Task<string> PacketHashAsync(string file)
    {
        var hash = Path.Combine(_root, $"hash-{Guid.NewGuid():N}.txt");
        await Media().RunAsync(["-i", file, "-map", "0:a:0", "-c", "copy", "-f", "md5", hash]);
        var text = (await File.ReadAllTextAsync(hash)).Trim();
        File.Delete(hash);
        return text;
    }

    /// <summary>Records what a job reports, from whichever thread FFmpeg's output arrives on.</summary>
    private sealed class Recorder : IProgress<double>
    {
        private readonly List<double> _values = [];

        public double[] Values
        {
            get
            {
                lock (_values)
                    return [.. _values];
            }
        }

        public void Report(double value)
        {
            lock (_values)
                _values.Add(value);
        }
    }

    // ───────── Audio: quality → bitrate ─────────

    [Theory]
    [InlineData(".mp3", 10, 48)]
    [InlineData(".mp3", 35, 80)]
    [InlineData(".mp3", 50, 96)]
    [InlineData(".mp3", 70, 128)]
    [InlineData(".mp3", 85, 192)]
    [InlineData(".mp3", 100, 256)]
    [InlineData(".m4a", 70, 128)]
    [InlineData(".ogg", 70, 128)]
    [InlineData(".opus", 10, 24)]
    [InlineData(".opus", 70, 80)]
    [InlineData(".opus", 100, 160)]
    public void Quality_maps_to_the_documented_bitrate(string extension, int quality, int kbps) =>
        Assert.Equal(kbps, AudioCompressor.QualityKbps(extension, quality));

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".opus")]
    public void A_higher_quality_never_asks_for_a_lower_bitrate(string extension)
    {
        var previous = 0;
        for (var quality = 1; quality <= 100; quality++)
        {
            var kbps = AudioCompressor.QualityKbps(extension, quality);
            Assert.True(kbps >= previous, $"{extension} at {quality} gives {kbps} after {previous}");
            previous = kbps;
        }
    }

    [Theory]
    [InlineData(".mp3", 31.4, 1, 44100, 24, 22050)]     // below 32 kbps MP3 only exists at 24 kHz and under: halve the rate
    [InlineData(".mp3", 18.0, 2, 48000, 16, 24000)]
    [InlineData(".mp3", 20.0, 1, 22050, 16, 0)]         // already low: nothing to halve
    [InlineData(".mp3", 33.0, 1, 44100, 32, 0)]         // snapped down to a real constant bitrate
    [InlineData(".mp3", 100.9, 2, 44100, 96, 0)]
    [InlineData(".mp3", 900.0, 2, 44100, 320, 0)]
    [InlineData(".mp3", 200.0, 2, 22050, 160, 0)]       // the low-rate family stops at 160
    [InlineData(".ogg", 20.0, 2, 44100, 48, 0)]         // Vorbis cannot be steered lower
    [InlineData(".ogg", 20.0, 1, 44100, 32, 0)]
    [InlineData(".opus", 20.9, 2, 48000, 20, 0)]
    [InlineData(".m4a", 3.0, 2, 44100, 16, 0)]
    public void A_bitrate_is_brought_to_what_the_codec_can_do(string extension, double wanted, int channels, int rate, int kbps, int resample) =>
        Assert.Equal((kbps, resample), AudioCompressor.Settle(extension, wanted, channels, rate));

    [Theory]
    [InlineData("a.wav", ".flac")]
    [InlineData("a.WAV", ".flac")]
    [InlineData("a.aiff", ".flac")]
    [InlineData("a.aif", ".flac")]
    [InlineData("a.flac", ".flac")]
    [InlineData("a.mp3", ".mp3")]
    [InlineData("a.m4a", ".m4a")]
    [InlineData("a.ogg", ".ogg")]
    [InlineData("a.opus", ".opus")]
    [InlineData("C:\\música\\canción.wav", ".flac")]
    public void The_output_extension_is_flac_for_lossless_sources_and_the_sources_own_otherwise(string source, string extension) =>
        Assert.Equal(extension, AudioCompressor.OutputExtension(source));

    // ───────── Audio: lossless ─────────

    [Fact]
    public async Task A_wav_becomes_a_smaller_flac_that_decodes_to_the_same_sound()
    {
        var wav = await MakeAsync("canción ñandú.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=5");
        var before = Fingerprint(wav);
        var media = Media();

        var output = await AudioAsync(wav, Percent(70));

        Assert.Equal(Path.Combine(_root, "canción ñandú comprimido.flac"), output);
        Assert.True(SizeOf(output) < SizeOf(wav));
        Assert.Equal("flac", (await media.ProbeAsync(output, Token)).AudioCodec);
        var decoded = Path.Combine(_root, "decodificado.wav");
        await media.RunAsync(["-i", output, decoded], cancel: Token);
        Assert.InRange((await media.ProbeAsync(decoded, Token)).Duration, 4.98, 5.02);
        Assert.Equal(await PcmHashAsync(wav, "pcm_s16le"), await PcmHashAsync(output, "pcm_s16le"));
        Assert.Equal(before, Fingerprint(wav));     // the original is untouched
    }

    [Fact]
    public async Task A_24_bit_stereo_wav_keeps_its_depth_channels_and_rate_in_the_flac()
    {
        var wav = await MakeAsync("alta.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=3:sample_rate=48000", "-ac", "2", "-c:a", "pcm_s24le");

        var output = await AudioAsync(wav, Percent(10));     // quality means nothing for a lossless result

        var audio = StreamsOf(await ProbeJsonAsync(output), "audio").Single();
        Assert.Equal("24", Text(audio, "bits_per_raw_sample"));
        Assert.Equal("48000", Text(audio, "sample_rate"));
        Assert.Equal("2", Text(audio, "channels"));
        Assert.Equal(await PcmHashAsync(wav, "pcm_s24le"), await PcmHashAsync(output, "pcm_s24le"));
    }

    [Fact]
    public async Task An_aiff_becomes_a_flac_with_the_same_samples()
    {
        var aiff = await MakeAsync("viejo.aiff", "-f", "lavfi", "-i", "sine=frequency=523:duration=2", "-c:a", "pcm_s16be");

        var output = await AudioAsync(aiff, Percent(70));

        Assert.Equal("viejo comprimido.flac", Path.GetFileName(output));
        Assert.Equal(await PcmHashAsync(aiff, "pcm_s16le"), await PcmHashAsync(output, "pcm_s16le"));
    }

    [Fact]
    public async Task A_flac_is_repacked_at_the_highest_level_without_changing_a_sample()
    {
        var flac = await MakeNoiseAsync("suelto.flac", 3, "-c:a", "flac", "-compression_level", "0");

        var output = await AudioAsync(flac, Percent(70));

        Assert.Equal(".flac", Path.GetExtension(output));
        Assert.True(SizeOf(output) < SizeOf(flac));
        Assert.Equal(await PcmHashAsync(flac, "pcm_s16le"), await PcmHashAsync(output, "pcm_s16le"));
    }

    [Fact]
    public async Task The_tags_of_a_wav_reach_the_flac()
    {
        var wav = await MakeAsync("etiquetado.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-metadata", "artist=Autor01", "-metadata", "title=Prueba");

        var output = await AudioAsync(wav, Percent(70));

        var tags = (await Media().ProbeAsync(output, Token)).Tags;
        Assert.Equal("Autor01", tags["artist"]);
        Assert.Equal("Prueba", tags["title"]);
    }

    // ───────── Audio: lossy ─────────

    [Fact]
    public async Task A_lower_quality_makes_a_lighter_mp3()
    {
        var mp3 = await MakeAsync("canción.mp3", "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-c:a", "libmp3lame", "-b:a", "192k");
        var media = Media();

        var light = await AudioAsync(mp3, Percent(30));
        var heavy = await AudioAsync(mp3, Percent(90));

        Assert.NotEqual(light, heavy);
        Assert.True(SizeOf(light) < SizeOf(heavy), $"{SizeOf(light)} B at 30 should weigh less than {SizeOf(heavy)} B at 90");
        foreach (var file in new[] { light, heavy })
        {
            var probe = await media.ProbeAsync(file, Token);
            Assert.Equal("mp3", probe.AudioCodec);
            Assert.InRange(probe.Duration, 4.9, 5.2);
            Assert.Equal(1, probe.Channels);     // the tone is mono and stays mono
        }
    }

    [Fact]
    public async Task The_presets_weigh_less_the_stronger_they_are()
    {
        var mp3 = await MakeNoiseAsync("ruido.mp3", 5, "-c:a", "libmp3lame", "-b:a", "320k");

        var sizes = new List<long>();
        foreach (var quality in new[] { 100, 85, 70, 50, 35 })      // Maximum is the last
            sizes.Add(SizeOf(await AudioAsync(mp3, new CompressOptions(CompressMode.Preset, quality))));

        for (var i = 1; i < sizes.Count; i++)
            Assert.True(sizes[i] < sizes[i - 1], $"sizes by quality 100, 85, 70, 50, 35: {string.Join(", ", sizes)}");
    }

    [Theory]
    [InlineData(20 * 1024L)]
    [InlineData(20_000L)]       // decimal KB: a 32 kbps MP3 of 5 s alone would be 20 000 bytes, so it takes a lower sample rate
    [InlineData(12_000L)]
    public async Task A_wanted_size_gives_an_mp3_at_or_under_it(long target)
    {
        var mp3 = await MakeAsync("cinco.mp3", "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-c:a", "libmp3lame", "-b:a", "192k");

        var output = await AudioAsync(mp3, Target(target));

        var size = SizeOf(output);
        Assert.InRange(size, 8 * 1024, target);
        Assert.InRange((await Media().ProbeAsync(output, Token)).Duration, 4.9, 5.2);
    }

    [Theory]
    [InlineData("m4a")]
    [InlineData("ogg")]
    [InlineData("opus")]
    public async Task A_wanted_size_is_respected_by_every_lossy_codec(string extension)
    {
        var source = await MakeNoiseAsync($"objetivo.{extension}", 6, "-ac", "2", "-b:a", "256k");
        var target = SizeOf(source) * 45 / 100;

        var output = await AudioAsync(source, Target(target));

        Assert.InRange(SizeOf(output), (long)(target * 0.4), target);
        Assert.InRange((await Media().ProbeAsync(output, Token)).Duration, 5.8, 6.3);
    }

    [Fact]
    public async Task A_wanted_size_that_the_first_try_overshoots_is_corrected()
    {
        // The AAC encoder goes over its bitrate at the low end, so the first try is too big and the second lands under.
        var m4a = await MakeNoiseAsync("musica.m4a", 10, "-ac", "2", "-c:a", "aac", "-b:a", "256k");

        var output = await AudioAsync(m4a, Target(30_000));

        Assert.InRange(SizeOf(output), 15_000, 30_000);
        Assert.Equal("aac", (await Media().ProbeAsync(output, Token)).AudioCodec);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    [InlineData("opus")]
    public async Task Every_lossy_format_is_recompressed_in_its_own_codec_with_its_tags(string extension)
    {
        var source = await MakeNoiseAsync($"etiquetas.{extension}", 4, "-b:a", "192k", "-metadata", "artist=Autor01", "-metadata", "title=Prueba");
        var codec = extension switch { "mp3" => "mp3", "m4a" => "aac", "ogg" => "vorbis", _ => "opus" };

        var output = await AudioAsync(source, new CompressOptions(CompressMode.Preset, 35));

        Assert.Equal($".{extension}", Path.GetExtension(output));
        var probe = await Media().ProbeAsync(output, Token);
        Assert.Equal(codec, probe.AudioCodec);
        Assert.InRange(probe.Duration, 3.8, 4.3);
        Assert.True(SizeOf(output) < SizeOf(source), $"{SizeOf(output)} B should be under {SizeOf(source)} B");
        Assert.Equal("Autor01", await TagAsync(output, "artist"));
        Assert.Equal("Prueba", await TagAsync(output, "title"));
    }

    [Theory]
    [InlineData("mp3", 1)]
    [InlineData("mp3", 2)]
    [InlineData("m4a", 1)]
    [InlineData("ogg", 1)]
    [InlineData("opus", 1)]
    public async Task The_channels_of_a_lossy_file_stay_as_they_are(string extension, int channels)
    {
        var source = await MakeNoiseAsync($"canales.{extension}", 3, "-ac", channels.ToString(), "-b:a", "192k");

        var output = await AudioAsync(source, Percent(50));

        Assert.Equal(channels, (await Media().ProbeAsync(output, Token)).Channels);
    }

    [Fact]
    public async Task Re_encoding_never_raises_the_bitrate_of_the_source()
    {
        var source = await MakeNoiseAsync("pobre.mp3", 5, "-c:a", "libmp3lame", "-b:a", "64k");

        var output = await AudioAsync(source, Percent(100));     // asks for 256 kbps

        Assert.True(SizeOf(output) <= SizeOf(source) * 1.05, $"{SizeOf(output)} B from a source of {SizeOf(source)} B");
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("flac")]
    public async Task The_cover_art_is_kept_where_the_container_can_hold_it(string extension)
    {
        var cover = MakePhoto(Path.Combine(_root, "portada.jpg"), 300, 300);
        var source = await MakeAsync($"con portada.{extension}", "-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-i", cover,
            "-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v:0", "attached_pic", "-metadata", "title=Con portada");

        var output = await AudioAsync(source, Percent(70));

        var probe = await ProbeJsonAsync(output);
        var picture = Assert.Single(StreamsOf(probe, "video"));
        Assert.Equal("1", Text(picture.GetProperty("disposition"), "attached_pic"));
        Assert.Equal("Con portada", await TagAsync(output, "title"));
    }

    [Fact]
    public async Task A_cover_that_cannot_be_carried_over_is_dropped_instead_of_failing_the_job()
    {
        // An MP3 that really is a Matroska file with an H.264 stream: the MP3 muxer takes only pictures, so the first try fails.
        var matroska = await MakeAsync("raro.mkv", "-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-f", "lavfi", "-i",
            "testsrc2=size=160x120:rate=5:duration=3", "-c:a", "libmp3lame", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-metadata", "artist=Autor01");
        var source = Path.Combine(_root, "raro.mp3");
        File.Move(matroska, source);

        var output = await AudioAsync(source, Percent(70));

        Assert.Empty(StreamsOf(await ProbeJsonAsync(output), "video"));
        Assert.Equal("mp3", (await Media().ProbeAsync(output, Token)).AudioCodec);
        Assert.Equal("Autor01", await TagAsync(output, "artist"));
    }

    [Fact]
    public async Task Cancelling_an_audio_job_leaves_no_partial_file()
    {
        // Three minutes of noise take the FLAC encoder a few seconds at its highest level: time to stop it half way.
        var wav = await MakeAsync("largo.wav", "-f", "lavfi", "-i", "anoisesrc=d=180:c=pink:r=44100:a=0.3", "-ac", "2");
        var output = OutputPaths.Beside(wav, "comprimido", ".flac");
        using var cancel = new CancellationTokenSource();

        var job = AudioCompressor.CompressAsync(Media(), wav, output, Percent(70), null, cancel.Token);
        var giveUp = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(output) && !job.IsCompleted && DateTime.UtcNow < giveUp)
            await Task.Delay(10, Token);
        Assert.True(File.Exists(output), "the encode never started");
        Assert.False(job.IsCompleted, "the job ended before it could be cancelled");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(new[] { wav }, Directory.GetFiles(_root));
    }

    // ───────── Audio: refusals ─────────

    [Fact]
    public async Task Audio_that_cannot_be_compressed_fails_with_a_message_and_leaves_nothing_behind()
    {
        var media = Media();
        var broken = Path.Combine(_root, "roto.mp3");
        await File.WriteAllTextAsync(broken, "this is not audio", Token);
        var picture = await MakeAsync("imagen.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=5:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var silent = Path.Combine(_root, "mudo.mp3");
        File.Move(picture, silent);
        var other = Path.Combine(_root, "otro.wma");
        await File.WriteAllTextAsync(other, "x", Token);

        foreach (var source in new[] { broken, silent, other })
        {
            var output = Path.Combine(_root, Path.GetFileNameWithoutExtension(source) + " comprimido" + AudioCompressor.OutputExtension(source));
            var error = await Assert.ThrowsAsync<PanelException>(() => AudioCompressor.CompressAsync(media, source, output, Percent(70), null, Token));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.False(File.Exists(output), $"{error.Message} left {output}");
        }
        Assert.Equal(new[] { "mudo.mp3", "otro.wma", "roto.mp3" }, Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task Audio_is_never_written_over_a_file_that_exists()
    {
        var mp3 = await MakeNoiseAsync("origen.mp3", 2, "-b:a", "128k");
        var taken = Path.Combine(_root, "ocupado.mp3");
        await File.WriteAllTextAsync(taken, "mine", Token);

        await Assert.ThrowsAsync<PanelException>(() => AudioCompressor.CompressAsync(Media(), mp3, taken, Percent(70), null, Token));

        Assert.Equal("mine", await File.ReadAllTextAsync(taken, Token));
    }

    // ───────── Video: quality → CRF ─────────

    [Theory]
    [InlineData(100, 18)]
    [InlineData(85, 22)]
    [InlineData(70, 26)]
    [InlineData(50, 30)]
    [InlineData(35, 34)]
    [InlineData(60, 28)]        // halfway between 50 and 70
    [InlineData(10, 40)]
    [InlineData(1, 40)]
    public void Quality_maps_to_the_documented_crf(int quality, double crf) => Assert.Equal(crf, VideoCompressor.Crf(quality));

    [Fact]
    public void A_higher_quality_never_means_a_higher_crf()
    {
        var previous = double.MaxValue;
        for (var quality = 1; quality <= 100; quality++)
        {
            var crf = VideoCompressor.Crf(quality);
            Assert.True(crf <= previous, $"quality {quality} gives CRF {crf} after {previous}");
            previous = crf;
        }
    }

    [Theory]
    [InlineData(1_000, 1_300_000L, 1_000_000L, 900)]        // 30 % over: ask for 10 % less
    [InlineData(1_000, 1_081_000L, 1_000_000L, 900)]        // just beyond the 8 % tolerance
    [InlineData(1_000, 1_070_000L, 1_000_000L, null)]       // inside it: good enough
    [InlineData(1_000, 600_000L, 1_000_000L, null)]         // under: nothing to correct
    [InlineData(105, 2_000_000L, 1_000_000L, 100)]          // the floor is 100 kbps
    [InlineData(100, 2_000_000L, 1_000_000L, null)]         // already there: another pass would give the same file
    public void The_second_pass_is_repeated_only_when_the_result_is_too_big_and_there_is_room(int kbps, long size, long target, int? expected) =>
        Assert.Equal(expected, VideoCompressor.LowerBitrate(kbps, size, target));

    // ───────── Video ─────────

    [Fact]
    public async Task A_lower_quality_makes_a_lighter_video_in_h264_and_aac()
    {
        var clip = await MakeClipAsync("vídeo ñ.mp4");
        var before = Fingerprint(clip);
        var media = Media();

        var light = await VideoAsync(clip, Percent(30));
        var heavy = await VideoAsync(clip, Percent(90));

        Assert.Equal("vídeo ñ comprimido.mp4", Path.GetFileName(light));
        Assert.True(SizeOf(light) < SizeOf(heavy), $"{SizeOf(light)} B at 30 should weigh less than {SizeOf(heavy)} B at 90");
        foreach (var file in new[] { light, heavy })
        {
            var probe = await ProbeJsonAsync(file);
            var video = StreamsOf(probe, "video").Single();
            Assert.Equal("h264", Text(video, "codec_name"));
            Assert.Equal("yuv420p", Text(video, "pix_fmt"));
            Assert.Equal("1280", Text(video, "width"));
            Assert.Equal("aac", Text(StreamsOf(probe, "audio").Single(), "codec_name"));
            var summary = await media.ProbeAsync(file, Token);
            Assert.InRange(summary.Duration, 5.7, 6.3);
            Assert.Equal("Mi vídeo", summary.Tags["title"]);
        }
        Assert.Equal(before, Fingerprint(clip));     // the original is untouched
    }

    [Fact]
    public async Task The_video_presets_weigh_less_the_stronger_they_are()
    {
        var clip = await MakeClipAsync("presets.mp4");

        var sizes = new List<long>();
        foreach (var quality in new[] { 85, 70, 50, 35 })      // Light, Balanced, Strong, Maximum
            sizes.Add(SizeOf(await VideoAsync(clip, new CompressOptions(CompressMode.Preset, quality))));

        for (var i = 1; i < sizes.Count; i++)
            Assert.True(sizes[i] < sizes[i - 1], $"sizes by quality 85, 70, 50, 35: {string.Join(", ", sizes)}");
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mov")]
    [InlineData("mkv")]
    [InlineData("m4v")]
    public async Task Every_video_container_keeps_its_extension_and_gets_h264_and_aac(string extension)
    {
        var clip = await MakeAsync($"contenedor.{extension}", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-metadata", "title=Mi vídeo");

        var output = await VideoAsync(clip, Percent(50));

        Assert.Equal($".{extension}", Path.GetExtension(output));
        var probe = await ProbeJsonAsync(output);
        Assert.Equal("h264", Text(StreamsOf(probe, "video").Single(), "codec_name"));
        Assert.Equal("aac", Text(StreamsOf(probe, "audio").Single(), "codec_name"));
        var summary = await Media().ProbeAsync(output, Token);
        Assert.InRange(summary.Duration, 2.7, 3.3);
        Assert.Equal("Mi vídeo", summary.Tags["title"]);
    }

    [Fact]
    public async Task A_maximum_height_shrinks_the_picture_with_an_even_width_and_never_enlarges_it()
    {
        var clip = await MakeClipAsync("tamaño.mp4");
        var awkward = await MakeAsync("raro.mp4", "-f", "lavfi", "-i", "testsrc2=size=1000x562:rate=25:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var media = Media();

        var small = await media.ProbeAsync(await VideoAsync(clip, Percent(70, maxSize: 360)), Token);
        Assert.Equal((640, 360), (small.Width, small.Height));

        var odd = await media.ProbeAsync(await VideoAsync(awkward, Percent(70, maxSize: 360)), Token);
        Assert.Equal(360, odd.Height);
        Assert.Equal(0, odd.Width % 2);
        Assert.InRange(odd.Width, 638, 644);                // 360 · 1000 / 562 = 640.6

        foreach (var cap in new[] { 720, 1080 })             // as tall as the clip, or taller: left as it is
        {
            var same = await media.ProbeAsync(await VideoAsync(clip, Percent(70, maxSize: cap)), Token);
            Assert.Equal((1280, 720), (same.Width, same.Height));
        }
    }

    [Fact]
    public async Task A_picture_with_odd_sides_is_made_even_for_h264()
    {
        // 4:4:4 is the one H.264 flavour that holds an odd size; 4:2:0 output cannot.
        var clip = await MakeAsync("impar.mkv", "-f", "lavfi", "-i", "testsrc2=size=322x202:rate=25:duration=2", "-vf", "format=yuv444p,crop=321:201", "-c:v", "libx264", "-pix_fmt", "yuv444p");
        var media = Media();
        var odd = await media.ProbeAsync(clip, Token);
        Assert.Equal((321, 201), (odd.Width, odd.Height));

        var output = await VideoAsync(clip, Percent(70));

        var probe = await media.ProbeAsync(output, Token);
        Assert.Equal(0, probe.Width % 2);
        Assert.Equal(0, probe.Height % 2);
        Assert.InRange(probe.Width, 318, 322);
        Assert.InRange(probe.Height, 198, 202);
    }

    [Fact]
    public async Task A_phone_video_turned_by_its_rotation_comes_out_upright()
    {
        var clip = await MakeClipAsync("horizontal.mp4");
        var phone = await MakeAsync("móvil.mp4", "-display_rotation", "90", "-i", clip, "-c", "copy");     // stored 1280x720, shown 720x1280
        var media = Media();
        var shown = await media.ProbeAsync(phone, Token);
        Assert.Equal((720, 1280), (shown.Width, shown.Height));

        var upright = await ProbeJsonAsync(await VideoAsync(phone, Percent(70)));
        var video = StreamsOf(upright, "video").Single();
        Assert.Equal(("720", "1280"), (Text(video, "width"), Text(video, "height")));     // turned for good, no matrix left
        Assert.False(video.TryGetProperty("side_data_list", out _));

        // The cap is a height, so a portrait clip gives up more: 360 high is 202 wide.
        var small = await media.ProbeAsync(await VideoAsync(phone, Percent(70, maxSize: 360)), Token);
        Assert.Equal(360, small.Height);
        Assert.InRange(small.Width, 200, 204);
    }

    [Fact]
    public async Task A_wanted_size_gives_a_video_at_or_just_under_it_with_progress_over_two_passes()
    {
        var clip = await MakeClipAsync("objetivo.mp4");
        var target = SizeOf(clip) * 40 / 100;
        var progress = new Recorder();
        var logs = PassLogs();

        var output = await VideoAsync(clip, Target(target), progress);

        var size = SizeOf(output);
        Assert.True(size <= target * 1.08, $"{size} B for a target of {target} B");
        Assert.True(size >= target * 0.5, $"{size} B is far under a target of {target} B");
        Assert.InRange((await Media().ProbeAsync(output, Token)).Duration, 5.7, 6.3);

        var values = progress.Values;
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.InRange(value, 0, 1));
        Assert.Equal(values.Order(), values);                           // never goes back
        Assert.Contains(values, value => value <= 0.45 + 1e-9);         // the first pass takes the first 45 %
        Assert.True(values[^1] >= 0.99, $"ends at {values[^1]}");
        Assert.Empty(PassLogs().Except(logs));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(25)]
    [InlineData(12)]
    public async Task A_wanted_size_is_respected_across_the_range(int percent)
    {
        var clip = await MakeClipAsync("rango.mp4");
        var target = SizeOf(clip) * percent / 100;
        var logs = PassLogs();

        var output = await VideoAsync(clip, Target(target));

        var size = SizeOf(output);
        Assert.True(size <= target * 1.08, $"{size} B for a target of {target} B ({percent} %)");
        Assert.True(size >= target * 0.6, $"{size} B is far under a target of {target} B ({percent} %)");
        Assert.Empty(PassLogs().Except(logs));
    }

    [Fact]
    public async Task A_wanted_size_bigger_than_the_source_does_not_inflate_it()
    {
        var clip = await MakeClipAsync("grande.mp4");

        var output = await VideoAsync(clip, Target(SizeOf(clip) * 10));

        Assert.True(SizeOf(output) <= SizeOf(clip) * 1.2, $"{SizeOf(output)} B from a source of {SizeOf(clip)} B");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_video_without_sound_stays_without_sound(bool wantedSize)
    {
        var clip = await MakeClipAsync("mudo.mp4", withAudio: false);
        var logs = PassLogs();

        var output = await VideoAsync(clip, wantedSize ? Target(SizeOf(clip) / 2) : Percent(60));

        var probe = await Media().ProbeAsync(output, Token);
        Assert.False(probe.HasAudio);
        Assert.True(probe.HasVideo);
        Assert.Empty(StreamsOf(await ProbeJsonAsync(output), "audio"));
        Assert.InRange(probe.Duration, 5.7, 6.3);
        Assert.Equal("Mi vídeo", probe.Tags["title"]);
        Assert.Empty(PassLogs().Except(logs));
    }

    [Fact]
    public async Task Audio_that_is_aac_and_light_enough_is_copied_and_heavier_audio_is_re_encoded()
    {
        var light = await MakeClipAsync("ligero.mp4");      // AAC at 64 kbps, under the 128 wanted
        var heavy = await MakeAsync("pesado.mp4", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3", "-f", "lavfi", "-i",
            "anoisesrc=d=3:c=pink:r=44100:a=0.3", "-ac", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "256k");

        var copied = await VideoAsync(light, Percent(70));
        var recoded = await VideoAsync(heavy, Percent(70));

        Assert.Equal(await PacketHashAsync(light), await PacketHashAsync(copied));      // the very same AAC packets
        Assert.NotEqual(await PacketHashAsync(heavy), await PacketHashAsync(recoded));
        var audio = StreamsOf(await ProbeJsonAsync(recoded), "audio").Single();
        Assert.Equal("aac", Text(audio, "codec_name"));
        Assert.InRange(long.Parse(Text(audio, "bit_rate")!), 100_000, 160_000);          // 128 kbps, give or take the encoder

        // Below quality 50 the audio is held to 96 kbps: the 256 kbps track is re-encoded lower than at 70.
        var lower = StreamsOf(await ProbeJsonAsync(await VideoAsync(heavy, Percent(40))), "audio").Single();
        Assert.InRange(long.Parse(Text(lower, "bit_rate")!), 70_000, 120_000);
    }

    [Fact]
    public async Task Cancelling_between_the_passes_leaves_neither_partial_files_nor_logs()
    {
        // 1080p makes each pass last a couple of seconds, long enough to stop the second one half way.
        var clip = await MakeAsync("largo.mp4", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=25:duration=12", "-c:v", "libx264",
            "-preset", "ultrafast", "-crf", "32", "-pix_fmt", "yuv420p");
        var media = Media();
        var output = OutputPaths.Beside(clip, "comprimido", ".mp4");
        var logs = PassLogs();
        using var cancel = new CancellationTokenSource();

        var job = VideoCompressor.CompressAsync(media, clip, output, Target(1_000_000), null, cancel.Token);
        var giveUp = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(output) && !job.IsCompleted && DateTime.UtcNow < giveUp)     // the second pass has started when its file appears
            await Task.Delay(10, Token);
        Assert.True(File.Exists(output), "the second pass never started");
        Assert.False(job.IsCompleted, "the job ended before it could be cancelled");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(new[] { clip }, Directory.GetFiles(_root));
        Assert.Empty(PassLogs().Except(logs));
    }

    [Fact]
    public async Task Cancelling_a_single_pass_job_leaves_no_partial_file()
    {
        var clip = await MakeAsync("otro largo.mp4", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=25:duration=12", "-c:v", "libx264",
            "-preset", "ultrafast", "-crf", "32", "-pix_fmt", "yuv420p");
        var output = OutputPaths.Beside(clip, "comprimido", ".mp4");
        using var cancel = new CancellationTokenSource();

        var job = VideoCompressor.CompressAsync(Media(), clip, output, Percent(70), null, cancel.Token);
        var giveUp = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(output) && !job.IsCompleted && DateTime.UtcNow < giveUp)
            await Task.Delay(10, Token);
        Assert.True(File.Exists(output), "the encode never started");
        Assert.False(job.IsCompleted, "the job ended before it could be cancelled");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(new[] { clip }, Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Cancelling_during_the_first_pass_leaves_neither_partial_files_nor_logs()
    {
        var clip = await MakeAsync("primero.mp4", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=25:duration=12", "-c:v", "libx264",
            "-preset", "ultrafast", "-crf", "32", "-pix_fmt", "yuv420p");
        var output = OutputPaths.Beside(clip, "comprimido", ".mp4");
        var logs = PassLogs();
        using var cancel = new CancellationTokenSource();

        var job = VideoCompressor.CompressAsync(Media(), clip, output, Target(1_000_000), null, cancel.Token);
        var giveUp = DateTime.UtcNow.AddSeconds(60);
        while (!PassLogs().Except(logs).Any() && !job.IsCompleted && DateTime.UtcNow < giveUp)     // the first pass is running once its log exists
            await Task.Delay(5, Token);
        Assert.NotEmpty(PassLogs().Except(logs));
        Assert.False(job.IsCompleted, "the job ended before it could be cancelled");
        Assert.False(File.Exists(output), "the second pass had already started");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(new[] { clip }, Directory.GetFiles(_root));
        Assert.Empty(PassLogs().Except(logs));
    }

    [Fact]
    public async Task A_failure_in_the_second_pass_leaves_neither_partial_files_nor_logs()
    {
        var clip = await MakeClipAsync("fallo.mp4");
        var logs = PassLogs();
        // The first pass writes nothing, so it goes through; the second cannot open its output: the folder is not there.
        var output = Path.Combine(_root, "no existe", "fallo comprimido.mp4");

        var error = await Assert.ThrowsAsync<PanelException>(() => VideoCompressor.CompressAsync(Media(), clip, output, Target(SizeOf(clip) / 2), null, Token));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(new[] { clip }, Directory.GetFiles(_root));
        Assert.Empty(PassLogs().Except(logs));
    }

    [Fact]
    public async Task Video_that_cannot_be_compressed_fails_with_a_message_and_leaves_nothing_behind()
    {
        var media = Media();
        var broken = Path.Combine(_root, "roto.mp4");
        await File.WriteAllTextAsync(broken, "this is not a video", Token);
        var song = await MakeNoiseAsync("canción.mp3", 1, "-b:a", "64k");
        var imposter = Path.Combine(_root, "sonido.mp4");
        File.Move(song, imposter);
        var clip = await MakeClipAsync("bueno.mp4");
        var logs = PassLogs();

        foreach (var (source, options) in new[] { (broken, Percent(70)), (imposter, Percent(70)), (clip, Target(0)) })
        {
            var output = Path.Combine(_root, Path.GetFileNameWithoutExtension(source) + " comprimido.mp4");
            var error = await Assert.ThrowsAsync<PanelException>(() => VideoCompressor.CompressAsync(media, source, output, options, null, Token));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.False(File.Exists(output), $"{error.Message} left {output}");
        }
        Assert.Equal(new[] { "bueno.mp4", "roto.mp4", "sonido.mp4" }, Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray());
        Assert.Empty(PassLogs().Except(logs));
    }

    [Fact]
    public async Task Video_is_never_written_over_a_file_that_exists()
    {
        var clip = await MakeClipAsync("origen.mp4");
        var taken = Path.Combine(_root, "ocupado.mp4");
        await File.WriteAllTextAsync(taken, "mine", Token);

        await Assert.ThrowsAsync<PanelException>(() => VideoCompressor.CompressAsync(Media(), clip, taken, Percent(70), null, Token));

        Assert.Equal("mine", await File.ReadAllTextAsync(taken, Token));
    }

    /// <summary>
    /// The two-pass logs x264 leaves around: in the temp folder under a name of ours, and FFmpeg's default ones in the
    /// folder it runs from. A test notes them before it starts and checks that none has been added when it ends.
    /// </summary>
    private static string[] PassLogs()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "Mandarina", "panels");
        var own = Directory.Exists(scratch) ? Directory.GetFiles(scratch, "*-0.log*") : [];
        return [.. own, .. Directory.GetFiles(Path.GetDirectoryName(Media().Ffmpeg)!, "ffmpeg2pass*")];
    }
}
