# End-to-end check of "Ajustes → Atajos → Rueda que abre" with the INSTALLED app and the real gesture: sets the Shift gesture to open the
# actions wheel (through the settings page, by UI Automation), drags prueba.png and taps Shift: the ACTIONS wheel must open (its second
# petal, Float, puts the file on the shelf); then it puts the setting back to "Convertir" and checks settings.json. Needs the Explorer
# window on e2e\carpeta open. Refuses to run unless the PC is idle (e2e-guard.ps1). Writes e2e-<Name>.log.
# Usage: powershell -File e2e-wheel-setting.ps1 [-Name wheel-setting]
param([string]$Name = 'wheel-setting')
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $dir 'e2e-guard.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$log = Join-Path $dir "e2e-$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text) -Encoding UTF8; Write-Host $text }
function Fail($text) { Log "FAIL $text"; Restore; Mark-Synthetic; exit 1 }
function Check($ok, $text) { if ($ok) { Log "ok   $text" } else { Fail $text } }
$settings = Join-Path $env:APPDATA 'Mandarina\settings.json'
function ShiftWheel() {
    $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    ($json.triggers | Where-Object { $_.kind -eq 'Drag' -and $_.modifiers -eq 'Shift' } | Select-Object -First 1).wheel
}
function Settings-Pick($value) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'e2e-settings.ps1') -Page Atajos -Name "$Name-settings" -Select "TriggerWheel:0=$value" 2>&1 | Out-Null
    Start-Sleep -Milliseconds 800
}
function Restore() { if ((ShiftWheel) -ne 'Convert') { Settings-Pick 'Convertir' } }
Assert-UserAway

Log "the Shift gesture opens: $(ShiftWheel)"
Check ((ShiftWheel) -eq 'Convert') 'it starts on Convert'
Settings-Pick 'Acciones'
Check ((ShiftWheel) -eq 'Actions') 'choosing "Acciones" in the page is saved to settings.json'

# Shift now opens the actions wheel: its second petal (Float) puts the file on the shelf.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'e2e-gesture.ps1') -Key Shift -Petal 1 -Count 6 -Name "$Name-gesture" -Item 'prueba.png' -Wait 2 2>&1 | Out-Null
$root = [Windows.Automation.AutomationElement]::RootElement
$procIds = @(Get-Process Mandarina | ForEach-Object Id)
$shelf = $null
for ($i = 0; $i -lt 20 -and -not $shelf; $i++) {
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($procIds -contains $w.Current.ProcessId -and $w.Current.ClassName -eq 'FloatShelfWindow') { $shelf = $w; break }
    }
    if (-not $shelf) { Start-Sleep -Milliseconds 250 }
}
Check ($null -ne $shelf) 'Shift opened the ACTIONS wheel: its Float petal opened the shelf'
$close = $shelf.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
if ($close) { $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }

Settings-Pick 'Convertir'
Check ((ShiftWheel) -eq 'Convert') 'the setting is back on Convert'
Mark-Synthetic
Log 'DONE all checks passed'
