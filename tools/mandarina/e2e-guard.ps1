# Guard for the e2e scripts that send real mouse and keyboard input: dot-source it (". .\e2e-guard.ps1") and call Assert-UserAway before
# a batch and Mark-Synthetic after it. The input a script injects also counts for GetLastInputInfo, so "the user is away" is
# decided against the moment of the script's own last input: anything newer than that is the user's, and the batch is refused
# until the PC has been idle for 60 s (then the guard starts again from there). Without a mark yet the PC must have been idle for 60 s.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Guard {
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  public static double IdleSeconds() { var i = new LASTINPUTINFO(); i.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)); GetLastInputInfo(ref i); return (Environment.TickCount - (int)i.dwTime) / 1000.0; }
  public static int Tick() { return Environment.TickCount; }
}
"@
$script:GuardMark = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'last-synthetic.txt'

function Assert-UserAway {
    $idle = [Guard]::IdleSeconds()
    if (Test-Path $script:GuardMark) {
        $mine = ([Guard]::Tick() - [int](Get-Content $script:GuardMark -Raw)) / 1000.0
        # Nothing newer than this script's own last input: the PC is still its own.
        if ($idle -ge $mine - 3) { return }
        # Someone touched the PC after it: wait for 60 s of calm, then start again from there.
        if ($idle -lt 60) { Write-Host ("REFUSED: someone used the PC {0:N0} s ago, after this script's own last input {1:N0} s ago" -f $idle, $mine); exit 3 }
        Remove-Item $script:GuardMark
        return
    }
    if ($idle -lt 60) { Write-Host ("REFUSED: the PC was used {0:N0} s ago (needs 60 s idle before the first batch)" -f $idle); exit 3 }
}

function Mark-Synthetic { Set-Content -Path $script:GuardMark -Value ([Guard]::Tick()) -NoNewline }
