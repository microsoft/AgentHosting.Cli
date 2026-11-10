using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Commands.Common;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Proves the packaged README only names options the parser actually accepts.</summary>
public sealed partial class ReadmeOptionTests
{
    [Theory]
    [InlineData("src", "Microsoft.Agents.Hosting.Cli", "Readme.md")]
    [InlineData("src", "Microsoft.Agents.Hosting.Cli", "agent.md")]
    [InlineData("docs", "developer", "ManualTesting.md")]
    public void EveryOptionTheReadmeNamesExists(string folder, string subfolder, string fileName)
    {
        var path = ReadmePath(folder, subfolder, fileName);

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

    private static string ReadmePath(string folder, string subfolder, string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, folder, subfolder, fileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName}.");
    }
}
