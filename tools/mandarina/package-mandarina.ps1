# Packages Mandarina for download: a portable zip and, if Inno Setup is installed, a per-user installer, each with its SHA-256.
#
#   pwsh tools/mandarina/package-mandarina.ps1 [-Version 0.1.0] [-Output <folder>] [-SkipPublish] [-NoInstaller]
#
# Publishes with publish-mandarina.ps1 (needs the engines rhwp and 7-Zip: pwsh build/fetch-engines.ps1 -Only rhwp,7zip), renames
# the launcher to Mandarina.exe, adds the licenses and writes, in -Output (default artifacts\release):
#   Mandarina-<version>-win-x64.zip        the portable build; unzip anywhere and run Mandarina.exe
#   Mandarina-<version>-win-x64-setup.exe  the installer (installer\Mandarina.iss); skipped without ISCC.exe
#   SHA256SUMS.txt                         one "<hash> *<file>" line per file (sha256sum -c compatible)
# Only what is in the repository goes in: the app, rhwp and 7-Zip. FFmpeg, Pandoc and the Whisper models are downloaded by the
# user from Settings -> Engines.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Output,
    [switch]$SkipPublish,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Output) { $Output = Join-Path $repo 'artifacts\release' }
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Not a version: '$Version'" }

$work = Join-Path $repo "artifacts\package\win-x64"
if (-not $SkipPublish) {
    # publish-mandarina.ps1 builds with the version of Directory.Build.props unless told otherwise.
    $env:MSBUILDDISABLENODEREUSE = '1'
    # Its own build root, so packaging does not disturb a development build (or a test run) of another checkout.
    if (-not $env:MandarinaBuildRoot) { $env:MandarinaBuildRoot = Join-Path $env:USERPROFILE '.mandarina\build-release\' }
    # The script throws when a step fails; $LASTEXITCODE is robocopy's there (1..7 mean "copied something").
    & (Join-Path $PSScriptRoot 'publish-mandarina.ps1') -Output $work -Version $Version -NoDebugInfo
}
if (-not (Test-Path (Join-Path $work 'Filee.exe'))) { throw "No published build in $work" }

# The launcher is called Mandarina.exe in the package (it loads Filee.dll whatever its own name is).
$exe = Join-Path $work 'Mandarina.exe'
if (Test-Path $exe) { Remove-Item $exe -Force }
Move-Item (Join-Path $work 'Filee.exe') $exe
# Debug symbols are 27 of the 141 MB (libSkiaSharp.pdb alone 21), and the XML documentation files are only for developers.
Get-ChildItem $work -Include *.pdb, *.xml -File -Recurse | Remove-Item -Force
# Nothing of the builder's user profile may travel in Mandarina's own files (the path of a .pdb is written into every DLL).
$latin1 = [Text.Encoding]::GetEncoding(28591)
$needles = @($env:USERPROFILE, ($env:USERPROFILE -replace '(.)', "`$1`0")) | Where-Object { $_ }
foreach ($file in Get-ChildItem $work -File | Where-Object { $_.Name -match '^(Filee|filee-cli|Mandarina)' -and $_.Extension -in '.dll', '.exe', '.json' }) {
    $text = $latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
    foreach ($needle in $needles) {
        if ($text.Contains($needle)) { throw "$($file.Name) contains '$($needle -replace "`0", '')': publish without debug info (-NoDebugInfo)" }
    }
}
# Code-signing services want one product name and version on every file: Mandarina's own programs and DLLs must agree, and say
# "Mandarina" (not Filee) where Task Manager and SmartScreen read it.
$info = foreach ($file in Get-ChildItem $work -File | Where-Object { $_.Name -match '^(Filee|filee-cli|Mandarina)' -and $_.Extension -in '.dll', '.exe' }) {
    $v = $file.VersionInfo
    if ($v.ProductName -ne 'Mandarina') { throw "$($file.Name): ProductName is '$($v.ProductName)', not Mandarina" }
    if ($v.FileDescription -notlike 'Mandarina*') { throw "$($file.Name): FileDescription is '$($v.FileDescription)', not Mandarina" }
    $v.ProductVersion
}
if (@($info | Select-Object -Unique).Count -ne 1) { throw "Mandarina's files disagree on ProductVersion: $(($info | Select-Object -Unique) -join ', ')" }
foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $repo $file) (Join-Path $work $file) -Force
}

New-Item -ItemType Directory -Force $Output | Out-Null
$zip = Join-Path $Output "Mandarina-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# One top-level folder, so unzipping does not scatter files.
$stage = Join-Path $repo "artifacts\package\zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
robocopy $work (Join-Path $stage 'Mandarina') /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
# ZipFile, not Compress-Archive: Windows PowerShell 5.1's writes backslashes in the entry names.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $stage 'Mandarina'), $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item $stage -Recurse -Force
Write-Host "Zip: $zip"

$installer = $null
if (-not $NoInstaller) {
    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iscc) {
        & $iscc "/DAppVersion=$Version" "/DPublishDir=$work" "/O$Output" "/Qp" (Join-Path $repo 'installer\Mandarina.iss')
        if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
        $installer = Join-Path $Output "Mandarina-$Version-win-x64-setup.exe"
        Write-Host "Installer: $installer"
    }
    else { Write-Warning 'Inno Setup (ISCC.exe) not found: no installer. Install it with: winget install JRSoftware.InnoSetup' }
}

$sums = foreach ($file in @($zip, $installer) | Where-Object { $_ }) {
    '{0} *{1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
# Unix line endings, so "sha256sum -c SHA256SUMS.txt" works on Linux and WSL too.
[IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), [Text.Encoding]::ASCII)
$sums | ForEach-Object { Write-Host $_ }
