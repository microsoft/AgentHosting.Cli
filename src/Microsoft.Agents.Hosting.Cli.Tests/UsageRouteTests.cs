using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class UsageRouteTests
{
    private static string Help(IFeatureFlags featureFlags, params string[] path)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        new AgentHostCli(new FakeOperations(), featureFlags: featureFlags)
            .InvokeAsync([.. path, "--help"], stdout, stderr, color: false).GetAwaiter().GetResult();

        return stdout.ToString().Replace("\r\n", "\n");
    }

    private static IReadOnlyList<string> UsageLines(string help)
    {
        var lines = help.Split('\n');
        var heading = Array.FindIndex(lines, line => line.Trim() == "Usage:");

        return [.. lines.Skip(heading + 1).TakeWhile(line => line.Trim().Length > 0).Select(line => line.Trim())];
    }

    [Fact]
    public void AgentCreate_RendersOneUsageLinePerRoute()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "agent", "create"));

        Assert.Equal(
            [
                "Scaffold a new MCS agent",
                "msagent agent create --agent-type MCSAgent --agent-name <agent-name> --description <description> --initial-prompt <initial-prompt> [options]",
                "Clone an existing MCS agent",
                "msagent agent create [--agent-type MCSAgent] (--mcs-clone-agent-id <id> | --mcs-clone-agent-schemaname <schema-name>) [options]",
                "Scaffold a new standalone workflow",
                "msagent agent create --agent-type MCSWorkflow --agent-name <agent-name> [options]",
                "Clone an existing standalone workflow",
                "msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <id> [options]",
                "Create a CodeAgent",
                "msagent agent create --agent-type CodeAgent --agent-name <agent-name> --language <language> --language-version <language-version> [options]",
            ],
            usage);
    }

    [Fact]
    public void DeploymentCreate_RendersBothRoutesWhenAzureIsEnabled()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "create"));

        Assert.Equal(
            [
                "Record an MCS deployment",
                "msagent deployment create (--agent-id <agent-id> | --agent-name <agent-name>) --deployment-name <deployment-name> --deployment-type <deployment-type> [options]",
                "Provision an Azure Bot Service deployment",
                "msagent deployment create --agent-id <agent-id> --deployment-name <deployment-name> --deployment-type <deployment-type> --entra-id <entra-id> --entra-id-type <entra-id-type> [options]",
            ],
            usage);
    }

    [Fact]
    public void ADisabledRouteLeavesItsUsageLineOut()
    {
        var usage = UsageLines(Help(new TestFeatureFlags(), "deployment", "create"));

        Assert.Equal(
            ["msagent deployment create (--agent-id <agent-id> | --agent-name <agent-name>) --deployment-name <deployment-name> --deployment-type <deployment-type> [options]"],
            usage);
    }

    [Fact]
    public void DeploymentUpdate_RendersTheMcsAzureAndAmsRoutes()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "update"));

        Assert.Equal(
            [
                "Rename an MCS deployment",
                "msagent deployment update (--deployment-id <deployment-id> | --deployment-name <deployment-name>) --new-name <new-name> [options]",
                "Change an Azure Bot Service deployment",
                "msagent deployment update (--deployment-id <deployment-id> | --agent-id <agent-id> --deployment-name <deployment-name>) (--deployment-type <deployment-type> | --sku <sku> | --display-name <display-name> | --description <description> | --endpoint-url <endpoint-url> | --default) [options]",
                "Point a deployment at a new package revision",
                "msagent deployment update --agent-id <agent-id> --deployment-id <deployment-id> --package-revision <package-revision> [options]",
            ],
            usage);
    }

    [Fact]
    public void AgentShare_RendersBothGrantShapesAndKeepsASwitchBare()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "agent", "share"));

        Assert.Equal(
            [
                "Grant a user access",
                "msagent agent share (--agent-id <agent-id> | --agent-name <agent-name>) --user <user> --role <role> [options]",
                "Change organization-wide access",
                "msagent agent share (--agent-id <agent-id> | --agent-name <agent-name>) --share-with-org [options]",
            ],
            usage);
    }

    [Theory]
    [InlineData("show", "Get")]
    [InlineData("delete", "Delete")]
    public void AnAddressingVerb_AdvertisesTheMcsAndAzureFormsSeparately(string verb, string summaryVerb)
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", verb));

        Assert.Equal(
            [
                $"{summaryVerb} an MCS deployment",
                $"msagent deployment {verb} (--deployment-id <deployment-id> | --deployment-name <deployment-name>) [options]",
                $"{summaryVerb} an Azure Bot Service deployment",
                $"msagent deployment {verb} (--deployment-id <deployment-id> | --agent-id <agent-id> --deployment-name <deployment-name>) [options]",
            ],
            usage);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("delete")]
    public void AnAddressingVerb_DropsTheAzureFormWhenAzureIsDisabled(string verb)
    {
        var usage = UsageLines(Help(new TestFeatureFlags(), "deployment", verb));

        Assert.Equal(
            [$"msagent deployment {verb} (--deployment-id <deployment-id> | --deployment-name <deployment-name>) [options]"],
            usage);
    }

    [Fact]
    public void EvalRun_RendersOneLinePerConversationSource()
    {
        var usage = UsageLines(Help(TestFeatureFlags.Everything, "agent", "eval", "run"));

        Assert.Equal(
            [
                "Run an existing test set",
                "msagent agent eval run --testset <testset> [options]",
                "Run conversations read from a CSV",
                "msagent agent eval run --from-csv <from-csv> --name <name> [options]",
                "Run conversations generated from the agent's description",
                "msagent agent eval run --generate <generate> --name <name> [options]",
            ],
            usage);
    }

    [Theory]
    [InlineData("msagent deployment connection list (--deployment-id <deployment-id> | --deployment-name <deployment-name>) [options]", "deployment", "connection", "list")]
    [InlineData("msagent deployment connection update (--deployment-id <deployment-id> | --deployment-name <deployment-name>) [options]", "deployment", "connection", "update")]
    [InlineData("msagent agent share show (--agent-id <agent-id> | --agent-name <agent-name>) [options]", "agent", "share", "show")]
    [InlineData("msagent agent unshare (--agent-id <agent-id> | --agent-name <agent-name>) --user <user> [options]", "agent", "unshare")]
    public void ASelectorEnforcedAtRuntimeIsAdvertisedInUsage(string expected, params string[] path)
    {
        Assert.Equal([expected], UsageLines(Help(TestFeatureFlags.AllProductSurfaces, path)));
    }

    [Theory]
    [InlineData("msagent deployment oauth list (--deployment-id <deployment-id> | --agent-id <agent-id> --deployment-name <deployment-name>) [options]", "list")]
    [InlineData("msagent deployment oauth show (--deployment-id <deployment-id> | --agent-id <agent-id> --deployment-name <deployment-name>) --connection <connection> [options]", "show")]
    [InlineData("msagent deployment oauth create (--deployment-id <deployment-id> | --agent-id <agent-id> --deployment-name <deployment-name>) --connection <connection> --provider-name <provider-name> [options]", "create")]
    public void AnOAuthVerbAdvertisesTheDeploymentAddressBeforeItsOwnRequiredOptions(string expected, string verb)
    {
        Assert.Equal([expected], UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "oauth", verb)));
    }

    [Fact]
    public void ConnectionConnectors_RendersTheDeploymentAndEnvironmentShapes()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "connection", "connectors"));

        Assert.Equal(
            [
                "List the connectors a deployment's environment serves",
                "msagent deployment connection connectors (--deployment-id <deployment-id> | --deployment-name <deployment-name>) [options]",
                "List the connectors a named environment serves",
                "msagent deployment connection connectors --environment-id <environment-id> [options]",
            ],
            usage);
    }

    [Fact]
    public void ConnectionRemove_RendersTheDeploymentAndEnvironmentShapes()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "connection", "remove"));

        Assert.Equal(
            [
                "Remove from a deployment",
                "msagent deployment connection remove (--deployment-id <deployment-id> | --deployment-name <deployment-name>) (--connection-reference <connection-reference> | --connection-id <connection-id>) [options]",
                "Delete a connection from a named environment",
                "msagent deployment connection remove --connection-id <connection-id> --environment-id <environment-id> [options]",
            ],
            usage);
    }

    [Fact]
    public void ConnectionAdd_RendersTheReferenceAndConnectionOnlyShapes()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "connection", "add"));

        Assert.Equal(
            [
                "Declare a connection reference and bind it",
                "msagent deployment connection add (--deployment-id <deployment-id> | --deployment-name <deployment-name>) --connector <connector> [options]",
                "Create a connection in a named environment",
                "msagent deployment connection add --connector <connector> --connection-only --environment-id <environment-id> [options]",
            ],
            usage);
    }

    [Fact]
    public void ASingleRouteIsNotLabelled()
    {
        var usage = UsageLines(Help(new TestFeatureFlags(), "deployment", "create"));

        Assert.Equal(
            ["msagent deployment create (--agent-id <agent-id> | --agent-name <agent-name>) --deployment-name <deployment-name> --deployment-type <deployment-type> [options]"],
            usage);
    }

    [Fact]
    public void ACommandWithoutRoutesDerivesOneLineFromItsRequiredOptions()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "identity", "create", "app"));

        Assert.Equal(
            ["msagent identity create app --tenant <tenant> --display-name <display-name> --auth-type <auth-type> --config-format <config-format> [options]"],
            usage);
    }

    [Fact]
    public void ACommandWithNoRequiredOptionKeepsTheDefaultUsageLine()
    {
        var usage = UsageLines(Help(TestFeatureFlags.AllProductSurfaces, "agent", "show"));

        Assert.Equal(["msagent agent show [options]"], usage);
    }

    private static IReadOnlyList<string> Sections(string help) =>
        [.. help.Split('\n').Select(line => line.TrimEnd()).Where(line => line.EndsWith(':') && !line.StartsWith(' '))];

    [Theory]
    [InlineData]
    [InlineData("auth")]
    [InlineData("env")]
    [InlineData("telemetry")]
    [InlineData("deployment", "connection")]
    public void AGroupWithoutSubgroupsListsCommandsBeforeOptions(params string[] path)
    {
        Assert.Equal(["Description:", "Usage:", "Commands:", "Options:"], Sections(Help(TestFeatureFlags.AllProductSurfaces, path)));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("deployment")]
    public void AGroupWithSubgroupsListsSubgroupsThenCommandsThenOptions(params string[] path)
    {
        Assert.Equal(["Description:", "Usage:", "Subgroups:", "Commands:", "Options:"], Sections(Help(TestFeatureFlags.AllProductSurfaces, path)));
    }

    [Fact]
    public void ALeafCommandKeepsItsArgumentsBeforeItsOptions()
    {
        Assert.Equal(["Description:", "Usage:", "Arguments:", "Options:"], Sections(Help(TestFeatureFlags.AllProductSurfaces, "env", "select")));
    }

    [Fact]
    public void EveryOptionRowAlignsOnTheLongOptionColumn()
    {
        foreach (var path in (string[][])[["deployment", "create"], ["agent", "eval", "run"], ["deployment", "list"], ["auth", "login"]])
        {
            var declared = OptionRows(Help(TestFeatureFlags.Everything, path))
                .Where(row => row.StartsWith("  -", StringComparison.Ordinal))
                .ToList();

            Assert.All(declared, row => Assert.Equal(declared[0].IndexOf("--", StringComparison.Ordinal), row.IndexOf("--", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Wrap_KeepsAShortDescriptionOnTheLeadLine()
    {
        Assert.Equal(["  -p  --project  Short text."], SubgroupHelpAction.Wrap("  -p  --project  ", "Short text.", 17, 80));
    }

    [Fact]
    public void Wrap_BreaksALongDescriptionOnWordsAndIndentsTheRest()
    {
        var wrapped = SubgroupHelpAction.Wrap("  -p  --project  ", "Project directory holding the configuration, defaulting to the current directory.", 17, 60);

        Assert.True(wrapped.Count > 1, "a description wider than the terminal wraps");
        Assert.All(wrapped, line => Assert.True(line.Length <= 60, $"'{line}' fits the terminal"));
        Assert.StartsWith("  -p  --project  Project", wrapped[0], StringComparison.Ordinal);
        Assert.All(wrapped.Skip(1), line => Assert.Equal(17, line.Length - line.TrimStart().Length));
        Assert.DoesNotContain(wrapped, line => line.TrimEnd().EndsWith(' '));
        Assert.Equal(
            "Project directory holding the configuration, defaulting to the current directory.",
            string.Join(' ', wrapped.Select(line => line.Trim()).Select((line, index) => index == 0 ? line["-p  --project  ".Length..] : line)));
    }

    [Fact]
    public void Wrap_NeverSplitsTheOptionNameOffItsRow()
    {
        var wrapped = SubgroupHelpAction.Wrap("  -vlong  --very-long-option-name  ", "A description long enough to need more than one line at this width.", 35, 60);

        Assert.StartsWith("  -vlong  --very-long-option-name  A", wrapped[0], StringComparison.Ordinal);
        Assert.DoesNotContain(wrapped.Skip(1), line => line.Contains("--", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrap_LeavesTheRowIntactWhenTheTerminalIsTooNarrowToWrapInto()
    {
        var description = "A description that cannot be wrapped into the space that is left.";

        Assert.Equal(["  -p  --project  " + description], SubgroupHelpAction.Wrap("  -p  --project  ", description, 56, 60));
    }

    [Fact]
    public void Wrap_HardBreaksAWordWiderThanTheColumn()
    {
        var wrapped = SubgroupHelpAction.Wrap("  -p  --project  ", new string('x', 90), 17, 60);

        Assert.All(wrapped, line => Assert.True(line.Length <= 60, $"'{line}' fits the terminal"));
        Assert.Equal(90, wrapped.Sum(line => line.Trim().Length) - "-p  --project".Length - 2);
    }

    private static IReadOnlyList<string> OptionRows(string help)
    {
        var lines = help.Split('\n').Select(line => line.TrimEnd()).ToArray();
        var heading = Array.FindIndex(lines, line => line.Trim() == "Options:");

        return [.. lines.Skip(heading + 1).TakeWhile(line => line.Trim().Length > 0)];
    }

    [Fact]
    public void TheOptionsTableDropsTheValuePlaceholderAndAlignsEveryColumn()
    {
        var rows = OptionRows(Help(TestFeatureFlags.AllProductSurfaces, "deployment", "create"));

        Assert.DoesNotContain(rows, row => row.Contains('<', StringComparison.Ordinal));

        var declared = rows.Where(row => row.StartsWith("  -", StringComparison.Ordinal)).ToList();

        Assert.All(declared, row => Assert.Equal(declared[0].IndexOf("--", StringComparison.Ordinal), row.IndexOf("--", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARequiredOptionKeepsItsMarkerWithoutThePlaceholder()
    {
        var rows = OptionRows(Help(TestFeatureFlags.Everything, "agent", "eval", "testset", "create"));

        Assert.Contains(rows, row => row.TrimEnd().StartsWith("  -n ", StringComparison.Ordinal) && row.Contains("--name (REQUIRED)", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains('<', StringComparison.Ordinal));
    }

    [Fact]
    public void AnArgumentKeepsItsAngleBrackets()
    {
        var help = Help(TestFeatureFlags.AllProductSurfaces, "env", "select");

        Assert.Contains("  <environment-id>  Environment id to make active.", help, StringComparison.Ordinal);
        Assert.Contains("msagent env select <environment-id> [options]", help, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupKeepsItsSubgroupUsageLine()
    {
        Assert.Contains("msagent deployment [subgroup] [command] [options]", Help(TestFeatureFlags.AllProductSurfaces, "deployment"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--agent-type", "--agent-type")]
    [InlineData("[--agent-type MCSAgent]", "--agent-type")]
    [InlineData("(--mcs-clone-agent-id <id> | --mcs-clone-agent-schemaname <schema-name>)", "--mcs-clone-agent-id", "--mcs-clone-agent-schemaname")]
    [InlineData("[--deployment-id <id> | --deployment-name <name>]", "--deployment-id", "--deployment-name")]
    public void OptionNames_ReadsEveryOptionInsideBracketsAndAlternatives(string token, params string[] expected)
    {
        Assert.Equal(expected, CommandTreeBuilder.OptionNames(token));
    }

    [Fact]
    public void EveryUsageRouteNamesOptionsTheCommandDeclares()
    {
        foreach (var (command, route) in Routes())
        {
            var declared = command.Implementation.Symbols.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var name in route.Options.SelectMany(CommandTreeBuilder.OptionNames))
            {
                Assert.True(declared.Contains(name), $"`{command.Name}` declares a usage route naming '{name}', which is not one of its options.");
            }
        }
    }

    [Fact]
    public void EveryUsageRouteIsGatedAtLeastAsTightlyAsTheOptionsItNames()
    {
        foreach (var (command, route) in Routes())
        {
            var options = command.Implementation.Symbols.ToDictionary(symbol => symbol.Name, StringComparer.Ordinal);

            foreach (var name in route.Options.SelectMany(CommandTreeBuilder.OptionNames))
            {
                Assert.True(
                    options[name].RequiredFeatures.All(route.RequiredFeatures.Contains),
                    $"`{command.Name}` names gated option '{name}' in a usage route that does not require the same features.");
            }
        }
    }

    private static IEnumerable<(CommandMetadata Command, UsageRoute Route)> Routes() =>
        CommandReflector.Discover(typeof(EnvGroup).Assembly)
            .SelectMany(CommandTree.AllCommands)
            .SelectMany(command => command.Implementation.UsageRoutes.Select(route => (command, route)));
}
