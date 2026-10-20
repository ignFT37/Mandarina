// Removes per-user integration and downloaded engines after Filee exits. User settings remain for reinstalling.

using Filee.Core.Platform;
using Filee.Platform.Linux;
using Filee.Platform.MacOS;
using Filee.Platform.Windows;

namespace Filee.App.Services;

internal static class UninstallCleanup
{
    public static bool Run()
    {
        var platform = PlatformLayers.Create();
        var succeeded = true;
        Clean(() => platform.SetContextMenu(false, "", ""));
        Clean(() => platform.SetStartWithSystem(false, ""));
        if (PlatformLayers.IsLinux(platform))
            Clean(() => PlatformLayers.SetThunarShortcut(platform, null));
        if (OperatingSystem.IsWindows() && ExplorerMenuRegistration.IsSupportedOs)
        {
            Clean(() =>
            {
                var task = ExplorerMenuRegistration.RemoveAsync(allowElevation: false);
                if (!task.Wait(TimeSpan.FromSeconds(40)))
                    throw new TimeoutException("Timed out removing the Explorer menu.");
                if (!task.Result.Succeeded)
                    throw new IOException(task.Result.Error ?? "Could not remove the Explorer menu.");
            });
        }
        if (succeeded)
        {
            Clean(() =>
            {
                var engines = Filee.Engines.Infrastructure.EngineEnvironment.DownloadRoot;
                if (Directory.Exists(engines))
                    Directory.Delete(engines, recursive: true);
            });
        }
        return succeeded;

        void Clean(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                succeeded = false;
                Console.Error.WriteLine(ex.Message);
            }
        }
    }
}
