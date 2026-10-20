// Mandarina: Windows' share sheet (Nearby sharing, Phone Link, mail, Teams…) for the Share action of the actions wheel.
//
// The sheet is opened the way an app opens it: DataTransferManager through IDataTransferManagerInterop, handing over the files as
// StorageFiles when the sheet asks for them. This project is plain net10.0 without the WinRT projection, so Windows PowerShell 5.1
// (which has it) does the calling, as LocalDetector does for the text and face detectors: the script goes over the command line
// (-EncodedCommand, the files in an environment variable). The script stays alive while the sheet is on screen, because the sheet asks
// for the files again when a target is chosen, and ends a moment after the sheet closes.
//
// What does NOT work, tried on Windows 11 (build 26200): invoking the "Windows.ModernShare" verb of the files' classic context menu
// (IContextMenu.InvokeCommand answers ERROR_NO_ASSOCIATION by verb and ERROR_INVALID_WINDOW_HANDLE by command id, with or without a
// site), which is what this action used to do: the sheet never opened.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Filee.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class MandarinaShare
{
    /// <summary>How long the script may take to tell that the sheet is open (Windows PowerShell starts in about a second).</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Opens the share sheet for the files among <paramref name="paths"/> (the sheet takes files, not folders: the Share action zips
    /// folders before it gets here). True when it is open; false when there is nothing to share or Windows would not show it. Blocks until
    /// then (a few seconds): call it off the UI thread. The script goes on in the background until the sheet is closed, and then
    /// <paramref name="sheetClosed"/> runs once (on a pool thread; never when this returns false): the files are no longer needed.
    /// </summary>
    public static bool Start(IReadOnlyList<string> paths, Action? sheetClosed = null)
    {
        var files = paths.Select(path => path.TrimEnd('\\', '/')).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return false;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return false;

        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-InputFormat", "None", "-STA", "-EncodedCommand", EncodedScript })
            start.ArgumentList.Add(argument);
        start.Environment["MANDARINA_SHARE_FILES"] = string.Join('\n', files);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
                return false;
            string? answer;
            try
            {
                answer = process.StandardOutput.ReadLineAsync().WaitAsync(OpenTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            if (answer?.TrimStart('﻿') != "OK")
                return false;
            if (sheetClosed is not null)
            {
                WatchUntilClosed(process, sheetClosed);
                process = null; // the watcher owns it now
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>The script ends a moment after the sheet closes (or after ten minutes): that is when the files it was given are free.</summary>
    private static void WatchUntilClosed(Process process, Action sheetClosed) => _ = Task.Run(async () =>
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // the process is gone or cannot be waited on: either way it is not running
        }
        finally
        {
            process.Dispose();
        }
        try
        {
            sheetClosed();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Share: cleaning up after the sheet failed: {ex}");
        }
    });

    private static string EncodedScript { get; } = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    /// <summary>
    /// The PowerShell that shows the sheet. Input: MANDARINA_SHARE_FILES (one path per line). It prints "OK" once the sheet has been
    /// asked for, or "ERR …" when it could not, and then waits for the sheet to close.
    /// </summary>
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        function Say([string]$text) { [Console]::Out.WriteLine($text); [Console]::Out.Flush() }
        try {
            $paths = @($env:MANDARINA_SHARE_FILES -split "`n" | Where-Object { $_ })
            Add-Type -AssemblyName System.Runtime.WindowsRuntime, System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
            Add-Type -TypeDefinition @"
        using System;
        using System.Runtime.InteropServices;
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow(IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }
        public static class MandarinaShareInterop
        {
            public static IntPtr GetForWindow(object factory, IntPtr window, Guid iid) { return ((IDataTransferManagerInterop)factory).GetForWindow(window, ref iid); }
            public static void Show(object factory, IntPtr window) { ((IDataTransferManagerInterop)factory).ShowShareUIForWindow(window); }
        }
        "@
            $null = [Windows.ApplicationModel.DataTransfer.DataTransferManager, Windows.ApplicationModel.DataTransfer, ContentType = WindowsRuntime]
            $null = [Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs, Windows.ApplicationModel.DataTransfer, ContentType = WindowsRuntime]
            $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]

            # WinRT asynchronous operations as .NET tasks, waited on here.
            $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
            } | Select-Object -First 1
            function Await($operation, [Type]$resultType) {
                $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
                if (-not $task.Wait(30000)) { throw 'timeout' }
                $task.Result
            }
            $items = New-Object 'System.Collections.Generic.List[Windows.Storage.IStorageItem]'
            foreach ($path in $paths) { $items.Add((Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile]))) }
            $title = if ($items.Count -eq 1) { $items[0].Name } else { 'Mandarina' }

            # The sheet belongs to a window of this process: a borderless 1 x 1 px one next to the pointer, which is where the wheel was.
            $form = New-Object Windows.Forms.Form
            $form.FormBorderStyle = 'None'
            $form.ShowInTaskbar = $false
            $form.StartPosition = 'Manual'
            $form.TopMost = $true
            $form.Size = New-Object Drawing.Size(1, 1)
            $form.Location = [Windows.Forms.Cursor]::Position
            $form.Show()
            [Windows.Forms.Application]::DoEvents()

            $factory = [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal]::GetActivationFactory([Windows.ApplicationModel.DataTransfer.DataTransferManager])
            $pointer = [MandarinaShareInterop]::GetForWindow($factory, $form.Handle, [Guid]'a5caee9b-8708-49d1-8d36-67d25a8da00c')
            $manager = [System.Runtime.InteropServices.Marshal]::GetObjectForIUnknown($pointer)
            $handler = [Windows.Foundation.TypedEventHandler[Windows.ApplicationModel.DataTransfer.DataTransferManager, Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs]] {
                param($sender, $request)
                $data = $request.Request.Data
                $data.Properties.Title = $title
                $data.SetStorageItems($items)
            }
            $manager.add_DataRequested($handler) | Out-Null
            [MandarinaShareInterop]::Show($factory, $form.Handle)
            Say 'OK'

            # The sheet is a window of its own: stay while it is open (it asks for the files again when a target is chosen).
            $AE = [Windows.Automation.AutomationElement]
            function Sheet-Open {
                foreach ($frame in $AE::RootElement.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'ApplicationFrameWindow')))) {
                    if ($frame.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'SharePickerCloseButton')))) { return $true }
                }
                return $false
            }
            $started = Get-Date
            $seen = $false
            while ((Get-Date) -lt $started.AddMinutes(10)) {
                [Windows.Forms.Application]::DoEvents()
                if (Sheet-Open) { $seen = $true }
                elseif ($seen -or (Get-Date) -gt $started.AddSeconds(10)) { break }
                Start-Sleep -Milliseconds 300
            }
            $grace = (Get-Date).AddSeconds(3)
            while ((Get-Date) -lt $grace) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
            $form.Close()
        } catch {
            Say ('ERR ' + $_.Exception.Message)
            exit 1
        }
        """;
}
