using System.ComponentModel;
using System.Globalization;
using Microsoft.PIX;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.Shaders;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ShaderDiagnosticsTools
{
    [McpServerTool(Name = "pix_gpu_shader_diagnostics", ReadOnly = true), Description("Checks one shader's recorded PDB hash and HLSL/IL/ISA source-node availability without retrieving code text or scanning other shaders. Reports absent metadata separately from native query failures. A PDB hash identifies symbols; it does not prove PIX resolved a matching PDB. Does not configure symbol paths or load PDB files. Requires GPU analysis, prepared as a shared job.")]
    public static Task<string> Diagnostics(PixSession session, JobManager jobs,
        [Description("Shader reference returned by pipeline inspection or shader inventory.")] ShaderRef shaderRef,
        [Description("Nonempty subset of HLSL, IL, ISA; omitted checks all three. Duplicate kinds are checked once.")] ShaderCodeKind[]? codeTypes = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Shader(session, shaderRef);
        ShaderCodeKind[] kinds = NormalizeCodeTypes(codeTypes);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shader_diagnostics", shaderRef.EventRef.Handle,
            GpuCaptureHandle.AnalysisPreparation(shaderRef.EventRef.Handle), h =>
            {
                IPixShader shader = PipelineTools.ReadShader(h, shaderRef);
                return Probe(shaderRef, shader.GetId().ToString(CultureInfo.InvariantCulture), Json.EnumName(shader.GetStage()),
                    kinds, () => ReadPdbHash(shader), kind => ReadNodeCount(shader, kind), cancellationToken);
            }, waitSeconds, cancellationToken);
    }

    internal static ShaderCodeKind[] NormalizeCodeTypes(ShaderCodeKind[]? codeTypes)
    {
        if (codeTypes is null) return [ShaderCodeKind.HLSL, ShaderCodeKind.IL, ShaderCodeKind.ISA];
        if (codeTypes.Length == 0 || codeTypes.Any(kind => !Enum.IsDefined(kind)))
            throw new PixToolException("invalid_arguments", "codeTypes must be a nonempty subset of HLSL, IL, ISA.");
        return codeTypes.Distinct().ToArray();
    }

    private static byte[] ReadPdbHash(IPixShader shader)
    {
        byte[] hash = new byte[checked((int)shader.GetPdbHashSizeBytes())];
        if (hash.Length > 0) Internal_IPixShader_Extensions.GetPdbHash(shader, hash);
        return hash;
    }

    private static ulong ReadNodeCount(IPixShader shader, ShaderCodeKind kind)
    {
        PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(kind.ToString());
        IPixCollection? nodes = PixApiExtensionsShaders.TryGetNodes(shader, type, out Exception? error);
        if (nodes is null && error is not null) throw error;
        return nodes?.GetCount() ?? 0;
    }

    internal static ShaderDiagnosticsDto Probe(ShaderRef shaderRef, string id, string stage,
        IReadOnlyList<ShaderCodeKind> codeTypes, Func<byte[]?> readPdbHash, Func<ShaderCodeKind, ulong> readNodeCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShaderPdbMetadataDto pdb;
        try
        {
            byte[]? hash = readPdbHash();
            pdb = hash is { Length: > 0 }
                ? new("available", Convert.ToHexString(hash).ToLowerInvariant())
                : new("absent", null, "PIX reports no recorded PDB hash for this shader.");
        }
        catch (Exception ex) when (!IsCancellation(ex))
        {
            ErrorDto error = PixErrors.ToDto(ex);
            pdb = new("unavailable", null, error.Message, error.Code, error.Hresult);
        }

        var availability = new List<ShaderCodeAvailabilityDto>();
        var nextCalls = new List<ToolCallDto>();
        foreach (ShaderCodeKind kind in codeTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string codeType = kind.ToString();
            try
            {
                ulong count = readNodeCount(kind);
                availability.Add(new(codeType, count > 0 ? "available" : "absent", count,
                    count > 0 ? null : "PIX reports no source nodes for this code kind."));
                if (count > 0) nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef, codeType, nodeIndex = -1 }));
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                ErrorDto error = PixErrors.ToDto(ex);
                availability.Add(new(codeType, "unavailable", null, error.Message, error.Code, error.Hresult));
            }
        }
        var guidance = new List<string>
        {
            "A recorded PDB hash identifies shader symbols; it does not confirm a matching PDB was found or loaded.",
            "Available means PIX returned source nodes. Use the follow-up code tool to retrieve their text.",
        };
        if (availability.Any(code => code.CodeType == "HLSL" && code.State != "available"))
            guidance.Add("Missing HLSL does not identify a single cause. Check shader compilation debug information and PIX shader symbol configuration; the API does not provide a general GPU PDB path resolver.");
        return new(shaderRef, id, stage, pdb, availability, guidance, nextCalls);
    }

    private static bool IsCancellation(Exception exception)
        => exception is OperationCanceledException || PixErrors.IsCancellationHResult(PixErrors.HResultOf(exception) ?? 0);
}
