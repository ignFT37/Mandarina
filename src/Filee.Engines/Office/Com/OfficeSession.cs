// Mandarina: one Office application (Word, Excel or PowerPoint) driven from a thread of its own.
//
//  * STA thread. Office is a COM server in another process; every call to one object goes through one thread, which is
//    created when the first job arrives and ends after Idle without work (Office is closed then). Jobs of one application
//    run one after another; Word, Excel and PowerPoint have a session each and can work at the same time.
//  * The user's Office is never touched. Right after creating the application the engine compares the Office processes
//    before and after: a new process started with -Embedding is OURS (it may be closed or killed, by id, never by name);
//    when none appears the application was already running (PowerPoint has one instance per user) and is SHARED: it is
//    used without a window, only our own document is closed, and it is never quit or killed. Alerts and macro settings
//    that a job changes are put back afterwards when the application is shared.
//  * Never stuck. A supervisor watches each job: past the job's time, or when our own Office shows a dialog for a few
//    seconds, the job fails with a message for the user and our Office process is killed (which also ends the call that
//    was blocked). Our Office processes are also in a job object that Windows closes when Mandarina ends.

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Filee.Core.Conversion;

namespace Filee.Engines.Office.Com;

/// <summary>Waits of the sessions; tests shorten them.</summary>
/// <param name="Job">Longest time of one conversion, from the moment it starts (Office starting included).</param>
/// <param name="SelfTest">Longest time of the self-test conversion.</param>
/// <param name="Idle">How long Office stays open, without work, for the next file.</param>
/// <param name="DialogGrace">A dialog counts only after the job has run this long (Office shows a splash while it starts).</param>
/// <param name="DialogTicks">How many checks in a row must see the dialog before the job is given up.</param>
/// <param name="QueueWait">Longest wait in the queue behind other jobs.</param>
/// <param name="Tick">Time between the supervisor's checks.</param>
/// <param name="QuitWait">How long Office gets to close after Quit before it is killed.</param>
internal sealed record OfficeTimings(
    TimeSpan Job, TimeSpan SelfTest, TimeSpan Idle, TimeSpan DialogGrace, int DialogTicks, TimeSpan QueueWait, TimeSpan Tick, TimeSpan QuitWait)
{
    public static OfficeTimings Default { get; } = new(
        Job: TimeSpan.FromSeconds(150), SelfTest: TimeSpan.FromSeconds(25), Idle: TimeSpan.FromSeconds(30),
        DialogGrace: TimeSpan.FromSeconds(12), DialogTicks: 3, QueueWait: TimeSpan.FromMinutes(10),
        Tick: TimeSpan.FromSeconds(1), QuitWait: TimeSpan.FromSeconds(8));
}

/// <summary>The application a job works with.</summary>
/// <param name="shared">It was already running: it belongs to the user.</param>
[SupportedOSPlatform("windows")]
internal sealed class OfficeApplication(OfficeKind kind, ComObject root, bool shared)
{
    public OfficeKind Kind => kind;

    public ComObject Root => root;

    public bool Shared => shared;

    /// <summary>
    /// No alerts, no macros (AutomationSecurity "force disable": AutoOpen and Workbook_Open never run), no links updated,
    /// for the duration of a job. A shared application gets its own values back afterwards.
    /// </summary>
    public IDisposable Quiet()
    {
        var restore = new List<Action>();

        void Set(string property, object value, bool remember = true)
        {
            try
            {
                var before = shared && remember ? root.Get(property) : null;
                root.Set(property, value);
                if (before is not null)
                    restore.Add(() => root.Set(property, before));
            }
            catch (OfficeComException)
            {
                // a property this version doesn't have: nothing to quiet
            }
        }

        switch (kind)
        {
            case OfficeKind.Word:
                Set("DisplayAlerts", 0); // wdAlertsNone
                Set("AutomationSecurity", 3); // msoAutomationSecurityForceDisable
                break;
            case OfficeKind.Excel:
                Set("DisplayAlerts", false);
                Set("AutomationSecurity", 3);
                Set("EnableEvents", false);
                Set("AskToUpdateLinks", false);
                if (!shared)
                    Set("ScreenUpdating", false, remember: false);
                break;
            default:
                Set("DisplayAlerts", 1); // ppAlertsNone
                Set("AutomationSecurity", 3);
                break;
        }
        return new Restorer(restore);
    }

