using System.ComponentModel;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record EventRef(
    [property: Description("Open GPU capture handle.")] string Handle,
    [property: Description("Queue index within this capture.")] int QueueIndex,
    [property: Description("Event index within this queue.")] uint EventIndex);

public sealed record ResourceRef(string Handle, string ApiObjectId);
public sealed record ShaderRef(EventRef EventRef, int ShaderIndex);

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
