// Mandarina: lets go of what a UI Automation client holds of a window that has closed.
//
// Avalonia hands the client one COM node per control it reads and never disconnects them: while the client keeps an
// element (a screen reader, a test script, anything that walks the windows), the node keeps its control, the control keeps
// its parent, and the whole closed window with its panel stays in memory (about 2 MB and a handful of GDI objects for the
// photo editor), once more with every window that is opened and closed. Measured with the soak harness
// (tools/mandarina/soak): +3 MB per panel while a client walked the windows, nothing at all without one.
// UiaDisconnectAllProviders drops every provider the process has registered; the UIA core asks Avalonia for fresh ones
// when a client looks again, so the windows still open lose nothing but the client's stale elements.

using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Filee.App.Mandarina.Panels;

internal static class AccessibilityRelease
{
    /// <summary>How long after a window closes its providers are dropped: the last events of the closing have gone by.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    /// <summary>What runs after the delay (tests swap it to count the calls).</summary>
    internal static Action Disconnect { get; set; } = DisconnectAllProviders;

    /// <summary>Call when a window has closed. Windows only; elsewhere there is nothing to drop.</summary>
    public static void AfterWindowClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // On a worker thread: the UIA core may be waiting for the UI thread to answer a client while this runs.
        DispatcherTimer.RunOnce(() => _ = Task.Run(Disconnect), Delay);
    }

    private static void DisconnectAllProviders()
    {
        try
        {
            UiaDisconnectAllProviders();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No UI Automation on this system (Windows Server Core, Wine): nothing is held either.
        }
    }

    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaDisconnectAllProviders();
}
