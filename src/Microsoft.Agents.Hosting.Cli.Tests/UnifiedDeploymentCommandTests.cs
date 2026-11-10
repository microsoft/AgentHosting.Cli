using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public class UnifiedDeploymentCommandTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, params string[] args) =>
        Run(operations, TestFeatureFlags.AllProductSurfaces, args);

    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, IFeatureFlags featureFlags, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(
            operations,
            featureFlags: featureFlags)
            .InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static FakeOperations Mcs() => new()
    {
        OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.DeploymentRecord },
    };

    private static string[] ArgsOf(string command, params (string Placeholder, string Value)[] fills) =>
        [.. fills
            .Aggregate(command, (text, fill) => text.Replace(fill.Placeholder, fill.Value, StringComparison.Ordinal))
            .Split('"')
            .SelectMany((part, index) => index % 2 == 1 ? new[] { part } : part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Skip(1)];

    private static FakeOperations CodeAgent() => new()
    {
        OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.DeploymentRecord },
    };

    private static FakeOperations UnresolvedAgent() => new()
    {
        OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.Unresolved },
    };

    private static FakeOperations RealResolver()
    {
        var store = ManagedAgentFixtures.Store();
        var resolver = new DeploymentAgentTypeResolver(new AgentConfigLocator(), store, store);

        return new FakeOperations { OnResolveDeploymentAgentType = resolver.Resolve };
    }

    [Theory]
    [InlineData("create", "--agent-id or --agent-name")]
    [InlineData("show", "--deployment-id or --deployment-name")]
    [InlineData("update", "--deployment-id or --deployment-name")]
    [InlineData("delete", "--deployment-id or --deployment-name")]
    public void TheRealResolverInAnUnconfiguredDirectory_NamesTheSelectorRatherThanTheAzureRoute(string verb, string selector)
    {
        var project = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);

        try
        {
            var operations = RealResolver();

            var (exitCode, _, stderr) = verb == "create"
                ? Run(operations, "deployment", "create", "--project", project, "--deployment-name", "Development", "--deployment-type", "dev")
                : Run(operations, "deployment", verb, "--project", project);

            Assert.Equal(ExitCodes.Usage, exitCode);
            Assert.Contains(selector, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(FeatureName.AzureBotServiceFeatures), stderr, StringComparison.Ordinal);
            Assert.Null(operations.LastDeploymentRequest);
            Assert.Null(operations.LastGetDeploymentRequest);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void TheRealResolverInAnUnconfiguredDirectory_StillReachesAzureForACloudAddress()
    {
        var project = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);

        try
        {
            var (_, _, stderr) = Run(RealResolver(), "deployment", "show", "--project", project, "--deployment-id", "dep-1");

            Assert.DoesNotContain("is required", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing in this project matches", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Theory]
    [InlineData("deployment", "connection", "channel", "--help")]
    [InlineData("show", "deployment", "--help")]
    [InlineData("deployment", "oauth", "badverb", "--help")]
    public void InvalidCommandPathWithHelp_IsAUsageError(params string[] args)
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), args);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Unrecognized command or argument", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidCommandPathWithHelp_StillRendersHelp()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "deployment", "oauth", "create", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Usage:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentGroup_ExposesEveryVerbOnce()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "deployment", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        foreach (var verb in new[] { "create", "list", "show", "update", "delete" })
        {
            Assert.Contains(verb, stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeploymentGroupWithoutAVerb_IsAUsageError()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "deployment");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Valid subgroups: connection, channel, oauth. Valid commands: create, list, show, update, delete.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void McsAgent_CreateRoutesToTheManagedAgentBackend()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "create", "--agent-id", "agent-1", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastAgentDeploymentCreateRequest);
        Assert.Null(operations.LastDeploymentRequest);
    }

    [Fact]
    public void CodeAgent_CreateRoutesToTheAzureBackend()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(
            operations,
            "deployment",
            "create",
            "--agent-id",
            "agent-1",
            "--deployment-name",
            "dev",
            "--deployment-type",
            "Dev",
            "--entra-id",
            "app-1",
            "--entra-id-type",
            "AppReg");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastDeploymentRequest);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }

    [Fact]
    public void CodeAgent_ShowBindsTheProjectDirectory()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "show", "--agent-id", "agent-1", "--deployment-name", "dev", "--project", "C:/agents/one");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("C:/agents/one", operations.LastGetDeploymentRequest?.Project);
    }

    [Fact]
    public void CodeAgent_UpdateBindsTheProjectDirectory()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "update", "--agent-id", "agent-1", "--deployment-name", "dev", "--sku", "S1", "--project", "C:/agents/one");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("C:/agents/one", operations.LastUpdateDeploymentRequest?.Project);
    }

    [Fact]
    public void CodeAgent_DeleteBindsTheProjectDirectory()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "delete", "--agent-id", "agent-1", "--deployment-name", "dev", "--project", "C:/agents/one");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("C:/agents/one", operations.LastDeleteDeploymentRequest?.Project);
    }

    [Theory]
    [InlineData("--entra-id", "app-1")]
    [InlineData("--subscription", "11111111-1111-1111-1111-111111111111")]
    [InlineData("--resource-group", "rg-1")]
    [InlineData("--sku", "F0")]
    public void McsAgent_RejectsCodeAgentOnlyCreateOptions(string option, string value)
    {
        var operations = Mcs();

        var (exitCode, _, stderr) = Run(operations, "deployment", "create", "--agent-id", "agent-1", "--deployment-name", "dev", "--deployment-type", "dev", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(option, stderr, StringComparison.Ordinal);
        Assert.Contains("CodeAgent", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }

    [Fact]
    public void CodeAgent_RejectsTheMcsEnvironmentOption()
    {
        var operations = CodeAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "create", "--agent-id", "agent-1", "--deployment-name", "dev", "--environment-id", "env-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--environment-id", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastDeploymentRequest);
    }

    [Fact]
    public void CodeAgent_RejectsTheMcsDeleteFlag()
    {
        var operations = CodeAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "delete", "--deployment-id", "deployment-1", "--delete-deployed-mcs-agent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--delete-deployed-mcs-agent", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeAgent_RejectsTheConnectionVerb()
    {
        var operations = CodeAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "update", "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("connection", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionRequest);
    }

    [Theory]
    [InlineData("create", "--connection", "aad-conn", "--provider-name", "Aadv2")]
    [InlineData("show", "--connection", "aad-conn")]
    [InlineData("list")]
    [InlineData("delete", "--connection", "aad-conn")]
    public void Mcs_RejectsTheOAuthVerbs(params string[] verb)
    {
        var operations = Mcs();

        var (exitCode, _, stderr) = Run(operations, ["deployment", "oauth", .. verb, "--deployment-id", "deployment-1"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("CodeAgent deployment", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastShowDeploymentOAuthRequest);
        Assert.Null(operations.LastListDeploymentOAuthRequest);
        Assert.Null(operations.LastDeleteDeploymentOAuthRequest);
    }

    [Fact]
    public void CodeAgent_OAuthShowRoutesToTheBackend()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "oauth", "show", "--deployment-id", "deployment-1", "--connection", "aad-conn");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("deployment-1", operations.LastShowDeploymentOAuthRequest?.DeploymentId);
    }

    [Fact]
    public void McsAgent_ShowRoutesToTheManagedAgentBackend()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "show", "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("deployment-1", operations.LastAgentDeploymentShowRequest?.DeploymentId);
        Assert.Null(operations.LastGetDeploymentRequest);
    }

    [Fact]
    public void CodeAgent_ShowRoutesToTheAzureBackend()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "show", "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("deployment-1", operations.LastGetDeploymentRequest?.DeploymentId);
        Assert.Null(operations.LastAgentDeploymentShowRequest);
    }

    [Fact]
    public void McsAgent_ShowCarriesEverySelector()
    {
        var operations = Mcs();

        Run(operations, "deployment", "show", "--agent-id", "agent-9", "--deployment-name", "prod", "--project", "C:\\proj");

        var request = operations.LastAgentDeploymentShowRequest;

        Assert.Equal("agent-9", request?.AgentId);
        Assert.Equal("prod", request?.DeploymentName);
        Assert.Equal("C:\\proj", request?.Project);
    }

    [Fact]
    public void CodeAgent_ListReachesTheSharedStore()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "list", "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentListRequest?.AgentId);
    }

    [Fact]
    public void List_IsAgentTypeAgnostic()
    {
        var mcs = Mcs();
        var codeAgent = CodeAgent();

        Run(mcs, "deployment", "list", "--agent-id", "agent-1");
        Run(codeAgent, "deployment", "list", "--agent-id", "agent-1");

        Assert.Equal(mcs.LastAgentDeploymentListRequest?.AgentId, codeAgent.LastAgentDeploymentListRequest?.AgentId);
    }

    [Fact]
    public void List_WithoutASelector_ListsEveryAgent()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "list");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastAgentDeploymentListRequest);
        Assert.Null(operations.LastAgentDeploymentListRequest?.AgentId);
        Assert.Null(operations.LastAgentDeploymentListRequest?.AgentName);
    }

    [Theory]
    [InlineData("--agent")]
    [InlineData("--name")]
    [InlineData("--type")]
    [InlineData("--deployment")]
    [InlineData("--environment")]
    [InlineData("--endpoint")]
    public void PreUnificationSpellingsAreRejected(string removed)
    {
        var (exitCode, _, stderr) = Run(Mcs(), "deployment", "create", removed, "value");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"Unrecognized command or argument '{removed}'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalSpellingsBindTheRequest()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "create", "--agent-id", "agent-1", "--deployment-name", "dev", "--deployment-type", "prod");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentCreateRequest?.AgentId);
        Assert.Equal("dev", operations.LastAgentDeploymentCreateRequest?.DeploymentName);
        Assert.Equal("prod", operations.LastAgentDeploymentCreateRequest?.DeploymentType);
    }

    [Fact]
    public void ShortSelectorAliasesStillBind()
    {
        var operations = Mcs();

        Run(operations, "deployment", "delete", "-did", "deployment-1");

        Assert.Equal("deployment-1", operations.LastAgentDeploymentDeleteRequest?.DeploymentId);
    }

    [Fact]
    public void TheMcsDeploymentGroupNoLongerExists()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "mcs-deployment", "list", "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Unrecognized command", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RootHelpNamesOnlyTheUnifiedGroup()
    {
        var (_, stdout, _) = Run(new FakeOperations(), "--help");

        Assert.Contains("deployment", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("mcs-deployment", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateWithoutAnyOption_NamesEveryMissingRequirement()
    {
        var (exitCode, _, stderr) = Run(CodeAgent(), "deployment", "create");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Option '--agent-id' is required.", stderr, StringComparison.Ordinal);
        Assert.Contains("Option '--deployment-name' is required.", stderr, StringComparison.Ordinal);
        Assert.Contains("Option '--entra-id' is required.", stderr, StringComparison.Ordinal);
        Assert.Contains("Run `msagent --help` for usage.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void McsCreateWithoutAStage_NamesTheMissingRequirement()
    {
        var (exitCode, _, stderr) = Run(Mcs(), "deployment", "create", "--agent-id", "agent-1", "--deployment-name", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Option '--deployment-type' is required.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInAProjectThatNamesNoAgent_AsksForTheAgentInsteadOfTheAzureOptions()
    {
        var operations = UnresolvedAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "create", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Option '--agent-id or --agent-name' is required.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--entra-id", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastDeploymentRequest);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }

    [Fact]
    public void McsAgent_CreateBindsTheAgentName()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "create", "--agent-name", "My agent", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("My agent", operations.LastAgentDeploymentCreateRequest?.AgentName);
        Assert.Null(operations.LastAgentDeploymentCreateRequest?.AgentId);
        Assert.Null(operations.LastDeploymentRequest);
    }

    [Fact]
    public void CreateWithTheAgentNameAlone_ShapesTheRoutingQueryAsMcs()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "create", "--agent-name", "My agent", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("My agent", operations.LastDeploymentAgentTypeQuery?.AgentName);
        Assert.True(operations.LastDeploymentAgentTypeQuery?.HasMcsOptions);
        Assert.False(operations.LastDeploymentAgentTypeQuery?.HasCodeAgentOptions);
    }

    [Fact]
    public void CodeAgent_RejectsTheMcsAgentNameOption()
    {
        var operations = CodeAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "create", "--agent-name", "My agent", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--agent-name", stderr, StringComparison.Ordinal);
        Assert.Contains("MCS", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastDeploymentRequest);
    }

    [Fact]
    public void CreateWithTheAzureOptionShape_StillReachesTheAzureRoute()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.OptionShape },
        };

        var (exitCode, _, _) = Run(
            operations,
            "deployment",
            "create",
            "--agent-id",
            "agent-1",
            "--deployment-name",
            "dev",
            "--deployment-type",
            "dev",
            "--entra-id",
            "app-1",
            "--entra-id-type",
            "AppReg");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastDeploymentRequest);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("update")]
    [InlineData("delete")]
    public void AnUnresolvedSelector_NamesWhatItRejectedRatherThanTheAzureRoute(string verb)
    {
        var operations = UnresolvedAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", verb, "--agent-id", "agent-missing");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Nothing in this project matches --agent-id 'agent-missing'.", stderr, StringComparison.Ordinal);
        Assert.Contains("msagent deployment list", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("is required", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FeatureName.AzureBotServiceFeatures), stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastGetDeploymentRequest);
        Assert.Null(operations.LastAgentDeploymentShowRequest);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("update")]
    [InlineData("delete")]
    public void AnUnstoredCloudAddress_ReachesTheAzureRouteRatherThanBeingRefused(string verb)
    {
        var operations = UnresolvedAgent();

        var (_, _, stderr) = Run(operations, "deployment", verb, "--agent-id", "agent-missing", "--deployment-name", "dev");

        Assert.DoesNotContain("Nothing in this project matches", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("The project does not identify a single agent", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("update")]
    [InlineData("delete")]
    public void AnUnstoredCloudAddress_IsStillRefusedWhenAzureIsDisabled(string verb)
    {
        var operations = UnresolvedAgent();

        var (exitCode, _, stderr) = Run(
            operations,
            new TestFeatureFlags(FeatureName.AmsFeatures, FeatureName.IdentityFeatures),
            "deployment",
            verb,
            "--agent-id",
            "agent-missing",
            "--deployment-name",
            "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Nothing in this project matches", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FeatureName.AzureBotServiceFeatures), stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedCreateThatNamesAnAgent_ReportsTheMismatchRatherThanTheAzureRoute()
    {
        var operations = UnresolvedAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "create", "--agent-id", "agent-missing", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Nothing in this project matches --agent-id 'agent-missing'.", stderr, StringComparison.Ordinal);
        Assert.Contains("msagent agent show", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastDeploymentRequest);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }

    [Fact]
    public void AnUnresolvedCreateThatNamesNoAgent_StillAsksForTheAgentSelector()
    {
        var operations = UnresolvedAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "create", "--deployment-name", "dev", "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Option '--agent-id or --agent-name' is required.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FeatureName.AzureBotServiceFeatures), stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutingQuery_CarriesTheSelectorsAndOptionShape()
    {
        var operations = Mcs();

        Run(operations, "deployment", "delete", "--agent-id", "agent-1", "--deployment-name", "dev", "--project", "../elsewhere", "--delete-deployed-mcs-agent");

        Assert.NotNull(operations.LastDeploymentAgentTypeQuery);
        Assert.Equal("agent-1", operations.LastDeploymentAgentTypeQuery!.AgentId);
        Assert.Equal("dev", operations.LastDeploymentAgentTypeQuery.DeploymentName);
        Assert.Equal("../elsewhere", operations.LastDeploymentAgentTypeQuery.Project);
        Assert.True(operations.LastDeploymentAgentTypeQuery.HasMcsOptions);
    }

    [Fact]
    public void Update_TreatsTheEndpointUrlAsACodeAgentOption()
    {
        var operations = Mcs();

        Run(operations, "deployment", "update", "--deployment-id", "dep-1", "--endpoint-url", "https://contoso.example/api/messages");

        Assert.True(operations.LastDeploymentAgentTypeQuery?.HasCodeAgentOptions);
    }

    [Fact]
    public void Update_RefusesTheEndpointUrlOnAnMcsDeployment()
    {
        var (exitCode, _, stderr) = Run(Mcs(), "deployment", "update", "--deployment-id", "dep-1", "--endpoint-url", "https://contoso.example/api/messages", "--new-name", "dev2");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--endpoint-url applies to a CodeAgent deployment", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionUpdate_BindsTheByReferenceSwitch()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "update", "-aid", "agent-1", "-dn", "dev", "--by-reference");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(operations.LastAgentDeploymentConnectionRequest?.ByReference);
    }

    [Fact]
    public void ConnectionUpdate_DefaultsToTheConnectorView()
    {
        var operations = Mcs();

        Run(operations, "deployment", "connection", "update", "-aid", "agent-1", "-dn", "dev");

        Assert.False(operations.LastAgentDeploymentConnectionRequest?.ByReference);
    }

    [Fact]
    public void ConnectionAdd_RendersJson()
    {
        var operations = Mcs();

        var (exitCode, stdout, _) = Run(operations, "deployment", "connection", "add", "--deployment-name", "dev", "--connector", "shared_teams", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("connection-reference-bound", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionList_BindsTheByReferenceSwitch()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "list", "--deployment-name", "dev", "--by-reference");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(operations.LastAgentDeploymentConnectionShowRequest?.ByReference);
    }

    [Fact]
    public void ConnectionList_DefaultsToTheConnectorView()
    {
        var operations = Mcs();

        Run(operations, "deployment", "connection", "list", "--deployment-name", "dev");

        Assert.False(operations.LastAgentDeploymentConnectionShowRequest?.ByReference);
    }

    [Fact]
    public void ConnectionList_ShowsAWorkflowsPortalLabelAsText_AndOnlyItsStateAsJson()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.DeploymentRecord },
            OnAgentDeploymentConnectionShow = _ => new AgentDeploymentConnectionResult
            {
                Status = "connections-previewed",
                AgentId = "agent-1",
                DeploymentId = "slot-1",
                DeploymentName = "dev",
                EnvironmentId = "env-1",
                Workflows = WorkflowStateProjection.FromActivation([new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense" }], new CopilotWorkflowActivation { ObservedById = new Dictionary<string, bool> { ["wf-1"] = true } }, ManagedAgentFixtures.StateRules),
            },
        };

        var (_, text, _) = Run(operations, "deployment", "connection", "list", "--deployment-name", "dev");
        var (_, json, _) = Run(operations, "deployment", "connection", "list", "--deployment-name", "dev", "--json");

        using var document = JsonDocument.Parse(json);
        var workflow = Assert.Single(document.RootElement.GetProperty("workflows").EnumerateArray());
        Assert.Contains(text.Split('\n'), line => line.Contains("SubmitExpense", StringComparison.Ordinal) && line.TrimEnd().EndsWith("published", StringComparison.Ordinal));
        Assert.Equal(["workflowId", "displayName", "filePath", "connectionReferences", "state"], workflow.EnumerateObject().Select(property => property.Name));
        Assert.Equal("activated", workflow.GetProperty("state").GetString());
    }

    [Fact]
    public void ConnectionConnectors_BindsEveryOption()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(
            operations,
            "deployment", "connection", "connectors",
            "--deployment-name", "dev",
            "--filter", "teams",
            "--connector", "shared_teams",
            "--limit", "7");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("teams", operations.LastAgentDeploymentConnectorListRequest?.Filter);
        Assert.Equal("shared_teams", operations.LastAgentDeploymentConnectorListRequest?.Connector);
        Assert.Equal(7, operations.LastAgentDeploymentConnectorListRequest?.Limit);
    }

    [Fact]
    public void ConnectionConnectors_TakesAnEnvironmentInsteadOfADeployment()
    {
        var operations = CodeAgent();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "connectors", "--environment-id", "env-9");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("env-9", operations.LastAgentDeploymentConnectorListRequest?.EnvironmentId);
    }

    [Fact]
    public void ConnectionAdd_DeclaresAReferenceByDefault()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "add", "--deployment-name", "dev", "--connector", "shared_teams", "--connection-id", "conn-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("shared_teams", operations.LastAgentDeploymentConnectionReferenceCreateRequest?.Connector);
        Assert.Equal("conn-1", operations.LastAgentDeploymentConnectionReferenceCreateRequest?.ConnectionId);
        Assert.Null(operations.LastAgentDeploymentConnectionCreateRequest);
    }

    [Fact]
    public void ConnectionAdd_ConnectionOnlyCreatesNoReference()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "add", "--deployment-name", "dev", "--connector", "shared_teams", "--connection-only");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("shared_teams", operations.LastAgentDeploymentConnectionCreateRequest?.Connector);
        Assert.Null(operations.LastAgentDeploymentConnectionReferenceCreateRequest);
    }

    [Fact]
    public void ConnectionAdd_ConnectionOnlyRefusesAConnectionToBind()
    {
        var operations = Mcs();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "add", "--deployment-name", "dev", "--connector", "shared_teams", "--connection-only", "--connection-id", "conn-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--connection-id does not apply with --connection-only", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionCreateRequest);
        Assert.Null(operations.LastAgentDeploymentConnectionReferenceCreateRequest);
    }

    [Fact]
    public void ConnectionUpdate_AcceptsTheShapeTheUnverifiedDiscoveryHintSuggests()
    {
        var operations = Mcs();
        var hint = ConnectionCommandHints.BindConnector("shared_eventhubs", "conn-1");

        var (exitCode, _, stderr) = Run(operations, ArgsOf(hint, ("<name>", "dev")));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("--connection-only", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--environment-id applies only", stderr, StringComparison.Ordinal);
        Assert.Equal("shared_eventhubs", operations.LastAgentDeploymentConnectionRequest?.Connector);
    }

    [Fact]
    public void ConnectionRemove_AcceptsTheShapeTheOrphanHintSuggests()
    {
        var operations = Mcs();
        var hint = ConnectionCommandHints.DeleteConnection("env-other", "shared_eventhubs", "conn-1");

        var (exitCode, _, stderr) = Run(operations, ArgsOf(hint));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("--connector is required", stderr, StringComparison.Ordinal);
        Assert.Equal("shared_eventhubs", operations.LastAgentDeploymentConnectionRemoveRequest?.Connector);
    }

    [Fact]
    public void ConnectionAdd_RefusesAnEnvironmentWithoutConnectionOnly()
    {
        var operations = Mcs();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "add", "--deployment-name", "dev", "--connector", "shared_teams", "--environment-id", "env-other");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--environment-id applies only with --connection-only", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionReferenceCreateRequest);
        Assert.Null(operations.LastAgentDeploymentConnectionCreateRequest);
    }

    [Fact]
    public void ConnectionAdd_ConnectionOnlyStillTakesAnEnvironment()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "add", "--connector", "shared_teams", "--connection-only", "--environment-id", "env-other");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("env-other", operations.LastAgentDeploymentConnectionCreateRequest?.EnvironmentId);
    }

    [Fact]
    public void ConnectionAdd_RequiresAConnector()
    {
        var operations = Mcs();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "add", "--deployment-name", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--connector", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionReferenceCreateRequest);
    }

    [Fact]
    public void ConnectionRemove_BindsBothObjects()
    {
        var operations = Mcs();

        var (exitCode, _, _) = Run(
            operations,
            "deployment", "connection", "remove",
            "--deployment-name", "dev",
            "--connection-reference", "cr_teams",
            "--connection-id", "conn-1",
            "--connector", "shared_teams",
            "--force");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("cr_teams", operations.LastAgentDeploymentConnectionRemoveRequest?.ConnectionReference);
        Assert.Equal("conn-1", operations.LastAgentDeploymentConnectionRemoveRequest?.ConnectionId);
        Assert.Equal("shared_teams", operations.LastAgentDeploymentConnectionRemoveRequest?.Connector);
        Assert.True(operations.LastAgentDeploymentConnectionRemoveRequest?.Force);
    }

    [Fact]
    public void ConnectionRemove_RequiresOneOfTheTwoObjects()
    {
        var operations = Mcs();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "remove", "--deployment-name", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--connection-reference or --connection-id", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionRemoveRequest);
    }

    [Fact]
    public void ConnectionRemove_AReferenceAloneReportsOnlyTheReference()
    {
        var operations = Mcs();

        var (exitCode, stdout, _) = Run(operations, "deployment", "connection", "remove", "--deployment-name", "dev", "--connection-reference", "cr_teams");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Removed connection reference cr_teams", stdout, StringComparison.Ordinal);
        Assert.Contains("only the workspace declaration was removed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionRemove_AConnectionAloneWarnsItIsEnvironmentWide()
    {
        var operations = Mcs();

        var (exitCode, stdout, _) = Run(operations, "deployment", "connection", "remove", "--deployment-name", "dev", "--connection-id", "conn-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Anything outside this agent that used it stops working", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Removed connection reference", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionRemove_CodeAgent_IsAUsageError()
    {
        var operations = CodeAgent();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "remove", "--deployment-id", "deployment-1", "--connection-id", "conn-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("applies to an MCS deployment", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionRemoveRequest);
    }

    [Fact]
    public void ConnectionRemove_RendersJson()
    {
        var operations = Mcs();

        var (exitCode, stdout, _) = Run(operations, "deployment", "connection", "remove", "--deployment-name", "dev", "--connection-id", "conn-1", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("connection-removed", stdout, StringComparison.Ordinal);
    }
}
