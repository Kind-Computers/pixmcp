using System.ComponentModel;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record EventRef(
    [property: Description("Open GPU capture handle.")] string Handle,
    [property: Description("Queue index within this capture.")] int QueueIndex,
    [property: Description("Event index within this queue.")] uint EventIndex);

public sealed record ResourceRef(
    [property: Description("GPU capture handle that owns the resource.")] string Handle,
    [property: Description("D3D12 API object id of the resource as 0x-prefixed hex (from pix_gpu_resources).")] string ApiObjectId);
public sealed record ShaderRef(
    [property: Description("Event whose pipeline binds the shader.")] EventRef EventRef,
    [property: Description("Index of the shader in that event's pipeline (from pix_gpu_pipeline_state).")] int ShaderIndex);

public static class EventNavigation
{
    public static string[] MarkerPath(EventRecord[] events, uint index)
    {
        var path = new List<string>();
        var visited = new HashSet<uint> { index };
        if (index >= events.Length) return [];
        uint parent = events[index].ParentIndex;
        while (parent < events.Length && visited.Add(parent))
        {
            path.Add(events[parent].Name);
            parent = events[parent].ParentIndex;
        }
        path.Reverse();
        return path.ToArray();
    }

    /// <summary>Number of direct children per event index.</summary>
    public static int[] ChildCounts(EventRecord[] events)
    {
        var counts = new int[events.Length];
        foreach (EventRecord e in events)
            if (e.ParentIndex < events.Length && e.ParentIndex != e.Index) counts[e.ParentIndex]++;
        return counts;
    }

    /// <summary>Indices that appear as some event's parent.</summary>
    public static HashSet<uint> ParentSet(EventRecord[] events)
    {
        var parents = new HashSet<uint>();
        foreach (EventRecord e in events)
            if (e.ParentIndex < events.Length && e.ParentIndex != e.Index) parents.Add(e.ParentIndex);
        return parents;
    }

    public static bool IsWithin(EventRecord[] events, uint index, uint parent)
    {
        var visited = new HashSet<uint>();
        while (index < events.Length && visited.Add(index))
        {
            if (index == parent) return true;
            index = events[index].ParentIndex;
        }
        return false;
    }
}
