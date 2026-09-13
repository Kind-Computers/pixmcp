using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class PromptTests
{
    private static readonly IReadOnlyDictionary<string, string?> Sample = new Dictionary<string, string?>
    {
        ["handle"] = "gpu-7",
        ["baselineHandle"] = "gpu-6",
        ["eventRef"] = "{\"handle\":\"gpu-7\",\"queueIndex\":0,\"eventIndex\":12}",
        ["scope"] = "{\"handle\":\"gpu-7\",\"queueIndex\":0,\"eventIndex\":3}",
        ["markerPathPrefix"] = "Frame/Shadow",
    };

    /// <summary>Template values copied from earlier answers rather than passed as prompt arguments.</summary>
    private static readonly string[] CopiedValues = ["fullResultRef", "resourceRef", "shaderRef", "threadRowId", "startNs", "endNs"];

    [Fact]
    public void EveryPlaybookStepCallsARegisteredToolWithArgumentsItAccepts()
    {
        Assert.Equal(10, Playbooks.All.Count);
        Assert.Equal(Playbooks.All.Count, Playbooks.All.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (Playbook playbook in Playbooks.All)
        {
            Assert.NotEmpty(playbook.Steps);
            Assert.NotEmpty(playbook.Caveats);
            Assert.NotEmpty(playbook.ExampleQuestions);
            foreach (PlaybookStep step in playbook.Steps)
            {
                Assert.True(ToolRegistry.Has(step.Tool), $"{playbook.Name} names unregistered tool {step.Tool}");
                Assert.InRange(step.Level, 0, 3);
                foreach (IReadOnlyDictionary<string, string?> arguments in new[] { Sample, new Dictionary<string, string?>() })
                {
                    ToolCallDto call = Playbooks.Call(playbook, step, arguments);
                    string raw = ((JsonElement)call.Arguments).GetRawText();
                    Assert.True(ToolRegistry.Accepts(call), $"{playbook.Name}: {step.Tool} {raw}");
                    Assert.DoesNotContain("\"$", raw);
                }
                foreach (Match match in Regex.Matches(step.Arguments, "\"\\$([A-Za-z]+)\\??\""))
                {
                    string name = match.Groups[1].Value;
                    if (name == "selection")
                        Assert.True(playbook.Arguments.Any(a => a.Name == "scope") && playbook.Arguments.Any(a => a.Name == "markerPathPrefix"), $"{playbook.Name} selects a scope without scope arguments");
                    else
                        Assert.True(CopiedValues.Contains(name) || playbook.Arguments.Any(a => a.Name == name), $"{playbook.Name} uses undeclared argument {name}");
                }
            }
        }
    }

    [Fact]
    public void ReferenceArgumentsAreEmbeddedAsObjectsAndSelectionsPreferTheScope()
    {
        Playbook bottleneck = Playbooks.Find("pix_gpu_bottleneck")!;
        JsonElement scoped = (JsonElement)Playbooks.Call(bottleneck, bottleneck.Steps[0], Sample).Arguments;
        Assert.Equal(3, scoped.GetProperty("scope").GetProperty("eventIndex").GetInt32());
        Assert.False(scoped.TryGetProperty("markerPathPrefix", out _));
        JsonElement prefixed = (JsonElement)Playbooks.Call(bottleneck, bottleneck.Steps[0], new Dictionary<string, string?> { ["handle"] = "gpu-2", ["markerPathPrefix"] = "Frame/Lighting" }).Arguments;
        Assert.Equal("Frame/Lighting", prefixed.GetProperty("markerPathPrefix").GetString());
        Assert.False(prefixed.TryGetProperty("scope", out _));

        Playbook regression = Playbooks.Find("pix_regression")!;
        JsonElement compare = (JsonElement)Playbooks.Call(regression, regression.Steps[0], new Dictionary<string, string?> { ["baselineHandle"] = "gpu-1", ["handle"] = "gpu-2" }).Arguments;
        Assert.False(compare.TryGetProperty("markerPathPrefix", out _));
        Assert.Equal("gpu-2", compare.GetProperty("candidateHandle").GetString());

        Playbook dispatch = Playbooks.Find("pix_dispatch_slow")!;
        JsonElement derived = (JsonElement)Playbooks.Call(dispatch, dispatch.Steps[1], new Dictionary<string, string?> { ["eventRef"] = Sample["eventRef"] }).Arguments;
        Assert.Equal("gpu-7", derived.GetProperty("handle").GetString());
    }

    [Fact]
    public void PromptMethodsMatchThePlaybooksAndRenderWithArgumentsSubstituted()
    {
        var prompts = typeof(PixPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (Attribute: m.GetCustomAttribute<McpServerPromptAttribute>(), Method: m))
            .Where(p => p.Attribute is not null).ToArray();
        Assert.Equal(Playbooks.All.Select(p => p.Name).Order(StringComparer.Ordinal), prompts.Select(p => p.Attribute!.Name).Order(StringComparer.Ordinal));
        foreach ((McpServerPromptAttribute? attribute, MethodInfo method) in prompts)
        {
            Playbook playbook = Playbooks.Find(attribute!.Name!)!;
            Assert.Equal(playbook.Title, attribute.Title);
            Assert.Equal(playbook.Description, method.GetCustomAttribute<DescriptionAttribute>()?.Description);
            Assert.Equal(playbook.Arguments.Select(a => a.Name), method.GetParameters().Select(p => p.Name));
            Assert.All(method.GetParameters(), p => Assert.False(string.IsNullOrWhiteSpace(p.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{playbook.Name}.{p.Name}"));

            string text = (string)method.Invoke(null, method.GetParameters().Select(p => (object?)Sample.GetValueOrDefault(p.Name!)).ToArray())!;
            Assert.Contains("\"handle\":\"gpu-7\"", text);
            Assert.Contains(playbook.Steps[0].Tool, text);
            if (playbook.Steps.FirstOrDefault(s => s.Level == 0) is { } levelZero) Assert.Contains(levelZero.Tool, text);
            Assert.Contains(Playbooks.Ladder, text);
            Assert.Contains("What these numbers do not mean:", text);
            Assert.Contains(playbook.ExampleQuestions[0], text);
            Assert.DoesNotContain("\\u00", text);

            string bare = (string)method.Invoke(null, method.GetParameters().Select(_ => (object?)null).ToArray())!;
            Assert.Contains("not given", bare);
        }
    }

    [Fact]
    public void ToolsetsHidePlaybooksWhoseRequiredToolsAreDisabled()
    {
        using (ServerOptions.Override(ServerOptions.Current with { Toolsets = new HashSet<string> { "gpu", "session" } }))
        {
            Assert.True(Playbooks.IsAvailable("pix_frame_budget"));
            Assert.True(Playbooks.IsAvailable("pix_dispatch_slow"));
            Assert.False(Playbooks.IsAvailable("pix_crash_triage"));
            Assert.False(Playbooks.IsAvailable("pix_cpu_vs_gpu"));
            Assert.False(Playbooks.IsAvailable("pix_sql_investigation"));
            string dispatch = Playbooks.Render("pix_dispatch_slow", Sample);
            Assert.DoesNotContain("pix_gpu_drpix_run", dispatch);
            Assert.Contains("pix_gpu_bottleneck", dispatch);
        }
        using (ServerOptions.Override(ServerOptions.Current with { Toolsets = new HashSet<string> { "timing", "session" } }))
        {
            Assert.True(Playbooks.IsAvailable("pix_sql_investigation"));
            string sql = Playbooks.Render("pix_sql_investigation", Sample);
            Assert.Contains("pix_timing_sql", sql);
            Assert.DoesNotContain("pix_gpu_sql", sql);
        }
        Assert.All(Playbooks.All, p => Assert.True(Playbooks.IsAvailable(p), p.Name));
    }

    [Fact]
    public void InstructionsAreShortAndNameOnlyRegisteredTools()
    {
        Assert.InRange(ServerHost.Instructions.Length, 200, 800);
        foreach (string expected in new[] { "pix_gpu_overview", "pix_job_wait", "pix_result_read", "nextCalls", "prompts", "frame latency" })
            Assert.Contains(expected, ServerHost.Instructions);
        foreach (Match match in Regex.Matches(ServerHost.Instructions, @"\bpix_[a-z_]+"))
            Assert.True(ToolRegistry.Has(match.Value), match.Value);
    }

    [Fact]
    public void LadderDocumentCoversEveryPlaybookAndNamesOnlyRegisteredTools()
    {
        Assert.NotNull(TestArtifacts.Root);
        string text = File.ReadAllText(Path.Combine(TestArtifacts.Root!, "docs", "investigation-ladder.md"));
        string[] prompts = Playbooks.All.Select(p => p.Name).ToArray();
        foreach (string prompt in prompts) Assert.Contains($"`{prompt}`", text);
        foreach (Match match in Regex.Matches(text, @"\bpix_[a-z_]+\b"))
            Assert.True(ToolRegistry.Has(match.Value) || prompts.Contains(match.Value), match.Value);
    }
}
