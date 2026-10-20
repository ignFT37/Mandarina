// Mandarina soak harness: runs one "variant" (a whole tool panel, a bare window, a picture on its own…) round after round in a
// real Avalonia/Win32 process and prints how the process's memory moves. Windows are placed off screen and never take focus,
// so it can run while somebody uses the PC.
//   Soak --variant panel-edit --photo foto.jpg --rounds 40 [--warmup 3] [--hold 1200] [--settle 300] [--every 5] [--gc]
//        [--taskbar] [--visible] [--tail seconds] [--files a|b|c] [--keep-peers] [--disconnect-uia] [--persistent]
// --keep-peers makes every closed window's accessibility peers stay referenced, like a UI Automation client that holds the
// elements it has read: what a closed panel costs then is what stays alive (see the managed figure after a forced GC).
// Variants: panel:<toolId> (any tool, e.g. panel:cropImage with --photo/--files), panel-edit, window-trivial, window-plain,
// window-image, magick-load, magick-only, bitmap-only, bitmap-write.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Automation.Peers;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Filee.App;
using Filee.App.Mandarina.Panels;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace Soak;

internal sealed class Options
{
    public string Variant { get; private set; } = "panel-edit";
    public string Photo { get; private set; } = "";
    public int Rounds { get; private set; } = 40;
    public int Warmup { get; private set; } = 3;
    public int Hold { get; private set; } = 1200;
    public int Settle { get; private set; } = 300;
    public int Every { get; private set; } = 5;
    public bool ForceGc { get; private set; }
    public bool Taskbar { get; private set; }
    public bool Visible { get; private set; }
    public int Tail { get; private set; }
    public bool KeepPeers { get; private set; }
    public bool DisconnectUia { get; private set; }
    public bool Persistent { get; private set; }
    public string[] Files { get; private set; } = [];

    /// <summary>The files a panel opens: --files a|b|c, or the single --photo.</summary>
    public string[] PanelFiles => Files.Length > 0 ? Files : [Photo];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => args[++i];
            switch (args[i])
            {
                case "--variant": o.Variant = Next(); break;
                case "--photo": o.Photo = Next(); break;
                case "--rounds": o.Rounds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--warmup": o.Warmup = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--hold": o.Hold = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--settle": o.Settle = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--every": o.Every = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--gc": o.ForceGc = true; break;
                case "--taskbar": o.Taskbar = true; break;
                case "--visible": o.Visible = true; break;
                case "--keep-peers": o.KeepPeers = true; break;
                case "--disconnect-uia": o.DisconnectUia = true; break;
                case "--persistent": o.Persistent = true; break;
                case "--tail": o.Tail = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--files": o.Files = Next().Split('|'); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return o;
    }
}

/// <summary>A panel that shows nothing: the window frame on its own.</summary>
[ToolPanel("soakEmpty")]
internal sealed class EmptyPanel(PanelContext context) : ToolPanel(context)
{
    public override Control CreateView() => new Border { Height = 200 };

    public override Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
                new Uri("fonts:Filee", UriKind.Absolute), new Uri("avares://Filee/Assets/Fonts", UriKind.Absolute))))
            .SetupWithoutStarting();

        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandarina", "soak-data");
        AppHost.Build(dataDirectory);
        var store = AppHost.Get<UserDataStore>();
        store.Load();
        var localizer = AppHost.Get<LocalizationService>();
        localizer.Attach(Application.Current!);
        store.Settings.Language = "es";
        localizer.SetLanguage("es");
        AppHost.Get<ThemeService>().Apply(store.Settings.Theme, "es");

        var cts = new CancellationTokenSource();
        var failed = false;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await new Soak(options).RunAsync();
            }
            catch (Exception ex)
            {
                failed = true;
                Console.Error.WriteLine(ex);
            }
            finally
            {
                cts.Cancel();
            }
        });
        Application.Current!.Run(cts.Token);
        return failed ? 1 : 0;
    }
}

internal sealed class Soak(Options o)
{
    private readonly ILocalizer _loc = AppHost.Get<ILocalizer>();
    private readonly IPlatformServices _platform = AppHost.Get<IPlatformServices>();
    private readonly ILogger _log = AppHost.Get<ILoggerFactory>().CreateLogger("soak");

