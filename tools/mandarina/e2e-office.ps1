# End-to-end check of the Microsoft Office engine with the INSTALLED Mandarina: Settings > Engines (UI Automation) and then the real
# gesture (Shift, drop on the PDF petal) over an Office file of each kind, with real mouse and keyboard, so it refuses to run while the
# user is using the PC (e2e-guard.ps1).
# Samples, in a folder of their own (%USERPROFILE%\.mandarina\e2e\oficina\muestras): informe.docx, informe.doc, presentacion.pptx,
# presentacion.ppt, datos.xlsx, datos.xls and informe.pdf. They are made once: docx and pptx with Pandoc from a Markdown file, xlsx with
# filee-cli (convert datos.csv --to xlsx), the old formats with filee-cli through Office (docx -> doc, pptx -> ppt, xlsx -> xls) and the
# PDF with filee-cli (convert informe.docx --to pdf).
# It opens an Explorer window on the test folder (%USERPROFILE%\.mandarina\e2e\carpeta), drags each sample onto the PDF petal (PDF to PPTX
# for the PDF) and lists the new files next to the originals: check their producer ("Microsoft Word / PowerPoint / Excel 2021").
# Writes e2e-office-*.log / .png next to e2e-gesture.ps1. Run it from the e2e folder (the scripts are copied there).
# Usage: powershell -File e2e-office.ps1 [-SettingsOnly]
param([switch]$SettingsOnly)
$ErrorActionPreference = 'Stop'
$e2e = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $e2e 'oficina\muestras'
$work = Join-Path $e2e 'carpeta'
Set-Location $e2e
. (Join-Path $e2e 'e2e-guard.ps1')
Assert-UserAway

# The samples in the test folder (names without spaces)
$samples = @{
  'informe.docx' = 'informe.docx'; 'informe.doc' = 'informe.doc'
  'presentacion.pptx' = 'presentacion.pptx'; 'presentacion.ppt' = 'presentacion.ppt'
  'datos.xlsx' = 'datos.xlsx'; 'datos.xls' = 'datos.xls'
  'informe-pdf.pdf' = 'informe.pdf'
}
foreach ($k in $samples.Keys) { Copy-Item (Join-Path $src $samples[$k]) (Join-Path $work $k) -Force }
# No result of an earlier run
foreach ($n in 'informe.pdf', 'presentacion.pdf', 'datos.pdf', 'informe-pdf.pptx') { Remove-Item (Join-Path $work $n) -Force -ErrorAction SilentlyContinue }

# Settings > Engines (UI Automation only)
Write-Host '--- Settings / Engines'
& (Join-Path $e2e 'e2e-settings.ps1') -Page 'Motores' 2>&1 | Select-Object -First 60
if ($SettingsOnly) { return }

# An Explorer window on the test folder
$shell = New-Object -ComObject Shell.Application
if (-not ($shell.Windows() | Where-Object { $_.LocationName -like 'carpeta*' })) {
  Start-Process explorer.exe $work
  Start-Sleep -Seconds 3
}

# (file, key, petal, number of petals, label): the petal of the PDF in the wheel of each kind of file
$cases = @(
  @('informe.docx', 'Shift', 0, 7, 'docx-pdf'),
  @('presentacion.pptx', 'Shift', 0, 3, 'pptx-pdf'),
  @('datos.xlsx', 'Shift', 3, 4, 'xlsx-pdf'),
  @('informe.doc', 'Shift', 1, 8, 'doc-pdf'),
  @('presentacion.ppt', 'Shift', 0, 3, 'ppt-pdf'),
  @('datos.xls', 'Shift', 4, 5, 'xls-pdf'),
  @('informe-pdf.pdf', 'Shift', 1, 8, 'pdf-pptx')
)
foreach ($c in $cases) {
  Assert-UserAway
  $before = @(Get-ChildItem $work -Name)
  Write-Host ("--- {0}: petal {1} of {2}" -f $c[0], $c[2], $c[3])
  & (Join-Path $e2e 'e2e-gesture.ps1') -Key $c[1] -Petal $c[2] -Count $c[3] -Name ("office-" + $c[4]) -Item $c[0] -Wait 14 | Out-Null
  Mark-Synthetic
  $after = @(Get-ChildItem $work -Name)
  $new = $after | Where-Object { $before -notcontains $_ }
  Write-Host ("    new: " + ($new -join ', '))
  Get-Content (Join-Path $e2e ("e2e-office-" + $c[4] + ".log")) | Select-Object -Last 3 | ForEach-Object { Write-Host "    log: $_" }
}
Write-Host 'DONE'
