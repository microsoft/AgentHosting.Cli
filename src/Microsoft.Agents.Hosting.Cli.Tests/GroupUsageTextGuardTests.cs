using System.CommandLine;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins baseline defect 3.2: a group's "no command specified" line is hand-written, so it drifts
/// from the children actually built. <c>msagent agent</c> omitted <c>show</c> for two releases.
/// </summary>
public sealed class GroupUsageTextGuardTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(new FakeOperations(), featureFlags: TestFeatureFlags.AllProductSurfaces)
            .InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static List<string> BuiltChildren(params string[] groupPath)
    {
        var help = Run([.. groupPath, "--help"]).Stdout;
        var lines = help.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var children = new List<string>();

        foreach (var section in new[] { "Subgroups:", "Commands:" })
        {
            var start = lines.FindIndex(line => line.StartsWith(section, StringComparison.Ordinal));

            if (start < 0)
            {
                continue;
            }

            for (var index = start + 1; index < lines.Count; index++)
            {
                var line = lines[index];

                if (string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                children.Add(line.Trim().Split(' ')[0]);
            }
        }

        return children;
    }

    private static List<string> AdvertisedChildren(string usageLine)
    {
        var names = new List<string>();

        foreach (var label in new[] { "Valid subgroups: ", "Valid commands: " })
        {
            var start = usageLine.IndexOf(label, StringComparison.Ordinal);

            if (start < 0)
            {
                continue;
            }

            start += label.Length;
            var end = usageLine.IndexOf('.', start);
            names.AddRange(usageLine[start..end].Split(',').Select(name => name.Trim()));
        }

        return names;
    }

    public static TheoryData<string[]> Groups =>
    [
        ["auth"],
        ["env"],
        ["agent"],
        ["deployment"],
        ["abs"],
        ["identity"],
        ["identity", "create"],
        ["telemetry"],
    ];

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryGroupAdvertisesExactlyTheChildrenItBuilds(string[] groupPath)
    {
        var (exitCode, _, stderr) = Run(groupPath);
        var built = BuiltChildren(groupPath);
        var advertised = AdvertisedChildren(stderr);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(built.Except(advertised));
        Assert.Empty(advertised.Except(built));
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryGroupUsageLinePointsAtRootHelp(string[] groupPath)
    {
        Assert.Contains("Run `msagent --help` for usage.", Run(groupPath).Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentGroupAdvertisesShow()
    {
        Assert.Contains("show", AdvertisedChildren(Run("agent").Stderr));
    }

    [Fact]
    public void IdentityCreateKeepsItsDistinctWording()
    {
        Assert.Contains("No identity type specified.", Run("identity", "create").Stderr, StringComparison.Ordinal);
    }
}
