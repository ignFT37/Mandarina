// Mandarina: build output lives outside the checkout (see Directory.Build.props), so tests that read repository files
// start their search for Filee.slnx in the checkout MSBuild built them from.

using System.Reflection;

namespace Filee.Platform.Tests;

internal static class TestPaths
{
    public static string Start { get; } =
        typeof(TestPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MandarinaRepoRoot")?.Value ?? AppContext.BaseDirectory;
}
