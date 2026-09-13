using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// Test classes that replay captures on the local GPU. PIX fails a replay that starts while another process (the stdio server
/// under test) or session replays at the same time, so these classes never run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GpuReplayCollection
{
    public const string Name = "GPU replay";
}
