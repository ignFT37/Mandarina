// Mandarina: what the user ticked on the installer's "Windows integration" page. The setup writes it next to the program
// (setup-options.json); the first start that sees it applies it as if it had been switched on the General page, then deletes it,
// so a later start never overrides what the user changed in Settings. A portable copy has no such file.

using System.Text.Json;
using System.Text.Json.Serialization;
using Filee.Core.Settings;

namespace Filee.App.Mandarina;

internal sealed record InstallerChoices(
    [property: JsonPropertyName("startWithWindows")] bool? StartWithWindows,
    [property: JsonPropertyName("contextMenu")] bool? ContextMenu)
{
    public const string FileName = "setup-options.json";

    /// <summary>The choices in the file next to <paramref name="programFolder"/>'s program, or null when there is none or it is unreadable.</summary>
    public static InstallerChoices? Read(string? programFolder)
    {
        if (programFolder is null)
            return null;
        try
        {
            var path = Path.Combine(programFolder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallerChoices>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Applies the choices to <paramref name="settings"/> and removes the file; true when a setting changed.</summary>
    public static bool Consume(string? programFolder, AppSettings settings)
    {
        if (Read(programFolder) is not { } choices)
            return false;
        var changed = false;
        if (choices.StartWithWindows is { } start && settings.StartWithSystem != start)
        {
            settings.StartWithSystem = start;
            changed = true;
        }
        if (choices.ContextMenu is { } menu && settings.ContextMenuEnabled != menu)
        {
            settings.ContextMenuEnabled = menu;
            changed = true;
        }
        try { File.Delete(Path.Combine(programFolder!, FileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // read-only folder: harmless, same values next time
        return changed;
    }
}
