# End-to-end check of the first-run engine setup window with the INSTALLED app on this screen (its real size and scaling):
# starts Mandarina as if it were the first run (firstRunCompleted=false in settings.json, set from outside Claude through
# explorer.exe because Claude Desktop virtualises AppData; the app sets it back to true by itself), waits for the window
# "Elige los motores de conversión", checks it fits in the screen's working area with its buttons in view, writes a screenshot
# and closes it with "Más tarde". Writes e2e-<Name>.log / .png next to this script.
# Usage: powershell -File e2e-firstrun.ps1 [-Name firstrun]
param([string]$Name = 'firstrun')
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[Dpi]::SetProcessDPIAware() | Out-Null
$log = Join-Path $dir "e2e-$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text) -Encoding UTF8; Write-Host $text }
function Fail($text) { Log "FAIL $text"; exit 1 }

# The helper that flips the flag and restarts the app runs outside Claude (explorer.exe starts it), like the installer.
$helperDir = Join-Path $env:USERPROFILE '.mandarina\install'
$helper = Join-Path $helperDir 'firstrun-test.ps1'
@'
$ErrorActionPreference = 'Stop'
Get-Process Mandarina -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200
$path = Join-Path $env:APPDATA 'Mandarina\settings.json'
Copy-Item $path "$path.before-firstrun" -Force
$text = [IO.File]::ReadAllText($path)
[IO.File]::WriteAllText($path, ($text -replace '"firstRunCompleted":\s*true', '"firstRunCompleted": false'), (New-Object Text.UTF8Encoding($false)))
Start-Process (Join-Path $env:LOCALAPPDATA 'Programs\Mandarina\Mandarina.exe')
'OK' | Set-Content (Join-Path $env:USERPROFILE '.mandarina\install\firstrun-test.done')
'@ | Set-Content $helper -Encoding UTF8
'@powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firstrun-test.ps1"' | Set-Content (Join-Path $helperDir 'firstrun-test.cmd') -Encoding ASCII
Remove-Item (Join-Path $helperDir 'firstrun-test.done') -ErrorAction SilentlyContinue
$oldProcs = @(Get-Process Mandarina -ErrorAction SilentlyContinue | ForEach-Object Id)   # the helper replaces these
Start-Process explorer.exe -ArgumentList ('"' + (Join-Path $helperDir 'firstrun-test.cmd') + '"')

$root = [Windows.Automation.AutomationElement]::RootElement
$window = $null
for ($i = 0; $i -lt 60 -and -not $window; $i++) {
    Start-Sleep -Milliseconds 500
    $procIds = @(Get-Process Mandarina -ErrorAction SilentlyContinue | ForEach-Object Id | Where-Object { $oldProcs -notcontains $_ })
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($procIds -contains $w.Current.ProcessId -and $w.Current.Name -eq 'Elige los motores de conversión') { $window = $w; break }
    }
}
if (-not $window) { Fail 'the first-run engine setup did not open' }
# The rectangle is empty while the window is still being created: wait until it has a size.
$r = $window.Current.BoundingRectangle
for ($i = 0; $i -lt 40 -and ($r.Width -lt 100 -or $r.Height -lt 100); $i++) { Start-Sleep -Milliseconds 250; $r = $window.Current.BoundingRectangle }
Start-Sleep -Milliseconds 1500
$r = $window.Current.BoundingRectangle
if ($r.Width -lt 100) { Fail 'the setup window has no size' }
$area = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
Log ("screen working area {0}x{1} px at {2:P0} -> {3:N0} DIP tall; window {4}x{5} px at {6},{7}" -f $area.Width, $area.Height, ($dpi / 96.0), ($area.Height / ($dpi / 96.0)), $r.Width, $r.Height, $r.X, $r.Y)
$bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
$g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
$bmp.Save((Join-Path $dir "e2e-$Name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

$fits = ($r.Y -ge $area.Y - 1) -and (($r.Y + $r.Height) -le ($area.Y + $area.Height + 1)) -and ($r.X -ge $area.X - 1) -and (($r.X + $r.Width) -le ($area.X + $area.Width + 1))
Log ("fits in the working area: {0}" -f $fits)
$later = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Más tarde')))
$lb = if ($later) { $later.Current.BoundingRectangle } else { $null }
$offscreen = if ($later) { $later.Current.IsOffscreen } else { $true }
if ($lb) { Log ("'Más tarde' button at y {0}..{1}; offscreen: {2}" -f $lb.Y, ($lb.Y + $lb.Height), $offscreen) }
$scroll = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'EngineScroller')))
Log ("engine list scroller found: {0}" -f ($null -ne $scroll))
if ($later) { $later.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Log "closed with 'Más tarde'" }
if (-not $fits) { Fail 'the window is bigger than the usable area of the screen' }
if (-not $lb -or $offscreen) { Fail 'the buttons are not in view' }
Log 'DONE the setup window fits this screen'
