using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsResultsTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void AnAgentListIsFilteredByAgentFolderAndType()
    {
        ManagedAgentListItem[] rows = [Row("agent-1", "Folder A", "MCSAgent"), Row("agent-1", "Folder A", "MCSWorkflow")];
        var listed = new ManagedAgentListResult { Scope = "environment", Agents = rows };

        Assert.Equal([rows[1]], AmsMcsResults.Holding(listed, "agent-1", "folder a", "mcsworkflow"));
        Assert.Equal([rows[0]], AmsMcsResults.Holding(listed, "agent-1", agentType: "MCSAgent"));
        Assert.Empty(AmsMcsResults.Holding(listed, "agent-1", "Folder B", "MCSAgent"));
    }

    [Fact]
    public void TheRowsHeldInAProjectAreThoseWhoseFolderItHolds()
    {
        var project = _scratch.Folder("project");
        _scratch.Folder(Path.Combine("project", "Folder A"));
        ManagedAgentListItem[] rows = [Row("agent-1", "Folder A"), Row("agent-2", "Folder B"), Row("agent-3", null), Row("agent-4", string.Empty)];

        Assert.Equal([rows[0]], AmsMcsResults.HeldIn(new ManagedAgentListResult { Scope = "repository", Agents = rows }, project));
    }

    [Fact]
    public void AListIsOfOneTypeWhenEveryRowIs()
    {
        Assert.True(AmsMcsResults.OnlyOfType(List(Row("agent-1", "A", "MCSWorkflow"), Row("agent-2", "B", "mcsworkflow")), "MCSWorkflow"));
        Assert.False(AmsMcsResults.OnlyOfType(List(Row("agent-1", "A", "MCSWorkflow"), Row("agent-2", "B", "MCSAgent")), "MCSWorkflow"));
        Assert.True(AmsMcsResults.OnlyOfType(List(), "MCSWorkflow"));
    }

    [Fact]
    public void IdsAreLowercaseAndOrderedAndARowWithoutOneKeepsItsPlace()
    {
        Assert.Equal([string.Empty, "agent-1", "agent-2"], AmsMcsResults.Ids([Row("AGENT-2", "B"), Row(null, "C"), Row("agent-1", "A")]));
    }

    [Fact]
    public void TheRestoredIdsAreLowercaseOrderedAndNoneWithoutARestore()
    {
        var registration = Registration(new AmsRestoredAgent("AGENT-2", "B", "B"), new AmsRestoredAgent("agent-1", "A", "A"));

        Assert.Equal(["agent-1", "agent-2"], AmsMcsResults.RestoredIds(registration));
        Assert.Empty(AmsMcsResults.RestoredIds(registration with { Restored = null }));
        Assert.Empty(AmsMcsResults.RestoredIds(null));
    }

    [Fact]
    public void ARestoreRestoresExactlyTheAgentsNamedWhateverTheirCaseOrOrder()
    {
        var registration = Registration(new AmsRestoredAgent("AGENT-2", "B", "B"), new AmsRestoredAgent("agent-1", "A", "A"));

        Assert.True(AmsMcsResults.Restores(registration, ["agent-2", "AGENT-1"]));
        Assert.False(AmsMcsResults.Restores(registration, ["agent-1"]));
        Assert.False(AmsMcsResults.Restores(registration, ["agent-1", "agent-2", "agent-3"]));
        Assert.True(AmsMcsResults.Restores(null, []));
    }

    [Fact]
    public void AnAgentListAndBotsAreDescribedRowByRow()
    {
        Assert.Equal("agent-1@Folder A MCSAgent, agent-2@ MCSWorkflow", AmsMcsResults.Describe(List(Row("agent-1", "Folder A"), Row("agent-2", null, "MCSWorkflow"))));
        Assert.Equal("bot-1 'ahe2e Home A', bot-2 ''", AmsMcsResults.Describe([new DataverseBot { BotId = "bot-1", Name = "ahe2e Home A" }, new DataverseBot { BotId = "bot-2" }]));
        Assert.Equal(string.Empty, AmsMcsResults.Describe(Array.Empty<DataverseBot>()));
    }

    [Fact]
    public void NoticesAreFoundByKindAndNameTheirRelatedItems()
    {
        var draft = Notice("FlowLeftInDraft", "new_ahe2ec_msnweather", "other");
        var deployed = Deployed(draft, Notice("WorkspaceLeftAlone"));

        Assert.Equal([draft], AmsMcsResults.Notices(deployed, "flowleftindraft"));
        Assert.Empty(AmsMcsResults.Notices(deployed, "Other"));
        Assert.True(AmsMcsResults.NoticeNames(deployed, "FlowLeftInDraft", "NEW_AHE2EC_MSNWEATHER"));
        Assert.False(AmsMcsResults.NoticeNames(deployed, "FlowLeftInDraft", "missing"));
        Assert.False(AmsMcsResults.NoticeNames(deployed, "WorkspaceLeftAlone", "new_ahe2ec_msnweather"));
        Assert.Equal("FlowLeftInDraft: new_ahe2ec_msnweather,other | WorkspaceLeftAlone: ", AmsMcsResults.DescribeNotices(deployed));
        Assert.Equal(string.Empty, AmsMcsResults.DescribeNotices(Deployed()));
    }

    [Fact]
    public void ConnectionsAreUsableWhenEachIsBoundOrHasACandidate()
    {
        Assert.True(AmsMcsResults.Usable(Connections(Connection("a", "bound", "c-1"), Connection("b", "unbound", candidates: ["c-2"]))));
        Assert.False(AmsMcsResults.Usable(Connections(Connection("a", "bound", "c-1"), Connection("b", "unbound"))));
        Assert.False(AmsMcsResults.Usable(Connections()));
    }

    [Fact]
    public void BoundConnectionsAreTheBoundStatesThatNameAConnection()
    {
        var bound = Connection("a", "bound", "c-1");
        var already = Connection("b", "Already-Bound", "c-2");
        var created = Connection("c", "created", "c-3");
        var listed = Connections(bound, already, created, Connection("d", "bound"), Connection("e", "unbound", "c-5"));

        Assert.Equal([bound, already, created], AmsMcsResults.Bound(listed));
        Assert.False(AmsMcsResults.AllBound(listed));
        Assert.True(AmsMcsResults.AllBound(Connections(bound, created)));
        Assert.False(AmsMcsResults.AllBound(Connections()));
    }

    [Fact]
    public void AListingHoldsAConnectionByItsIdWithoutCase()
    {
        var listed = Connections(Connection("a", "bound", "CONNECTION-1"));

        Assert.True(AmsMcsResults.Holds(listed, "connection-1"));
        Assert.False(AmsMcsResults.Holds(listed, "connection-2"));
        Assert.False(AmsMcsResults.Holds(listed, null));
    }

    [Fact]
    public void TheConsentLinkIsTheFirstOneGiven()
    {
        Assert.Equal("https://consent.example.invalid/2", AmsMcsResults.ConsentLink(Connections(Connection("a", "unbound"), Connection("b", "unbound", consentLink: string.Empty), Connection("c", "unbound", consentLink: "https://consent.example.invalid/2"), Connection("d", "unbound", consentLink: "https://consent.example.invalid/3"))));
        Assert.Null(AmsMcsResults.ConsentLink(Connections(Connection("a", "unbound"))));
    }

    [Fact]
    public void ConnectionsAreDescribedReferenceByReference()
    {
        var listed = Connections(new AgentDeploymentConnectionItem { ConnectionReference = "a", State = "bound", ConnectionId = "c-1", ConnectionOwner = "user@example.invalid", ConnectorStatus = "Connected", Candidates = ["c-1", "c-2"] }, Connection("b", "unbound"));

        Assert.Equal("a=bound c-1 owner=user@example.invalid connector=Connected candidates=c-1;c-2 | b=unbound  owner= connector= candidates=", AmsMcsResults.Describe(listed));
    }

    private static ManagedAgentListItem Row(string? agentId, string? folder, string agentType = "MCSAgent") => new() { AgentId = agentId, AgentFolder = folder, DisplayName = "Agent", TenantId = AmsMcsTestKit.Tenant, EnvironmentId = AmsMcsTestKit.Dev, AgentType = agentType };

    private static ManagedAgentListResult List(params ManagedAgentListItem[] rows) => new() { Scope = "repository", Agents = rows };

    private static AmsAgentRegistration Registration(params AmsRestoredAgent[] restored) => new() { RepositoryId = "repository-1", GitUrl = "https://git.example.invalid/r", Folder = "Folder", Restored = restored };

    private static McsAmsNoticeView Notice(string kind, params string[] relatedItems) => new() { Kind = kind, Message = $"{kind} happened", RelatedItems = relatedItems };

    private static McsAmsAgentDeployResult Deployed(params McsAmsNoticeView[] notices) => new() { Status = "deployed", AgentId = "agent-1", DisplayName = "Agent", AgentType = "MCSWorkflow", DeploymentId = "deployment-1", DeploymentName = "test-ring", DeploymentType = "test", TargetEnvironmentId = AmsMcsTestKit.Test, OperationId = "operation-1", Notices = notices };

    private static AgentDeploymentConnectionItem Connection(string reference, string state, string? connectionId = null, string[]? candidates = null, string? consentLink = null) => new() { ConnectionReference = reference, State = state, ConnectionId = connectionId, Candidates = candidates ?? [], ConsentLink = consentLink };

    private static AgentDeploymentConnectionResult Connections(params AgentDeploymentConnectionItem[] connections) => new() { Status = "connections-listed", AgentId = "agent-1", DeploymentId = "deployment-1", DeploymentName = "test-ring", EnvironmentId = AmsMcsTestKit.Test, Connections = connections };
}
