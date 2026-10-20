// Mandarina: opens the panel of a wheel tool (RadialController.Invoke calls it when a petal "opens a panel").

using Avalonia;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Filee.App.Mandarina.Panels;

public sealed class PanelHost(ILocalizer loc, IPlatformServices platform, ILogger<PanelHost> log)
{
    private readonly List<ToolPanelWindow> _open = [];

    /// <summary>The panel windows that are open now (UI thread only).</summary>
    public IReadOnlyList<ToolPanelWindow> OpenWindows => _open;

    /// <summary>
    /// Shows the panel of tool <paramref name="id"/> for <paramref name="files"/>, centred on the screen under
    /// <paramref name="near"/>. Returns the window, or null when the tool has no panel or could not start.
    /// </summary>
    public ToolPanelWindow? Open(string id, IReadOnlyList<string> files, PixelPoint? near = null)
    {
        if (!PanelRegistry.Has(id) || files.Count == 0)
            return null;
        try
        {
            log.LogInformation("Opening the {Tool} panel for {Count} file(s)", id, files.Count);
            var window = new ToolPanelWindow(PanelRegistry.Create(id, files, loc, platform, log));
            _open.Add(window);
            window.Closed += (_, _) => _open.Remove(window);
            window.ShowCentered(near);
            return window;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PanelException
                                       or System.Reflection.TargetInvocationException)
        {
            log.LogWarning(ex, "The {Tool} panel could not open", id);
            return null;
        }
    }
}
