using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ConnectorCommandServiceTests
{
    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Deployed()
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();

        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(lastDeployedTime: ManagedAgentFixtures.Now));

        return (workspace, store);
    }

    private static AgentDeploymentConnectionResolver Deployments(JsonAgentConfigStore store) =>
        new(
            ManagedAgentFixtures.AgentResolver(store),
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities(),
            new FakeCopilotCatalogPort { ResolveReturns = "bot-1" },
            ManagedAgentFixtures.WorkflowWorkspace());

    private static ConnectionTargetResolver Targets(JsonAgentConfigStore store) =>
        new(Deployments(store), ManagedAgentFixtures.Resolver());

    private static ConnectionTargetResolver TargetsWithNoCloudAgent(JsonAgentConfigStore store) =>
        new(
            new AgentDeploymentConnectionResolver(
                ManagedAgentFixtures.AgentResolver(store),
                ManagedAgentFixtures.Resolver(),
                ManagedAgentFixtures.Identities(),
                new FakeCopilotCatalogPort(),
                ManagedAgentFixtures.WorkflowWorkspace()),
            ManagedAgentFixtures.Resolver());

    [Fact]
    public async Task List_ListsConnectorsBeforeTheAgentIsDeployed()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await new AgentDeploymentConnectorListService(TargetsWithNoCloudAgent(store), Catalog("shared_teams"), new FakeConnectionPort()).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("shared_teams", Assert.Single(result.Connectors).ConnectorId);
    }

    [Fact]
    public async Task Remove_RemovesAReferenceAfterTheCloudAgentIsGone()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        var result = await new AgentDeploymentConnectionRemoveService(
            TargetsWithNoCloudAgent(store),
            Resolver(Catalog("shared_teams")),
            connections,
            new FakeConnectionLifecyclePort(),
            new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Confirmed }).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams" },
            CancellationToken.None);

        Assert.Equal("cr_teams", result.ConnectionReference);
        Assert.Equal("cr_teams", Assert.Single(connections.RemovedReferenceNames));
    }

    private static ConnectorResolver Resolver(FakeConnectorCatalogPort connectors) => new(connectors);

    [Fact]
    public async Task Resolver_ReturnsTheCatalogRowWithoutProbingConnections()
    {
        var connectors = Catalog("shared_teams");

        var connector = await Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_teams", "list", CancellationToken.None);

        Assert.Equal("shared_teams", connector.Connector.InternalId);
        Assert.Null(connector.Connections);
        Assert.Empty(connectors.ConnectionLookups);
    }

    [Fact]
    public async Task Resolver_AcceptsAConnectorTheCatalogOmitsButConnectionsProve()
    {
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-live")];

        var connector = await Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_advancedapprovals", "list", CancellationToken.None);

        Assert.Equal("shared_advancedapprovals", connector.Connector.InternalId);
        Assert.Equal("shared_advancedapprovals", Assert.Single(connectors.ConnectionLookups));
    }

    [Fact]
    public async Task Resolver_AcceptsAnUnlistedConnectorThatHasNoConnectionsYet()
    {
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [];

        var connector = await Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_advancedapprovals", "list", CancellationToken.None);

        Assert.Equal("shared_advancedapprovals", connector.Connector.InternalId);
        Assert.Empty(connector.Connections!);
    }

    [Fact]
    public async Task Resolver_ReusesTheConnectionsTheProbeAlreadyRead()
    {
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-live")];
        var resolver = Resolver(connectors);

        var resolution = await resolver.RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_advancedapprovals", "list", CancellationToken.None);
        var connections = await resolver.ConnectionsAsync(ManagedAgentFixtures.EnvironmentContext(), resolution, CancellationToken.None);

        Assert.Equal(["conn-live"], connections.Select(connection => connection.ConnectionId));
        Assert.Equal("shared_advancedapprovals", Assert.Single(connectors.ConnectionLookups));
    }

    [Fact]
    public async Task Resolver_ReadsTheConnectionsOfAListedConnectorOnlyOnDemand()
    {
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-a")];
        var resolver = Resolver(connectors);

        var resolution = await resolver.RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_teams", "list", CancellationToken.None);
        var connections = await resolver.ConnectionsAsync(ManagedAgentFixtures.EnvironmentContext(), resolution, CancellationToken.None);

        Assert.Equal(["conn-a"], connections.Select(connection => connection.ConnectionId));
        Assert.Equal("shared_teams", Assert.Single(connectors.ConnectionLookups));
    }

    [Fact]
    public async Task Resolver_RejectsAConnectorNeitherTheCatalogNorAConnectionKnows()
    {
        var connectors = Catalog("shared_teams");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_nothing", "list", CancellationToken.None));

        Assert.Contains("serves no connector 'shared_nothing'", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Connections request failed (403).")]
    [InlineData("Connections request failed (429).")]
    [InlineData("Connections request failed (500).")]
    public async Task Resolver_AConnectionsLookupThatFailsIsNotReportedAsAnUnknownConnector(string message)
    {
        var connectors = new FakeConnectorCatalogPort { ConnectionsThrow = new InvalidOperationException(message) };
        connectors.InternalIds.Add("shared_teams");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_advancedapprovals", "list", CancellationToken.None));

        Assert.Equal(message, failure.Message);
    }

    [Fact]
    public async Task Resolver_AConnectionsLookupThatTimesOutIsNotReportedAsAnUnknownConnector()
    {
        var connectors = new FakeConnectorCatalogPort { ConnectionsThrow = new TimeoutException("Connections request timed out after 30s.") };
        connectors.InternalIds.Add("shared_teams");

        await Assert.ThrowsAsync<TimeoutException>(() => Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_advancedapprovals", "list", CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_AConnectionsLookupThatCannotAuthenticateIsNotReportedAsAnUnknownConnector()
    {
        var connectors = new FakeConnectorCatalogPort { ConnectionsThrow = new HttpRequestException("No such host is known.") };
        connectors.InternalIds.Add("shared_teams");

        await Assert.ThrowsAsync<HttpRequestException>(() => Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_advancedapprovals", "list", CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_ACancelledConnectionsLookupIsNotSwallowed()
    {
        var connectors = new FakeConnectorCatalogPort { ConnectionsThrow = new OperationCanceledException() };
        connectors.InternalIds.Add("shared_teams");

        await Assert.ThrowsAsync<OperationCanceledException>(() => Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_typo", "list", CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_AcceptsAnUnlistedConnectorNamedAsAFullProviderPath()
    {
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-live")];

        var connector = await Resolver(connectors).RequireAsync(ManagedAgentFixtures.EnvironmentContext(), "/providers/Microsoft.PowerApps/apis/shared_advancedapprovals", "list", CancellationToken.None);

        Assert.Equal("shared_advancedapprovals", connector.Connector.InternalId);
    }

    [Fact]
    public async Task Reference_DeclaresAReferenceForAConnectorTheCatalogOmits()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("new_sharedadvancedapprovals_abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("new_sharedadvancedapprovals_abc", "shared_advancedapprovals", null, ManagedAgentFixtures.Candidate("conn-live")));
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-live")];

        var result = await ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_advancedapprovals" },
            CancellationToken.None);

        Assert.Equal("new_sharedadvancedapprovals_abc", result.ConnectionReference);
        Assert.Equal("shared_advancedapprovals", Assert.Single(connections.CreatedReferenceConnectors));
    }

    [Fact]
    public async Task Reference_AConnectorNothingKnowsDeclaresNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        var connectors = Catalog("shared_teams");

        await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_typo" },
            CancellationToken.None));

        Assert.Empty(connections.CreatedReferenceConnectors);
    }

    [Fact]
    public async Task Reference_BindsAnExplicitConnectionGivenWithSurroundingWhitespace()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-a")));
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-a")];

        var result = await ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "  conn-a  " },
            CancellationToken.None);

        Assert.Equal("conn-a", result.ConnectionId);
        Assert.Equal("conn-a", Assert.Single(connections.Bound).ConnectionId);
        Assert.Equal("shared_teams", Assert.Single(connections.CreatedReferenceConnectors));
    }

    [Fact]
    public async Task Reference_ValidatesAnExplicitConnectionWithoutReadingTheConnectionsTwice()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_advancedapprovals.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_advancedapprovals.abc", "shared_advancedapprovals", null, ManagedAgentFixtures.Candidate("conn-a")));
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-a")];

        await ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_advancedapprovals", ConnectionId = "conn-a" },
            CancellationToken.None);

        Assert.Equal("shared_advancedapprovals", Assert.Single(connectors.ConnectionLookups));
    }

    [Fact]
    public async Task Reference_AnExplicitConnectionTheConnectorRejectsDeclaresNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-a")];

        await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = " conn-z " },
            CancellationToken.None));

        Assert.Empty(connections.CreatedReferenceConnectors);
    }

    [Fact]
    public async Task Reference_ADeclarationThatFailsWarnsTheEnvironmentMayHoldOne()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort { CreateReferenceThrows = new IOException("the workspace file is locked") };
        var connectors = Catalog("shared_teams");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Equal("connection-reference-not-declared", failure.ErrorKind);
        Assert.Contains("the workspace file is locked", failure.Message, StringComparison.Ordinal);
        Assert.Contains("unused reference", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_ADeclarationCancelledByTheCallerStaysCancelled()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort { CreateReferenceThrows = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Remove_DeletesAConnectionOnAConnectorTheCatalogOmits()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("new_sharedadvancedapprovals_abc", "shared_advancedapprovals", "conn-live"));
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-live")];
        var lifecycle = new FakeConnectionLifecyclePort();

        await RemoveService(store, connectors, connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-live", Force = true },
            CancellationToken.None);

        Assert.Equal(("shared_advancedapprovals", "conn-live"), Assert.Single(lifecycle.Deleted));
    }

    [Fact]
    public async Task Create_CreatesAConnectionForAConnectorTheCatalogOmits()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [ManagedAgentFixtures.Candidate("conn-live")];

        var result = await CreateService(store, connectors, consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_advancedapprovals" },
            CancellationToken.None);

        Assert.Equal("conn-new", result.ConnectionId);
        Assert.Equal("shared_advancedapprovals", result.ConnectorId);
    }

    private static AgentDeploymentConnectorListService ListService(JsonAgentConfigStore store, FakeConnectorCatalogPort connectors, FakeConnectionPort connections) =>
        new(Targets(store), connectors, connections);

    private static AgentDeploymentConnectionCreateService CreateService(JsonAgentConfigStore store, FakeConnectorCatalogPort connectors, IConnectionConsentFlow consentFlow) =>
        new(
            Targets(store),
            Resolver(connectors),
            new ConnectionCreationFlow(ManagedAgentFixtures.ConnectorReadiness(connectors), consentFlow),
            ManagedAgentFixtures.Principals());

    private static AgentDeploymentConnectionReferenceCreateService ReferenceService(JsonAgentConfigStore store, FakeConnectorCatalogPort connectors, FakeConnectionPort connections, IConnectionConsentFlow? consentFlow = null, IConnectionSelectionPrompt? prompt = null) =>
        new(
            Deployments(store),
            Resolver(connectors),
            connections,
            new ConnectionBindingCoordinator(
                prompt ?? new ScriptedSelectionPrompt(),
                new ConnectionCreationFlow(
                    ManagedAgentFixtures.ConnectorReadiness(connectors),
                    consentFlow ?? new ScriptedConsentFlow { Throws = true })),
            ManagedAgentFixtures.Principals());

    private static FakeConnectorCatalogPort Catalog(params string[] internalIds)
    {
        var catalog = new FakeConnectorCatalogPort();

        catalog.Connectors.AddRange(internalIds.Select(id => new CopilotConnectorInfo { InternalId = id, DisplayName = id, Publisher = "Microsoft", Tier = "Standard" }));

        return catalog;
    }

    private const string UnlistedCustomConnector = "shared_custom-5f0123456789abcdef";

    private static FakeConnectorCatalogPort CatalogServing(string internalId, params string[] connectionIds)
    {
        var catalog = Catalog(internalId);

        catalog.Connections[internalId] = [.. connectionIds.Select(connectionId => ManagedAgentFixtures.Candidate(connectionId))];

        return catalog;
    }

    [Fact]
    public async Task List_ReportsTheEnvironmentsConnectors()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await ListService(store, Catalog("shared_teams", "shared_sql"), new FakeConnectionPort()).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("connectors-listed", result.Status);
        Assert.Equal(["shared_sql", "shared_teams"], result.Connectors.Select(connector => connector.ConnectorId));
    }

    [Fact]
    public async Task List_MarksAConnectorTheWorkspaceAlreadyDeclares()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams"));

        var result = await ListService(store, Catalog("shared_teams", "shared_sql"), connections).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.True(result.Connectors.Single(connector => connector.ConnectorId == "shared_teams").InUse);
        Assert.False(result.Connectors.Single(connector => connector.ConnectorId == "shared_sql").InUse);
    }

    [Fact]
    public async Task List_ReadsTheWorkspaceOnceForTheWholeListing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        await ListService(store, Catalog("shared_teams", "shared_sql", "shared_office365"), connections).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(1, connections.DeclaredConnectorCalls);
    }

    [Fact]
    public async Task List_DiscoversNoConnectionsToAnnotateDeclarations()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        var connectors = Catalog("shared_teams", "shared_sql");

        await ListService(store, connectors, connections).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev", Filter = "teams", Limit = 1 },
            CancellationToken.None);

        Assert.Equal(0, connections.ReadCalls);
        Assert.Empty(connectors.ConnectionLookups);
    }

    [Fact]
    public async Task List_DoesNotMarkAConnectorOnlyAnUndeclaredReferenceTargets()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("authored-only", "shared_sql", null) with { IsDeclared = false });

        var result = await ListService(store, Catalog("shared_sql"), connections).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.False(Assert.Single(result.Connectors).InUse);
    }

    [Fact]
    public async Task List_ReadsTheCatalogOnce()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connectors = Catalog("shared_teams", "shared_sql");

        await ListService(store, connectors, new FakeConnectionPort()).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(1, connectors.Calls);
    }

    [Fact]
    public async Task List_AnUnreadableWorkspaceLeavesInUseUnknownAndSaysSo()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await ListService(store, Catalog("shared_teams"), new FakeConnectionPort { DeclaredConnectorsThrows = new InvalidOperationException("no workspace") }).ListAsync(
            new AgentDeploymentConnectorListRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Null(Assert.Single(result.Connectors).InUse);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("no workspace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_AnEnvironmentNamedDirectlyReadsNoWorkspace()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        var result = await ListService(store, Catalog("shared_teams"), connections).ListAsync(
            new AgentDeploymentConnectorListRequest { EnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal(0, connections.ReadCalls);
        Assert.Null(Assert.Single(result.Connectors).InUse);
    }

    [Fact]
    public async Task List_RejectsAnEnvironmentAndADeploymentTogether()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => ListService(store, Catalog("shared_teams"), new FakeConnectionPort()).ListAsync(
            new AgentDeploymentConnectorListRequest { EnvironmentId = "env-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Contains("takes --environment-id or a deployment, not both", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ReportsTheConnectionTheConsentFlowProduced()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", "Teams", null));

        var result = await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("connection-created", result.Status);
        Assert.Equal("conn-new", result.ConnectionId);
        Assert.Equal("shared_teams", result.ConnectorId);
        Assert.Equal("shared_teams", Assert.Single(consentFlow.RequestedConnectors));
    }

    [Fact]
    public async Task Create_AnUnknownConnectorNeverReachesTheConsentFlow()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();

        await Assert.ThrowsAsync<ManagedAgentException>(() => CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_nothing" },
            CancellationToken.None));

        Assert.Empty(consentFlow.RequestedConnectors);
    }

    [Fact]
    public async Task Create_RequiresAConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => CreateService(store, Catalog("shared_teams"), new ScriptedConsentFlow { Throws = true }).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Contains("requires --connector", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ACancelledConsentFailsAndNamesTheBindCommand()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, null));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Equal("connection-not-created", failure.ErrorKind);
        Assert.NotEqual(ExitCodes.Success, failure.ExitCode);
        Assert.Contains("--connection-id", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("make.powerapps.com", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_NonInteractiveCreatesWithoutOpeningABrowser()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", "op@contoso.com", null));

        var result = await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.False(Assert.Single(consentFlow.OpenedBrowser));
        Assert.Equal("conn-new", result.ConnectionId);
    }

    [Fact]
    public async Task Create_InteractiveStillOpensABrowser()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", "op@contoso.com", null));

        await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.True(Assert.Single(consentFlow.OpenedBrowser));
    }

    [Fact]
    public async Task Create_NonInteractiveThatIsNeverCompletedStillFails()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, null));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None));

        Assert.Equal("connection-not-created", failure.ErrorKind);
        Assert.DoesNotContain("--non-interactive", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WarnsAboutTheConnectionsTheOperatorAlreadyOwns()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-mine")];
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await CreateService(store, connectors, consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("conn-new", result.ConnectionId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("conn-mine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_NonInteractiveCreatesEvenWhenTheOperatorAlreadyOwnsOne()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-mine")];
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await CreateService(store, connectors, consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("conn-new", result.ConnectionId);
        Assert.False(Assert.Single(consentFlow.OpenedBrowser));
    }

    [Fact]
    public async Task Create_DoesNotWarnAboutSomebodyElsesConnection()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-theirs", owner: "somebody@contoso.com")];
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await CreateService(store, connectors, consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Reference_DeclaresAndAutoBindsAConnectionTheOperatorOwns()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-mine")));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("connection-reference-bound", result.Status);
        Assert.Equal("cre98_agent.shared_teams.abc", result.ConnectionReference);
        Assert.Equal("conn-mine", result.ConnectionId);
        Assert.True(result.Ready);
        Assert.Equal("shared_teams", Assert.Single(connections.CreatedReferenceConnectors));
        Assert.Equal("conn-mine", Assert.Single(connections.Bound).ConnectionId);
    }

    [Fact]
    public async Task Reference_BindsTheConnectionTheRequestNames()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b")));
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b")];

        var result = await ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-b" },
            CancellationToken.None);

        Assert.Equal("conn-b", result.ConnectionId);
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task Reference_RejectsAConnectionThatIsNotACandidate()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-a")];

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-missing" },
            CancellationToken.None));

        Assert.Contains("conn-a", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connections.CreatedReferenceConnectors);
    }

    [Fact]
    public async Task Reference_RejectsAConnectionThatIsNotConnected()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_teams"] = [ManagedAgentFixtures.Candidate("conn-broken", status: "Error")];

        var failure = await Assert.ThrowsAsync<UsageException>(() => ReferenceService(store, connectors, connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-broken" },
            CancellationToken.None));

        Assert.Contains("Choose a Connected connection", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connections.CreatedReferenceConnectors);
    }

    [Fact]
    public async Task Reference_ADeclarationThatCannotBeBoundNamesItselfAndItsBindCommand()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort { ReadThrows = new InvalidOperationException("the service is unavailable") };
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Equal("connection-reference-not-bound", failure.ErrorKind);
        Assert.Contains("cre98_agent.shared_teams.abc", failure.Message, StringComparison.Ordinal);
        Assert.Contains("do not declare another", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("--connection-reference cre98_agent.shared_teams.abc", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_InteractivelyOffersTheConnectionsNobodyOwns()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-theirs", owner: "somebody@contoso.com")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Existing, "conn-theirs"));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, prompt: prompt).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("shared_teams", Assert.Single(prompt.Asked));
        Assert.Equal("conn-theirs", result.ConnectionId);
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task Reference_InteractivelyFallsThroughToCreatingAConnection()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, consentFlow, prompt).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("conn-new", result.ConnectionId);
        Assert.True(result.Ready);
        Assert.Equal("shared_teams", Assert.Single(consentFlow.RequestedConnectors));
    }

    [Fact]
    public async Task Reference_NonInteractiveWithNobodysConnectionStaysUnboundAndNamesTheIds()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-theirs", owner: "somebody@contoso.com")));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, new ScriptedConsentFlow()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("connection-reference-declared", result.Status);
        Assert.False(result.Ready);
        Assert.Null(result.ConnectionId);
        Assert.Equal(0, connections.BindCalls);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("conn-theirs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reference_NonInteractiveWithNoConnectionCreatesOneWithoutOpeningABrowser()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", "op@contoso.com", null));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, consentFlow).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("cre98_agent.shared_teams.abc", result.ConnectionReference);
        Assert.Equal("shared_teams", Assert.Single(consentFlow.RequestedConnectors));
        Assert.False(Assert.Single(consentFlow.OpenedBrowser));
        Assert.Equal("conn-new", result.ConnectionId);
        Assert.True(result.Ready);
        Assert.Single(connections.CreatedReferenceConnectors);
    }

    [Fact]
    public async Task Reference_NonInteractiveWithCandidatesStillDeclinesToChoose()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("candidate-a", owner: "someone@contoso.com"), ManagedAgentFixtures.Candidate("candidate-b", owner: "someone@contoso.com")));
        var consentFlow = new ScriptedConsentFlow { Throws = true };

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, consentFlow).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Empty(consentFlow.RequestedConnectors);
    }

    [Fact]
    public async Task Reference_NeverActivatesAWorkflow()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-mine")));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "workflow-1", DisplayName = "Flow", CanEnable = true });

        await ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal(0, connections.ActivateCalls);
        Assert.Empty(connections.StateChanges);
    }

    [Fact]
    public async Task Reference_AnUnknownConnectorDeclaresNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_nothing" },
            CancellationToken.None));

        Assert.Empty(connections.CreatedReferenceConnectors);
    }

    [Fact]
    public async Task Reference_ACatalogThatCouldNotBeReadLeavesTheReferenceUnboundAndExplains()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(new CopilotConnectionView
        {
            LogicalName = "cre98_agent.shared_teams.abc",
            ConnectorId = "shared_teams",
            CatalogUnavailable = true,
        });

        var result = await ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Reference_NonInteractiveWithAnUnreadableCatalog_CreatesNothingAndSaysItIsRetryable()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams") with { CatalogUnavailable = true });
        var consentFlow = new ScriptedConsentFlow { Throws = true };

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, consentFlow).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Empty(consentFlow.RequestedConnectors);
        Assert.Equal(0, connections.BindCalls);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("could not be listed", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Retry", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("--connection-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reference_NonInteractiveWithAReadableCatalogAndNoConnections_StillCreatesOne()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, consentFlow).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("shared_teams", Assert.Single(consentFlow.RequestedConnectors));
        Assert.Equal("conn-new", result.ConnectionId);
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task Create_ReadsTheCatalogOnceForASharedConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connectors = Catalog("shared_teams");
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        await CreateService(store, connectors, consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal(1, connectors.Calls);
    }

    [Fact]
    public async Task Create_TheSuggestedBindCommandCarriesTheDeploymentSelector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("msagent deployment connection update --agent-id agent-1 --deployment-name dev --connector shared_teams --connection-id conn-new", result.BindCommand);
    }

    [Fact]
    public async Task Create_ForAnAgentSelectedByName_SuggestsABindCommandThatSelectsItByIdentifier()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentName = "Contoso Agent", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("msagent deployment connection update --agent-id agent-1 --deployment-name dev --connector shared_teams --connection-id conn-new", result.BindCommand);
    }

    [Fact]
    public async Task Create_WithoutADeploymentTheSuggestedBindCommandShowsAPlaceholder()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        var result = await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { EnvironmentId = "env-1", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Contains(ConnectionCommandHints.DeploymentPlaceholder, result.BindCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_TheSuggestedBindCommandCarriesTheDeploymentSelector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, new ScriptedConsentFlow()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("msagent deployment connection update --agent-id agent-1 --deployment-name dev --connection-reference cre98_agent.shared_teams.abc", result.BindCommand);
    }

    [Fact]
    public async Task Reference_ForAnAgentSelectedByName_SuggestsABindCommandThatSelectsItByIdentifier()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, new ScriptedConsentFlow()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentName = "Contoso Agent", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("msagent deployment connection update --agent-id agent-1 --deployment-name dev --connection-reference cre98_agent.shared_teams.abc", result.BindCommand);
    }

    [Fact]
    public async Task Reference_AnUnknownConnectorSuggestsAListingThatCarriesTheSelector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, Catalog("shared_teams"), new FakeConnectionPort()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "zzz" },
            CancellationToken.None));

        Assert.Contains("msagent deployment connection connectors --agent-id agent-1 --deployment-name dev --filter <text>", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_TheSuggestedListingOmitsTheSchemaOverrideConnectorsCannotTake()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => ReferenceService(store, Catalog("shared_teams"), new FakeConnectionPort()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", SchemaName = "cr123_override", Connector = "zzz" },
            CancellationToken.None));

        Assert.DoesNotContain("--schema-name", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_TheSuggestedBindCommandCarriesTheSchemaOverride()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, new ScriptedConsentFlow()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", SchemaName = "cr123_override", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Contains("--schema-name cr123_override", result.BindCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reference_AnUnbindableDeclarationIsNotToldToDeclareAnother()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, new ScriptedConsentFlow()).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", NonInteractive = true },
            CancellationToken.None);

        Assert.Contains("--connection-reference cre98_agent.shared_teams.abc", result.BindCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("connection add", result.BindCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("connection add", StringComparison.Ordinal));
    }

    private static AgentDeploymentConnectionRemoveService RemoveService(JsonAgentConfigStore store, FakeConnectorCatalogPort connectors, FakeConnectionPort connections, FakeConnectionLifecyclePort? lifecycle = null, IConfirmationPrompt? confirmation = null) =>
        new(Targets(store), Resolver(connectors), connections, lifecycle ?? new FakeConnectionLifecyclePort(), confirmation ?? new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Confirmed });

    [Fact]
    public async Task Remove_ReportsThatTheConnectionReferenceRecordWasDeleted()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(true, []) { RecordDeleted = true });

        var result = await RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one" },
            CancellationToken.None);

        Assert.True(result.ConnectionReferenceRecordDeleted);
        Assert.Contains(result.ToTextLines(), line => line.Contains("record was deleted from the environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_WhenTheEnvironmentHeldNoRecord_SaysOnlyTheDeclarationWent()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(true, []));

        var result = await RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one" },
            CancellationToken.None);

        Assert.False(result.ConnectionReferenceRecordDeleted);
        Assert.Contains(result.ToTextLines(), line => line.Contains("held no connectionreference record", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains("left as it is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_AForcedRemovalStillReportsTheDeletedRecord()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, [new CopilotConnectionUsage { FilePath = "topics/Greeting.mcs.yml" }]));
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(true, []) { RecordDeleted = true });

        var result = await RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", Force = true },
            CancellationToken.None);

        Assert.True(result.ConnectionReferenceRecordDeleted);
        Assert.Contains("topics/Greeting.mcs.yml", result.Usages);
    }

    [Fact]
    public async Task Remove_AnotherHolderStopsTheRunBeforeTheReferenceIsRemoved()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-shared"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_two", "shared_teams", "conn-shared"));
        var lifecycle = new FakeConnectionLifecyclePort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-shared" },
            CancellationToken.None));

        Assert.Contains("cr_two", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connections.RemovedReferenceNames);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task Remove_AConnectorThatDoesNotMatchTheReferenceStopsBeforeEitherMutation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort();
        var catalog = CatalogServing("shared_sql", "conn-a");

        catalog.Connectors.Add(new CopilotConnectorInfo { InternalId = "shared_teams", DisplayName = "shared_teams" });

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, catalog, connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Connector = "shared_sql" },
            CancellationToken.None));

        Assert.Contains("belongs to connector shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connections.RemovedReferenceNames);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task Remove_AConnectorMismatchIsCaughtEvenUnderForce()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort();
        var catalog = CatalogServing("shared_sql", "conn-a");

        catalog.Connectors.Add(new CopilotConnectorInfo { InternalId = "shared_teams", DisplayName = "shared_teams" });

        await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, catalog, connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Connector = "shared_sql", Force = true },
            CancellationToken.None));

        Assert.Empty(connections.RemovedReferenceNames);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task Remove_AConnectorThatServesNoSuchConnectionStopsBeforeEitherMutation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams"));
        var lifecycle = new FakeConnectionLifecyclePort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-other"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Equal(11051, failure.ErrorCode);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("connection-not-served", failure.ErrorKind);
        Assert.Contains("serves no connection conn-a", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection list", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("--deployment-name dev", failure.Remediation, StringComparison.Ordinal);
        Assert.Empty(connections.RemovedReferenceNames);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task Remove_AMatchingConnectorRemovesTheReferenceAndDeletesTheConnection()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort();

        await RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("cr_one", Assert.Single(connections.RemovedReferenceNames));
        Assert.Equal(("shared_teams", "conn-a"), Assert.Single(lifecycle.Deleted));
    }

    [Fact]
    public async Task Remove_ADeletionWithoutAReferenceNeverLooksUpTheConnectorsConnections()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        var lifecycle = new FakeConnectionLifecyclePort();
        var catalog = Catalog("shared_teams");

        await RemoveService(store, catalog, connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-a", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal(("shared_teams", "conn-a"), Assert.Single(lifecycle.Deleted));
        Assert.Empty(catalog.ConnectionLookups);
    }

    [Fact]
    public async Task Reference_AProvenServableConnectorIsNotWaitedForAgain()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.custom.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.custom.abc", UnlistedCustomConnector));
        var catalog = new FakeConnectorCatalogPort();
        catalog.Connections[UnlistedCustomConnector] = [];
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", "op@contoso.com", null));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));

        var result = await ReferenceService(store, catalog, connections, consentFlow, prompt).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = UnlistedCustomConnector },
            CancellationToken.None);

        Assert.Equal("conn-new", result.ConnectionId);
        Assert.Equal(1, catalog.Calls);
    }

    [Fact]
    public async Task Reference_AListedConnectorIsStillWaitedForBeforeConsent()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.custom.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.custom.abc", UnlistedCustomConnector));
        var catalog = Catalog(UnlistedCustomConnector);
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", "op@contoso.com", null));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));

        await ReferenceService(store, catalog, connections, consentFlow, prompt).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = UnlistedCustomConnector },
            CancellationToken.None);

        Assert.True(catalog.Calls > 1);
    }

    [Fact]
    public async Task Remove_AConnectorWhoseConnectionsCannotBeReadStopsBeforeEitherMutation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams"));
        var lifecycle = new FakeConnectionLifecyclePort();
        var catalog = new FakeConnectorCatalogPort { ConnectionsThrow = new ConnectorNotFoundException("shared_teams", "env-1") };

        catalog.Connectors.Add(new CopilotConnectorInfo { InternalId = "shared_teams", DisplayName = "shared_teams" });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, catalog, connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Equal(11051, failure.ErrorCode);
        Assert.Contains("serves no connection conn-a", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connections.RemovedReferenceNames);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task Remove_AnUnknownConnectorStopsTheRunBeforeTheReferenceIsRemoved()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));

        await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, Catalog("shared_sql"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Empty(connections.RemovedReferenceNames);
    }

    [Fact]
    public async Task Remove_AFailedDeletionSaysTheReferenceIsAlreadyGone()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort { Throws = new InvalidOperationException("the service refused") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a" },
            CancellationToken.None));

        Assert.Equal("connection-not-deleted", failure.ErrorKind);
        Assert.Contains("cr_one", failure.Message, StringComparison.Ordinal);
        Assert.Contains("already gone", failure.Remediation, StringComparison.Ordinal);
        Assert.Equal("cr_one", Assert.Single(connections.RemovedReferenceNames));
    }

    [Fact]
    public async Task Remove_ADeletionOnItsOwnIsNotReportedAsAReferenceRemoval()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var lifecycle = new FakeConnectionLifecyclePort { Throws = new InvalidOperationException("the service refused") };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort(), lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-a", Connector = "shared_teams" },
            CancellationToken.None));

        Assert.Equal("the service refused", failure.Message);
    }

    [Fact]
    public async Task Remove_APartialRemovalSuggestsARetryThatStillNamesTheConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort { Throws = new InvalidOperationException("the service refused") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a" },
            CancellationToken.None));

        var retry = RetryCommand(failure.Remediation!);

        Assert.Contains("--connector shared_teams", retry, StringComparison.Ordinal);
        Assert.Contains("--connection-id conn-a", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("--connection-reference", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_APartialRemovalRetryDeletesTheConnectionWithoutTheGoneReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, new FakeConnectionLifecyclePort { Throws = new InvalidOperationException("the service refused") }).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a" },
            CancellationToken.None));

        var retry = new FakeConnectionLifecyclePort();

        await RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort(), retry).RemoveAsync(
            Retry(failure.Remediation!),
            CancellationToken.None);

        Assert.Equal(("shared_teams", "conn-a"), Assert.Single(retry.Deleted));
    }

    [Fact]
    public async Task Remove_APartialRemovalRetryCarriesForceWhenTheRunNeededIt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_two", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort { Throws = new InvalidOperationException("the service refused") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a", Force = true },
            CancellationToken.None));

        Assert.Contains("--force", RetryCommand(failure.Remediation!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ADeletionThatTimesOutStillReportsTheReferenceItAlreadyRemoved()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort { Throws = new TimeoutException("the request timed out") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a" },
            CancellationToken.None));

        Assert.Equal("connection-not-deleted", failure.ErrorKind);
        Assert.Contains("is unknown", failure.Message, StringComparison.Ordinal);
        Assert.Equal("cr_one", Assert.Single(connections.RemovedReferenceNames));
    }

    [Fact]
    public async Task Remove_ADeletionCancelledByTheCallerStaysCancelled()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        using var cancellation = new CancellationTokenSource();
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_teams", "conn-a"));
        var lifecycle = new FakeConnectionLifecyclePort { Throws = new OperationCanceledException(cancellation.Token) };

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => RemoveService(store, CatalogServing("shared_teams", "conn-a"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_one", ConnectionId = "conn-a" },
            cancellation.Token));
    }

    [Fact]
    public async Task Remove_DeletesTheLastConnectionOfAnUnlistedConnectorAgain()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connectors = Catalog("shared_teams");
        connectors.Connections["shared_advancedapprovals"] = [];
        var lifecycle = new FakeConnectionLifecyclePort();

        await RemoveService(store, connectors, new FakeConnectionPort(), lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-gone", Connector = "shared_advancedapprovals" },
            CancellationToken.None);

        Assert.Equal(("shared_advancedapprovals", "conn-gone"), Assert.Single(lifecycle.Deleted));
    }

    private static string RetryCommand(string remediation)
    {
        var command = remediation[(remediation.IndexOf('`', StringComparison.Ordinal) + 1)..];

        return command[..command.IndexOf('`', StringComparison.Ordinal)];
    }

    private static AgentDeploymentConnectionRemoveRequest Retry(string remediation)
    {
        var arguments = RetryCommand(remediation).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        string? Value(string option) => Array.IndexOf(arguments, option) is var index && index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;

        return new AgentDeploymentConnectionRemoveRequest
        {
            AgentId = Value("--agent-id"),
            DeploymentName = Value("--deployment-name"),
            Connector = Value("--connector"),
            ConnectionId = Value("--connection-id"),
            Force = arguments.Contains("--force"),
        };
    }

    [Fact]
    public async Task ReferenceRemove_RemovesAReferenceNothingUses()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        var result = await RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams" },
            CancellationToken.None);

        Assert.Equal("connection-reference-removed", result.Status);
        Assert.Equal("cr_teams", result.ConnectionReference);
        Assert.Equal("cr_teams", Assert.Single(connections.RemovedReferenceNames));
        Assert.Equal([false], connections.RemovalConfirmations);
    }

    [Fact]
    public async Task ReferenceRemove_RequiresAReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort()).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Contains("requires --connection-reference", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection list --agent-id agent-1 --deployment-name dev", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceRemove_WithoutAReferenceForAnAgentSelectedByName_SuggestsAListingThatSelectsItByIdentifier()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort()).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentName = "Contoso Agent", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Contains("Run `msagent deployment connection list --agent-id agent-1 --deployment-name dev` to see them.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceRemove_AsksBeforeRemovingAReferenceStillInUse()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, [new CopilotConnectionUsage { FilePath = "topics/greeting.mcs.yml", DisplayName = "Greeting" }]));
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(true, []));
        var confirmation = new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Confirmed };

        var result = await RemoveService(store, Catalog("shared_teams"), connections, confirmation: confirmation).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams" },
            CancellationToken.None);

        Assert.Equal("cr_teams", result.ConnectionReference);
        Assert.Equal([false, true], connections.RemovalConfirmations);
        Assert.Contains("Greeting", confirmation.LastDetail!, StringComparison.Ordinal);
        Assert.Equal(["Greeting"], result.Usages);
    }

    [Fact]
    public async Task ReferenceRemove_ADeclinedConfirmationKeepsTheReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, [new CopilotConnectionUsage { FilePath = "topics/greeting.mcs.yml" }]));

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), connections, confirmation: new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Declined }).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams" },
            CancellationToken.None));

        Assert.Contains("--force", failure.Message, StringComparison.Ordinal);
        Assert.Equal([false], connections.RemovalConfirmations);
    }

    [Fact]
    public async Task ReferenceRemove_ForceSkipsTheConfirmation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, [new CopilotConnectionUsage { FilePath = "topics/greeting.mcs.yml" }]));
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(true, []));
        var confirmation = new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Declined };

        var result = await RemoveService(store, Catalog("shared_teams"), connections, confirmation: confirmation).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams", Force = true },
            CancellationToken.None);

        Assert.Equal("cr_teams", result.ConnectionReference);
        Assert.Equal(0, confirmation.Calls);
        Assert.Equal([false, true], connections.RemovalConfirmations);
    }

    [Fact]
    public async Task ReferenceRemove_ARefusalWithNoUsagesIsAFailure()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView { LogicalName = "cr_teams", ConnectorId = "shared_teams" });
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, []));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams" },
            CancellationToken.None));

        Assert.Equal("connection-reference-not-removed", failure.ErrorKind);
    }

    [Fact]
    public async Task ReferenceRemove_ANameTheWorkspaceDoesNotDeclareSaysSoRatherThanBlamingFilePermissions()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView { LogicalName = "cr_teams", ConnectorId = "shared_teams" });
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, []));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_absent" },
            CancellationToken.None));

        Assert.Equal("connection-reference-not-declared", failure.ErrorKind);
        Assert.Contains("msagent deployment connection list", failure.Remediation!, StringComparison.Ordinal);
        Assert.DoesNotContain("writable", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceRemove_ATruncatedNameNamesTheDeclarationItAlmostMatched()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView { LogicalName = "cr_agent.shared_onedrive.a47dfcb7c6694e52a52ff1d6b974fa38", ConnectorId = "shared_onedrive" });
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, []));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, Catalog("shared_onedrive"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_agent.shared_onedrive.a47dfcb7c6694e52a52ff1d6b974fa3" },
            CancellationToken.None));

        Assert.Equal("connection-reference-not-declared", failure.ErrorKind);
        Assert.Contains("cr_agent.shared_onedrive.a47dfcb7c6694e52a52ff1d6b974fa38", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceRemove_AWorkspaceThatCannotBeReadKeepsTheOriginalFailure()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort { ReadThrows = new InvalidOperationException("the read failed") };
        connections.Removals.Enqueue(new CopilotConnectionReferenceRemoval(false, []));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, Catalog("shared_teams"), connections).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams" },
            CancellationToken.None));

        Assert.Equal("connection-reference-not-removed", failure.ErrorKind);
    }

    [Fact]
    public async Task ConnectionRemove_DeletesTheConnection()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var lifecycle = new FakeConnectionLifecyclePort();

        var result = await RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort(), lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-1" },
            CancellationToken.None);

        Assert.Equal("connection-removed", result.Status);
        Assert.Equal(("shared_teams", "conn-1"), Assert.Single(lifecycle.Deleted));
    }

    [Fact]
    public async Task ConnectionRemove_RefusesWhileAReferenceStillHoldsIt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-1"));
        var lifecycle = new FakeConnectionLifecyclePort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-1" },
            CancellationToken.None));

        Assert.Contains("cr_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("--force", failure.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task ConnectionRemove_RefusesWhileAReferenceHoldsItEvenWhenTheCatalogCouldNotBeRead()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with
        {
            BoundConnectionId = null,
            RecordedConnectionId = "conn-1",
            BoundConnectionExists = false,
            CatalogUnavailable = true,
        });
        var lifecycle = new FakeConnectionLifecyclePort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-1" },
            CancellationToken.None));

        Assert.Contains("cr_teams", failure.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task ConnectionRemove_ForceDeletesEvenWhileAReferenceHoldsIt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-1"));
        var lifecycle = new FakeConnectionLifecyclePort();

        var result = await RemoveService(store, Catalog("shared_teams"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-1", Force = true },
            CancellationToken.None);

        Assert.Equal("conn-1", result.ConnectionId);
        Assert.Single(lifecycle.Deleted);
        Assert.Equal(0, connections.ReadCalls);
    }

    [Fact]
    public async Task ConnectionRemove_AReferenceOnAnotherConnectionDoesNotBlockIt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-other"));
        var lifecycle = new FakeConnectionLifecyclePort();

        await RemoveService(store, Catalog("shared_teams"), connections, lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams", ConnectionId = "conn-1" },
            CancellationToken.None);

        Assert.Single(lifecycle.Deleted);
    }

    [Fact]
    public async Task ConnectionRemove_AnUnknownConnectorDeletesNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var lifecycle = new FakeConnectionLifecyclePort();

        await Assert.ThrowsAsync<ManagedAgentException>(() => RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort(), lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_nothing", ConnectionId = "conn-1" },
            CancellationToken.None));

        Assert.Empty(lifecycle.Deleted);
    }

    [Theory]
    [InlineData(null, "conn-1", "--connector")]
    [InlineData("shared_teams", null, "--connection-id")]
    public async Task ConnectionRemove_RequiresBothSelectors(string? connector, string? connectionId, string expected)
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort()).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = connector, ConnectionId = connectionId },
            CancellationToken.None));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionRemove_AnUnreferencedConnectionAsksForTheConnectorAndSaysWhereToFindIt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var lifecycle = new FakeConnectionLifecyclePort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort(), lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-orphan" },
            CancellationToken.None));

        Assert.Contains("--connector is required", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection list --agent-id agent-1 --deployment-name dev", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("powerapps.com", failure.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.Deleted);
    }

    [Fact]
    public async Task ConnectionRemove_AConnectorTheRequestNamesNeedsNoReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var lifecycle = new FakeConnectionLifecyclePort();

        await RemoveService(store, Catalog("shared_teams"), new FakeConnectionPort(), lifecycle).RemoveAsync(
            new AgentDeploymentConnectionRemoveRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-orphan", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal(("shared_teams", "conn-orphan"), Assert.Single(lifecycle.Deleted));
    }

    [Fact]
    public async Task Reference_OnlyBindsTheReferenceItDeclared()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.new");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.old", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-mine")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.new", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-mine")));

        await ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("cre98_agent.shared_teams.new", Assert.Single(connections.Bound).LogicalName);
    }

    [Fact]
    public async Task Reference_WritesNoWorkflowStateCache()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var fileSystem = new FakeCopilotFileSystem();
        var workflowStates = ManagedAgentFixtures.WorkflowStates(fileSystem);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-mine")));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "workflow-1", DisplayName = "Flow", CanEnable = true });

        await ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Empty(fileSystem.Written);
        Assert.Empty(workflowStates.Read(workspace.Root, "env-1"));
    }

    [Fact]
    public async Task Create_WritesNoWorkflowStateCache()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var fileSystem = new FakeCopilotFileSystem();
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "conn-new", null, null));

        await CreateService(store, Catalog("shared_teams"), consentFlow).CreateAsync(
            new AgentDeploymentConnectionCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Empty(ManagedAgentFixtures.WorkflowStates(fileSystem).Read(workspace.Root, "env-1"));
        Assert.Empty(fileSystem.Written);
    }

    [Fact]
    public async Task Reference_AConsentThatProducedNothingBindsNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Error, null, null, null, "the service refused"));

        var result = await ReferenceService(store, Catalog("shared_teams"), connections, consentFlow, prompt).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
        Assert.False(result.Ready);
        Assert.Null(result.ConnectionId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("the service refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reference_BindsOnlyAConnectionTheServiceReported()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        connections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-mine")));

        await ReferenceService(store, Catalog("shared_teams"), connections).CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.All(connections.Bound, binding => Assert.Contains(connections.Views.SelectMany(view => view.Candidates), candidate => candidate.ConnectionId == binding.ConnectionId));
    }

    [Fact]
    public async Task TheBindingCoordinatorResolvesTheSameWayForBothVerbs()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Existing, "conn-theirs"));
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Existing, "conn-theirs"));

        var coordinator = new ConnectionBindingCoordinator(
            prompt,
            new ConnectionCreationFlow(ManagedAgentFixtures.ConnectorReadiness(), new ScriptedConsentFlow { Throws = true }));

        var unowned = ManagedAgentFixtures.Candidate("conn-theirs", owner: "somebody@contoso.com");

        var updateConnections = new FakeConnectionPort();
        updateConnections.Views.Add(ManagedAgentFixtures.Reference("cr_existing", "shared_teams", null, unowned));

        var updateService = new AgentDeploymentConnectionService(
            Deployments(store),
            updateConnections,
            coordinator,
            prompt,
            ManagedAgentFixtures.WorkflowStates(),
            ManagedAgentFixtures.Principals(),
            CoreServicesClusterCategory.Prod);

        await updateService.UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var referenceConnections = new FakeConnectionPort();
        referenceConnections.CreatedReferenceNames.Enqueue("cre98_agent.shared_teams.abc");
        referenceConnections.Views.Add(ManagedAgentFixtures.Reference("cre98_agent.shared_teams.abc", "shared_teams", null, unowned));

        var referenceCatalog = Catalog("shared_teams");
        var referenceService = new AgentDeploymentConnectionReferenceCreateService(
            Deployments(store),
            Resolver(referenceCatalog),
            referenceConnections,
            coordinator,
            ManagedAgentFixtures.Principals());

        var result = await referenceService.CreateAsync(
            new AgentDeploymentConnectionReferenceCreateRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal("conn-theirs", Assert.Single(updateConnections.Bound).ConnectionId);
        Assert.Equal("conn-theirs", Assert.Single(referenceConnections.Bound).ConnectionId);
        Assert.Equal("conn-theirs", result.ConnectionId);
        Assert.Equal(["shared_teams", "shared_teams"], prompt.Asked);
    }
}
