// Mandarina: the Microsoft Office engine. What doesn't need Office runs always: the routes and costs with a fake
// installation, the switch and the license badge, the self-test's memory, password detection, private copies and the
// process helpers. The conversions with the real Word, Excel and PowerPoint run only with MANDARINA_OFFICE_TESTS=1 on a PC
// that has them (they open hidden Office processes, and the ones that need a particular application skip when it can't
// save: this PC's Office is not activated and its Word hangs when it saves, which is the case "fails fast" is tested on).

using System.Diagnostics;
using System.Runtime.Versioning;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Com;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

/// <summary>An Office installation that exists only in the test.</summary>
internal sealed class FakeOfficeProbe(OfficeLicense license, params OfficeKind[] installed) : IOfficeProbe
{
    public const string Version = "16.0.14334.20974";

    public OfficeAppInfo? Find(OfficeKind kind) =>
        installed.Contains(kind) ? new OfficeAppInfo(kind, $@"C:\Office\{kind}.exe", Version) : null;

    public (OfficeLicense State, string Signature) License() => (license, license.ToString());

    /// <summary>What <c>OfficeConverter</c> uses to tell a result from an Office of another version or license.</summary>
    public string Fingerprint => $"{Version}|{license}";
}

/// <summary>Hands out paths in a folder, like the app does next to the original.</summary>
internal sealed class FolderAllocator(string folder) : IOutputAllocator
{
    public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, "out" + suffix + "." + extension);
}

