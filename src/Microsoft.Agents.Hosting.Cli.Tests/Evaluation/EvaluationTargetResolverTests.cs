using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationTargetResolverTests
{
    private const string Gateway = "https://powervamg.us-il301.gateway.prod.island.powerapps.com/";
    private const string SchemaName = "con_agent";

    private readonly FakeCopilotCatalogPort _catalog = new() { ResolveReturns = null };

    private static EvaluationTargetResolver Resolver(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, params string[] environmentIds)
    {
        var environments = (environmentIds.Length == 0 ? ["env-1"] : environmentIds)
            .Select(id => EnvironmentFixtures.Environment(id, instanceUrl: CopilotTestFixtures.DataverseOrganization) with { AgentManagementUrl = Gateway })
            .ToArray();

        return new EvaluationTargetResolver(
            ManagedAgentFixtures.Resolver(environments),
            catalog,
            ManagedAgentFixtures.AgentResolver(store),
            new AgentConfigLocator(),
            ManagedAgentFixtures.Identities());
    }

    private static JsonAgentConfigStore Seeded(TemporaryWorkspace workspace, ManagedAgentRecord? agent = null)
    {
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, agent ?? ManagedAgentFixtures.Agent());

        return store;
    }

    private static CopilotAgentSummary Agent(string agentId, string schemaName, string displayName) =>
        new() { AgentId = agentId, SchemaName = schemaName, DisplayName = displayName };

    private Task<EvaluationTarget> ResolveAsync(JsonAgentConfigStore store, EvaluationSelector selector) =>
        Resolver(store, _catalog).ResolveAsync(selector, "agent eval testset delete", CancellationToken.None);

    private Task<EvaluationTarget> ResolveAwayFromTheRecordAsync(JsonAgentConfigStore store, EvaluationSelector selector) =>
        Resolver(store, _catalog, "env-2", "env-1").ResolveAsync(selector, "agent eval testset list", CancellationToken.None);

    [Fact]
    public async Task ResolveAsync_ActsInTheEnvironmentTheRecordNamesNotTheActiveOne()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);
        _catalog.ResolveReturns = "bot-7";

        var target = await ResolveAwayFromTheRecordAsync(Seeded(workspace), new EvaluationTestSetListRequest { Project = workspace.Root });

        Assert.Equal("env-1", target.Environment.EnvironmentId);
        Assert.Equal("bot-7", target.McsAgentId);
    }

    [Fact]
    public async Task ResolveAsync_UsesTheRecordsEnvironmentWhenNoneIsActive()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);
        _catalog.ResolveReturns = "bot-7";

        var target = await new EvaluationTargetResolver(
            ManagedAgentFixtures.ResolverWithoutSelection(
                EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization) with { AgentManagementUrl = Gateway }),
            _catalog,
            ManagedAgentFixtures.AgentResolver(Seeded(workspace)),
            new AgentConfigLocator(),
            ManagedAgentFixtures.Identities())
            .ResolveAsync(new EvaluationTestSetListRequest { Project = workspace.Root }, "agent eval testset list", CancellationToken.None);

        Assert.Equal("env-1", target.Environment.EnvironmentId);
        Assert.Equal("bot-7", target.McsAgentId);
    }

    [Fact]
    public async Task ResolveAsync_StillNeedsAnActiveEnvironmentWhenNoProjectNamesTheAgent()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        _catalog.Agents.Add(Agent("bot-only", "con_only", "The Only Agent"));

        await Assert.ThrowsAsync<EnvironmentNotSelectedException>(() => new EvaluationTargetResolver(
            ManagedAgentFixtures.ResolverWithoutSelection(
                EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization) with { AgentManagementUrl = Gateway }),
            _catalog,
            ManagedAgentFixtures.AgentResolver(ManagedAgentFixtures.Store()),
            new AgentConfigLocator(),
            ManagedAgentFixtures.Identities())
            .ResolveAsync(new EvaluationTestSetListRequest { Project = workspace.Root }, "agent eval testset list", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_LetsAnExplicitEnvironmentIdWinOverTheRecord()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);
        _catalog.ResolveReturns = "bot-7";

        var target = await ResolveAwayFromTheRecordAsync(Seeded(workspace), new EvaluationTestSetListRequest { Project = workspace.Root, EnvironmentId = "env-2" });

        Assert.Equal("env-2", target.Environment.EnvironmentId);
    }

    [Fact]
    public async Task ResolveAsync_NamesTheEnvironmentItSearchedWhenTheRecordsAgentIsNotThere()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => ResolveAwayFromTheRecordAsync(Seeded(workspace), new EvaluationTestSetListRequest { Project = workspace.Root }));

        Assert.Equal(CopilotErrorKinds.AgentNotFound, failure.ErrorKind);
        Assert.Contains(SchemaName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("--environment-id env-1", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RefusesAnAgentTheProjectCannotIdentifyRemotely()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = Seeded(workspace, ManagedAgentFixtures.Agent() with { McsSchemaName = null, McsAgentId = null });
        _catalog.Agents.Add(Agent("bot-other", "con_unrelated", "Someone Else's Agent"));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => ResolveAsync(store, new EvaluationTestSetDeleteRequest { Project = workspace.Root, TestSet = "Nightly" }));

        Assert.Equal(CopilotErrorKinds.AgentNotFound, failure.ErrorKind);
        Assert.Contains("Contoso Agent", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_catalog.ResolvedValues);
        Assert.Empty(_catalog.RequestedMaximumCounts);
    }

    [Fact]
    public async Task ResolveAsync_RefusesAnAgentIdTheEnvironmentDoesNotHold()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        _catalog.Agents.Add(Agent("bot-named", "con_named", "The Named Agent"));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => ResolveAsync(ManagedAgentFixtures.Store(), new EvaluationTestSetDeleteRequest { Project = workspace.Root, AgentId = "bot-gone", AgentName = "The Named Agent", TestSet = "Nightly" }));

        Assert.Equal(CopilotErrorKinds.AgentNotFound, failure.ErrorKind);
        Assert.Contains("bot-gone", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_catalog.RequestedMaximumCounts);
    }

    [Fact]
    public async Task ResolveAsync_ResolvesByNameOnlyWhenNoAgentIdWasGiven()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        _catalog.Agents.Add(Agent("bot-named", "con_named", "The Named Agent"));
        _catalog.Agents.Add(Agent("bot-other", "con_other", "Another Agent"));

        var target = await ResolveAsync(ManagedAgentFixtures.Store(), new EvaluationTestSetListRequest { Project = workspace.Root, AgentName = "The Named Agent" });

        Assert.Equal("bot-named", target.McsAgentId);
    }

    [Fact]
    public async Task ResolveAsync_RefusesTheEnvironmentsOtherAgentWhenTheProjectsAgentIsNotThere()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);
        _catalog.Agents.Add(Agent("bot-other", "con_unrelated", "Someone Else's Agent"));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => ResolveAsync(Seeded(workspace), new EvaluationTestSetDeleteRequest { Project = workspace.Root, TestSet = "Nightly" }));

        Assert.Equal(CopilotErrorKinds.AgentNotFound, failure.ErrorKind);
        Assert.Contains(SchemaName, failure.Message, StringComparison.Ordinal);
        Assert.Equal([SchemaName], _catalog.ResolvedValues);
        Assert.Empty(_catalog.RequestedMaximumCounts);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsTheAgentTheProjectDeclares()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);
        _catalog.ResolveReturns = "bot-7";

        var target = await ResolveAsync(Seeded(workspace), new EvaluationTestSetListRequest { Project = workspace.Root });

        Assert.Equal("bot-7", target.McsAgentId);
        Assert.Equal("Contoso Agent", target.DisplayName);
        Assert.Empty(_catalog.RequestedMaximumCounts);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToTheEnvironmentOnlyWhenNoProjectNamesAnAgent()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        _catalog.Agents.Add(Agent("bot-only", "con_only", "The Only Agent"));

        var target = await ResolveAsync(ManagedAgentFixtures.Store(), new EvaluationTestSetListRequest { Project = workspace.Root });

        Assert.Equal("bot-only", target.McsAgentId);
        Assert.Equal("The Only Agent", target.DisplayName);
    }

    [Fact]
    public async Task ResolveAsync_RefusesAProjectDirectoryThatDoesNotExist()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        _catalog.Agents.Add(Agent("bot-only", "con_only", "The Only Agent"));

        var missing = Path.Combine(workspace.Root, "no-such-project");

        var failure = await Assert.ThrowsAsync<UsageException>(
            () => ResolveAsync(ManagedAgentFixtures.Store(), new EvaluationTestSetListRequest { Project = missing }));

        Assert.Contains("does not exist", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_catalog.RequestedMaximumCounts);
    }

    [Fact]
    public async Task ResolveAsync_PrefersAnExplicitAgentIdOverTheProject()
    {
        using var workspace = new TemporaryWorkspace(schemaName: SchemaName);
        _catalog.ResolveReturns = "bot-9";

        var target = await ResolveAsync(Seeded(workspace), new EvaluationTestSetListRequest { Project = workspace.Root, AgentId = "bot-9" });

        Assert.Equal("bot-9", target.McsAgentId);
        Assert.Equal(["bot-9"], _catalog.ResolvedValues);
    }
}
