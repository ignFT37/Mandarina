// Renders the demo animations of the "What's new" slides (src/Filee.App/Assets/WhatsNew/<version>/*.webp) with the
// headless app: real windows, real donut animations, frame by frame, encoded as compact animated WebP by Magick.NET.
// The files it writes are product assets; run it again when the UI they show changes. It is explicit, so a normal test
// run skips it:
//
//   dotnet build tests/Filee.App.Tests
//   tests/Filee.App.Tests/bin/Debug/net10.0/Filee.App.Tests.exe -method "Filee.App.Tests.WhatsNewMedia.*" -explicit only
//
// Each clip is drawn on a 768 × 432 canvas (16:9, shown at 496 × 279 in the window, sharp up to 150 % scaling) in the
// light theme. The first frame of each clip is what reduced motion shows, so every clip starts on a meaningful picture.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using ImageMagick.Drawing;

namespace Filee.App.Tests;

[Collection("Motion")]
public class WhatsNewMedia
{
    internal const int CanvasWidth = 768;
    internal const int CanvasHeight = 432;
    private const double Frame = 1 / 25.0;

    /// <summary>The light theme's PageBrush: the canvas behind every clip.</summary>
    private const string Page = "#F6F5F1";

    /// <summary>Where the 2.0.0 slides live in the repository.</summary>
    internal static string Folder(string version) =>
        Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "WhatsNew", version);

    internal static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    [AvaloniaFact(Explicit = true)]
    public void Generate_2_0_0()
    {
        TestServices.EnsureInitialized("en");
        var folder = Folder("2.0.0");
        Directory.CreateDirectory(folder);
        var theme = AppHost.Get<ThemeService>();
        // Animations on whatever this machine prefers: every theme applied here says ReduceMotion = false, since
        // ThemeService.Apply reads the setting again.
        Motion.Set(true);
        // Times in the history as an English reader expects them, whatever the machine's region.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, "en");
            // browser.webp and flip.webp come from the demo-video generator (branch media/demo-videos: a scripted desktop
            // with a browser and a file manager). These headless versions are written only when asked for by name
            // (FILEE_WHATSNEW_ONLY=browser,flip).
            Write(folder, "browser.webp", BrowserDrop, onRequestOnly: true);
            Write(folder, "flip.webp", DonutFlip, onRequestOnly: true);
            Write(folder, "formats.webp", FormatDonuts);
            Write(folder, "crop.webp", Crop);
            Write(folder, "theme.webp", Softness);
            Write(folder, "languages.webp", () => Languages("2.0.0"));
            Write(folder, "more.webp", () => More("2.0.0"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            theme.Apply(new ThemeSettings(), "en");
            TestServices.EnsureInitialized("en");
        }
    }

    // ───────────────────────── Scenes ─────────────────────────

    /// <summary>
    /// A picture dropped from a browser (#261): the donut for it, "Drop → JPG", then the ring with the download badge
    /// above it while the picture is saved, then the check. The badge shows what JobViewModel would write; the real
    /// drag from a browser can't be made headless.
    /// </summary>
    private static Clip BrowserDrop()
    {
        var clip = new Clip();
        // Where a browser picture stands before it is saved (BrowserDrags: placeholder paths named like the file).
        var (window, vm) = OpenDonut([Path.Combine(Path.GetTempPath(), "Filee", "web", "drop", "1", "sunset.jpg")], clickMode: false);
        var donut = window.DonutControl;
        var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DownloadBadge");
        var badgeText = (TextBlock)badge.Child!;
        var loc = AppHost.Get<ILocalizer>();
        try
        {
            // Frame 0 (reduced motion): the open donut with "Drop → JPG".
            donut.FinishAnimations();
            Highlight(window, vm, 1);
            clip.Add(Capture(window), 1400);

            donut.BeginProgress();
            badge.Transitions = null;
            const long size = 3_100_000;
            var shown = false;
            for (var t = 0.0; t < 1.6; t += Frame)
            {
                if (t >= 0.3)
                {
                    var part = Math.Min(1, (t - 0.3) / 1.2);
                    var done = (long)(size * part);
                    donut.SetProgress(part / 2); // saving is the first half of a browser file's progress
                    badgeText.Text = ViewModels.DownloadBadge.Format(loc, 2_400_000, new Filee.Core.Conversion.JobTransfer(done, size, 0, 1, done));
                    if (!shown)
                    {
                        (badge.Opacity, badge.RenderTransform, shown) = (1, null, true);
                    }
                }
                Step(donut);
                clip.Add(Capture(window), (int)(Frame * 1000));
            }
            badge.Opacity = 0;
            donut.SetProgress(1);
            Run(clip, window, 0.6);
            donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => { });
            Run(clip, window, 2.5, () => donut.ShownOutcome is not null);
            // The check stays a moment before the clip starts over.
            clip.Hold(1600);
        }
        finally
        {
            window.Close();
        }
        return clip;
    }

    /// <summary>
    /// The donut's back face (#262): the images donut with "Tab ↻ back" in the hub turns over to its back face and,
    /// after a moment, to the front again.
    /// </summary>
    private static Clip DonutFlip()
    {
        var clip = new Clip();
        var (window, vm) = OpenDonut([@"C:\photos\a.jpg", @"C:\photos\b.jpg"], clickMode: true);
        var donut = window.DonutControl;
        try
        {
            donut.FinishAnimations();
            Pump(); // the back face is checked once the front is on screen
            Assert.True(vm.CanFlip);
            Settle(donut);
            // Frame 0: the front with its "Tab ↻ back" hint.
            clip.Add(Capture(window), 1600);
            for (var turn = 0; turn < 2; turn++)
            {
                Assert.True(donut.Flip());
                for (var t = 0.0; t < DonutMenu.FlipDuration + Frame; t += Frame)
                {
                    Step(donut);
                    clip.Add(Capture(window), (int)(Frame * 1000));
                }
                Settle(donut);
                clip.Add(Capture(window), turn == 0 ? 1800 : 900);
            }
        }
        finally
        {
            window.Close();
            if (vm.IsBackFace)
                vm.ToggleFace();
        }
        return clip;
    }

    /// <summary>The donut opening for one new format after another.</summary>
    private static Clip FormatDonuts()
    {
        var clip = new Clip();
        string[][] sets =
        [
            [@"C:\gis\roads.shp"],
            [@"C:\invoices\invoice.ofd"],
            [@"C:\web\article.mhtml"],
            [@"C:\photos\scan.jxr"],
            [@"C:\models\chair.fbx"],
            [@"C:\slides\template.potm"],
        ];
        foreach (var files in sets)
        {
            var (window, vm) = OpenDonut(files, clickMode: true);
            // As someone with the optional engines (LibreOffice) sees it, not greyed out for a missing engine.
            vm.Items = vm.Items.Select(Enabled).ToList();
            try
            {
                var donut = window.DonutControl;
                if (clip.Count == 0)
                {
                    // Frame 0: the first donut open, for reduced motion.
                    donut.FinishAnimations();
                    Highlight(window, vm, 0);
                    clip.Add(Capture(window), 900);
                    donut.SetExternalHighlight(-1);
                }
                donut.PlayOpenAnimation();
                Run(clip, window, 0.4);
                Highlight(window, vm, 0);
                clip.Add(Capture(window), 1100);
            }
            finally
            {
                window.Close();
            }
        }
        return clip;
    }

    /// <summary>The crop overlay: the frame closes in on part of a picture, then the square ratio is picked.</summary>
    private static Clip Crop()
    {
        var clip = new Clip();
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-crop-").FullName;
        try
        {
            var picture = Path.Combine(folder, "harbour.jpg");
            DrawLandscape(picture);
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var vm = new CropOverlayViewModel([picture], preset, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            var window = new CropOverlayWindow
            {
                DataContext = vm,
                Width = 960,
                Height = 540,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)),
            };
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            Pump();
            var canvas = window.GetVisualDescendants().OfType<CropCanvas>().Single();
            try
            {
                var from = new Avalonia.Rect(0.05, 0.05, 0.9, 0.9);
                var to = new Avalonia.Rect(0.42, 0.18, 0.44, 0.62);
                // Frame 0: the frame already on the part to keep.
                canvas.NormalizedCropRect = to;
                Pump();
                clip.Add(Capture(window), 1200);
                canvas.NormalizedCropRect = from;
                Pump();
                clip.Add(Capture(window), 600);
                const int steps = 18;
                for (var i = 1; i <= steps; i++)
                {
                    var t = Ease(i / (double)steps);
                    canvas.NormalizedCropRect = Lerp(from, to, t);
                    Pump();
                    clip.Add(Capture(window), 40);
                }
                clip.Add(Capture(window), 700);
                vm.SetAspect("1:1");
                Pump();
                clip.Add(Capture(window), 1600);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;
    }

    /// <summary>The donut rounding off as Softness goes up, then a few accent colours.</summary>
    private static Clip Softness()
    {
        var clip = new Clip();
        var theme = AppHost.Get<ThemeService>();
        var (window, vm) = OpenDonut([@"C:\photos\a.jpg", @"C:\photos\b.jpg"], clickMode: true);
        try
        {
            window.DonutControl.FinishAnimations();
            Highlight(window, vm, 2);
            // Frame 0: soft, default colours.
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light, CornerRadius = 28 }, "en");
            Pump();
            clip.Add(Capture(window), 1000);
            const int steps = 14;
            for (var i = 0; i <= steps; i++)
            {
                theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light, CornerRadius = 28.0 * i / steps }, "en");
                Pump();
                clip.Add(Capture(window), i == 0 ? 500 : 60);
            }
            clip.Add(Capture(window), 500);
            foreach (var accent in new[] { "#1D9E75", "#D4537E", "#E8873A", "#7F77DD" })
            {
                theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light, CornerRadius = 28, Accent = accent }, "en");
                Pump();
                clip.Add(Capture(window), 800);
            }
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, "en");
        }
        return clip;
    }

    /// <summary>The Home page in one language after another.</summary>
    private static Clip Languages(string version)
    {
        var clip = new Clip();
        foreach (var language in new[] { "es", "hi", "ja", "ru", "en", "ko", "zh-CN", "pl" })
        {
            TestServices.EnsureInitialized(language);
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, language);
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 540 };
            window.Show();
            Pump();
            clip.Add(Capture(window, version), 1100);
            window.Close();
            vm.Dispose();
        }
        TestServices.EnsureInitialized("en");
        return clip;
    }

    /// <summary>
    /// "And more": the history search finding an old conversion as it is typed, then "Reset to default" beside a
    /// built-in donut on the Toolbar page.
    /// </summary>
    private static Clip More(string version)
    {
        var clip = new Clip();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        store.ClearHistory();
        store.History.AddRange(Enumerable.Range(0, store.Settings.EffectiveHistoryLimit).Select(i => new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now.AddMinutes(-7 * i - 3),
            PresetName = i == 150 ? "PDF" : i % 3 == 0 ? "JPG" : "PNG",
            State = Filee.Core.Conversion.JobState.Completed,
            Sources = i == 150 ? [@"C:\Work\Annual report.docx"] : [$@"C:\Photos\IMG_{4000 + i:0000}.heic"],
        }));
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 540 };
            window.Show();
            Pump();
            var home = (HomePageViewModel)vm.CurrentPage!;
            // Frame 0: the old report found.
            home.Search = "report";
            Pump();
            clip.Add(Capture(window, version), 1400);
            home.Search = "";
            Pump();
            clip.Add(Capture(window, version), 700);
            foreach (var typed in new[] { "r", "re", "rep", "repo", "repor", "report" })
            {
                home.Search = typed;
                Pump();
                clip.Add(Capture(window, version), 160);
            }
            clip.Add(Capture(window, version), 1400);

            // A tall window shows the whole profile card; the clip shows its lower part, down to "Reset to default".
            vm.Navigate("toolbar");
            window.Width = 1240;
            window.Height = 1700;
            var page = (ToolbarPageViewModel)vm.CurrentPage!;
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "images");
            Pump();
            var actions = window.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ProfileActions");
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProfileCard");
            var cardAt = Avalonia.VisualExtensions.TranslatePoint(card, default, window)!.Value;
            var actionsAt = Avalonia.VisualExtensions.TranslatePoint(actions, default, window)!.Value;
            var bottom = actionsAt.Y + actions.Bounds.Height + 28;
            var region = new Avalonia.Rect(Math.Max(0, cardAt.X - 8), Math.Max(0, bottom - 540), 960, 540);
            clip.Add(Capture(window, version, region), 2200);
            window.Close();
            vm.Dispose();
        }
        finally
        {
            store.ClearHistory();
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
        }
        return clip;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static (RadialWindow Window, RadialViewModel Vm) OpenDonut(IReadOnlyList<string> files, bool clickMode)
    {
        var vm = AppHost.Get<RadialViewModel>();
        if (!clickMode)
            vm.ResetForDrag();
        vm.Load(files, clickMode);
        vm.FitToThisDonut();
        // The headless platform has no transparent windows: the page colour instead of black around the donut.
        var window = new RadialWindow { DataContext = vm, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)) };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static DonutItem Enabled(DonutItem item) => new()
    {
        Id = item.Id,
        Label = item.Label,
        Caption = item.Caption,
        Children = item.Children?.Select(Enabled).ToList(),
    };

    private static void Highlight(RadialWindow window, RadialViewModel vm, int index)
    {
        window.DonutControl.SetExternalHighlight(index);
        vm.ShowHint(index, -1, centerHot: false);
        Settle(window.DonutControl);
    }

    /// <summary>Lets the donut's springs (highlight, hub) come to rest, and draws it.</summary>
    private static void Settle(DonutMenu donut)
    {
        for (var i = 0; i < 90; i++)
            donut.Advance(1 / 60.0);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>One frame: the donut's clock moves on by <see cref="Frame"/>.</summary>
    private static void Step(DonutMenu donut)
    {
        KeepFramesComing(donut);
        donut.Advance(Frame);
        Dispatcher.UIThread.RunJobs();
        KeepFramesComing(donut);
    }

    /// <summary>Runs the donut's animations <paramref name="seconds"/> (or until <paramref name="until"/>), a frame every <see cref="Frame"/>.</summary>
    private static void Run(Clip clip, RadialWindow window, double seconds, Func<bool>? until = null)
    {
        for (var t = 0.0; t < seconds && until?.Invoke() != true; t += Frame)
        {
            Step(window.DonutControl);
            clip.Add(Capture(window), (int)(Frame * 1000));
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Assert.True(condition());
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

    private static Avalonia.Rect Lerp(Avalonia.Rect a, Avalonia.Rect b, double t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);

    /// <summary>
    /// A rendered frame of <paramref name="window"/> centred on the page-coloured canvas, scaled down to fit. With
    /// <paramref name="version"/>, the sidebar shows that version instead of the one being built (the clips are made
    /// before the version is bumped).
    /// </summary>
    private static MagickImage Capture(TopLevel window, string? version = null, Avalonia.Rect? region = null)
    {
        if (window is RadialWindow radial)
            KeepFramesComing(radial.DonutControl);
        try
        {
            return CaptureCore(window, version, region);
        }
        finally
        {
            if (window is RadialWindow after)
                KeepFramesComing(after.DonutControl);
        }
    }

    private static readonly System.Reflection.FieldInfo LastFrameTick =
        typeof(DonutMenu).GetField("_lastTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DonutMenu._lastTick is gone: update WhatsNewMedia.KeepFramesComing");

    /// <summary>
    /// Taking a frame (PNG, compare) takes longer than the donut's stalled-frames watchdog waits (StalledLoopMs), which
    /// would then jump every animation to its end. The clips step the clock themselves, so the watchdog is told that
    /// frames are coming.
    /// </summary>
    private static void KeepFramesComing(DonutMenu donut) => LastFrameTick.SetValue(donut, Environment.TickCount64);

    private static MagickImage CaptureCore(TopLevel window, string? version, Avalonia.Rect? region)
    {
        if (version is not null)
        {
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
                if (text.Text?.Contains(UpdateService.CurrentVersion, StringComparison.Ordinal) == true)
                    text.Text = text.Text.Replace(UpdateService.CurrentVersion, version, StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var png = new MemoryStream();
        if (window is RadialWindow)
        {
            // The donut is drawn straight from its state: the clips step its clock themselves, and the render timer
            // would run its own frame loop as well.
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            bitmap.Render(window);
            bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        else
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        png.Position = 0;
        using var shot = new MagickImage(png);
        if (region is { } r)
        {
            var x = (int)Math.Clamp(r.X, 0, Math.Max(0, shot.Width - r.Width));
            var y = (int)Math.Clamp(r.Y, 0, Math.Max(0, shot.Height - r.Height));
            shot.Crop(new MagickGeometry(x, y, (uint)r.Width, (uint)r.Height));
            shot.ResetPage();
        }
        var canvas = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
        var scale = Math.Min(1, Math.Min((double)CanvasWidth / shot.Width, (double)CanvasHeight / shot.Height));
        if (scale < 1)
        {
            shot.FilterType = FilterType.Lanczos;
            shot.Resize((uint)Math.Round(shot.Width * scale), (uint)Math.Round(shot.Height * scale));
        }
        canvas.Composite(shot, Gravity.Center, CompositeOperator.Over);
        return canvas;
    }

    /// <summary>A made-up photo (sky, sun, sea, hills) to crop: generated, so no picture is kept in the repository.</summary>
    private static void DrawLandscape(string path)
    {
        using var image = new MagickImage("gradient:#8EC5FC-#F8D2A6", 1600, 1000);
        new Drawables()
            .FillColor(new MagickColor("#FFF2C2")).Circle(1080, 360, 1080, 460)
            .FillColor(new MagickColor("#4F7CAC")).Rectangle(0, 640, 1600, 1000)
            .FillColor(new MagickColor("#6C9A5B")).Polygon(new PointD(0, 700), new PointD(380, 470), new PointD(760, 690), new PointD(0, 760))
            .FillColor(new MagickColor("#3F6E4A")).Polygon(new PointD(600, 700), new PointD(1000, 520), new PointD(1600, 700), new PointD(1600, 760), new PointD(600, 760))
            .FillColor(new MagickColor("#F2F2F2")).Polygon(new PointD(1180, 760), new PointD(1260, 600), new PointD(1260, 760))
            .FillColor(new MagickColor("#7A4E2D")).Rectangle(1150, 760, 1300, 790)
            .Draw(image);
        image.Quality = 92;
        image.Write(path, MagickFormat.Jpeg);
    }

    /// <summary>
    /// Renders and writes one clip. FILEE_WHATSNEW_ONLY (names without extension, comma-separated) limits a run to some
    /// clips, e.g. <c>FILEE_WHATSNEW_ONLY=crop,theme</c>; <paramref name="onRequestOnly"/> clips (made elsewhere) are
    /// written only when named there.
    /// </summary>
    private static void Write(string folder, string name, Func<Clip> render, bool onRequestOnly = false)
    {
        var only = Environment.GetEnvironmentVariable("FILEE_WHATSNEW_ONLY");
        var named = !string.IsNullOrWhiteSpace(only)
                    && only.Split(',', StringSplitOptions.TrimEntries).Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);
        if (onRequestOnly ? !named : !string.IsNullOrWhiteSpace(only) && !named)
            return;
        using (var clip = render())
        {
            var path = Path.Combine(folder, name);
            clip.WriteWebP(path);
            // The first frame next to the screenshots, for review.
            clip.SaveFirstFrame(Path.Combine(TestServices.ScreenshotDirectory, "whatsnew-" + Path.ChangeExtension(name, ".png")));
            Assert.InRange(new FileInfo(path).Length, 1, 400 * 1024);
        }
    }

    /// <summary>Frames with how long each shows; a frame identical to the one before only lengthens it.</summary>
    private sealed class Clip : IDisposable
    {
        private readonly List<(MagickImage Image, int Milliseconds)> _frames = [];

        public int Count => _frames.Count;

        /// <summary>Shows the last frame <paramref name="milliseconds"/> longer.</summary>
        public void Hold(int milliseconds) => _frames[^1] = (_frames[^1].Image, _frames[^1].Milliseconds + milliseconds);

        public void Add(MagickImage image, int milliseconds)
        {
            if (_frames.Count > 0 && _frames[^1].Image.Compare(image, ErrorMetric.Absolute) == 0)
            {
                _frames[^1] = (_frames[^1].Image, _frames[^1].Milliseconds + milliseconds);
                image.Dispose();
                return;
            }
            _frames.Add((image, milliseconds));
        }

        public void WriteWebP(string path)
        {
            using var collection = new MagickImageCollection();
            foreach (var (image, milliseconds) in _frames)
            {
                var copy = image.Clone();
                // Delays are in 1/100 s; at least 2 so viewers don't speed the frame up.
                copy.AnimationDelay = (uint)Math.Max(2, Math.Round(milliseconds / 10.0));
                copy.AnimationTicksPerSecond = 100;
                copy.Quality = 82;
                collection.Add(copy);
            }
            var defines = new ImageMagick.Formats.WebPWriteDefines { Method = 6, Lossless = false, ThreadLevel = true };
            if (_frames.Count > 1)
                collection[0].Settings.SetDefine(MagickFormat.WebP, "mixed", "true");
            collection.Write(path, defines);
        }

        public void SaveFirstFrame(string path) => _frames[0].Image.Write(path, MagickFormat.Png);

        public void Dispose()
        {
            foreach (var (image, _) in _frames)
                image.Dispose();
            _frames.Clear();
        }
    }
}
