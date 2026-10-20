using Avalonia;
using Avalonia.Headless;
using Filee.App;
using Filee.App.Services;
using Filee.Core.Settings;

[assembly: AvaloniaTestApplication(typeof(Filee.App.Tests.TestAppBuilder))]
// One application for all tests. Per test, Avalonia disposes the font manager and loads the embedded Noto fonts
// again, and on Windows the test process then crashed with 0xC0000005 (only builds with the fonts, i.e. releases).
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Filee.App.Tests;

/// <summary>Headless Avalonia with real Skia rendering so views can be captured as images.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .ConfigureFonts(fonts => fonts.AddFontCollection(new FileeFontCollection()))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Builds the service container once against a throw-away data folder (never touches %APPDATA%).</summary>
internal static class TestServices
{
    private static Application? _initializedFor;

    public static string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "filee-app-tests", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// The headless runner may create a new Application per test, so services (and the localization
    /// dictionary attached to Application.Resources) are rebuilt whenever the application changes.
    /// </summary>
    public static void EnsureInitialized(string language = "en")
    {
        var app = Application.Current!;
        if (!ReferenceEquals(_initializedFor, app))
        {
            AppHost.Build(DataDirectory);
            AppHost.Get<UserDataStore>().Load();
            AppHost.Get<LocalizationService>().Attach(app);
            _initializedFor = app;
        }

        // Mandarina: the main windows of the tests before are closed for good (below) and only what is not in place is applied.
        if (!_watchingWindows)
        {
            Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Filee.App.Views.MainWindow>((window, _) =>
            {
                if (!MainWindows.Contains(window))
                    MainWindows.Add(window);
            });
            _watchingWindows = true;
        }

        var store = AppHost.Get<UserDataStore>();
        store.Settings.Language = language;
        var localization = AppHost.Get<LocalizationService>();
        var theme = store.Settings.Theme;
        var changed = localization.NeedsUpdate(language) || !ThemeIsApplied(app, theme);
        if (changed || MainWindows.Count >= CleanUpAfter)
            CloseMainWindows();
        if (!changed)
            return;

        localization.SetLanguage(language);
        AppHost.Get<ThemeService>().Apply(theme, language);
    }

    /// <summary>The main windows the tests opened and "closed" (which only hides them), until they are closed for good.</summary>
    private static readonly List<Filee.App.Views.MainWindow> MainWindows = [];

    private const int CleanUpAfter = 3;
    private static bool _watchingWindows;

    /// <summary>
    /// Closes for good the main windows the tests before left hidden, and collects them. Closing a MainWindow only hides it (the
    /// app lives in the tray and reuses it), so every test that made one left it behind: registered with the renderer and subscribed
    /// to every resource change, with its pages, view models and pictures, for the rest of the run. Each of the ~1,200 tests
    /// piled up more and every <c>SetLanguage</c> (a thousand resources, each one told to every control alive) paid over a second
    /// per window: RenderTests alone took over ten minutes, the whole suite over two hours and 3.8 GB.
    /// </summary>
    private static void CloseMainWindows()
    {
        var keep = AppHost.Get<WindowService>().Main;
        foreach (var window in MainWindows.ToArray())
        {
            if (window.IsVisible || ReferenceEquals(window, keep))
                continue;
            window.CloseForGood();
            MainWindows.Remove(window);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>The application already shows <paramref name="theme"/> (a test that left another one behind is put right).</summary>
    private static bool ThemeIsApplied(Application app, ThemeSettings theme)
    {
        var variant = theme.Mode switch
        {
            ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
            ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
        return app.RequestedThemeVariant == variant
               && Avalonia.Media.Color.TryParse(theme.Accent, out var accent)
               && app.Resources.TryGetResource("AccentColor", null, out var applied) && applied is Avalonia.Media.Color current && current == accent
               && app.Resources.TryGetResource("SoftRadius", null, out var radius) && radius is Avalonia.CornerRadius corner
               && Math.Abs(corner.TopLeft - Math.Clamp(theme.CornerRadius, 0, 28)) < 0.01;
    }

    /// <summary>Where rendered screenshots go: tests/Filee.App.Tests/bin/.../screenshots.</summary>
    public static string ScreenshotDirectory
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
