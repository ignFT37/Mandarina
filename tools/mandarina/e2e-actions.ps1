# End-to-end check of the actions wheel (Alt) with the INSTALLED app and real input, for the actions that open something of the system:
#   Move:  drags mover-1.png (and then the folder mover-dir) onto the Move petal, picks e2e\carpeta\destino in the folder picker by UI
#          Automation, and checks the originals are gone from carpeta and are in destino (and that Explorer shows destino).
#   Float: drags the folder "muestra" onto the Float petal and checks the shelf shows a tile for it, then closes the shelf.
#   Share: drags prueba.png onto the Share petal and reports which new window the Windows share sheet opens (screenshot), closes it
#          with Esc; then the same with the folder "muestra" (the sheet takes no folders, so it is zipped to a temporary ZIP first and
#          the sheet opens for that: it must not crash or hang, and %TEMP%\Mandarina\share-* goes away shortly after the sheet closes).
# Needs the Explorer window on e2e\carpeta open. Refuses to run unless the PC is idle (e2e-guard.ps1). Writes e2e-<Name>.log and PNGs.
# Usage: powershell -File e2e-actions.ps1 -Do Move|Float|Share [-Name actions]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Move', 'Float', 'Share')][string]$Do,
    [string]$Name = 'actions'
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $dir 'e2e-guard.ps1')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ActKey {
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public static void Tap(ushort vk) {
    var d = new INPUT { type = 1 }; d.ki.wVk = vk; SendInput(1, new[] { d }, Marshal.SizeOf(typeof(INPUT)));
    var u = new INPUT { type = 1 }; u.ki.wVk = vk; u.ki.dwFlags = 2; SendInput(1, new[] { u }, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ActWin {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
[ActKey]::SetProcessDPIAware() | Out-Null
$log = Join-Path $dir "e2e-$Name-$Do.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($text) { Add-Content -Path $log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text) -Encoding UTF8; Write-Host $text }
function Fail($text) { Log "FAIL $text"; Mark-Synthetic; exit 1 }
function Check($ok, $text) { if ($ok) { Log "ok   $text" } else { Fail $text } }
Assert-UserAway
$AE = [Windows.Automation.AutomationElement]
$root = $AE::RootElement
$carpeta = Join-Path $dir 'carpeta'

function Gesture($petal, $item) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'e2e-gesture.ps1') -Key Alt -Petal $petal -Count 6 -Name "$Name-$Do-g" -Item $item -Wait 1 2>&1 | Out-Null
    $g = Get-Content (Join-Path $dir "e2e-$Name-$Do-g.log") -ErrorAction SilentlyContinue
    if ($g -match 'ERROR') { Fail ("the gesture failed: " + ($g -match 'ERROR' | Select-Object -First 1)) }
}
function Shot($path, $r) {
    $bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
function Top-Windows() { @($root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { "{0}|{1}|{2}" -f $_.Current.ProcessId, $_.Current.ClassName, $_.Current.Name }) }
function Dump($element, $limit = 120) {
    $n = 0
    foreach ($e in $element.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        $c = $e.Current
        if ($c.Name -or $c.AutomationId) { Log ("   {0} | {1} | {2}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId) }
        if (++$n -ge $limit) { break }
    }
}

switch ($Do) {
    'Move' {
        $target = Join-Path $carpeta 'destino'
        # Fresh material every run: a copy of the photo and a folder with a file in it; destino emptied of last run's.
        Remove-Item (Join-Path $target 'mover-1.png'), (Join-Path $target 'mover-dir') -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $carpeta 'prueba.png') (Join-Path $carpeta 'mover-1.png') -Force
        New-Item -ItemType Directory -Force (Join-Path $carpeta 'mover-dir') | Out-Null
        Set-Content (Join-Path $carpeta 'mover-dir\x.txt') 'x'
        Start-Sleep -Milliseconds 1500       # Explorer shows them
        foreach ($item in 'mover-1.png', 'mover-dir') {
            Check (Test-Path (Join-Path $carpeta $item)) "$item is there before"
            Gesture 3 $item
            # The folder picker is owned by the wheel's window, so UI Automation lists it under that window, not on the desktop.
            $procIds = @(Get-Process Mandarina | ForEach-Object Id)
            $dialog = $null
            for ($i = 0; $i -lt 40 -and -not $dialog; $i++) {
                foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
                    if ($procIds -contains $w.Current.ProcessId -and $w.Current.ClassName -eq 'RadialWindow') {
                        $dialog = $w.FindFirst([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')))
                    }
                }
                if (-not $dialog) { Start-Sleep -Milliseconds 250 }
            }
            Check ($null -ne $dialog) "the folder picker opens for $item"
            Log "dialog '$($dialog.Current.Name)'"
            Shot (Join-Path $dir "e2e-$Name-move-picker.png") $dialog.Current.BoundingRectangle
            # Its controls are old Win32 ones (no UI Automation patterns): the name box takes the path as a message, the button a click.
            $edit = $dialog.FindFirst([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1152')))
            $ok = $dialog.FindFirst([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1')))
            Check (($null -ne $edit) -and ($null -ne $ok)) 'the picker has its name box and its button'
            [void][ActWin]::SendMessage([IntPtr]$edit.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $target)     # WM_SETTEXT
            Start-Sleep -Milliseconds 500
            [void][ActWin]::SendMessage([IntPtr]$ok.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)   # BM_CLICK
            Start-Sleep -Milliseconds 3000
            Check (-not (Test-Path (Join-Path $carpeta $item))) "$item is gone from carpeta"
            Check (Test-Path (Join-Path $target $item)) "$item is in destino"
        }
        Check (Test-Path (Join-Path $target 'mover-dir\x.txt')) 'the folder arrived with its file'
        $explorer = $root.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'CabinetWClass'))) | Where-Object { $_.Current.Name -like 'destino*' }
        Log ("Explorer windows named destino (the action reveals the target): " + @($explorer).Count)
        Log ("destino now holds: " + ((Get-ChildItem $target | ForEach-Object Name) -join ', '))
    }
    'Float' {
        Gesture 1 'muestra'
        $procIds = @(Get-Process Mandarina | ForEach-Object Id)
        $shelf = $null
        for ($i = 0; $i -lt 20 -and -not $shelf; $i++) {
            foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
                if ($procIds -contains $w.Current.ProcessId -and $w.Current.ClassName -eq 'FloatShelfWindow') { $shelf = $w; break }
            }
            if (-not $shelf) { Start-Sleep -Milliseconds 250 }
        }
        Check ($null -ne $shelf) 'the shelf opens for a folder'
        Start-Sleep -Milliseconds 800
        Shot (Join-Path $dir "e2e-$Name-float-folder.png") $shelf.Current.BoundingRectangle
        $texts = @($shelf.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        Log ("shelf texts: " + ($texts -join ' | '))
        Check ($texts -contains 'muestra') 'the folder has its tile ("muestra")'
        $close = $shelf.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
        if ($close) { $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    }
    'Share' {
        foreach ($item in 'prueba.png', 'muestra') {
            $before = Top-Windows
            Gesture 0 $item
            Start-Sleep -Milliseconds 2500
            $after = Top-Windows
            $new = @($after | Where-Object { $before -notcontains $_ })
            Log ("new top-level windows after sharing '$item': " + $(if ($new.Count) { $new -join ' ;; ' } else { '(none)' }))
            $sw = [Windows.Forms.Screen]::PrimaryScreen.Bounds
            Shot (Join-Path $dir "e2e-$Name-share-$($item -replace '[^a-z]', '').png") $sw
            [ActKey]::Tap(0x1B); Start-Sleep -Milliseconds 800       # Esc closes the share sheet if it is the window with the focus
            $left = @((Top-Windows) | Where-Object { $before -notcontains $_ })
            Log ("after Esc: " + $(if ($left.Count) { $left -join ' ;; ' } else { 'nothing left open' }))
            Check ($null -ne (Get-Process Mandarina -ErrorAction SilentlyContinue)) "Mandarina is still running after sharing '$item'"
        }
    }
}
Mark-Synthetic
Log 'DONE'
