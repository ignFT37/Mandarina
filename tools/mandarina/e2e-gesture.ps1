# End-to-end check of Mandarina's gesture with real input: drags prueba.png in an Explorer window, taps a wheel key,
# moves onto a petal and drops. Writes screenshots and a result log next to this script.
# Usage: powershell -File e2e-gesture.ps1 -Key Shift|Ctrl|Alt -Petal <index> -Count <petals> -Name <label> [-Item <name>] [-Also <names>] [-Wait <s>]
#   -Item: the file or folder to drag, by its exact name in the Explorer window (default: the first "prueba*" without spaces).
#   -Also: more items of the same window to select first (Ctrl-click, by UI Automation), so the drag carries a mixed selection.
#   -Wait: seconds to wait after the drop before listing the folder and the clipboard (a dialog may need longer).
param(
    [ValidateSet('Shift', 'Ctrl', 'Alt')][string]$Key = 'Shift',
    [int]$Petal = 0,
    [int]$Count = 7,
    [string]$Name = 'convert',
    [string]$Item = '',
    [string[]]$Also = @(),
    [int]$Wait = 4
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$work = Join-Path $dir 'carpeta'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Input {
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
  public static IntPtr RootAt(int x, int y) { return GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2); }
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
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text) }
Remove-Item $log -ErrorAction SilentlyContinue

# The Explorer window on the test folder.
$root = [Windows.Automation.AutomationElement]::RootElement
$window = $null
for ($i = 0; $i -lt 30 -and -not $window; $i++) {
    $window = $root.FindAll([Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, 'CabinetWClass'))) |
        Where-Object { $_.Current.Name -like 'carpeta*' } | Select-Object -First 1
    if (-not $window) { Start-Sleep -Milliseconds 300 }
}
if (-not $window) { Log 'ERROR no Explorer window'; exit 1 }
[Input]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$items = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
$tile = if ($Item) { $items | Where-Object { $_.Current.Name -eq $Item } | Select-Object -First 1 }
        else { $items | Where-Object { $_.Current.Name -like 'prueba*' -and $_.Current.Name -notlike '* *' } | Select-Object -First 1 }
if (-not $tile) { Log 'ERROR the item to drag is not in the Explorer window'; exit 1 }
# A mixed selection: the dragged item plus the others (selected by UI Automation, no clicks).
if ($Also.Count -gt 0) {
    $tile.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    foreach ($other in $Also) {
        $el = $items | Where-Object { $_.Current.Name -eq $other } | Select-Object -First 1
        if (-not $el) { Log "ERROR '$other' is not in the Explorer window"; exit 1 }
        $el.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    }
    Log ("selected: " + (@($Item) + $Also -join ', '))
    Start-Sleep -Milliseconds 400
}
$r = $tile.Current.BoundingRectangle
$x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)   # the middle: the picture of a tile, the row of a list
Log "item '$($tile.Current.Name)' at $x,$y"
# Safety: only press the button when the Explorer window is what is under the pointer.
if ([Input]::RootAt($x, $y) -ne [IntPtr]$window.Current.NativeWindowHandle) { Log 'ERROR another window covers the item'; exit 1 }

$vk = @{ Shift = 0xA0; Ctrl = 0xA2; Alt = 0xA4 }[$Key]
[Input]::MoveTo($x, $y); Start-Sleep -Milliseconds 200
[Input]::Button($true); Start-Sleep -Milliseconds 150
for ($s = 1; $s -le 12; $s++) { [Input]::MoveTo($x + 3 * $s, $y + 2 * $s); Start-Sleep -Milliseconds 30 }
$cx = $x + 36; $cy = $y + 24
Start-Sleep -Milliseconds 300
[Input]::Key($vk, $true); Start-Sleep -Milliseconds 90; [Input]::Key($vk, $false)
Log "tapped $Key at $cx,$cy"
Start-Sleep -Milliseconds 120
[Input]::MoveTo($cx + 1, $cy); Start-Sleep -Milliseconds 400

# Where the wheel is: centred on the pointer; petal i sits at angle i·360/n clockwise from the top, mid radius.
$dpi =(Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
$factor = if ($dpi) { $dpi / 96.0 } else { 1.5 }
$radius = (124 * (1 + 0.41) / 2) * $factor
$angle = [Math]::PI * 2 * $Petal / $Count
$tx = [int]($cx + 1 + $radius * [Math]::Sin($angle)); $ty = [int]($cy - $radius * [Math]::Cos($angle))
for ($s = 1; $s -le 10; $s++) { [Input]::MoveTo([int]($cx + ($tx - $cx) * $s / 10), [int]($cy + ($ty - $cy) * $s / 10)); Start-Sleep -Milliseconds 35 }
Start-Sleep -Milliseconds 450

$size = [int](300 * $factor)
$bmp = New-Object Drawing.Bitmap $size, $size
$g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($cx - $size / 2, $cy - $size / 2, 0, 0, $bmp.Size); $g.Dispose()
$bmp.Save((Join-Path $dir "e2e-$Name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Log "on petal $Petal at $tx,$ty (factor $factor)"
[Input]::Button($false)
Log 'dropped'
Start-Sleep -Seconds $Wait
Get-ChildItem $work | ForEach-Object { Log ("file: " + $_.Name) }
try { Log ("clipboard: " + (Get-Clipboard -Raw)) } catch { }
