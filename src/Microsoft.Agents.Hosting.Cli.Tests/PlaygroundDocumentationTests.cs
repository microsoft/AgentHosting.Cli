namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundDocumentationTests
{
    [Fact]
    public void ReadmeDocumentsThePlaygroundMvpContract()
    {
        var readme = File.ReadAllText(ReadmePath());
        var section = Section(readme, "### M365 Agents Playground test (MVP)");

        Assert.Contains(
            "| `msagent test playground [--target directline\\|directengine]` |",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("`msagent test playground`", section, StringComparison.Ordinal);
        Assert.Contains("discovery service", section, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent-harness", readme, StringComparison.Ordinal);
        Assert.Contains("same agent selectors as `test ask`", section, StringComparison.Ordinal);
        Assert.Contains("Neither mode accepts `--query` or `--conversation-id`", section, StringComparison.Ordinal);
        Assert.Contains("`msagent test playground --target directline`", section, StringComparison.Ordinal);
        Assert.Contains("`directengine` (the default)", section, StringComparison.Ordinal);
        Assert.Contains("masked terminal prompt", section, StringComparison.Ordinal);
        Assert.Contains("It rejects `--non-interactive`", section, StringComparison.Ordinal);
        Assert.Contains("no authentication", section, StringComparison.Ordinal);
        Assert.Contains("22.17.1+ LTS", section, StringComparison.Ordinal);
        Assert.Contains("The user manually validated that the Direct Line connection works.", section, StringComparison.Ordinal);
        Assert.Contains("`LiveTestFeatures`", section, StringComparison.Ordinal);
        Assert.Contains("`@microsoft/m365agentsplayground`", section, StringComparison.Ordinal);
        Assert.Contains("0.2.29", section, StringComparison.Ordinal);
        Assert.Contains("`npm install -g @microsoft/m365agentsplayground@latest`", section, StringComparison.Ordinal);
        Assert.Contains("lifecycle scripts", section, StringComparison.Ordinal);
        Assert.Contains("Node.js 22", section, StringComparison.Ordinal);
        Assert.Contains("`--install-playground`", section, StringComparison.Ordinal);
        Assert.Contains("`--non-interactive`", section, StringComparison.Ordinal);
        Assert.Contains("`--plain`", section, StringComparison.Ordinal);
        Assert.Contains("`--json`", section, StringComparison.Ordinal);
        Assert.Contains("10 seconds", section, StringComparison.Ordinal);
        Assert.Contains("600 seconds", section, StringComparison.Ordinal);
        Assert.Contains("Readiness is established immediately when", section, StringComparison.Ordinal);
        Assert.Contains("the process starts.", section, StringComparison.Ordinal);
        Assert.Contains("The package owns authentication timing", section, StringComparison.Ordinal);
        Assert.DoesNotContain("60 seconds", section, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", section, StringComparison.Ordinal);
        Assert.Contains("POSIX", section, StringComparison.Ordinal);
        Assert.Contains("pending", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not rolled back", section, StringComparison.Ordinal);
        Assert.Contains("known MVP limitation", section, StringComparison.Ordinal);
    }

    private static string Section(string readme, string heading)
    {
        var start = readme.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README is missing the '{heading}' section.");

        var nextHeading = readme.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        return nextHeading < 0
            ? readme[start..]
            : readme[start..nextHeading];
    }

    private static string ReadmePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Microsoft.Agents.Hosting.Cli", "README.md");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate the CLI README.");
    }
}
