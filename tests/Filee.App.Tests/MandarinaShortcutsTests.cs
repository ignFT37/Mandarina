// Mandarina: Settings → Shortcuts lets each gesture pick the wheel it opens (convert, tools or actions): before, only
// settings.json could. The choice is saved, the gesture detector obeys it, and the page renders it in the four languages.

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public sealed class MandarinaShortcutsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mandarina-shortcuts").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A store in its own folder holding Mandarina's default gestures: Shift → convert, Ctrl → tools, Alt → actions, Ctrl+Alt+Space.</summary>
    private UserDataStore Store()
    {
        var store = new UserDataStore(_dir);
        store.Load();
        store.Settings.Triggers.Clear();
        store.Settings.Triggers.AddRange(TriggerGesture.Defaults());
        return store;
    }

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Each_gesture_shows_the_wheel_it_opens()
    {
        TestServices.EnsureInitialized("es");
        var vm = new TriggersPageViewModel(Store(), AppHost.Get<ILocalizer>());

        Assert.Equal([WheelMode.Convert, WheelMode.Tools, WheelMode.Actions, WheelMode.Convert], vm.Gestures.Select(g => g.Wheel.Value));
        Assert.Equal(["Convertir", "Herramientas", "Acciones"], vm.Gestures[0].Wheels.Select(w => w.Label));
        Assert.EndsWith("→ Convertir", vm.Gestures[0].Summary);
        Assert.EndsWith("→ Herramientas", vm.Gestures[1].Summary);
        Assert.EndsWith("→ Acciones", vm.Gestures[2].Summary);
    }

    [AvaloniaFact]
    public void Choosing_a_wheel_is_saved_and_the_detector_opens_that_wheel()
    {
        TestServices.EnsureInitialized("es");
        var store = Store();
        var vm = new TriggersPageViewModel(store, AppHost.Get<ILocalizer>());

        // Swap two keys: Shift now opens the actions, Alt the conversion.
        vm.Gestures[0].Wheel = vm.Gestures[0].Wheels.Single(w => w.Value == WheelMode.Actions);
        vm.Gestures[2].Wheel = vm.Gestures[2].Wheels.Single(w => w.Value == WheelMode.Convert);
        Assert.EndsWith("→ Acciones", vm.Gestures[0].Summary);

        var reloaded = new UserDataStore(_dir);
        reloaded.Load();
        Assert.Equal([WheelMode.Actions, WheelMode.Tools, WheelMode.Convert], reloaded.Settings.Triggers.Take(3).Select(g => g.Wheel));

        var detector = new GestureDetector { SystemDragThreshold = 4 };
        detector.Configure(reloaded.Settings.Triggers);
        var wheels = new List<WheelMode>();
        detector.DragStarted += (gesture, _, _) => wheels.Add(gesture.Wheel);
        detector.WheelRequested += gesture => wheels.Add(gesture.Wheel);
        detector.MouseDown(TriggerMouseButton.Left, 100, 100, ModifierKeys.None);
        detector.MouseMove(140, 100, ModifierKeys.None, TriggerMouseButton.Left);
        detector.KeyDown("LeftShift", ModifierKeys.Shift);   // opens what Shift is set to
        detector.KeyDown("LeftAlt", ModifierKeys.Alt);       // switches to what Alt is set to

        Assert.Equal([WheelMode.Actions, WheelMode.Convert], wheels);
    }

    [AvaloniaFact]
    public void A_gesture_can_also_open_another_wheel_from_a_key_chord()
    {
        TestServices.EnsureInitialized("es");
        var store = Store();
        var vm = new TriggersPageViewModel(store, AppHost.Get<ILocalizer>());
        var chord = vm.Gestures[3];
        Assert.Equal(TriggerKind.KeyChord, chord.Kind.Value);

        chord.Wheel = chord.Wheels.Single(w => w.Value == WheelMode.Tools);

        var detector = new GestureDetector();
        detector.Configure(store.Settings.Triggers);
        TriggerGesture? fired = null;
        detector.KeyChordTriggered += (gesture, _, _) => fired = gesture;
        detector.KeyDown("Space", ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(WheelMode.Tools, fired?.Wheel);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void The_shortcuts_page_renders_the_wheel_choice_in_every_language(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var vm = new TriggersPageViewModel(Store(), loc);
        var window = new Window { Content = new TriggersPage { DataContext = vm }, Width = 820, Height = 1500 };
        window.Show();
        Pump();

        var combos = window.GetVisualDescendants().OfType<ComboBox>().Where(c => AutomationProperties.GetAutomationId(c) == "TriggerWheel").ToList();
        Assert.Equal(4, combos.Count);
        Assert.Equal([loc["triggers.wheel.Convert"], loc["triggers.wheel.Tools"], loc["triggers.wheel.Actions"], loc["triggers.wheel.Convert"]],
            combos.Select(c => ((IChoice)c.SelectedItem!).Label));
        Assert.All(combos, combo => Assert.True(combo.Bounds.Width > 60 && combo.IsEffectivelyVisible));
        Assert.NotEqual("triggers.wheel", loc["triggers.wheel"]);

        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-shortcuts-{language}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }
}
