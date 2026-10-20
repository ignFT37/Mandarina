// Mandarina: which parts of Microsoft Office are installed on this PC (found in the registry, never by starting them) and
// how Windows says they are licensed.

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Filee.Engines.Office.Com;

/// <summary>The Office applications the engine drives.</summary>
internal enum OfficeKind
{
    Word,
    Excel,
    PowerPoint,
}

internal static class OfficeKinds
{
    public static readonly IReadOnlyList<OfficeKind> All = [OfficeKind.Word, OfficeKind.Excel, OfficeKind.PowerPoint];

    public static string ProgId(this OfficeKind kind) => kind switch
    {
        OfficeKind.Word => "Word.Application",
        OfficeKind.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    /// <summary>Name of the process (no .exe).</summary>
    public static string ProcessName(this OfficeKind kind) => kind switch
    {
        OfficeKind.Word => "WINWORD",
        OfficeKind.Excel => "EXCEL",
        _ => "POWERPNT",
    };

    public static string DisplayName(this OfficeKind kind) => kind switch
    {
        OfficeKind.Word => "Word",
        OfficeKind.Excel => "Excel",
        _ => "PowerPoint",
    };
}

/// <summary>An installed Office application.</summary>
/// <param name="ExePath">Its program (WINWORD.EXE, EXCEL.EXE, POWERPNT.EXE).</param>
/// <param name="Version">File version of the program, e.g. 16.0.14334.20974.</param>
internal sealed record OfficeAppInfo(OfficeKind Kind, string ExePath, string Version);

/// <summary>Looks at the PC: what is installed and how it is licensed. A fake stands in for it in tests.</summary>
internal interface IOfficeProbe
{
    /// <summary>The installed application, or null.</summary>
    OfficeAppInfo? Find(OfficeKind kind);

    /// <summary>What Windows says about Office's license, and a signature that changes when that changes.</summary>
    (OfficeLicense State, string Signature) License();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsOfficeProbe : IOfficeProbe
{
    public OfficeAppInfo? Find(OfficeKind kind)
    {
        // The program that serves the ProgID: HKCR\Word.Application\CLSID → HKCR\CLSID\{id}\LocalServer32. Both views of the
        // registry, because a 32-bit Office on 64-bit Windows registers its servers in the 32-bit one.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using var progId = root.OpenSubKey(kind.ProgId() + "\\CLSID");
                if (progId?.GetValue(null) as string is not { Length: > 0 } clsid)
                    continue;
                using var server = root.OpenSubKey($"CLSID\\{clsid}\\LocalServer32");
                if (ExecutableOf(server?.GetValue(null) as string) is not { } exe || !File.Exists(exe))
                    continue;
                var version = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                return new OfficeAppInfo(kind, exe, string.IsNullOrWhiteSpace(version) ? "?" : version.Trim());
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // an unreadable key is the same as no Office
            }
        }
        return null;
    }

    public (OfficeLicense State, string Signature) License() => OfficeNative.QueryOfficeLicense();

    /// <summary>"\"C:\...\WINWORD.EXE\" /Automation" → "C:\...\WINWORD.EXE".</summary>
    internal static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }
}
