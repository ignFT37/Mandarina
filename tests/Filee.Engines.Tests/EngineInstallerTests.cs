// Optional engine downloads without the network: a fake HTTP handler serves generated archives.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Infrastructure;
using SharpCompress.Compressors.ZStandard;

namespace Filee.Engines.Tests;

public sealed class EngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-installer-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Serves byte arrays by URL; a string value is a redirect target.</summary>
    private sealed class FakeServer(Dictionary<string, object> content) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = content.GetValueOrDefault(url) switch
            {
                byte[] bytes => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                string location => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] TarGzip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
        return buffer.ToArray();
    }

    /// <summary>A conda package: a zip with metadata and the files as a zstd-compressed tarball.</summary>
    private static byte[] Conda(params (string Path, string Text)[] files)
    {
        using var tarball = new MemoryStream();
        using (var zstd = new CompressionStream(tarball, level: 3, leaveOpen: true))
        using (var tar = new TarWriter(zstd, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("metadata.json").Open()))
                writer.Write("{\"conda_pkg_format_version\": 2}");
            using (var entry = zip.CreateEntry("pkg-tool-1.0-0.tar.zst").Open())
                entry.Write(tarball.ToArray());
        }
        return buffer.ToArray();
    }

    private static EngineComponent Component(string id, string kind, byte[] bytes, string? sha256 = null) =>
        new(id, "1.0", $"https://example.test/{id}.{kind}", sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, kind);

    private EngineInstaller Installer(FakeServer server, params EngineComponent[] components) =>
        new(new HttpClient(server), _root, components.ToDictionary(c => c.Id));

    [Fact]
    public async Task Zip_engines_are_verified_unpacked_without_their_top_folder_and_reported()
    {
        var archive = Zip(("tool-1.0/tool.exe", "binary"), ("tool-1.0/readme.txt", "hello"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new() { [component.Url] = archive });
        var installer = Installer(server, component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        var reports = new List<EngineInstallProgress>();

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
        Assert.True(installer.IsInstalled(package));
        Assert.True(installer.IsUpToDate(package));
        Assert.Contains(reports, r => r.Stage == EngineInstallStage.Unpacking);
        Assert.Equal(1.0, reports.Where(r => r.Stage == EngineInstallStage.Downloading).Max(r => r.Fraction), 3);
        // Byte counts drive the speed and time-left display.
        var last = reports.Last(r => r.Stage == EngineInstallStage.Downloading);
        Assert.Equal((archive.Length, archive.Length), ((int)last.BytesDone, (int)last.BytesTotal));
        // Nothing is left over from downloading or unpacking.
        Assert.Equal(["tool"], Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => !n!.StartsWith(".downloads", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));

        // An installed, up-to-date package is not downloaded again.
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);

        installer.Uninstall(package);
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
    }

    [Fact]
    public async Task A_single_file_such_as_a_model_is_verified_and_kept_under_the_name_of_its_url()
    {
        // Mandarina: the speech model of the transcription tools is one 574 MB file, not an archive.
        var weights = Encoding.UTF8.GetBytes("pretend these are the weights of a speech model");
        var component = new EngineComponent("whisper", "large-v3-turbo-q5_0", "https://example.test/models/ggml-test-q5_0.bin",
            Convert.ToHexString(SHA256.HashData(weights)), weights.Length, "file");
        var server = new FakeServer(new() { [component.Url] = weights });
        var installer = Installer(server, component);
        var package = new EnginePackage("whisper", ["whisper"], 1000, []);

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.Equal(weights, await File.ReadAllBytesAsync(Path.Combine(_root, "whisper", "ggml-test-q5_0.bin"), TestContext.Current.CancellationToken));
        Assert.True(installer.IsInstalled(package));
        Assert.True(installer.IsUpToDate(package));
        // The download was moved into place, and no staging folder is left.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
        Assert.Equal(["whisper"], Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => !n!.StartsWith(".downloads", StringComparison.Ordinal)));

        installer.Uninstall(package);
        Assert.False(Directory.Exists(Path.Combine(_root, "whisper")));
    }

    [Fact]
    public async Task The_pinned_speech_model_downloads_from_its_real_address_and_matches_its_checksum()
    {
        // Mandarina: the real thing, 574 MB from Hugging Face (through its redirect to the CDN). Only when asked for.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MANDARINA_DOWNLOAD_TESTS") == "1", "set MANDARINA_DOWNLOAD_TESTS=1 to download 574 MB");
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var installer = new EngineInstaller(http, _root, EngineDownloads.Components);
        var package = EngineDownloads.Packages.Single(p => p.Id == "whisper");

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var model = Assert.Single(Directory.GetFiles(Path.Combine(_root, "whisper"), "*.bin"));
        Assert.Equal(EngineDownloads.Components["whisper"].Size, new FileInfo(model).Length);
        Assert.True(installer.IsUpToDate(package));
    }

    [Fact]
    public void The_small_speech_model_is_a_package_of_its_own_with_a_folder_of_its_own()
    {
        // Mandarina: a package replaces the whole folder of its component, so two models cannot share one: "whisper" is the
        // large one, "whisper-small" the small one, each pinned to a file of the same repository and commit.
        var package = EngineDownloads.Packages.Single(p => p.Id == "whisper-small");
        var component = EngineDownloads.Components[Assert.Single(package.Components)];

        Assert.Equal("whisper-small", component.Id);
        Assert.Equal("file", component.Kind);
        Assert.Equal("ggml-small-q8_0.bin", Path.GetFileName(new Uri(component.Url).AbsolutePath));
        Assert.Matches("^https://huggingface.co/ggerganov/whisper.cpp/resolve/[0-9a-f]{40}/ggml-small-q8_0.bin$", component.Url);
        Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
        Assert.Equal(264_464_607, component.Size);
        Assert.Equal(component.Size, package.InstalledSize);
        Assert.Empty(package.ConverterIds);
        Assert.NotEqual(EngineDownloads.Components["whisper"].Sha256, component.Sha256);
        Assert.DoesNotContain("whisper-small", EngineEnvironment.OwnCopyPrograms.Keys); // a data file: no program to point at
    }

    [Fact]
    public async Task The_two_speech_models_install_and_uninstall_each_in_its_own_folder()
    {
        // The real packages and file names, with made-up files behind the addresses.
        var large = Encoding.UTF8.GetBytes("pretend these are the weights of the large speech model");
        var small = Encoding.UTF8.GetBytes("and these the weights of the small one");
        EngineComponent Fake(string id, byte[] bytes)
        {
            var real = EngineDownloads.Components[id];
            return real with { Url = "https://example.test/models/" + Path.GetFileName(new Uri(real.Url).AbsolutePath),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Size = bytes.Length };
        }
        var (largeComponent, smallComponent) = (Fake("whisper", large), Fake("whisper-small", small));
        var installer = Installer(new FakeServer(new() { [largeComponent.Url] = large, [smallComponent.Url] = small }), largeComponent, smallComponent);
        var largePackage = EngineDownloads.Packages.Single(p => p.Id == "whisper");
        var smallPackage = EngineDownloads.Packages.Single(p => p.Id == "whisper-small");
        var largeFile = Path.Combine(_root, "whisper", "ggml-large-v3-turbo-q5_0.bin");
        var smallFile = Path.Combine(_root, "whisper-small", "ggml-small-q8_0.bin");

        await installer.InstallAsync(smallPackage, null, TestContext.Current.CancellationToken);

        Assert.True(installer.IsInstalled(smallPackage));
        Assert.False(installer.IsInstalled(largePackage)); // the small one is not the large one
        Assert.Equal(small, await File.ReadAllBytesAsync(smallFile, TestContext.Current.CancellationToken));
        Assert.Equal(smallComponent.Sha256, File.ReadAllText(Path.Combine(_root, "whisper-small", ".filee-component"))); // the mark the local installer writes too

        await installer.InstallAsync(largePackage, null, TestContext.Current.CancellationToken);

        Assert.Equal(large, await File.ReadAllBytesAsync(largeFile, TestContext.Current.CancellationToken));
        Assert.Equal(small, await File.ReadAllBytesAsync(smallFile, TestContext.Current.CancellationToken)); // installing the large one left the small one alone
        Assert.True(installer.IsUpToDate(largePackage) && installer.IsUpToDate(smallPackage));

        installer.Uninstall(smallPackage);

        Assert.False(Directory.Exists(Path.Combine(_root, "whisper-small")));
        Assert.True(File.Exists(largeFile));
        Assert.False(installer.IsInstalled(smallPackage));
        Assert.True(installer.IsUpToDate(largePackage));
    }

    [Fact]
    public async Task The_pinned_small_speech_model_downloads_from_its_real_address_and_matches_its_checksum()
    {
        // Mandarina: the real thing, 264 MB from Hugging Face (through its redirect to the CDN). Only when asked for.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MANDARINA_DOWNLOAD_TESTS") == "1", "set MANDARINA_DOWNLOAD_TESTS=1 to download 264 MB");
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var installer = new EngineInstaller(http, _root, EngineDownloads.Components);
        var package = EngineDownloads.Packages.Single(p => p.Id == "whisper-small");

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken); // it checks the SHA-256 before it keeps the file

        var model = Assert.Single(Directory.GetFiles(Path.Combine(_root, "whisper-small"), "*.bin"));
        Assert.Equal("ggml-small-q8_0.bin", Path.GetFileName(model));
        Assert.Equal(EngineDownloads.Components["whisper-small"].Size, new FileInfo(model).Length);
        Assert.True(installer.IsUpToDate(package));
    }

    [Fact]
    public void Transfer_rate_averages_over_the_last_seconds_and_estimates_the_time_left()
    {
        var rate = new TransferRate(TimeSpan.FromSeconds(5));
        Assert.Null(rate.Remaining(0, 1000));

        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromSeconds(0.2), 100); // too short to tell
        Assert.Equal(0, rate.BytesPerSecond);

        for (var second = 1; second <= 10; second++)
            rate.Add(TimeSpan.FromSeconds(second), second * 1_000_000L);
        Assert.Equal(1_000_000, rate.BytesPerSecond, 0);
        Assert.Equal(TimeSpan.FromSeconds(30), rate.Remaining(10_000_000, 40_000_000));

        // The speed follows a slower connection within the window instead of keeping the old average.
        for (var second = 11; second <= 20; second++)
            rate.Add(TimeSpan.FromSeconds(second), 10_000_000L + (second - 10) * 100_000L);
        Assert.Equal(100_000, rate.BytesPerSecond, 0);
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_rejected_and_nothing_is_installed()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive, sha256: new string('0', 64));
        var installer = Installer(new FakeServer(new() { [component.Url] = archive }), component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message);
        Assert.False(installer.IsInstalled(package));
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    public async Task An_incomplete_engine_update_preserves_the_working_install_and_its_marker(string kind)
    {
        var programs = EngineEnvironment.OwnCopyPrograms["ffmpeg"];
        byte[] Archive(params (string Path, string Text)[] files) => kind == "zip" ? Zip(files) : TarGzip(files);
        var previousArchive = Archive(($"ffmpeg-1.0/bin/{programs[0]}", "working encoder"),
            ($"ffmpeg-1.0/bin/{programs[1]}", "working probe"));
        var previousComponent = Component("ffmpeg", kind, previousArchive);
        var package = new EnginePackage("ffmpeg", ["ffmpeg"], 1, ["ffmpeg"]);
        var previousInstaller = Installer(new FakeServer(new() { [previousComponent.Url] = previousArchive }), previousComponent);
        await previousInstaller.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var incompleteArchive = Archive(($"ffmpeg-2.0/bin/{programs[0]}", "new encoder without probe"));
        var incompleteComponent = Component("ffmpeg", kind, incompleteArchive) with { Version = "2.0" };
        var installer = Installer(new FakeServer(new() { [incompleteComponent.Url] = incompleteArchive }), incompleteComponent);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("ffmpeg", error.Message);
        var folder = Path.Combine(_root, "ffmpeg");
        Assert.Equal("working encoder", File.ReadAllText(Path.Combine(folder, "bin", programs[0])));
        Assert.Equal("working probe", File.ReadAllText(Path.Combine(folder, "bin", programs[1])));
        Assert.Equal(previousComponent.Sha256, File.ReadAllText(Path.Combine(folder, ".filee-component")));
        Assert.True(previousInstaller.IsUpToDate(package));
        Assert.True(installer.IsInstalled(package));
        Assert.False(installer.IsUpToDate(package));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Theory]
    [InlineData("libreoffice", "LibreOffice.app", "soffice")]
    [InlineData("calibre", "calibre.app", "ebook-convert")]
    public void A_Mac_application_bundle_requires_its_conversion_program(string id, string app, string program)
    {
        var folder = Path.Combine(_root, app, "Contents", "MacOS");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README"), "not an executable");

        Assert.Throws<InvalidDataException>(() => EngineInstaller.RequiredProgramFolder(id, _root, windows: false));

        File.WriteAllText(Path.Combine(folder, program), "binary");
        Assert.Equal(folder, EngineInstaller.RequiredProgramFolder(id, _root, windows: false));
    }

    [Fact]
    public async Task Redirects_to_plain_http_mirrors_are_followed_because_the_hash_is_checked()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new()
        {
            [component.Url] = "https://example.test/mirror-list",
            ["https://example.test/mirror-list"] = "http://mirror.example.test/tool.zip",
            ["http://mirror.example.test/tool.zip"] = archive,
        });
        var installer = Installer(server, component);

        await installer.InstallAsync(new EnginePackage("tool", ["tool"], 1, ["tool"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, server.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
    }

    [Fact]
    public async Task Conda_packages_unpack_their_windows_binaries_and_get_the_runtime_next_to_them()
    {
        var gs = Conda(("Library/bin/gswin64c.exe", "exe"), ("Library/bin/gsdll64.dll", "dll"), ("Library/share/doc.txt", "doc"), ("info/index.json", "{}"));
        var runtime = Conda(("Library/bin/vcruntime140.dll", "runtime"), ("Library/bin/msvcp140.dll", "c++"));
        var ghostscript = Component("ghostscript", "conda", gs);
        var vcruntime = Component("vcruntime", "conda", runtime);
        var installer = Installer(new FakeServer(new() { [ghostscript.Url] = gs, [vcruntime.Url] = runtime }), ghostscript, vcruntime);
        var package = new EnginePackage("ghostscript", ["ghostscript", "vcruntime"], 1, ["ghostscript"]);

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var folder = Path.Combine(_root, "ghostscript");
        Assert.Equal(["gsdll64.dll", "gswin64c.exe", "msvcp140.dll", "vcruntime140.dll"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')).Order(StringComparer.Ordinal));
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(folder, "vcruntime140.dll")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void Conda_packages_cannot_write_outside_the_engine_folder()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "evil.conda");
        File.WriteAllBytes(file, Conda(("Library/bin/../../../evil.txt", "evil")));

        Assert.Throws<InvalidDataException>(() => EngineInstaller.UnpackConda(file, Path.Combine(_root, "target"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task LibreOffice_extensions_go_into_the_installed_LibreOffice_and_need_it()
    {
        var oxt = Zip(("description.xml", "<description/>"), ("H2Orestart.jar", "jar"));
        var extension = Component("h2orestart", "oxt", oxt);
        var server = new FakeServer(new() { [extension.Url] = oxt });
        var installer = Installer(server, extension);
        var package = new EnginePackage("filter", ["h2orestart"], 1, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Directory.CreateDirectory(Path.Combine(_root, "libreoffice", "share", "extensions"));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "libreoffice", "share", "extensions", "H2Orestart", "H2Orestart.jar")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void LibreOffice_is_trimmed_to_what_headless_conversion_needs()
    {
        var root = Path.Combine(_root, "lo");
        foreach (var folder in new[] { "help", "readmes", "program", "share/gallery", "share/extensions/dict-en", "share/extensions/dict-ko", "share/extensions/dict-fr", "share/extensions/H2Orestart" })
            Directory.CreateDirectory(Path.Combine(root, folder));
        File.WriteAllText(Path.Combine(root, "LibreOffice.msi"), "");

        EngineInstaller.TrimLibreOffice(root);

        var remaining = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["program", "share", "share/extensions", "share/extensions/H2Orestart", "share/extensions/dict-en", "share/extensions/dict-ko"], remaining);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void Every_package_component_is_pinned_in_the_manifest()
    {
        foreach (var package in EngineDownloads.Packages.Where(EngineDownloads.CanDownload))
        {
            foreach (var id in package.Components)
            {
                var component = EngineDownloads.Components[id];
                Assert.StartsWith("https://", component.Url);
                Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
                Assert.True(component.Size > 0);
            }
            Assert.True(EngineDownloads.DownloadSize(package) > 0);
        }
        // rhwp is bundled with the installer, but its download is pinned in the same list.
        Assert.Contains("rhwp", EngineDownloads.Components.Keys);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<EngineInstallProgress> report) : IProgress<EngineInstallProgress>
    {
        public void Report(EngineInstallProgress value) => report(value);
    }
}
