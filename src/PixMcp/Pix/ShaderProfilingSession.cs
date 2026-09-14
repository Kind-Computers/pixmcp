using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.ShaderProfiling;
using Microsoft.PIX.Internal.Extension.ShaderProfiling.Types;
using PixMcp.Pix.StaticProfiling;
using Wrappers = Microsoft.PIX.Internal.Extension.PipelineState.Types;

namespace PixMcp.Pix;

/// <summary>A shader ready for the profiling API: stage, profile, entry, compiler flags and arguments, files with the main file first.</summary>
internal sealed record StaticCompileSource(PIX_SHADER_STAGE Stage, string Target, string Entry, string Flags, string Defines, IReadOnlyList<StaticSourceFile> Files);

/// <summary>
/// Static shader profiling state of a session: the PIX shader profiling document, its targets and the probed defines format.
/// Everything except the job caches runs on the PIX worker.
/// </summary>
internal sealed class ShaderProfilingSession
{
    private readonly PixSession _session;
    private IPixShaderProfilingDocument? _document;
    private List<IPixShaderProfilingAdapter>? _adapters;
    private IReadOnlyList<ShaderTargetDto>? _targets;
    private StaticDefinesFormatDto? _definesFormat;

    public ShaderProfilingSession(PixSession session) => _session = session;

    /// <summary>Identical inline requests join the running or retained job.</summary>
    public StaticProfileJobCache InlineJobs { get; } = new();

    private IPixShaderProfilingDocument Document
    {
        get
        {
            if (_document is not null) return _document;
            try { using (ServerPaths.CompilerWorkingDirectory()) _document = PixApiExtensionsShaderProfiling.OpenShaderProfilingDocument(_session.Factory); }
            catch (InvalidCastException ex)
            {
                throw new PixToolException(PixErrors.Codes.PixApiMismatch, "The PIX factory does not expose shader profiling documents: " + PixErrors.Describe(ex), false,
                    [new ToolCallDto("pix_info", new { probe = true }, CostHints.Query)]);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new PixToolException(PixErrors.Codes.PreparationFailed, "PIX could not open a shader profiling document: " + PixErrors.Describe(ex), false,
                    [new ToolCallDto("pix_info", new { probe = true }, CostHints.Query), new ToolCallDto("pix_log", new { }, CostHints.Cached)], ex.HResult);
            }
            return _document;
        }
    }

    /// <summary>Every AMD and Intel target of the install, in id order (worker only).</summary>
    public IReadOnlyList<ShaderTargetDto> Targets
    {
        get
        {
            if (_targets is not null) return _targets;
            List<IPixShaderProfilingAdapter> adapters;
            using (ServerPaths.CompilerWorkingDirectory()) adapters = PixApiExtensionsShaderProfiling.GetAdapters(Document);
            _targets = adapters.Select(adapter =>
            {
                string name = Interop.WOrNull(adapter.GetName()) ?? $"adapter {adapter.GetId()}";
                string family = Interop.WOrNull(adapter.GetFamilyName()) ?? name;
                string? description = Interop.WOrNull(adapter.GetDescription());
                GpuVendor vendor = GpuVendors.FromVendorName(Interop.WOrNull(adapter.GetVendorName()));
                return new ShaderTargetDto(adapter.GetId().ToString(System.Globalization.CultureInfo.InvariantCulture), adapter.GetId(), GpuVendors.Name(vendor), name,
                    description == name ? null : description, family, adapter.GetFamilyId(), StaticTargets.Architecture(name, family));
            }).OrderBy(t => t.Id).ToArray();
            _adapters = adapters;
            return _targets;
        }
    }

