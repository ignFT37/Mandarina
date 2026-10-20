# Installs or updates Mandarina for the current user from a build made by publish-mandarina.ps1.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\mandarina\install-mandarina.ps1 [-Source <folder>] [-Engines <folder>] [-AddCliToPath]
#
# What it does: stops a running Mandarina, copies the program to %LOCALAPPDATA%\Programs\Mandarina (as Mandarina.exe), copies the
# engines it finds in -Engines (FFmpeg, Pandoc, the Whisper models...) to where the app keeps its own downloads, so Settings ->
# Engines shows them as installed, adds a Start menu shortcut, optionally puts the `mandarina` command on the user's PATH, and
# starts Mandarina. Engines that are not in -Engines can be downloaded later from Settings -> Engines. The app itself registers
# "start with Windows" and the "Convert with Mandarina" context menu entry (both can be turned off in Settings -> General).
#
# Run it from a normal PowerShell or from the Explorer. Programs packaged like Microsoft Store apps see a private copy of
# %LOCALAPPDATA%: start it through explorer.exe if you launch it from one of them, or the real AppData and Start menu stay untouched.
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $env:USERPROFILE '.mandarina\publish\win-x64'),
    [string]$Engines = (Join-Path $env:USERPROFILE '.mandarina\engines'),
    [switch]$AddCliToPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$home_ = Join-Path $env:USERPROFILE '.mandarina\install'
New-Item -ItemType Directory -Force $home_ | Out-Null
Start-Transcript -Path (Join-Path $home_ 'install.log') -Force | Out-Null
try {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\Mandarina'
    if (-not (Test-Path (Join-Path $Source 'Filee.exe'))) { throw "No published build in $Source (run publish-mandarina.ps1 first)" }

    # Only one Mandarina may listen to the gestures: stop a test build or an older install.
    Get-Process Filee, Mandarina -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    robocopy $Source $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    # The program is Mandarina.exe; the launcher loads Filee.dll whatever its own name is.
    Move-Item (Join-Path $target 'Filee.exe') (Join-Path $target 'Mandarina.exe') -Force

    # Engines: every package the manifest knows that is already downloaded, marked with the hash the manifest pins. The ones that
    # ship inside the program (engines\ next to Mandarina.exe: rhwp, 7-Zip) are not copied again; a copy an earlier run of this
    # script made (it has the marker file) is removed.
    $manifest = Get-Content (Join-Path $repo 'src\Filee.Engines\Infrastructure\engines.json') -Raw | ConvertFrom-Json
    $installed = Join-Path $env:LOCALAPPDATA 'Mandarina\engines'
    foreach ($component in $manifest.components.PSObject.Properties) {
        $dest = Join-Path $installed $component.Name
        if (Test-Path (Join-Path $target "engines\$($component.Name)")) {
            if (Test-Path (Join-Path $dest '.filee-component')) { Remove-Item $dest -Recurse -Force }
            continue
        }
        $from = Join-Path $Engines $component.Name
        if (-not (Test-Path $from)) { continue }
        robocopy $from $dest /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy of $($component.Name) failed ($LASTEXITCODE)" }
        Set-Content -Path (Join-Path $dest '.filee-component') -Value $component.Value.sha256 -NoNewline -Encoding ascii
        Write-Host "Engine installed: $($component.Name)"
    }

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Mandarina.lnk'))
    $link.TargetPath = Join-Path $target 'Mandarina.exe'
    $link.WorkingDirectory = $target
    $link.Description = 'Mandarina: convert and tweak files by dragging them'
    $link.Save()

    if ($AddCliToPath) {
        $cli = Join-Path $target 'cli'
        $path = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($path -split ';') -notcontains $cli) {
            [Environment]::SetEnvironmentVariable('Path', ($path.TrimEnd(';') + ';' + $cli), 'User')
            Write-Host "Added to the user PATH: $cli (open a new terminal)"
        }
    }

    Start-Process (Join-Path $target 'Mandarina.exe')
    'RESULT: OK'
}
catch {
    "RESULT: ERROR $_"
}
finally {
    Stop-Transcript | Out-Null
}
