using System.Runtime.CompilerServices;
using PixMcp.Pix;

namespace PixMcp.Tests;

internal static class TestInit
{
    /// <summary>Forces PixMcp's module initializer (the PixApiCsExt assembly resolver) to run before any test touches PIX types.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        _ = PixDiscovery.InstallDir;
    }
}
