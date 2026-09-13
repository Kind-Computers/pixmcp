using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>API call text in the XML element form PIX 2606.18 records (strings from baseline.wpix) and the positional form.</summary>
public sealed class ApiCallParserTests
{
    private const string FixtureDraw = "<DrawInstanced><this>ID3D12GraphicsCommandList obj#11</this><VertexCountPerInstance>3</VertexCountPerInstance><InstanceCount>1</InstanceCount><StartVertexLocation>0</StartVertexLocation><StartInstanceLocation>0</StartInstanceLocation></DrawInstanced>";
    private const string FixtureIndexedDraw = "<DrawIndexedInstanced><this>ID3D12GraphicsCommandList obj#11</this><IndexCountPerInstance>3</IndexCountPerInstance><InstanceCount>1</InstanceCount><StartIndexLocation>0</StartIndexLocation><BaseVertexLocation>0</BaseVertexLocation><StartInstanceLocation>0</StartInstanceLocation></DrawIndexedInstanced>";
    private const string FixtureDispatch = "<Dispatch><this>ID3D12GraphicsCommandList obj#5</this><ThreadGroupCountX>4</ThreadGroupCountX><ThreadGroupCountY>1</ThreadGroupCountY><ThreadGroupCountZ>1</ThreadGroupCountZ></Dispatch>";
    private const string FixturePresent = "<Present><this>ID3D12SharingContract obj#1</this><pResource>obj#13</pResource><Subresource>0</Subresource><window>&#60;unknown&#62;</window></Present>";
    private const string FixtureQueue = "<CreateCommandQueue><this>ID3D12Device obj#2</this><returnValue>S_OK</returnValue><pDesc><Type>D3D12_COMMAND_LIST_TYPE_DIRECT</Type><Priority>0</Priority></pDesc><ppCommandQueue>obj#1</ppCommandQueue></CreateCommandQueue>";

    [Fact]
    public void ElementFormYieldsArgumentsObjectsAndWorkItems()
    {
        ApiCallDto draw = ApiCallParser.Parse(FixtureDraw)!;
        Assert.Equal(("DrawInstanced", "element", "ID3D12GraphicsCommandList", "vertices", (long?)3, (long?)1), (draw.Api, draw.Form, draw.Interface, draw.WorkItemKind, draw.WorkItems, draw.InstanceCount));
        ApiArgumentDto self = draw.Arguments[0];
        Assert.Equal(("this", -1, Interop.Hex(11), "ID3D12GraphicsCommandList"), (self.Name, self.Position, self.ApiObjectId, self.ObjectKind));
        Assert.Equal(("VertexCountPerInstance", 0, (long?)3), (draw.Arguments[1].Name, draw.Arguments[1].Position, draw.Arguments[1].Int64));

        ApiCallDto indexed = ApiCallParser.Parse(FixtureIndexedDraw)!;
        Assert.Equal(("indices", (long?)3), (indexed.WorkItemKind, indexed.WorkItems));
        ApiCallDto dispatch = ApiCallParser.Parse(FixtureDispatch)!;
        Assert.Equal(("threadGroups", (long?)4, (long?)null), (dispatch.WorkItemKind, dispatch.WorkItems, dispatch.InstanceCount));

        ApiCallDto present = ApiCallParser.Parse(FixturePresent)!;
        Assert.Equal((Interop.Hex(13), (string?)null), (present.Arguments.Single(a => a.Name == "pResource").ApiObjectId, present.Arguments.Single(a => a.Name == "pResource").ObjectKind));
        Assert.Equal("<unknown>", present.Arguments.Single(a => a.Name == "window").Text);
        Assert.Null(present.WorkItemKind);

        ApiCallDto queue = ApiCallParser.Parse(FixtureQueue)!;
        Assert.Contains("<Type>D3D12_COMMAND_LIST_TYPE_DIRECT</Type>", queue.Arguments.Single(a => a.Name == "pDesc").Text);
        Assert.Equal("S_OK", queue.Arguments.Single(a => a.Name == "returnValue").Text);
    }

    [Fact]
    public void PositionalFormUsesD3D12NamesAndNestedArgumentsStayWhole()
    {
        ApiCallDto draw = ApiCallParser.Parse("DrawInstanced(3, 1, 0, 0)")!;
        Assert.Equal(("positional", "VertexCountPerInstance", "vertices", (long?)3), (draw.Form, draw.Arguments[0].Name, draw.WorkItemKind, draw.WorkItems));
        Assert.Equal(("threadGroups", (long?)64), (ApiCallParser.Parse("Dispatch(8, 8, 1)")!.WorkItemKind, ApiCallParser.Parse("Dispatch(8, 8, 1)")!.WorkItems));
        ApiCallDto scoped = ApiCallParser.Parse("ID3D12GraphicsCommandList::DrawIndexedInstanced(6, 2, 0, 0, 0)")!;
        Assert.Equal(("DrawIndexedInstanced", (long?)12, (long?)2), (scoped.Api, scoped.WorkItems, scoped.InstanceCount));
        ApiCallDto indirect = ApiCallParser.Parse("ExecuteIndirect(obj#4, 16, obj#5, 0, NULL, 0)")!;
        Assert.Equal(("maxCommands", (long?)16, Interop.Hex(4)), (indirect.WorkItemKind, indirect.WorkItems, indirect.Arguments[0].ApiObjectId));
        ApiCallDto nested = ApiCallParser.Parse("Foo(1, (2, 3), {4, 5}, 0x10, 2.5)")!;
        Assert.Equal(new[] { "arg0", "arg1", "arg2", "arg3", "arg4" }, nested.Arguments.Select(a => a.Name));
        Assert.Equal(("(2, 3)", (long?)16, (double?)2.5), (nested.Arguments[1].Text, nested.Arguments[3].Int64, nested.Arguments[4].Double));
        Assert.Empty(ApiCallParser.Parse("ClearState()")!.Arguments);
    }

    [Theory]
    [InlineData("<Broken><x>1</Broken>")]
    [InlineData("Present")]
    [InlineData("<<<>>>")]
    [InlineData("Dispatch(99999999999, 99999999999, 99999999999)")]
    public void GarbageNeverThrows(string text)
    {
        ApiCallDto parsed = ApiCallParser.Parse(text, "Event")!;
        Assert.NotNull(parsed);
        if (parsed.Form == "unparsed") Assert.Equal("Event", parsed.Api);
        else Assert.Equal(long.MaxValue, parsed.WorkItems);
    }

    [Fact]
    public void EmptyTextHasNoCall()
    {
        Assert.Null(ApiCallParser.Parse(""));
        Assert.Null(ApiCallParser.Parse(null));
    }
}
