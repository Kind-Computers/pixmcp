using System.Text.Json;
using System.Text.Json.Nodes;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ResultProjectionTests
{
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json.Options);
    private static WhereClause Clause(string field, string op, object? value = null) => new(field, op, value is null ? null : Element(value));
    private static readonly JsonElement Row = Element(new { name = "DrawInstanced", index = 7, eventRef = new { queueIndex = 0, eventIndex = 7 }, tags = new[] { "a", "b" }, big = "18446744073709551615", none = (string?)null });

    [Theory]
    [InlineData("eq", "DrawInstanced", true)]
    [InlineData("eq", "drawinstanced", false)]
    [InlineData("ne", "Dispatch", true)]
    [InlineData("contains", "instanced", true)]
    [InlineData("startsWith", "draw", true)]
    [InlineData("startsWith", "Dispatch", false)]
    public void StringOperatorsCompareOrdinalForEqualityAndIgnoreCaseForSearch(string op, string value, bool expected)
        => Assert.Equal(expected, ResultProjection.Matches(Row, [Clause("name", op, value)]));

    [Theory]
    [InlineData("gt", 6, true)]
    [InlineData("gt", 7, false)]
    [InlineData("ge", 7, true)]
    [InlineData("lt", 8, true)]
    [InlineData("le", 6, false)]
    [InlineData("eq", 7.0, true)]
    public void NumericOperatorsCompareAsNumbers(string op, double value, bool expected)
        => Assert.Equal(expected, ResultProjection.Matches(Row, [Clause("index", op, value)]));

    [Fact]
    public void NestedPointersArraysBigIntegersAndExistenceWork()
    {
        Assert.True(ResultProjection.Matches(Row, [Clause("eventRef/eventIndex", "eq", 7), Clause("/eventRef/queueIndex", "in", new[] { 0, 1 })]));
        Assert.True(ResultProjection.Matches(Row, [Clause("tags", "contains", "b")]));
        Assert.False(ResultProjection.Matches(Row, [Clause("tags", "contains", "c")]));
        Assert.True(ResultProjection.Matches(Row, [Clause("tags/1", "eq", "b")]));
        Assert.True(ResultProjection.Matches(Row, [Clause("big", "gt", 1e18)])); // decimal strings above 2^53 compare numerically
        Assert.True(ResultProjection.Matches(Row, [Clause("name", "exists")]));
        Assert.False(ResultProjection.Matches(Row, [Clause("none", "exists")]));
        Assert.True(ResultProjection.Matches(Row, [Clause("missing", "exists", false)]));
        Assert.False(ResultProjection.Matches(Row, [Clause("missing", "eq", 1)]));
        Assert.True(ResultProjection.Matches(Row, [Clause("missing", "ne", 1)]));
        Assert.False(ResultProjection.Matches(Row, [Clause("name", "eq", "DrawInstanced"), Clause("index", "eq", 8)])); // clauses are ANDed
    }

    [Fact]
    public void SelectKeepsRequestedFieldsUnderTheirSpecAndOmitsMissingOnes()
    {
        JsonObject projected = ResultProjection.Select(Row, ["name", "eventRef/eventIndex", "missing"]);
        Assert.Equal("DrawInstanced", projected["name"]!.GetValue<string>());
        Assert.Equal(7, projected["eventRef/eventIndex"]!.GetValue<int>());
        Assert.False(projected.ContainsKey("missing"));
        Assert.Equal(2, projected.Count);
    }

    [Fact]
    public void MalformedProjectionsAreInvalidArguments()
    {
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ResultProjection.Validate(Enumerable.Repeat("f", 33).ToArray(), null)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ResultProjection.Validate(null, Enumerable.Repeat(Clause("f", "eq", 1), 9).ToArray())).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ResultProjection.Validate(null, [Clause("f", "like", 1)])).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ResultProjection.Validate(null, [Clause("f", "in", 1)])).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ResultProjection.Validate(null, [Clause("f", "eq")])).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ResultProjection.Validate([""], null)).Detail.Code);
        ResultProjection.Validate(["a"], [Clause("f", "exists")]);
    }

    [Fact]
    public void StoreQueriesFilterProjectAndContinueWithMatchedTotals()
    {
        using var store = new ResultStore();
        string reference = store.Store(new { items = Enumerable.Range(0, 200).Select(i => new { i, kind = i % 2 == 0 ? "work" : "marker", name = "Event " + i }).ToArray() });
        ResultReadDto page = Assert.IsType<ResultReadDto>(store.Query(reference, "/items", 0, 10, null, ["i"], [Clause("kind", "eq", "work")]));
        Assert.Equal(100, page.Total);
        Assert.Equal(10, page.Count);
        Assert.Equal(10, page.NextOffset);
        Assert.Equal(200, page.Projection!.Scanned);
        Assert.Equal(100, page.Projection.Matched);
        Assert.Equal(0, page.Projection.Unevaluated);
        JsonElement value = JsonSerializer.SerializeToElement(page.Value, Json.Options);
        Assert.Equal(new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18 }, value.EnumerateArray().Select(row => row.GetProperty("i").GetInt32()));
        Assert.All(value.EnumerateArray(), row => Assert.Equal(1, row.EnumerateObject().Count()));
        ToolCallDto continuation = Assert.Single(page.NextCalls);
        JsonElement arguments = JsonSerializer.SerializeToElement(continuation.Arguments, Json.Options);
        Assert.Equal(10, arguments.GetProperty("offset").GetInt32());
        Assert.Equal("kind", arguments.GetProperty("where")[0].GetProperty("field").GetString());
        Assert.Equal("i", arguments.GetProperty("fields")[0].GetString());
        ResultReadDto next = Assert.IsType<ResultReadDto>(store.Query(reference, "/items", 10, 1000, null, ["i"], [Clause("kind", "eq", "work")]));
        Assert.Equal(90, next.Count);
        Assert.Null(next.NextOffset);
        // Fields without where keep the array total.
        ResultReadDto fieldsOnly = Assert.IsType<ResultReadDto>(store.Query(reference, "/items", 0, 5, null, ["name"], null));
        Assert.Equal(200, fieldsOnly.Total);
        Assert.Null(fieldsOnly.Projection!.Where);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => store.Query(reference, "", 0, 5, null, ["name"], null)).Detail.Code);
    }

    [Fact]
    public void LargeRowsProjectInlineAndOversizedRowsAreCountedAsUnevaluated()
    {
        using var store = new ResultStore();
        string reference = store.Store(new { items = new object[] { new { i = 0, blob = new string('x', 40000) }, new { i = 1, blob = "small" } } });
        ResultReadDto projected = Assert.IsType<ResultReadDto>(store.Query(reference, "/items", 0, 25, null, ["i"], null));
        Assert.Equal(2, projected.Count); // the 40 KB row projects to { i } and stays inline
        Assert.Equal(0, projected.Projection!.Unevaluated);
        using (ServerOptions.Override(ServerOptions.With(inlineResultBytes: 1024, maxResultBytes: 4096)))
        {
            ResultReadDto capped = Assert.IsType<ResultReadDto>(store.Query(reference, "/items", 0, 25, null, ["i"], [Clause("i", "ge", 0)]));
            Assert.Equal(1, capped.Projection!.Unevaluated);
            Assert.Equal(1, capped.Projection.Matched);
            Assert.Equal(1, capped.Total);
            Assert.Equal(1, JsonSerializer.SerializeToElement(capped.Value, Json.Options)[0].GetProperty("i").GetInt32());
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(-1, true)]
    public void UnfilteredProjectionKeepsOversizedRowsAndTheirPhysicalContinuations(int oversizedIndex, bool emptyWhere)
    {
        using var options = ServerOptions.Override(ServerOptions.With(inlineResultBytes: 1024, maxResultBytes: 4096));
        using var store = new ResultStore();
        string reference = store.Store(new { items = Enumerable.Range(0, 3).Select(i => new
        {
            i, blob = oversizedIndex < 0 || i == oversizedIndex ? new string('x', 8192) : "small",
        }).ToArray() });
        IReadOnlyList<WhereClause>? where = emptyWhere ? [] : null;
        int offset = 0;
        for (int i = 0; i < 3; i++)
        {
            ResultReadDto page = Assert.IsType<ResultReadDto>(store.Query(reference, "/items", offset, 1, null, ["i"], where));
            Assert.Equal(3, page.Total);
            Assert.Equal(1, page.Count);
            Assert.Equal(3, page.Projection!.Matched);
            Assert.Equal(oversizedIndex < 0 ? 3 : 1, page.Projection.Unevaluated);
            JsonElement row = Element(page.Value!)[0];
            if (oversizedIndex < 0 || i == oversizedIndex)
            {
                Assert.True(row.GetProperty("deferred").GetBoolean());
                Assert.Equal($"/items/{i}", row.GetProperty("pointer").GetString());
                JsonElement read = row.GetProperty("nextCalls")[0];
                Assert.Equal("pix_result_read", read.GetProperty("tool").GetString());
                JsonElement arguments = read.GetProperty("arguments");
                Assert.Equal(reference, arguments.GetProperty("resultRef").GetString());
                ResultReadDto original = store.Read(reference, arguments.GetProperty("pointer").GetString()!,
                    arguments.GetProperty("offset").GetInt32(), arguments.GetProperty("limit").GetInt32());
                Assert.Equal(i, Element(original.Value!).GetProperty("i").GetInt32());
            }
            else Assert.Equal(i, row.GetProperty("i").GetInt32());

            if (i < 2)
            {
                Assert.Equal(i + 1, page.NextOffset);
                JsonElement continuation = Element(Assert.Single(page.NextCalls).Arguments);
                offset = continuation.GetProperty("offset").GetInt32();
                Assert.Equal("i", continuation.GetProperty("fields")[0].GetString());
            }
            else
            {
                Assert.Null(page.NextOffset);
                Assert.Empty(page.NextCalls);
            }
        }
    }
}
