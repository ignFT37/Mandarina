// Mandarina: the Share action opens Windows' share sheet through DataTransferManager, called from Windows PowerShell 5.1. The sheet is a real
// system window on the user's screen, so the suite only checks what can be checked without showing it (the script is valid PowerShell, folders
// and missing files are not offered to the sheet itself: the action turns folders into ZIPs before, see MandarinaShareStagingTests); opening
// it for real, with a photo and a folder, is opt-in (MANDARINA_UI_TESTS=1) and closes the sheet again by UI Automation.

using System.Diagnostics;
using System.Text;
using Filee.App.Mandarina;
using Filee.Core.Localization;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaShareTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-share").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string? WindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Runs <paramref name="script"/> in Windows PowerShell (as an encoded command) and returns what it printed.</summary>
    private static async Task<string> RunAsync(string script, TimeSpan timeout, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo(WindowsPowerShell()!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-InputFormat", "None", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        using var cancel = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancel.Token);
        return await output;
    }

    [Fact]
    public async Task The_script_is_valid_powershell()
    {
        if (WindowsPowerShell() is null)
        {
            Assert.Skip("Windows PowerShell 5.1 is not here.");
            return;
        }

        // The real parser, on the script exactly as the action sends it: a typo in it would only show when somebody shares something.
        var answer = await RunAsync(
            "$tokens = $null; $errors = $null; [void][System.Management.Automation.Language.Parser]::ParseInput($env:MANDARINA_SCRIPT, [ref]$tokens, [ref]$errors); " +
            "'errors: ' + $errors.Count; $errors | ForEach-Object { $_.Message + ' @' + $_.Extent.StartLineNumber }",
            TimeSpan.FromSeconds(60), ("MANDARINA_SCRIPT", MandarinaShare.Script));

        Assert.Contains("errors: 0", answer);
    }

    [Fact]
    public void Only_files_are_offered_to_the_sheet()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The share sheet is Windows'.");
            return;
        }
        var folder = Directory.CreateDirectory(Path.Combine(_root, "carpeta")).FullName;

        Assert.False(MandarinaShare.Start([]));
        Assert.False(MandarinaShare.Start([folder]));                              // the sheet takes no folders
        Assert.False(MandarinaShare.Start([Path.Combine(_root, "no existe.png")])); // and nothing that is not there
    }

    private sealed class Texts : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key + ": " + string.Join(", ", args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task The_sheet_opens_for_a_photo_and_a_folder_and_closes_again_and_the_zip_goes()
    {
        if (Environment.GetEnvironmentVariable("MANDARINA_UI_TESTS") != "1" || WindowsPowerShell() is null)
        {
            Assert.Skip("Opens Windows' share sheet on the screen: set MANDARINA_UI_TESTS=1 to run it.");
            return;
        }
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"), 600, 400);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Vacaciones de verano")).FullName;
        MakePhoto(Path.Combine(folder, "playa.jpg"), 300, 200);
        File.WriteAllText(Path.Combine(folder, "notas.txt"), "sol y arena");
        Directory.CreateDirectory(Path.Combine(folder, "vacía"));
        var staging = Path.Combine(_root, "staging");
        // The whole action, with the real platform: the folder is zipped, the sheet opens for the photo and the ZIP, and the ZIP goes
        // as soon as the sheet is closed (no wait, in the test).
        var actions = new FileActions(new WindowsPlatformServices(), new Texts(), NullLogger<FileActions>.Instance) { ShareCleanupDelay = TimeSpan.Zero, StagingRoot = staging };

        var result = await actions.ShareAsync(0, [photo, folder], null, CancellationToken.None);

        Assert.True(result.Succeeded, "the sheet did not open: " + result.Problem);
        var zip = Assert.Single(Directory.GetFiles(staging, "*.zip", SearchOption.AllDirectories));   // there while the sheet is open
        Assert.Equal("Vacaciones de verano.zip", Path.GetFileName(zip));
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            Assert.Equal(["Vacaciones de verano/notas.txt", "Vacaciones de verano/playa.jpg", "Vacaciones de verano/vacía/"], archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));

        // The sheet is a window of its own: find it with UI Automation and press its Close button.
        var closed = await RunAsync("""
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $AE = [Windows.Automation.AutomationElement]
            for ($i = 0; $i -lt 40; $i++) {
                foreach ($frame in $AE::RootElement.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'ApplicationFrameWindow')))) {
                    $close = $frame.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'SharePickerCloseButton')))
                    if ($close) { $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); 'closed'; exit 0 }
                }
                Start-Sleep -Milliseconds 250
            }
            'not found'
            """, TimeSpan.FromSeconds(60));

        Assert.Contains("closed", closed);
        // The helper ends a few seconds after the sheet closes, and the ZIP is deleted then; the photo and the folder are not touched.
        var until = DateTime.UtcNow.AddSeconds(40);
        while (File.Exists(zip) && DateTime.UtcNow < until)
            await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(zip), "the ZIP was still there 40 s after the sheet closed");
        Assert.True(File.Exists(photo) && File.Exists(Path.Combine(folder, "playa.jpg")) && File.Exists(Path.Combine(folder, "notas.txt")));
    }
}
