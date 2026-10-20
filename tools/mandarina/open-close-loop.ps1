# Opens and closes a tool panel many times in the INSTALLED Mandarina and prints how its memory moves: a panel that did not
# release its pictures, timers or windows would make the numbers climb with every round.
#
# By default it never touches UI Automation: it finds the window with EnumWindows and closes it with WM_CLOSE. That
# matters: a UIA client that has read a window keeps all of it alive (Avalonia never disconnects its automation nodes),
# so measuring with UIA adds ~3 MB per round of the photo editor that a normal user never has. -Uia measures that case.
#
# The panels open on screen and take focus: the script waits until the PC has been idle for -IdleSeconds before each
# round, so it does not interrupt somebody who is working (-IdleSeconds 0 skips the wait).
#
# Usage: powershell -File open-close-loop.ps1 -File <path> -Panel editImage -Title Editar -Rounds 30 [-Uia] [-IdleSeconds 30]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [Parameter(Mandatory = $true)][string]$Panel,
    [Parameter(Mandatory = $true)][string]$Title,
    [int]$Rounds = 30,
    [switch]$Uia,
    [int]$IdleSeconds = 30
)
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class LoopWin {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    public static IntPtr Find(HashSet<uint> pids, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            var s = new StringBuilder(256); GetWindowText(h, s, 256);
            RECT r; GetWindowRect(h, out r);
            if (s.ToString() == title && r.Right - r.Left > 300) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void Close(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }   // WM_CLOSE
    public static uint Gdi(IntPtr process) { return GetGuiResources(process, 0); }
    public static uint User(IntPtr process) { return GetGuiResources(process, 1); }
    public static double IdleSeconds() {
        var i = new LASTINPUTINFO(); i.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)); GetLastInputInfo(ref i);
        return ((uint)Environment.TickCount - i.dwTime) / 1000.0;
    }
}
'@
if ($Uia) { Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes }
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Mandarina\Mandarina.exe'
function Mem {
    $p = Get-Process Mandarina | Select-Object -First 1
    [pscustomobject]@{ WS = [int]($p.WorkingSet64 / 1MB); Private = [int]($p.PrivateMemorySize64 / 1MB); Handles = $p.HandleCount; Threads = $p.Threads.Count
        Gdi = [LoopWin]::Gdi($p.Handle); User = [LoopWin]::User($p.Handle) }
}
function Pids { $set = New-Object 'System.Collections.Generic.HashSet[uint32]'; Get-Process Mandarina | ForEach-Object { [void]$set.Add([uint32]$_.Id) }; $set }
function FindPanelUia {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $pids = @(Get-Process Mandarina | ForEach-Object Id)
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($pids -contains $w.Current.ProcessId -and $w.Current.Name -eq $Title -and $w.Current.BoundingRectangle.Width -gt 300) { return $w }
    }
    return $null
}
function PanelOpen { if ($Uia) { [bool](FindPanelUia) } else { [LoopWin]::Find((Pids), $Title) -ne [IntPtr]::Zero } }
function WaitIdle {
    $waited = 0
    while ($IdleSeconds -gt 0 -and [LoopWin]::IdleSeconds() -lt $IdleSeconds) {
        if ($waited -eq 0) { "esperando a que el PC esté libre ($IdleSeconds s sin teclado ni ratón)..." }
        Start-Sleep -Seconds 5; $waited += 5
    }
}
$start = Mem
$mode = if ($Uia) { 'con UI Automation' } else { 'sin UI Automation' }
"modo       : $mode"
"antes      : WS {0} MB, privada {1} MB, handles {2}, hilos {3}, GDI {4}, USER {5}" -f $start.WS, $start.Private, $start.Handles, $start.Threads, $start.Gdi, $start.User
for ($i = 1; $i -le $Rounds; $i++) {
    WaitIdle
    Start-Process $exe -ArgumentList '--panel', $Panel, "`"$File`""
    $open = $false
    for ($k = 0; $k -lt 40 -and -not $open; $k++) { Start-Sleep -Milliseconds 250; $open = PanelOpen }
    if (-not $open) { "ronda ${i}: el panel no apareció"; break }
    Start-Sleep -Milliseconds 1500      # lets the preview load
    if ($Uia) {
        $w = FindPanelUia
        $close = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'PanelClose')))
        $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    } else {
        [LoopWin]::Close([LoopWin]::Find((Pids), $Title))
    }
    for ($k = 0; $k -lt 20 -and (PanelOpen); $k++) { Start-Sleep -Milliseconds 200 }
    if ($i % 5 -eq 0) { $m = Mem; "ronda {0,2}   : WS {1} MB, privada {2} MB, handles {3}, hilos {4}, GDI {5}, USER {6}" -f $i, $m.WS, $m.Private, $m.Handles, $m.Threads, $m.Gdi, $m.User }
}
Start-Sleep -Seconds 3
$end = Mem
"después    : WS {0} MB, privada {1} MB, handles {2}, hilos {3}, GDI {4}, USER {5}" -f $end.WS, $end.Private, $end.Handles, $end.Threads, $end.Gdi, $end.User
"diferencia : WS {0:+0;-0;0} MB, privada {1:+0;-0;0} MB, handles {2:+0;-0;0}, GDI {3:+0;-0;0}, USER {4:+0;-0;0}" -f ($end.WS - $start.WS), ($end.Private - $start.Private), ($end.Handles - $start.Handles), ($end.Gdi - $start.Gdi), ($end.User - $start.User)
