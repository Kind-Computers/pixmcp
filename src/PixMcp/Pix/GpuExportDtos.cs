namespace PixMcp.Pix;

public sealed record GpuExportOptionsDto(bool UseWinPixEventRuntime, bool UseAgilitySdk,
    bool UseReplayTimeExecuteIndirectBuffers);

public sealed record GpuExportResultDto(string Handle, string CapturePath, string OutputDirectory,
    string CMakePath, string Backend, string? RuntimeVersion, GpuExportOptionsDto Options);
