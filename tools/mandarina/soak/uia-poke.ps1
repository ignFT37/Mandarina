# Walks the whole UI Automation tree of every top-level window of a process, over and over, the way a screen reader (or the
# e2e scripts) does, to see whether that alone makes the process keep memory when its windows close.
# Usage: powershell -File uia-poke.ps1 -Match Soak.dll -Seconds 120 [-Full] [-Release]
# -Release collects this script's own garbage after every walk, like a client that lets go of the elements it has read;
# without it the elements are only released when PowerShell happens to run its garbage collector.
param(
    [string]$Match = 'Soak.dll',
    [int]$Seconds = 120,
    [switch]$Full,
    [switch]$Release
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class WinList {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  public static List<IntPtr> Of(uint pid) {
    var r = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
  public static string Title(IntPtr h) { var s = new System.Text.StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
}
'@
$target = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($Match) } | Select-Object -First 1
if (-not $target) { 'poke: no process matches ' + $Match; exit 1 }
"poke: walking the windows of process $($target.ProcessId) for $Seconds s"
$end = (Get-Date).AddSeconds($Seconds)
$walks = 0
$nodes = 0
$persistentOk = 0
$persistentFail = 0
while ((Get-Date) -lt $end) {
    foreach ($h in [WinList]::Of([uint32]$target.ProcessId)) {
        $isPersistent = ([WinList]::Title($h) -eq 'persistent')
        try {
            $e = [Windows.Automation.AutomationElement]::FromHandle($h)
            if ($isPersistent) {
                # the window that never closes: it must stay readable after other windows are let go
                $kids = $e.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
                if ($kids.Count -gt 0) { $persistentOk++ } else { $persistentFail++ }
                continue
            }
            if ($e.Current.BoundingRectangle.Width -lt 300) { continue }
            if ($Full) {
                $all = $e.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
                foreach ($n in $all) { $null = $n.Current.Name; $null = $n.Current.AutomationId; $nodes++ }
            } else {
                $null = $e.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'PanelClose')))
            }
            $walks++
        } catch { if ($isPersistent) { $persistentFail++ } }
    }
    if ($Release) { $all = $null; $e = $null; $n = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers() }
    Start-Sleep -Milliseconds 250
}
"poke: $walks walks, $nodes nodes read; persistent window readable $persistentOk times, unreadable $persistentFail"
