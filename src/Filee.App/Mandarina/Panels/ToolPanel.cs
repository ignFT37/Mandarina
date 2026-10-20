// Mandarina: the base of every tool that opens a panel (trim, crop, redact, …) instead of running straight
// from the wheel. A panel is a view model (ToolPanel) plus the view with its options (CreateView); ToolPanelWindow wraps
// it in the orange-to-lilac frame with the ✕ and the Apply button, runs ApplyAsync when Apply is pressed and shows the
// progress, the error or the ✓. To add a tool: derive from ToolPanel in Mandarina/Tools, mark it with [ToolPanel("id")]
// and list its id in WheelCatalog; nothing else needs registering.

using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Mandarina.Panels;

/// <summary>Marks a <see cref="ToolPanel"/> as the panel of the wheel entry <paramref name="id"/> (the tool's id: "trimAudio").</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ToolPanelAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    /// <summary>True when the panel runs FFmpeg: the petal is greyed out while FFmpeg is not installed.</summary>
    public bool NeedsFfmpeg { get; init; }

    /// <summary>
    /// True when the panel needs a speech model (Settings → Engines, the large or the small one): the petal is greyed out while
    /// neither is installed.
    /// </summary>
    public bool NeedsWhisper { get; init; }
}

/// <summary>
/// A failure the user can read: shown in the panel's footer as it is (already localized or short English), unless it carries a
/// <see cref="Key"/>, which says it in the user's language (see <see cref="PanelErrors"/>).
/// </summary>
public sealed class PanelException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The i18n key of the sentence to show instead of <see cref="Exception.Message"/>, the short English fallback.</summary>
    public string? Key { get; init; }

    /// <summary>The arguments of <see cref="Key"/>'s sentence.</summary>
    public object[] Args { get; init; } = [];

    /// <summary>What an engine said, word for word: goes to the log, not to the footer.</summary>
    public string? Detail { get; init; }

    /// <summary>The file the failure is about, when the tool that ran several knows it: the footer names it.</summary>
    public string? Subject { get; set; }
}

/// <summary>What a panel works with: the files, the language and the platform.</summary>
public sealed class PanelContext(IReadOnlyList<string> files, ILocalizer loc, IPlatformServices platform)
{
    /// <summary>The files the tool was opened for (no folders), in the order they were selected.</summary>
    public IReadOnlyList<string> Files { get; } = files;

    public ILocalizer Loc { get; } = loc;

    public IPlatformServices Platform { get; } = platform;

    /// <summary>The app's log (a failed run is written to it); nothing in tests.</summary>
    public ILogger Log { get; init; } = NullLogger.Instance;

    /// <summary>The translation of <paramref name="key"/>.</summary>
    public string this[string key] => Loc[key];

    public string Format(string key, params object[] args) => Loc.Format(key, args);

    /// <summary>What the user reads for a failure: its cause in their language when it is recognised, else the message as it is.</summary>
    public string Describe(Exception ex) => PanelErrors.Describe(ex, Loc);

    /// <summary>
    /// <see cref="Describe"/>, and the original words of the failure go to the log: for a panel that shows a failure itself
    /// (a file that does not load) instead of through Apply.
    /// </summary>
    public string Explain(Exception ex)
    {
        Log.LogWarning(ex, "A tool panel reported a failure: {Original}", PanelErrors.Original(ex));
        return Describe(ex);
    }

    /// <summary>
    /// A free path beside <paramref name="source"/> named "name suffix.ext" (the tool's <c>wheel.&lt;toolId&gt;.suffix</c>);
    /// " 2", " 3", … when it is taken. <paramref name="extension"/> is ".png"-style, the source's by default.
    /// </summary>
    public string OutputFor(string source, string toolId, string? extension = null) =>
        OutputPaths.Beside(source, Loc[$"wheel.{toolId}.suffix"], extension);

    /// <summary>Like <see cref="OutputFor"/> for a folder ("name suffix") that a tool fills with several files.</summary>
    public string FolderFor(string source, string toolId) => OutputPaths.FolderBeside(source, Loc[$"wheel.{toolId}.suffix"]);

    /// <summary>FFmpeg, or null when it is not installed (see <see cref="MediaTools.Locate"/>).</summary>
    public MediaTools? Media => MediaTools.Locate();

    /// <summary>FFmpeg for a tool that needs it; throws a readable <see cref="PanelException"/> when it is missing.</summary>
    public MediaTools RequireMedia() =>
        Media ?? throw new PanelException(Loc.Format("engine.reason.needs_package", "FFmpeg"));
}

