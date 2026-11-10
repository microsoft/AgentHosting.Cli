using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins the CLI surface before the command-model restructure moves it. Decision D5 is
/// "byte-identical"; these snapshots are what make that checkable rather than intentional.
/// </summary>
public sealed class CommandSurfaceSnapshotTests
{
    private static string Help(params string[] path) => Help(TestFeatureFlags.AllProductSurfaces, path);

    private static string Help(IFeatureFlags featureFlags, params string[] path)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        new AgentHostCli(new FakeOperations(), featureFlags: featureFlags)
            .InvokeAsync([.. path, "--help"], stdout, stderr, color: false).GetAwaiter().GetResult();

        return stdout.ToString().Replace("\r\n", "\n");
    }

    private static readonly string[][] Paths = [[], .. DiscoveredPaths()];

    private static IEnumerable<string[]> DiscoveredPaths() =>
        CommandReflector
            .Discover(
                typeof(EnvGroup).Assembly,
                TestFeatureFlags.AllProductSurfaces)
            .SelectMany(group => GroupPaths(group, []));

    private static IEnumerable<string[]> GroupPaths(CommandGroupMetadata group, string[] prefix)
    {
        string[] path = [.. prefix, group.Name];

        return
        [
            path,
            .. group.Commands.SelectMany(command => CommandPaths(command, path)),
            .. group.Groups.SelectMany(child => GroupPaths(child, path)),
        ];
    }

    private static IEnumerable<string[]> CommandPaths(CommandMetadata command, string[] prefix)
    {
        string[] path = [.. prefix, command.Name];

        return [path, .. command.Commands.SelectMany(child => CommandPaths(child, path))];
    }

    public static TheoryData<string[]> EveryCommandPath
    {
        get
        {
            var data = new TheoryData<string[]>();

            foreach (var path in Paths)
            {
                data.Add(path);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCommandPath))]
    public void EveryCommandRendersHelp(string[] path)
    {
        var help = Help(path);

        Assert.False(string.IsNullOrWhiteSpace(help));
        Assert.Contains("Usage:", help, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootGroupSetIsPinned()
    {
        Assert.Equal(
            ["abs", "agent", "auth", "deployment", "env", "identity", "telemetry"],
            Children(Help()).Order());
    }

    [Fact]
    public void EveryRootGroupTheHelpAdvertisesHasADiscoveredPath()
    {
        Assert.Equal(Children(Help()).Order(), Paths.Where(path => path.Length == 1).Select(path => path[0]).Order());
    }

    [Fact]
    public void EveryGroupHoldingASubgroupSplitsItsHelp()
    {
        foreach (var path in Paths.Where(path => path.Length > 0))
        {
            var help = Help(path);

            // `identity` is exempt: its only child, `create`, is presented as a command rather than a
            // subgroup, so its help keeps the default (unsplit) layout even though `create` holds children.
            if (path is ["identity"])
            {
                Assert.DoesNotContain("Subgroups:", help);
                continue;
            }

            var holdsSubgroup = Children(help).Any(child => Children(Help([.. path, child])).Count > 0);

            Assert.Equal(holdsSubgroup, help.Contains("Subgroups:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EveryGroupChildSetIsPinned()
    {
        Assert.Equal(["login", "status", "switch", "logout"], Children(Help("auth")));
        Assert.Equal(["login", "status", "switch", "logout", "setup-git"], Children(Help(TestFeatureFlags.Everything, "auth")));
        Assert.Equal(["list", "select"], Children(Help("env")));
        Assert.Equal(["package", "create", "init", "list", "show", "pull", "delete", "deploy", "share", "unshare"], Children(Help("agent")));
        Assert.Equal(["show"], Children(Help("agent", "share")));
        Assert.Equal(["connection", "channel", "oauth", "create", "list", "show", "update", "delete"], Children(Help("deployment")));
        Assert.Equal(["list", "add", "update", "remove", "connectors"], Children(Help("deployment", "connection")));
        Assert.Equal(["create", "delete", "list", "list-providers", "show", "update"], Children(Help("deployment", "oauth")));
        Assert.Equal(["create", "update", "delete", "show", "list"], Children(Help("deployment", "channel")));
        Assert.Equal(["preview", "apply"], Children(Help("abs")));
        Assert.Equal(["create"], Children(Help("identity")));
        Assert.Equal(["blueprint", "app"], Children(Help("identity", "create")));
        Assert.Equal(["enable", "disable", "status"], Children(Help("telemetry")));
    }

    [Fact]
    public void NonInteractiveHelp_FailsInsteadOfOpeningABrowserOrBrokerSignIn()
    {
        var loginHelp = Regex.Replace(Help("auth", "login"), @"\s+", " ");

        Assert.Contains("Do not open a browser or broker sign-in, fail instead.", loginHelp, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryCommandPath))]
    public void EveryOptionKeepsItsAlias(string[] path)
    {
        foreach (var (option, alias) in OptionAliases(Help(path)))
        {
            Assert.Equal(PinnedAliases[option], alias);
        }
    }

    [Fact]
    public void EveryPinnedAliasIsStillReachable()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in EveryFeaturePaths())
        {
            foreach (var (option, _) in OptionAliases(Help(TestFeatureFlags.Everything, path)))
            {
                seen.Add(option);
            }
        }

        Assert.Empty(PinnedAliases.Keys.Except(seen));
    }

    private static IEnumerable<string[]> EveryFeaturePaths() =>
    [
        [],
        .. CommandReflector
            .Discover(typeof(EnvGroup).Assembly, TestFeatureFlags.Everything)
            .SelectMany(group => GroupPaths(group, [])),
    ];

    private static List<string> Children(string help)
    {
        var lines = help.Split('\n').ToList();
        var children = new List<string>();

        foreach (var section in new[] { "Subgroups:", "Commands:" })
        {
            var start = lines.FindIndex(line => line.StartsWith(section, StringComparison.Ordinal));

            if (start < 0)
            {
                continue;
            }

            for (var index = start + 1; index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]); index++)
            {
                children.Add(lines[index].Trim().Split(' ')[0]);
            }
        }

        return children;
    }

    private static IEnumerable<(string Option, string Alias)> OptionAliases(string help)
    {
        foreach (Match match in Regex.Matches(help, @"^\s+(-[A-Za-z?][A-Za-z0-9-]*),?\s+(--[a-z][a-z0-9-]*)", RegexOptions.Multiline))
        {
            yield return (match.Groups[2].Value, match.Groups[1].Value);
        }
    }

    internal static IReadOnlyDictionary<string, string> CurrentAliases => PinnedAliases;

    private static readonly IReadOnlyDictionary<string, string> PinnedAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["--account"] = "-acc",
        ["--app-id"] = "-app",
        ["--async"] = "-as",
        ["--agent-id"] = "-aid",
        ["--agent-name"] = "-an",
        ["--agent-type"] = "-at",
        ["--app-object-id"] = "-aoid",
        ["--auth-type"] = "-aut",
        ["--bot-name"] = "-bn",
        ["--by-reference"] = "-bref",
        ["--client-id"] = "-clid",
        ["--client-secret"] = "-clsec",
        ["--cloud"] = "-ci",
        ["--commit-id"] = "-cid",
        ["--config-format"] = "-cf",
        ["--connection"] = "-con",
        ["--connection-id"] = "-conid",
        ["--connection-only"] = "-conly",
        ["--connection-reference"] = "-cref",
        ["--connector"] = "-conn",
        ["--default"] = "-def",
        ["--debug"] = "-dbg",
        ["--delete-deployed-mcs-agent"] = "-ddma",
        ["--deployment-id"] = "-did",
        ["--deployment-name"] = "-dn",
        ["--deployment-type"] = "-dt",
        ["--description"] = "-d",
        ["--device-code"] = "-dc",
        ["--display-name"] = "-disp",
        ["--dry-run"] = "-dr",
        ["--endpoint"] = "-e",
        ["--endpoint-url"] = "-eu",
        ["--entra-id"] = "-ent",
        ["--entra-id-type"] = "-eit",
        ["--environment-id"] = "-eid",
        ["--filter"] = "-flt",
        ["--force"] = "-f",
        ["--idempotency-key"] = "-ik",
        ["--identity-mode"] = "-im",
        ["--init-template"] = "-it",
        ["--initial-prompt"] = "-ip",
        ["--json"] = "-j",
        ["--language"] = "-lang",
        ["--language-version"] = "-lv",
        ["--limit"] = "-lim",
        ["--location"] = "-loc",
        ["--managed-identity-name"] = "-min",
        ["--mcp-discovery"] = "-md",
        ["--mcs-agent-source"] = "-mas",
        ["--mcs-agent-type"] = "-mat",
        ["--mcs-clone-agent-id"] = "-mcaid",
        ["--mcs-clone-agent-schemaname"] = "-mcas",
        ["--mcs-clone-environment-id"] = "-mceid",
        ["--mcs-clone-workflow-id"] = "-mcwid",
        ["--mcs-workflow-source"] = "-mws",
        ["--msa-app-id"] = "-maid",
        ["--package"] = "-pkg",
        ["--package-revision"] = "-pr",
        ["--new-name"] = "-nn",
        ["--no-color"] = "-nc",
        ["--non-interactive"] = "-ni",
        ["--oauth"] = "-oa",
        ["--object-id"] = "-oid",
        ["--observability"] = "-obs",
        ["--overwrite"] = "-ow",
        ["--parameters"] = "-par",
        ["--project"] = "-p",
        ["--provider-name"] = "-pn",
        ["--publish"] = "-pub",
        ["--redirect-uri"] = "-ru",
        ["--resource"] = "-r",
        ["--resource-group"] = "-rg",
        ["--role"] = "-rl",
        ["--schema-name"] = "-sn",
        ["--share-with-org"] = "-swo",
        ["--scope"] = "-s",
        ["--scopes"] = "-scp",
        ["--subscription"] = "-sub",
        ["--teams-sso"] = "-ts",
        ["--target"] = "-tgt",
        ["--target-environment-id"] = "-tenv",
        ["--tenant"] = "-t",
        ["--tenant-wide"] = "-tw",
        ["--user"] = "-u",
        ["--version"] = "-v",
    };
}
