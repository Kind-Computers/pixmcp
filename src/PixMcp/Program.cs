using PixMcp;
using PixMcp.Pix;

// Keep bootstrap independent of PIX-backed types: report discovery failures before
// tool registration tries to load the experimental assembly.
if (PixDiscovery.InstallDir is null || !string.IsNullOrEmpty(PixDiscovery.Error))
{
    Console.Error.WriteLine("pixmcp: " + (PixDiscovery.Error ?? "No compatible PIX Preview installation found.") +
        $" Install {PixDiscovery.Requirement}, or set PIX_DIR to its installation directory.");
    return 1;
}

// Build-versus-runtime PIX compatibility (version.xml and assembly file version against the
// AssemblyMetadata recorded by Directory.Build.props). Exit code 2 keeps it apart from
// discovery and option failures; PIXMCP_PIX_STRICT tunes it (see PixDiscovery.Classify).
PixCompatibility compatibility = PixDiscovery.Compatibility;
if (compatibility.Exit)
{
    Console.Error.WriteLine($"pixmcp: {compatibility.Message} (compatibility {compatibility.State}; set {PixDiscovery.StrictVariable}=0 to start anyway)");
    return 2;
}
if (compatibility.State != "match")
{
    Console.Error.WriteLine($"pixmcp: warning: {compatibility.Message} (compatibility {compatibility.State})");
}

if (ServerOptions.Current.Problems is { Count: > 0 } problems)
{
    foreach (string problem in problems) Console.Error.WriteLine("pixmcp: " + problem);
    return 1;
}

// Native PIX components print to standard output; the MCP transport gets its own copy of the handle first.
await ServerHost.RunAsync(args, ProtocolStdout.Claim(Console.Error));
return 0;
