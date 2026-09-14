using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class SqlArgumentValidationTests
{
    private static Dictionary<string, JsonElement> Arguments(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Theory]
    [InlineData("pix_gpu_sql")]
    [InlineData("pix_gpu_sql_export")]
    [InlineData("pix_timing_sql")]
    public void SqlBindingsDoNotInheritToolArgumentRestrictions(string tool)
    {
        StructuredToolResults.ValidateArguments(tool, Arguments("""
            { "handle": "gpu-1", "params": { "kind": "application-defined", "limit": 2000,
              "offset": -1, "maxRows": 0, "rangeMode": "custom" } }
            """));

        PixToolException error = Assert.Throws<PixToolException>(() =>
            StructuredToolResults.ValidateArguments(tool, Arguments("""{ "maxRows": 0 }""")));
        Assert.Equal(PixErrors.Codes.InvalidArguments, error.Detail.Code);
    }

    [Theory]
    [InlineData("pix_gpu_sql")]
    [InlineData("pix_gpu_sql_export")]
    public void SqlToolsStillValidateNestedEventReferences(string tool)
    {
        PixToolException error = Assert.Throws<PixToolException>(() => StructuredToolResults.ValidateArguments(tool,
            Arguments("""{ "scope": { "handle": "gpu-1", "queueIndex": -1, "eventIndex": 1 } }""")));
        Assert.Equal(PixErrors.Codes.InvalidArguments, error.Detail.Code);
    }
}
