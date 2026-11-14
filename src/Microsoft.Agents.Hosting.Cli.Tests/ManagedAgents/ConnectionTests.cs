using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionReadinessPlannerTests
{
    [Fact]
    public void Plan_WithExactlyOneConnectedCandidate_Binds()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        Assert.Equal(ConnectionReadinessPlanner.Bound, item.State);
        Assert.Equal("conn-1", item.ConnectionId);
    }

    [Fact]
    public void Plan_WithTwoConnectedCandidatesAndNoOwners_BindsTheFirst()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));

        Assert.Equal(ConnectionReadinessPlanner.Bound, item.State);
        Assert.Equal("conn-1", item.ConnectionId);
        Assert.Equal(2, item.Candidates.Count);
    }

    [Fact]
    public void Plan_WithSeveralCandidatesTheOperatorOwns_BindsTheFirstById()
    {
        var view = ManagedAgentFixtures.Reference(
            "cr_teams",
            "shared_teams",
            null,
            ManagedAgentFixtures.Candidate("conn-b"),
            ManagedAgentFixtures.Candidate("conn-a", owner: ManagedAgentFixtures.Principal.ToUpperInvariant()));

        var item = ManagedAgentFixtures.Plan(view);

        Assert.Equal(ConnectionReadinessPlanner.Bound, item.State);
        Assert.Equal("conn-a", item.ConnectionId);
    }

    [Fact]
    public void Plan_WithCandidatesOwnedByOthers_BindsNothing()
    {
        var view = ManagedAgentFixtures.Reference(
            "cr_teams",
            "shared_teams",
            null,
            ManagedAgentFixtures.Candidate("conn-a", owner: "svc-expense@contoso.com"),
            ManagedAgentFixtures.Candidate("conn-b", owner: "alice@contoso.com"),
            ManagedAgentFixtures.Candidate("conn-c", owner: "alice@contoso.com"));

        var item = ManagedAgentFixtures.Plan(view);

        Assert.Equal(ConnectionReadinessPlanner.Unbound, item.State);
        Assert.Null(item.ConnectionId);
        Assert.Equal(3, item.Candidates.Count);
    }

    [Fact]
    public void Plan_WithACandidateWhoseOwnerIsUnknown_BindsNothing()
    {
        var view = ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-a", owner: null));

        Assert.Equal(ConnectionReadinessPlanner.Unbound, ManagedAgentFixtures.Plan(view).State);
    }

    [Fact]
    public void Plan_WithNobodySignedIn_BindsNothing()
    {
        var view = ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-a"));

        Assert.Equal(ConnectionReadinessPlanner.Unbound, ConnectionReadinessPlanner.Plan(view, null).State);
    }

    [Fact]
    public void Plan_WithSeveralCandidatesWhoseOwnerIsUnknown_NeverPoolsThemIntoAMajority()
    {
        var view = ManagedAgentFixtures.Reference(
            "cr_teams",
            "shared_teams",
            null,
            ManagedAgentFixtures.Candidate("conn-a"),
            ManagedAgentFixtures.Candidate("conn-b"),
            ManagedAgentFixtures.Candidate("conn-c") with { Owner = "alice@contoso.com" });

        Assert.Equal("conn-a", ManagedAgentFixtures.Plan(view).ConnectionId);
    }

    [Fact]
    public void Plan_WithADisconnectedCandidateOfItsOwn_StillBindsTheConnectedOne()
    {
        var view = ManagedAgentFixtures.Reference(
            "cr_teams",
            "shared_teams",
            null,
            ManagedAgentFixtures.Candidate("conn-a"),
            ManagedAgentFixtures.Candidate("conn-b", "Disconnected"));

        var item = ManagedAgentFixtures.Plan(view);

        Assert.Equal(ConnectionReadinessPlanner.Bound, item.State);
        Assert.Equal("conn-a", item.ConnectionId);
    }

    [Fact]
    public void Plan_WithNoCandidates_IsPending()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_http", "shared_http"));

        Assert.Equal(ConnectionReadinessPlanner.NoCandidate, item.State);
        Assert.Empty(item.Candidates);
    }

    [Fact]
    public void Plan_IgnoresCandidatesThatAreNotConnected()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_http", "shared_http", null, ManagedAgentFixtures.Candidate("conn-1", "Disconnected")));

        Assert.Equal(ConnectionReadinessPlanner.NoCandidate, item.State);
    }

    [Fact]
    public void Plan_AnAlreadyBoundReference_KeepsItsConnection()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-existing", ManagedAgentFixtures.Candidate("conn-other")));

        Assert.Equal(ConnectionReadinessPlanner.AlreadyBound, item.State);
        Assert.Equal("conn-existing", item.ConnectionId);
    }

    [Fact]
    public void Plan_AnUndeclaredReferenceWithAConnection_IsBoundWhichDeclaresIt()
    {
        var view = ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-1")) with { IsDeclared = false };

        var item = ManagedAgentFixtures.Plan(view);

        Assert.Equal(ConnectionReadinessPlanner.Bound, item.State);
        Assert.Equal("conn-1", item.ConnectionId);
    }

    [Fact]
    public void Plan_AnUndeclaredReferenceWithNoConnection_StaysUndeclared()
    {
        var view = ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with { IsDeclared = false };

        var item = ManagedAgentFixtures.Plan(view);

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, item.State);
        Assert.Null(item.ConnectionId);
    }

    [Fact]
    public void Plan_AnUndeclaredReferenceThatIsAlreadyBound_IsStillUndeclared()
    {
        var view = ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-existing") with { IsDeclared = false };

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, ManagedAgentFixtures.Plan(view).State);
    }

    [Fact]
    public void IsReady_IsFalseWhileAnyReferenceIsUndeclared()
    {
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("a", "x", null, ManagedAgentFixtures.Candidate("conn-1")));
        var undeclared = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("b", "y") with { IsDeclared = false });

        Assert.False(ConnectionReadinessPlanner.IsReady([bound, undeclared]));
    }

    [Fact]
    public void Observe_NeverProposesAConnection()
    {
        var view = ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"));

        var observed = ConnectionReadinessPlanner.Observe(view);

        Assert.Equal(ConnectionReadinessPlanner.Unbound, observed.State);
        Assert.Null(observed.ConnectionId);
        Assert.False(ConnectionReadinessPlanner.IsResolved(observed));
        Assert.Single(observed.Candidates);
    }

    [Fact]
    public void Plan_ProposesWhereObserveOnlyReports()
    {
        var view = ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"));

        Assert.Equal(ConnectionReadinessPlanner.Unbound, ConnectionReadinessPlanner.Observe(view).State);
        Assert.Equal(ConnectionReadinessPlanner.Bound, ManagedAgentFixtures.Plan(view).State);
        Assert.Equal("conn-1", ManagedAgentFixtures.Plan(view).ConnectionId);
    }

    [Fact]
    public void Observe_AnUndeclaredReferenceWithACandidate_IsUnboundRatherThanBound()
    {
        var view = ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")) with { IsDeclared = false };

        Assert.Equal(ConnectionReadinessPlanner.Unbound, ConnectionReadinessPlanner.Observe(view).State);
        Assert.False(ConnectionReadinessPlanner.IsReady([ConnectionReadinessPlanner.Observe(view)]));
    }

    [Fact]
    public void FromActivation_AnIdentifiedObservation_WinsOverASharedDisplayName()
    {
        IReadOnlyList<CopilotWorkflowView> workflows =
        [
            new() { WorkflowId = "wf-1", DisplayName = "Shared" },
            new() { WorkflowId = "wf-2", DisplayName = "Shared" },
        ];

        var reported = WorkflowStateProjection.FromActivation(
            workflows,
            new CopilotWorkflowActivation { ObservedById = new Dictionary<string, bool> { ["wf-1"] = true, ["wf-2"] = false } });

        Assert.Equal(WorkflowStateProjection.Activated, reported[0].State);
        Assert.Equal(WorkflowStateProjection.Draft, reported[1].State);
    }

    [Fact]
    public void ToCacheEntries_AnIdentifiedObservation_IsRecordedForASharedDisplayName()
    {
        IReadOnlyList<CopilotWorkflowView> workflows =
        [
            new() { WorkflowId = "wf-1", DisplayName = "Shared" },
            new() { WorkflowId = "wf-2", DisplayName = "Shared" },
        ];

        var entries = WorkflowStateProjection.ToCacheEntries(
            workflows,
            new CopilotWorkflowActivation { ObservedById = new Dictionary<string, bool> { ["wf-1"] = true, ["wf-2"] = false } });

        Assert.Equal(WorkflowStateCache.Activated, entries["wf-1"]);
        Assert.Equal(WorkflowStateCache.Draft, entries["wf-2"]);
    }

    [Fact]
    public void FromActivation_AnIdentifiedObservation_WinsOverADisagreeingName()
    {
        IReadOnlyList<CopilotWorkflowView> workflows = [new() { WorkflowId = "wf-1", DisplayName = "Renamed" }];

        var reported = WorkflowStateProjection.FromActivation(
            workflows,
            new CopilotWorkflowActivation
            {
                ObservedById = new Dictionary<string, bool> { ["wf-1"] = true },
                Observed = new Dictionary<string, bool> { ["Renamed"] = false },
            });

        Assert.Equal(WorkflowStateProjection.Activated, Assert.Single(reported).State);
    }

    [Fact]
    public void FromCache_WithNoObservation_FallsBackToTheWorkspaceRecord()
    {
        IReadOnlyList<CopilotWorkflowView> workflows =
        [
            new() { WorkflowId = "wf-1", DisplayName = "Running", IsActivatedLocally = true },
            new() { WorkflowId = "wf-2", DisplayName = "Drafted", IsActivatedLocally = false },
            new() { WorkflowId = "wf-3", DisplayName = "Neither" },
        ];

        var reported = WorkflowStateProjection.FromCache(workflows, new Dictionary<string, int>());

        Assert.Equal(
            [WorkflowStateProjection.Activated, WorkflowStateProjection.Draft, WorkflowStateProjection.Unknown],
            reported.Select(workflow => workflow.State));
    }

    [Fact]
    public void FromCache_AnObservation_WinsOverTheWorkspaceRecord()
    {
        IReadOnlyList<CopilotWorkflowView> workflows = [new() { WorkflowId = "wf-1", DisplayName = "Drifted", IsActivatedLocally = true }];

        var reported = WorkflowStateProjection.FromCache(workflows, new Dictionary<string, int> { ["wf-1"] = WorkflowStateCache.Draft });

        Assert.Equal(WorkflowStateProjection.Draft, Assert.Single(reported).State);
    }

    [Fact]
    public void ToCacheEntries_NeverRecordsTheWorkspaceRecordAsAnObservation()
    {
        IReadOnlyList<CopilotWorkflowView> workflows = [new() { WorkflowId = "wf-1", DisplayName = "Running", IsActivatedLocally = true }];

        var reported = WorkflowStateProjection.FromActivation(workflows, new CopilotWorkflowActivation());

        Assert.Equal(WorkflowStateProjection.Activated, Assert.Single(reported).State);
        Assert.Empty(WorkflowStateProjection.ToCacheEntries(workflows, new CopilotWorkflowActivation()));
    }

    [Fact]
    public void ToCacheEntries_RecordsOnlyWhatTheServiceSettled()
    {
        IReadOnlyList<CopilotWorkflowView> workflows =
        [
            new() { WorkflowId = "wf-1", DisplayName = "Running" },
            new() { WorkflowId = "wf-2", DisplayName = "Silent", IsActivatedLocally = true },
        ];

        var entries = WorkflowStateProjection.ToCacheEntries(
            workflows,
            new CopilotWorkflowActivation { Observed = new Dictionary<string, bool> { ["Running"] = true } });

        Assert.Equal(WorkflowStateCache.Activated, entries["wf-1"]);
        Assert.DoesNotContain("wf-2", entries.Keys);
    }

    [Fact]
    public void IsReady_IsTrueOnlyWhenEveryReferenceIsBound()
    {
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("a", "x", null, ManagedAgentFixtures.Candidate("conn-1")));
        var pending = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("b", "y"));

        Assert.True(ConnectionReadinessPlanner.IsReady([bound]));
        Assert.False(ConnectionReadinessPlanner.IsReady([bound, pending]));
    }

    [Fact]
    public void Observe_AReferenceWithNoConnector_IsNotActionable()
    {
        var observed = ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_orphan", string.Empty) with { IsDeclared = false });

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, observed.State);
        Assert.False(ConnectionReadinessPlanner.IsActionable(observed));
    }

    [Fact]
    public void IsActionable_IsTrueForABoundReferenceSoItsConnectionCanBeChanged()
    {
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-existing"));

        Assert.True(ConnectionReadinessPlanner.IsActionable(bound));
    }

    [Fact]
    public void IsActionable_IsTrueWhenTheExistingConnectionsCouldNotBeListed()
    {
        var unlisted = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        Assert.Equal(ConnectionReadinessPlanner.NoCandidate, unlisted.State);
        Assert.True(ConnectionReadinessPlanner.IsActionable(unlisted));
    }

    [Fact]
    public void IsProvablyWithoutAConnection_IsFalseWhenTheCatalogCouldNotBeRead()
    {
        var unlisted = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        Assert.Equal(ConnectionReadinessPlanner.NoCandidate, unlisted.State);
        Assert.False(ConnectionReadinessPlanner.IsProvablyWithoutAConnection(unlisted));
    }

    [Fact]
    public void IsProvablyWithoutAConnection_IsTrueOnlyWhenAReadableCatalogReportedNone()
    {
        var none = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql"));
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-1"));
        var candidate = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));

        Assert.True(ConnectionReadinessPlanner.IsProvablyWithoutAConnection(none));
        Assert.False(ConnectionReadinessPlanner.IsProvablyWithoutAConnection(bound));
        Assert.False(ConnectionReadinessPlanner.IsProvablyWithoutAConnection(candidate));
        Assert.Throws<ArgumentNullException>(() => ConnectionReadinessPlanner.IsProvablyWithoutAConnection(null!));
    }

    [Fact]
    public void Observe_AVerifiedBindingIsConnected()
    {
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-existing"));

        Assert.Equal(ConnectionReadinessPlanner.AlreadyBound, bound.State);
        Assert.Equal("conn-existing", bound.ConnectionId);
        Assert.Equal(ConnectionSelectionScope.Connected, ConnectionSelectionScope.DescribeState(bound.State));
    }

    [Fact]
    public void Observe_AnUnboundReferenceIsNotConnectedWhenTheExistingConnectionsCouldNotBeListed()
    {
        var unbound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        Assert.Equal(ConnectionSelectionScope.NotConnected, ConnectionSelectionScope.DescribeState(unbound.State));
    }

    [Fact]
    public void Select_WhenTheConnectionsCouldNotBeListed_TakesTheOperatorsIdOnTrustButDoesNotClaimItIsVerified()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        var bound = ConnectionReadinessPlanner.Select(item, "conn-made-in-browser");

        Assert.Equal(ConnectionReadinessPlanner.BoundUnverified, bound.State);
        Assert.Equal("conn-made-in-browser", bound.ConnectionId);
        Assert.True(ConnectionReadinessPlanner.IsBinding(bound));
        Assert.False(ConnectionReadinessPlanner.IsResolved(bound));
    }

    [Fact]
    public void Rebind_WhenTheCatalogCouldNotBeRead_DowngradesTheBindingRatherThanClaimingItIsVerified()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true }),
        };

        ConnectionBindingCoordinator.BindGroup(plan, [0], ConnectionReadinessPlanner.Bound, "conn-1");

        Assert.Equal(ConnectionReadinessPlanner.BoundUnverified, plan[0].State);
        Assert.False(ConnectionReadinessPlanner.IsReady(plan));
    }

    [Fact]
    public void Rebind_WhenAConnectionWasCreated_StaysVerifiedEvenWithoutACatalog()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true }),
        };

        ConnectionBindingCoordinator.BindGroup(plan, [0], ConnectionReadinessPlanner.Created, "conn-1");

        Assert.Equal(ConnectionReadinessPlanner.Created, plan[0].State);
        Assert.True(ConnectionReadinessPlanner.IsReady(plan));
    }

    [Fact]
    public void Select_WhenTheConnectionsCouldNotBeListed_ReducesAProviderPathToItsId()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        var bound = ConnectionReadinessPlanner.Select(item, "/providers/Microsoft.PowerApps/apis/shared_sql/connections/abc123");

        Assert.Equal("abc123", bound.ConnectionId);
    }

    [Fact]
    public void Select_WhenTheConnectionsWereListed_StillRejectsAnIdThatIsNotAmongThem()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));

        var failure = Assert.Throws<ManagedAgentException>(() => ConnectionReadinessPlanner.Select(item, "conn-9"));

        Assert.Contains("conn-1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_WhenTheListingFailedButSomeConnectionsWereStillReturned_StillValidates()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")) with { CatalogUnavailable = true });

        Assert.Throws<ManagedAgentException>(() => ConnectionReadinessPlanner.Select(item, "conn-9"));
    }

    [Fact]
    public void Select_WhenTheConnectionsWereListed_StillRejectsADisconnectedOne()
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1", "Disconnected")));

        Assert.Throws<UsageException>(() => ConnectionReadinessPlanner.Select(item, "conn-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Select_RefusesAnEmptyConnectionId(string? connectionId)
    {
        var item = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        Assert.ThrowsAny<ArgumentException>(() => ConnectionReadinessPlanner.Select(item, connectionId!));
    }

    [Fact]
    public void Status_IsOnlyEverConnectedOrNotConnected()
    {
        CopilotConnectionView[] views =
        [
            ManagedAgentFixtures.Reference("cr_bound", "shared_teams", "conn-1"),
            ManagedAgentFixtures.Reference("cr_unbound", "shared_sql"),
            ManagedAgentFixtures.Reference("cr_unlisted", "shared_office365") with { CatalogUnavailable = true },
            ManagedAgentFixtures.Reference("cr_bound_unlisted", "shared_onedrive", "conn-2"),
            ManagedAgentFixtures.Reference("cr_undeclared", "shared_msnweather") with { IsDeclared = false },
        ];

        var plan = views.Select(ConnectionReadinessPlanner.Observe).ToList();
        var groups = ConnectionSelectionScope.Group(plan, [.. Enumerable.Range(0, plan.Count)]);

        Assert.All(groups, group => Assert.Contains(
            ConnectionSelectionScope.Status(plan, group),
            new[] { ConnectionSelectionScope.Connected, ConnectionSelectionScope.NotConnected }));
    }

    [Fact]
    public void Status_AVerifiedBindingIsConnected()
    {
        var plan = new[] { ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_bound", "shared_teams", "conn-1")) };
        var group = Assert.Single(ConnectionSelectionScope.Group(plan, [0]));

        Assert.Equal(ConnectionSelectionScope.Connected, ConnectionSelectionScope.Status(plan, group));
    }

    [Fact]
    public void Connected_ReadsCandidatesFromTheReferenceEvenWhenTheDecisionDroppedThem()
    {
        var view = ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-a", ManagedAgentFixtures.Candidate("conn-b"), ManagedAgentFixtures.Candidate("conn-a"));

        Assert.Empty(ManagedAgentFixtures.Plan(view).Candidates);
        Assert.Equal(["conn-a", "conn-b"], ConnectionReadinessPlanner.Connected(view).Select(candidate => candidate.ConnectionId));
    }

    [Fact]
    public void Connected_LeavesOutCandidatesThatAreNotConnected()
    {
        var view = ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-broken", "Error"));

        Assert.Equal(["conn-a"], ConnectionReadinessPlanner.Connected(view).Select(candidate => candidate.ConnectionId));
    }

    [Fact]
    public void Rebind_PointsABoundReferenceAtADifferentConnection()
    {
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-a"));

        var changed = ConnectionReadinessPlanner.Rebind(bound, ConnectionReadinessPlanner.Bound, "conn-b");

        Assert.Equal(ConnectionReadinessPlanner.Bound, changed.State);
        Assert.Equal("conn-b", changed.ConnectionId);
    }

    [Fact]
    public void Rebind_LeavesAReferenceAloneWhenTheConnectionIsTheOneItAlreadyUses()
    {
        var bound = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-a"));

        Assert.Same(bound, ConnectionReadinessPlanner.Rebind(bound, ConnectionReadinessPlanner.Bound, "CONN-A"));
    }

    [Fact]
    public void Rebind_RewritesAnUndeclaredReferenceEvenWhenTheConnectionMatches()
    {
        var undeclared = ManagedAgentFixtures.Plan(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-a") with { IsDeclared = false });

        var changed = ConnectionReadinessPlanner.Rebind(undeclared, ConnectionReadinessPlanner.Bound, "conn-a");

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, undeclared.State);
        Assert.Equal(ConnectionReadinessPlanner.Bound, changed.State);
        Assert.Equal("conn-a", changed.ConnectionId);
    }
}

public sealed class AgentDeploymentConnectionResultRenderingTests
{
    private static AgentDeploymentConnectionResult Result(params AgentDeploymentConnectionItem[] connections) => new()
    {
        Status = "connections-updated",
        AgentId = "agent-1",
        DeploymentId = "deployment-1",
        DeploymentName = "dev",
        EnvironmentId = "env-1",
        Connections = connections,
    };

    private static AgentDeploymentConnectionItem Item(string state, string? connectionId = null, string? consentLink = null, params string[] candidates) => new()
    {
        ConnectionReference = "cr_sql",
        ConnectorId = "shared_sql",
        ConnectorStatus = ConnectionSelectionScope.Connected,
        State = state,
        ConnectionId = connectionId,
        ConsentLink = consentLink,
        Candidates = candidates,
    };

    [Fact]
    public void Options_AConnectionCreatedForOneReferenceIsOfferedToTheNextOnTheSameConnector()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_one", "shared_teams", null)),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_two", "shared_teams", null)),
        };
        var groups = ConnectionSelectionScope.Group(plan, [0, 1], byReference: true);

        ConnectionBindingCoordinator.BindGroup(plan, groups[0].References, ConnectionReadinessPlanner.Created, "conn-new");

        var offered = ConnectionSelectionScope.Options(plan, groups[1]);

        Assert.Equal("conn-new", Assert.Single(offered).ConnectionId);
        Assert.False(offered[0].IsCurrent);
    }

    [Fact]
    public void Options_AConnectionInUseOnAnotherConnectorIsNotOffered()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null)),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null)),
        };
        var groups = ConnectionSelectionScope.Group(plan, [0, 1], byReference: true);
        var sql = groups.Single(group => group.ConnectorId == "shared_sql");
        var teams = groups.Single(group => group.ConnectorId == "shared_teams");

        ConnectionBindingCoordinator.BindGroup(plan, teams.References, ConnectionReadinessPlanner.Created, "conn-teams");

        Assert.Empty(ConnectionSelectionScope.Options(plan, sql));
    }

    [Fact]
    public void Options_RebindingMarksOnlyTheNewConnectionAsInUse()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-old", ManagedAgentFixtures.Candidate("conn-old"), ManagedAgentFixtures.Candidate("conn-new"))),
        };
        var group = Assert.Single(ConnectionSelectionScope.Group(plan, [0]));

        ConnectionBindingCoordinator.BindGroup(plan, [0], ConnectionReadinessPlanner.Bound, "conn-new");

        var options = ConnectionSelectionScope.Options(plan, group);

        Assert.True(options.Single(option => option.ConnectionId == "conn-new").IsCurrent);
        Assert.False(options.Single(option => option.ConnectionId == "conn-old").IsCurrent);
    }

    [Fact]
    public void Options_TheConnectionInUseIsOfferedFirst()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-a", ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b"))),
        };
        var group = Assert.Single(ConnectionSelectionScope.Group(plan, [0]));

        ConnectionBindingCoordinator.BindGroup(plan, [0], ConnectionReadinessPlanner.Bound, "conn-b");

        Assert.Equal("conn-b", ConnectionSelectionScope.Options(plan, group)[0].ConnectionId);
    }

    [Fact]
    public void BoundConnections_FollowsTheRunsDecision()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-old", ManagedAgentFixtures.Candidate("conn-new"))),
        };
        var group = Assert.Single(ConnectionSelectionScope.Group(plan, [0]));

        Assert.Equal(["conn-old"], ConnectionSelectionScope.BoundConnections(plan, group));

        ConnectionBindingCoordinator.BindGroup(plan, [0], ConnectionReadinessPlanner.Bound, "conn-new");

        Assert.Equal(["conn-new"], ConnectionSelectionScope.BoundConnections(plan, group));
    }

    [Fact]
    public void BoundConnections_AnUnboundConnectorHasNone()
    {
        var plan = new List<ConnectionPlanItem> { ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_teams", "shared_teams")) };

        Assert.Empty(ConnectionSelectionScope.BoundConnections(plan, Assert.Single(ConnectionSelectionScope.Group(plan, [0]))));
    }

    [Fact]
    public void Menu_CarriesTheConnectionsSoAChoiceShowsOnReturn()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-old", ManagedAgentFixtures.Candidate("conn-new"))),
        };
        var groups = ConnectionSelectionScope.Group(plan, [0]);

        ConnectionBindingCoordinator.BindGroup(plan, [0], ConnectionReadinessPlanner.Bound, "conn-new");

        Assert.Equal(["conn-new"], Assert.Single(ConnectionSelectionScope.Menu(plan, groups).Connectors).Connections);
    }

    [Fact]
    public void Status_APartlyBoundConnectorNamesBothCounts()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_one", "shared_sql", "conn-1")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_two", "shared_sql")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_three", "shared_sql")),
        };

        Assert.Equal("connected (1), not connected (2)", ConnectionSelectionScope.Status(plan, Assert.Single(ConnectionSelectionScope.Group(plan, [0, 1, 2]))));
    }

    [Fact]
    public void Status_AFullyBoundConnectorIsConnected()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_one", "shared_sql", "conn-1")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_two", "shared_sql", "conn-1")),
        };

        Assert.Equal(ConnectionSelectionScope.Connected, ConnectionSelectionScope.Status(plan, Assert.Single(ConnectionSelectionScope.Group(plan, [0, 1]))));
    }

    [Fact]
    public void Status_AConnectorWithNothingBoundIsNotConnected()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_one", "shared_sql")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_two", "shared_sql")),
        };

        Assert.Equal(ConnectionSelectionScope.NotConnected, ConnectionSelectionScope.Status(plan, Assert.Single(ConnectionSelectionScope.Group(plan, [0, 1]))));
    }

    [Fact]
    public void Group_ByConnectorPutsEveryReferenceOfOneConnectorTogether()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_one", "shared_sql")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_two", "shared_sql")),
        };

        var group = Assert.Single(ConnectionSelectionScope.Group(plan, [0, 1]));

        Assert.Equal("shared_sql", group.ConnectorId);
        Assert.Equal([0, 1], group.References);
    }

    [Fact]
    public void Group_ByReferenceGivesEachReferenceItsOwnGroup()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_one", "shared_sql")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_two", "shared_sql")),
        };

        var groups = ConnectionSelectionScope.Group(plan, [0, 1], byReference: true);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.References));
        Assert.All(groups, group => Assert.Equal("shared_sql", group.ConnectorId));
    }

    [Fact]
    public void Group_ByReferenceStatusIsPerReference()
    {
        var plan = new List<ConnectionPlanItem>
        {
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_bound", "shared_sql", "conn-1")),
            ConnectionReadinessPlanner.Observe(ManagedAgentFixtures.Reference("cr_open", "shared_sql")),
        };

        var groups = ConnectionSelectionScope.Group(plan, [0, 1], byReference: true);

        Assert.Equal(
            [ConnectionSelectionScope.NotConnected, ConnectionSelectionScope.Connected],
            groups.Select(group => ConnectionSelectionScope.Status(plan, group)));
    }

    [Fact]
    public void ConnectorListing_AnAvailableConnectionIsNeverShownAsABinding()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.NoCandidate, candidates: "0216182f (nguhoa@contoso.com)")).ToTextLines();

        var row = Assert.Single(lines, line => line.Contains("shared_sql", StringComparison.Ordinal));

        Assert.DoesNotContain("0216182f", row, StringComparison.Ordinal);
        Assert.Contains(TextGrid.Dash, row, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectorListing_ABoundConnectionIsShown()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.AlreadyBound, "conn-1", candidates: "conn-2 (other)")).ToTextLines();

        var row = Assert.Single(lines, line => line.Contains("shared_sql", StringComparison.Ordinal));

        Assert.Contains("conn-1", row, StringComparison.Ordinal);
        Assert.DoesNotContain("conn-2", row, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectorListing_NothingBoundShowsADash()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.NoCandidate)).ToTextLines();

        Assert.Contains(TextGrid.Dash, Assert.Single(lines, line => line.Contains("shared_sql", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceListing_AnAvailableConnectionIsNeverShownAsABinding()
    {
        var lines = (Result(Item(ConnectionReadinessPlanner.Unbound, candidates: "conn-2 (other)")) with { ReferenceFirst = true }).ToTextLines();

        var row = Assert.Single(lines, line => line.Contains("cr_sql", StringComparison.Ordinal));

        Assert.Contains(ConnectionSelectionScope.NotConnected, row, StringComparison.Ordinal);
        Assert.DoesNotContain("conn-2", row, StringComparison.Ordinal);
        Assert.Contains(TextGrid.Dash, row, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceListing_ABoundConnectionIsShown()
    {
        var lines = (Result(Item(ConnectionReadinessPlanner.AlreadyBound, "conn-1")) with { ReferenceFirst = true }).ToTextLines();

        Assert.Contains("conn-1", Assert.Single(lines, line => line.Contains("cr_sql", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionReadinessPlanner.Bound, ConnectionSelectionScope.Connected)]
    [InlineData(ConnectionReadinessPlanner.AlreadyBound, ConnectionSelectionScope.Connected)]
    [InlineData(ConnectionReadinessPlanner.Created, ConnectionSelectionScope.Connected)]
    [InlineData(ConnectionReadinessPlanner.Unbound, ConnectionSelectionScope.NotConnected)]
    [InlineData(ConnectionReadinessPlanner.NoCandidate, ConnectionSelectionScope.NotConnected)]
    [InlineData(ConnectionReadinessPlanner.Undeclared, ConnectionSelectionScope.NotConnected)]
    public void DescribeState_SpeaksTheSameWordsAsAConnectorStatus(string state, string expected)
    {
        Assert.Equal(expected, ConnectionSelectionScope.DescribeState(state));
    }

    [Fact]
    public void DescribeState_OnlyEverSaysConnectedOrNotConnected()
    {
        string[] states =
        [
            ConnectionReadinessPlanner.Bound,
            ConnectionReadinessPlanner.AlreadyBound,
            ConnectionReadinessPlanner.Created,
            ConnectionReadinessPlanner.Unbound,
            ConnectionReadinessPlanner.NoCandidate,
            ConnectionReadinessPlanner.Undeclared,
        ];

        Assert.All(states, state => Assert.Contains(
            ConnectionSelectionScope.DescribeState(state),
            new[] { ConnectionSelectionScope.Connected, ConnectionSelectionScope.NotConnected }));
    }

    [Fact]
    public void ReferenceListing_ShowsConnectedRatherThanTheInternalState()
    {
        var result = Result(
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_bound", ConnectorId = "shared_teams", State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" },
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_open", ConnectorId = "shared_sql", State = ConnectionReadinessPlanner.NoCandidate }) with
        { ReferenceFirst = true };

        var lines = result.ToTextLines();

        Assert.Contains(lines, line => line.Contains("cr_bound", StringComparison.Ordinal) && line.Contains(ConnectionSelectionScope.Connected, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("cr_open", StringComparison.Ordinal) && line.Contains(ConnectionSelectionScope.NotConnected, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(ConnectionReadinessPlanner.AlreadyBound, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(ConnectionReadinessPlanner.NoCandidate, StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceListing_LeadsWithTheReferenceColumn()
    {
        var lines = (Result(new AgentDeploymentConnectionItem { ConnectionReference = "cr_teams", ConnectorId = "shared_teams", State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" }) with { ReferenceFirst = true }).ToTextLines();

        var header = Assert.Single(lines, line => line.Contains("CONNECTION REFERENCE", StringComparison.Ordinal));

        Assert.True(header.IndexOf("CONNECTION REFERENCE", StringComparison.Ordinal) < header.IndexOf("CONNECTOR", StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectorListing_StillGroupsByConnector()
    {
        Assert.Contains(
            Result(new AgentDeploymentConnectionItem { ConnectionReference = "cr_teams", ConnectorId = "shared_teams", ConnectorStatus = ConnectionSelectionScope.Connected, State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" }).ToTextLines(),
            line => line.Contains("CONNECTION REFERENCES", StringComparison.Ordinal));
    }

    private static string[] Cells(string line) =>
        [.. line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    [Fact]
    public void ToTextLines_ShowsTheConnectorStatusTheMenuWouldShow()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.NoCandidate) with { ConnectorStatus = ConnectionSelectionScope.NotConnected }).ToTextLines();

        Assert.Contains(lines, line => Cells(line) is ["shared_sql", "not connected", "\u2014", "cr_sql"]);
    }

    [Fact]
    public void ToTextLines_AWorkflowNothingHasObserved_ShowsAsUnknownWithoutChangingTheResult()
    {
        var result = Result(Item(ConnectionReadinessPlanner.Bound, "conn-1")) with
        {
            Workflows = [new AgentDeploymentWorkflowItem { WorkflowId = "wf-1", DisplayName = "SubmitExpense", State = WorkflowStateProjection.Unknown }],
        };

        Assert.Contains(result.ToTextLines(), line => Cells(line) is ["SubmitExpense", "unknown"]);
        Assert.Equal(WorkflowStateProjection.Unknown, Assert.Single(result.Workflows).State);
    }

    [Fact]
    public void ToTextLines_ASuspendedWorkflow_StillShowsAsADraft()
    {
        var result = Result(Item(ConnectionReadinessPlanner.Bound, "conn-1")) with
        {
            Workflows = [new AgentDeploymentWorkflowItem { WorkflowId = "wf-1", DisplayName = "SubmitExpense", State = WorkflowStateProjection.Suspended }],
        };

        Assert.Contains(result.ToTextLines(), line => Cells(line) is ["SubmitExpense", "draft"]);
    }

    [Fact]
    public void ToTextLines_AnAgentWorkflowThatIsRunning_ShowsAsPublished()
    {
        var result = Result(Item(ConnectionReadinessPlanner.Bound, "conn-1")) with
        {
            Workflows = [new AgentDeploymentWorkflowItem { WorkflowId = "wf-1", DisplayName = "SubmitExpense", State = WorkflowStateProjection.Activated }],
        };

        Assert.Contains(result.ToTextLines(), line => Cells(line) is ["SubmitExpense", "published"]);
        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains(WorkflowStateProjection.Activated, StringComparison.Ordinal));
        Assert.Equal(WorkflowStateProjection.Activated, Assert.Single(result.Workflows).State);
    }

    [Fact]
    public void ToTextLines_ASuspendedWorkflow_ShowsAsDraft()
    {
        var result = Result(Item(ConnectionReadinessPlanner.Bound, "conn-1")) with
        {
            Workflows = [new AgentDeploymentWorkflowItem { WorkflowId = "wf-1", DisplayName = "SubmitExpense", State = WorkflowStateProjection.Suspended }],
        };

        Assert.Contains(result.ToTextLines(), line => Cells(line) is ["SubmitExpense", "draft"]);
        Assert.Equal(WorkflowStateProjection.Suspended, Assert.Single(result.Workflows).State);
    }

    [Fact]
    public void ToTextLines_GroupsEveryReferenceUnderItsConnector()
    {
        var lines = Result(
            Item(ConnectionReadinessPlanner.NoCandidate, consentLink: "https://apps.powerapps.com/consent"),
            Item(ConnectionReadinessPlanner.Bound, "conn-1")).ToTextLines();

        Assert.Equal(
            [
                "  CONNECTOR   STATUS     CONNECTION  CONNECTION REFERENCES",
                "  shared_sql  connected  conn-1      cr_sql",
                "                                     cr_sql",
            ],
            lines);
    }

    [Fact]
    public void ConnectorListing_StacksReferencesAndSeparatesConnectorsWithABlankLine()
    {
        var lines = Result(
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_sql", ConnectorId = "shared_sql", ConnectorStatus = ConnectionSelectionScope.Connected, State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" },
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_sql2", ConnectorId = "shared_sql", ConnectorStatus = ConnectionSelectionScope.Connected, State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" },
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_teams", ConnectorId = "shared_teams", ConnectorStatus = ConnectionSelectionScope.NotConnected, State = ConnectionReadinessPlanner.NoCandidate }).ToTextLines();

        Assert.Equal(
            [
                "  CONNECTOR     STATUS         CONNECTION  CONNECTION REFERENCES",
                "  shared_sql    connected      conn-1      cr_sql",
                "                                           cr_sql2",
                string.Empty,
                "  shared_teams  not connected  \u2014           cr_teams",
            ],
            lines);
    }

    [Fact]
    public void ConnectorListing_NamesEveryReferenceExactlyOnceAndNeverJoinsThem()
    {
        var lines = Result(
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_sql", ConnectorId = "shared_sql", ConnectorStatus = ConnectionSelectionScope.Connected, State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" },
            new AgentDeploymentConnectionItem { ConnectionReference = "cr_sql2", ConnectorId = "shared_sql", ConnectorStatus = ConnectionSelectionScope.Connected, State = ConnectionReadinessPlanner.AlreadyBound, ConnectionId = "conn-1" }).ToTextLines();

        Assert.DoesNotContain(lines, line => line.Contains("cr_sql, cr_sql2", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("(2)", StringComparison.Ordinal));
        Assert.Equal(1, lines.Count(line => line.TrimEnd().EndsWith("cr_sql2", StringComparison.Ordinal)));
    }

    [Fact]
    public void ToTextLines_NeverTitlesTheTables()
    {
        var result = Result(Item(ConnectionReadinessPlanner.Bound, "conn-1")) with
        {
            Workflows = [new AgentDeploymentWorkflowItem { WorkflowId = "wf-1", DisplayName = "SubmitExpense", State = "draft" }],
        };

        var lines = result.ToTextLines();

        Assert.DoesNotContain(lines, line => line.Trim() == "Connections");
        Assert.DoesNotContain(lines, line => line.Trim() == "Workflows");
        Assert.Equal(
            [
                "  CONNECTOR   STATUS     CONNECTION  CONNECTION REFERENCES",
                "  shared_sql  connected  conn-1      cr_sql",
                string.Empty,
                "  WORKFLOW       STATUS",
                "  SubmitExpense  draft",
            ],
            lines);
    }

    [Fact]
    public void ToTextLines_NeverRestatesReadinessAboveTheTable()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.NoCandidate)).ToTextLines();

        Assert.DoesNotContain(lines, line => line.Contains("is not ready", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("env-1", StringComparison.Ordinal));
        Assert.NotEqual(string.Empty, lines[0]);
    }

    [Fact]
    public void ToTextLines_WithNoReferencesAtAll_SaysSoRatherThanRenderingNothing()
    {
        Assert.Equal(["Deployment dev declares no connection references."], Result().ToTextLines());
    }

    [Fact]
    public void ToTextLines_NeverRendersAConsentLink()
    {
        var lines = Result(
            Item(ConnectionReadinessPlanner.NoCandidate, consentLink: "https://apps.powerapps.com/consent", candidates: ["conn-1 (a)", "conn-2 (b)"])).ToTextLines();

        Assert.DoesNotContain(lines, line => line.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void ToTextLines_NamesTheConnectionButNotItsOwner()
    {
        var lines = Result(
            Item(ConnectionReadinessPlanner.AlreadyBound, "conn-existing", candidates: ["conn-1 (a)", "conn-2 (b)"]) with { ConnectionOwner = "alice@contoso.com" }).ToTextLines();

        Assert.Contains(lines, line => Cells(line) is ["shared_sql", _, "conn-existing", "cr_sql"]);
        Assert.DoesNotContain(lines, line => line.Contains("alice@contoso.com", StringComparison.Ordinal));
    }

    [Fact]
    public void ToTextLines_AReferenceWithNoConnection_NamesNoCandidateInTheConnectionColumn()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.NoCandidate, candidates: ["conn-1 (a)", "conn-2 (b)"])).ToTextLines();

        Assert.DoesNotContain(lines, line => line.Contains("conn-1 (a)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("conn-2 (b)", StringComparison.Ordinal));
    }

    [Fact]
    public void ToTextLines_NeverTellsTheOperatorToRunAnotherCommand()
    {
        var lines = Result(Item(ConnectionReadinessPlanner.NoCandidate), Item(ConnectionReadinessPlanner.Undeclared)).ToTextLines();

        Assert.DoesNotContain(lines, line => line.Contains("msagent deployment", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("--connection-id", StringComparison.Ordinal));
    }

    [Fact]
    public void ToTextLines_RendersWorkflowsAsNameAndStatusOnly()
    {
        var result = Result(Item(ConnectionReadinessPlanner.Bound, "conn-1")) with
        {
            Workflows = [new AgentDeploymentWorkflowItem
            {
                WorkflowId = "wf-1",
                DisplayName = "SubmitExpense",
                FilePath = "workflows/SubmitExpense/workflow.json",
                ConnectionReferences = ["cr_sql"],
                State = "draft",
            }],
        };

        var lines = result.ToTextLines();

        Assert.Contains(lines, line => line.Contains("  WORKFLOW", StringComparison.Ordinal) && line.Contains("STATUS", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("SubmitExpense", StringComparison.Ordinal) && line.EndsWith("draft", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("workflow.json", StringComparison.Ordinal));
    }

    [Fact]
    public void ToTextLines_StillRendersTheDiagnostics()
    {
        var result = Result(Item(ConnectionReadinessPlanner.NoCandidate)) with { Diagnostics = ["cr_sql: Connection creation was cancelled."] };

        Assert.Contains(result.ToTextLines(), line => line.Contains("Connection creation was cancelled.", StringComparison.Ordinal));
    }
}

public sealed class WorkflowSettlementTests
{
    private static ConnectionPlanItem Bound(string logicalName) =>
        new(new CopilotConnectionView { LogicalName = logicalName, ConnectorId = "shared_teams" }, ConnectionReadinessPlanner.Bound, "conn-1", []);

    [Fact]
    public void NewlyStartable_AWorkflowWhoseActivationIsUnknown_IsStarted()
    {
        var settlement = new WorkflowSettlement([new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Cloned", ConnectionReferences = ["cr_teams"] }]);

        Assert.Equal(["wf-1"], settlement.NewlyStartable([Bound("cr_teams")]).Select(change => change.WorkflowId));
    }

    [Fact]
    public void NewlyStartable_AWorkflowTheWorkspaceRecordsAsRunning_IsLeftAlone()
    {
        var settlement = new WorkflowSettlement([new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Running", ConnectionReferences = ["cr_teams"], IsActivatedLocally = true }]);

        Assert.Empty(settlement.NewlyStartable([Bound("cr_teams")]));
    }

    [Fact]
    public void NewlyStartable_AWorkflowWithAnUnboundReference_IsNotStarted()
    {
        var settlement = new WorkflowSettlement([new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Cloned", ConnectionReferences = ["cr_teams", "cr_sql"] }]);

        Assert.Empty(settlement.NewlyStartable([Bound("cr_teams")]));
    }

    [Fact]
    public void NewlyStartable_AWorkflowThisRunAlreadyStarted_IsNotStartedAgain()
    {
        var settlement = new WorkflowSettlement([new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Cloned", ConnectionReferences = ["cr_teams"] }]);
        var requested = settlement.NewlyStartable([Bound("cr_teams")]);

        settlement.Record(new CopilotWorkflowActivation { Activated = ["Cloned"] }, requested);

        Assert.Empty(settlement.NewlyStartable([Bound("cr_teams")]));
    }
}

public sealed class WorkflowStateCacheTests
{
    private const string Workspace = "/work/agent";

    private static WorkflowStateCache Cache(FakeCopilotFileSystem fileSystem) => new(fileSystem);

    [Fact]
    public void ReadWrite_RoundTripsWhatWasObserved()
    {
        var fileSystem = new FakeCopilotFileSystem();
        var cache = Cache(fileSystem);

        cache.Write(Workspace, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated, ["SendReceipt"] = WorkflowStateCache.Draft });

        var observed = cache.Read(Workspace, "env-1");

        Assert.Equal(WorkflowStateCache.Activated, observed["SubmitExpense"]);
        Assert.Equal(WorkflowStateCache.Draft, observed["SendReceipt"]);
    }

    [Fact]
    public void Write_OneEnvironment_LeavesTheOthersUnchanged()
    {
        var fileSystem = new FakeCopilotFileSystem();
        var cache = Cache(fileSystem);

        cache.Write(Workspace, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });
        cache.Write(Workspace, "env-2", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Draft });

        Assert.Equal(WorkflowStateCache.Activated, cache.Read(Workspace, "env-1")["SubmitExpense"]);
        Assert.Equal(WorkflowStateCache.Draft, cache.Read(Workspace, "env-2")["SubmitExpense"]);
    }

    [Fact]
    public void Read_AnEnvironmentThatWasNeverObserved_IsEmpty()
    {
        var cache = Cache(new FakeCopilotFileSystem());

        cache.Write(Workspace, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });

        Assert.Empty(cache.Read(Workspace, "env-other"));
    }

    [Fact]
    public void Read_AMissingFile_IsEmptyAndDoesNotThrow()
    {
        Assert.Empty(Cache(new FakeCopilotFileSystem()).Read(Workspace, "env-1"));
    }

    [Fact]
    public void Read_AnUnreadableFile_IsEmptyAndDoesNotThrow()
    {
        var fileSystem = new FakeCopilotFileSystem();
        fileSystem.Files[Path.Combine(Workspace, ".mcs/workflow-state.json")] = "{ not json";

        Assert.Empty(Cache(fileSystem).Read(Workspace, "env-1"));
    }

    [Fact]
    public void Write_AfterAnUnreadableFile_ReplacesIt()
    {
        var fileSystem = new FakeCopilotFileSystem();
        fileSystem.Files[Path.Combine(Workspace, ".mcs/workflow-state.json")] = "{ not json";
        var cache = Cache(fileSystem);

        cache.Write(Workspace, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });

        Assert.Equal(WorkflowStateCache.Activated, cache.Read(Workspace, "env-1")["SubmitExpense"]);
    }

    [Fact]
    public void Read_ANullEnvironmentEntry_IsEmptyAndDoesNotThrow()
    {
        var fileSystem = new FakeCopilotFileSystem();
        fileSystem.Files[Path.Combine(Workspace, ".mcs/workflow-state.json")] = "{\"env-1\":null}";

        Assert.Empty(Cache(fileSystem).Read(Workspace, "env-1"));
    }

    [Fact]
    public void Read_EnvironmentKeysDifferingOnlyByCase_RejectTheWholeFile()
    {
        var fileSystem = new FakeCopilotFileSystem();
        fileSystem.Files[Path.Combine(Workspace, ".mcs/workflow-state.json")] = "{\"env-1\":{\"SubmitExpense\":1},\"ENV-1\":{\"SubmitExpense\":0}}";

        Assert.Empty(Cache(fileSystem).Read(Workspace, "env-1"));
    }

    [Fact]
    public void Read_AStateValueTheCacheNeverWrites_RejectsTheWholeFile()
    {
        var fileSystem = new FakeCopilotFileSystem();
        fileSystem.Files[Path.Combine(Workspace, ".mcs/workflow-state.json")] = "{\"env-1\":{\"Good\":1,\"Bogus\":7}}";

        Assert.Empty(Cache(fileSystem).Read(Workspace, "env-1"));
    }

    [Fact]
    public void Invalidate_ForgetsOneEnvironmentAndLeavesTheOthers()
    {
        var cache = Cache(new FakeCopilotFileSystem());

        cache.Write(Workspace, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });
        cache.Write(Workspace, "env-2", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });
        cache.Invalidate(Workspace, "env-1");

        Assert.Empty(cache.Read(Workspace, "env-1"));
        Assert.Equal(WorkflowStateCache.Activated, cache.Read(Workspace, "env-2")["SubmitExpense"]);
    }

    [Fact]
    public void Write_WhileAnotherCommandHoldsTheWorkspace_StillRecordsWhatWasObserved()
    {
        var fileSystem = new FakeCopilotFileSystem();
        using var holder = HeldNamedMutex.OnAnotherThread(WorkflowStateCache.WriteGuardNamePrefix, Workspace);

        new WorkflowStateCache(fileSystem, TimeSpan.Zero).Write(Workspace, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });

        Assert.Equal(WorkflowStateCache.Activated, Cache(fileSystem).Read(Workspace, "env-1")["SubmitExpense"]);
    }
}

public sealed class ConnectorReadinessWaitTests
{
    private const string CustomConnector = "cre98_weather-5f0123456789abcdef";

    private static ConnectorReadinessWait Wait(IConnectorCatalogPort connectors, TimeSpan? timeout = null) =>
        new(connectors, new SyncNamingRulesAdapter(), timeout ?? TimeSpan.FromMinutes(1), (_, _) => Task.CompletedTask);

    [Theory]
    [InlineData("shared_office365", false)]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_sql", false)]
    [InlineData(CustomConnector, true)]
    [InlineData("/providers/Microsoft.PowerApps/apis/" + CustomConnector, true)]
    [InlineData("", false)]
    [InlineData("-5f0123456789abcdef", false)]
    public void IsCustomConnector_RecognisesTheCustomConnectorSuffix(string connectorId, bool expected)
    {
        Assert.Equal(expected, Wait(new FakeConnectorCatalogPort()).IsCustomConnector(connectorId));
    }

    [Fact]
    public async Task WaitAsync_AStandardConnector_NeverReadsTheCatalog()
    {
        var connectors = new FakeConnectorCatalogPort();

        await Wait(connectors).WaitAsync(ManagedAgentFixtures.EnvironmentContext(), "shared_office365", CancellationToken.None);

        Assert.Equal(0, connectors.Calls);
    }

    [Fact]
    public async Task WaitAsync_AConnectorAlreadyServed_ReturnsAfterOneRead()
    {
        var connectors = new FakeConnectorCatalogPort { InternalIds = { CustomConnector } };

        await Wait(connectors).WaitAsync(ManagedAgentFixtures.EnvironmentContext(), CustomConnector, CancellationToken.None);

        Assert.Equal(1, connectors.Calls);
    }

    [Fact]
    public async Task WaitAsync_AConnectorThatAppearsLater_PollsUntilItIsServed()
    {
        var connectors = new FakeConnectorCatalogPort();
        connectors.Responses.Enqueue([]);
        connectors.Responses.Enqueue(["shared_office365"]);
        connectors.Responses.Enqueue([CustomConnector]);

        await Wait(connectors).WaitAsync(ManagedAgentFixtures.EnvironmentContext(), CustomConnector, CancellationToken.None);

        Assert.Equal(3, connectors.Calls);
    }

    [Fact]
    public async Task WaitAsync_AConnectorThatNeverAppears_ProceedsAnywayWithoutThrowing()
    {
        var connectors = new FakeConnectorCatalogPort();

        await Wait(connectors, TimeSpan.Zero).WaitAsync(ManagedAgentFixtures.EnvironmentContext(), CustomConnector, CancellationToken.None);

        Assert.Equal(1, connectors.Calls);
    }

    [Fact]
    public async Task WaitAsync_ACatalogThatFails_ProceedsAnywayWithoutThrowing()
    {
        var connectors = new FakeConnectorCatalogPort { Throws = new InvalidOperationException("catalog down") };

        await Wait(connectors, TimeSpan.Zero).WaitAsync(ManagedAgentFixtures.EnvironmentContext(), CustomConnector, CancellationToken.None);

        Assert.Equal(1, connectors.Calls);
    }
}

public sealed class ConnectionConsentLinkTests
{
    [Theory]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_office365users", "shared_office365users")]
    [InlineData("shared_office365users", "shared_office365users")]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_teams/", "shared_teams")]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_teams/connections/1b7e5cddf05642f2846898f5cc5223a0", "1b7e5cddf05642f2846898f5cc5223a0")]
    [InlineData("1b7e5cddf05642f2846898f5cc5223a0", "1b7e5cddf05642f2846898f5cc5223a0")]
    public void NormalizeResourceId_ReducesAProviderPathToItsInternalId(string resourceId, string expected)
    {
        Assert.Equal(expected, ConnectionConsentLink.NormalizeResourceId(resourceId));
    }

    [Fact]
    public void Build_SendsTheConnectorInternalIdNotTheProviderPath()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Prod, "env-1", "/providers/Microsoft.PowerApps/apis/shared_office365users", null, null);

        Assert.Equal("shared_office365users", System.Web.HttpUtility.ParseQueryString(link.Query)["connector"]);
        Assert.DoesNotContain("providers", link.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TargetsThePublicCloudHost()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Prod, "Default-72f9", "shared_office365", null, null);

        Assert.Equal("apps.powerapps.com", link.Host);
        Assert.Equal("/appframework/e/Default-72f9/connections/new", link.AbsolutePath);
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Gov)]
    [InlineData(CoreServicesClusterCategory.High)]
    [InlineData(CoreServicesClusterCategory.DoD)]
    [InlineData(CoreServicesClusterCategory.Mooncake)]
    public void Build_UsesADistinctHostPerCluster(CoreServicesClusterCategory cluster)
    {
        Assert.NotEqual(
            ConnectionConsentLink.HostFor(CoreServicesClusterCategory.Prod),
            ConnectionConsentLink.HostFor(cluster));
    }

    [Fact]
    public void Build_RoundTripsTheCallbackAndNonce()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Prod, "Default-72f9", "shared_sql", new Uri("http://127.0.0.1:51234/callback"), "nonce-1");
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);

        Assert.Equal("shared_sql", query["connector"]);
        Assert.Equal("http://127.0.0.1:51234/callback", query["callbackUrl"]);
        Assert.Equal("nonce-1", query["nonce"]);
        Assert.Equal("1", query["v"]);
    }

    [Fact]
    public void Build_OmitsTheCallbackWhenNothingIsListening()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Prod, "Default-72f9", "shared_sql", null, null);

        Assert.DoesNotContain("callbackUrl", link.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", link.Query, StringComparison.Ordinal);
    }
}

[Collection("CurrentDirectory")]
public sealed class AgentDeploymentConnectionServiceTests
{
    private static AgentDeploymentConnectionService Create(JsonAgentConfigStore store, FakeConnectionPort connections, FakeCopilotCatalogPort? catalog = null, IConnectionConsentFlow? consentFlow = null, IConnectorCatalogPort? connectors = null, WorkflowStateCache? workflowStates = null, IConnectionSelectionPrompt? prompt = null)
    {
        var selectionPrompt = prompt ?? new ScriptedSelectionPrompt();

        return new(
            new AgentDeploymentConnectionResolver(
                ManagedAgentFixtures.AgentResolver(store),
                ManagedAgentFixtures.Resolver(),
                ManagedAgentFixtures.Identities(),
                catalog ?? new FakeCopilotCatalogPort { ResolveReturns = "bot-1" },
                ManagedAgentFixtures.WorkflowWorkspace()),
            connections,
            new ConnectionBindingCoordinator(
                selectionPrompt,
                new ConnectionCreationFlow(
                    ManagedAgentFixtures.ConnectorReadiness(connectors),
                    consentFlow ?? new ScriptedConsentFlow { Throws = true })),
            selectionPrompt,
            workflowStates ?? ManagedAgentFixtures.WorkflowStates(),
            ManagedAgentFixtures.Principals(),
            CoreServicesClusterCategory.Prod);
    }

    private static AgentDeploymentConnectionReader Reader(JsonAgentConfigStore store, FakeConnectionPort connections, FakeCopilotCatalogPort? catalog = null, WorkflowStateCache? workflowStates = null) =>
        new(
            new AgentDeploymentConnectionResolver(
                ManagedAgentFixtures.AgentResolver(store),
                ManagedAgentFixtures.Resolver(),
                ManagedAgentFixtures.Identities(),
                catalog ?? new FakeCopilotCatalogPort { ResolveReturns = "bot-1" },
                ManagedAgentFixtures.WorkflowWorkspace()),
            connections,
            workflowStates ?? ManagedAgentFixtures.WorkflowStates(),
            CoreServicesClusterCategory.Prod);

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Deployed(bool deployed = true)
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();

        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(lastDeployedTime: deployed ? ManagedAgentFixtures.Now : null));

        return (workspace, store);
    }

    [Fact]
    public async Task Show_ReportsReadinessWithoutBindingOrActivating()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("connections-previewed", result.Status);
        Assert.Equal(0, connections.BindCalls);
        Assert.Equal(0, connections.ActivateCalls);
        Assert.Equal(ConnectionReadinessPlanner.Unbound, Assert.Single(result.Connections).State);
        Assert.False(result.ConnectionsReady);
        Assert.False(result.Ready);
    }

    [Fact]
    public async Task Show_TheConnectorStatusMatchesTheMenusForTheSameConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_bound", "shared_sql", "conn-broken"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_open", "shared_sql"));

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var plan = connections.Views.Select(ConnectionReadinessPlanner.Observe).ToArray();
        var group = Assert.Single(ConnectionSelectionScope.Group(plan, [0, 1]));

        Assert.Equal(ConnectionSelectionScope.Status(plan, group), Assert.Single(result.Connections.Select(item => item.ConnectorStatus).Distinct()));
        Assert.Equal("connected (1), not connected (1)", Assert.Single(result.Connections.Select(item => item.ConnectorStatus).Distinct()));
    }

    [Fact]
    public async Task List_TheTwoViewsReportTheSameState()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-1"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql"));

        var byConnector = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var byReference = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev", ByReference = true },
            CancellationToken.None);

        Assert.True(byReference.ReferenceFirst);
        Assert.False(byConnector.ReferenceFirst);
        Assert.Equal(
            byConnector.Connections.Select(item => (item.ConnectionReference, item.ConnectorId, item.State, item.ConnectionId, item.ConnectorStatus)),
            byReference.Connections.Select(item => (item.ConnectionReference, item.ConnectorId, item.State, item.ConnectionId, item.ConnectorStatus)));
        Assert.Equal(byConnector.Ready, byReference.Ready);
        Assert.Equal(byConnector.Diagnostics, byReference.Diagnostics);
    }

    [Fact]
    public async Task List_ChangesNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));

        await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
        Assert.Equal(0, connections.ActivateCalls);
        Assert.Empty(connections.CreatedReferenceConnectors);
        Assert.Empty(connections.RemovedReferenceNames);
    }

    [Fact]
    public async Task Show_AConnectorWithNothingBoundIsReportedAsNotConnected()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_open", "shared_sql"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_other", "shared_sql"));

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(ConnectionSelectionScope.NotConnected, Assert.Single(result.Connections.Select(item => item.ConnectorStatus).Distinct()));
    }

    [Fact]
    public async Task Show_AConnectorWithEveryReferenceBoundIsReportedAsConnected()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_sql", "conn-1"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_two", "shared_sql", "conn-1"));

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(ConnectionSelectionScope.Connected, Assert.Single(result.Connections.Select(item => item.ConnectorStatus).Distinct()));
    }

    [Fact]
    public async Task Show_AReferenceBoundInTheCloud_IsTheOnlyOneReportedAsBound()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_bound", "shared_office365", "conn-existing"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_pickable", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal([ConnectionReadinessPlanner.AlreadyBound, ConnectionReadinessPlanner.Unbound], result.Connections.Select(item => item.State));
        Assert.False(result.ConnectionsReady);
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Show_AnUndeclaredReference_IsReportedAndBlocksReadiness()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-1"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-2") with { IsDeclared = false });

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.False(result.ConnectionsReady);
        Assert.Contains(result.Connections, connection => connection.State == ConnectionReadinessPlanner.Undeclared);
    }

    [Fact]
    public async Task Show_SurfacesUsagesAndCandidateOwners()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1") with { Owner = "svc@contoso.com" }) with
        {
            Usages = [new CopilotConnectionUsage { FilePath = "topics/SendReceipt.mcs.yml", DisplayName = "Send receipt" }],
        });

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Single(result.Connections);
        Assert.Contains("conn-1", Assert.Single(result.Connections).Candidates[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_WorkflowsWithNoObservation_AreUnknown()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Unknown, Assert.Single(result.Workflows).State);
    }

    [Fact]
    public async Task Show_ReportsTheStateObservedForThisEnvironment()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });
        var states = ManagedAgentFixtures.WorkflowStates();
        var request = new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" };

        await Reader(store, connections, workflowStates: states).ShowAsync(request, CancellationToken.None);
        states.Write(connections.Directories[0], "env-1", new Dictionary<string, int> { ["wf-1"] = WorkflowStateCache.Activated });
        var result = await Reader(store, connections, workflowStates: states).ShowAsync(request, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Activated, Assert.Single(result.Workflows).State);
        Assert.True(result.WorkflowsReady);
    }

    [Fact]
    public async Task Show_IgnoresAnotherEnvironmentsObservation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });
        var states = ManagedAgentFixtures.WorkflowStates();
        var request = new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" };

        await Reader(store, connections, workflowStates: states).ShowAsync(request, CancellationToken.None);
        states.Write(connections.Directories[0], "env-other", new Dictionary<string, int> { ["wf-1"] = WorkflowStateCache.Activated });
        var result = await Reader(store, connections, workflowStates: states).ShowAsync(request, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Unknown, Assert.Single(result.Workflows).State);
    }

    [Fact]
    public async Task Show_WithNeitherDeploymentSelector_IsAUsageError()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Reader(store, new FakeConnectionPort()).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Show_WhenTheAgentWasNeverDeployed_FailsLikeUpdate()
    {
        var (workspace, store) = Deployed(deployed: false);
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<ManagedAgentException>(() => Reader(store, new FakeConnectionPort(), new FakeCopilotCatalogPort()).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Update_AnAmbiguousReferenceWithAnExplicitConnection_Binds()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_sql", ConnectionId = "conn-2", NonInteractive = true },
            CancellationToken.None);

        Assert.True(result.ConnectionsReady);
        Assert.Equal("conn-2", Assert.Single(connections.Bound).ConnectionId);
    }

    [Fact]
    public async Task Update_AnExplicitConnectionWhenTheConnectionsCouldNotBeListed_IsBoundButNotReportedReady()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_sql", ConnectionId = "conn-made-in-browser", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.ConnectionsReady);
        Assert.Equal("conn-made-in-browser", Assert.Single(connections.Bound).ConnectionId);
    }

    [Fact]
    public async Task Update_AnExplicitConnectionThatIsNotACandidate_IsRejectedWithoutBinding()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_sql", ConnectionId = "conn-9", NonInteractive = true },
            CancellationToken.None));

        Assert.Contains("conn-1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Update_AnExplicitConnectionThatIsDisconnected_IsRejected()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2", "Disconnected")));

        await Assert.ThrowsAsync<UsageException>(() => Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_sql", ConnectionId = "conn-2", NonInteractive = true },
            CancellationToken.None));
    }

    [Fact]
    public async Task Update_AnExplicitConnectionOnOneConnector_BindsEveryReferenceItServes()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_a", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_b", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-2", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(["cr_a", "cr_b"], connections.Bound.Select(binding => binding.LogicalName));
        Assert.Equal(["conn-2", "conn-2"], connections.Bound.Select(binding => binding.ConnectionId));
    }

    [Fact]
    public async Task Update_AnExplicitConnectionMatchingSeveralConnectors_IsAUsageError()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_a", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_b", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-1")));

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionId = "conn-1", NonInteractive = true },
            CancellationToken.None));

        Assert.Contains("exactly one connector", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, connections.BindCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_AnUnknownConnectionReference_IsRejectedBeforeAnyBinding(bool hasReferences)
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        if (hasReferences)
        {
            connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));
        }

        var states = ManagedAgentFixtures.WorkflowStates();
        states.Write(workspace.Root, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", Project = workspace.Root, ConnectionReference = " cr_missing ", NonInteractive = true },
            CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("connection-reference-not-declared", failure.ErrorKind);
        Assert.Contains("cr_missing", failure.Message, StringComparison.Ordinal);
        Assert.Contains(workspace.Root, failure.Message, StringComparison.Ordinal);
        if (hasReferences)
        {
            Assert.Contains("cr_sql", failure.Remediation, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("msagent deployment connection list", failure.Remediation, StringComparison.Ordinal);
            Assert.Contains(workspace.Root, failure.Remediation, StringComparison.Ordinal);
            Assert.Contains("--agent-id agent-1", failure.Remediation, StringComparison.Ordinal);
            Assert.Contains("--deployment-name dev", failure.Remediation, StringComparison.Ordinal);
        }

        Assert.Equal(0, connections.BindCalls);
        Assert.Equal(0, connections.ActivateCalls);
        Assert.Equal(WorkflowStateCache.Activated, states.Read(workspace.Root, "env-1")["SubmitExpense"]);
    }

    [Fact]
    public async Task Update_AnUnknownConnectionReferenceOfAnAgentSelectedByName_SuggestsAListingThatSelectsItByIdentifier()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeConnectionPort()).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentName = "Contoso Agent", DeploymentName = "dev", ConnectionReference = "cr_missing", NonInteractive = true },
            CancellationToken.None));

        Assert.Equal("Run `msagent deployment connection list --agent-id agent-1 --deployment-name dev` to see the references the workspace declares.", failure.Remediation);
    }

    [Fact]
    public async Task Update_WhenActivationFails_ForgetsThePreviousObservation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort { ActivateThrows = new InvalidOperationException("Dataverse said no.") };
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-1"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });
        var states = ManagedAgentFixtures.WorkflowStates();
        states.Write(workspace.Root, "env-1", new Dictionary<string, int> { ["SubmitExpense"] = WorkflowStateCache.Activated });

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None));

        Assert.Empty(states.Read(workspace.Root, "env-1"));
    }

    [Fact]
    public async Task Update_AReferenceWithNoConnector_IsNeverOfferedForCreation()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_orphan", string.Empty) with { IsDeclared = false });
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();
        var consentFlow = new ScriptedConsentFlow();

        await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        var menu = Assert.Single(prompt.Menus);
        var orphan = Assert.Single(menu.Connectors, connector => connector.ConnectorId.Length == 0);

        Assert.False(orphan.Selectable);
        Assert.Empty(consentFlow.RequestedConnectors);
    }

    [Fact]
    public async Task Update_AReferenceWithNoConnector_IsReportedRatherThanCrashing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_orphan", string.Empty) with { IsDeclared = false });
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        var item = Assert.Single(result.Connections);

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, item.State);
        Assert.Equal(string.Empty, item.ConnectorId);
        Assert.Null(item.ConsentLink);
    }

    [Fact]
    public async Task Show_AReferenceWithNoConnector_IsReportedRatherThanCrashing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_orphan", string.Empty) with { IsDeclared = false });

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, Assert.Single(result.Connections).State);
        Assert.False(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_TwoWorkflowsSharingAName_AreNeitherAttributedNorCached()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Shared", CanEnable = true });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-2", DisplayName = "Shared", CanEnable = true, IsActivatedLocally = false });
        connections.ActivatedWorkflows.Add("Shared");
        var states = ManagedAgentFixtures.WorkflowStates();

        var result = await Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Empty(states.Read(connections.Directories[0], "env-1"));
        Assert.DoesNotContain(result.Workflows, workflow => workflow.State == WorkflowStateProjection.Activated);
        Assert.False(result.WorkflowsReady);
    }

    [Fact]
    public async Task Update_AnUndeclaredReferenceWhoseConnectionsCouldNotBeListed_ReadsAsNotConnected()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with { IsDeclared = false, CatalogUnavailable = true });
        var prompt = new ScriptedSelectionPrompt();
        var consentFlow = new ScriptedConsentFlow();

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var item = Assert.Single(result.Connections);

        Assert.Equal(ConnectionReadinessPlanner.Undeclared, item.State);
        Assert.Equal(ConnectionSelectionScope.NotConnected, item.ConnectorStatus);
    }

    [Fact]
    public async Task Update_AnUndeclaredReferenceWithAConnection_IsBoundWithoutBeingAsked()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")) with { IsDeclared = false });
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_teams"], prompt.Asked);
        Assert.Equal("conn-1", Assert.Single(connections.Bound).ConnectionId);
        Assert.True(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_TheConnectorSelector_NarrowsToThatConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_sql" },
            CancellationToken.None);

        Assert.Equal(["shared_sql"], prompt.Asked);
    }

    [Fact]
    public async Task Update_TheConnectorSelector_BindsNothingOutsideTheSelection()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-sql")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_teams" },
            CancellationToken.None);

        Assert.Equal(["cr_teams"], connections.Bound.Select(binding => binding.LogicalName));
    }

    [Fact]
    public async Task Update_AnExplicitConnection_BindsNothingOutsideTheSelection()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-sql")));

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", ConnectionReference = "cr_teams", ConnectionId = "conn-2" },
            CancellationToken.None);

        Assert.Equal(["cr_teams"], connections.Bound.Select(binding => binding.LogicalName));
    }

    [Fact]
    public async Task Update_AWorkflowTheServiceSaidNothingAbout_IsNotCachedAsADraft()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Answered", CanEnable = true });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-2", DisplayName = "Unanswered", CanEnable = true });
        connections.ActivatedWorkflows.Add("Answered");
        var states = ManagedAgentFixtures.WorkflowStates();

        var result = await Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        var observed = states.Read(connections.Directories[0], "env-1");

        Assert.Equal([WorkflowStateCache.Activated], observed.Values);
        Assert.DoesNotContain("Unanswered", observed.Keys);
        Assert.Equal(WorkflowStateProjection.Unknown, result.Workflows.Single(workflow => workflow.DisplayName == "Unanswered").State);
        Assert.False(result.WorkflowsReady);
    }

    [Fact]
    public async Task Update_AnUnknownWorkflowState_IsNotReady()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-1"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Unanswered" });

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.True(result.ConnectionsReady);
        Assert.False(result.WorkflowsReady);
        Assert.False(result.Ready);
    }

    [Fact]
    public async Task Show_AnUnknownWorkflowState_IsNotReady()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-1"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "NeverObserved" });

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.True(result.ConnectionsReady);
        Assert.False(result.WorkflowsReady);
        Assert.False(result.Ready);
    }

    [Fact]
    public async Task Update_WithTwoCandidatesAndNoSelection_BindsNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(ConnectionReadinessPlanner.Bound, Assert.Single(result.Connections).State);
        Assert.Equal(1, connections.BindCalls);
    }

    [Fact]
    public async Task Update_WithOneCandidate_IsBoundWithoutBeingAskedButStillOffered()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("conn-1", Assert.Single(connections.Bound).ConnectionId);
        Assert.Equal(["shared_sql"], prompt.Asked);
    }

    [Fact]
    public async Task Update_UnderNonInteractive_StillPromptsButNeverOpensABrowser()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(["shared_sql"], prompt.Asked);
        Assert.Empty(connections.Bound);
    }

    [Fact]
    public async Task Update_WhenNoConnectionBelongsToTheOperator_NamesTheCandidateIds()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference(
            "cr_sql",
            "shared_sql",
            null,
            ManagedAgentFixtures.Candidate("conn-1", owner: "alice@contoso.com"),
            ManagedAgentFixtures.Candidate("conn-2", owner: "svc@contoso.com")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
        Assert.False(result.ConnectionsReady);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("conn-1, conn-2", StringComparison.Ordinal) && diagnostic.Contains("--connection-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_AConnectionTheOperatorOwns_IsStillBoundWithoutAsking()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference(
            "cr_sql",
            "shared_sql",
            null,
            ManagedAgentFixtures.Candidate("conn-1", owner: "alice@contoso.com"),
            ManagedAgentFixtures.Candidate("conn-2")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("conn-2", Assert.Single(connections.Bound).ConnectionId);
        Assert.Equal(ManagedAgentFixtures.Principal, Assert.Single(result.Connections).ConnectionOwner);
        Assert.True(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_AnInUseConnectionTheCatalogDoesNotCallConnected_KeepsItsOwner()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference(
            "cr_teams",
            "shared_teams",
            "conn-fresh",
            ManagedAgentFixtures.Candidate("conn-fresh", "Created", "alice@contoso.com")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var option = Assert.Single(Assert.Single(prompt.Options));

        Assert.Equal("conn-fresh", option.ConnectionId);
        Assert.Equal("alice@contoso.com", option.Owner);
        Assert.True(option.IsCurrent);
    }

    [Fact]
    public async Task Update_ThePromptOffersEveryCandidateWithItsOwner()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference(
            "cr_sql",
            "shared_sql",
            null,
            ManagedAgentFixtures.Candidate("conn-1") with { Owner = "alice@contoso.com" },
            ManagedAgentFixtures.Candidate("conn-2") with { Owner = "svc@contoso.com" }));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var options = Assert.Single(prompt.Options);

        Assert.Equal(["conn-1", "conn-2"], options.Select(option => option.ConnectionId));
        Assert.Equal(["alice@contoso.com", "svc@contoso.com"], options.Select(option => option.Owner));
        Assert.Equal([1, 2], options.Select(option => option.Number));
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Update_SkippingAtThePrompt_LeavesTheReferenceUnbound()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.False(result.ConnectionsReady);
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Update_AConnectorWithNoConnection_IsOfferedAtThePrompt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "cr_http", "user@contoso.com", null));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_http"], prompt.Asked);
                Assert.Equal(["conn-new"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task Update_GoingBackFromAConnectorWithNoConnection_NeverOpensTheBrowser()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));
        var consentFlow = new ScriptedConsentFlow();

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(consentFlow.RequestedConnectors);
        Assert.Equal(0, connections.BindCalls);
        Assert.False(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_OneConnectionBindsEveryReferenceOnThatConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams_a", "shared_teams"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams_b", "shared_teams"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams_c", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", null, null, null));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_teams"], prompt.Asked);
        Assert.Equal(["cr_teams_a", "cr_teams_b", "cr_teams_c"], connections.Bound.Select(binding => binding.LogicalName));
        Assert.Equal(["conn-new", "conn-new", "conn-new"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.True(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_OneCreatedConnectionBindsEveryReferenceOnThatConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams_a", "shared_teams"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams_b", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", null, null, null));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_teams"], consentFlow.RequestedConnectors);
        Assert.Equal(["conn-new", "conn-new"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.True(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_ListsEveryConnectorWithTheWorkThatIsLeft()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        for (var index = 0; index < 8; index++)
        {
            connections.Views.Add(ManagedAgentFixtures.Reference($"cr_bound_{index}", "shared_sql", $"conn-bound-{index}"));
        }

        connections.Views.Add(ManagedAgentFixtures.Reference("cr_single", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-single")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_missing_1", "shared_http"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_missing_2", "shared_teams"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_choice", "shared_office365"));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var menu = Assert.Single(prompt.Menus);

        Assert.Equal(["shared_http", "shared_office365", "shared_teams", "shared_sql"], menu.Connectors.Select(connector => connector.ConnectorId));
        Assert.Equal([1, 2, 3, 4], menu.Connectors.Select(connector => connector.Number));
        Assert.Equal([true, true, true, false], menu.Connectors.Select(connector => connector.NeedsAttention));
        Assert.Equal([true, true, true, true], menu.Connectors.Select(connector => connector.Selectable));
        Assert.Equal("not connected", menu.Connectors[0].Status);
        Assert.Equal("connected", menu.Connectors[3].Status);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task Update_ChoosingSeveralConnectorsAtOnce_ResolvesThemInOrder()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_choice", "shared_office365"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_missing", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Connectors.Enqueue([1, 2]);
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_http", "shared_office365"], prompt.Asked);
        Assert.Empty(connections.Bound);
        Assert.Equal([3, 3], prompt.Menus.Select(menu => menu.Connectors.Count(connector => connector.NeedsAttention)));
        Assert.False(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_ChoosingAConnectorFromTheList_ResolvesOnlyThatOne()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_choice", "shared_office365"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_missing", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Connectors.Enqueue([1]);
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_http"], prompt.Asked);
        Assert.Empty(connections.Bound);
        Assert.False(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_WithSeveralConnectors_ShowsTheListEvenWhenOnlyOneNeedsAttention()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_bound", "shared_sql", "conn-bound"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_choice", "shared_office365"));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var menu = Assert.Single(prompt.Menus);

        Assert.Equal([true, true], menu.Connectors.Select(connector => connector.Selectable));
        Assert.Equal([true, false], menu.Connectors.Select(connector => connector.NeedsAttention));
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task Update_WithOneConnector_GoesStraightToItWithoutTheList()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(prompt.Menus);
        Assert.Equal(["shared_sql"], prompt.Asked);
    }

    [Fact]
    public async Task Update_WithEverythingBound_KeepsOfferingTheListUntilTheOperatorQuits()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-sql"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-teams"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Connectors.Enqueue([1]);
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(2, prompt.Menus.Count);
        Assert.Equal(["shared_sql"], prompt.Asked);
        Assert.Empty(connections.Bound);
    }

    [Fact]
    public async Task Update_NonInteractive_RunsTheSameFlowWithTheBrowserSuppressed()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", null, null, null));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(["shared_http"], prompt.Asked);
        Assert.Equal([false], consentFlow.OpenedBrowser);
        Assert.Equal(["conn-new"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task Update_Interactively_OpensTheBrowser()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", null, null, null));

        await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal([true], consentFlow.OpenedBrowser);
    }

    [Fact]
    public async Task Update_NonInteractive_ShowsTheSameConnectorListAsInteractive()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        var menu = Assert.Single(prompt.Menus);

        Assert.Equal(["shared_http", "shared_teams"], menu.Connectors.Select(connector => connector.ConnectorId));
    }

    [Fact]
    public async Task Update_ThePromptNamesTheFilesThatUseEachReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with
        {
            Usages = [new CopilotConnectionUsage { FilePath = "workflows/SubmitExpense/workflow.json" }],
        });
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("cr_sql", prompt.References[0][0].LogicalName);
        Assert.Equal(["workflows/SubmitExpense/workflow.json"], prompt.References[0][0].Usages);
    }

    [Fact]
    public async Task Update_ThePromptOffersOnlyConnectedCandidates()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-broken", "Error")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["conn-1"], Assert.Single(prompt.Options).Select(option => option.ConnectionId));
    }

    [Fact]
    public async Task Update_AnAlreadyBoundConnector_IsOfferedButLeftAloneWhenTheOperatorGoesBack()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-existing"));
        var consentFlow = new ScriptedConsentFlow();
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_sql"], prompt.Asked);
        Assert.Empty(consentFlow.RequestedConnectors);
        Assert.Equal(0, connections.BindCalls);
        Assert.Equal(ConnectionReadinessPlanner.AlreadyBound, Assert.Single(result.Connections).State);
    }

    [Fact]
    public async Task Update_ChangingABoundConnection_RebindsEveryReferenceOnThatConnector()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql_read", "shared_sql", "conn-a", ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql_write", "shared_sql", "conn-a", ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Existing, "conn-b"));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["cr_sql_read", "cr_sql_write"], connections.Bound.Select(binding => binding.LogicalName));
        Assert.Equal(["conn-b", "conn-b"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.Equal([ConnectionReadinessPlanner.Bound, ConnectionReadinessPlanner.Bound], result.Connections.Select(item => item.State));
    }

    [Fact]
    public async Task Update_ReselectingTheConnectionAlreadyBound_WritesNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-a", ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Existing, "conn-a"));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
        Assert.Equal(ConnectionReadinessPlanner.AlreadyBound, Assert.Single(result.Connections).State);
    }

    [Fact]
    public async Task Update_ABoundConnectorsOptions_LeadWithTheConnectionInUse()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-b", ManagedAgentFixtures.Candidate("conn-a"), ManagedAgentFixtures.Candidate("conn-b")));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var options = Assert.Single(prompt.Options);

        Assert.Equal(["conn-b", "conn-a"], options.Select(option => option.ConnectionId));
        Assert.Equal([1, 2], options.Select(option => option.Number));
        Assert.Equal([true, false], options.Select(option => option.IsCurrent));
    }

    [Fact]
    public async Task Update_AConnectorWhoseConnectionsCouldNotBeListed_IsStillOfferedAsNotConnected()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-teams"));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var menu = Assert.Single(prompt.Menus);
        var unlisted = Assert.Single(menu.Connectors, connector => connector.ConnectorId == "shared_sql");

        Assert.True(unlisted.Selectable);
        Assert.Equal(ConnectionSelectionScope.NotConnected, unlisted.Status);
    }

    [Fact]
    public async Task Update_AConnectorWhoseConnectionsWereListed_IsStillReportedByItsBinding()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql") with { CatalogUnavailable = true });
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-teams"));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var menu = Assert.Single(prompt.Menus);

        Assert.All(menu.Connectors, connector => Assert.Contains(
            connector.Status,
            new[] { ConnectionSelectionScope.Connected, ConnectionSelectionScope.NotConnected }));
    }

    [Fact]
    public async Task Update_NeverTellsTheOperatorToRunTheCommandTheyJustRan()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains("Run `msagent deployment connection update`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_RecordsTheObservedWorkflowStatesForThisEnvironment()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-2", DisplayName = "NotifyOnReject", CanEnable = true });
        connections.ActivatedWorkflows.Add("SubmitExpense");
        connections.DraftWorkflows.Add("NotifyOnReject");
        var states = ManagedAgentFixtures.WorkflowStates();

        var result = await Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        var observed = states.Read(connections.Directories[0], "env-1");

        Assert.Equal(WorkflowStateCache.Activated, observed["wf-1"]);
        Assert.Equal(WorkflowStateCache.Draft, observed["wf-2"]);
        Assert.False(result.WorkflowsReady);
    }

    [Fact]
    public async Task Update_AWorkflowTheServiceRefused_IsRecordedAsADraft()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-1"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });
        connections.DraftWorkflows.Add("SubmitExpense");
        connections.RefusedWorkflows.Add("SubmitExpense: premium licence required");
        var states = ManagedAgentFixtures.WorkflowStates();

        var result = await Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.True(result.ConnectionsReady);
        Assert.False(result.WorkflowsReady);
        Assert.Equal(WorkflowStateCache.Draft, states.Read(connections.Directories[0], "env-1")["wf-1"]);
    }

    [Fact]
    public async Task Update_RecordingOneEnvironment_LeavesAnotherIntact()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", CanEnable = true });
        connections.ActivatedWorkflows.Add("SubmitExpense");
        var states = ManagedAgentFixtures.WorkflowStates();

        await Create(store, connections, workflowStates: states).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        states.Write(connections.Directories[0], "env-other", new Dictionary<string, int> { ["wf-1"] = WorkflowStateCache.Draft });

        Assert.Equal(WorkflowStateCache.Activated, states.Read(connections.Directories[0], "env-1")["wf-1"]);
        Assert.Equal(WorkflowStateCache.Draft, states.Read(connections.Directories[0], "env-other")["wf-1"]);
    }

    [Fact]
    public async Task Update_TargetsTheAgentsWorkspace()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine("Agent One", AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Agent One\"\nschemaName: \"con_one\"\n");
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Agent One" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(lastDeployedTime: ManagedAgentFixtures.Now));
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.All(connections.Directories, directory => Assert.Equal(Path.Combine(workspace.Root, "Agent One"), directory));
    }

    [Fact]
    public async Task Update_InAFlatProject_TargetsTheProjectRoot()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.All(connections.Directories, directory => Assert.Equal(workspace.Root, directory));
    }

    [Fact]
    public async Task Update_ReadsTheSchemaNameFromTheAgentsWorkspace()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine("Agent One", AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Agent One\"\nschemaName: \"con_nested\"\n");
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Agent One" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(lastDeployedTime: ManagedAgentFixtures.Now));
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };

        await Create(store, new FakeConnectionPort(), catalog).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["con_nested"], catalog.ResolvedValues);
    }

    [Fact]
    public async Task Update_BindsAReferenceWithOneConnectedCandidate()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(1, connections.BindCalls);
        Assert.Equal(["cr_office"], connections.Bound.Select(binding => binding.LogicalName));
    }

    [Fact]
    public async Task Update_WithNothingToBind_NeverWrites()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(0, connections.BindCalls);
        Assert.NotNull(result.Connections[0].ConsentLink);
    }

    [Fact]
    public async Task Update_UsesTheSchemaNameOverrideForDiscovery()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        await Create(store, connections, catalog).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", SchemaName = "cr123_override" },
            CancellationToken.None);

        Assert.Contains("cr123_override", catalog.ResolvedValues);
    }

    [Fact]
    public async Task Update_WithoutASchemaNameOverride_UsesTheProjectSchema()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        await Create(store, connections, catalog).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.DoesNotContain("cr123_override", catalog.ResolvedValues);
    }

    [Fact]
    public async Task Update_ResolvesTheAgentByName()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", null, ManagedAgentFixtures.Candidate("conn-1")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentName = "Contoso Agent", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("agent-1", result.AgentId);
    }

    [Fact]
    public async Task Update_OnANeverDeployedSlotWhoseAgentIsNotInTheCloud_Fails()
    {
        var (workspace, store) = Deployed(deployed: false);
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, connections, new FakeCopilotCatalogPort { ResolveReturns = null }).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("not-deployed-yet", failure.ErrorKind);
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Update_OnANeverDeployedSlotWhoseAgentExists_BindsAnyway()
    {
        var (workspace, store) = Deployed(deployed: false);
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeConnectionPort()).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("connections-updated", result.Status);
    }

    [Fact]
    public async Task Update_WhenTheDeployedAgentIsGone_Fails()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeConnectionPort(), new FakeCopilotCatalogPort { ResolveReturns = null })
            .UpdateAsync(new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" }, CancellationToken.None));

        Assert.Equal("target-agent-missing", failure.ErrorKind);
    }

    [Fact]
    public async Task Update_WithoutADeploymentSelector_IsAUsageError()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(store, new FakeConnectionPort())
            .UpdateAsync(new AgentDeploymentConnectionRequest { AgentId = "agent-1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Update_InteractivelyCreatesAMissingConnectionAndBindsIt()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-new", "cr_http", "user@contoso.com", null));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(["shared_http"], consentFlow.RequestedConnectors);
        Assert.Equal(["conn-new"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.Equal("created", result.Connections[0].State);
    }

    [Fact]
    public async Task Update_UnderNonInteractive_NeverReachesTheConsentFlow()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http"));

        var result = await Create(store, connections, consentFlow: new ScriptedConsentFlow { Throws = true }).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("no-candidate", result.Connections[0].State);
        Assert.NotNull(result.Connections[0].ConsentLink);
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Update_TheConsentLinkInTheResultIsAMakerPortalLink()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        var result = await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var link = Assert.Single(result.Connections).ConsentLink;

        Assert.Contains("/connections/available?apiName=shared_teams", link, StringComparison.Ordinal);
        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_NonInteractive_AddsNoDiagnosticForWhatItCouldNotResolve()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_http", "shared_http", null));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains("--non-interactive", StringComparison.Ordinal));
    }


    [Fact]
    public async Task Update_NonInteractive_LeavesAnAmbiguousReferenceUnboundAndSilent()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(ConnectionReadinessPlanner.Bound, Assert.Single(result.Connections).State);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, connections.BindCalls);
    }

    [Fact]
    public async Task Update_WhenOneConsentFlowFails_StillProcessesTheRest()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_first", "shared_first"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_second", "shared_second"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Connectors.Enqueue([1]);
        prompt.Connectors.Enqueue([2]);
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.CreateNew));
        var consentFlow = new ScriptedConsentFlow();
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, "Timed out."));
        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-second", null, null, null));

        var result = await Create(store, connections, consentFlow: consentFlow, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["shared_first", "shared_second"], consentFlow.RequestedConnectors);
        Assert.Equal(["conn-second"], connections.Bound.Select(binding => binding.ConnectionId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("cr_first", StringComparison.Ordinal));
        Assert.False(result.Ready);
    }

    [Fact]
    public async Task Update_NeverOpensConsentForAnAmbiguousReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1"), ManagedAgentFixtures.Candidate("conn-2")));
        var consentFlow = new ScriptedConsentFlow();

        await Create(store, connections, consentFlow: consentFlow).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(consentFlow.RequestedConnectors);
    }









    [Fact]
    public async Task Update_WhenTheConnectionsCouldNotBeListed_PrintsNothingAfterTheTable()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();

        foreach (var (reference, connector) in new[] { ("cr_teams", "shared_teams"), ("cr_office", "shared_office365users"), ("cr_weather", "shared_msnweather") })
        {
            connections.Views.Add(ManagedAgentFixtures.Reference(reference, connector) with { CatalogUnavailable = true });
        }

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Update_AnUndeclaredReference_PrintsNothingAfterTheTable()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with { IsDeclared = false });

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Update_WhenNoConnectionIsTheOperatorsOwn_StillSaysWhatToPass()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1", owner: "someone.else@contoso.com"), ManagedAgentFixtures.Candidate("conn-2", owner: "someone.else@contoso.com")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Contains("--connection-id", diagnostic, StringComparison.Ordinal);
        Assert.Contains("conn-1", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_PrintsNothingAfterTheTable()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with { CatalogUnavailable = true });
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_stale", "shared_sql") with { IsDeclared = false });

        var result = await Reader(store, connections).ShowAsync(
            new AgentDeploymentConnectionShowRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Update_WhenTheCatalogCannotBeRead_StillDoesNotBindBlind()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365") with { CatalogUnavailable = true });

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Update_ReportsNoWorkflows()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-existing"));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Empty(result.ActivatedWorkflows);
    }

    [Fact]
    public async Task Update_WhenEveryReferenceIsBound_ActivatesTheDraftWorkflows()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-existing"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-escalate", DisplayName = "Escalate", CanEnable = true });
        connections.ActivatedWorkflows.Add("Escalate");

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(1, connections.ActivateCalls);
        Assert.Equal(["Escalate"], result.ActivatedWorkflows);
    }

    [Fact]
    public async Task Update_ScopedToOneConnector_NeverReportsAnUnselectedReferenceAsBound()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-sql")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-teams")));

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_sql", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(["cr_sql"], connections.Bound.Select(binding => binding.LogicalName));
        Assert.Equal(ConnectionReadinessPlanner.Unbound, Assert.Single(result.Connections, item => item.ConnectionReference == "cr_teams").State);
        Assert.False(result.ConnectionsReady);
    }

    [Fact]
    public async Task Update_ScopedToOneConnector_NeverEnablesAWorkflowNeedingAnUnselectedReference()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-sql")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-teams")));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "NeedsBoth", ConnectionReferences = ["cr_sql", "cr_teams"] });

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", Connector = "shared_sql", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, connections.ActivateCalls);
    }

    [Fact]
    public async Task Update_StartsAWorkflowAsSoonAsItsLastReferenceIsBound()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-sql")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-sql", DisplayName = "SqlOnly", ConnectionReferences = ["cr_sql"] });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-both", DisplayName = "NeedsBoth", ConnectionReferences = ["cr_sql", "cr_teams"] });
        var prompt = new ScriptedSelectionPrompt();
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["wf-sql"], connections.StateChanges.Select(change => change.WorkflowId));
        Assert.Contains("Enabling 1 workflow…", prompt.Reported);
    }

    [Fact]
    public async Task Update_ResolvingASecondConnector_NeverRewritesTheFirstBinding()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-sql")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams"));
        var prompt = new ScriptedSelectionPrompt();
        prompt.Connectors.Enqueue([1]);
        prompt.Choices.Enqueue(new ConnectionSelection(ConnectionSelectionKind.Back));

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(["cr_sql"], connections.Bound.Select(binding => binding.LogicalName));
    }

    [Fact]
    public async Task Update_WhileItWorks_SaysWhatItIsDoing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "SubmitExpense", ConnectionReferences = ["cr_sql"] });
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(["Binding 1 connection reference…", "Enabling 1 workflow…"], prompt.Reported);
    }

    [Fact]
    public async Task Update_WithNothingToDo_SaysNothing()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_sql", "shared_sql", "conn-existing"));
        var prompt = new ScriptedSelectionPrompt();

        await Create(store, connections, prompt: prompt).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Empty(prompt.Reported);
    }

    [Fact]
    public async Task Update_AWorkflowWhoseOtherReferenceIsStillUnbound_IsNeverAskedAbout()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_sql", null, ManagedAgentFixtures.Candidate("conn-1")));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_two", "shared_teams"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "NeedsBoth", ConnectionReferences = ["cr_one", "cr_two"] });

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("cr_one", Assert.Single(connections.Bound).LogicalName);
        Assert.Equal(0, connections.ActivateCalls);
    }

    [Fact]
    public async Task Update_AWorkflowWhoseLastReferenceThisRunBinds_IsActivatedWithoutRereading()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_one", "shared_sql", "conn-existing"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_two", "shared_teams", null, ManagedAgentFixtures.Candidate("conn-2")));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "NeedsBoth", ConnectionReferences = ["cr_one", "cr_two"] });
        connections.ActivatedWorkflows.Add("NeedsBoth");

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(1, connections.ActivateCalls);
        Assert.Equal("wf-1", Assert.Single(connections.StateChanges).WorkflowId);
        Assert.Equal(1, connections.ReadCalls);
        Assert.True(result.WorkflowsReady);
    }

    [Fact]
    public async Task Update_WhenEveryWorkflowIsAlreadyRunning_NeverAsksTheService()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-existing"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Escalate", CanEnable = true, IsActivatedLocally = true });

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, connections.ActivateCalls);
        Assert.True(result.WorkflowsReady);
    }

    [Fact]
    public async Task Update_AWorkflowThatCannotBeEnabled_IsNeverAskedAbout()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", boundConnectionId: null));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Blocked", ConnectionReferences = ["cr_office"] });

        await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, connections.ActivateCalls);
    }

    [Fact]
    public async Task Update_WhileAReferenceIsUnbound_StillActivatesTheWorkflowsThatCan()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", boundConnectionId: null));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Independent", CanEnable = true });

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev", NonInteractive = true },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.False(result.ConnectionsReady);
        Assert.Equal(1, connections.ActivateCalls);
    }

    [Fact]
    public async Task Update_WhenAWorkflowStaysDisabled_IsNotReportedAsReady()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-existing"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Escalate", CanEnable = true });
        connections.RefusedWorkflows.Add("Workflow 'Escalate' is still disabled after activation.");

        var result = await Create(store, connections).UpdateAsync(
            new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Contains(result.RefusedWorkflows, refusal => refusal.Contains("still disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains("still disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_WhenActivationFails_IsNotReportedAsReady()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var connections = new FakeConnectionPort { ActivateThrows = new InvalidOperationException("the flow could not be started") };
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_office", "shared_office365", "conn-existing"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "wf-1", DisplayName = "Escalate", CanEnable = true });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, connections).UpdateAsync(
                new AgentDeploymentConnectionRequest { AgentId = "agent-1", DeploymentName = "dev" },
                CancellationToken.None));

        Assert.Equal("workflow-activation-failed", failure.ErrorKind);
    }
}







