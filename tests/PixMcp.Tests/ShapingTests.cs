using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ShapingTests
{
    private static readonly QueueTotals Totals = new(0, 1_000_000, 1_000_000, 0, 1_000_000, false, 200, 0, 0, 1_000_000, "eopOnly");

    private static TimingEventDto Row(uint index, string name = "DrawIndexedInstanced", int pathDepth = 3) => new(
        new EventRef("gpu-1", 0, index), Enumerable.Range(0, pathDepth).Select(i => i switch { 0 => "Frame", 1 => "Shadow pass", _ => $"Cascade {index % 4}" }).ToArray(),
        0, index, index, name, "draw", Metrics.Duration(5000, Totals, null, (int)index + 1), Metrics.Duration(4000, Totals),
        1000UL * index, 4000UL, 1000UL * index + 1000, 1000UL * index + 6000);

    private static List<TimingEventDto> Rows(int count) => Enumerable.Range(0, count).Select(i => Row((uint)i)).ToList();

    private static ToolCallDto Continue(int at) => new("pix_gpu_timing_events", new { handle = "gpu-1", offset = at, limit = 25 });
    private static ToolCallDto Full() => new("pix_gpu_timing_events", new { handle = "gpu-1", offset = 0, limit = 25, maxStringLength = 4096 });

    private static int Bytes(object value) => Encoding.UTF8.GetByteCount(Json.Serialize(value));

    [Fact]
    public void TableRowsAreSeveralTimesSmallerThanObjectRowsAndStayPositional()
    {
        List<TimingEventDto> rows = Rows(200);
        object objects = Shaping.Apply(rows, 200, 0, 200, new ShapingOptions(MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full);
        object table = Shaping.Apply(rows, 200, 0, 200, new ShapingOptions("table", MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full);
        object briefTable = Shaping.Apply(rows, 200, 0, 200, new ShapingOptions("table", Brief: true, MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full);
        Assert.True(Bytes(objects) > 2 * Bytes(table), $"objects {Bytes(objects)} vs table {Bytes(table)}");
        Assert.True(Bytes(objects) > 4 * Bytes(briefTable), $"objects {Bytes(objects)} vs brief table {Bytes(briefTable)}");
        TableDto dto = Assert.IsType<TableDto>(table);
        Assert.Equal("gpu-1", dto.Handle);
        Assert.Equal(dto.Columns.Count, dto.Rows[0].Length);
        Assert.Equal(200, dto.Count);
        Assert.False(dto.TruncatedRows);
        Assert.Equal(0, dto.TruncatedStrings);
        int nameColumn = dto.Columns.ToList().FindIndex(c => c.Name == "name");
        Assert.Equal("DrawIndexedInstanced", dto.Rows[0][nameColumn]);
        Assert.Contains(dto.Columns, c => c.Name == "eop.ns" && c.Unit == "ns");
        Assert.Contains(dto.Columns, c => c.Name == "eop.percentOfQueueSpan" && c.Unit == "percent");
        Assert.Contains("percentOfQueueSpan", dto.Legend.Notes.Keys);
        Assert.Equal("EventRef", dto.Legend.Refs["eventRef"].Kind);
        Assert.Equal("$col:eventIndex", dto.Legend.Refs["eventRef"].From["eventIndex"]);
        Assert.Equal("$handle", dto.Legend.Refs["eventRef"].From["handle"]);
        // The recipe rebuilds a valid reference from the row.
        int queueColumn = dto.Columns.ToList().FindIndex(c => c.Name == "queueIndex");
        int eventColumn = dto.Columns.ToList().FindIndex(c => c.Name == "eventIndex");
        var rebuilt = new EventRef(dto.Handle!, Convert.ToInt32(dto.Rows[5][queueColumn]), Convert.ToUInt32(dto.Rows[5][eventColumn]));
        Assert.Equal(rows[5].EventRef, rebuilt);
    }

    [Fact]
    public void BriefKeepsIdentifyingColumnsInTablesAndNullsTheRestInObjects()
    {
        List<TimingEventDto> rows = Rows(3);
        TableDto table = Assert.IsType<TableDto>(Shaping.Apply(rows, 3, 0, 25, new ShapingOptions("table", Brief: true, MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        Assert.Equal(new[] { "queueIndex", "eventIndex", "name", "kind", "eop.ns", "eop.ms", "eop.percentOfQueueSpan", "eop.rank" }, table.Columns.Select(c => c.Name).ToArray());
        Assert.Equal("only identifying and ranking columns are included", table.Legend.Notes["brief"]);

        JsonObject objects = Assert.IsType<JsonObject>(Shaping.Apply(rows, 3, 0, 25, new ShapingOptions(Brief: true, MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        JsonObject first = objects["items"]![0]!.AsObject();
        Assert.Null(first["markerPath"]);
        Assert.Null(first["exec"]);
        Assert.NotNull(first["eop"]);
        Assert.Equal("DrawIndexedInstanced", first["name"]!.GetValue<string>());
        Assert.True(objects["extra"]!["brief"]!.GetValue<bool>());
    }

    [Fact]
    public void LongStringsAreCutCountedAndOfferedInFull()
    {
        List<TimingEventDto> rows = Rows(2).Select(r => r with { MarkerPath = ["Frame with a long name", "Shadow pass with a long name", "Cascade with a long name"] }).ToList();
        JsonObject objects = Assert.IsType<JsonObject>(Shaping.Apply(rows, 2, 0, 25, new ShapingOptions(MaxStringLength: 16), RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        string cut = objects["items"]![0]!["markerPath"]![0]!.GetValue<string>();
        Assert.Equal(16, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal("DrawIndexedInst…", objects["items"]![0]!["name"]!.GetValue<string>());
        int truncated = objects["extra"]!["truncatedStrings"]!.GetValue<int>();
        Assert.Equal(2 * (3 + 1), truncated);
        Assert.Equal(4096, objects["extra"]!["fullStrings"]!["arguments"]!["maxStringLength"]!.GetValue<int>());

        TableDto table = Assert.IsType<TableDto>(Shaping.Apply(rows, 2, 0, 25, new ShapingOptions("table", MaxStringLength: 16), RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        Assert.Equal(2 * 2, table.TruncatedStrings); // name and joined markerPath per row
        Assert.Contains(table.NextCalls, c => Json.Serialize(c.Arguments).Contains("4096"));
    }

    [Fact]
    public void TopNIsAWindowWithoutContinuationAndOffsetPagingKeepsOne()
    {
        List<TimingEventDto> rows = Rows(200);
        var options = new ShapingOptions("table", TopN: 5, MaxStringLength: 4096);
        (int offset, int limit) = Shaping.Window(options, 0, 25);
        Assert.Equal((0, 5), (offset, limit));
        TableDto top = Assert.IsType<TableDto>(Shaping.Apply(rows.Take(5).ToList(), 200, offset, limit, options, RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        Assert.Equal(5, top.Count);
        Assert.Equal(200, top.Total);
        Assert.Null(top.NextOffset);
        Assert.Empty(top.NextCalls);
        Assert.True(top.TruncatedRows);

        TableDto paged = Assert.IsType<TableDto>(Shaping.Apply(rows.Take(25).ToList(), 200, 0, 25, new ShapingOptions("table", MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        Assert.Equal(25, paged.NextOffset);
        object continuation = Assert.Single(paged.NextCalls).Arguments;
        Assert.Equal(25, (int)continuation.GetType().GetProperty("offset")!.GetValue(continuation)!);

        PageResult<TimingEventDto> plain = Assert.IsType<PageResult<TimingEventDto>>(Shaping.Apply(rows.Take(25).ToList(), 200, 0, 25, new ShapingOptions(MaxStringLength: 4096), RowShapes.TimingEvents, "gpu-1", null, Continue, Full));
        Assert.Equal(25, plain.NextOffset);
    }

    [Fact]
    public void OptionsAreValidatedAsInvalidArguments()
    {
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("csv", false, null, null, 0)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("table", false, 0, null, 0)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("table", false, 1001, null, 0)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("table", false, 5, null, 10)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("objects", false, null, 8, 0)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("objects", false, null, 5000, 0)).Detail.Code);
        ShapingOptions options = Shaping.Options(" TABLE ", true, 10, null, 0);
        Assert.True(options.Table);
        Assert.Equal(200, options.MaxStringLength);
        Assert.Equal(10, options.TopN);
    }

    [Fact]
    public void NullCellsStayPositionalAndCounterColumnsFollowTheCounterSet()
    {
        var counters = new[] { new PixMcp.Pix.Handles.CounterInfo(7, "GPU Busy (%)", "", "FLOAT", []), new PixMcp.Pix.Handles.CounterInfo(9, "L2 Bytes", "", "UINT64", []) };
        RowShape<CounterValueRowDto> shape = RowShapes.Counters(counters);
        var row = new CounterValueRowDto(new EventRef("gpu-1", 0, 3), 3, 3, "Draw", ["Frame"], new Dictionary<string, object?> { ["7"] = 12.5, ["9"] = null });
        TableDto table = Assert.IsType<TableDto>(Shaping.Apply([row], 1, 0, 25, new ShapingOptions("table"), shape, "gpu-1", null, null, null));
        int busy = table.Columns.ToList().FindIndex(c => c.Name == "7");
        int bytes = table.Columns.ToList().FindIndex(c => c.Name == "9");
        Assert.Equal("GPU Busy (%)", table.Columns[busy].Description);
        Assert.Equal(12.5, table.Rows[0][busy]);
        Assert.Null(table.Rows[0][bytes]);
        Assert.Equal(table.Columns.Count, table.Rows[0].Length);
    }

    [Fact]
    public void TimingTreeFlattensInPreOrderWithDepthAndParent()
    {
        var totals = Totals;
        TimingBranchDto Leaf(uint index, string name) => new(new EventRef("gpu-1", 0, index), index, name, index, TimingSemantics.Measured,
            Metrics.Duration(10, totals, null, 1), Metrics.Duration(10, totals), 10UL, 0UL, false, 0UL, 0, false, 0UL, 10UL, true, 0, 0, [], false, []);
        TimingBranchDto root = Leaf(0, "Frame") with { Children = [Leaf(1, "Shadow") with { Children = [Leaf(2, "Draw")] }, Leaf(3, "Main")], ChildCount = 2 };
        List<TimingTreeRow> rows = RowShapes.Flatten([root, Leaf(8, "Present")]);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 8 }, rows.Select(r => r.Node.Index).ToArray());
        Assert.Equal(new int[] { 0, 1, 2, 1, 0 }, rows.Select(r => r.Depth).ToArray());
        Assert.Equal(new uint?[] { null, 0, 1, 0, null }, rows.Select(r => r.ParentEventIndex).ToArray());
        var tree = new TimingTreeDto("gpu-1", 0, null, "inclusive", totals, Metrics.Denominators, 5, 0, 0, 0, 2, [root, Leaf(8, "Present")], null, false, 5, false, new { source = "test" }, []);
        TableDto table = Assert.IsType<TableDto>(PixMcp.Tools.CountersTools.ShapeTimingTree(tree, new ShapingOptions("table")));
        Assert.Equal(5, table.Rows.Count);
        Assert.Contains(table.Columns, c => c.Name == "parentEventIndex");
        Assert.Same(tree, PixMcp.Tools.CountersTools.ShapeTimingTree(tree, new ShapingOptions(MaxStringLength: 4096)));
        JsonObject cut = Assert.IsType<JsonObject>(PixMcp.Tools.CountersTools.ShapeTimingTree(tree, new ShapingOptions(MaxStringLength: 16)));
        Assert.True(cut["truncatedStrings"]!.GetValue<int>() >= 4, "the four denominator sentences are longer than 16 characters");
        Assert.Equal(5, cut["returnedNodes"]!.GetValue<int>());
    }
}
