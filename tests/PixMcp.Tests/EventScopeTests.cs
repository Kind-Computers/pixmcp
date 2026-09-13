using System.Reflection;
using Microsoft.PIX;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

public sealed class EventScopeTests
{
    // Queue 0:
    // 0 Frame
    //   1 Shadow pass        (first occurrence)
    //     2 DrawInstanced    gpu 1
    //   3 Shadow pass        (repeated name)
    //     4 DrawInstanced    gpu 2
    //   5 Main pass
    //     6 Dispatch         gpu 3
    //     7 Hello label      gpu 4 (timed leaf label)
    // 8 Present              gpu 5
    private static readonly EventRecord[] Queue0 =
    {
        new(0, uint.MaxValue, uint.MaxValue, "Frame", "", 0, 0),
        new(1, uint.MaxValue, 0, "Shadow pass", "", 0, 0),
        new(2, 1, 1, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(3, uint.MaxValue, 0, "Shadow pass", "", 0, 0),
        new(4, 2, 3, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(5, uint.MaxValue, 0, "Main pass", "", 0, 0),
        new(6, 3, 5, "Dispatch", "Dispatch(1,1,1)", 0, 0),
        new(7, 4, 5, "Hello label", "", 0, 0),
        new(8, 5, uint.MaxValue, "Present", "", 0, 0),
    };

    // Queue 1: 0 Frame > 1 Shadow pass > 2 Dispatch gpu 9; 3 Signal (no gpu id, top level).
    private static readonly EventRecord[] Queue1 =
    {
        new(0, uint.MaxValue, uint.MaxValue, "Frame", "", 0, 0),
        new(1, uint.MaxValue, 0, "Shadow pass", "", 0, 0),
        new(2, 9, 1, "Dispatch", "Dispatch(4,1,1)", 0, 0),
        new(3, uint.MaxValue, uint.MaxValue, "Signal", "<Signal/>", 0, 0),
    };

    private static EventRecord[] Events(int queue) => queue == 0 ? Queue0 : Queue1;

    private static ScopeSelection Select(EventRef? root = null, string? prefix = null, int? queueIndex = null)
        => new("gpu-1", queueIndex, root, prefix, EventScope.ParsePrefix(prefix));

    private static uint[] Selected(ScopeSelection selection, int queue)
        => Events(queue).Where(e => selection.Contains(queue, Events(queue), e.Index)).Select(e => e.Index).ToArray();

    [Fact]
    public void PrefixSelectsEveryMatchingSubtreeCaseInsensitivelyAndTolerantly()
    {
        ScopeSelection exact = Select(prefix: "Frame/Shadow pass");
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, Selected(exact, 0));
        Assert.Equal(new uint[] { 1, 3 }, exact.MatchedRoots(0, Queue0));
        Assert.False(exact.IsUnrestricted);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, Selected(Select(prefix: " frame/SHADOW PASS/ "), 0));
        Assert.Equal(new uint[] { 5, 6, 7 }, Selected(Select(prefix: "Frame/Main pass"), 0));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (uint)i).ToArray(), Selected(Select(prefix: "Frame"), 0));
        ScopeSelection none = Select(prefix: "Frame/Nope");
        Assert.Empty(Selected(none, 0));
        Assert.False(none.IsUnrestricted);
        Assert.Equal(0, none.Describe(2, Events).MatchedRootCount);
    }

    [Fact]
    public void RootAndPrefixIntersectAndDescriptionsListMatchedRoots()
    {
        ScopeSelection both = Select(root: new("gpu-1", 0, 3), prefix: "Frame/Shadow pass");
        Assert.Equal(new uint[] { 3, 4 }, Selected(both, 0));
        Assert.Empty(Selected(both, 1)); // the root pins queue 0
        ScopeDescriptionDto description = both.Describe(2, Events);
        Assert.Equal(1, description.MatchedRootCount);
        Assert.Equal(3u, Assert.Single(description.MatchedRoots).EventIndex);
        Assert.Contains("under the root", description.Semantics);

        ScopeSelection rootOnly = Select(root: new("gpu-1", 0, 5));
        Assert.Equal(new uint[] { 5, 6, 7 }, Selected(rootOnly, 0));
        Assert.Equal("event and descendants", rootOnly.Describe(2, Events).Semantics);
        Assert.Empty(rootOnly.Describe(2, Events).MatchedRoots);

        ScopeSelection prefixBothQueues = Select(prefix: "Frame/Shadow pass");
        Assert.Equal(new uint[] { 1, 2 }, Selected(prefixBothQueues, 1));
        Assert.Equal(3, prefixBothQueues.Describe(2, Events).MatchedRootCount);
        Assert.True(Select().IsUnrestricted);
        Assert.Equal("unrestricted", Select().Describe(2, Events).Semantics);
        Assert.True(Select(queueIndex: 1).Contains(1, Queue1, 3));
        Assert.False(Select(queueIndex: 1).Contains(0, Queue0, 3));
    }

    [Fact]
    public void IndexRangeCoversGpuEventsOnlyAndRejectsMarkerOnlySubtrees()
    {
        IndexRange range = EventScope.ToIndexRange(Select(root: new("gpu-1", 0, 0)), 2, Events);
        Assert.Equal((0, 2u, 7u, 1u, 4u, 4, 4), (range.QueueIndex, range.FirstIndex, range.LastIndex, range.FirstGpuId, range.LastGpuId, range.WorkEvents, range.SkippedWithoutGpuId));
        IndexRange shadow = EventScope.ToIndexRange(Select(root: new("gpu-1", 0, 3)), 2, Events);
        Assert.Equal((4u, 4u, 2u, 2u, 1), (shadow.FirstIndex, shadow.LastIndex, shadow.FirstGpuId, shadow.LastGpuId, shadow.WorkEvents));
        var error = Assert.Throws<PixToolException>(() => EventScope.ToIndexRange(Select(root: new("gpu-1", 1, 3)), 2, Events));
        Assert.Equal("invalid_reference", error.Detail.Code);
        Assert.Contains(error.Detail.NextCalls, c => c.Tool == "pix_gpu_events");
        IndexRange wholeQueue = EventScope.ToIndexRange(Select(queueIndex: 1), 2, Events);
        Assert.Equal((2u, 2u, 9u, 9u), (wholeQueue.FirstIndex, wholeQueue.LastIndex, wholeQueue.FirstGpuId, wholeQueue.LastGpuId));
    }

    [Fact]
    public void RangeConsumersNeedOneQueueAndEnumeratorsSpanQueues()
    {
        ScopeSelection twoQueues = Select(prefix: "Frame/Shadow pass");
        var error = Assert.Throws<PixToolException>(() => EventScope.SingleQueue(twoQueues, 2, Events));
        Assert.Equal("invalid_arguments", error.Detail.Code);
        Assert.Equal(2, error.Detail.NextCalls.Count);
        Assert.All(error.Detail.NextCalls, c => Assert.Equal("pix_gpu_events", c.Tool));
        Assert.True(twoQueues.Contains(0, Queue0, 2));
        Assert.True(twoQueues.Contains(1, Queue1, 2));
        Assert.Equal(0, EventScope.SingleQueue(Select(prefix: "Frame/Main pass"), 2, Events));
        Assert.Equal(1, EventScope.SingleQueue(Select(prefix: "Frame/Shadow pass", queueIndex: 1), 2, Events));
        Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => EventScope.SingleQueue(Select(prefix: "Nope"), 2, Events)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => EventScope.SingleQueue(Select(), 2, Events)).Detail.Code);
    }

    [Fact]
    public void PrefixSyntaxAndIdentityAreValidatedBeforeAnyReplay()
    {
        Assert.Null(EventScope.ParsePrefix(null));
        Assert.Equal(new[] { "Frame", "Shadow" }, EventScope.ParsePrefix(" Frame / Shadow/ "));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => EventScope.ParsePrefix("  ")).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => EventScope.ParsePrefix("/")).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => EventScope.ParsePrefix("Frame//Shadow")).Detail.Code);
        var scope = new EventRef("gpu-1", 0, 0);
        EventScope.ValidateIdentity("gpu-1", null, scope);
        Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => EventScope.ValidateIdentity("gpu-2", null, scope)).Detail.Code);
        Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => EventScope.ValidateIdentity("gpu-1", 1, scope)).Detail.Code);
    }

    [Fact]
    public void MomentHandleOrderIsEnforcedBySwapping()
    {
        var first = new PIX_EVENT_INFO { GpuId = 1, MomentHandle = 50 };
        var last = new PIX_EVENT_INFO { GpuId = 2, MomentHandle = 10 };
        Assert.True(EventScope.OrderByMoment(ref first, ref last));
        Assert.Equal((2u, 1u), (first.GpuId, last.GpuId));
        Assert.False(EventScope.OrderByMoment(ref first, ref last));
        var same = new PIX_EVENT_INFO { MomentHandle = 7 };
        var alsoSame = new PIX_EVENT_INFO { MomentHandle = 7 };
        Assert.False(EventScope.OrderByMoment(ref same, ref alsoSame));
    }

    [Fact]
    public void TimeWindowSpansTimedRowsWithTopStartFallback()
    {
        const ulong none = GpuCaptureHandle.TimingNone;
        var rows = new Dictionary<int, EventTimingRow[]>
        {
            [0] =
            [
                new(0, 2, 1, "DrawInstanced", "", 12, 5, 15, 10),       // TOP 12, EOP [15,25)
                new(0, 4, 2, "DrawInstanced", "", none, none, 30, 10),   // no TOP: window starts at EopStart 30
                new(0, 6, 3, "Dispatch", "", 0, 0, 0, none),             // untimed
                new(0, 8, 5, "Present", "", 100, 1, 100, 1),             // outside the Frame scope
            ],
        };
        var window = EventScope.ToTimeWindow(Select(root: new("gpu-1", 0, 0)), 2, Events, rows);
        Assert.Equal((12UL, 40UL, 2), window);
        Assert.Null(EventScope.ToTimeWindow(Select(root: new("gpu-1", 0, 5)), 2, Events, rows));
        Assert.Equal((100UL, 101UL, 1), EventScope.ToTimeWindow(Select(root: new("gpu-1", 0, 8)), 2, Events, rows));
        Assert.Null(EventScope.ToTimeWindow(Select(queueIndex: 1), 2, Events, rows));
    }

    [Theory]
    [InlineData("pix_gpu_events")]
    [InlineData("pix_gpu_timing_events")]
    [InlineData("pix_gpu_timing_tree")]
    [InlineData("pix_gpu_counters_read")]
    [InlineData("pix_gpu_occupancy")]
    [InlineData("pix_gpu_hf_counters")]
    [InlineData("pix_gpu_drpix_run")]
    [InlineData("pix_gpu_shader_profile")]
    [InlineData("pix_gpu_resource_uses")]
    [InlineData("pix_gpu_shaders")]
    [InlineData("pix_gpu_shader_uses")]
    [InlineData("pix_gpu_overview")]
    public void EveryScopedToolDeclaresScopeAndMarkerPathPrefix(string tool)
    {
        MethodInfo method = typeof(ServerHost).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);
        ParameterInfo scope = Assert.Single(method.GetParameters(), p => p.Name == "scope");
        Assert.Equal(typeof(EventRef), scope.ParameterType);
        ParameterInfo prefix = Assert.Single(method.GetParameters(), p => p.Name == "markerPathPrefix");
        Assert.Equal(typeof(string), prefix.ParameterType);
        Assert.All(new[] { scope, prefix }, p => Assert.False(string.IsNullOrEmpty(p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description)));
        Assert.DoesNotContain(method.GetParameters(), p => p.Name is "firstEventIndex" or "lastEventIndex" or "firstEventGpuId" or "lastEventGpuId" or "firstEventRef" or "lastEventRef" or "parentIndex" && tool != "pix_gpu_events");
    }
}
