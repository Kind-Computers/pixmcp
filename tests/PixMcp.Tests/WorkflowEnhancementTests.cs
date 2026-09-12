using System.Runtime.InteropServices;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class WorkflowEnhancementTests
{
    private static ShaderOccurrence Shader(uint @event, int slot = 0, string? hash = "aa", string stage = "PIXEL", string entry = "Lighting")
    {
        var reference = new ShaderRef(new("gpu-1", 0, @event), slot);
        return new(new(reference, slot, @event.ToString(), stage, hash, entry, "target", null, null, 8, "0x0", ["IL"]), ["Pass"]);
    }

    [Fact]
    public void ShaderInventoryDeduplicatesByHashAndStageButCountsDistinctEvents()
    {
        var index = new ShaderIndex([Shader(5), Shader(2, hash: "AA"), Shader(2, slot: 1), Shader(3, stage: "VERTEX")], []);
        var items = index.Inventory(null, "light", null, _ => true);
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items[0].UseCount);
        Assert.Equal(2u, items[0].Shader.ShaderRef!.EventRef.EventIndex);
        var uses = index.Uses(Shader(5).Shader.ShaderRef!, _ => true);
        Assert.Equal("hashAndStage", uses.MatchMethod);
        Assert.Equal(new uint[] { 2, 5 }, uses.Items.Select(i => i.EventRef.EventIndex));
        Assert.Equal(2, uses.Items[0].ShaderRefs.Count);
        Assert.Equal(2, index.Inventory("pixel", null, "AA", _ => true).Single().UseCount);
    }

    [Fact]
    public void MissingHashesNeverMergeEvenWithTheSameEntryAndStage()
    {
        var index = new ShaderIndex([Shader(1, hash: null), Shader(2, hash: null)], []);
        Assert.Equal(2, index.Inventory(null, null, null, _ => true).Count);
        var uses = index.Uses(Shader(1, hash: null).Shader.ShaderRef!, _ => true);
        Assert.Equal("exactOccurrence", uses.MatchMethod);
        Assert.Equal(1u, Assert.Single(uses.Items).EventRef.EventIndex);
    }

    [Fact]
    public void ShaderScopesFilterOccurrencesBeforeChoosingRepresentativesAndCounting()
    {
        var index = new ShaderIndex([Shader(1), Shader(2), Shader(3)], [new { unavailable = true }]);
        var item = Assert.Single(index.Inventory(null, null, null, r => r.EventIndex > 1));
        Assert.Equal(2, item.UseCount);
        Assert.Equal(2u, item.Shader.ShaderRef!.EventRef.EventIndex);
        Assert.Single(index.Uses(Shader(1).Shader.ShaderRef!, r => r.EventIndex == 3).Items);
        Assert.Single(index.Coverage);
        Assert.Throws<PixToolException>(() => index.Uses(new(new("gpu-1", 0, 5), 9), _ => true));
    }

    [Fact]
    public void OnlyExplicitlyUnsupportedBindingsCanBeSkippedDuringPreparation()
    {
        InspectionTools.PrepareOptionalBindings(() => throw new COMException("unsupported", unchecked((int)0x80004001)));
        InspectionTools.PrepareOptionalBindings(() => throw new PixToolException("unsupported_feature", "unsupported"));
        Assert.Throws<COMException>(() => InspectionTools.PrepareOptionalBindings(() =>
            throw new COMException("device removed", unchecked((int)0x887A0005))));
        Assert.Throws<OperationCanceledException>(() => InspectionTools.PrepareOptionalBindings(() => throw new OperationCanceledException()));
        Assert.Throws<InvalidOperationException>(() => InspectionTools.PrepareOptionalBindings(() => throw new InvalidOperationException("unexpected")));
    }

    [Fact]
    public void ScopeIdentityAndSubtreesDoNotDependOnRepeatedMarkerNames()
    {
        EventRecord[] events = [new(0, 0, uint.MaxValue, "Pass", "", 0, 0), new(1, 1, 0, "Draw", "Draw", 0, 0),
            new(2, 2, uint.MaxValue, "Pass", "", 0, 0), new(3, 3, 2, "Draw", "Draw", 0, 0)];
        Assert.Equal(new uint[] { 0, 1 }, events.Where(e => EventNavigation.IsWithin(events, e.Index, 0)).Select(e => e.Index));
        var scope = new EventRef("gpu-1", 0, 0);
        EventScope.ValidateIdentity("gpu-1", null, scope);
        Assert.Throws<PixToolException>(() => EventScope.ValidateIdentity("gpu-2", null, scope));
        Assert.Throws<PixToolException>(() => EventScope.ValidateIdentity("gpu-1", 1, scope));
    }

    [Fact]
    public void SharedResourceIndexPreservesEvidenceForMultipleResourcesWithoutConflatingBindings()
    {
        var a = new ResourceRef("gpu-1", "0x1");
        var b = new ResourceRef("gpu-1", "0x2");
        ResourceUseDto Row(ResourceRef resource, uint index, string source)
            => new(resource, null, "API_ARGUMENT", new(0, "API_PARAMETER", new("gpu-1", 0, index), ["Pass"], null) { EventSource = source });
        var index = new ResourceUseIndex([Row(a, 1, "capturedApiArgument"), Row(b, 1, "eventScopedView"), Row(a, 4, "eventScopedView")], ["coverage"]);
        Assert.Equal(new uint[] { 1, 4 }, index.For(a).Items.Select(i => i.Binding.EventRef!.EventIndex));
        Assert.Equal("eventScopedView", Assert.Single(index.For(b).Items).Binding.EventSource);
        Assert.Same(index.For(a).Coverage, index.For(b).Coverage);
        Assert.Empty(index.For(new("gpu-1", "0x3")).Items);
        const string api = "<CopyResource><dst>obj#1</dst><src>obj#2</src><label>obj#10suffix</label></CopyResource>";
        Assert.Equal(new[] { ("dst", 1ul), ("src", 2ul) }, ResourceTools.ResourceArguments(api));
    }
}
