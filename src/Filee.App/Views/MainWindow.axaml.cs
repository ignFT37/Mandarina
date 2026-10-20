using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;

namespace Filee.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await Launcher.LaunchUriAsync(new Uri(vm.RepositoryUrl));
    }

    private async void OnSponsor(object? sender, RoutedEventArgs e) =>
        await SponsorWindow.Create(AppHost.Get<ILocalizer>()).ShowDialog(this);

    // Mandarina: set by the tests (see TestServices) to close a window they created for good.
    private bool _closeForGood;

    /// <summary>Closing the window keeps Filee running in the tray (Quit is in the tray menu).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is WindowCloseReason.WindowClosing && !_closeForGood)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// Mandarina: closes the window instead of hiding it. A closed-but-hidden window stays registered with the renderer and
    /// subscribed to every resource change for as long as the process lives; the app has one and reuses it, the tests make a new
    /// one for each test and use this to get rid of it.
    /// </summary>
    internal void CloseForGood()
    {
        _closeForGood = true;
        Close();
    }
}
