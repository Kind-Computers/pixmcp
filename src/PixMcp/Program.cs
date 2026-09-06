using PixMcp;
using PixMcp.Pix;

// Keep bootstrap independent of PIX-backed types: report discovery failures before
// tool registration tries to load the experimental assembly.
if (PixDiscovery.InstallDir is null || !string.IsNullOrEmpty(PixDiscovery.Error))
{
    Console.Error.WriteLine("pixmcp: " + (PixDiscovery.Error ?? "No compatible PIX Preview installation found.") +
        " Install PIX Preview 2606.18 or newer, or set PIX_DIR to its installation directory.");
    return 1;
}

await ServerHost.RunAsync(args);
return 0;
