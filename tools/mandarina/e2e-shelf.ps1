# End-to-end check of the floating shelf (the Float action) docking to the edges of the screen, with the INSTALLED app and real input:
# drags prueba.png from the Explorer window "carpeta" onto the Float petal of the actions wheel (Alt), then drags the shelf by its title
# to 12 px from the right edge of the usable area and lets go: it must end flush with the edge (its card, not the invisible margin around
# it); floats a second file: the shelf grows and must stay flush; drags it to the left edge: flush there; closes it with ×.
# Needs the Explorer window on e2e\carpeta open. Refuses to run unless the PC is idle (e2e-guard.ps1). Writes e2e-<Name>.log/.png.
# Usage: powershell -File e2e-shelf.ps1 [-Name shelf]
param([string]$Name = 'shelf')
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $dir 'e2e-guard.ps1')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ShelfInput {
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  static void Send(INPUT i) { SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
  public static void MoveTo(int x, int y) {
    int left = GetSystemMetrics(76), top = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
    var i = new INPUT { type = 0 };
    i.mi.dx = (int)((x - left) * 65535.0 / (w - 1)); i.mi.dy = (int)((y - top) * 65535.0 / (h - 1));
    i.mi.dwFlags = 0x0001 | 0x8000 | 0x4000; Send(i); }
  public static void Button(bool down) { var i = new INPUT { type = 0 }; i.mi.dwFlags = down ? 0x0002u : 0x0004u; Send(i); }
}
"@
[ShelfInput]::SetProcessDPIAware() | Out-Null
$log = Join-Path $dir "e2e-$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text); Write-Host $text }
function Fail($text) { Log "FAIL $text"; Mark-Synthetic; exit 1 }
function Check($ok, $text) { if ($ok) { Log "ok   $text" } else { Fail $text } }
Assert-UserAway

$root = [Windows.Automation.AutomationElement]::RootElement
function Shelf() {
    $procIds = @(Get-Process Mandarina -ErrorAction SilentlyContinue | ForEach-Object Id)
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($procIds -contains $w.Current.ProcessId -and $w.Current.ClassName -eq 'FloatShelfWindow') { return $w }
    }
}
$area = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
$scale = if ($dpi) { $dpi / 96.0 } else { 1.25 }
$margin = [int][Math]::Round(12 * $scale)      # the transparent margin around the card
Log "working area $($area.X),$($area.Y) $($area.Width)x$($area.Height) at $([int]($scale * 100)) %, card margin $margin px"

function Float($item) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'e2e-gesture.ps1') -Key Alt -Petal 1 -Count 6 -Name "$Name-float" -Item $item -Wait 3 2>&1 | Out-Null
}
function Drag-Shelf($cardEdgeTarget, $edge) {
    $r = (Shelf).Current.BoundingRectangle
    $sx = [int]($r.X + 60 * $scale); $sy = [int]($r.Y + 32 * $scale)        # the title of the card
    [ShelfInput]::MoveTo($sx, $sy); Start-Sleep -Milliseconds 300
    [ShelfInput]::Button($true); Start-Sleep -Milliseconds 200
    $dx = if ($edge -eq 'right') { $cardEdgeTarget - ($r.X + $r.Width - $margin) } else { $cardEdgeTarget - ($r.X + $margin) }
    for ($i = 1; $i -le 14; $i++) { [ShelfInput]::MoveTo([int]($sx + $dx * $i / 14), [int]($sy + 3 * $i)); Start-Sleep -Milliseconds 40 }
    [ShelfInput]::Button($false)
    Start-Sleep -Milliseconds 1200       # it docks 250 ms after it stops
}

Float 'prueba.png'
$shelf = Shelf
Check ($null -ne $shelf) 'the Float action opens the shelf'
$r = $shelf.Current.BoundingRectangle
Log "shelf at $($r.X),$($r.Y) $($r.Width)x$($r.Height)"

Drag-Shelf ($area.Right - 12) 'right'
$r = (Shelf).Current.BoundingRectangle
Log "after the drag to the right edge: window $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
Check (($r.X + $r.Width - $margin) -eq $area.Right) "the card is flush with the right edge ($($r.X + $r.Width - $margin) = $($area.Right))"
$widthBefore = $r.Width

Float 'prueba.jpg'
Start-Sleep -Milliseconds 1200
$r = (Shelf).Current.BoundingRectangle
Log "after floating a second file: window $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
Check ($r.Width -gt $widthBefore) 'the shelf grew'
Check (($r.X + $r.Width - $margin) -eq $area.Right) 'and its card is still flush with the right edge'
$bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
$g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
$bmp.Save((Join-Path $dir "e2e-$Name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

Drag-Shelf ($area.X + 10) 'left'
$r = (Shelf).Current.BoundingRectangle
Log "after the drag to the left edge: window $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
Check (($r.X + $margin) -eq $area.X) 'the card is flush with the left edge'

$close = (Shelf).FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
if ($close) { $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500 }
Check ($null -eq (Shelf)) 'the × closes the shelf'
Mark-Synthetic
Log 'DONE all checks passed'
