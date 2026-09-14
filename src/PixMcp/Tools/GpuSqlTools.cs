using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Pix.Sql;

namespace PixMcp.Tools;

[McpServerToolType]
public static class GpuSqlTools
{
    /// <summary>Names pix_gpu_sql binds itself; callers may not shadow them.</summary>
    public static readonly string[] PreboundNames = ["$handle", "$scopeQueue", "$scopeFirst", "$scopeLast", "$markerPathPrefix"];

    private const string ParamsDescription = "Named parameters as a JSON object: { \"name\": value } binds $name, @name or :name. Scalars only (string, number, boolean, null); pass arrays as JSON text and read them with json_each($name). Cannot set the pre-bound names.";
    private const string RefHints = "queue_index and event_index build an EventRef { handle, queueIndex, eventIndex }; add shader_index for a ShaderRef { eventRef, shaderIndex }; api_object_id builds a ResourceRef { handle, apiObjectId }.";

    [McpServerTool(Name = "pix_gpu_sql_populate", Title = "Populate GPU SQL tables", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Materialises GPU capture data into the handle's private SQLite store for pix_gpu_sql as one job, one transaction per family. core (capture, queues, events, parsed call arguments, work items, frames) and resources need no replay; timing, shaders, counters, resourceUses and psos replay the capture on the local GPU if analysis is not started. Ready families are skipped unless force=true; counters, resourceUses and psos are populated only when named.")]
    public static Task<string> Populate(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Families to populate: core, timing, shaders, counters (needs counterIds or preset), resources, resourceUses, psos, or all (core, timing, shaders and resources). Default: core.")] string[]? tables = null,
        [Description("Counter ids for the counters family (from pix_gpu_counters_list).")] uint[]? counterIds = null,
        [Description(CounterPresets.NamesDescription + " For the counters family, instead of counterIds.")] string? preset = null,
        [Description("Repopulate families that are already ready (default false).")] bool force = false,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        string[] families = Families(tables);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (families.Contains("counters"))
        {
            if (preset is not null && counterIds is { Length: > 0 }) throw PixErrors.InvalidArguments("Pass counterIds or preset, not both.");
            if (preset is null && counterIds is not { Length: > 0 })
                throw PixErrors.InvalidArguments("The counters family needs counterIds or preset.", [new("pix_gpu_counters_list", new { handle }, CostHints.Replay)]);
            if (preset is not null)
                return CountersTools.WithPreset(session, jobs, "pix_gpu_sql_populate", handle, preset, waitSeconds, cancellationToken,
                    ids => Populate(session, jobs, handle, families, ids, null, force, waitSeconds, cancellationToken));
        }
        else if (preset is not null || counterIds is { Length: > 0 }) throw PixErrors.InvalidArguments("counterIds and preset apply only to the counters family.");
        uint[]? ids = families.Contains("counters") ? CountersTools.NormalizeCounterIds(counterIds) : null;
        GpuSqlStore store = capture.EnsureSqlStore(session.Results);
        return Tools.RunJob(jobs, "pix_gpu_sql_populate", () => StartPopulate(session, jobs, capture, store, families, ids, force), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_sql", Title = "Query GPU capture with SQL", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Runs one read-only SQL statement, or a named library query, over the GPU capture tables that pix_gpu_sql_populate materialised in the handle's private SQLite store, off the PIX worker, and returns positional rows. A statement that reads a family that is not populated fails with sql_tables_not_populated and the populate call; autoPopulate=true starts that populate job instead, which may replay the capture on the local GPU. Call pix_gpu_sql_tables for tables, documented columns, views and named queries.")]
    public static Task<string> Sql(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("One read-only statement (SELECT, WITH ... SELECT, VALUES) over the tables and views. Exactly one of sql or query.")] string? sql = null,
        [Description("Name of a library query listed by pix_gpu_sql_tables (namedQueries). Exactly one of sql or query.")] string? query = null,
        [Description(ParamsDescription)] Dictionary<string, JsonElement>? @params = null,
        [Description(EventScope.Description + " Binds $scopeQueue, $scopeFirst and $scopeLast (the event's subtree); library queries honour them.")] EventRef? scope = null,
        [Description("Binds $markerPathPrefix; library queries keep events whose path starts with these '/'-separated segments.")] string? markerPathPrefix = null,
        [Description("Maximum rows (default 200, max 5000).")] int maxRows = SqlRequest.DefaultMaxRows,
        [Description("Maximum serialized row bytes (default 131072, 4096 to 1048576).")] int maxBytes = SqlRequest.DefaultMaxBytes,
        [Description("Text cells longer than this end with an ellipsis (default 256, 16 to 65536).")] int maxStringLength = SqlRequest.DefaultMaxStringLength,
        [Description("Rows to skip (default 0). Stable only when the statement has ORDER BY.")] int offset = 0,
        [Description("Also count every row the statement produces (default false; runs it a second time).")] bool countTotal = false,
        [Description("Also return EXPLAIN QUERY PLAN rows (default false).")] bool explain = false,
        [Description("Return base64 of the first 4096 bytes of BLOB cells instead of only their size (default false).")] bool includeBlobs = false,
        [Description("Start or join the populate job for missing families instead of failing with sql_tables_not_populated (default false; the job may replay the capture). The counters family is never populated implicitly.")] bool autoPopulate = false,
        [Description("Statement budget in seconds (default 30, max 600); sql_timeout when exceeded.")] double timeoutSeconds = SqlRequest.DefaultTimeoutSeconds,
        [Description("Seconds to wait inline for a populate or query job (default 2); a pending answer names the job to wait for.")] double waitSeconds = 2,
        CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_gpu_sql", async () =>
        {
            if (!double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600) throw PixErrors.InvalidArguments("waitSeconds must be between 0 and 3600.");
            (GpuCaptureHandle capture, NamedTimingQuery? named, SqlRequest request) = Prepare(session, handle, sql, query, @params, scope, timeoutSeconds,
                maxRows, maxBytes, maxStringLength, offset, countTotal, explain, includeBlobs);
            GpuSqlStore store = capture.EnsureSqlStore(session.Results);
            string[] families = Families(store, request, cancellationToken);
            string[] missing = families.Where(f => !store.IsReady(f)).ToArray();
            if (missing.Length > 0)
            {
                if (!autoPopulate || missing.Contains("counters")) throw NotPopulated(handle, missing, autoPopulate);
                Job populate = StartPopulate(session, jobs, capture, store, missing, null, false);
                if (await Pending(populate, "pix_gpu_sql", handle, waitSeconds, cancellationToken).ConfigureAwait(false) is string pending) return pending;
            }
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(new
            {
                request.Sql, request.Params, scope, markerPathPrefix, offset, maxRows, maxBytes, maxStringLength, countTotal, explain, includeBlobs, timeoutSeconds,
                populated = families.Select(f => store.State(f).PopulatedAt).ToArray(),
            }))));
            object continuation = new { handle, sql, query, @params, scope, markerPathPrefix, maxRows, maxBytes, maxStringLength, countTotal, explain, includeBlobs, timeoutSeconds };
            Job job = store.QueryJob(key, session.Results, () => jobs.StartManaged("gpu-sql-query", $"pix_gpu_sql on {handle}", j => Task.FromResult<object?>(store.Read(closing =>
            {
                using var database = new GpuSqlDatabase(store.Path, j.Cancellation.Token, closing, timeoutSeconds + 5);
                SqlResultDto result = SqlQuery.Execute(database, request with { Prebound = Bindings(database, store, handle, scope, markerPathPrefix) }, "gpusql", handle);
                var calls = new List<ToolCallDto>();
                if (result.NextOffset is int next) calls.Add(new("pix_gpu_sql", Merge(continuation, "offset", next), CostHints.Query));
                calls.Add(new("pix_gpu_sql_tables", new { handle }, CostHints.Cached));
                GpuCaptureHandle current = session.Get<GpuCaptureHandle>(handle);
                GpuSqlFamilyStateDto[] states = GpuSqlSchema.Families.Where(f => families.Contains(f.Name)).Select(f => StateDto(store, f, current)).ToArray();
                var notes = new List<string>(result.Notes) { RefHints };
                if (named is not null) notes.AddRange(named.Caveats);
                if (states.Any(s => s.Stale))
                    notes.Add("Some families were populated under an earlier GPU analysis (stale); they still answer, and pix_gpu_sql_populate with force=true refreshes them.");
                return result with { Notes = notes, NextCalls = calls, Provenance = new { tableStates = states }, Query = named?.Name };
            })), owner: handle));
            return await Pending(job, "pix_gpu_sql", handle, waitSeconds, cancellationToken).ConfigureAwait(false) ?? Read(session, "pix_gpu_sql", handle, job, cancellationToken);
        });

    [McpServerTool(Name = "pix_gpu_sql_tables", Title = "GPU SQL tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Describes the handle's GPU SQL store for pix_gpu_sql without touching the PIX worker: each family's state (empty, ready, stale after an analysis restart), cost and requirements, the tables with documented columns and units, the views, the named query library and the pre-bound parameters, plus a populate call for every family that is not ready.")]
    public static Task<string> TablesTool(PixSession session,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("summary (default: names and descriptions) or full (columns, view SQL and query SQL).")] string detail = "summary",
        CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_gpu_sql_tables", () =>
        {
            string level = RollupTools.Canonical(detail, ["summary", "full"], "detail");
            GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
            GpuSqlStore? store = capture.SqlStore;
            bool full = level == "full";
            GpuSqlFamilyStateDto[] families = GpuSqlSchema.Families.Select(f => StateDto(store, f, capture)).ToArray();
            var dto = new GpuSqlTablesDto(handle, level, new GpuSqlStoreDto(store is not null, store?.Bytes ?? 0, store?.MaxBytes ?? ServerOptions.Current.GpuSqlMaxBytes, GpuSqlSchema.Version),
                families,
                GpuSqlSchema.Tables.Where(t => t.Family != "meta")
                    .Select(t => new GpuSqlTableDto(t.Name, t.Family, t.Description, full ? t.Columns.Select(c => new GpuSqlColumnDto(c.Name, c.Type, c.Unit, c.Description)).ToArray() : null)).ToArray(),
                GpuSqlSchema.Views.Select(v => new GpuSqlViewDto(v.Name, v.Description, v.Families, full ? v.Sql : null)).ToArray(),
                GpuQueryLibrary.All.Select(q => q.Describe(q.Requires.All(f => store?.IsReady(f) == true), full)).ToArray(),
                PreboundNames,
                [
                    "Populate families with pix_gpu_sql_populate before querying them; pix_gpu_sql fails with sql_tables_not_populated otherwise.",
                    "$scopeQueue, $scopeFirst and $scopeLast bind the scope event's subtree; $markerPathPrefix binds markerPathPrefix; unset ones are NULL.",
                    "occupancy, hf and drpix are not materialised yet: use pix_gpu_occupancy, pix_gpu_hf_counters and pix_gpu_drpix_run.",
                    RefHints,
                ],
                families.Where(f => f.State != "ready" || f.Stale).Select(f => PopulateCall(handle, f.Family)).ToArray());
            return Task.FromResult(Tools.Serialize(dto, "pix_gpu_sql_tables", session, handle));
        });

    [McpServerTool(Name = "pix_gpu_sql_export", Title = "Export GPU SQL rows", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Streams every row of one read-only statement, or named query, over the populated GPU SQL tables to a CSV or JSON file as a job, off the PIX worker. The file is written under a temporary name and moved into place; the families it reads must already be populated.")]
    public static Task<string> Export(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Output file path; its directory must exist.")] string outPath,
        [Description("One read-only statement. Exactly one of sql or query.")] string? sql = null,
        [Description("Name of a library query listed by pix_gpu_sql_tables. Exactly one of sql or query.")] string? query = null,
        [Description(ParamsDescription)] Dictionary<string, JsonElement>? @params = null,
        [Description(EventScope.Description + " Binds $scopeQueue, $scopeFirst and $scopeLast.")] EventRef? scope = null,
        [Description("Binds $markerPathPrefix.")] string? markerPathPrefix = null,
        [Description("csv (default, RFC 4180 with a header row) or json (an array of row objects).")] string format = "csv",
        [Description("Replace an existing file (default false).")] bool overwrite = false,
        [Description("Maximum rows to write (default 1000000).")] int maxRows = 1_000_000,
        [Description("Statement budget in seconds (default 300, max 3600).")] double timeoutSeconds = 300,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        string fmt = RollupTools.Canonical(format, ["csv", "json"], "format");
        if (maxRows < 1) throw PixErrors.InvalidArguments("maxRows must be at least 1.");
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 3600) throw PixErrors.InvalidArguments("timeoutSeconds must be greater than 0 and at most 3600.");
        (GpuCaptureHandle capture, _, SqlRequest request) = Prepare(session, handle, sql, query, @params, scope, Math.Min(timeoutSeconds, 600));
        request = request with { TimeoutSeconds = timeoutSeconds };
        string full = Tools.PrepareOutputPath(outPath, overwrite, session.Results);
        GpuSqlStore store = capture.EnsureSqlStore(session.Results);
        string[] missing = Families(store, request, cancellationToken).Where(f => !store.IsReady(f)).ToArray();
        if (missing.Length > 0) throw NotPopulated(handle, missing, false);
        return Tools.RunJob(jobs, "pix_gpu_sql_export", () => jobs.StartManaged("gpu-sql-export", $"Export GPU SQL rows of {handle} to {full}", j => Task.FromResult<object?>(store.Read(closing =>
        {
            using var database = new GpuSqlDatabase(store.Path, j.Cancellation.Token, closing, timeoutSeconds + 5);
            SqlRequest bound = request with { Prebound = Bindings(database, store, handle, scope, markerPathPrefix) };
            string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                IReadOnlyList<string> columns = [];
                long rows;
                bool truncated;
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (fmt == "csv")
                    {
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        rows = SqlQuery.Stream(database, bound, header =>
                        {
                            columns = header;
                            writer.Write(string.Join(",", header.Select(Csv)));
                            writer.Write("\r\n");
                        }, row =>
                        {
                            writer.Write(string.Join(",", row.Select(Csv)));
                            writer.Write("\r\n");
                        }, maxRows, out truncated);
                    }
                    else
                    {
                        using var writer = new Utf8JsonWriter(stream);
                        writer.WriteStartArray();
                        rows = SqlQuery.Stream(database, bound, header => columns = header, row =>
                        {
                            writer.WriteStartObject();
                            for (int i = 0; i < row.Length; i++)
                            {
                                writer.WritePropertyName(columns[i]);
                                JsonSerializer.Serialize(writer, row[i], Json.Options);
                            }
                            writer.WriteEndObject();
                        }, maxRows, out truncated);
                        writer.WriteEndArray();
                    }
                }
                File.Move(temp, full, overwrite);
                return new GpuSqlExportDto(handle, full, fmt, rows, truncated, columns, new FileInfo(full).Length);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        })), owner: handle), waitSeconds, cancellationToken);
    }

    /// <summary>Canonical family names in schema order; all expands to the default set; unavailable families name their replacement tool.</summary>
    internal static string[] Families(IEnumerable<string>? tables)
    {
        var result = new List<string>();
        foreach (string requested in tables ?? new[] { "core" })
        {
            if (string.IsNullOrWhiteSpace(requested)) continue;
            if (requested.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(GpuSqlSchema.AllFamilies);
                continue;
            }
            if (GpuSqlSchema.UnavailableReason(requested) is string reason) throw PixErrors.InvalidArguments(reason);
            GpuSqlFamily family = GpuSqlSchema.Family(requested)
                ?? throw PixErrors.InvalidArguments($"Unknown family '{requested}'. Families: {string.Join(", ", GpuSqlSchema.FamilyOrder)}, all.");
            result.Add(family.Name);
        }
        if (result.Count == 0) throw PixErrors.InvalidArguments("tables must name at least one family.");
        return result.Distinct(StringComparer.Ordinal).OrderBy(f => Array.IndexOf(GpuSqlSchema.FamilyOrder, f)).ToArray();
    }

    /// <summary>RFC 4180 cell text.</summary>
    internal static string Csv(object? cell)
    {
        string text = cell switch
        {
            null => "",
            string s => s,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(cell, Json.Options),
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static bool Stale(GpuCaptureHandle capture, GpuSqlFamilyState state)
        => state.State == "ready" && state.Family is not ("core" or "resources") && state.AnalysisGeneration != capture.AnalysisGeneration;

    internal static GpuSqlFamilyStateDto StateDto(GpuSqlStore? store, GpuSqlFamily family, GpuCaptureHandle capture)
    {
        GpuSqlFamilyState state = store?.State(family.Name) ?? new(family.Name, "empty", 0, null, null, null);
        return new(family.Name, state.State, state.Rows, state.PopulatedAt, state.AnalysisGeneration, Stale(capture, state), family.Cost, family.Requires, state.Detail);
    }

    private static ToolCallDto PopulateCall(string handle, string family)
        => family == "counters"
            ? new("pix_gpu_sql_populate", new { handle, tables = new[] { family }, preset = "utilization" }, CostHints.Job)
            : new("pix_gpu_sql_populate", new { handle, tables = new[] { family } }, CostHints.Job);

    private static PixToolException NotPopulated(string handle, string[] missing, bool autoPopulate)
    {
        var calls = new List<ToolCallDto>();
        string[] implicitFamilies = missing.Where(f => f != "counters").ToArray();
        if (implicitFamilies.Length > 0) calls.Add(new("pix_gpu_sql_populate", new { handle, tables = implicitFamilies }, CostHints.Job));
        if (missing.Contains("counters"))
        {
            calls.Add(new("pix_gpu_counters_list", new { handle }, CostHints.Replay));
            calls.Add(PopulateCall(handle, "counters"));
        }
        calls.Add(new("pix_gpu_sql_tables", new { handle }, CostHints.Cached));
        return new PixToolException(PixErrors.Codes.SqlTablesNotPopulated,
            $"The statement reads families that are not populated: {string.Join(", ", missing)}. Populate them first{(autoPopulate ? "; the counters family needs explicit counter ids" : ", or pass autoPopulate=true")}.",
            false, calls);
    }

    private static (GpuCaptureHandle Capture, NamedTimingQuery? Named, SqlRequest Request) Prepare(PixSession session, string handle, string? sql, string? query,
        Dictionary<string, JsonElement>? parameters, EventRef? scope, double timeoutSeconds, int maxRows = SqlRequest.DefaultMaxRows, int maxBytes = SqlRequest.DefaultMaxBytes,
        int maxStringLength = SqlRequest.DefaultMaxStringLength, int offset = 0, bool countTotal = false, bool explain = false, bool includeBlobs = false)
    {
        if ((sql is null) == (query is null))
            throw PixErrors.InvalidArguments("Pass exactly one of sql or query.", [new("pix_gpu_sql_tables", new { handle }, CostHints.Cached)]);
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 600) throw PixErrors.InvalidArguments("timeoutSeconds must be greater than 0 and at most 600.");
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (scope is not null)
        {
            ReferenceValidation.Event(session, scope);
            if (scope.Handle != handle)
                throw PixErrors.InvalidReference($"scope addresses {scope.Handle}, not {handle}.", new ToolCallDto("pix_gpu_events", new { handle, kind = "marker" }, CostHints.Query));
        }
        NamedTimingQuery? named = null;
        if (query is not null)
            named = GpuQueryLibrary.Find(query) ?? throw PixErrors.InvalidArguments(
                $"Unknown named query '{query}'. Queries: {string.Join(", ", GpuQueryLibrary.All.Select(q => q.Name))}.", [new("pix_gpu_sql_tables", new { handle }, CostHints.Cached)]);
        var request = new SqlRequest(named?.Sql ?? sql!)
        {
            Params = named is null ? parameters : named.BindParams(parameters), Prebound = PreboundNames.ToDictionary(n => n, _ => (object?)null),
            MaxRows = maxRows, MaxBytes = maxBytes, MaxStringLength = maxStringLength, Offset = offset, CountTotal = countTotal, Explain = explain,
            IncludeBlobs = includeBlobs, TimeoutSeconds = timeoutSeconds,
        };
        SqlQuery.Validate(request);
        return (capture, named, request);
    }

    /// <summary>The families a statement reads, from the tables SQLite reports while preparing it on the store (nothing runs).</summary>
    private static string[] Families(GpuSqlStore store, SqlRequest request, CancellationToken cancellationToken)
        => GpuSqlSchema.FamiliesOf(store.Read(closing =>
        {
            using var database = new GpuSqlDatabase(store.Path, cancellationToken, closing, 10);
            return SqlQuery.ReferencedTables(database, request.Sql);
        })).ToArray();

    internal static Dictionary<string, object?> Bindings(GpuSqlDatabase database, GpuSqlStore store, string handle, EventRef? scope, string? markerPathPrefix)
    {
        long? last = null;
        if (scope is not null)
            last = (store.IsReady("core")
                ? database.Scalar("SELECT subtree_last FROM events WHERE queue_index = $queue AND event_index = $event", ("$queue", (long)scope.QueueIndex), ("$event", (long)scope.EventIndex))
                : null) ?? scope.EventIndex;
        return new(StringComparer.Ordinal)
        {
            ["$handle"] = handle,
            ["$scopeQueue"] = scope?.QueueIndex,
            ["$scopeFirst"] = scope?.EventIndex,
            ["$scopeLast"] = last,
            ["$markerPathPrefix"] = string.IsNullOrWhiteSpace(markerPathPrefix) ? null : markerPathPrefix.Trim().TrimEnd('/'),
        };
    }

    private static Job StartPopulate(PixSession session, JobManager jobs, GpuCaptureHandle capture, GpuSqlStore store, string[] families, uint[]? ids, bool force)
    {
        string key = string.Join(",", families) + (ids is null ? "" : ":" + string.Join(",", ids)) + (force ? ":force" : "");
        string handle = capture.Id;
        lock (store.JobsGate)
        {
            if (store.PopulateJobs.TryGetValue(key, out Job? running) && !running.IsFinished) return running;
            Job job = jobs.StartManaged("gpu-sql-populate", $"Populate GPU SQL [{string.Join(", ", families)}] for {handle}", async j =>
            {
                var skipped = new List<string>();
                for (int f = 0; f < families.Length; f++)
                {
                    string family = families[f];
                    j.ThrowIfCancellationRequested();
                    string? detail = family == "counters" ? string.Join(",", ids!) : null;
                    GpuSqlFamilyState state = store.State(family);
                    if (!force && state.State == "ready" && state.Detail == detail && !Stale(session.Get<GpuCaptureHandle>(handle), state))
                    {
                        skipped.Add(family);
                        continue;
                    }
                    j.AddMessage($"Reading the {family} family from the capture...");
                    (IReadOnlyList<GpuSqlTableRows> rows, int generation) = await session.Worker.Run(
                        () => GpuSqlSnapshot.Take(session.Get<GpuCaptureHandle>(handle), family, ids, j), j.Cancellation.Token, $"pix_gpu_sql_populate {family}").ConfigureAwait(false);
                    j.AddMessage($"Writing the {family} family to SQLite...");
                    store.Write(family, rows, generation, detail, j.Cancellation.Token);
                    j.SetProgress((float)(f + 1) / families.Length);
                }
                GpuCaptureHandle current = session.Get<GpuCaptureHandle>(handle);
                ToolCallDto sample = new("pix_gpu_sql", new { handle, query = families.Contains("timing") ? "top_passes" : "kinds_by_queue" }, CostHints.Query);
                return (object?)new GpuSqlPopulateDto(handle, GpuSqlSchema.Families.Where(x => families.Contains(x.Name)).Select(x => StateDto(store, x, current)).ToArray(),
                    skipped, store.Bytes, [new("pix_gpu_sql_tables", new { handle }, CostHints.Cached), sample]);
            }, owner: handle);
            store.PopulateJobs[key] = job;
            return job;
        }
    }

    /// <summary>Waits up to waitSeconds: a pending answer while the job runs, its error when it failed, else null.</summary>
    private static async Task<string?> Pending(Job job, string tool, string handle, double waitSeconds, CancellationToken cancellationToken)
    {
        if (!job.IsFinished && waitSeconds > 0)
        {
            try { await job.WaitAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        if (!job.IsFinished)
            return Json.Serialize(new PendingDto(true, job.Id, tool, $"{job.Description} is running as {job.Id}. Wait for it with pix_job_wait, then repeat the call.", job.ToDto(),
                [new("pix_job_wait", new { jobId = job.Id, timeoutSeconds = 2 }, CostHints.Job), new(tool, StructuredToolResults.CurrentArguments() ?? new { handle }, CostHints.Query)]));
        if (job.Status != JobStatus.Succeeded)
            throw new PixToolException(job.ErrorDetail ?? new ErrorDto(PixErrors.Codes.PreparationFailed, job.Error ?? $"{job.Description} failed.", null, false, []));
        return null;
    }

    private static string Read(PixSession session, string tool, string handle, Job job, CancellationToken cancellationToken)
    {
        string resultRef = job.ResultRef ?? throw new PixToolException(PixErrors.Codes.ResultExpired, "The GPU SQL result is unavailable. Repeat the call.");
        object value;
        try { value = session.Results.ReadElement(resultRef, maxBytes: Tools.MaxResultBytes, cancellationToken: cancellationToken); }
        catch (PixToolException ex) when (ex.Detail.Code == PixErrors.Codes.ResultTooLarge)
        {
            throw new PixToolException(PixErrors.Codes.ResultTooLarge, "The complete result is retained. Read it in bounded windows with pix_result_read.", nextCalls: [ResultStore.ReadCall(resultRef)]);
        }
        return Tools.Serialize(value, tool, session, handle);
    }

    private static Dictionary<string, object?> Merge(object arguments, string name, object? value)
    {
        var merged = JsonSerializer.SerializeToElement(arguments, Json.Options).EnumerateObject()
            .Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal);
        merged[name] = value;
        return merged;
    }
}

/// <summary>Reads one GPU SQL family from the capture on the PIX worker, preparing what it needs, into rows for the store.</summary>
internal static class GpuSqlSnapshot
{
    public static (IReadOnlyList<GpuSqlTableRows> Rows, int Generation) Take(GpuCaptureHandle h, string family, uint[]? ids, Job job)
    {
        IReadOnlyList<GpuSqlTableRows> rows = family switch
        {
            "core" => Core(h),
            "timing" => Timing(h, job),
            "shaders" => Shaders(h, job),
            "counters" => Counters(h, ids ?? throw PixErrors.InvalidArguments("The counters family needs counter ids."), job),
            "resources" => GpuSqlPopulate.Resources(ResourceTools.AllResourceSummaries(h)),
            "resourceUses" => ResourceUses(h, job),
            "psos" => PipelineStates(h, job),
            _ => throw PixErrors.InvalidArguments($"Unknown family '{family}'."),
        };
        return (rows, h.AnalysisGeneration);
    }

    private static IReadOnlyList<GpuSqlTableRows> Core(GpuCaptureHandle h)
    {
        GpuSqlQueueInput[] queues = h.Queues.Select(q => new GpuSqlQueueInput(q.Index, q.Name ?? "", Json.EnumName(q.Type), h.AllEvents(q.Index), h.ChildCounts(q.Index))).ToArray();
        FrameTable frames = FrameSegmentation.Build(queues.Select(q => new FrameQueueInput(q.QueueIndex, q.Events, h.TimingRowsByQueue.GetValueOrDefault(q.QueueIndex))).ToArray(),
            e => Tools.MatchesKind(e, "present"));
        VendorIdentity? vendor = h.CachedCaptureVendor;
        return GpuSqlPopulate.Core(h.Id, h.Path, vendor is null ? null : GpuVendors.Name(vendor.Vendor), vendor?.AdapterName, queues, frames);
    }

    private static IReadOnlyList<GpuSqlTableRows> Timing(GpuCaptureHandle h, Job job)
    {
        CountersTools.CollectTiming(h, job);
        return GpuSqlPopulate.Timing(h.Queues.Select(q => new GpuSqlTimingInput(q.Index, h.TimingRowsByQueue.GetValueOrDefault(q.Index, []), h.TimingTreeFor(q.Index))).ToArray());
    }

    private static IReadOnlyList<GpuSqlTableRows> Shaders(GpuCaptureHandle h, Job job)
    {
        h.EnsureAnalysisStarted(job);
        ShaderIndex index = h.ShaderIndex ??= ShaderInventoryTools.BuildIndex(h, job);
        return GpuSqlPopulate.Shaders(index.Occurrences, index.PsoKeyOf, index.IsComplete);
    }

    private static IReadOnlyList<GpuSqlTableRows> Counters(GpuCaptureHandle h, uint[] ids, Job job)
    {
        CounterCollectionCache cache = CountersTools.CounterSet(h, ids, job);
        return GpuSqlPopulate.Counters(cache.Counters,
            h.Queues.Select(q => new GpuSqlCounterInput(q.Index, CountersTools.CachedCounterRows(h, cache, q.Index), h.ChildCounts(q.Index))).ToArray());
    }

    private static IReadOnlyList<GpuSqlTableRows> ResourceUses(GpuCaptureHandle h, Job job)
    {
        h.EnsureAnalysisStarted(job);
        h.EnsureAccessedResources(job);
        var uses = new List<GpuSqlResourceUse>();
        foreach (QueueEntry queue in h.Queues)
            foreach (EventRecord e in h.AllEvents(queue.Index))
            {
                job.ThrowIfCancellationRequested();
                if (!Tools.MatchesKind(e, "work")) continue;
                var eventRef = new EventRef(h.Id, queue.Index, e.Index);
                try
                {
                    uses.AddRange(ReadEventResourceUses(eventRef,
                        offset => ResourceTools.QueryEventResources(h, eventRef, null, 0, 1, offset, Paging.MaxLimit), job.Cancellation.Token));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    job.AddMessage($"Resource views of queue {queue.Index} event {e.Index} are unavailable: {PixErrors.Describe(ex)}");
                    continue;
                }
            }
        return GpuSqlPopulate.ResourceUses(uses);
    }

    /// <summary>Detaches every page of an event's resource views; the caller supplies the worker-bound page reader.</summary>
    internal static IReadOnlyList<GpuSqlResourceUse> ReadEventResourceUses(EventRef eventRef, Func<int, EventResourcesDto> readPage, CancellationToken cancellationToken)
    {
        var uses = new List<GpuSqlResourceUse>();
        int? offset = 0;
        while (offset is int current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EventResourcesDto page = readPage(current);
            foreach (ResourceGroupDto group in page.Resources)
            {
                var resource = group.Resource as ResourceDetailsDto;
                foreach (object view in group.Views) Add(view, resource?.ApiObjectId, resource?.Name);
            }
            foreach (object view in page.OtherViews) Add(view, null, null);
            offset = page.NextViewOffset;
        }
        return uses;

        void Add(object view, string? apiObjectId, string? name)
        {
            JsonElement json = JsonSerializer.SerializeToElement(view, Json.Options);
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("index", out JsonElement index) || index.ValueKind != JsonValueKind.Number
                || !index.TryGetUInt32(out uint viewIndex)) return;
            string? type = json.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            uses.Add(new(eventRef.QueueIndex, eventRef.EventIndex, viewIndex, apiObjectId, name, type));
        }
    }

    private static IReadOnlyList<GpuSqlTableRows> PipelineStates(GpuCaptureHandle h, Job job)
    {
        h.EnsureAnalysisStarted(job);
        var rows = new List<object?[]>();
        foreach (QueueEntry queue in h.Queues)
            foreach (EventRecord e in h.AllEvents(queue.Index))
            {
                job.ThrowIfCancellationRequested();
                if (!Tools.MatchesKind(e, "work")) continue;
                try
                {
                    PipelineStateDto state = PipelineTools.QueryPipelineState(h, new EventRef(h.Id, queue.Index, e.Index), false, false, true);
                    object[] shaders = (state.Shaders as IEnumerable<object>)?.ToArray() ?? [];
                    string? psoKey = shaders.All(s => s is ShaderInfoDto) ? ShaderIdentity.PsoKey(shaders.Cast<ShaderInfoDto>()) : null;
                    rows.Add([(long)queue.Index, (long)e.Index, state.ProgramType, psoKey, InspectionTools.WithoutEvent(state).ToJsonString(Json.Options)]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { job.AddMessage($"Pipeline state of queue {queue.Index} event {e.Index} is unavailable: {PixErrors.Describe(ex)}"); }
            }
        return [new GpuSqlTableRows("pipeline_states", rows)];
    }
}
