// Mandarina: finds the ToolPanel classes. A panel registers itself with [ToolPanel("id")]; the wheel offers the tools
// whose panel exists (WheelCatalog) and greys out the ones that need FFmpeg, or the speech model, while it is missing.

using System.Reflection;
using Filee.App.Mandarina.Tools;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Mandarina.Panels;

public static class PanelRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, (Type Type, ToolPanelAttribute Info)>> Panels = new(() =>
        typeof(ToolPanel).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(ToolPanel)))
            .Select(type => (Type: type, Info: type.GetCustomAttribute<ToolPanelAttribute>()))
            .Where(entry => entry.Info is not null)
            .ToDictionary(entry => entry.Info!.Id, entry => (entry.Type, entry.Info!), StringComparer.Ordinal));

    /// <summary>True when the tool <paramref name="id"/> has a panel in this build.</summary>
    public static bool Has(string id) => Panels.Value.ContainsKey(id);

    /// <summary>Every id that has a panel.</summary>
    public static IEnumerable<string> Ids => Panels.Value.Keys;

    /// <summary>Why the tool can't run right now (a localized reason for the greyed-out petal), or null when it can.</summary>
    public static string? Unavailable(string id, ILocalizer loc) => Unavailable(id, loc, null);

    /// <summary><paramref name="models"/> is what is installed of Whisper (null: look); tests pass their own.</summary>
    internal static string? Unavailable(string id, ILocalizer loc, WhisperModels? models)
    {
        if (!Panels.Value.TryGetValue(id, out var panel))
            return null;
        if (panel.Info.NeedsFfmpeg && MediaTools.Locate() is null)
            return loc.Format("engine.reason.needs_package", "FFmpeg");
        // Either speech model lets the tools work (the large one on a card, the small one on the processor): the petal is greyed
        // out only when there is none.
        return panel.Info.NeedsWhisper && !(models ?? WhisperModel.Installed()).Any ? loc.Format("engine.reason.needs_package", "Whisper") : null;
    }

    /// <summary>Builds the panel of tool <paramref name="id"/> for these files.</summary>
    public static ToolPanel Create(string id, IReadOnlyList<string> files, ILocalizer loc, IPlatformServices platform, ILogger? log = null)
    {
        if (!Panels.Value.TryGetValue(id, out var panel))
            throw new ArgumentException($"No panel for tool '{id}'.", nameof(id));
        return (ToolPanel)Activator.CreateInstance(panel.Type, new PanelContext(files, loc, platform) { Log = log ?? NullLogger.Instance })!;
    }
}