public abstract partial class ToolPanel(PanelContext context) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;

    public PanelContext Context { get; } = context;

    /// <summary>The files of the tool.</summary>
    public IReadOnlyList<string> Files => Context.Files;

    /// <summary>The id of the tool ("trimAudio"); the title is <c>wheel.&lt;id&gt;</c>.</summary>
    public string Id => ((ToolPanelAttribute?)Attribute.GetCustomAttribute(GetType(), typeof(ToolPanelAttribute)))?.Id
        ?? throw new InvalidOperationException($"{GetType().Name} needs a [ToolPanel] attribute.");

    /// <summary>The window's title, centred above the options.</summary>
    public string Title => Context[$"wheel.{Id}"];

    /// <summary>The options. Called once, on the UI thread; set the view's DataContext to this panel.</summary>
    public abstract Control CreateView();

    /// <summary>The size of the window in device-independent pixels (the options are laid out inside it).</summary>
    public virtual Size PreferredSize => new(760, 560);

    /// <summary>
    /// True for a tool that has no options to choose: its window starts applying as soon as it opens, and only shows the
    /// progress and the result (a one-click tool that takes long enough to need them).
    /// </summary>
    public virtual bool AutoRun => false;

    /// <summary>
    /// True when Apply can run. Override it and call <see cref="NotifyCanApplyChanged"/> whenever its answer may
    /// have changed (or let the properties it reads raise PropertyChanged through <see cref="ObservableObject"/>).
    /// </summary>
    public virtual bool CanApply => true;

    /// <summary>
    /// True when closing the window now would throw away something the user made by hand (marks drawn, areas covered, pages
    /// reordered, a crop box, edits…): the window then asks before closing with Esc or ✕. A panel that only has options to
    /// choose keeps the default, since choosing them again costs nothing. Read at the moment of closing, not tracked.
    /// </summary>
    public virtual bool HasUnsavedWork => false;

    /// <summary>
    /// Does the work and returns the files it wrote, each saved beside its original as "name suffix.ext"
    /// (<see cref="PanelContext.OutputFor"/>). Runs on the UI thread: start CPU-heavy work with Task.Run and report
    /// <paramref name="progress"/> (0..1) now and then. Throw <see cref="PanelException"/> with a readable message to
    /// fail; delete half-written outputs first. Honour <paramref name="cancel"/>.
    /// </summary>
    public abstract Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel);

    // ───────── State the window shows (all set by RunAsync) ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isDone;

    /// <summary>Neither working nor finished: the Apply button says "Apply".</summary>
    public bool IsIdle => !IsBusy && !IsDone;

    /// <summary>0..100 while applying.</summary>
    [ObservableProperty] private double _progress;

    /// <summary>The footer line: the error, or what was saved.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty] private bool _hasError;

    /// <summary>The files the last successful run wrote.</summary>
    public IReadOnlyList<string> Outputs { get; private set; } = [];

    /// <summary>Raised on the UI thread after a successful run (the window closes a moment later).</summary>
    public event EventHandler? Applied;

    /// <summary>True when the Apply button is enabled.</summary>
    public bool CanRun => CanApply && !IsBusy && !IsDone;

    /// <summary>Call when <see cref="CanApply"/> may have changed.</summary>
    protected void NotifyCanApplyChanged() => OnPropertyChanged(nameof(CanRun));

    /// <summary>The Apply button: runs the tool, with progress, cancellation and the error shown in the footer.</summary>
    public async Task RunAsync()
    {
        if (!CanRun)
            return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        HasError = false;
        Status = null;
        Progress = 0;
        try
        {
            var outputs = await ApplyAsync(new Progress<double>(p => Progress = Math.Clamp(p, 0, 1) * 100), _cts.Token);
            Outputs = outputs;
            Progress = 100;
            IsDone = true;
            Status = outputs.Count switch
            {
                0 => Context["panel.saved_none"],
                1 => Context.Format("panel.saved", Path.GetFileName(outputs[0])),
                _ => Context.Format("panel.saved_many", outputs.Count),
            };
            Applied?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = Context["panel.cancelled"];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever a tool throws ends up in the footer and the log. This runs from the Apply button (an async void
            // handler): an exception nobody expected would otherwise take Mandarina itself down.
            // The footer says the cause in the user's language when it is recognised; the log keeps what the engine said.
            HasError = true;
            Status = Context.Format("panel.failed", Context.Describe(ex));
            Context.Log.LogWarning(ex, "The {Tool} panel failed: {Original}", Id, PanelErrors.Original(ex));
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Stops a running Apply (the ✕ while it works).</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Reveals the first output in the file manager.</summary>
    public void Reveal()
    {
        if (Outputs.Count > 0)
            Context.Platform.RevealInFileManager(Outputs[0]);
    }

    /// <summary>The window closed: stop playback, delete temporary files.</summary>
    public virtual void Dispose()
    {
        Cancel();
        GC.SuppressFinalize(this);
    }
}
