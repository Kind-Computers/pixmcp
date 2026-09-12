using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ComparisonResultQueryTests
{
    private static object Row(uint index, decimal? ns, double? percent, object? fields = null) => new
    {
        baseline = new EventRef("gpu-a", 0, index), candidate = new EventRef("gpu-b", 0, index + 10),
        name = "Draw", markerPath = new[] { "Frame" }, matchMethod = "markerPath",
        deltaNs = ns, deltaPercent = percent, fields = fields ?? Array.Empty<object>(),
    };
    [Fact]
    public void DefaultKeepsStructuralChangesAndSortsMissingNumbersLastWithStableEventTies()
    {
        using var store = new ResultStore();
        string reference = store.Store(new { items = new[] { Row(3, null, null), Row(2, -20, -10), Row(1, 20, 10), Row(4, 0, null) } });
        ComparisonChangesDto result = ComparisonResultQuery.Read(store, reference);
        Assert.Equal(4, result.Total);
        Assert.Equal(new uint[] { 1, 2, 4, 3 }, result.Items.Select(EventIndex));
        Assert.Equal(new uint[] { 4, 1, 2, 3 }, ComparisonResultQuery.Read(store, reference, descending: false).Items.Select(EventIndex));
    }
    [Fact]
    public void ThresholdsUseInclusiveAbsoluteValuesAndCombineWithAnd()
    {
        using var store = new ResultStore();
        string reference = store.Store(new { items = new[] { Row(1, 20, 10), Row(2, -20, -10), Row(3, 21, 9), Row(4, 20, null), Row(5, null, null) } });
        Assert.Equal(new uint[] { 1, 2 }, ComparisonResultQuery.Read(store, reference, minDeltaNs: 20, minDeltaPercent: 10).Items.Select(EventIndex));
        Assert.Equal(2u, EventIndex(Assert.Single(ComparisonResultQuery.Read(store, reference, direction: "improvements").Items)));
        Assert.Equal(new uint[] { 3, 1, 4 }, ComparisonResultQuery.Read(store, reference, direction: "regressions").Items.Select(EventIndex));
    }
    [Fact]
    public void PagesCarryIdenticalFilterAndSortArgumentsAndDoNotReplay()
    {
        using var store = new ResultStore();
        string reference = store.Store(new { items = Enumerable.Range(0, 7).Select(i => Row((uint)i, i, i)).ToArray() });
        ComparisonChangesDto first = ComparisonResultQuery.Read(store, reference, minDeltaNs: 2, sortBy: "event", descending: false, limit: 2);
        Assert.Equal(new uint[] { 2, 3 }, first.Items.Select(EventIndex));
        Assert.Equal(5, first.Total); Assert.Equal(2, first.NextOffset);
        JsonElement next = JsonSerializer.SerializeToElement(Assert.Single(first.NextCalls).Arguments, Json.Options);
        Assert.Equal(reference, next.GetProperty("fullResultRef").GetString());
        Assert.Equal(2, next.GetProperty("offset").GetInt32()); Assert.Equal(2, next.GetProperty("minDeltaNs").GetInt32());
        Assert.Equal("event", next.GetProperty("sortBy").GetString()); Assert.False(next.GetProperty("descending").GetBoolean());
        Assert.Equal(new uint[] { 4, 5 }, ComparisonResultQuery.Read(store, reference, minDeltaNs: 2, sortBy: "event", descending: false, offset: 2, limit: 2).Items.Select(EventIndex));
    }
    [Fact]
    public void SpilledHugeFieldIsDeferredToItsOriginalComparisonPointer()
    {
        using var store = new ResultStore(0, 4 * 1024 * 1024);
        string reference = store.Store(new { items = new[] { Row(1, 1, 1, new[] { new { section = "shaders", path = "/code", before = new string('x', 1024 * 1024), after = "tail" } }) } });
        JsonElement item = Assert.Single(ComparisonResultQuery.Read(store, reference).Items);
        Assert.True(item.GetProperty("fields").GetProperty("deferred").GetBoolean());
        Assert.Equal("/items/0/fields", item.GetProperty("fields").GetProperty("pointer").GetString());
        Assert.Equal("tail", store.ReadElement(reference, "/items/0/fields/0/after").GetString());
    }
    [Theory]
    [InlineData("unknown", "event", 0, 25)]
    [InlineData("all", "unknown", 0, 25)]
    [InlineData("all", "event", -1, 25)]
    [InlineData("all", "event", 0, 0)]
    public void InvalidQueriesFailBeforeReading(string direction, string sortBy, int offset, int limit)
    {
        using var store = new ResultStore();
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ComparisonResultQuery.Read(store, "absent", direction, sortBy: sortBy, offset: offset, limit: limit)).Detail.Code);
    }
    private static uint EventIndex(JsonElement row) => row.GetProperty("baseline").GetProperty("eventIndex").GetUInt32();
}
