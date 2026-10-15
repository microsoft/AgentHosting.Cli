using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Commands.Common;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Proves the packaged README only names options the parser actually accepts.</summary>
public sealed partial class ReadmeOptionTests
{
    [Fact]
    public void EveryOptionTheReadmeNamesExists()
    {
        if (ReadmePath() is not { } path)
        {
            return;
        }

        var unknown = File.ReadAllLines(path)
            .Where(line => line.Contains("msagent ", StringComparison.Ordinal))
            .SelectMany(line => OptionToken().Matches(line).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .Where(option => option != "--help" && !OptionCatalog.DeclaredAliases.ContainsKey(option))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unknown);
    }

    [GeneratedRegex(@"--[a-z][a-z0-9-]*")]
    private static partial Regex OptionToken();

    private static string? ReadmePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Microsoft.Agents.Hosting.Cli", "README.md");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
