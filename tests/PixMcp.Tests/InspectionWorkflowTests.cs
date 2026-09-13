using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class InspectionWorkflowTests
{
    [Fact]
    public void ResourceArgumentEvidenceRequiresAnExactObjectIdLeaf()
    {
        const string xml = "<CopyResource><this>ID3D12GraphicsCommandList obj#14</this><pDstResource>obj#14</pDstResource><pSrcResource>obj#140</pSrcResource><ResourceOrHeap>obj#14</ResourceOrHeap><RenderTarget>res#14</RenderTarget></CopyResource>";
        Assert.Equal(new[] { "pDstResource", "ResourceOrHeap" }, ResourceTools.ResourceArgumentNames(xml, 14));
        Assert.Equal(new[] { "pSrcResource" }, ResourceTools.ResourceArgumentNames(xml, 140));
        Assert.Empty(ResourceTools.ResourceArgumentNames(xml, 1));
    }

    [Fact]
    public void MissingTimingSentinelsAreNotReportedAsDurations()
    {
        Assert.Null(InspectionTools.AvailableTime(ulong.MaxValue));
        Assert.Equal(0ul, InspectionTools.AvailableTime(0));
        Assert.Equal(123ul, InspectionTools.AvailableTime(123));
    }

    [Fact]
    public void ShaderWindowsReconstructLateFunctionsWithoutLosingLines()
    {
        string source = string.Join("\r\n", Enumerable.Range(1, 437).Select(i => $"line {i}"));
        var lines = new List<string>();
        int? next = 1;
        while (next.HasValue)
        {
            var window = ShaderText.Window(source, next.Value, 100);
            Assert.Equal(437, window.TotalLines);
            lines.AddRange(window.Code.Split('\n'));
            next = window.NextStartLine;
        }
        Assert.Equal(ShaderText.Lines(source), lines);
        Assert.Equal("line 401", ShaderText.Window(source, 401, 1).Code);
        var beyondEnd = ShaderText.Window(source, 500, 100);
        Assert.Equal(0, beyondEnd.Count);
        Assert.Null(beyondEnd.NextStartLine);
    }

    [Fact]
    public void ShaderWindowsPreserveLongLinesAndTrailingBlankLines()
    {
        string source = new string('x', 40000) + "\r\nlast\r\n";
        var first = ShaderText.Window(source, 1, 1);
        Assert.Equal(40000, first.Code.Length);
        Assert.Equal(2, first.NextStartLine);
        var tail = ShaderText.Window(source, 2, 10);
        Assert.Equal("last\n", tail.Code);
        Assert.Equal(2, tail.Count);
        Assert.Null(tail.NextStartLine);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    [InlineData(1, 1001)]
    public void InvalidShaderWindowsAreRejected(int startLine, int lineCount)
        => Assert.Throws<PixToolException>(() => ShaderText.Window("code", startLine, lineCount));

    [Fact]
    public void ShaderSearchIsLiteralCaseInsensitiveAndKeepsNodeAndContext()
    {
        string source = "before\nTexture[0].Load();\nAFTER\ntexture0.Load();\nTexture[0].Load();";
        ShaderSearchMatchDto[] matches = ShaderText.Search(source, "texture[0]", 8, "lighting.hlsl", 1).ToArray();
        Assert.Equal(new[] { 2, 5 }, matches.Select(m => m.Line));
        Assert.All(matches, m => Assert.Equal(8ul, m.NodeIndex));
        Assert.Equal(1, matches[0].StartLine);
        Assert.Equal(new[] { "before", "Texture[0].Load();", "AFTER" }, matches[0].Lines);
        Assert.Equal(2, matches[1].Lines.Count);
    }

    [Fact]
    public void ProfileReferencesRequireBothHashAndStageAndPreserveAmbiguity()
    {
        var first = new ShaderRef(new EventRef("gpu-1", 0, 10), 1);
        var second = new ShaderRef(new EventRef("gpu-1", 0, 20), 1);
        static ShaderInfoDto Identity(ShaderRef reference, string hash, string stage)
            => new(reference, reference.ShaderIndex, "1", stage, hash, null, null, null, null, 0, "0x0", []);
        ShaderInfoDto[] identities = [Identity(first, "abcdef", "PIXEL"), Identity(second, "ABCDEF", "PIXEL"),
            Identity(first, "abcdef", "VERTEX"), Identity(first, "other", "PIXEL")];
        Assert.Equal(new[] { first, second }, ShaderProfilingTools.MatchReferences(identities, "ABCDEF", "pixel"));
        Assert.Empty(ShaderProfilingTools.MatchReferences(identities, null, "PIXEL"));
    }

    [Fact]
    public async Task BadReferencesFailBeforeStartingAnyPreparationJob()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session);
        var missing = new EventRef("gpu-missing", 0, 10);
        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => InspectionTools.InspectEvent(session, jobs, missing));
        Assert.Equal("invalid_reference", error.Detail.Code);
        Assert.Equal("pix_handles", Assert.Single(error.Detail.NextCalls).Tool);
        await Assert.ThrowsAsync<PixToolException>(() => ResourceTools.EventResources(session, jobs, missing));
        await Assert.ThrowsAsync<PixToolException>(() => PipelineTools.PipelineState(session, jobs, missing));
        await Assert.ThrowsAsync<PixToolException>(() => PipelineTools.ShaderCode(session, jobs, new(missing, 0)));
        await Assert.ThrowsAsync<PixToolException>(() => ShaderProfilingTools.Profile(session, jobs, missing.Handle, scope: missing));
        Assert.Empty(jobs.All);
        Assert.Equal(0, worker.PendingCount);
    }
}
