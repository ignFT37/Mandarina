# End-to-end look at a page of Mandarina's main window (Ajustes) with the INSTALLED app, by UI Automation only (no mouse, no keys):
# opens the window (Mandarina.exe --settings), goes to the page called <Page> in the side menu, writes a screenshot and the texts
# of the page (control type, name, AutomationId) so a check can read them. Pages: Atajos, Motores, General…
# Writes e2e-<Name>.log / .png next to this script.
# Usage: powershell -File e2e-settings.ps1 -Page Atajos [-Name settings] [-Select "TriggerWheel:1=Acciones"]
#   -Select "<AutomationId>:<index>=<item name>" picks an item of the index-th ComboBox with that AutomationId (to test a setting).
param(
    [Parameter(Mandatory = $true)][string]$Page,
    [string]$Name = 'settings',
    [string]$Select = ''
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
$log = Join-Path $dir "e2e-$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value $text -Encoding UTF8; Write-Host $text }
$AE = [Windows.Automation.AutomationElement]
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Mandarina\Mandarina.exe'
if (-not (Get-Process Mandarina -ErrorAction SilentlyContinue)) { Start-Process $exe -ArgumentList '--background'; Start-Sleep -Seconds 4 }
Start-Process $exe -ArgumentList '--settings'
Start-Sleep -Seconds 2
$root = $AE::RootElement
$procIds = @(Get-Process Mandarina | ForEach-Object Id)
$window = $null
for ($i = 0; $i -lt 40 -and -not $window; $i++) {
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($procIds -contains $w.Current.ProcessId -and $w.Current.BoundingRectangle.Width -gt 700 -and $w.Current.ClassName -ne 'Shell_TrayWnd') { $window = $w; break }
    }
    if (-not $window) { Start-Sleep -Milliseconds 300 }
}
if (-not $window) { Log 'ERROR no main window'; exit 1 }
Log "window '$($window.Current.Name)' $($window.Current.BoundingRectangle)"

# The side menu entry called <Page>.
$entry = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $Page)))
if (-not $entry) { Log "ERROR no menu entry '$Page'"; exit 1 }
$walker = [Windows.Automation.TreeWalker]::ControlViewWalker
$el = $entry
for ($level = 0; $level -lt 5 -and $el; $level++) {
    $obj = $null
    if ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$obj)) { $obj.Select(); break }
    if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$obj)) { $obj.Invoke(); break }
    $el = $walker.GetParent($el)
}
Start-Sleep -Milliseconds 1200

function Pick($spec) {
    if ($spec -notmatch '^(?<id>[^:]+):(?<index>\d+)=(?<item>.+)$') { Log "ERROR bad -Select '$spec'"; exit 1 }
    $combos = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Matches.id))))
    $combo = $combos[[int]$Matches.index]
    if (-not $combo) { Log "ERROR no combo $($Matches.id) #$($Matches.index)"; exit 1 }
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $byName = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $Matches.item)
    $byType = New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $item = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.AndCondition($byName, $byType)))
    if (-not $item) { Log "ERROR no item '$($Matches.item)'"; exit 1 }
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 600
    Log "picked '$($Matches.item)' in $($Matches.id) #$($Matches.index)"
}
if ($Select) { Pick $Select }

$r = $window.Current.BoundingRectangle
$bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
$g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
$bmp.Save((Join-Path $dir "e2e-$Name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

$count = 0
foreach ($e in $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.Name -or $c.AutomationId) {
        Log ("{0} | {1} | {2}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId)
        if (++$count -ge 700) { break }
    }
}
