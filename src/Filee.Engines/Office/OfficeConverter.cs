// Mandarina: documents, spreadsheets and presentations converted by the Microsoft Office installed on the PC, driven
// through COM with late binding (Word.Application, Excel.Application, PowerPoint.Application). A deliberate exception to
// "engines never use software installed on the system" (see EngineRegistry): it is optional, shows in Settings → Engines
// whether Office is installed or not, has a switch, and only adds routes when Office is there and works.
//
//  * What it does: DOC, DOCX, RTF, ODT (and the template / macro variants) → PDF; PPT, PPTX, ODP → PDF (slide images come
//    from that PDF, through PDFium, like every other route); XLS, XLSX, ODS → PDF; DOC ↔ DOCX, PPT ↔ PPTX, XLS ↔ XLSX;
//    PDF → DOCX with Word's own reflow (PDF → DOCX is DirectOnly: Word is never a step of a longer route from a PDF).
//    CSV, TXT, HTML and Markdown stay with the built-in engines, which do them well and with every sheet.
//  * Costs: 10 (0 for presentations → PDF, 5 for XLS → XLSX) beat the built-in route through the HWPX writer and rhwp
//    (about 31) and LibreOffice's fallback (25); registered before LibreOffice, so a tie goes to Office.
//  * Safe by construction (OfficeSession, OfficeFiles, OfficeOperations): a private copy of the file, read-only, no window,
//    no alerts, no macros, no password prompts; an STA thread per application; Office that was already open is never
//    closed or killed, and what Mandarina started is killed by id after a time limit; a self-test (OfficeSelfTest) keeps an
//    Office that hangs when it saves out of the routes instead of failing every file.
//  * Off by default in code (EngineEnvironment.OfficeEnabled): the app and the command line turn it on from the settings,
//    so a test run doesn't depend on the machine's Office.

using System.Runtime.Versioning;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Com;

namespace Filee.Engines.Office;

/// <summary>Conversions through the Word, Excel and PowerPoint installed on this PC.</summary>
public sealed class OfficeConverter : IConverter
{
    /// <summary>Beats the built-in route (about 31) and LibreOffice's fallback (25).</summary>
    private const int Cost = 10;

    /// <summary>Presentations → PDF: ties LibreOffice's "preferred" cost (0), and Office is registered first.</summary>
    private const int SlidesCost = 0;

    /// <summary>XLS → XLSX through Excel keeps what the in-process reader drops (charts, pictures, conditional formats).</summary>
    private const int BetterThanBuiltIn = 5;

    private static readonly TimeSpan TypicalDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LicenseCacheFor = TimeSpan.FromSeconds(30);

    private readonly EngineEnvironment _env;
    private readonly IOfficeProbe? _probe;
    private readonly OfficeTimings _timings;
    private readonly Func<bool> _enabled;

    private readonly object _gate = new();
    private readonly Dictionary<OfficeKind, OfficeSession> _sessions = [];
    private readonly Dictionary<OfficeKind, SemaphoreSlim> _selfTests = [];
    private IReadOnlyDictionary<OfficeKind, OfficeAppInfo> _installed = new Dictionary<OfficeKind, OfficeAppInfo>();
    private (OfficeLicense State, string Signature) _license = (OfficeLicense.Unknown, "");
    private DateTime _licenseAt;

    public OfficeConverter(EngineEnvironment env) : this(env, CreateProbe(), OfficeTimings.Default)
    {
    }

    /// <param name="enabled">The switch; the settings' one (<see cref="EngineEnvironment.OfficeEnabled"/>) unless a test passes its own,
    /// so a test that uses the real Office doesn't turn it on for the tests that run beside it.</param>
    internal OfficeConverter(EngineEnvironment env, IOfficeProbe? probe, OfficeTimings timings, Func<bool>? enabled = null)
    {
        _env = env;
        _probe = probe;
        _timings = timings;
        _enabled = enabled ?? (() => EngineEnvironment.OfficeEnabled);
        Locate();
    }

    public string Id => "office";
    public string DisplayName => "Microsoft Office";

