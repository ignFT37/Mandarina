# End-to-end check of a Mandarina tool panel with the INSTALLED app.
#   Direct mode (-Panel <id>): Mandarina.exe --panel <id> <file> opens the panel; no mouse or keyboard is touched.
#   Wheel mode (default): opens the tools wheel at the cursor (Mandarina.exe --tools <file>) and clicks petal <Petal> of
#   <Count> with real mouse input (only when the wheel is under the pointer and the pointer is where it should be).
# Then waits for the panel window titled <Title>, saves a screenshot, presses Apply with UI Automation (AutomationId
# PanelApply), waits for the status line (AutomationId PanelStatus) and lists the files that appeared beside the original.
# Writes e2e-<Name>.log / .png next to this script (-after.png once the buttons of -Press are pressed, -result.png once Apply says its status).
# -Auto: for a tool that starts by itself when its window opens (TRANSCRIBE): Apply is not pressed, the script just waits for the
# status line.
# Usage: powershell -File e2e-tool.ps1 -File <path> -Panel trimAudio -Title Recortar -Name trimaudio [-NoApply] [-Auto] [-Extra <more files>]
# -Settle <ms>: how long to wait after each button pressed with -Press (default 600; a detection needs several seconds).
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Panel = '',
    [int]$Petal = 0,
    [int]$Count = 4,
    [string]$Title = '',
    [string]$Name = 'tool',
    [string[]]$Extra = @(),
    [string[]]$Press = @(),
    [int]$Settle = 600,
    [double[]]$Drag = @(),
    [string]$Type = '',
    [switch]$NoApply,
    [switch]$Auto,
    [int]$WaitSeconds = 120
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Input {
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint PidAt(int x, int y) { uint pid; GetWindowThreadProcessId(GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2), out pid); return pid; }
  static void Send(INPUT i) { SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
  public static void MoveTo(int x, int y) {
    int left = GetSystemMetrics(76), top = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
    var i = new INPUT { type = 0 };
    i.mi.dx = (int)((x - left) * 65535.0 / (w - 1)); i.mi.dy = (int)((y - top) * 65535.0 / (h - 1));
    i.mi.dwFlags = 0x0001 | 0x8000 | 0x4000; Send(i); }
  public static void Button(bool down) { var i = new INPUT { type = 0 }; i.mi.dwFlags = down ? 0x0002u : 0x0004u; Send(i); }
  public static int[] Cursor() { POINT p; GetCursorPos(out p); return new[] { p.X, p.Y }; }
}
"@
[Input]::SetProcessDPIAware() | Out-Null
$log = Join-Path $dir "e2e-$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text) }
function Shot($x, $y, $w, $h, $path) {
    $bmp = New-Object Drawing.Bitmap ([int]$w), ([int]$h)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$x, [int]$y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

$exe = Join-Path $env:LOCALAPPDATA 'Programs\Mandarina\Mandarina.exe'
if (-not (Test-Path $exe)) { Log 'ERROR Mandarina is not installed'; exit 1 }
if (-not (Get-Process Mandarina -ErrorAction SilentlyContinue)) { Start-Process $exe; Start-Sleep -Seconds 4 }
$folder = Split-Path -Parent $File
$before = @(Get-ChildItem $folder | ForEach-Object Name)
$root = [Windows.Automation.AutomationElement]::RootElement
$files = @($File) + $Extra | ForEach-Object { "`"$_`"" }

if ($Panel -ne '') {
    Start-Process $exe -ArgumentList (@('--panel', $Panel) + $files)
    Log "panel $Panel requested for $File"
}
else {
    $cx = 760; $cy = 420
    [Input]::MoveTo($cx, $cy); Start-Sleep -Milliseconds 500
    [Input]::MoveTo($cx + 1, $cy); Start-Sleep -Milliseconds 300
    Start-Process $exe -ArgumentList (@('--tools') + $files)
    Start-Sleep -Milliseconds 1800
    $procIds = @(Get-Process Mandarina | ForEach-Object Id)
    $wheel = $null
    for ($i = 0; $i -lt 20 -and -not $wheel; $i++) {
        foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
            $b = $w.Current.BoundingRectangle
            if ($procIds -contains $w.Current.ProcessId -and $b.Contains($cx, $cy) -and $b.Width -gt 200 -and $b.Width -lt 800) { $wheel = $w; break }
        }
        if (-not $wheel) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $wheel) { Log 'ERROR the wheel is not under the pointer; nothing clicked'; exit 1 }
    $wb = $wheel.Current.BoundingRectangle
    $dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
    $factor = if ($dpi) { $dpi / 96.0 } else { 1.25 }
    $radius = (124 * (1 + 0.41) / 2) * $factor
    $angle = [Math]::PI * 2 * $Petal / $Count
    $mx = [int]($wb.X + $wb.Width / 2); $my = [int]($wb.Y + $wb.Height / 2)
    $tx = [int]($mx + $radius * [Math]::Sin($angle)); $ty = [int]($my - $radius * [Math]::Cos($angle))
    for ($s = 1; $s -le 8; $s++) { [Input]::MoveTo([int]($cx + 1 + ($tx - $cx - 1) * $s / 8), [int]($cy + ($ty - $cy) * $s / 8)); Start-Sleep -Milliseconds 30 }
    Start-Sleep -Milliseconds 400
    $side = [int](300 * $factor)
    Shot ($mx - $side / 2) ($my - $side / 2) $side $side (Join-Path $dir "e2e-$Name-wheel.png")
    # Safety: the pointer must be where we put it and Mandarina's window must be under it, or nothing is clicked.
    $now = [Input]::Cursor()
    if ([Math]::Abs($now[0] - $tx) -gt 3 -or [Math]::Abs($now[1] - $ty) -gt 3 -or $procIds -notcontains [Input]::PidAt($tx, $ty)) {
        Log "ERROR the pointer moved ($($now -join ',') instead of $tx,$ty) or another window is there; nothing clicked"; exit 1
    }
    Log "clicking petal $Petal of $Count at $tx,$ty (factor $factor)"
    [Input]::Button($true); Start-Sleep -Milliseconds 60; [Input]::Button($false)
}

# The panel window.
$procIds = @(Get-Process Mandarina | ForEach-Object Id)
$panelWindow = $null
for ($i = 0; $i -lt 60 -and -not $panelWindow; $i++) {
    Start-Sleep -Milliseconds 250
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($procIds -contains $w.Current.ProcessId -and $w.Current.Name -ne 'Mandarina' -and $w.Current.BoundingRectangle.Width -gt 300 -and ($Title -eq '' -or $w.Current.Name -eq $Title)) { $panelWindow = $w; break }
    }
}
if (-not $panelWindow) { Log 'ERROR the panel did not appear'; exit 1 }
$r = $panelWindow.Current.BoundingRectangle
Log "panel '$($panelWindow.Current.Name)' at $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
Start-Sleep -Milliseconds 2500   # previews, waveforms, detections…
Shot $r.X $r.Y $r.Width $r.Height (Join-Path $dir "e2e-$Name.png")
if ($NoApply) {
    $close = $panelWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'PanelClose')))
    if ($close) { $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Log 'NoApply: screenshot taken, panel closed' } else { Log 'NoApply: panel left open' }
    exit 0
}

function PressByName($label) {
    $el = $panelWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $label)))
    if (-not $el) { Log "WARN nothing named '$label' to press"; return }
    # The element found by name may be the text inside the button: climb until something can be pressed.
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    for ($level = 0; $level -lt 4 -and $el; $level++) {
        foreach ($pattern in @([Windows.Automation.InvokePattern]::Pattern, [Windows.Automation.TogglePattern]::Pattern, [Windows.Automation.SelectionItemPattern]::Pattern)) {
            $obj = $null
            if ($el.TryGetCurrentPattern($pattern, [ref]$obj)) {
                if ($obj -is [Windows.Automation.InvokePattern]) { $obj.Invoke() }
                elseif ($obj -is [Windows.Automation.TogglePattern]) { $obj.Toggle() }
                else { $obj.Select() }
                Log "pressed '$label'"; Start-Sleep -Milliseconds $Settle; return
            }
        }
        $el = $walker.GetParent($el)
    }
    Log "WARN '$label' has no invoke/toggle pattern"
}
foreach ($label in $Press) { PressByName $label }
if ($Drag.Count -eq 4) {
    $x1 = [int]($r.X + $r.Width * $Drag[0]); $y1 = [int]($r.Y + $r.Height * $Drag[1]); $x2 = [int]($r.X + $r.Width * $Drag[2]); $y2 = [int]($r.Y + $r.Height * $Drag[3])
    [Input]::MoveTo($x1, $y1); Start-Sleep -Milliseconds 300
    if ($procIds -notcontains [Input]::PidAt($x1, $y1)) { Log "ERROR another window is under the drag start $x1,$y1; nothing dragged"; exit 1 }
    [Input]::Button($true); Start-Sleep -Milliseconds 150
    for ($i = 1; $i -le 12; $i++) { [Input]::MoveTo([int]($x1 + ($x2 - $x1) * $i / 12), [int]($y1 + ($y2 - $y1) * $i / 12)); Start-Sleep -Milliseconds 40 }
    [Input]::Button($false); Start-Sleep -Milliseconds 500
    Log "dragged $x1,$y1 -> $x2,$y2"
}
if ($Press.Count -gt 0) { Start-Sleep -Milliseconds 800; Shot $r.X $r.Y $r.Width $r.Height (Join-Path $dir "e2e-$Name-after.png") }

function Find($id) {
    $panelWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
if ($Auto) {
    Log 'Auto: the tool starts by itself, Apply is not pressed'
}
else {
    $apply = Find 'PanelApply'
    if (-not $apply) { Log 'ERROR no Apply button found by AutomationId'; exit 1 }
    Log "Apply enabled: $($apply.Current.IsEnabled)"
    if (-not $apply.Current.IsEnabled) { Log 'ERROR Apply is disabled'; exit 1 }
    $apply.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Log 'Apply pressed'
}

$status = $null
$end = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 400
    try {
        $st = Find 'PanelStatus'
        if ($st -and $st.Current.Name) { $status = $st.Current.Name; if ($status -notlike 'Aplicando*') { break } }
    } catch { break }   # the window closed
}
Log "status: $status"
if ($status) { Shot $r.X $r.Y $r.Width $r.Height (Join-Path $dir "e2e-$Name-result.png") }
Start-Sleep -Seconds 2
$after = @(Get-ChildItem $folder | ForEach-Object Name)
foreach ($n in ($after | Where-Object { $before -notcontains $_ })) { Log "NEW: $n" }