[Collection("Office")]
public class OfficeEngineTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private static readonly OfficeKind[] Everything = [OfficeKind.Word, OfficeKind.Excel, OfficeKind.PowerPoint];

    /// <summary>Short waits, so a test that expects Office to give up doesn't take minutes.</summary>
    private static readonly OfficeTimings Fast = new(
        Job: TimeSpan.FromSeconds(120), SelfTest: TimeSpan.FromSeconds(25), Idle: TimeSpan.FromSeconds(2),
        DialogGrace: TimeSpan.FromSeconds(8), DialogTicks: 3, QueueWait: TimeSpan.FromMinutes(5),
        Tick: TimeSpan.FromMilliseconds(250), QuitWait: TimeSpan.FromSeconds(8));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mandarina-office-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly List<OfficeConverter> _converters = [];

    private EngineEnvironment Env => new(Path.Combine(_folder, "data"));

    private static bool RealOffice => Environment.GetEnvironmentVariable("MANDARINA_OFFICE_TESTS") == "1" && OperatingSystem.IsWindows();

    /// <summary>An engine over a fake installation (switch on unless said).</summary>
    private OfficeConverter Fake(OfficeLicense license = OfficeLicense.Licensed, bool enabled = true, params OfficeKind[] installed) =>
        new(Env, new FakeOfficeProbe(license, installed), Fast, () => enabled);

    private static string Fingerprint(OfficeLicense license = OfficeLicense.Licensed) => new FakeOfficeProbe(license).Fingerprint;

    /// <summary>The route planner over every real engine, with <paramref name="office"/> in the place of Office's own.</summary>
    private RoutePlanner Planner(OfficeConverter office) =>
        new ConverterCatalog(fx.Converters.Select(c => c.Id == "office" ? office : c)) { Priority = new AppSettings().EnginePriority }.CreatePlanner();

    /// <summary>Some step of the route is Office's (not LibreOffice's: the ids share a word).</summary>
    private static bool UsesOffice(Route? route) => route is not null && route.Steps.Any(step => step.Converter.Id == "office");

    private static string Steps(Route? route) =>
        route is null ? "none" : string.Join(" > ", route.Steps.Select(s => $"{s.Converter.Id}:{s.From}->{s.To}"));

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var office in _converters)
            {
                try { office.CloseAllAsync().Wait(TimeSpan.FromSeconds(20)); } catch (Exception ex) when (ex is AggregateException or TimeoutException) { }
            }
        }
        OfficeSelfTest.Forget();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ───────────────────────── What it converts ─────────────────────────

    [Fact]
    public void Each_application_has_the_planned_pairs_and_costs()
    {
        var word = OfficeConverter.BuildEdges([OfficeKind.Word]);
        foreach (var from in new[] { "doc", "dot", "docx", "docm", "dotx", "dotm", "rtf", "odt" })
            Assert.Contains(new ConversionEdge(from, "pdf", 10, FirstStepOnly: true), word);
        Assert.Contains(new ConversionEdge("doc", "docx", 10, FirstStepOnly: true), word);
        Assert.Contains(new ConversionEdge("docx", "doc", 10, FirstStepOnly: true), word);
        // Word's reflow of a PDF is only for a PDF the user wants as DOCX, never a step towards another format.
        Assert.Contains(new ConversionEdge("pdf", "docx", 10, DirectOnly: true, FirstStepOnly: true), word);
        Assert.DoesNotContain(word, e => e.To is "csv" or "txt" or "html" or "md"); // those stay with the built-in engines

        var excel = OfficeConverter.BuildEdges([OfficeKind.Excel]);
        foreach (var from in new[] { "xls", "xlsx", "xlsm", "xlt", "xltx", "ods" })
            Assert.Contains(new ConversionEdge(from, "pdf", 10, FirstStepOnly: true), excel);
        Assert.Contains(new ConversionEdge("xls", "xlsx", 5, FirstStepOnly: true), excel);
        Assert.Contains(new ConversionEdge("xlsx", "xls", 10, FirstStepOnly: true), excel);
        Assert.DoesNotContain(excel, e => e.To is "csv" or "tsv");

        var powerPoint = OfficeConverter.BuildEdges([OfficeKind.PowerPoint]);
        foreach (var from in new[] { "ppt", "pptx", "pptm", "ppsx", "potx", "odp" })
            Assert.Contains(new ConversionEdge(from, "pdf", 0, FirstStepOnly: true), powerPoint);
        Assert.Contains(new ConversionEdge("ppt", "pptx", 10, FirstStepOnly: true), powerPoint);
        Assert.Contains(new ConversionEdge("pptx", "ppt", 10, FirstStepOnly: true), powerPoint);

        Assert.Empty(OfficeConverter.BuildEdges([]));
    }

    [Fact]
    public void With_Office_the_conversions_it_does_best_go_through_it()
    {
        var planner = Planner(Fake(OfficeLicense.Licensed, true, Everything));

        foreach (var (from, to) in new[]
                 {
                     ("docx", "pdf"), ("doc", "pdf"), ("rtf", "pdf"), ("odt", "pdf"), ("doc", "docx"), ("docx", "doc"),
                     ("ppt", "pdf"), ("pptx", "pdf"), ("odp", "pdf"), ("ppt", "pptx"), ("pptx", "ppt"),
                     ("xls", "pdf"), ("xlsx", "pdf"), ("ods", "pdf"), ("xls", "xlsx"), ("xlsx", "xls"),
                     ("pdf", "docx"),
                 })
        {
            var route = planner.Plan(from, to);
            Assert.True(route is { Steps.Count: 1 } && route.Steps[0].Converter.Id == "office", $"{from} → {to}: {Steps(route)}");
        }

        // Longer routes use Office for the first hop: PPT → PDF (Office) → PNG (PDFium).
        Assert.Equal("office:ppt->pdf > pdfium:pdf->png", Steps(planner.Plan("ppt", "png")));
    }

    [Fact]
    public void Word_is_never_a_step_of_a_longer_route_from_a_PDF()
    {
        var planner = Planner(Fake(OfficeLicense.Licensed, true, Everything));

        foreach (var to in new[] { "md", "txt", "html", "epub", "odt", "rtf", "pptx", "hwpx", "jpg", "png" })
        {
            var route = planner.Plan("pdf", to);
            Assert.False(route is { Steps.Count: > 1 } && route.Steps[0].Converter.Id == "office", $"pdf → {to}: {Steps(route)}");
        }
    }

    [Fact]
    public void Office_only_converts_the_files_that_are_its_own_documents()
    {
        var planner = Planner(Fake(OfficeLicense.Licensed, true, Everything));
        var others = Planner(Fake(OfficeLicense.Licensed, true));

        // Whatever else is installed (rhwp may be missing), a file that is not a Word, Excel or PowerPoint document never
        // goes through them: no spreadsheet turned into DOCX for Word, no Word document sent through slides to a PDF. (Word does
        // open an ODT, so on a PC with no other way from it to a format, e.g. no Pandoc, Office is the only route and may be it.)
        foreach (var (from, to) in new[]
                 {
                     ("xlsx", "docx"), ("md", "pdf"), ("txt", "pdf"), ("html", "pdf"), ("epub", "pdf"), ("hwpx", "pdf"),
                     ("csv", "pdf"), ("odt", "md"), ("md", "docx"), ("jpg", "pdf"),
                 })
        {
            if (others.Plan(from, to) is null)
                continue;
            var route = planner.Plan(from, to);
            Assert.False(route is not null && route.Steps.Any(s => s.Converter.Id == "office"), $"{from} → {to}: {Steps(route)}");
        }
        // As a first step it is welcome: a DOC reaches everything a DOCX does, a PPT every picture of its slides.
        Assert.StartsWith("office:doc->docx", Steps(planner.Plan("doc", "md")));
        Assert.StartsWith("office:ppt->pdf", Steps(planner.Plan("ppt", "jpg")));
    }

    [Fact]
    public void Without_Office_nothing_goes_through_it()
    {
        var without = Planner(Fake(OfficeLicense.Licensed, true));
        var withOffice = Planner(Fake(OfficeLicense.Licensed, true, Everything));

        foreach (var (from, to) in new[] { ("docx", "pdf"), ("xlsx", "pdf"), ("pptx", "pdf"), ("pdf", "docx"), ("ppt", "pdf"), ("doc", "docx"), ("xls", "xlsx") })
        {
            Assert.False(UsesOffice(without.Plan(from, to)), Steps(without.Plan(from, to)));
            Assert.True(UsesOffice(withOffice.Plan(from, to)), Steps(withOffice.Plan(from, to)));
        }
    }

    [Fact]
    public void Only_the_installed_applications_add_routes()
    {
        var planner = Planner(Fake(OfficeLicense.Licensed, true, OfficeKind.Word));

        Assert.True(UsesOffice(planner.Plan("docx", "pdf")));
        Assert.False(UsesOffice(planner.Plan("xlsx", "pdf")));
        Assert.False(UsesOffice(planner.Plan("pptx", "pdf")));
        Assert.Equal(OfficeConverter.BuildEdges([OfficeKind.Word]).Count, Fake(OfficeLicense.Licensed, true, OfficeKind.Word).Edges.Count);
    }

    // ───────────────────────── Settings → Engines ─────────────────────────

    [Fact]
    public void Not_installed_it_is_unavailable_and_adds_no_conversions_even_to_a_planner_that_ignores_availability()
    {
        var office = Fake();

        var status = office.GetStatus();

        Assert.False(status.IsAvailable);
        Assert.Equal("engine.reason.not_installed", status.ReasonKey);
        Assert.False(office.IsInstalled);
        Assert.Empty(office.Edges); // tests build planners straight from the converters, whatever they report
        Assert.Equal("Microsoft Office", office.DisplayName);
        Assert.Equal("office", office.Id);
    }

    [Fact]
    public void Installed_it_is_available_with_its_version_and_applications()
    {
        var office = Fake(OfficeLicense.Licensed, true, Everything);

        var status = office.GetStatus();

        Assert.True(status.IsAvailable);
        Assert.Null(status.ReasonKey);
        Assert.Equal(FakeOfficeProbe.Version, status.Version);
        Assert.Contains("Word, Excel, PowerPoint", status.Detail);
        Assert.True(office.IsInstalled);
    }

    [Fact]
    public void Turned_off_in_the_settings_it_is_unavailable_but_still_shows_its_version()
    {
        var office = Fake(OfficeLicense.Licensed, false, Everything);

        var status = office.GetStatus();

        Assert.False(status.IsAvailable);
        Assert.Equal("engine.reason.office_off", status.ReasonKey);
        Assert.Equal(FakeOfficeProbe.Version, status.Version);
        Assert.Empty(office.Edges);
        Assert.False(UsesOffice(Planner(office).Plan("docx", "pdf")));
        // A planner that takes the converters as they come (no availability check) doesn't find Office either.
        Assert.False(UsesOffice(new RoutePlanner(fx.Converters.Select(c => c.Id == "office" ? office : c)).Plan("pptx", "pdf")));
    }

    [Fact]
    public void An_Office_that_is_not_activated_is_flagged_but_stays_available()
    {
        var status = Fake(OfficeLicense.Restricted, true, Everything).GetStatus();

        Assert.True(status.IsAvailable);
        Assert.Equal("engine.reason.office_unlicensed", status.ReasonKey);
    }

    [Fact]
    public void The_switch_defaults_to_off_in_code_and_on_in_the_settings()
    {
        Assert.False(EngineEnvironment.OfficeEnabled); // tests never depend on the machine's Office
        Assert.True(new AppSettings().OfficeEnabled);
        Assert.Contains("office", new AppSettings().EnginePriority);
        Assert.True(new AppSettings().EnginePriority.IndexOf("office") < new AppSettings().EnginePriority.IndexOf("libreoffice"));
        // The engine registered for the app is there, and off until the settings turn it on.
        var registered = fx.Converters.OfType<OfficeConverter>().Single();
        Assert.False(registered.GetStatus().IsAvailable);
    }

    // ───────────────────────── The self-test ─────────────────────────

    [Fact]
    public void A_broken_application_is_left_out_of_the_routes()
    {
        var office = Fake(OfficeLicense.Licensed, true, Everything);
        Assert.True(UsesOffice(Planner(office).Plan("docx", "pdf")));

        OfficeSelfTest.Record(Env.DataDirectory, OfficeKind.Word, Fingerprint(), works: false, "office.error.unlicensed", "Word", persist: false);

        Assert.DoesNotContain(office.Edges, e => e.From == "docx" && e.To == "pdf");
        Assert.Contains(office.Edges, e => e.From == "xlsx" && e.To == "pdf"); // Excel still works
        Assert.False(UsesOffice(Planner(office).Plan("docx", "pdf")));
        Assert.Contains("Word: office.error.unlicensed", office.GetStatus().Detail);
    }

    [Fact]
    public void A_self_test_result_does_not_outlive_the_Office_version_or_its_license()
    {
        OfficeSelfTest.Record(Env.DataDirectory, OfficeKind.Word, Fingerprint(OfficeLicense.Restricted), works: false, "office.error.unlicensed", "Word", persist: true);

        Assert.Equal(OfficeSelfTest.Verdict.Broken, OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Word, Fingerprint(OfficeLicense.Restricted)).Verdict);
        // Office got activated: the fingerprint changed, ask again.
        Assert.Equal(OfficeSelfTest.Verdict.Unknown, OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Word, Fingerprint(OfficeLicense.Licensed)).Verdict);
        Assert.Equal(OfficeSelfTest.Verdict.Unknown, OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Word, "16.0.1|Restricted").Verdict);
    }

    [Fact]
    public void A_broken_result_is_saved_only_when_Office_is_not_activated_and_check_again_forgets_it()
    {
        var file = Path.Combine(Env.DataDirectory, "office-self-test.json");

        OfficeSelfTest.Record(Env.DataDirectory, OfficeKind.Excel, Fingerprint(), works: false, "office.error.timeout", "25 s", persist: false);
        Assert.False(File.Exists(file)); // a slow start or an update is asked again soon, not remembered for days

        OfficeSelfTest.Record(Env.DataDirectory, OfficeKind.Word, Fingerprint(OfficeLicense.Restricted), works: false, "office.error.unlicensed", "Word", persist: true);
        Assert.True(File.Exists(file));

        // A new run of Mandarina: nothing in memory, the file is read.
        OfficeSelfTest.ForgetMemory();
        var word = OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Word, Fingerprint(OfficeLicense.Restricted));
        Assert.Equal(OfficeSelfTest.Verdict.Broken, word.Verdict);
        Assert.Equal("office.error.unlicensed", word.Key);
        Assert.Equal(OfficeSelfTest.Verdict.Unknown, OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Excel, Fingerprint()).Verdict);

        // Settings → Engines → Check again.
        EngineEnvironment.ForgetSystemCopies();
        Assert.False(File.Exists(file));
        Assert.Equal(OfficeSelfTest.Verdict.Unknown, OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Word, Fingerprint(OfficeLicense.Restricted)).Verdict);
    }

    [Fact]
    public void An_Office_that_is_not_activated_and_does_not_answer_is_blamed_on_its_license()
    {
        var timeout = new ConversionFailureException("office.error.timeout", "25 s");
        var dialog = new ConversionFailureException("office.error.dialog", "Sign in");

        Assert.Equal(("office.error.unlicensed", "Word"), OfficeConverter.Describe(timeout, OfficeKind.Word, OfficeLicense.Restricted));
        Assert.Equal(("office.error.unlicensed", "Excel"), OfficeConverter.Describe(dialog, OfficeKind.Excel, OfficeLicense.Restricted));
        // An activated Office that hangs is just that, and other failures keep their own message.
        Assert.Equal(("office.error.timeout", "25 s"), OfficeConverter.Describe(timeout, OfficeKind.Word, OfficeLicense.Licensed));
        Assert.Equal(("office.error.open", "x"), OfficeConverter.Describe(new ConversionFailureException("office.error.open", "x"), OfficeKind.Word, OfficeLicense.Restricted));
        Assert.Equal(("office.error.failed", "boom"), OfficeConverter.Describe(new InvalidOperationException("boom"), OfficeKind.Word, OfficeLicense.Unknown));
    }

    [Fact]
    public void A_working_result_is_remembered_in_memory_and_replaces_a_broken_one()
    {
        OfficeSelfTest.Record(Env.DataDirectory, OfficeKind.Word, Fingerprint(), works: false, "office.error.timeout", null, persist: true);
        OfficeSelfTest.Record(Env.DataDirectory, OfficeKind.Word, Fingerprint(), works: true, null, null, persist: false);

        Assert.Equal(OfficeSelfTest.Verdict.Works, OfficeSelfTest.Get(Env.DataDirectory, OfficeKind.Word, Fingerprint()).Verdict);
        Assert.False(File.Exists(Path.Combine(Env.DataDirectory, "office-self-test.json")));
    }

    // ───────────────────────── Files ─────────────────────────

    /// <summary>An OLE container (what "Encrypt with password" makes of a DOCX) whose directory lists EncryptedPackage.</summary>
    internal static byte[] EncryptedPackageFile(bool listsEncryptedPackage = true)
    {
        var bytes = new byte[512 * 3];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)9).CopyTo(bytes, 0x1E); // sectors of 512 bytes
        BitConverter.GetBytes(0).CopyTo(bytes, 0x30); // the directory is in sector 0, which starts at 512
        var name = System.Text.Encoding.Unicode.GetBytes(listsEncryptedPackage ? "EncryptedPackage" : "WordDocument");
        name.CopyTo(bytes, 512 + 128); // the second directory entry
        return bytes;
    }

    [Fact]
    public void An_encrypted_package_is_recognised_and_nothing_else_is()
    {
        Directory.CreateDirectory(_folder);
        var encrypted = Path.Combine(_folder, "locked.docx");
        File.WriteAllBytes(encrypted, EncryptedPackageFile());
        var legacy = Path.Combine(_folder, "old.doc");
        File.WriteAllBytes(legacy, EncryptedPackageFile(listsEncryptedPackage: false));
        var plain = Path.Combine(_folder, "plain.docx");
        if (OperatingSystem.IsWindows())
            WriteSample(OfficeKind.Word, plain);
        var text = Path.Combine(_folder, "text.docx");
        File.WriteAllText(text, "not an office file");

        Assert.True(OfficeFiles.IsEncryptedPackage(encrypted));
        Assert.False(OfficeFiles.IsEncryptedPackage(legacy)); // an old OLE file: Office says if it has a password
        Assert.False(OfficeFiles.IsEncryptedPackage(text));
        Assert.False(OfficeFiles.IsEncryptedPackage(Path.Combine(_folder, "missing.docx")));
        if (OperatingSystem.IsWindows())
            Assert.False(OfficeFiles.IsEncryptedPackage(plain));
    }

    [Fact]
    public async Task A_password_protected_package_fails_with_its_own_message_before_Office_is_asked()
    {
        Directory.CreateDirectory(_folder);
        var locked = Path.Combine(_folder, "locked.docx");
        File.WriteAllBytes(locked, EncryptedPackageFile());
        var step = new ConversionStep(locked, "docx", "pdf", new Preset { TargetFormat = "pdf" }, new FolderAllocator(_folder), _folder);

        var failure = await Assert.ThrowsAsync<ConversionFailureException>(() =>
            Fake(OfficeLicense.Licensed, true, Everything).ConvertAsync(step, null, CancellationToken.None));

        Assert.Equal("office.error.password", failure.Key);
    }

    [Fact]
    public async Task Without_Office_or_with_the_switch_off_a_conversion_says_so()
    {
        Directory.CreateDirectory(_folder);
        var input = Path.Combine(_folder, "a.docx");
        File.WriteAllText(input, "x");
        var step = new ConversionStep(input, "docx", "pdf", new Preset { TargetFormat = "pdf" }, new FolderAllocator(_folder), _folder);

        foreach (var office in new[] { Fake(), Fake(OfficeLicense.Licensed, false, Everything) })
        {
            var failure = await Assert.ThrowsAsync<ConversionFailureException>(() => office.ConvertAsync(step, null, CancellationToken.None));
            Assert.Equal("office.error.not_installed", failure.Key);
        }
        var odd = await Assert.ThrowsAsync<ConversionFailureException>(() =>
            Fake(OfficeLicense.Licensed, true, Everything).ConvertAsync(step with { To = "epub" }, null, CancellationToken.None));
        Assert.Equal("office.error.unsupported", odd.Key);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_private_copy_has_the_bytes_and_not_the_Internet_mark()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Alternate data streams are an NTFS thing");
        Directory.CreateDirectory(_folder);
        var original = Path.Combine(_folder, "downloaded.docx");
        File.WriteAllText(original, "bytes of the file");
        File.WriteAllText(original + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var scratch = Directory.CreateDirectory(Path.Combine(_folder, "scratch")).FullName;

        var copy = OfficeFiles.PrivateCopy(original, scratch);

        Assert.Equal("downloaded.docx", Path.GetFileName(copy));
        Assert.Equal("bytes of the file", File.ReadAllText(copy));
        Assert.True(File.Exists(original + ":Zone.Identifier") || File.ReadAllText(original + ":Zone.Identifier").Contains("ZoneId"));
        Assert.Throws<FileNotFoundException>(() => File.ReadAllText(copy + ":Zone.Identifier")); // Office would open it in Protected View
    }

    [Fact]
    public void The_private_copy_gets_a_short_name_when_the_path_would_be_too_long()
    {
        Directory.CreateDirectory(_folder);
        var original = Path.Combine(_folder, new string('x', 150) + ".pptx");
        File.WriteAllText(original, "slides");
        var scratch = Directory.CreateDirectory(Path.Combine(_folder, new string('d', 60))).FullName;

        var copy = OfficeFiles.PrivateCopy(original, scratch);

        Assert.Equal("document.pptx", Path.GetFileName(copy));
        Assert.True(copy.Length < 260);
    }

    [Fact]
    public void The_private_copy_works_while_the_user_has_the_file_open()
    {
        Directory.CreateDirectory(_folder);
        var original = Path.Combine(_folder, "open-in-word.docx");
        File.WriteAllText(original, "content");
        using var userHasItOpen = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); // like Word does
        var scratch = Directory.CreateDirectory(Path.Combine(_folder, "scratch")).FullName;

        var copy = OfficeFiles.PrivateCopy(original, scratch);

        Assert.Equal("content", File.ReadAllText(copy));
    }

    [Fact]
    public void The_server_command_of_a_program_id_is_read_with_or_without_quotes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(@"C:\Program Files\Microsoft Office\Root\Office16\WINWORD.EXE",
            ExecutableOfWindows("\"C:\\Program Files\\Microsoft Office\\Root\\Office16\\WINWORD.EXE\" /Automation"));
        Assert.Equal(@"C:\Office\EXCEL.EXE", ExecutableOfWindows(@"C:\Office\EXCEL.EXE /automation"));
        Assert.Equal(@"C:\Office\POWERPNT.EXE", ExecutableOfWindows(@"C:\Office\POWERPNT.EXE"));
        Assert.Null(ExecutableOfWindows("  "));
        Assert.Null(ExecutableOfWindows(null));
    }

    [SupportedOSPlatform("windows")]
    private static string? ExecutableOfWindows(string? command) => WindowsOfficeProbe.ExecutableOf(command);

    [Fact]
    public void Password_prompts_are_told_from_other_open_errors()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(LooksLikePassword("The password is incorrect. Word cannot open the document."));
        Assert.True(LooksLikePassword("La contraseña no es correcta"));
        Assert.True(LooksLikePassword("Das Kennwort ist falsch"));
        Assert.True(LooksLikePassword("密码不正确"));
        Assert.False(LooksLikePassword("This file could not be found."));
    }

    [SupportedOSPlatform("windows")]
    private static bool LooksLikePassword(string message) => OfficeOperations.LooksLikePassword(message);

    [Fact]
    public void Office_dialogs_are_told_from_ordinary_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(IsDialog("#32770"));
        Assert.True(IsDialog("NUIDialog"));
        Assert.True(IsDialog("bosa_sdm_Microsoft Office Word 11.0"));
        Assert.False(IsDialog("OpusApp"));
        Assert.False(IsDialog("XLMAIN"));
        Assert.False(IsDialog("MsoSplash"));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsDialog(string className) => OfficeNative.IsDialogClass(className);

    // ───────────────────────── Processes ─────────────────────────

    /// <summary>A process whose command line ends like the one COM gives the Office it starts (and that does nothing for a while).</summary>
    private static Process Sleeper(bool embedding)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c \"ping -n 8 127.0.0.1 >nul & rem {(embedding ? "-Embedding" : "-Other")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        return Process.Start(start)!;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Only_the_process_COM_started_is_ours_and_only_it_is_killed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows processes");
        using var started = Sleeper(embedding: true);
        using var opened = Sleeper(embedding: false);
        try
        {
            Assert.Contains("-Embedding", OfficeNative.CommandLineOf(started.Id));
            var ours = OfficeProcesses.StartedByCom([started.Id, opened.Id]);
            Assert.Equal(started.Id, ours.Id);
            Assert.Equal(started.StartTime, ours.Started);
            // The user's own Office (no -Embedding) is never taken for ours...
            Assert.Equal(0, OfficeProcesses.StartedByCom([opened.Id]).Id);
            // ...and two candidates are too many to be sure of either.
            using var another = Sleeper(embedding: true);
            Assert.Equal(0, OfficeProcesses.StartedByCom([started.Id, another.Id]).Id);
            another.Kill();

            // A recycled id (another start time) is left alone; the right one is killed.
            OfficeProcesses.Kill(started.Id, started.StartTime.AddMinutes(-5));
            Assert.False(started.HasExited);
            OfficeProcesses.Kill(started.Id, started.StartTime);
            Assert.True(started.WaitForExit(5000));
            Assert.False(opened.HasExited);
        }
        finally
        {
            if (!started.HasExited) started.Kill();
            if (!opened.HasExited) opened.Kill();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void An_Office_that_Mandarina_started_goes_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job objects");
        using var child = Sleeper(embedding: true);
        try
        {
            // A child of this test run may be in the job of whatever started the run, which Windows doesn't let it leave;
            // the Office that COM starts for us is in none (the real Office tests prove it works there).
            Assert.SkipWhen(InJob(child), "The test process is already in a job object.");
            Assert.True(OfficeNative.KillWithMandarina(child.Id)); // the job closes, and kills what is in it, when the process ends
            Assert.True(InJob(child));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    private static bool InJob(Process process) => IsProcessInJob(process.Handle, IntPtr.Zero, out var inJob) && inJob;

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_license_status_of_this_PC_is_readable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows licensing");
        var (state, signature) = OfficeNative.QueryOfficeLicense();

        Assert.True(Enum.IsDefined(state));
        Assert.Equal(state == OfficeLicense.Unknown, signature.Length == 0);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_dialog_of_a_process_is_found_by_its_title()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("MANDARINA_UI_TESTS") == "1",
            "Opens a message box on the screen: set MANDARINA_UI_TESTS=1 to run it.");
        var start = new ProcessStartInfo("powershell.exe",
            "-NoProfile -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show('x', 'Mandarina test dialog')\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        try
        {
            string? title = null;
            for (var i = 0; i < 40 && title is null; i++)
            {
                Thread.Sleep(250);
                title = OfficeNative.BlockingDialogOf(process.Id);
            }
            Assert.Equal("Mandarina test dialog", title);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }

    // ───────────────────────── The real Office ─────────────────────────

    [SupportedOSPlatform("windows")]
    private OfficeConverter Real(OfficeTimings? timings = null)
    {
        var office = new OfficeConverter(Env, new WindowsOfficeProbe(), timings ?? Fast, () => true);
        _converters.Add(office);
        return office;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteSample(OfficeKind kind, string path) => OfficeSamples.Write(kind, path);

    private async Task<ConversionJob> RunAsync(OfficeConverter office, string source, string target, Preset? preset = null)
    {
        var catalog = new ConverterCatalog(fx.Converters.Select(c => c.Id == "office" ? office : c)) { Priority = new AppSettings().EnginePriority };
        await using var queue = new JobQueue(catalog, EngineRegistry.FindPdfMerger(fx.Converters),
            combiner: EngineRegistry.FindFileCombiner(fx.Converters), tiffMerger: EngineRegistry.FindTiffMerger(fx.Converters));
        var job = new ConversionJob([source], preset ?? new Preset { TargetFormat = target }, "office test");
        await queue.RunAsync(job);
        return job;
    }

    private static string DocxText(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return reader.ReadToEnd();
    }

    private static bool IsPdfA(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)).Contains("pdfaid:part");

    private static string Why(ConversionJob job) => string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));

    private static string PdfText(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return string.Join(" ", pdf.GetPages().Select(page => page.Text));
    }

    private static string Producer(string path) => PdfReader.Open(path, PdfDocumentOpenMode.Import).Info.Producer;

    private static HashSet<int> Running(string name) => OfficeProcesses.Pids(name);

    /// <summary>Waits until none of <paramref name="ids"/> of process <paramref name="name"/> is left.</summary>
    private static bool Gone(string name, IEnumerable<int> ids, int seconds = 15)
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            if (!ids.Any(id => Running(name).Contains(id)))
                return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Skips unless the test run asked for the real Office and the application is installed.</summary>
    private static void NeedsRealOffice(OfficeKind? kind = null)
    {
        Assert.SkipUnless(RealOffice, "Runs the real Office (hidden processes): set MANDARINA_OFFICE_TESTS=1.");
        if (kind is { } wanted && OperatingSystem.IsWindows())
            Assert.SkipWhen(RealProbe().Find(wanted) is null, $"{wanted} is not installed.");
    }

    [SupportedOSPlatform("windows")]
    private static IOfficeProbe RealProbe() => new WindowsOfficeProbe();

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Excel_converts_a_workbook_to_PDF_and_closes_the_Excel_it_started()
    {
        NeedsRealOffice(OfficeKind.Excel);
        var before = Running("EXCEL");
        var office = Real();
        var broken = await office.CheckAsync(OfficeKind.Excel, TestContext.Current.CancellationToken);
        Assert.SkipWhen(broken is not null, $"This Excel can't save: {broken?.Key} {broken?.Detail}");
        var workbook = Path.Combine(_folder, "sales.xlsx");
        Directory.CreateDirectory(_folder);
        OfficeSamples.Write(OfficeKind.Excel, workbook);

        var job = await RunAsync(office, workbook, "pdf");

        Assert.True(job.State == JobState.Completed, Why(job));
        var pdf = Assert.Single(job.Outputs);
        Assert.Contains("Mandarina", PdfText(pdf));
        Assert.Contains("Excel", Producer(pdf));
        // Every sheet goes into the one PDF, with text that isn't Latin (the sales workbook of the built-in engine's tests).
        var sales = await RunAsync(office, OfficeTests.SalesWorkbook(_folder), "pdf");
        Assert.True(sales.State == JobState.Completed, Why(sales));
        Assert.True(PdfReader.Open(sales.Outputs.Single(), PdfDocumentOpenMode.Import).PageCount >= 2);
        Assert.Contains("서울 강남", PdfText(sales.Outputs.Single()));
        // The Excel that Mandarina started is in a job object, so Windows closes it with Mandarina (also if it crashes)...
        var started = Running("EXCEL").Where(id => !before.Contains(id)).ToList();
        Assert.NotEmpty(started);
        using (var ours = Process.GetProcessById(started[0]))
            Assert.True(InJob(ours), "the Excel the engine started is not in Mandarina's job object");
        // ...and closes by itself when the engine is done; the user's is untouched (it didn't exist).
        await office.CloseAllAsync();
        Assert.True(Gone("EXCEL", started), "An Excel started by the engine is still running");
        Assert.True(File.Exists(workbook)); // the original is never touched
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PowerPoint_converts_a_deck_to_PDF_and_between_PPTX_and_PPT()
    {
        NeedsRealOffice(OfficeKind.PowerPoint);
        var office = Real();
        var broken = await office.CheckAsync(OfficeKind.PowerPoint, TestContext.Current.CancellationToken);
        Assert.SkipWhen(broken is not null, $"This PowerPoint can't save: {broken?.Key} {broken?.Detail}");
        Directory.CreateDirectory(_folder);
        var deck = Path.Combine(_folder, "deck.pptx");
        OfficeSamples.Write(OfficeKind.PowerPoint, deck);

        var pdf = await RunAsync(office, deck, "pdf");
        Assert.True(pdf.State == JobState.Completed, Why(pdf));
        Assert.Contains("PowerPoint", Producer(pdf.Outputs.Single()));
        Assert.Equal(1, PdfReader.Open(pdf.Outputs.Single(), PdfDocumentOpenMode.Import).PageCount);

        var ppt = await RunAsync(office, deck, "ppt");
        Assert.True(ppt.State == JobState.Completed, Why(ppt));
        Assert.EndsWith(".ppt", ppt.Outputs.Single());

        var back = await RunAsync(office, ppt.Outputs.Single(), "pptx");
        Assert.True(back.State == JobState.Completed, Why(back));
        Assert.EndsWith(".pptx", back.Outputs.Single());

        // PPT → PDF: a format only Office reads here, and the slide pictures follow from the PDF.
        var fromPpt = await RunAsync(office, ppt.Outputs.Single(), "png");
        Assert.True(fromPpt.State == JobState.Completed, Why(fromPpt));
        await office.CloseAllAsync();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Word_converts_when_it_can_save_and_fails_fast_when_it_cannot()
    {
        NeedsRealOffice(OfficeKind.Word);
        var office = Real();
        Directory.CreateDirectory(_folder);
        var document = Path.Combine(_folder, "letter.docx");
        OfficeSamples.Write(OfficeKind.Word, document);
        var before = Running("WINWORD");
        var watch = Stopwatch.StartNew();

        var job = await RunAsync(office, document, "pdf");

        if (job.State == JobState.Completed)
        {
            Assert.Contains("Mandarina", PdfText(job.Outputs.Single()));
            Assert.Contains("Word", Producer(job.Outputs.Single()));
            var doc = await RunAsync(office, document, "doc");
            Assert.True(doc.State == JobState.Completed, Why(doc));
            var docx = await RunAsync(office, doc.Outputs.Single(), "docx");
            Assert.True(docx.State == JobState.Completed, Why(docx));
            // Word's own reflow turns a PDF into an editable DOCX.
            var reflow = await RunAsync(office, job.Outputs.Single(), "docx");
            Assert.True(reflow.State == JobState.Completed, Why(reflow));
            Assert.Contains("Mandarina", DocxText(reflow.Outputs.Single()));
            // PDF/A when the preset asks for it.
            var archival = await RunAsync(office, document, "pdf", new Preset { TargetFormat = "pdf", Document = { PdfA = true } });
            Assert.True(archival.State == JobState.Completed, Why(archival));
            Assert.True(IsPdfA(archival.Outputs.Single()), "the PDF is not PDF/A");
        }
        else
        {
            // A Word that can't save (this PC's isn't activated): a message for the user, soon, and no Word left behind.
            Assert.Equal(JobState.Failed, job.State);
            var key = job.Files.Single().ErrorKey;
            Assert.Contains(key, new[] { "office.error.unlicensed", "office.error.timeout", "office.error.dialog", "office.error.start" });
            Assert.True(watch.Elapsed < Fast.SelfTest + TimeSpan.FromSeconds(30), $"it took {watch.Elapsed}");
            // The next file doesn't wait again: Word is left out of the routes.
            Assert.DoesNotContain(office.Edges, e => e.From == "docx" && e.To == "pdf");
        }
        await office.CloseAllAsync();
        var started = Running("WINWORD").Where(id => !before.Contains(id)).ToList();
        Assert.True(Gone("WINWORD", started), "A Word started by the engine is still running");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task A_job_that_takes_too_long_ends_with_a_message_and_kills_only_the_Excel_it_started()
    {
        NeedsRealOffice(OfficeKind.Excel);
        Assert.SkipWhen(Running("EXCEL").Count > 0, "Excel is open: this test would share it.");
        // A converter with the normal waits proves, once, that this Excel can save; the one with a 1 ms limit then times out.
        var checker = Real();
        var broken = await checker.CheckAsync(OfficeKind.Excel, TestContext.Current.CancellationToken);
        Assert.SkipWhen(broken is not null, $"This Excel can't save: {broken?.Key} {broken?.Detail}");
        await checker.CloseAllAsync();
        Assert.True(Gone("EXCEL", Running("EXCEL")), "the checker's Excel did not close");

        // "The user's Excel": one that was already there. It must live through all of this.
        using var users = ComObject.Create("Excel.Application");
        var usersProcess = Running("EXCEL");
        Assert.Single(usersProcess);

        Directory.CreateDirectory(_folder);
        var workbook = Path.Combine(_folder, "slow.xlsx");
        OfficeSamples.Write(OfficeKind.Excel, workbook);
        var step = new ConversionStep(workbook, "xlsx", "pdf", new Preset { TargetFormat = "pdf" }, new FolderAllocator(_folder), _folder);
        var office = Real(Fast with { Job = TimeSpan.FromMilliseconds(1), Tick = TimeSpan.FromMilliseconds(50) });

        var failure = await Assert.ThrowsAsync<ConversionFailureException>(() => office.ConvertAsync(step, null, CancellationToken.None));

        Assert.Equal("office.error.timeout", failure.Key);
        Assert.True(Gone("EXCEL", Running("EXCEL").Except(usersProcess).ToList()), "the Excel the engine started is still running");
        Assert.Equal(usersProcess, Running("EXCEL")); // the user's Excel is still there, untouched
        users.Call("Quit");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task A_PowerPoint_the_user_has_open_is_used_without_closing_it_or_touching_its_presentation()
    {
        NeedsRealOffice(OfficeKind.PowerPoint);
        Assert.SkipWhen(Running("POWERPNT").Count > 0, "PowerPoint is open: this test would share the user's.");
        var checker = Real();
        var broken = await checker.CheckAsync(OfficeKind.PowerPoint, TestContext.Current.CancellationToken);
        Assert.SkipWhen(broken is not null, $"This PowerPoint can't save: {broken?.Key} {broken?.Detail}");
        await checker.CloseAllAsync();
        Assert.True(Gone("POWERPNT", Running("POWERPNT")), "the checker's PowerPoint did not close");

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "users-deck.pptx");
        OfficeSamples.Write(OfficeKind.PowerPoint, path);
        // "The user's PowerPoint": already running (PowerPoint has one instance per user), with this very file open.
        using var users = ComObject.Create("PowerPoint.Application");
        using var presentations = users.Child("Presentations");
        using var open = presentations.CallObject("Open", ("FileName", path), ("ReadOnly", 0), ("Untitled", 0), ("WithWindow", 0));
        var usersProcess = Running("POWERPNT");
        Assert.Single(usersProcess);
        var office = Real();

        var job = await RunAsync(office, path, "pdf"); // converts the file the user has open

        Assert.True(job.State == JobState.Completed, Why(job));
        Assert.Contains("PowerPoint", Producer(job.Outputs.Single()));
        await office.CloseAllAsync();
        // The user's PowerPoint was reused: still running, still with its one presentation, which is the same file.
        Assert.Equal(usersProcess, Running("POWERPNT"));
        Assert.Equal(1, Convert.ToInt32(presentations.Get("Count")));
        Assert.Equal(path, (string)open.Get("FullName")!, ignoreCase: true);
        open.Call("Close");
        users.Call("Quit");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task An_Excel_the_user_has_open_keeps_its_workbook_and_stays_open()
    {
        NeedsRealOffice(OfficeKind.Excel);
        Assert.SkipWhen(Running("EXCEL").Count > 0, "Excel is open: this test would share the user's.");
        var checker = Real();
        var broken = await checker.CheckAsync(OfficeKind.Excel, TestContext.Current.CancellationToken);
        Assert.SkipWhen(broken is not null, $"This Excel can't save: {broken?.Key} {broken?.Detail}");
        await checker.CloseAllAsync();
        Assert.True(Gone("EXCEL", Running("EXCEL")), "the checker's Excel did not close");

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "users-book.xlsx");
        OfficeSamples.Write(OfficeKind.Excel, path);
        using var users = ComObject.Create("Excel.Application");
        using var workbooks = users.Child("Workbooks");
        using var open = workbooks.CallObject("Open", ("Filename", path), ("ReadOnly", false));
        var usersProcess = Running("EXCEL");
        Assert.Single(usersProcess);
        var office = Real();

        var job = await RunAsync(office, path, "pdf");

        Assert.True(job.State == JobState.Completed, Why(job));
        await office.CloseAllAsync();
        Assert.Equal(usersProcess, Running("EXCEL")); // the user's Excel is still the only one, and it is still there
        Assert.Equal(1, Convert.ToInt32(workbooks.Get("Count")));
        Assert.Equal(path, (string)open.Get("FullName")!, ignoreCase: true);
        open.CallNamed("Close", ("SaveChanges", false));
        users.Call("Quit");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task A_window_Word_stops_at_is_reported_and_Word_is_killed_and_started_again()
    {
        NeedsRealOffice(OfficeKind.Word);
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MANDARINA_UI_TESTS") == "1",
            "Word's Open dialog shows on the screen for a moment: set MANDARINA_UI_TESTS=1.");
        Assert.SkipWhen(Running("WINWORD").Count > 0, "Word is open: this test would share it.");
        var session = new OfficeSession(OfficeKind.Word, Fast with { DialogGrace = TimeSpan.FromSeconds(1), DialogTicks = 2, Tick = TimeSpan.FromMilliseconds(200) });

        var failure = await Assert.ThrowsAsync<ConversionFailureException>(() => session.RunAsync(app =>
        {
            using var dialogs = app.Root.Child("Dialogs");
            using var open = dialogs.CallChild("Item", 80); // wdDialogFileOpen: waits for someone to answer
            open.Call("Show");
            return true;
        }, TimeSpan.FromSeconds(60), CancellationToken.None));

        Assert.True(failure.Key == "office.error.dialog", $"{failure.Key}: {failure.Detail}");
        Assert.False(string.IsNullOrEmpty(failure.Detail));
        Assert.True(Gone("WINWORD", Running("WINWORD")), "the Word that showed the dialog is still running");
        // The session recovers: the next job starts a Word of its own.
        var version = await session.RunAsync(app => Convert.ToString(app.Root.Get("Version")), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(version));
        await session.CloseAsync();
        Assert.True(Gone("WINWORD", Running("WINWORD")));
    }
}