    /// <summary>Whether a -D argument reaches the preprocessor, probed once with a define-dependent source.</summary>
    public StaticDefinesFormatDto DefinesFormat
    {
        get
        {
            if (_definesFormat is not null) return _definesFormat;
            bool verified = false;
            try
            {
                var probe = new PixShaderProfilingSourceShader
                {
                    Stage = PIX_SHADER_STAGE.PIX_SHADER_STAGE_COMPUTE,
                    Target = "cs_6_0",
                    Entry = "main",
                    Flags = "",
                    Defines = "-DPIXMCP_DEFINES_PROBE=1",
                    SourcePaths = new List<string> { "pixmcp-defines-probe.hlsl" },
                    SourceContents = new List<string> { "#ifdef PIXMCP_DEFINES_PROBE\nstatic const int pixmcp_probe_defined = 1;\n#endif\n[numthreads(1, 1, 1)] void main() {}\n" },
                };
                IPixShaderProfilingPreprocess result;
                using (ServerPaths.CompilerWorkingDirectory()) result = PixApiExtensionsShaderProfiling.Preprocess(Document, probe);
                bool succeeded = result.Succeeded();
                verified = succeeded && Interop.W(result.GetText()).Contains("pixmcp_probe_defined", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not PixToolException) { }
            return _definesFormat = new(StaticSourcePolicy.DefinesFormat, verified);
        }
    }

    /// <summary>Preprocesses every source, compiles them for the target and snapshots the result (worker only).</summary>
    public StaticCompileOutcome Compile(uint adapterId, Func<PixShaderProfilingPipelineState> pipeline, IReadOnlyList<StaticCompileSource> sources, PixApplicationDesc? appDesc, Job job)
    {
        long started = Stopwatch.GetTimestamp();
        double Elapsed() => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _ = Targets;
        IPixShaderProfilingAdapter adapter = _adapters!.FirstOrDefault(a => a.GetId() == adapterId)
            ?? throw PixErrors.InvalidArguments($"Shader target {adapterId} is not available.", [new ToolCallDto("pix_shader_targets", new { }, CostHints.Query)]);
        string directory = Path.Combine(Path.GetTempPath(), "pixmcp-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Vendor compiler plugins write report files into the working directory.
        using IDisposable compilerDirectory = ServerPaths.CompilerWorkingDirectory();
        try
        {
            var shaders = new List<PixShaderProfilingSourceShader>(sources.Count);
            for (int i = 0; i < sources.Count; i++)
            {
                StaticCompileSource source = sources[i];
                var paths = new List<string>();
                var contents = new List<string>();
                foreach (StaticSourceFile file in source.Files)
                {
                    string full = Path.Combine(directory, $"s{i}", StaticSourcePolicy.SafeRelativePath(file.Path));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, file.Content);
                    paths.Add(full);
                    contents.Add(file.Content);
                }
                shaders.Add(new PixShaderProfilingSourceShader
                {
                    Stage = source.Stage, Target = source.Target, Entry = source.Entry, Flags = source.Flags, Defines = source.Defines,
                    SourcePaths = paths, SourceContents = contents,
                });
            }
            for (int i = 0; i < shaders.Count; i++)
            {
                job.ThrowIfCancellationRequested();
                IPixShaderProfilingPreprocess preprocess = PixApiExtensionsShaderProfiling.Preprocess(Document, shaders[i]);
                bool preprocessed = preprocess.Succeeded();
                if (!preprocessed)
                    return new(false, "preprocess", PixApiExtensionsShaderProfiling.GetCompilerOutput(preprocess)
                        .Select(text => new CompilerMessageDto(Json.EnumName(sources[i].Stage), Clean(text, directory))).ToArray(), null, Elapsed());
            }
            job.ThrowIfCancellationRequested();
            job.AddMessage($"Compiling {shaders.Count} shader(s) for {Interop.WOrNull(adapter.GetName())}...");
            IPixShaderProfilingCompilation compilation = PixApiExtensionsShaderProfiling.Compile(Document, adapter, pipeline(), shaders, appDesc!, job.Sink, null!);
            CompilerMessageDto[] messages = Enum.GetValues<PIX_SHADER_STAGE>()
                .SelectMany(stage => compilation.GetCompilerOutputCount(stage) == 0
                    ? Enumerable.Empty<CompilerMessageDto>()
                    : PixApiExtensionsShaderProfiling.GetCompilerOutput(compilation, stage).Select(text => new CompilerMessageDto(Json.EnumName(stage), Clean(text, directory))))
                .ToArray();
            bool compiled = compilation.Succeeded();
            if (!compiled) return new(false, "compile", messages, null, Elapsed());
            IPixShaderProfilingStaticResult result = PixApiExtensionsShaderProfiling.GetResult(compilation);
            return new(true, null, messages, Snapshot(result, directory, job), Elapsed());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes the scratch directory prefix (and the per-source folder) from compiler text and source paths.</summary>
    internal static string Clean(string text, string directory)
        => Regex.Replace(text ?? "", Regex.Escape(directory) + @"[\\/]s\d+[\\/]", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static PIX_SHADER_STAGE Stage(string lowerCaseStage) => Enum.Parse<PIX_SHADER_STAGE>("PIX_SHADER_STAGE_" + lowerCaseStage.ToUpperInvariant());

    internal static bool TryStage(string? captureStage, out PIX_SHADER_STAGE stage)
    {
        stage = default;
        return captureStage is not null && Enum.TryParse("PIX_SHADER_STAGE_" + captureStage.Trim().ToUpperInvariant(), out stage) && stage != PIX_SHADER_STAGE.PIX_SHADER_STAGE_UNKNOWN;
    }

    internal static bool TryFormat(string name, out global::Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT format)
    {
        format = default;
        return !string.IsNullOrWhiteSpace(name) && !char.IsDigit(name.Trim()[0])
            && Enum.TryParse("DXGI_FORMAT_" + name.Trim().ToUpperInvariant(), out format) && Enum.IsDefined(format);
    }

    /// <summary>PIX's default pipeline state for the plan's type with the plan's topology, formats and samples; the empty root signature goes when a source declares one.</summary>
    internal static PixShaderProfilingPipelineState BuildPipeline(StaticInlinePlan plan)
    {
        PIX_GENERIC_PIPELINE_TYPE type = plan.PipelineType switch
        {
            "compute" => PIX_GENERIC_PIPELINE_TYPE.PIX_GENERIC_PIPELINE_TYPE_COMPUTE,
            "mesh" => PIX_GENERIC_PIPELINE_TYPE.PIX_GENERIC_PIPELINE_TYPE_MESH,
            _ => PIX_GENERIC_PIPELINE_TYPE.PIX_GENERIC_PIPELINE_TYPE_GRAPHICS,
        };
        var state = new PixShaderProfilingPipelineState(type);
        if (plan.DropRootSignature) state.Subobjects.RemoveAll(s => s is Wrappers.D3D12_VERSIONED_ROOT_SIGNATURE_DESC);
        if (plan.PipelineType == "compute") return state;
        foreach (Wrappers.IStateSubobject subobject in state.Subobjects)
        {
            switch (subobject)
            {
                case Wrappers.D3D12_PRIMITIVE_TOPOLOGY_TYPE topology:
                    topology.Value = plan.Topology switch
                    {
                        "point" => global::Microsoft.PIX.D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT,
                        "line" => global::Microsoft.PIX.D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_LINE,
                        "patch" => global::Microsoft.PIX.D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_PATCH,
                        _ => global::Microsoft.PIX.D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
                    };
                    break;
                case Wrappers.D3D12_RT_FORMAT_ARRAY formats:
                    formats.Formats ??= new();
                    formats.Formats.Clear();
                    foreach (string name in plan.RenderTargetFormats)
                        if (TryFormat(name, out global::Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT format)) formats.Formats.Add(format);
                    break;
                case Wrappers.DEPTH_STENCIL_FORMAT depth when plan.DepthStencilFormat is not null && TryFormat(plan.DepthStencilFormat, out global::Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT depthFormat):
                    depth.Format = depthFormat;
                    break;
                case Wrappers.SAMPLE_DESC sample:
                    sample.Count = plan.SampleCount;
                    break;
            }
        }
        return state;
    }

    /// <summary>Copies every value of a static result into managed records (worker only).</summary>
    internal static StaticResultData Snapshot(IPixShaderProfilingStaticResult result, string directory, Job? job)
    {
        uint categoryCount = result.GetInstructionCategoryCount();
        var categories = new List<StaticNamedItem>((int)categoryCount);
        for (uint i = 0; i < categoryCount; i++)
        {
            PIX_SHADER_PROFILING_INSTRUCTION_CATEGORY category = default;
            Internal_IPixShaderProfilingStaticResult_Extensions.GetInstructionCategory(result, i, ref category);
            categories.Add(new(category.Id, Interop.WOrNull(category.Name) ?? $"category {category.Id}", Interop.WOrNull(category.Description)));
        }
        StaticInstructionTypeInfo[] types = PixApiExtensionsShaderProfiling.GetInstructionTypes(result)
            .Select(t => new StaticInstructionTypeInfo(t.Id, t.Category, Interop.WOrNull(t.Name) ?? $"type {t.Id}", Interop.WOrNull(t.Description), t.Cycles)).ToArray();
        StaticRegisterTypeInfo[] registerTypes = PixApiExtensionsShaderProfiling.GetRegisterTypes(result)
            .Select(t => new StaticRegisterTypeInfo(t.Id, Interop.WOrNull(t.Name) ?? $"register type {t.Id}", Interop.WOrNull(t.Prefix))).ToArray();
        StaticNamedItem[] branchTypes = PixApiExtensionsShaderProfiling.GetBranchTypes(result)
            .Select(t => new StaticNamedItem(t.Id, Interop.WOrNull(t.Name) ?? $"branch {t.Id}")).ToArray();
        StaticNamedItem[] dependencyTypes = PixApiExtensionsShaderProfiling.GetDependencyTypes(result)
            .Select(t => new StaticNamedItem(t.Id, Interop.WOrNull(t.Name) ?? $"dependency {t.Id}", Interop.WOrNull(t.Description))).ToArray();
        string? frontend = null, backend = null;
        try { frontend = Interop.WOrNull(PixApiExtensionsShaderProfiling.GetFrontendCompiler(result).GetLocation()); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        try { backend = Interop.WOrNull(PixApiExtensionsShaderProfiling.GetBackendCompiler(result).GetLocation()); } catch (Exception ex) when (ex is not OperationCanceledException) { }

        var shaders = new List<StaticShaderData>();
        foreach (IPixShaderProfilingStaticShader shader in PixApiExtensionsShaderProfiling.GetShaders(result))
        {
            job?.ThrowIfCancellationRequested();
            byte[]? hash = null;
            try { hash = PixApiExtensionsShaderProfiling.GetHash(shader); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            bool placeholder = StaticTargets.IsPlaceholderHash(hash);
            StaticInfoDto[] infos = PixApiExtensionsShaderProfiling.GetInfos(shader)
                .Select(i => new StaticInfoDto(Interop.WOrNull(i.Name) ?? "", Interop.WOrNull(i.Value) ?? "")).ToArray();
            StaticNamedItem[] files = PixApiExtensionsShaderProfiling.GetSourceFiles(shader)
                .Select(f => new StaticNamedItem(f.Id, Clean(Interop.WOrNull(f.Path) ?? "", directory))).ToArray();
            StaticSourceFunctionInfo[] functions = PixApiExtensionsShaderProfiling.GetSourceFunctions(shader)
                .Select(f => new StaticSourceFunctionInfo(f.Id, Interop.WOrNull(f.Name) ?? "", f.FileId, f.Line, f.EndLine)).ToArray();
            StaticSourceLocationInfo[] locations = PixApiExtensionsShaderProfiling.GetSourceLocations(shader)
                .Select(l => new StaticSourceLocationInfo(l.Id, l.FileId, l.Line, l.EndLine, l.Column, l.EndColumn, l.FunctionId, l.CallingLocationId)).ToArray();
            StaticRegisterInfo[] registers = PixApiExtensionsShaderProfiling.GetRegisters(shader)
                .Select(r => new StaticRegisterInfo(r.Id, Interop.WOrNull(r.Name) ?? $"#{r.Id}", r.Type, r.Ordinal)).ToArray();
            var allocated = registerTypes.ToDictionary(t => t.Id, t => shader.GetRegisterAllocatedCount(t.Id));
            var blocks = new List<StaticBlockData>();
            foreach (IPixShaderProfilingStaticBlock block in PixApiExtensionsShaderProfiling.GetBlocks(shader))
            {
                job?.ThrowIfCancellationRequested();
                uint successorCount = block.GetSuccessorCount();
                var branches = new uint[successorCount];
                for (uint k = 0; k < successorCount; k++) block.GetSuccessorBranchType(k, ref branches[k]);
                StaticInstructionData[] instructions = PixApiExtensionsShaderProfiling.GetInstructions(block).Select(instruction => new StaticInstructionData(
                    instruction.GetId(), instruction.GetType(), Interop.WOrNull(instruction.GetText()) ?? "", instruction.GetOffsetBytes(), instruction.GetSizeBytes(),
                    instruction.GetSourceLocation(), Interop.WOrNull(instruction.GetPredicate())?.Trim() is { Length: > 0 } predicate ? predicate : null,
                    PixApiExtensionsShaderProfiling.GetRegistersRead(instruction), PixApiExtensionsShaderProfiling.GetRegistersWritten(instruction),
                    PixApiExtensionsShaderProfiling.GetRegistersLive(instruction), PixApiExtensionsShaderProfiling.GetRegistersFreed(instruction),
                    PixApiExtensionsShaderProfiling.GetDependencies(instruction).Select(d => new StaticDependency(d.Type, d.Instruction)).ToArray(),
                    PixApiExtensionsShaderProfiling.GetComments(instruction).Select(c => new StaticComment(Severity(c.Type), Interop.WOrNull(c.Text) ?? "")).ToArray())).ToArray();
                blocks.Add(new(block.GetId(), Interop.WOrNull(block.GetName()) is { Length: > 0 } blockName ? blockName : null, PixApiExtensionsShaderProfiling.GetSuccessors(block), branches,
                    PixApiExtensionsShaderProfiling.GetRegistersLiveIn(block), PixApiExtensionsShaderProfiling.GetRegistersLiveOut(block), instructions));
            }
            shaders.Add(new(shader.GetId(), Json.EnumName(shader.GetStage()), shader.GetEntryFunction(), placeholder ? null : Convert.ToHexString(hash!).ToLowerInvariant(), placeholder,
                infos, files, functions, locations, registers, allocated, blocks));
        }
        return new(Interop.WOrNull(result.GetVendorName()) ?? "", Interop.WOrNull(result.GetAdapterName()) ?? "", Interop.WOrNull(result.GetAdapterFamily()) ?? "",
            frontend, backend, Interop.WOrNull(result.GetIsaDocumentationLink()), categories, types, registerTypes, branchTypes, dependencyTypes, shaders);
    }

    private static string Severity(PIX_SHADER_PROFILING_COMMENT_TYPE type) => type switch
    {
        PIX_SHADER_PROFILING_COMMENT_TYPE.PIX_SHADER_PROFILING_COMMENT_TYPE_ERROR => "error",
        PIX_SHADER_PROFILING_COMMENT_TYPE.PIX_SHADER_PROFILING_COMMENT_TYPE_WARNING => "warning",
        _ => "info",
    };
}

/// <summary>A small most-recently-used set of profiling jobs keyed by request; a running job or one with an available result is joined.</summary>
internal sealed class StaticProfileJobCache(int capacity = 8)
{
    private readonly object _lock = new();
    private readonly LinkedList<(string Key, Job Job)> _entries = new();

    public Job StartOrJoin(string key, Func<Job> start)
    {
        lock (_lock)
        {
            for (LinkedListNode<(string Key, Job Job)>? node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != key) continue;
                Job existing = node.Value.Job;
                _entries.Remove(node);
                if (!existing.IsFinished || existing.ResultState == "available")
                {
                    _entries.AddFirst(node);
                    return existing;
                }
                break;
            }
            Job job = start();
            _entries.AddFirst((key, job));
            while (_entries.Count > capacity) _entries.RemoveLast();
            return job;
        }
    }
}
