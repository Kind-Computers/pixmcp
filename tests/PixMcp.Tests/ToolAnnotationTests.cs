using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Tool annotations tell the truth: titles everywhere, closed world, and no ReadOnly tool that replays another handle, spawns pixtool or writes files.</summary>
public sealed class ToolAnnotationTests
{
    private static (string Name, McpServerToolAttribute Attribute, MethodInfo Method)[] Tools() => ToolDocumentationTests.ToolMethods()
        .Select(m => (m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, m.GetCustomAttribute<McpServerToolAttribute>()!, m)).ToArray();

    [Fact]
    public void EveryToolHasATitle()
    {
        var missing = Tools().Where(t => string.IsNullOrWhiteSpace(t.Attribute.Title)).Select(t => t.Name).ToArray();
        Assert.Empty(missing);
        Assert.All(Tools(), t => Assert.True(t.Attribute.Title!.Length <= 60, $"{t.Name} title is too long"));
    }

    [Fact]
    public void OnlyDeviceConnectIsOpenWorld()
    {
        Assert.All(Tools(), t => Assert.Equal(t.Name == "pix_device_connect", t.Attribute.OpenWorld));
    }

    [Theory]
    [InlineData("pix_gpu_compare", false, true)]
    [InlineData("pix_gpu_preview", false, false)]
    [InlineData("pix_gpu_screenshot", false, false)]
    [InlineData("pix_gpu_export_cpp", false, false)]
    [InlineData("pix_gpu_subcapture", false, false)]
    [InlineData("pix_gpu_overview", true, false)]
    [InlineData("pix_gpu_events", true, false)]
    [InlineData("pix_close", false, true)]
    [InlineData("pix_result_read", true, false)]
    [InlineData("pix_gpu_timing_prepare", false, false)]
    [InlineData("pix_gpu_counters_prepare", false, false)]
    [InlineData("pix_gpu_counters_read", true, false)]
    public void AnnotationsMatchTheExpectedTable(string tool, bool readOnly, bool destructive)
    {
        var entry = Assert.Single(Tools(), t => t.Name == tool);
        Assert.Equal(readOnly, entry.Attribute.ReadOnly);
        Assert.Equal(destructive, entry.Attribute.Destructive);
    }

    [Fact]
    public void IdempotentReplaysAreMarked()
    {
        foreach (string tool in new[] { "pix_gpu_preview", "pix_gpu_screenshot", "pix_gpu_timing_prepare", "pix_gpu_counters_prepare", "pix_gpu_analysis_start" })
            Assert.True(Assert.Single(Tools(), t => t.Name == tool).Attribute.Idempotent, tool);
    }

    [Fact]
    public void ToolsThatMayReplaySayItInTheirFirstSentence()
    {
        string[] replaying =
        [
            "pix_gpu_timing_events", "pix_gpu_timing_tree", "pix_gpu_counters_list", "pix_gpu_counters_read", "pix_gpu_occupancy", "pix_gpu_hf_counters",
            "pix_gpu_drpix_experiments", "pix_gpu_pipeline_state", "pix_gpu_shader_code", "pix_gpu_shader_search", "pix_gpu_shader_diagnostics",
            "pix_gpu_inspect_event", "pix_gpu_overview", "pix_gpu_event_resources", "pix_gpu_resource_uses", "pix_gpu_shaders", "pix_gpu_shader_uses",
            "pix_correlate", "pix_gpu_rollup", "pix_gpu_pipelines", "pix_gpu_queue_overlap", "pix_gpu_bubbles", "pix_gpu_resources", "pix_gpu_resource_timeline",
            "pix_gpu_shader_static_profile",
        ];
        foreach (string tool in replaying)
        {
            string description = Assert.Single(Tools(), t => t.Name == tool).Method.GetCustomAttribute<DescriptionAttribute>()!.Description;
            Assert.StartsWith("Replays the capture on the local GPU if analysis is not started.", description);
        }
        foreach (string tool in new[] { "pix_gpu_api_objects", "pix_gpu_heap", "pix_gpu_events", "pix_gpu_queues" })
            Assert.DoesNotContain("Replays the capture", Assert.Single(Tools(), t => t.Name == tool).Method.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    [Fact]
    public void RenamedToolsExistAndTheOldNamesAreGone()
    {
        string[] names = Tools().Select(t => t.Name).ToArray();
        Assert.Contains("pix_gpu_timing_prepare", names);
        Assert.Contains("pix_gpu_counters_prepare", names);
        Assert.Contains("pix_gpu_counters_read", names);
        foreach (string old in new[] { "timing_collect", "counters_start", "counters_collect" })
            Assert.DoesNotContain("pix_gpu_" + old, names); // the pre-2.0 names

    }
}
