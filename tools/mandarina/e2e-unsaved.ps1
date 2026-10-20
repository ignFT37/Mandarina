# End-to-end check of the "Cambios sin guardar" question with the INSTALLED app and real input (mouse to draw, keyboard for Esc).
# Opens the annotate panel on <File> and checks, in this order:
#   1. nothing drawn: Esc closes the window at once;
#   2. a mark drawn with the mouse: the ✕ asks first (Seguir editando goes back to the panel), Esc asks, Esc on the question
#      answers it (keeps the panel), Alt+F4 asks too, and ✕ + "Cerrar sin guardar" closes the window while Mandarina keeps running.
# Writes e2e-<Name>.log and e2e-<Name>-*.png next to this script. Refuses to run unless the PC is idle (e2e-guard.ps1).
# Usage: powershell -File e2e-unsaved.ps1 -File <photo> [-Name unsaved]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Name = 'unsaved'
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $dir 'e2e-guard.ps1')
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Input {
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint PidAt(int x, int y) { uint pid; GetWindowThreadProcessId(GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2), out pid); return pid; }
  public static double IdleSeconds() { var i = new LASTINPUTINFO(); i.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)); GetLastInputInfo(ref i); return (Environment.TickCount - (int)i.dwTime) / 1000.0; }
  static void Send(INPUT i) { SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
  public static void MoveTo(int x, int y) {
    int left = GetSystemMetrics(76), top = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
    var i = new INPUT { type = 0 };
    i.mi.dx = (int)((x - left) * 65535.0 / (w - 1)); i.mi.dy = (int)((y - top) * 65535.0 / (h - 1));
    i.mi.dwFlags = 0x0001 | 0x8000 | 0x4000; Send(i); }
  public static void Button(bool down) { var i = new INPUT { type = 0 }; i.mi.dwFlags = down ? 0x0002u : 0x0004u; Send(i); }
  public static void Key(ushort vk, bool down) { var i = new INPUT { type = 1 }; i.ki.wVk = vk; i.ki.dwFlags = down ? 0u : 0x0002u; Send(i); }
}
"@
[Input]::SetProcessDPIAware() | Out-Null
$log = Join-Path $dir "e2e-$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text); Write-Host $text }
function Shot($r, $path) {
    $bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
function Fail($text) { Log "FAIL $text"; Mark-Synthetic; exit 1 }
function Check($ok, $text) { if ($ok) { Log "ok   $text" } else { Fail $text } }

Assert-UserAway
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Mandarina\Mandarina.exe'
if (-not (Get-Process Mandarina -ErrorAction SilentlyContinue)) { Start-Process $exe; Start-Sleep -Seconds 4 }
$root = [Windows.Automation.AutomationElement]::RootElement

function PanelWindow($title) {
    $procIds = @(Get-Process Mandarina | ForEach-Object Id)
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($procIds -contains $w.Current.ProcessId -and $w.Current.Name -eq $title -and $w.Current.BoundingRectangle.Width -gt 300) { return $w }
    }
    return $null
}
function WaitPanel($title, $present, $seconds = 15) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        $w = PanelWindow $title
        if ($present -and $w) { return $w }
        if (-not $present -and -not $w) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Find($window, $id) {
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function Press($window, $id) {
    $el = Find $window $id
    if (-not $el) { Fail "no button $id" }
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
}
function Esc($window) {
    [Input]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [Input]::Key(0x1B, $true); Start-Sleep -Milliseconds 60; [Input]::Key(0x1B, $false)
    Start-Sleep -Milliseconds 600
}
function AltF4($window) {
    [Input]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [Input]::Key(0x12, $true); [Input]::Key(0x73, $true); Start-Sleep -Milliseconds 60; [Input]::Key(0x73, $false); [Input]::Key(0x12, $false)   # Alt + F4
    Start-Sleep -Milliseconds 700
}
function OpenPanel() {
    Start-Process $exe -ArgumentList @('--panel', 'annotateImage', "`"$File`"")
    $w = WaitPanel 'Anotar' $true
    if (-not $w) { Fail 'the Annotate panel did not open' }
    Start-Sleep -Milliseconds 2000
    return $w
}

# 1. Nothing drawn: Esc closes at once.
$w = OpenPanel
Esc $w
Check ($null -ne (WaitPanel 'Anotar' $false 5)) 'with nothing drawn, Esc closes the panel at once'

# 2. Draw a mark with the real mouse.
$w = OpenPanel
$r = $w.Current.BoundingRectangle
Shot $r (Join-Path $dir "e2e-$Name-1-open.png")
$x1 = [int]($r.X + $r.Width * 0.40); $y1 = [int]($r.Y + $r.Height * 0.38); $x2 = [int]($r.X + $r.Width * 0.75); $y2 = [int]($r.Y + $r.Height * 0.66)
[Input]::MoveTo($x1, $y1); Start-Sleep -Milliseconds 300
if ([Input]::PidAt($x1, $y1) -ne $w.Current.ProcessId) { Fail 'another window covers the drawing area' }
[Input]::Button($true); Start-Sleep -Milliseconds 150
for ($i = 1; $i -le 12; $i++) { [Input]::MoveTo([int]($x1 + ($x2 - $x1) * $i / 12), [int]($y1 + ($y2 - $y1) * $i / 12)); Start-Sleep -Milliseconds 40 }
[Input]::Button($false); Start-Sleep -Milliseconds 600
$apply = Find $w 'PanelApply'
Check ($apply -and $apply.Current.IsEnabled) 'the mark is drawn (Aplicar is enabled)'

# 3. ✕ asks first; "Seguir editando" goes back.
Press $w 'PanelClose'
$keep = Find $w 'PanelUnsavedKeep'
Check ($null -ne $keep) 'the ✕ asks "Cambios sin guardar" and the window stays open'
Shot $r (Join-Path $dir "e2e-$Name-2-question.png")
Check (-not (Find $w 'PanelApply').Current.IsEnabled) 'the panel under the question does not take input'
Press $w 'PanelUnsavedKeep'
Check ($null -eq (Find $w 'PanelUnsavedKeep')) '"Seguir editando" goes back to the panel'
Check ((Find $w 'PanelApply').Current.IsEnabled) 'and the panel works again'

# 4. Esc asks, Esc answers it.
Esc $w
Check ($null -ne (Find $w 'PanelUnsavedKeep')) 'Esc asks first'
Esc $w
Check (($null -eq (Find $w 'PanelUnsavedKeep')) -and ($null -ne (PanelWindow 'Anotar'))) 'Esc on the question keeps the panel'

# 4b. Alt+F4 (and the taskbar's Close) asks too: the window hears WM_CLOSE.
AltF4 $w
Check ($null -ne (Find $w 'PanelUnsavedKeep')) 'Alt+F4 asks first as well, and the window stays open'
Esc $w
Check (($null -eq (Find $w 'PanelUnsavedKeep')) -and ($null -ne (PanelWindow 'Anotar'))) 'Esc on that question keeps the panel too'

# 5. ✕ + "Cerrar sin guardar" closes; Mandarina keeps running.
Press $w 'PanelClose'
Check ($null -ne (Find $w 'PanelUnsavedDiscard')) 'the ✕ asks again'
Press $w 'PanelUnsavedDiscard'
Check ($null -ne (WaitPanel 'Anotar' $false 5)) '"Cerrar sin guardar" closes the panel'
Check ($null -ne (Get-Process Mandarina -ErrorAction SilentlyContinue)) 'Mandarina keeps running'
Mark-Synthetic
Log 'DONE all checks passed'
