using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Pix.Sql;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>The GPU SQL store over the timing tree fixture: schema, family writes, guarded queries, the query library and lifecycle.</summary>
public sealed class GpuSqlStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pixmcp-gpusql-tests-" + Guid.NewGuid().ToString("N"));

    // 0 Frame > 1 Shadow pass > 2, 3 draws; 0 Frame > 4 Main pass (measured 8) > 5 Marker > 6 Dispatch, 4 > 7 untimed draw; 8 Present.
    private static readonly EventRecord[] Events =
    {
        new(0, uint.MaxValue, uint.MaxValue, "Frame", "", 0, 0),
        new(1, uint.MaxValue, 0, "Shadow pass", "", 0, 0),
        new(2, 1, 1, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(3, 2, 1, "DrawInstanced", "DrawInstanced(6)", 0, 0),
        new(4, uint.MaxValue, 0, "Main pass", "", 0, 0),
        new(5, uint.MaxValue, 4, "Marker", "", 0, 0),
        new(6, 3, 5, "Dispatch", "Dispatch(1,1,1)", 0, 0),
        new(7, 4, 4, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(8, 5, uint.MaxValue, "Present", "", 0, 0),
    };

    private static readonly EventTimingRow[] Rows =
    {
        Row(2, 10), Row(3, 20), Row(4, 8), Row(6, 5), Row(8, 1),
        new(0, 7, 4, "DrawInstanced", "DrawInstanced(3)", 0, 0, 0, GpuCaptureHandle.TimingNone),
    };

    private static EventTimingRow Row(uint index, ulong eop) => new(0, index, index, Events[index].Name, Events[index].ApiCallData, 0, eop, 0, eop);

    public GpuSqlStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    private GpuSqlStore Store(long maxBytes = 64L << 20) => new("gpu-1", Path.Combine(_directory, "gpusql-" + Guid.NewGuid().ToString("N") + ".sqlite"), maxBytes);

    private static IReadOnlyList<GpuSqlTableRows> CoreRows()
    {
        int[] children = EventNavigation.ChildCounts(Events);
        FrameTable frames = FrameSegmentation.Build([new FrameQueueInput(0, Events, Rows)], e => PixMcp.Tools.Tools.MatchesKind(e, "present"));
        return GpuSqlPopulate.Core("gpu-1", "capture.wpix", "nvidia", "GPU", [new GpuSqlQueueInput(0, "Graphics", "DIRECT", Events, children)], frames);
    }

    private GpuSqlStore Populated()
    {
        GpuSqlStore store = Store();
        store.Write("core", CoreRows(), 0, null, default);
        store.Write("timing", GpuSqlPopulate.Timing([new GpuSqlTimingInput(0, Rows, TimingTree.Build(Events, Rows))]), 0, null, default);
        return store;
    }

    private static SqlResultDto Query(GpuSqlStore store, string sql, IReadOnlyDictionary<string, JsonElement>? parameters = null, IReadOnlyDictionary<string, object?>? prebound = null)
        => store.Read(closing =>
        {
            using var database = new GpuSqlDatabase(store.Path, default, closing, 10);
            return SqlQuery.Execute(database, new SqlRequest(sql)
            {
                Params = parameters, Prebound = prebound ?? GpuSqlTools.PreboundNames.ToDictionary(n => n, _ => (object?)null),
            }, "gpusql", "gpu-1");
        });

    private static IReadOnlyList<string> Referenced(GpuSqlStore store, string sql)
        => store.Read(closing =>
        {
            using var database = new GpuSqlDatabase(store.Path, default, closing, 10);
            return SqlQuery.ReferencedTables(database, sql);
        });

    [Fact]
    public void PopulatedFamiliesAnswerJoinsViewsAndTreeSemantics()
    {
        using GpuSqlStore store = Populated();
        Assert.Equal(4L, Query(store, "SELECT COUNT(*) FROM v_work").Rows[0][0]);
        Assert.Equal(new object?[] { "mixed", 38L }, Query(store, "SELECT semantics, inclusive_ns FROM timing_tree WHERE event_index = 0").Rows[0]);
        Assert.Equal(new object?[] { "Frame/Main pass/Marker/Dispatch", 6L, 3L, "dispatch" }, Query(store, "SELECT path, subtree_last, depth, kind FROM events WHERE event_index = 6").Rows[0]);
        Assert.Equal(7L, Query(store, "SELECT subtree_last FROM events WHERE event_index = 0").Rows[0][0]);
        Assert.Equal(new object?[] { "vertices", 3L }, Query(store, "SELECT work_item_kind, work_items FROM work_items WHERE event_index = 2").Rows[0]);
        Assert.Equal(39L, Query(store, "SELECT sum_of_roots_ns FROM queue_totals").Rows[0][0]);
        FrameTable frames = FrameSegmentation.Build([new FrameQueueInput(0, Events, Rows)], e => PixMcp.Tools.Tools.MatchesKind(e, "present"));
        Assert.Equal(new object?[] { (long)frames.Count, frames.Assignment }, Query(store, "SELECT frame_count, frame_assignment FROM capture").Rows[0]);
        Assert.Equal(new object?[] { "timing", 0L }, Query(store, "SELECT family, analysis_generation FROM table_status WHERE family = 'timing'").Rows[0]);
        Assert.Equal(("ready", 9L), (store.State("core").State, Query(store, "SELECT COUNT(*) FROM events").Rows[0][0]));
    }

    [Fact]
    public void StatementsMapToTheFamiliesTheyReadAndLibraryQueriesRunWithScopes()
    {
        using GpuSqlStore store = Populated();
        Assert.Equal(new[] { "core", "timing" }, GpuSqlSchema.FamiliesOf(Referenced(store, "SELECT * FROM v_work")));
        Assert.Empty(GpuSqlSchema.FamiliesOf(Referenced(store, "SELECT family FROM table_status")));
        Assert.Equal(new[] { "counters" }, GpuSqlSchema.FamiliesOf(Referenced(store, "SELECT value FROM counters")));

        var required = new Dictionary<string, JsonElement>
        {
            ["apiObjectId"] = JsonSerializer.SerializeToElement("0x1"), ["counterId"] = JsonSerializer.SerializeToElement(1),
            ["numeratorId"] = JsonSerializer.SerializeToElement(1), ["denominatorId"] = JsonSerializer.SerializeToElement(2),
        };
        foreach (NamedTimingQuery query in GpuQueryLibrary.All)
        {
            Assert.Subset(query.Requires.ToHashSet(), GpuSqlSchema.FamiliesOf(Referenced(store, query.Sql)).ToHashSet());
            var parameters = query.BindParams(required.Where(p => query.Params.Any(d => d.Name == p.Key)).ToDictionary(p => p.Key, p => p.Value));
            SqlResultDto result = Query(store, query.Sql, parameters);
            Assert.NotNull(result.Rows);
        }

        NamedTimingQuery passes = GpuQueryLibrary.Find("TOP_PASSES")!;
        IReadOnlyDictionary<string, JsonElement> defaults = passes.BindParams(null);
        SqlResultDto all = Query(store, passes.Sql, defaults);
        Assert.Equal("Frame", all.Rows[0][2]);
        var scoped = new Dictionary<string, object?> { ["$handle"] = "gpu-1", ["$scopeQueue"] = 0L, ["$scopeFirst"] = 4L, ["$scopeLast"] = 7L, ["$markerPathPrefix"] = null };
        Assert.Equal(new[] { "Frame/Main pass", "Frame/Main pass/Marker" }, Query(store, passes.Sql, defaults, scoped).Rows.Select(r => (string)r[2]!).Order());
        var prefixed = new Dictionary<string, object?>(scoped) { ["$scopeQueue"] = null, ["$scopeFirst"] = null, ["$scopeLast"] = null, ["$markerPathPrefix"] = "frame/shadow pass" };
        Assert.Equal(new[] { "Frame/Shadow pass" }, Query(store, passes.Sql, defaults, prefixed).Rows.Select(r => (string)r[2]!));
        prefixed["$markerPathPrefix"] = "Frame/Shadow";
        Assert.Empty(Query(store, passes.Sql, defaults, prefixed).Rows);
    }

    [Fact]
    public void WritesReplaceTheirFamilyRejectMismatchedRowsAndRespectTheByteCap()
    {
        using GpuSqlStore store = Populated();
        store.Write("core", CoreRows(), 1, null, default);
        Assert.Equal(9L, Query(store, "SELECT COUNT(*) FROM events").Rows[0][0]);
        Assert.Equal(1, store.State("core").AnalysisGeneration);
        Assert.Throws<InvalidOperationException>(() => store.Write("core", [new GpuSqlTableRows("queues", [new object?[] { 1L }])], 0, null, default));
        Assert.Equal(9L, Query(store, "SELECT COUNT(*) FROM events").Rows[0][0]);

        using GpuSqlStore tiny = Store(maxBytes: 1);
        PixToolException full = Assert.Throws<PixToolException>(() => tiny.Write("core", CoreRows(), 0, null, default));
        Assert.Equal(PixErrors.Codes.SqlCapacityExceeded, full.Detail.Code);
        Assert.Equal("empty", tiny.State("core").State);
        Assert.Equal(0L, Query(tiny, "SELECT COUNT(*) FROM events").Rows[0][0]);
    }

    [Fact]
    public void ReadOnlyGuardHoldsAndClosingDeletesTheStore()
    {
        GpuSqlStore store = Populated();
        PixToolException write = Assert.Throws<PixToolException>(() => Query(store, "DELETE FROM events"));
        Assert.Contains(write.Detail.Code, new[] { PixErrors.Codes.SqlForbidden, PixErrors.Codes.SqlNotReadOnly });
        string path = store.Path;
        store.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal(PixErrors.Codes.SqlStoreClosed, Assert.Throws<PixToolException>(() => store.Read(_ => 1)).Detail.Code);
    }

    [Fact]
    public void ExportsStreamEveryRowAndQuoteCsvCells()
    {
        using GpuSqlStore store = Populated();
        var names = new List<object?[]>();
        IReadOnlyList<string> header = [];
        bool truncated = false;
        long rows = store.Read(closing =>
        {
            using var database = new GpuSqlDatabase(store.Path, default, closing, 10);
            return SqlQuery.Stream(database, new SqlRequest("SELECT name, event_index FROM events ORDER BY event_index"), h => header = h, names.Add, 5, out truncated);
        });
        Assert.Equal((5L, true), (rows, truncated));
        Assert.Equal(new[] { "name", "event_index" }, header);
        Assert.Equal(new object?[] { "Frame", 0L }, names[0]);
        Assert.Equal(("", "1.5", "\"a,b\"", "\"x\"\"y\"", "7"), (GpuSqlTools.Csv(null), GpuSqlTools.Csv(1.5), GpuSqlTools.Csv("a,b"), GpuSqlTools.Csv("x\"y"), GpuSqlTools.Csv(7L)));
    }

    [Fact]
    public void ResourceUsePopulationReadsEveryPageAndKeepsViewIdentity()
    {
        var eventRef = new EventRef("gpu-1", 0, 2);
        var resource = new ResourceDetailsDto(new ResourceRef("gpu-1", "0x123"), "0x123", "Texture", "COMMITTED", "LEGACY", null, null, null, null, null, null, null);
        var evt = new EventDto(0, 2, 1, 1, "DrawInstanced", null, 0, null);
        object[] firstViews = Enumerable.Range(0, 1000).Select(i => (object)new { index = i, type = "SHADER_RESOURCE_VIEW" }).ToArray();
        var first = new EventResourcesDto(eventRef, [], evt, 1001, 0, 1000, 1000, [new ResourceGroupDto(resource, firstViews)], []);
        var last = new EventResourcesDto(eventRef, [], evt, 1001, 1000, 1, null, [], [new { index = 1000, type = "SAMPLER" }]);
        var offsets = new List<int>();
        IReadOnlyList<GpuSqlResourceUse> uses = GpuSqlSnapshot.ReadEventResourceUses(eventRef, offset =>
        {
            offsets.Add(offset);
            return offset == 0 ? first : last;
        }, default);
        Assert.Equal(new[] { 0, 1000 }, offsets);
        Assert.Equal(1001, uses.Count);
        Assert.All(uses.Take(1000), u => Assert.Equal((0, 2u, "0x123", "Texture", "SHADER_RESOURCE_VIEW"),
            (u.QueueIndex, u.EventIndex, u.ApiObjectId, u.ResourceName, u.ViewType)));
        Assert.Equal((1000u, (string?)null, "SAMPLER"), (uses[^1].ViewIndex, uses[^1].ApiObjectId, uses[^1].ViewType));

        using GpuSqlStore store = Store();
        store.Write("resourceUses", GpuSqlPopulate.ResourceUses(uses), 0, null, default);
        Assert.Equal(new object?[] { 1001L, 0L, 1000L }, Query(store, "SELECT COUNT(*), MIN(view_index), MAX(view_index) FROM resource_uses").Rows[0]);

        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        Assert.Throws<OperationCanceledException>(() => GpuSqlSnapshot.ReadEventResourceUses(eventRef, _ =>
        {
            reads++;
            cancellation.Cancel();
            return first;
        }, cancellation.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void FamiliesAndBuildersFollowTheSchema()
    {
        Assert.Equal(new[] { "core", "timing", "shaders", "resources" }, GpuSqlTools.Families(["all"]));
        Assert.Equal(new[] { "core", "timing" }, GpuSqlTools.Families(["Timing", "core"]));
        Assert.Equal(new[] { "core" }, GpuSqlTools.Families(null));
        Assert.Equal(PixErrors.Codes.InvalidArguments, Assert.Throws<PixToolException>(() => GpuSqlTools.Families(["occupancy"])).Detail.Code);
        Assert.Equal(PixErrors.Codes.InvalidArguments, Assert.Throws<PixToolException>(() => GpuSqlTools.Families(["nope"])).Detail.Code);
        Assert.All(GpuSqlSchema.Tables.SelectMany(t => t.Columns), c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        Assert.All(GpuQueryLibrary.All, q => Assert.All(q.Requires, family => Assert.Contains(family, GpuSqlSchema.FamilyOrder)));

        Assert.Equal(new long[] { 7, 3, 2, 3, 7, 6, 6, 7, 8 }, GpuSqlPopulate.SubtreeLast(Events));
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 2, 3, 2, 0 }, GpuSqlPopulate.Depths(Events));
        Assert.Equal(("write", "readWrite", "read", "unknown"), (GpuSqlPopulate.Access("RENDER_TARGET_VIEW"), GpuSqlPopulate.Access("UNORDERED_ACCESS_VIEW"),
            GpuSqlPopulate.Access("SHADER_RESOURCE_VIEW"), GpuSqlPopulate.Access(null)));

        CounterInfo[] counters = [new(1, "GPU Utilization (%)", "", "FLOAT64", ["g"]), new(2, "Primitives", "", "UINT64", [])];
        int[] children = EventNavigation.ChildCounts(Events);
        IReadOnlyList<GpuSqlTableRows> counterRows = GpuSqlPopulate.Counters(counters,
            [new GpuSqlCounterInput(0, [new CounterEventRow(Events[1], [90.0, 999UL], true), new CounterEventRow(Events[2], [50.0, null], true), new CounterEventRow(Events[3], [null, null], false)], children)]);
        Assert.Equal(new[] { ("marker", 1L), ("marker", 2L), ("event", 1L) },
            counterRows.Single(t => t.Table == "counters").Rows.Select(r => ((string)r[5]!, (long)r[2]!)));

        using GpuSqlStore store = Store();
        store.Write("counters", counterRows, 0, "1,2", default);
        Assert.Equal(1L, Query(store, "SELECT COUNT(*) FROM counters WHERE row_kind = 'event' AND value = 50").Rows[0][0]);
        Assert.Equal("1,2", store.State("counters").Detail);
    }
}
