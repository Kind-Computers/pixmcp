using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class JsonRoundTripTests
{
    [Theory]
    [InlineData("\"GRAPHICS\"")]
    [InlineData("\"graphics\"")]
    [InlineData("\"PIX_QUEUE_TYPE_GRAPHICS\"")]
    public void EnumConverterReadsWhatItWritesAndFullNames(string json)
    {
        Assert.Equal(PIX_QUEUE_TYPE.PIX_QUEUE_TYPE_GRAPHICS, JsonSerializer.Deserialize<PIX_QUEUE_TYPE>(json, Json.Options));
    }

    [Fact]
    public void EnumConverterRoundTripsEveryQueueType()
    {
        foreach (PIX_QUEUE_TYPE value in Enum.GetValues<PIX_QUEUE_TYPE>())
        {
            string json = JsonSerializer.Serialize(value, Json.Options);
            Assert.Equal(value, JsonSerializer.Deserialize<PIX_QUEUE_TYPE>(json, Json.Options));
        }
    }

    [Fact]
    public void UnknownEnumTextStillFails()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<PIX_QUEUE_TYPE>("\"NOT_A_QUEUE\"", Json.Options));
    }

    [Fact]
    public void CollectCountsEverythingButProjectsOnlyTheWindow()
    {
        int projected = 0;
        PageResult<object> page = Paging.Collect(Enumerable.Range(0, 25), offset: 10, limit: 5, i => { projected++; return i; }, new { note = "x" });
        Assert.Equal(25, page.Total);
        Assert.Equal(5, page.Count);
        Assert.Equal(15, page.NextOffset);
        Assert.Equal(5, projected);
        Assert.Equal(10, page.Items[0]);
        Assert.NotNull(page.Extra);

        PageResult<object> unavailable = Paging.Unavailable(0, 100, "events", null, "nothing recorded");
        Assert.Equal(0, unavailable.Total);
        Assert.Empty(unavailable.Items);
        Assert.Contains("nothing recorded", Json.Serialize(unavailable));
    }
}
