// Mandarina: the tools of the wheel that run as presets (merge, split and create PDF, normalize, mute, extract) and the
// extra conversion targets, run through the real engines on generated files. The tools with a panel (compress, metadata, trim,
// crop, …) have their own Mandarina*Tests.

using Avalonia.Headless.XUnit;
using Filee.App.Mandarina;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public sealed class MandarinaToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-tools").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<ConversionJob> RunAsync(IReadOnlyList<string> files, Preset preset)
    {
        // Built like AppHost builds it: with the PDF merger and the archive combiner.
        var env = new EngineEnvironment(Path.Combine(_root, "data"));
        var converters = EngineRegistry.CreateAll(env);
        var catalog = new ConverterCatalog(converters) { Priority = new AppSettings().EnginePriority };
        await using var queue = new JobQueue(catalog, EngineRegistry.FindPdfMerger(converters),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueue>.Instance,
            EngineRegistry.FindFileCombiner(converters), EngineRegistry.FindTiffMerger(converters));
        var job = new ConversionJob(files, preset, preset.Name);
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job;
    }

    private static Preset Tool(string id, params string[] files)
    {
        TestServices.EnsureInitialized("es");
        return WheelCatalog.ToolsFor(files, AppHost.Get<ILocalizer>()).Single(t => t.Id == id).Preset!;
    }

    private string Photo(string name, int quality = 100)
    {
        using var image = new MagickImage("plasma:orange-purple", 900, 600);
        image.Quality = (uint)quality;
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Artist, "Autor01");
        profile.SetValue(ExifTag.Software, "Mandarina test");
        image.SetProfile(profile);
        var path = Path.Combine(_root, name);
        image.Write(path, MagickFormat.Jpg);
        return path;
    }

    private string Pdf(string name, int pages)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        var path = Path.Combine(_root, name);
        document.Save(path);
        return path;
    }

    [AvaloniaFact]
    public async Task Pdfs_merge_and_split()
    {
        var a = Pdf("a.pdf", 2);
        var b = Pdf("b.pdf", 3);

        var merged = Assert.Single((await RunAsync([a, b], Tool("mergePDF", a, b))).Outputs);
        using (var document = PdfReader.Open(merged, PdfDocumentOpenMode.Import))
            Assert.Equal(5, document.Pages.Count);

        var pages = (await RunAsync([b], Tool("splitPDF", b))).Outputs.ToList();
        Assert.Equal(3, pages.Count);
        Assert.All(pages, page => Assert.Equal("b páginas", Path.GetFileName(Path.GetDirectoryName(page))));
    }

    [AvaloniaFact]
    public async Task Images_become_one_pdf()
    {
        var one = Photo("uno.jpg");
        var two = Photo("dos.jpg");
        var pdf = Assert.Single((await RunAsync([one, two], Tool("createPDF", one, two))).Outputs);
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.Pages.Count);
    }

    [AvaloniaFact]
    public async Task Subtitles_become_plain_text()
    {
        var srt = Path.Combine(_root, "captions.srt");
        await File.WriteAllTextAsync(srt, "1\r\n00:00:01,000 --> 00:00:02,000\r\n<i>Hola</i> mundo\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSegunda\r\nlínea\r\n\r\n");
        var txt = Assert.Single((await RunAsync([srt], new Preset { Name = "TXT", TargetFormat = "txt" })).Outputs);
        Assert.Equal("Hola mundo\r\nSegunda línea\r\n", await File.ReadAllTextAsync(txt));
    }

    [AvaloniaFact]
    public async Task Media_tools_mute_and_normalize()
    {
        if (EngineEnvironment.FindBundled("ffmpeg") is not { } engine)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var ffmpeg = Path.Combine(engine, "bin", "ffmpeg.exe");
        var ffprobe = Path.Combine(engine, "bin", "ffprobe.exe");
        string Run(string exe, params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        string Probe(string file, string entries) =>
            Run(ffprobe, "-v", "error", "-show_entries", entries, "-of", "default=nw=1:nk=1", file).Trim();

        var clip = Path.Combine(_root, "clip.mp4");
        Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=25", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=2", "-c:v", "libx264", "-c:a", "aac", "-metadata", "title=Prueba", "-shortest", clip);
        var tone = Path.Combine(_root, "tono.wav");
        Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", tone);
        var mp3 = Path.Combine(_root, "tono.mp3");
        Run(ffmpeg, "-v", "error", "-i", tone, "-metadata", "artist=Autor01", mp3);

        var muted = Assert.Single((await RunAsync([clip], Tool("muteVideo", clip))).Outputs);
        Assert.Equal("clip sin audio.mp4", Path.GetFileName(muted));
        Assert.Equal("", Probe(muted, "stream=codec_type").Replace("video", "").Trim());

        Assert.Equal("Prueba", Probe(clip, "format_tags=title")); // muting writes a copy: the original keeps its tags

        var normalized = Assert.Single((await RunAsync([mp3], Tool("normalizeAudio", mp3))).Outputs);
        Assert.Equal("tono normalizado.mp3", Path.GetFileName(normalized));
        Assert.Equal("Autor01", Probe(mp3, "format_tags=artist"));
    }

    [AvaloniaFact]
    public async Task Archives_extract_into_a_folder_beside_them()
    {
        // Extracting needs the 7-Zip engine, which the installer bundles (build/fetch-engines.ps1 for a checkout).
        var converters = EngineRegistry.CreateAll(new EngineEnvironment(Path.Combine(_root, "data")));
        if (!new ConverterCatalog(converters).CreatePlanner().CanConvert("zip", Filee.Core.Formats.FormatRegistry.Folder))
            Assert.Skip("The 7-Zip engine is not installed.");

        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "hola.txt"), "hola");
        var zip = Path.Combine(_root, "proyecto.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, zip);

        var job = await RunAsync([zip], Tool("extractArchive", zip));
        Assert.Contains(job.Outputs, path => path.Contains("proyecto extraído", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_root, "proyecto extraído", "hola.txt")));
    }
}
