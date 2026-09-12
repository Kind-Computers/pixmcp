using PixMcp.Tools;

namespace PixMcp.Pix.Handles;

public sealed partial class GpuCaptureHandle
{
    internal ShaderIndex? ShaderIndex { get; set; }

    internal static Preparation<GpuCaptureHandle> ShaderIndexPreparation(string handle)
        => new("shader-index", "shader-index", $"Index shaders in {handle}", h => h.ShaderIndex is not null,
            (h, job) =>
            {
                h.EnsureAnalysisStarted(job);
                h.ShaderIndex ??= ShaderInventoryTools.BuildIndex(h, job);
            });
}
