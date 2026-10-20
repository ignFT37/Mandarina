// Mandarina: how the actions wheel's ring ends. An action that works shows the ✓; one that fails with something to tell (a file in a
// folder to share that another program holds) shows the "!" and says which file, instead of vanishing like a cancelled one.

using Avalonia.Headless.XUnit;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Settings;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

[Collection("Motion")]
public sealed class MandarinaActionsRingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-ring").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Closes the wheel and puts the shared view model back on the convert wheel: the tests that run after this one load it directly.</summary>
    private static void Restore(RadialController radial)
    {
        radial.Close(animate: false);
        AppHost.Get<RadialViewModel>().ResetForDrag();
    }

    /// <summary>Opens the actions wheel for <paramref name="files"/>, presses <paramref name="action"/> and runs the ring's clock until it ends.</summary>
    private static async Task<DonutProgressOutcome?> PressAsync(RadialController radial, string action, string[] files)
    {
        radial.ShowForFiles(400, 400, files, WheelMode.Actions);
        var donut = radial.Window!.DonutControl;
        var entries = AppHost.Get<RadialViewModel>().Entries;
        radial.Invoke(entries.Select((entry, index) => (entry, index)).First(pair => pair.entry.Id == action).index, -1, files);
        await WaitAsync(() =>
        {
            donut.Advance(1 / 60.0);
            return donut.ShownOutcome is not null;
        });
        return donut.ShownOutcome;
    }

    [AvaloniaFact]
    public async Task A_share_that_cannot_read_a_file_ends_the_ring_with_the_failure_and_says_which_file()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Informes")).FullName;
        var locked = Path.Combine(folder, "Informe final.docx");
        File.WriteAllText(locked, "abierto");
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);   // another program, exclusive
        var radial = AppHost.Get<RadialController>();
        var told = new List<string>();
        radial.ShowProblem = text =>
        {
            told.Add(text);
            return Task.CompletedTask;
        };
        try
        {
            var outcome = await PressAsync(radial, "share", [folder]);

            Assert.Equal(DonutProgressOutcome.Failed, outcome);
            var message = Assert.Single(told);
            Assert.Contains("Informe final.docx", message);
            Assert.Contains("Informes", message);
            Assert.DoesNotContain("{0}", message);                       // the placeholders were filled
            Assert.True(File.Exists(locked));                            // nothing of the user's was touched
        }
        finally
        {
            radial.ShowProblem = null;
            Restore(radial);
        }
    }

    [AvaloniaFact]
    public async Task An_action_that_just_does_nothing_ends_the_ring_without_a_failure_or_a_message()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var told = new List<string>();
        radial.ShowProblem = text =>
        {
            told.Add(text);
            return Task.CompletedTask;
        };
        try
        {
            // Share of a folder that is gone by the time the action runs: nothing to compress, nothing to open.
            var gone = Path.Combine(_root, "ya no está");
            var outcome = await PressAsync(radial, "share", [gone]);

            Assert.Equal(DonutProgressOutcome.Cancelled, outcome);
            Assert.Empty(told);
        }
        finally
        {
            radial.ShowProblem = null;
            Restore(radial);
        }
    }
}
