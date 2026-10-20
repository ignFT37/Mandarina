// Mandarina: the Microsoft Office card in Settings → Engines. It says "Installed on this PC" (not "Included" or "Ready"),
// why it can't be used (not installed, turned off), that it works but is not activated, has a switch only when Office is
// installed, and the switch saves the setting. The texts exist in the four languages.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Com;

namespace Filee.App.Tests;

public sealed class MandarinaOfficeEngineTests
{
    private sealed class FakeProbe(OfficeLicense license, params OfficeKind[] installed) : IOfficeProbe
    {
        public OfficeAppInfo? Find(OfficeKind kind) =>
            installed.Contains(kind) ? new OfficeAppInfo(kind, $@"C:\Office\{kind}.exe", "16.0.14334.20974") : null;

        public (OfficeLicense State, string Signature) License() => (license, license.ToString());
    }

    private static readonly OfficeKind[] All = [OfficeKind.Word, OfficeKind.Excel, OfficeKind.PowerPoint];

    private static OfficeConverter Office(OfficeLicense license, bool enabled, params OfficeKind[] installed) =>
        new(new EngineEnvironment(Path.Combine(TestServices.DataDirectory, "office")), new FakeProbe(license, installed), OfficeTimings.Default, () => enabled);

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static EnginesPageViewModel Page(ConverterCatalog catalog, ILocalizer loc) =>
        new(AppHost.Get<UserDataStore>(), catalog, loc, AppHost.Get<EngineDownloadService>());

    private static ConverterCatalog CatalogWith(OfficeConverter office) =>
        new(AppHost.Get<ConverterCatalog>().All.Select(c => c.Id == "office" ? office : c));

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void The_card_says_what_state_Office_is_in_and_has_a_switch_only_when_it_is_installed(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();

        foreach (var (office, key, hasSwitch) in new[]
                 {
                     (Office(OfficeLicense.Licensed, true), "engine.reason.not_installed", false),
                     (Office(OfficeLicense.Licensed, true, All), "engines.system", true),
                     (Office(OfficeLicense.Restricted, true, All), "engine.reason.office_unlicensed", true),
                     (Office(OfficeLicense.Licensed, false, All), "engine.reason.office_off", true),
                     (Office(OfficeLicense.Licensed, true, OfficeKind.Excel), "engines.system", true),
                 })
        {
            var card = new EngineItemViewModel(office, office.GetStatus(), loc);

            Assert.NotEqual(key, loc[key]); // translated
            Assert.Equal(loc[key], card.StatusText);
            Assert.NotEqual(loc["engines.included"], card.StatusText);
            Assert.NotEqual(loc["engines.available"], card.StatusText);
            Assert.Equal(hasSwitch, card.HasSwitch);
            Assert.False(card.IsIncluded);
            Assert.Equal("Microsoft Office", card.Name);
            Assert.NotNull(card.Description); // engine.office.description
        }
        Assert.NotEqual("engines.office_switch", loc["engines.office_switch"]);
    }

    [AvaloniaFact]
    public void The_switch_saves_the_setting_and_the_engine_starts_turned_on_in_the_settings()
    {
        TestServices.EnsureInitialized();
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.OfficeEnabled;
        using var page = Page(AppHost.Get<ConverterCatalog>(), AppHost.Get<ILocalizer>());
        try
        {
            Assert.Equal(before, page.OfficeEnabled);
            page.OfficeEnabled = !before;
            Assert.Equal(!before, store.Settings.OfficeEnabled);
        }
        finally
        {
            page.OfficeEnabled = before;
        }
        Assert.Equal(before, store.Settings.OfficeEnabled);
        Assert.True(new AppSettings().OfficeEnabled);
        Assert.Contains("office", AppHost.Get<ConverterCatalog>().All.Select(c => c.Id));
    }

    [AvaloniaTheory]
    [InlineData("es")]
    [InlineData("ko")]
    public void The_engines_page_renders_the_Office_card(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        // A short list (the neighbours of the card in the priority order), so the picture is readable.
        var office = Office(OfficeLicense.Restricted, true, All);
        var catalog = new ConverterCatalog(AppHost.Get<ConverterCatalog>().All.Where(c => c.Id is "pandoc" or "ffmpeg" or "libreoffice").Append(office))
        {
            Priority = AppHost.Get<UserDataStore>().Settings.EnginePriority,
        };
        using var page = Page(catalog, loc);
        Assert.Contains(page.Engines, e => e.Converter.Id == "office" && e.HasSwitch);
        Assert.Equal(["ffmpeg", "pandoc", "office", "libreoffice"], page.Engines.Select(e => e.Converter.Id));

        var window = new Window { Content = new EnginesPage { DataContext = page }, Width = 900, Height = 1500 };
        window.Show();
        Pump();
        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-engines-office-{language}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }
}