    private sealed class Restorer(List<Action> restore) : IDisposable
    {
        public void Dispose()
        {
            foreach (var undo in restore)
            {
                try
                {
                    undo();
                }
                catch (OfficeComException)
                {
                    // the application went away meanwhile
                }
            }
        }
    }
}

/// <summary>Processes of Office, found by name and checked by start time before anything is done to them.</summary>
internal static class OfficeProcesses
{
    public static HashSet<int> Pids(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            ids.Add(process.Id);
            process.Dispose();
        }
        return ids;
    }

    /// <summary>
    /// Of the processes that appeared while the application was being created, the one COM started for us (its command
    /// line has -Embedding): its id and start time. (0, default) when there is none or more than one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static (int Id, DateTime Started) StartedByCom(IEnumerable<int> candidates)
    {
        var found = new List<(int, DateTime)>();
        foreach (var id in candidates)
        {
            if (OfficeNative.CommandLineOf(id) is not { } line || !line.Contains("-Embedding", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var process = Process.GetProcessById(id);
                found.Add((id, process.StartTime));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // gone already
            }
        }
        return found.Count == 1 ? found[0] : (0, default);
    }

    /// <summary>Kills process <paramref name="id"/> if it is still the one that started at <paramref name="started"/>.</summary>
    public static void Kill(int id, DateTime started)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.StartTime == started)
                process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }

    /// <summary>Gives the process time to end by itself, then kills it.</summary>
    public static void WaitThenKill(int id, DateTime started, TimeSpan wait)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.StartTime != started)
                return;
            if (!process.WaitForExit(wait))
                process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class OfficeSession
{
    private sealed class WorkItem(Func<OfficeApplication, object?>? work, TimeSpan timeout, CancellationToken token)
    {
        public Func<OfficeApplication, object?>? Work => work;
        public TimeSpan Timeout => timeout;
        public CancellationToken Token => token;
        public TaskCompletionSource<object?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long EnqueuedAt { get; } = Stopwatch.GetTimestamp();
        public long StartedAt;
        public int DialogTicks;
        public Exception? Aborted;

        /// <summary>Closes the application instead of working.</summary>
        public bool IsClose => work is null;
    }

    private static readonly List<OfficeSession> All = [];

    static OfficeSession() => AppDomain.CurrentDomain.ProcessExit += (_, _) => TerminateAll();

    private readonly OfficeKind _kind;
    private readonly OfficeTimings _timings;

    private readonly object _gate = new();
    private readonly Queue<WorkItem> _queue = new();
    private Thread? _thread;

    // Owned by the session's thread.
    private ComObject? _app;
    private bool _shared;

    // Read by the supervisor too, under _gate.
    private int _ownProcess;
    private DateTime _ownStarted;
    private HashSet<int>? _before;
    private bool _creating;
    private WorkItem? _running;

    public OfficeSession(OfficeKind kind, OfficeTimings timings)
    {
        _kind = kind;
        _timings = timings;
        lock (All)
            All.Add(this);
    }

    public OfficeKind Kind => _kind;

    /// <summary>Id of the Office process this session started (0 when it has none, or the application is shared).</summary>
    public int OwnProcess
    {
        get
        {
            lock (_gate)
                return _ownProcess;
        }
    }

    /// <summary>Runs <paramref name="work"/> with the application, on the session's thread.</summary>
    /// <param name="timeout">Longest time from the moment the job starts.</param>
    public async Task<T> RunAsync<T>(Func<OfficeApplication, T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var item = new WorkItem(app => work(app), timeout, cancellationToken);
        Enqueue(item);
        using var supervisor = new Timer(_ => Supervise(item), null, _timings.Tick, _timings.Tick);
        using var cancellation = cancellationToken.Register(() => Abort(item, new OperationCanceledException(cancellationToken)));
        return (T)(await item.Result.Task.ConfigureAwait(false))!;
    }

    /// <summary>Closes Office now if this session started it (tests; the thread also does it after being idle).</summary>
    public Task CloseAsync()
    {
        var item = new WorkItem(null, _timings.QuitWait + _timings.QuitWait, CancellationToken.None);
        Enqueue(item);
        return item.Result.Task;
    }

    private void Enqueue(WorkItem item)
    {
        lock (_gate)
        {
            _queue.Enqueue(item);
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = $"Office {_kind}" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            else
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    // ───────────────────────── The session's thread ─────────────────────────

    private void Loop()
    {
        while (true)
        {
            WorkItem? item = null;
            var idle = false;
            lock (_gate)
            {
                if (_queue.Count == 0)
                    idle = !Monitor.Wait(_gate, _timings.Idle) && _queue.Count == 0;
                if (!idle && _queue.Count > 0)
                    item = _queue.Dequeue();
            }
            if (item is not null)
            {
                Execute(item);
                continue;
            }
            if (!idle)
                continue;

            // No work for a while: close Office; if a job arrived meanwhile keep going, else end the thread.
            CloseApplication();
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _thread = null;
                    return;
                }
            }
        }
    }

    private void Execute(WorkItem item)
    {
        if (item.Result.Task.IsCompleted)
            return; // cancelled or timed out while it waited
        if (item.IsClose)
        {
            CloseApplication();
            item.Result.TrySetResult(null);
            return;
        }
        if (item.Token.IsCancellationRequested)
        {
            item.Result.TrySetCanceled(item.Token);
            return;
        }

        Volatile.Write(ref item.StartedAt, Stopwatch.GetTimestamp());
        lock (_gate)
            _running = item;
        try
        {
            EnsureApplication();
            var application = new OfficeApplication(_kind, _app!, _shared);
            using (application.Quiet())
                item.Result.TrySetResult(item.Work!(application));
        }
        catch (Exception ex)
        {
            var failure = item.Aborted ?? Translate(ex);
            if (failure is OperationCanceledException)
                item.Result.TrySetCanceled(item.Token);
            else
                item.Result.TrySetException(failure);
            // The application can't be trusted after a call that broke, or that we gave up on.
            if (item.Aborted is not null || ex is OfficeComException { IsServerGone: true } or ConversionFailureException { Key: "office.error.start" })
                DropApplication();
        }
        finally
        {
            lock (_gate)
                _running = null;
        }
    }

    private static Exception Translate(Exception ex) => ex switch
    {
        ConversionFailureException or OperationCanceledException => ex,
        OfficeComException { IsBusy: true } com => new ConversionFailureException("office.error.busy", null, com),
        OfficeComException { IsServerGone: true } com => new ConversionFailureException("office.error.crashed", null, com),
        OfficeComException com => new ConversionFailureException("office.error.failed", com.Message, com),
        _ => new ConversionFailureException("office.error.failed", ex.Message, ex),
    };

    /// <summary>Starts (or attaches to) the application and finds out whether the process is ours.</summary>
    private void EnsureApplication()
    {
        if (_app is not null)
            return;
        var processName = _kind.ProcessName();
        var before = OfficeProcesses.Pids(processName);
        lock (_gate)
        {
            _before = before;
            _creating = true;
        }
        ComObject app;
        try
        {
            app = ComObject.Create(_kind.ProgId());
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            throw new ConversionFailureException("office.error.start", (ex.InnerException ?? ex).Message, ex);
        }
        finally
        {
            lock (_gate)
                _creating = false;
        }

        var created = OfficeProcesses.Pids(processName).Where(id => !before.Contains(id));
        var (id, started) = OfficeProcesses.StartedByCom(created);
        _app = app;
        _shared = id == 0;
        lock (_gate)
        {
            _ownProcess = id;
            _ownStarted = started;
        }
        if (id != 0)
            OfficeNative.KillWithMandarina(id);
    }

    /// <summary>Lets the application go: after an abort the process is dead (ours) or still the user's (shared).</summary>
    private void DropApplication()
    {
        var app = _app;
        _app = null;
        lock (_gate)
            _ownProcess = 0;
        app?.Dispose();
    }

    /// <summary>Quits the application if it is ours (and kills it if it doesn't end), or just lets go of a shared one.</summary>
    private void CloseApplication()
    {
        var app = _app;
        if (app is null)
            return;
        _app = null;
        int id;
        DateTime started;
        lock (_gate)
        {
            id = _ownProcess;
            started = _ownStarted;
            _ownProcess = 0;
        }
        try
        {
            if (!_shared)
                app.Call("Quit");
        }
        catch (OfficeComException)
        {
            // it ends or is killed below
        }
        finally
        {
            app.Dispose();
        }
        if (id != 0)
            OfficeProcesses.WaitThenKill(id, started, _timings.QuitWait);
    }

    // ───────────────────────── The supervisor ─────────────────────────

    private void Supervise(WorkItem item)
    {
        try
        {
            if (item.Result.Task.IsCompleted)
                return;
            var now = Stopwatch.GetTimestamp();
            var startedAt = Volatile.Read(ref item.StartedAt);
            if (startedAt == 0)
            {
                if (Stopwatch.GetElapsedTime(item.EnqueuedAt, now) > _timings.QueueWait)
                    Abort(item, new ConversionFailureException("office.error.busy"));
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt, now);
            var process = OwnProcess;
            var dialog = process != 0 && elapsed > _timings.DialogGrace ? OfficeNative.BlockingDialogOf(process) : null;
            item.DialogTicks = dialog is null ? 0 : item.DialogTicks + 1;
            if (item.DialogTicks >= _timings.DialogTicks)
                Abort(item, new ConversionFailureException("office.error.dialog", dialog));
            else if (elapsed > item.Timeout)
                Abort(item, new ConversionFailureException("office.error.timeout", $"{item.Timeout.TotalSeconds:0} s"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a supervisor that throws would take the process down; the job's own timeout is the next check
        }
    }

    /// <summary>Gives the caller the failure now and, if this job holds Office, kills our Office process to free the thread.</summary>
    private void Abort(WorkItem item, Exception reason)
    {
        try
        {
            item.Aborted ??= reason;
            var done = reason is OperationCanceledException ? item.Result.TrySetCanceled(item.Token) : item.Result.TrySetException(reason);
            if (!done)
                return;
            bool holdsOffice;
            lock (_gate)
                holdsOffice = ReferenceEquals(_running, item);
            if (holdsOffice)
                KillOwn();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // see Supervise
        }
    }

    /// <summary>Kills the Office process this session started, by id. A shared application is never killed.</summary>
    private void KillOwn()
    {
        int id;
        DateTime started;
        HashSet<int>? before;
        bool creating;
        lock (_gate)
        {
            id = _ownProcess;
            started = _ownStarted;
            before = _before;
            creating = _creating;
        }
        if (id != 0)
        {
            OfficeProcesses.Kill(id, started);
            return;
        }
        if (creating && before is not null)
        {
            // The call that is stuck is the one creating the application: its process is up but not identified yet.
            var (found, foundStarted) = OfficeProcesses.StartedByCom(OfficeProcesses.Pids(_kind.ProcessName()).Where(p => !before.Contains(p)));
            if (found != 0)
                OfficeProcesses.Kill(found, foundStarted);
        }
    }

    /// <summary>Process exit: whatever Office this session started goes with Mandarina.</summary>
    private static void TerminateAll()
    {
        OfficeSession[] sessions;
        lock (All)
            sessions = [.. All];
        foreach (var session in sessions)
            session.KillOwn();
    }
}
