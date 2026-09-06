using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.PostmortemDump;
using Microsoft.PIX.Internal.Extension.ShaderDebugging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>Shader debugging data of a DirectX dump: variables, call stacks and expression evaluation for a hung wave. Experimental PIX API surface.</summary>
[McpServerToolType]
public static class DumpShaderTools
{
    private const int MaxComponentDepth = 3;
    private const int MaxComponents = 64;

    [McpServerTool(Name = "pix_dump_shader_wave", ReadOnly = true), Description("Details of one in-flight shader wave from the dump's shader debugging data (pick waveIndex from pix_dump_shader_waves): the HLSL (or IL/ISA) call stack at the instruction pointer and the variables in scope with their values (per-wave view; use pix_dump_shader_eval for per-lane values or arbitrary expressions).")]
    public static Task<string> Wave(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Wave index from pix_dump_shader_waves (offset + position).")] int waveIndex,
        [Description("HLSL (default), IL or ISA.")] string codeType = "HLSL",
        [Description("Include the call stack (default true).")] bool includeCallStack = true,
        [Description("Include variables in scope (default true).")] bool includeVariables = true,
        [Description("Maximum variables (default 200, max 1000).")] int maxVariables = 200,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_wave", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType);
            IPixShaderWave wave = FindWave(h, waveIndex);
            int max = Math.Clamp(maxVariables, 1, Paging.MaxLimit);

            object? callStack = !includeCallStack ? null : Tools.Try(() =>
                PixApiExtensionsShaderDebugging.GetCallStackEntries(wave, type).Select(CodeLocation).ToArray(), "callStack");

            object? variables = null;
            bool variablesTruncated = false;
            if (includeVariables)
            {
                variables = Tools.Try(() =>
                {
                    IPixCollection? collection = PixApiExtensionsShaderDebugging.TryGetVariables(wave, type, out Exception ex);
                    if (collection is null)
                    {
                        return ex is null ? null : PixErrors.Unavailable("variables", ex);
                    }
                    variablesTruncated = collection.GetCount() > (ulong)max;
                    return Interop.Items<IPixPostmortemShaderVariable>(collection).Take(max).Select(v => Variable(v, 0)).ToArray();
                }, "variables");
            }

            return new
            {
                waveIndex,
                id = wave.GetId(),
                stage = wave.GetStage(),
                status = wave.GetStatus(),
                coordinates = Interop.WOrNull(wave.GetCoordinates()),
                exceptionsHit = wave.GetExceptionsHit(),
                codeType = type,
                callStack,
                variables,
                variablesTruncated = variablesTruncated ? true : (bool?)null,
            };
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_shader_eval", ReadOnly = true), Description("Evaluates an HLSL expression (a variable name, member or array access, e.g. 'index', 'input.uv.x', 'buffer[3]') in the context of one hung shader wave from the dump and returns its value for every lane in laneMask, plus components for vectors/structs. Use pix_dump_shader_wave to see the variables in scope first.")]
    public static Task<string> Evaluate(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Wave index from pix_dump_shader_waves.")] int waveIndex,
        [Description("Expression to evaluate in the shader's scope at the instruction pointer.")] string expression,
        [Description("HLSL (default), IL or ISA.")] string codeType = "HLSL",
        [Description("Bit mask of lanes to evaluate for (default all 64).")] ulong laneMask = ulong.MaxValue,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_eval", () =>
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new McpException("An expression is required.");
            }
            DumpHandle h = session.Get<DumpHandle>(handle);
            PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType);
            IPixShaderWave wave = FindWave(h, waveIndex);
            IPixPostmortemShaderVariableWithLaneValues? value = PixApiExtensionsShaderDebugging.TryEvaluateExpression(wave, expression, type, laneMask, out Exception ex);
            if (value is null)
            {
                throw new McpException($"PIX could not evaluate '{expression}': {(ex is null ? "no result" : PixErrors.Describe(ex))}");
            }
            return new { waveIndex, expression, codeType = type, laneMask = Interop.Hex(laneMask), result = LaneVariable(value, 0) };
        }, cancellationToken);

    private static IPixShaderWave FindWave(DumpHandle h, int waveIndex)
    {
        IPixShaderDebuggingData data = PixApiExtensionsPostmortemDump.TryGetShaderData(h.Document, out Exception ex)
            ?? throw new McpException(ex is null ? "This dump has no shader debugging data." : $"Shader debugging data is unavailable: {PixErrors.Describe(ex)}");
        IPixCollection waves = PixApiExtensionsShaderDebugging.TryGetWaves(data, out Exception wex)
            ?? throw new McpException(wex is null ? "The shader debugging data lists no waves." : $"Waves are unavailable: {PixErrors.Describe(wex)}");
        ulong count = waves.GetCount();
        if (waveIndex < 0 || (ulong)waveIndex >= count)
        {
            throw new McpException($"waveIndex {waveIndex} is out of range; the dump has {count} wave(s) (see pix_dump_shader_waves).");
        }
        return waves.Get<IPixShaderWave>((ulong)waveIndex);
    }

    private static object CodeLocation(IPixShaderCodeLocation location) => new
    {
        codeType = location.GetCodeType(),
        line = location.GetLineNumber(),
        statement = Interop.WOrNull(location.GetStatement()),
        node = Tools.Try(() =>
        {
            Guid guid = typeof(IPixShaderNode).GUID;
            Internal_IPixShaderCodeLocation_Extensions.GetNode(location, in guid, out object nodeObject);
            return nodeObject is IPixShaderNode node ? new { id = node.GetId(), name = Interop.WOrNull(node.GetName()) } : null;
        }, "node"),
    };

    private static object Variable(IPixPostmortemShaderVariable variable, int depth)
    {
        object? components = null;
        if (depth < MaxComponentDepth)
        {
            components = Tools.Try(() =>
            {
                Guid guid = typeof(IPixCollection).GUID;
                Internal_IPixPostmortemShaderVariable_Extensions.GetComponents(variable, in guid, out object collectionObject);
                if (collectionObject is not IPixCollection collection || collection.GetCount() == 0) return null;
                return Interop.Items<IPixPostmortemShaderVariable>(collection).Take(MaxComponents).Select(c => Variable(c, depth + 1)).ToArray();
            }, "components");
        }
        return new
        {
            name = Interop.WOrNull(variable.GetName()),
            location = Interop.WOrNull(variable.GetLocation()),
            value = Tools.Try(() => Interop.Value(Internal_IPixPostmortemShaderVariable_Extensions.GetValue(variable)), "value"),
            components,
        };
    }

    private static object LaneVariable(IPixPostmortemShaderVariableWithLaneValues variable, int depth)
    {
        var lanes = new List<object>();
        ulong count = variable.GetCount();
        Guid laneGuid = typeof(IPixPostmortemShaderVariableLaneValue).GUID;
        for (ulong i = 0; i < count; i++)
        {
            object? lane = Tools.Try(() =>
            {
                Internal_IPixPostmortemShaderVariableWithLaneValues_Extensions.Get(variable, i, in laneGuid, out object laneObject);
                var laneValue = (IPixPostmortemShaderVariableLaneValue)laneObject;
                return new { lane = laneValue.GetLaneIndex(), value = Interop.Value(Internal_IPixPostmortemShaderVariableLaneValue_Extensions.GetValue(laneValue)) };
            }, $"lane[{i}]");
            lanes.Add(lane!);
        }
        object? components = null;
        if (depth < MaxComponentDepth)
        {
            components = Tools.Try(() =>
            {
                Guid guid = typeof(IPixCollection).GUID;
                Internal_IPixPostmortemShaderVariableWithLaneValues_Extensions.GetComponents(variable, in guid, out object collectionObject);
                if (collectionObject is not IPixCollection collection || collection.GetCount() == 0) return null;
                return Interop.Items<IPixPostmortemShaderVariableWithLaneValues>(collection).Take(MaxComponents).Select(c => LaneVariable(c, depth + 1)).ToArray();
            }, "components");
        }
        return new
        {
            name = Interop.WOrNull(variable.GetName()),
            location = Interop.WOrNull(variable.GetLocation()),
            laneCount = count,
            lanes,
            components,
        };
    }
}
