using PixMcp.Pix.Handles;

namespace PixMcp.Tests;

/// <summary>
/// PIX-free GPU capture builder: queues of nested events with EOP/TOP timing that produce the <see cref="EventRecord"/> and
/// <see cref="EventTimingRow"/> arrays the handle caches hold, plus expectations computed independently of
/// <c>TimingTree</c> and <c>Tools.Classify</c> (inclusive EOP time and the declared kind of every event).
/// </summary>
internal sealed class SyntheticGpuCapture
{
    private readonly List<SyntheticQueue> _queues = [];

    internal IReadOnlyList<SyntheticQueue> Queues => _queues;

    internal SyntheticQueue Queue(string name = "Direct")
    {
        var queue = new SyntheticQueue(_queues.Count, name);
        _queues.Add(queue);
        return queue;
    }

    /// <summary>
    /// The TimingTreeTests capture: Frame (unmeasured) > Shadow pass (draws of 10 and 20 ns) and Main pass (measured 8 ns)
    /// > Marker > Dispatch (5 ns) plus an untimed draw; then a 1 ns Present. Frame is a mixed 38 ns sum.
    /// </summary>
    internal static SyntheticGpuCapture Canonical()
    {
        var capture = new SyntheticGpuCapture();
        capture.Queue()
            .Marker("Frame", frame => frame
                .Marker("Shadow pass", shadow => shadow.Draw("DrawInstanced(3)", eopNs: 10).Draw("DrawInstanced(6)", eopNs: 20))
                .Marker("Main pass", main => main
                    .Marker("Marker", marker => marker.Dispatch("Dispatch(1,1,1)", eopNs: 5))
                    .Draw("DrawInstanced(3)", eopNs: null), measuredEopNs: 8))
            .Present(eopNs: 1);
        return capture;
    }

    /// <summary>Classification edge cases: API names without call text, API-shaped names, timed leaf labels and markers.</summary>
    internal static SyntheticGpuCapture Kinds()
    {
        var capture = new SyntheticGpuCapture();
        capture.Queue()
            .Marker("Frame", frame => frame
                .Event("ExecuteIndirect", "", "executeIndirect", eopNs: 4)
                .Event("Foo(1,2)", "", "other", eopNs: 2)
                .Label("Bloom", eopNs: 3)
                .Event("ClearRenderTargetView", "ClearRenderTargetView(obj#3)", "clear", eopNs: 1)
                .Event("CopyResource", "CopyResource(obj#4, obj#5)", "copy", eopNs: 1)
                .Event("ResolveSubresource", "ResolveSubresource(obj#6)", "resolve", eopNs: 1)
                .Event("ResourceBarrier", "ResourceBarrier(1)", "barrier", eopNs: null)
                .Event("DrawIndexedInstanced", "DrawIndexedInstanced(6, 1, 0, 0, 0)", "draw", eopNs: 2))
            .Present(eopNs: 1);
        return capture;
    }
}

internal sealed class SyntheticQueue(int index, string name)
{
    private sealed class Entry
    {
        internal required EventRecord Record;
        internal EventTimingRow? Row;
        internal required string Kind;
        internal ulong? MeasuredEopNs;
        internal readonly List<uint> Children = [];
    }

    private readonly List<Entry> _entries = [];
    private readonly Stack<uint> _parents = new();
    private uint _nextGpuId = 1;

    internal int Index { get; } = index;
    internal string Name { get; } = name;
    internal EventRecord[] Events => _entries.Select(e => e.Record).ToArray();
    internal EventTimingRow[] Rows => _entries.Where(e => e.Row is not null).Select(e => e.Row!).ToArray();

    /// <summary>A marker without a GPU id; <paramref name="measuredEopNs"/> gives it its own timing row like a PIX-measured span.</summary>
    internal SyntheticQueue Marker(string name, Action<SyntheticQueue> children, ulong? measuredEopNs = null)
    {
        uint index = Add(name, "", "marker", gpuId: uint.MaxValue, measuredEopNs, timingRow: measuredEopNs is not null);
        _parents.Push(index);
        children(this);
        _parents.Pop();
        return this;
    }

    internal SyntheticQueue Draw(string apiCallData, ulong? eopNs, ulong eopStartNs = 0, ulong topStartNs = 0, ulong? topNs = null)
        => Api(apiCallData, "draw", eopNs, eopStartNs, topStartNs, topNs);

    internal SyntheticQueue Dispatch(string apiCallData, ulong? eopNs, ulong eopStartNs = 0, ulong topStartNs = 0, ulong? topNs = null)
        => Api(apiCallData, "dispatch", eopNs, eopStartNs, topStartNs, topNs);

    /// <summary>PIX records Present without call text.</summary>
    internal SyntheticQueue Present(ulong? eopNs, ulong eopStartNs = 0)
    {
        Add("Present", "", "present", _nextGpuId++, eopNs, timingRow: true, eopStartNs);
        return this;
    }

    /// <summary>A timed leaf with no call text: a GPU id, no children.</summary>
    internal SyntheticQueue Label(string name, ulong? eopNs, ulong eopStartNs = 0)
    {
        Add(name, "", "label", _nextGpuId++, eopNs, timingRow: true, eopStartNs);
        return this;
    }

    internal SyntheticQueue Event(string name, string apiCallData, string expectedKind, ulong? eopNs, ulong eopStartNs = 0)
    {
        Add(name, apiCallData, expectedKind, _nextGpuId++, eopNs, timingRow: true, eopStartNs);
        return this;
    }

    private SyntheticQueue Api(string apiCallData, string kind, ulong? eopNs, ulong eopStartNs, ulong topStartNs, ulong? topNs)
    {
        int open = apiCallData.IndexOf('(');
        Add(open < 0 ? apiCallData : apiCallData[..open], apiCallData, kind, _nextGpuId++, eopNs, timingRow: true, eopStartNs, topStartNs, topNs);
        return this;
    }

    private uint Add(string name, string apiCallData, string kind, uint gpuId, ulong? eopNs, bool timingRow, ulong eopStartNs = 0, ulong topStartNs = 0, ulong? topNs = null)
    {
        uint index = (uint)_entries.Count;
        uint parent = _parents.Count == 0 ? uint.MaxValue : _parents.Peek();
        var entry = new Entry { Record = new EventRecord(index, gpuId, parent, name, apiCallData, 0, 0), Kind = kind, MeasuredEopNs = eopNs };
        if (timingRow)
            entry.Row = new EventTimingRow(Index, index, gpuId, name, apiCallData, topStartNs, topNs ?? eopNs ?? 0, eopStartNs, eopNs ?? GpuCaptureHandle.TimingNone);
        if (parent != uint.MaxValue) _entries[(int)parent].Children.Add(index);
        _entries.Add(entry);
        return index;
    }

    internal bool HasChildren(uint index) => _entries[(int)index].Children.Count > 0;

    internal string ExpectedKind(uint index) => _entries[(int)index].Kind;

    /// <summary>A measured event's own EOP time, else the sum over timed children; null when nothing below is timed.</summary>
    internal ulong? ExpectedInclusiveEopNs(uint index)
    {
        Entry entry = _entries[(int)index];
        if (entry.MeasuredEopNs is ulong measured) return measured;
        ulong? sum = null;
        foreach (uint child in entry.Children)
            if (ExpectedInclusiveEopNs(child) is ulong value) sum = (sum ?? 0) + value;
        return sum;
    }
}
