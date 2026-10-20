// Mandarina: Settings → Engines says "Included" for the engines that ship with the program (7-Zip, rhwp and the libraries inside it)
// and keeps "Ready" for the ones that come from a download or from the user's own copy.

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

namespace Filee.App.Tests;

public sealed class MandarinaEnginesTests
{
    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static EnginesPageViewModel Page(ILocalizer loc) =>
        new(AppHost.Get<UserDataStore>(), AppHost.Get<ConverterCatalog>(), loc, AppHost.Get<EngineDownloadService>());

    [Fact]
    public void Only_the_engines_of_the_optional_downloads_need_one()
    {
        string[] downloads = [.. EngineDownloads.Packages.SelectMany(package => package.ConverterIds)];

        Assert.NotEmpty(downloads);
        Assert.All(downloads, id => Assert.True(EngineDownloads.ProvidedByPackage(id), id));
        // 7-Zip, rhwp and the libraries are not listed as downloads: they ship with the program.
        Assert.All(new[] { "archive", "rhwp", "markdown", "magick" }, id => Assert.False(EngineDownloads.ProvidedByPackage(id), id));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Engines_inside_the_program_say_included_and_downloads_keep_ready(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        Assert.NotEqual("engines.included", loc["engines.included"]);
        Assert.NotEqual(loc["engines.available"], loc["engines.included"]);

        using var vm = Page(loc);
        var byId = vm.Engines.ToDictionary(engine => engine.Converter.Id);

        foreach (var engine in vm.Engines)
        {
            var viaDownload = EngineDownloads.ProvidedByPackage(engine.Converter.Id);
            Assert.Equal(engine.IsAvailable && !viaDownload, engine.IsIncluded);
            Assert.Equal(engine.IsIncluded ? loc["engines.included"] : engine.IsAvailable ? loc["engines.available"] : engine.StatusText, engine.StatusText);
        }

        // The libraries inside the program, always there.
        Assert.Equal(loc["engines.included"], byId["markdown"].StatusText);
        Assert.Equal(loc["engines.included"], byId["magick"].StatusText);
        // 7-Zip and rhwp are files in the install folder (engines\ next to the test binaries here): included when they are there.
        foreach (var id in new[] { "archive", "rhwp" })
        {
            if (byId[id].IsAvailable)
                Assert.Equal(loc["engines.included"], byId[id].StatusText);
        }
        // A downloaded or user-chosen engine is never "included".
        foreach (var id in EngineDownloads.Packages.SelectMany(package => package.ConverterIds).Where(byId.ContainsKey))
            Assert.NotEqual(loc["engines.included"], byId[id].StatusText);
    }

    [AvaloniaTheory]
    [InlineData("es")]
    [InlineData("ko")]
    public void The_engines_page_renders_with_the_included_badges(string language)
    {
        TestServices.EnsureInitialized(language);
        using var vm = Page(AppHost.Get<ILocalizer>());
        var window = new Window { Content = new EnginesPage { DataContext = vm }, Width = 900, Height = 2800 };
        window.Show();
        Pump();

        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-engines-{language}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }
}