    /// <summary>Word, Excel and PowerPoint work at the same time; each one converts one file at a time.</summary>
    public int MaxParallelism => 3;

    /// <summary>Office is installed on this PC (whether it is turned on or not).</summary>
    public bool IsInstalled => _installed.Count > 0;

    /// <summary>
    /// The conversions of the applications that are installed and not left out by the self-test; none while Office is not
    /// installed or the switch is off. Always none when the engine is unavailable, because a route planner built straight
    /// from the converters (not through the catalog, which skips the unavailable ones) must never send a file to Office
    /// that isn't there: Settings → Engines then shows "0 conversions" for it.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges =>
        _probe is null || _installed.Count == 0 || !_enabled()
            ? []
            : BuildEdges([.. _installed.Keys.Where(kind => !IsBroken(kind))]);

    public EngineStatus GetStatus()
    {
        Locate();
        if (_probe is null)
            return EngineStatus.Unavailable("engine.reason.unsupported_platform");
        if (_installed.Count == 0)
            return EngineStatus.Unavailable("engine.reason.not_installed");

        var apps = string.Join(", ", _installed.Keys.Select(kind => kind.DisplayName()));
        var version = _installed.Values.First().Version;
        var broken = _installed.Keys.Where(IsBroken).Select(kind => $"{kind.DisplayName()}: {Known(kind).Key}").ToList();
        var detail = $"{apps} ({Path.GetDirectoryName(_installed.Values.First().ExePath)})"
                     + (broken.Count > 0 ? "\n" + string.Join("\n", broken) : "");
        if (!_enabled())
            return new EngineStatus(false, "engine.reason.office_off", detail, version);
        return new EngineStatus(true, _license.State == OfficeLicense.Restricted ? "engine.reason.office_unlicensed" : null, detail, version);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (_probe is null || !OperatingSystem.IsWindows() || !_enabled())
            throw new ConversionFailureException("office.error.not_installed");
        if (!Routes.TryGetValue((step.From, step.To), out var kind))
            throw new ConversionFailureException("office.error.unsupported", $"{step.From} → {step.To}");
        if (!_installed.TryGetValue(kind, out var app))
            throw new ConversionFailureException("office.error.not_installed", kind.DisplayName());
        return await ConvertWindowsAsync(step, kind, app, progress, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<string>> ConvertWindowsAsync(
        ConversionStep step, OfficeKind kind, OfficeAppInfo app, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(step.WorkDirectory, "office-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            OfficeFiles.RejectPasswordProtected(step.InputPath, step.From);
            var copy = OfficeFiles.PrivateCopy(step.InputPath, scratch);
            var result = Path.Combine(scratch, "result." + step.To);

            await EnsureWorksAsync(kind, app, cancellationToken);
            progress?.Report(0.1);
            var pdfA = step.Preset.Document.PdfA;
            using (ProgressEstimate.Start(progress, TypicalDuration))
            {
                await SessionOf(kind).RunAsync(application =>
                {
                    OfficeOperations.Convert(application, step.To, copy, result, pdfA);
                    return true;
                }, _timings.Job, cancellationToken);
            }

            if (!File.Exists(result) || new FileInfo(result).Length == 0)
                throw new ConversionFailureException("office.error.no_output");
            var target = step.Output.Allocate(step.To);
            if (target is null)
                return [];
            File.Move(result, target, overwrite: true);
            progress?.Report(1);
            return [target];
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    // ───────────────────────── Self-test ─────────────────────────

    /// <summary>
    /// Before the first conversion with an application (and again when its result has expired) converts a small file of
    /// our own. Returns when it works; throws what the user should read when it doesn't, and remembers that.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task EnsureWorksAsync(OfficeKind kind, OfficeAppInfo app, CancellationToken cancellationToken)
    {
        var known = Known(kind);
        if (known.Verdict == OfficeSelfTest.Verdict.Works)
            return;
        if (known.Verdict == OfficeSelfTest.Verdict.Broken)
            throw new ConversionFailureException(known.Key ?? "office.error.failed", known.Detail);

        var gate = SelfTestGate(kind);
        await gate.WaitAsync(cancellationToken);
        try
        {
            known = Known(kind); // another file may have done it while this one waited
            if (known.Verdict == OfficeSelfTest.Verdict.Works)
                return;
            if (known.Verdict == OfficeSelfTest.Verdict.Broken)
                throw new ConversionFailureException(known.Key ?? "office.error.failed", known.Detail);

            var folder = Path.Combine(Path.GetTempPath(), "mandarina-office-" + Guid.NewGuid().ToString("N")[..8]);
            var license = CurrentLicense();
            try
            {
                await SessionOf(kind).RunAsync(application =>
                {
                    OfficeOperations.SelfTest(application, folder);
                    return true;
                }, _timings.SelfTest, cancellationToken);
                OfficeSelfTest.Record(_env.DataDirectory, kind, Fingerprint(kind), works: true, null, null, persist: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var (key, detail) = Describe(ex, kind, license.State);
                // Office busy with a window of the user's is not Office broken: the next file tries again.
                if (key != "office.error.busy")
                    OfficeSelfTest.Record(_env.DataDirectory, kind, Fingerprint(kind), works: false, key, detail,
                        persist: license.State == OfficeLicense.Restricted);
                throw new ConversionFailureException(key, detail, ex);
            }
            finally
            {
                TryDelete(folder);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs the self-test of <paramref name="kind"/> if it is due. Null when it works, else why not (tests use it to skip).</summary>
    internal async Task<ConversionFailureException?> CheckAsync(OfficeKind kind, CancellationToken cancellationToken = default)
    {
        if (_probe is null || !OperatingSystem.IsWindows() || !_installed.TryGetValue(kind, out var app))
            return new ConversionFailureException("office.error.not_installed", kind.DisplayName());
        try
        {
            await WorksAsync(kind, app, cancellationToken);
            return null;
        }
        catch (ConversionFailureException ex)
        {
            return ex;
        }

        [SupportedOSPlatform("windows")]
        Task WorksAsync(OfficeKind k, OfficeAppInfo a, CancellationToken token) => EnsureWorksAsync(k, a, token);
    }

    /// <summary>Why a failed self-test means this Office can't be used. An Office that isn't activated hangs when it saves.</summary>
    internal static (string Key, string? Detail) Describe(Exception ex, OfficeKind kind, OfficeLicense license)
    {
        var failure = ex as ConversionFailureException;
        var key = failure?.Key ?? "office.error.failed";
        if (license == OfficeLicense.Restricted && key is "office.error.timeout" or "office.error.dialog")
            return ("office.error.unlicensed", kind.DisplayName());
        return (key, failure?.Detail ?? ex.Message);
    }

    private OfficeSelfTest.Result Known(OfficeKind kind) =>
        OfficeSelfTest.Get(_env.DataDirectory, kind, Fingerprint(kind));

    private bool IsBroken(OfficeKind kind) => Known(kind).Verdict == OfficeSelfTest.Verdict.Broken;

    /// <summary>Changes with the Office version and with its license, so a result doesn't outlive either.</summary>
    private string Fingerprint(OfficeKind kind) =>
        $"{(_installed.TryGetValue(kind, out var app) ? app.Version : "?")}|{CurrentLicense().Signature}";

    // ───────────────────────── The PC ─────────────────────────

    private static IOfficeProbe? CreateProbe() => OperatingSystem.IsWindows() ? NewWindowsProbe() : null;

    [SupportedOSPlatform("windows")]
    private static IOfficeProbe NewWindowsProbe() => new WindowsOfficeProbe();

    private void Locate()
    {
        if (_probe is null)
            return;
        _installed = OfficeKinds.All
            .Select(kind => _probe.Find(kind))
            .OfType<OfficeAppInfo>()
            .ToDictionary(app => app.Kind);
        _licenseAt = default; // read it again
        CurrentLicense();
    }

    private (OfficeLicense State, string Signature) CurrentLicense()
    {
        if (_probe is null)
            return (OfficeLicense.Unknown, "");
        lock (_gate)
        {
            if (DateTime.UtcNow - _licenseAt > LicenseCacheFor)
            {
                _license = _probe.License();
                _licenseAt = DateTime.UtcNow;
            }
            return _license;
        }
    }

    [SupportedOSPlatform("windows")]
    private OfficeSession SessionOf(OfficeKind kind)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(kind, out var session))
                _sessions[kind] = session = new OfficeSession(kind, _timings);
            return session;
        }
    }

    private SemaphoreSlim SelfTestGate(OfficeKind kind)
    {
        lock (_gate)
        {
            if (!_selfTests.TryGetValue(kind, out var gate))
                _selfTests[kind] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    /// <summary>Closes the Office this converter started (tests do it so no hidden Office outlives them).</summary>
    internal async Task CloseAllAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;
        OfficeSession[] sessions;
        lock (_gate)
            sessions = [.. _sessions.Values];
        foreach (var session in sessions)
            await CloseWindows(session);

        [SupportedOSPlatform("windows")]
        static Task CloseWindows(OfficeSession session) => session.CloseAsync();
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Office may still hold the copy for a moment; the job's folder is deleted afterwards anyway
        }
    }

    // ───────────────────────── Conversions ─────────────────────────

    /// <summary>
    /// Every edge is <c>FirstStepOnly</c>: Office converts files that ARE Word, Excel or PowerPoint documents. Without
    /// that, when the built-in route to PDF is missing (rhwp is not installed) the cheapest way to a PDF would be a
    /// spreadsheet turned into DOCX and sent to Word, or a Word document turned into Markdown, then into slides, then
    /// into a PDF by PowerPoint.
    /// </summary>
    private static IEnumerable<(OfficeKind Kind, ConversionEdge Edge)> Table()
    {
        foreach (var from in new[] { "doc", "dot", "docx", "docm", "dotx", "dotm", "rtf", "odt" })
            yield return (OfficeKind.Word, new ConversionEdge(from, "pdf", Cost, FirstStepOnly: true));
        yield return (OfficeKind.Word, new ConversionEdge("doc", "docx", Cost, FirstStepOnly: true));
        yield return (OfficeKind.Word, new ConversionEdge("dot", "docx", Cost, FirstStepOnly: true));
        yield return (OfficeKind.Word, new ConversionEdge("docx", "doc", Cost, FirstStepOnly: true));
        // Word's reflow of a PDF is slow and only worth it when a Word document is what was asked for.
        yield return (OfficeKind.Word, new ConversionEdge("pdf", "docx", Cost, DirectOnly: true, FirstStepOnly: true));

        foreach (var from in new[] { "xls", "xlsx", "xlsm", "xlt", "xltx", "ods" })
            yield return (OfficeKind.Excel, new ConversionEdge(from, "pdf", Cost, FirstStepOnly: true));
        yield return (OfficeKind.Excel, new ConversionEdge("xls", "xlsx", BetterThanBuiltIn, FirstStepOnly: true));
        yield return (OfficeKind.Excel, new ConversionEdge("xlsx", "xls", Cost, FirstStepOnly: true));

        foreach (var from in new[] { "ppt", "pptx", "pptm", "ppsx", "potx", "odp" })
            yield return (OfficeKind.PowerPoint, new ConversionEdge(from, "pdf", SlidesCost, FirstStepOnly: true));
        yield return (OfficeKind.PowerPoint, new ConversionEdge("ppt", "pptx", Cost, FirstStepOnly: true));
        yield return (OfficeKind.PowerPoint, new ConversionEdge("pptx", "ppt", Cost, FirstStepOnly: true));
    }

    /// <summary>Which application does each conversion.</summary>
    private static readonly Dictionary<(string From, string To), OfficeKind> Routes =
        Table().ToDictionary(row => (row.Edge.From, row.Edge.To), row => row.Kind);

    /// <summary>The conversions of <paramref name="kinds"/>.</summary>
    internal static List<ConversionEdge> BuildEdges(IEnumerable<OfficeKind> kinds)
    {
        var wanted = kinds.ToHashSet();
        return [.. Table().Where(row => wanted.Contains(row.Kind) && FormatRegistry.FindById(row.Edge.From) is not null).Select(row => row.Edge)];
    }
}