    public async Task RunAsync()
    {
        Console.WriteLine($"variant={o.Variant} rounds={o.Rounds} hold={o.Hold} settle={o.Settle} gc={o.ForceGc} photo={o.Photo}");
        Console.WriteLine($"renderer: {RenderInfo()}");
        if (o.Persistent)
        {
            // A window that stays open all the time: a UI Automation client must still be able to read it after panels close.
            var persistent = new Window { Title = "persistent", Width = 400, Height = 200, Content = new StackPanel { Children = { new Button { Content = "OK" }, new TextBlock { Text = "still here" } } } };
            Place(persistent);
            persistent.Show();
        }
        for (var i = 0; i < o.Warmup; i++)
            await OneRoundAsync();
        await Settle();
        var baseline = Mem.Take(forceGc: true);
        Console.WriteLine("baseline: " + baseline);
        var previous = baseline;
        for (var round = 1; round <= o.Rounds; round++)
        {
            await OneRoundAsync();
            if (round % o.Every != 0 && round != o.Rounds)
                continue;
            await Settle();
            var now = Mem.Take(o.ForceGc);
            Console.WriteLine($"round {round,3}: {now}   (d/round vs. baseline: priv {(now.PrivateMb - baseline.PrivateMb) / round:+0.00;-0.00} MB, heapAlloc {(now.HeapAllocMb - baseline.HeapAllocMb) / round:+0.00;-0.00} MB, vmPriv {(now.VmPrivateMb - baseline.VmPrivateMb) / round:+0.00;-0.00} MB)");
            previous = now;
        }
        await Settle();
        var end = Mem.Take(forceGc: true);
        Console.WriteLine("end (after forced GC): " + end);
        Console.WriteLine($"TOTAL after {o.Rounds} rounds, forced GC: priv {end.PrivateMb - baseline.PrivateMb:+0.0;-0.0} MB ({(end.PrivateMb - baseline.PrivateMb) / o.Rounds:+0.00;-0.00} per round), " +
                          $"heapAlloc {end.HeapAllocMb - baseline.HeapAllocMb:+0.0;-0.0} MB, vmPriv {end.VmPrivateMb - baseline.VmPrivateMb:+0.0;-0.0} MB, managed {end.ManagedMb - baseline.ManagedMb:+0.0;-0.0} MB, " +
                          $"native(priv-gc) {(end.PrivateMb - end.GcCommittedMb) - (baseline.PrivateMb - baseline.GcCommittedMb):+0.0;-0.0} MB, gdi {end.Gdi - baseline.Gdi:+0;-0;0}, user {end.User - baseline.User:+0;-0;0}, handles {end.Handles - baseline.Handles:+0;-0;0}, threads {end.Threads - baseline.Threads:+0;-0;0}");
        Mem.PrintGrowth(baseline, end);
        Console.WriteLine($"windows still alive after the forced GC: {Windows.Count(w => w.IsAlive)} of {Windows.Count}");
        _ = previous;
        for (var waited = 0; waited < o.Tail; waited += 10)
        {
            await Task.Delay(10_000);
            var later = Mem.Take(forceGc: true);
            Console.WriteLine($"tail +{waited + 10,3}s (forced GC): {later}   (vs. baseline: priv {later.PrivateMb - baseline.PrivateMb:+0.0;-0.0} MB, managed {later.ManagedMb - baseline.ManagedMb:+0.0;-0.0} MB, gdi {later.Gdi - baseline.Gdi:+0;-0;0}, user {later.User - baseline.User:+0;-0;0})");
        }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string RenderInfo()
    {
        // Which graphics stack ended up loaded: ANGLE/Direct3D (GPU) or Skia's software path.
        var names = new[] { "libEGL.dll", "libGLESv2.dll", "d3d11.dll", "dxgi.dll", "d3dcompiler_47.dll", "opengl32.dll", "dcomp.dll", "Windows.UI.Composition.dll" };
        using var process = Process.GetCurrentProcess();
        var loaded = process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
        return string.Join(", ", names.Where(n => loaded.Contains(n, StringComparer.OrdinalIgnoreCase)));
    }

    private static readonly List<object> KeptPeers = [];
    private static readonly List<WeakReference> Windows = [];

    /// <summary>References the peers of the whole tree of <paramref name="window"/> for ever (a UI Automation client that does not let go).</summary>
    private static void KeepPeersOf(Window window)
    {
        var root = ControlAutomationPeer.CreatePeerForElement(window);
        KeptPeers.Add(root);
        var pending = new Stack<AutomationPeer>();
        pending.Push(root);
        while (pending.Count > 0)
            foreach (var child in pending.Pop().GetChildren())
            {
                KeptPeers.Add(child);
                pending.Push(child);
            }
    }

    private async Task ShowHoldCloseAsync(Window window)
    {
        Windows.Add(new WeakReference(window));
        Place(window);
        window.Show();
        await Task.Delay(o.Hold);
        if (o.KeepPeers)
            KeepPeersOf(window);
        window.Close();
        if (o.DisconnectUia)
        {
            await Task.Delay(150);
            await Task.Run(() => UiaDisconnectAllProviders());
        }
    }

    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaDisconnectAllProviders();

    private void Place(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = o.Taskbar;
        window.Topmost = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (!o.Visible)
            window.Position = new PixelPoint(-20000, -20000);
    }

    private async Task OneRoundAsync()
    {
        if (o.Variant.StartsWith("panel:", StringComparison.Ordinal))
        {
            var panel = PanelRegistry.Create(o.Variant["panel:".Length..], o.PanelFiles, _loc, _platform, _log);
            await ShowHoldCloseAsync(new ToolPanelWindow(panel));
            await Task.Delay(o.Settle);
            return;
        }
        switch (o.Variant)
        {
            case "panel-edit":
            {
                var panel = PanelRegistry.Create("editImage", [o.Photo], _loc, _platform, _log);
                await ShowHoldCloseAsync(new ToolPanelWindow(panel));
                break;
            }
            case "window-trivial":
            {
                await ShowHoldCloseAsync(new ToolPanelWindow(new EmptyPanel(new PanelContext([o.Photo], _loc, _platform))));
                break;
            }
            case "window-plain":
            {
                var window = new Window
                {
                    Width = 1056, Height = 676, CanResize = false, WindowDecorations = WindowDecorations.None,
                    Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                    Content = new Border { Margin = new Thickness(18), CornerRadius = new CornerRadius(26), Background = Brushes.Orange },
                };
                Place(window);
                window.Show();
                await Task.Delay(o.Hold);
                window.Close();
                break;
            }
            case "window-image":
            {
                var preview = await PanelImages.LoadAsync(o.Photo, 1200);
                var window = new Window
                {
                    Width = 1056, Height = 676, CanResize = false, WindowDecorations = WindowDecorations.None,
                    Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                    Content = new Image { Source = preview.Bitmap, Margin = new Thickness(18) },
                };
                Place(window);
                window.Show();
                await Task.Delay(o.Hold);
                window.Close();
                preview.Bitmap.Dispose();
                break;
            }
            case "magick-load":
            {
                var preview = await PanelImages.LoadAsync(o.Photo, 1200);
                preview.Bitmap.Dispose();
                break;
            }
            case "magick-only":
            {
                await Task.Run(() =>
                {
                    using var image = PanelImages.Read(o.Photo);
                    PanelImages.Fit(image, 1200);
                    using var copy = image.Clone();
                    using var again = image.Clone();
                });
                break;
            }
            case "bitmap-only":
            {
                using var stream = File.OpenRead(o.Photo);
                var bitmap = new Bitmap(stream);
                bitmap.Dispose();
                break;
            }
            case "bitmap-write":
            {
                var bitmap = new WriteableBitmap(new PixelSize(900, 600), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                using (var frame = bitmap.Lock())
                    Marshal.Copy(new byte[900 * 4], 0, frame.Address, 900 * 4);
                bitmap.Dispose();
                break;
            }
            default:
                throw new ArgumentException("Unknown variant " + o.Variant);
        }
        await Task.Delay(o.Settle);
    }
}

internal sealed record MemSnapshot(
    double PrivateMb, double WorkingSetMb, double ManagedMb, double GcCommittedMb, double HeapAllocMb, double HeapCommitMb,
    double VmPrivateMb, double VmMappedMb, double VmImageMb, int Handles, int Gdi, int User, int Threads,
    Dictionary<nint, (double Mb, uint Type)> Regions, HashSet<nint> Heaps)
{
    public override string ToString() =>
        $"priv {PrivateMb,6:0.0} ws {WorkingSetMb,6:0.0} | managed {ManagedMb,6:0.0} gcCommit {GcCommittedMb,6:0.0} | heaps alloc {HeapAllocMb,6:0.0} commit {HeapCommitMb,6:0.0} | vm private {VmPrivateMb,6:0.0} mapped {VmMappedMb,6:0.0} image {VmImageMb,6:0.0} | h {Handles} gdi {Gdi} user {User} thr {Threads}";
}

internal static class Mem
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HeapSummaryInfo
    {
        public uint Cb;
        public nuint Allocated, Committed, Reserved, MaxReserve;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessHeaps(uint count, [Out] nint[] heaps);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSummary(nint heap, uint flags, ref HeapSummaryInfo summary);

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation info, nuint length);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    private const double Mb = 1024.0 * 1024.0;
    private const uint MemCommit = 0x1000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;

    public static MemSnapshot Take(bool forceGc)
    {
        if (forceGc)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        using var process = Process.GetCurrentProcess();
        double heapAlloc = 0, heapCommit = 0;
        var heaps = new HashSet<nint>();
        var handles = new nint[256];
        var count = GetProcessHeaps((uint)handles.Length, handles);
        for (var i = 0; i < Math.Min(count, handles.Length); i++)
        {
            heaps.Add(handles[i]);
            var summary = new HeapSummaryInfo { Cb = (uint)Marshal.SizeOf<HeapSummaryInfo>() };
            if (HeapSummary(handles[i], 0, ref summary))
            {
                heapAlloc += summary.Allocated / Mb;
                heapCommit += summary.Committed / Mb;
            }
        }

        double vmPrivate = 0, vmMapped = 0, vmImage = 0;
        var regions = new Dictionary<nint, (double Mb, uint Type)>();
        nint address = 0;
        var size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQuery(address, out var info, size) != 0)
        {
            if (info.State == MemCommit)
            {
                var megabytes = info.RegionSize / Mb;
                switch (info.Type)
                {
                    case MemPrivate: vmPrivate += megabytes; break;
                    case MemMapped: vmMapped += megabytes; break;
                    case MemImage: vmImage += megabytes; break;
                }
                regions[info.AllocationBase] = (regions.GetValueOrDefault(info.AllocationBase).Mb + megabytes, info.Type);
            }
            var next = info.BaseAddress + (nint)info.RegionSize;
            if (next <= address)
                break;
            address = next;
        }

        var gc = GC.GetGCMemoryInfo();
        return new MemSnapshot(
            process.PrivateMemorySize64 / Mb, process.WorkingSet64 / Mb, GC.GetTotalMemory(false) / Mb, gc.TotalCommittedBytes / Mb,
            heapAlloc, heapCommit, vmPrivate, vmMapped, vmImage, process.HandleCount,
            (int)GetGuiResources(process.Handle, 0), (int)GetGuiResources(process.Handle, 1), process.Threads.Count, regions, heaps);
    }

