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

    [McpServerTool(Name = "pix_dump_shader_wave_data", ReadOnly = true), Description("Pages lanes and offending HLSL locations for one dump shader wave. Follow nextCalls to retrieve every lane and location, including those truncated from pix_dump_shader_waves or triage.")]
    public static Task<string> WaveData(PixSession session, string handle, int waveIndex,
        int lanesOffset = 0, int lanesLimit = 256, int locationsOffset = 0, int locationsLimit = 32,
        bool includeLanes = true, bool includeOffendingLocations = true, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_wave_data", () =>
        {
            if (lanesOffset < 0 || locationsOffset < 0 || lanesLimit is < 1 or > 1000 || locationsLimit is < 1 or > 1000)
                throw new PixToolException("invalid_arguments", "Offsets must be nonnegative and limits must be 1 through 1000.");
            return DumpTools.WaveDto(FindWave(session.Get<DumpHandle>(handle), waveIndex), handle, waveIndex, includeLanes, includeOffendingLocations,
                lanesOffset, lanesLimit, locationsOffset, locationsLimit);
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_shader_wave", ReadOnly = true), Description("Details of one in-flight shader wave from the dump's shader debugging data (pick waveIndex from pix_dump_shader_waves): the HLSL (or IL/ISA) call stack at the instruction pointer and the variables in scope with their values (per-wave view; use pix_dump_shader_eval for per-lane values or arbitrary expressions).")]
    public static Task<string> Wave(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Wave index from pix_dump_shader_waves (offset + position).")] int waveIndex,
        [Description("HLSL (default), IL or ISA.")] string codeType = "HLSL",
        [Description("Include the call stack (default true).")] bool includeCallStack = true,
        [Description("Include variables in scope (default true).")] bool includeVariables = true,
        [Description("Maximum variables (default 25, max 1000).")] int maxVariables = 25,
        [Description("First variable index; continue with nextVariablesOffset.")] int variablesOffset = 0,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_wave", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType);
            IPixShaderWave wave = FindWave(h, waveIndex);
            int max = Math.Clamp(maxVariables, 1, Paging.MaxLimit);
            if (variablesOffset < 0) throw new PixToolException("invalid_arguments", "variablesOffset must be nonnegative.");

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
                    variablesTruncated = collection.GetCount() > (ulong)((long)variablesOffset + max);
                    return Interop.Items<IPixPostmortemShaderVariable>(collection).Skip(variablesOffset).Take(max)
                        .Select((v, index) => Variable(v, 0, handle, waveIndex, codeType, [variablesOffset + index])).ToArray();
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
                variablesOffset,
                variablesTruncated = variablesTruncated ? true : (bool?)null,
                nextVariablesOffset = variablesTruncated ? variablesOffset + max : (int?)null,
                nextCalls = variablesTruncated ? new[] { new ToolCallDto("pix_dump_shader_wave", new { handle, waveIndex, codeType, includeCallStack, includeVariables, maxVariables, variablesOffset = variablesOffset + max }) } : null,
            };
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_shader_eval", ReadOnly = true), Description("Evaluates an HLSL expression (a variable name, member or array access, e.g. 'index', 'input.uv.x', 'buffer[3]') in the context of one hung shader wave from the dump and returns its value for every lane in laneMask, plus components for vectors/structs. Use pix_dump_shader_wave to see the variables in scope first.")]
    public static Task<string> Evaluate(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Wave index from pix_dump_shader_waves.")] int waveIndex,
        [Description("Expression to evaluate in the shader's scope at the instruction pointer.")] string expression,
        [Description("HLSL (default), IL or ISA.")] string codeType = "HLSL",
        [Description("Decimal or hexadecimal bit mask of lanes to evaluate (default all 64); a string preserves all bits in JavaScript clients.")] string laneMask = "0xffffffffffffffff",
        [Description("Component-index path returned by a previous evaluation; omit for the expression itself.")] int[]? componentPath = null,
        [Description("First component index at the selected value.")] int componentsOffset = 0,
        [Description("Maximum components at the selected value, 1 through 1000.")] int componentsLimit = 25,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_eval", () =>
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new McpException("An expression is required.");
            }
            if (componentPath?.Any(i => i < 0) == true || componentsOffset < 0 || componentsLimit is < 1 or > 1000)
                throw new PixToolException("invalid_arguments", "Component path and offset must be nonnegative, and componentsLimit 1 through 1000.");
            DumpHandle h = session.Get<DumpHandle>(handle);
            PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType);
            IPixShaderWave wave = FindWave(h, waveIndex);
            ulong mask = Tools.ParseId(laneMask, "laneMask");
            IPixPostmortemShaderVariableWithLaneValues? value = PixApiExtensionsShaderDebugging.TryEvaluateExpression(wave, expression, type, mask, out Exception ex);
            if (value is null)
            {
                throw new McpException($"PIX could not evaluate '{expression}': {(ex is null ? "no result" : PixErrors.Describe(ex))}");
            }
            foreach (int index in componentPath ?? [])
            {
                IPixCollection? components = LaneComponents(value);
                if (components is null || (ulong)index >= components.GetCount()) throw new PixToolException("invalid_reference", "componentPath is out of range.");
                value = components.Get<IPixPostmortemShaderVariableWithLaneValues>((ulong)index);
            }
            return new { waveIndex, expression, codeType = type, laneMask = Interop.Hex(mask),
                result = LaneVariable(value, 0, handle, waveIndex, expression, codeType, mask, componentPath ?? [], componentsOffset, componentsLimit) };
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

    private static object Variable(IPixPostmortemShaderVariable variable, int depth, string handle, int waveIndex, string codeType, int[] variablePath, int componentsOffset = 0, int componentsLimit = MaxComponents)
    {
        object? components = null;
        ulong? componentCount = null;
        bool truncated = false;
        components = Tools.Try(() =>
        {
            IPixCollection? collection = VariableComponents(variable);
            componentCount = collection?.GetCount() ?? 0;
            if (collection is null || componentCount == 0) return null;
            truncated = depth >= MaxComponentDepth || componentCount > (ulong)((long)componentsOffset + componentsLimit);
            if (depth >= MaxComponentDepth) return null;
            return Interop.Items<IPixPostmortemShaderVariable>(collection).Skip(componentsOffset).Take(componentsLimit)
                .Select((c, index) => Variable(c, depth + 1, handle, waveIndex, codeType, [.. variablePath, componentsOffset + index])).ToArray();
        }, "components");
        return new
        {
            variablePath,
            name = Interop.WOrNull(variable.GetName()),
            location = Interop.WOrNull(variable.GetLocation()),
            value = Tools.Try(() => Interop.Value(Internal_IPixPostmortemShaderVariable_Extensions.GetValue(variable)), "value"),
            components,
            componentCount,
            nextCalls = truncated ? new[] { new ToolCallDto("pix_dump_shader_variable", new { handle, waveIndex, codeType, variablePath, offset = depth >= MaxComponentDepth ? 0 : componentsOffset + componentsLimit }) } : null,
        };
    }

    private static IPixCollection? VariableComponents(IPixPostmortemShaderVariable variable)
    {
        Guid guid = typeof(IPixCollection).GUID;
        Internal_IPixPostmortemShaderVariable_Extensions.GetComponents(variable, in guid, out object value);
        return value as IPixCollection;
    }

    [McpServerTool(Name = "pix_dump_shader_variable", ReadOnly = true), Description("Retrieves a dump shader variable and pages its components using the variablePath from pix_dump_shader_wave. Each child retains its full variablePath; follow nextCalls to retrieve omitted components.")]
    public static Task<string> VariableDetails(PixSession session, string handle, int waveIndex, int[] variablePath,
        string codeType = "HLSL", int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_variable", () =>
        {
            if (variablePath is not { Length: > 0 } || variablePath.Any(i => i < 0) || offset < 0 || limit is < 1 or > 1000)
                throw new PixToolException("invalid_arguments", "variablePath must be nonempty and nonnegative; offset must be nonnegative and limit 1 through 1000.");
            IPixShaderWave wave = FindWave(session.Get<DumpHandle>(handle), waveIndex);
            IPixCollection? values = PixApiExtensionsShaderDebugging.TryGetVariables(wave, Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType), out _);
            IPixPostmortemShaderVariable? variable = null;
            for (int depth = 0; depth < variablePath.Length; depth++)
            {
                if (values is null || (ulong)variablePath[depth] >= values.GetCount()) throw new PixToolException("invalid_reference", "variablePath is out of range.");
                variable = values.Get<IPixPostmortemShaderVariable>((ulong)variablePath[depth]);
                if (depth + 1 < variablePath.Length) values = VariableComponents(variable);
            }
            return Variable(variable!, MaxComponentDepth - 1, handle, waveIndex, codeType, variablePath, offset, limit);
        }, cancellationToken);

    private static object LaneVariable(IPixPostmortemShaderVariableWithLaneValues variable, int depth,
        string handle, int waveIndex, string expression, string codeType, ulong laneMask, int[] componentPath, int componentsOffset = 0, int componentsLimit = MaxComponents)
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
        ulong? componentCount = null;
        bool truncated = false;
        components = Tools.Try(() =>
        {
            IPixCollection? collection = LaneComponents(variable);
            componentCount = collection?.GetCount() ?? 0;
            if (collection is null || componentCount == 0) return null;
            truncated = depth >= MaxComponentDepth || componentCount > (ulong)((long)componentsOffset + componentsLimit);
            if (depth >= MaxComponentDepth) return null;
            return Interop.Items<IPixPostmortemShaderVariableWithLaneValues>(collection).Skip(componentsOffset).Take(componentsLimit)
                .Select((c, index) => LaneVariable(c, depth + 1, handle, waveIndex, expression, codeType, laneMask, [.. componentPath, componentsOffset + index])).ToArray();
        }, "components");
        return new
        {
            componentPath,
            name = Interop.WOrNull(variable.GetName()),
            location = Interop.WOrNull(variable.GetLocation()),
            laneCount = count,
            lanes,
            components,
            componentCount,
            nextCalls = truncated ? new[] { new ToolCallDto("pix_dump_shader_eval", new { handle, waveIndex, expression, codeType, laneMask = Interop.Hex(laneMask),
                componentPath, componentsOffset = depth >= MaxComponentDepth ? 0 : componentsOffset + componentsLimit, componentsLimit }) } : null,
        };
    }

    private static IPixCollection? LaneComponents(IPixPostmortemShaderVariableWithLaneValues variable)
    {
        Guid guid = typeof(IPixCollection).GUID;
        Internal_IPixPostmortemShaderVariableWithLaneValues_Extensions.GetComponents(variable, in guid, out object value);
        return value as IPixCollection;
    }
}
