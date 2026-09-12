using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ReflectedResultTests
{
    private sealed class Node
    {
        public int Value { get; init; }
        public Node? Child { get; set; }
    }

    [Fact]
    public void ReflectedArrayTailsAndDeepFieldsSurviveIntoResultSnapshots()
    {
        var root = new Node { Value = 0 };
        Node tail = root;
        for (int i = 1; i <= 12; i++) { tail.Child = new Node { Value = i }; tail = tail.Child; }
        object? reflected = Reflect.ToObject(new { nodes = root, values = Enumerable.Range(0, 1000).ToArray() });
        var store = new ResultStore();
        string reference = store.Store(reflected);
        ResultReadDto arrayTail = store.Read(reference, "/values", 990, 10);
        JsonElement json = JsonSerializer.SerializeToElement(arrayTail.Value, Json.Options);
        Assert.Equal(999, json[9].GetInt32());
        string pointer = "/nodes" + string.Concat(Enumerable.Repeat("/child", 12)) + "/value";
        Assert.Equal(12, JsonSerializer.SerializeToElement(store.Read(reference, pointer).Value).GetInt32());
    }

    [Fact]
    public void ActualCyclesAreMarkedWhileSharedSiblingsRemainComplete()
    {
        var node = new Node { Value = 7 };
        node.Child = node;
        JsonElement cycle = JsonSerializer.SerializeToElement(Reflect.ToObject(node), Json.Options);
        Assert.True(cycle.GetProperty("child").GetProperty("unavailable").GetBoolean());
        node.Child = null;
        JsonElement siblings = JsonSerializer.SerializeToElement(Reflect.ToObject(new[] { node, node }), Json.Options);
        Assert.Equal(7, siblings[0].GetProperty("value").GetInt32());
        Assert.Equal(7, siblings[1].GetProperty("value").GetInt32());
    }
}