    /// <summary>The allocation regions that grew the most between two snapshots (a heap segment, a mapped section…).</summary>
    public static void PrintGrowth(MemSnapshot before, MemSnapshot after)
    {
        var growth = after.Regions
            .Select(r => (Base: r.Key, Now: r.Value.Mb, Was: before.Regions.GetValueOrDefault(r.Key).Mb, r.Value.Type))
            .Where(r => r.Now - r.Was > 0.2)
            .OrderByDescending(r => r.Now - r.Was)
            .Take(14)
            .ToList();
        Console.WriteLine("regions that grew (allocation base: was -> now MB, type, heap?):");
        foreach (var r in growth)
        {
            var type = r.Type switch { MemPrivate => "private", MemMapped => "mapped", MemImage => "image", _ => "?" };
            var heap = after.Heaps.Contains(r.Base) ? "HEAP" : "";
            Console.WriteLine($"  0x{r.Base:X}: {r.Was:0.0} -> {r.Now:0.0} MB  {type} {heap}");
        }
        var newOnes = after.Regions.Count(r => !before.Regions.ContainsKey(r.Key));
        var gone = before.Regions.Count(r => !after.Regions.ContainsKey(r.Key));
        Console.WriteLine($"regions: {before.Regions.Count} -> {after.Regions.Count} ({newOnes} new, {gone} gone)");
    }
}
