# Publishes Mandarina (the app and the `mandarina` command) into one folder, ready for install-mandarina.ps1.
#
#   pwsh tools/mandarina/publish-mandarina.ps1 [-Output <folder>]
#
# Needs the .NET 10 SDK and the two engines that ship inside the program, rhwp and 7-Zip. They are taken from the checkout's
# engines/ folder or from %USERPROFILE%\.mandarina\engines; fetch them once with:  pwsh build/fetch-engines.ps1 -Only rhwp,7zip
# The result is a self-contained win-x64 build: Mandarina's launcher is Filee.exe here and install-mandarina.ps1 renames it.
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $env:USERPROFILE '.mandarina\publish\win-x64')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# The app and the command line go to the same folder, so they share the runtime.
$options = @('-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishReadyToRun=true', '-p:FileeRelease=true', '-o', $Output, '-nologo', '-v', 'q')
foreach ($project in 'src\Filee.App\Filee.App.csproj', 'src\Filee.Cli\Filee.Cli.csproj') {
    dotnet publish (Join-Path $repo $project) @options
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

# Engines that ship inside the program.
foreach ($id in 'rhwp', '7zip') {
    $from = @((Join-Path $repo "engines\$id"), (Join-Path $env:USERPROFILE ".mandarina\engines\$id")) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $from) { throw "Engine '$id' not found. Run: pwsh build/fetch-engines.ps1 -Only rhwp,7zip" }
    robocopy $from (Join-Path $Output "engines\$id") /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy of $id failed ($LASTEXITCODE)" }
}

# The command line tool as `mandarina` (the executable keeps Filee's name; the .cmd is the entry point).
$cli = Join-Path $Output 'cli'
New-Item -ItemType Directory -Force $cli | Out-Null
Set-Content -Path (Join-Path $cli 'mandarina.cmd') -Value '@"%~dp0..\filee-cli.exe" %*' -Encoding ascii

Write-Host "Published to $Output"
