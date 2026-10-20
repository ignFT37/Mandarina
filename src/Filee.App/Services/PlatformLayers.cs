// Mandarina: each OS layer is only touched from its own small method, so Windows never loads Filee.Platform.Linux.dll
// or Filee.Platform.MacOS.dll (the JIT loads an assembly when it compiles a method that names one of its types, even
// in a branch that never runs). Windows' Smart App Control judges every unsigned DLL that is loaded and can block a fresh
// build of one the app has no use for, which would stop it before it starts.

using System.Runtime.CompilerServices;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Platform.Linux;
using Filee.Platform.MacOS;
using Filee.Platform.Windows;

namespace Filee.App.Services;

internal static class PlatformLayers
{
    public static IPlatformServices Create() =>
        OperatingSystem.IsWindows() ? CreateWindows()
        : OperatingSystem.IsMacOS() ? CreateMac()
        : OperatingSystem.IsLinux() ? CreateLinux()
        : new NullPlatformServices();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlatformServices CreateWindows() => new WindowsPlatformServices();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlatformServices CreateMac() => new MacOSPlatformServices();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlatformServices CreateLinux() => new LinuxPlatformServices();

    // ───────── macOS ─────────

    public static bool IsMac(IPlatformServices? platform) => OperatingSystem.IsMacOS() && IsMacLayer(platform);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsMacLayer(IPlatformServices? platform) => platform is MacOSPlatformServices;

    /// <summary>macOS: the user's preferred language from the system (an app started from Finder gets no LANG).</summary>
    public static string? MacPreferredLanguage() => OperatingSystem.IsMacOS() ? MacLanguage() : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? MacLanguage() => MacOSLocale.PreferredLanguage();

    /// <summary>macOS: "Convert with Filee" in Finder's Services menu.</summary>
    public static void RegisterMacServices(Action<IReadOnlyList<string>> convert, Action<IReadOnlyList<string>> noFiles)
    {
        if (OperatingSystem.IsMacOS())
            RegisterMac(convert, noFiles);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterMac(Action<IReadOnlyList<string>> convert, Action<IReadOnlyList<string>> noFiles) =>
        MacOSServicesProvider.Register(files => convert(files), types => noFiles(types));

    /// <summary>macOS: lets the donut's window take file drags (once per window).</summary>
    public static bool AcceptMacFileDrags(IPlatformServices? platform, nint handle) =>
        OperatingSystem.IsMacOS() && AcceptDrags(platform, handle);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AcceptDrags(IPlatformServices? platform, nint handle)
    {
        if (platform is not MacOSPlatformServices mac)
            return false;
        mac.AcceptFileDrags(handle);
        return true;
    }

    public static void OpenMacAccessibilitySettings(IPlatformServices? platform)
    {
        if (OperatingSystem.IsMacOS())
            OpenAccessibility(platform);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenAccessibility(IPlatformServices? platform)
    {
        if (platform is MacOSPlatformServices mac)
            mac.OpenAccessibilitySettings();
    }

    public static void RequestMacAccessibility(IPlatformServices? platform)
    {
        if (OperatingSystem.IsMacOS())
            RequestAccessibility(platform);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequestAccessibility(IPlatformServices? platform)
    {
        if (platform is MacOSPlatformServices mac)
            mac.RequestAccessibilityPermission();
    }

    // ───────── Linux ─────────

    public static bool IsLinux(IPlatformServices? platform) => OperatingSystem.IsLinux() && IsLinuxLayer(platform);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsLinuxLayer(IPlatformServices? platform) => platform is LinuxPlatformServices;

    /// <summary>Linux under Wayland, where drag gestures can't work.</summary>
    public static bool IsWaylandSession => OperatingSystem.IsLinux() && Wayland();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Wayland() => LinuxPlatformServices.IsWaylandSession;

    /// <summary>Linux: Thunar's accelerator for a key chord (null elsewhere or without a chord).</summary>
    public static string? ThunarAccelerator(TriggerGesture? shortcut) =>
        OperatingSystem.IsLinux() && shortcut is not null ? Accelerator(shortcut) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? Accelerator(TriggerGesture shortcut) => LinuxPlatformServices.ToThunarAccelerator(shortcut);

    public static void SetThunarShortcut(IPlatformServices? platform, string? accelerator)
    {
        if (OperatingSystem.IsLinux())
            SetThunar(platform, accelerator);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetThunar(IPlatformServices? platform, string? accelerator)
    {
        if (platform is LinuxPlatformServices linux)
            linux.SetThunarShortcut(accelerator);
    }

    /// <summary>The notice on the Shortcuts page for macOS and Linux; null on Windows.</summary>
    public static string? ShortcutsNotice(IPlatformServices? platform, ILocalizer loc) =>
        IsMac(platform) ? loc["triggers.macos_permissions"]
        : IsLinux(platform) ? loc[IsWaylandSession
            ? platform!.SupportsGlobalPointerGestures ? "triggers.linux_wayland_experimental" : "triggers.linux_wayland"
            : "triggers.linux_x11"]
        : null;
}
