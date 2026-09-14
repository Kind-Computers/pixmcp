using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Pins the registered tool set: Fixtures/tool-names.txt lists it exactly and the README tool catalog names every tool once.</summary>
public sealed class ToolCatalogTests
{
    private static string[] Registered() => typeof(ServerHost).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>()
        .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void RegisteredToolsMatchThePinnedList()
    {
        string[] registered = Registered();
        Assert.Equal(registered.Length, registered.Distinct().Count());
        string[] pinned = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tool-names.txt")).Where(l => l.Length > 0).ToArray();
        Assert.True(pinned.SequenceEqual(registered),
            "Update tests/PixMcp.Tests/Fixtures/tool-names.txt (ordinal order). Added: " + string.Join(", ", registered.Except(pinned))
            + "; removed: " + string.Join(", ", pinned.Except(registered)));
    }

    [Fact]
    public void ReadmeToolCatalogNamesEveryRegisteredToolOnce()
    {
        string root = TestArtifacts.Root ?? throw new Xunit.Sdk.XunitException("Repository root not found.");
        string readme = File.ReadAllText(Path.Combine(root, "README.md")).ReplaceLineEndings("\n");
        int start = readme.IndexOf("\n## Tool catalog\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no Tool catalog section.");
        int end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string catalog = readme[start..(end < 0 ? readme.Length : end)];
        string[] listed = Regex.Matches(catalog, @"`(pix_[a-z0-9_]+)`").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(listed.Length, listed.Distinct().Count());
        string[] registered = Registered();
        Assert.True(listed.Order(StringComparer.Ordinal).SequenceEqual(registered),
            "README tool catalog drift. Missing: " + string.Join(", ", registered.Except(listed)) + "; unknown: " + string.Join(", ", listed.Except(registered)));
    }
}
