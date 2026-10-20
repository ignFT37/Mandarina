// Mandarina: what a closed tool panel leaves behind. A panel that stays reachable after its window closed keeps its controls,
// its pictures and its timers, once more every time it is opened. The checks: every panel (and its window) can be collected,
// opening and closing the photo editor again and again does not make the managed memory grow, and closing a window gives
// back what a UI Automation client may hold (AccessibilityRelease). The process's real memory, native included, is the job
// of tools/mandarina/soak (a real Win32 window, off screen); here the windows are headless.

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaMemoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-memory").FullName;

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

    /// <summary>Every tool with a panel and the kind of file it opens with: photo, photos, audio, video, videos or pdf.</summary>
    public static TheoryData<string, string> Panels => new()
    {
        { "annotateImage", "photo" }, { "cropImage", "photo" }, { "editImage", "photo" }, { "frameImage", "photo" },
        { "redactImage", "photo" }, { "compress", "photo" }, { "removeMetadata", "photo" }, { "createCollage", "photos" },
        { "trimAudio", "audio" }, { "audioChannels", "audio" }, { "redactAudio", "audio" }, { "audioToVideo", "audio" },
        { "trimVideo", "video" }, { "cropVideo", "video" }, { "changeVideoSpeed", "video" }, { "splitVideo", "video" },
        { "videoSnapshots", "video" }, { "redactVideo", "video" }, { "joinVideos", "videos" }, { "organizePDF", "pdf" },
        { "transcribe", "audio" }, { "subtitles", "audio" }, // closed while they load the model's devices (and, the first, transcribe)
    };

    [Fact]
    public void The_list_above_covers_every_panel_of_the_build()
    {
        // A new panel must be added to Panels: its window has to be collectible as well.
        var covered = Panels.Select(row => row.Data.Item1).ToHashSet();
        Assert.DoesNotContain(PanelRegistry.Ids, id => !covered.Contains(id));
    }

    private async Task<string[]?> MakeInputAsync(string kind)
    {
        switch (kind)
        {
            case "photo":
                return [MakePhoto(Path.Combine(_root, "foto.jpg"), 1200, 800)];
            case "photos":
                return [MakePhoto(Path.Combine(_root, "a.jpg"), 900, 600), MakePhoto(Path.Combine(_root, "b.png"), 600, 900),
                    MakePhoto(Path.Combine(_root, "c.jpg"), 800, 800)];
            case "pdf":
                return [MakePdf(Path.Combine(_root, "doc.pdf"), 3)];
            case "audio":
            {
                var audio = await MakeMediaAsync(Path.Combine(_root, "tono.wav"), "-f", "lavfi", "-i", "sine=frequency=440:duration=4");
                return audio is null ? null : [audio];
            }
            default:
            {
                string[] clip = ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                    "-shortest", "-pix_fmt", "yuv420p"];
                var first = await MakeMediaAsync(Path.Combine(_root, "uno.mp4"), clip);
                if (first is null)
                    return null;
                return kind == "videos" ? [first, (await MakeMediaAsync(Path.Combine(_root, "dos.mp4"), clip))!] : [first];
            }
        }
    }

    /// <summary>Runs the dispatcher for a moment so the panel's background loads start (and mostly finish).</summary>
    private static async Task LetItLoadAsync(int milliseconds = 700)
    {
        for (var waited = 0; waited < milliseconds; waited += 30)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
        Pump();
    }

    /// <summary>Opens the panel in its window, closes it, and returns weak references: nothing here keeps them alive.</summary>
    private static async Task<(WeakReference Window, WeakReference Panel)> OpenAndCloseAsync(string id, string[] files, int loadMilliseconds = 700)
    {
        var context = Context(files);
        var panel = PanelRegistry.Create(id, files, context.Loc, context.Platform);
        var window = new ToolPanelWindow(panel);
        window.Show();
        await LetItLoadAsync(loadMilliseconds);
        window.Close();
        Pump();
        return (new WeakReference(window), new WeakReference(panel));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Panels))]
    public async Task A_closed_panel_and_its_window_can_be_collected(string id, string kind)
    {
        var files = await MakeInputAsync(kind);
        if (files is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests");
            return;
        }
        var (window, panel) = await OpenAndCloseAsync(id, files);

        for (var round = 0; round < 25 && (window.IsAlive || panel.IsAlive); round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(120); // the timers and loads the window left behind run out
            Pump();
        }

        Assert.False(window.IsAlive, $"something still holds the closed {id} window");
        Assert.False(panel.IsAlive, $"something still holds the closed {id} panel");
    }

    private static long LiveBytes()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [AvaloniaFact]
    public async Task Opening_and_closing_the_photo_editor_again_and_again_keeps_no_memory()
    {
        var photo = new[] { MakePhoto(Path.Combine(_root, "grande.jpg"), 1600, 1067) };
        for (var i = 0; i < 3; i++)
            await OpenAndCloseAsync("editImage", photo, 400); // JIT, fonts, style caches: the first rounds grow whatever happens
        var before = LiveBytes();

        for (var i = 0; i < 12; i++)
            await OpenAndCloseAsync("editImage", photo, 400);

        // One panel left alive is ~2 MB of controls plus its pictures: twelve would be over 24 MB. (Other tests of the run, on other
        // threads, come and go: the limit leaves room for them.)
        var growth = LiveBytes() - before;
        Assert.True(growth < 10_000_000, $"12 openings left {growth / 1e6:0.0} MB of managed memory behind");
    }

    [AvaloniaFact]
    public async Task Closing_a_panel_window_drops_what_a_UI_Automation_client_holds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UI Automation is a Windows API");
        // A window another test closed a moment ago has its release pending (250 ms): let it run before counting, or it counts here.
        await Task.Delay(500);
        Pump();
        var calls = 0;
        var previous = AccessibilityRelease.Disconnect;
        AccessibilityRelease.Disconnect = () => Interlocked.Increment(ref calls);
        try
        {
            var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
            var window = new ToolPanelWindow(new EditImagePanel(Context(photo)));
            window.Show();
            Pump();
            Assert.Equal(0, Volatile.Read(ref calls)); // not while it is open
            window.Close();

            await WaitAsync(() => Volatile.Read(ref calls) > 0, 5000);
            Assert.Equal(1, Volatile.Read(ref calls)); // once per window, after its last events
        }
        finally
        {
            AccessibilityRelease.Disconnect = previous;
        }
    }
}
