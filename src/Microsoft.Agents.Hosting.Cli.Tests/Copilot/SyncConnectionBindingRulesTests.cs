using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncConnectionBindingRulesTests
{
    private static readonly SyncConnectionBindingRules Rules = new();

    [Fact]
    public void EveryStateTheLibraryDecidesHasItsOwnCliLabel()
    {
        var connected = ManagedAgentFixtures.Candidate("conn-1");

        string[] states =
        [
            Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a", "conn-1", connected), ManagedAgentFixtures.Principal).State,
            Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a", null, connected), ManagedAgentFixtures.Principal).State,
            Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a", null, ManagedAgentFixtures.Candidate("conn-1", "Error")), ManagedAgentFixtures.Principal).State,
            Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a", null, connected), null).State,
            Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a") with { IsDeclared = false }, ManagedAgentFixtures.Principal).State,
        ];

        Assert.Equal([ConnectionReadinessPlanner.AlreadyBound, ConnectionReadinessPlanner.Bound, ConnectionReadinessPlanner.NoCandidate, ConnectionReadinessPlanner.Unbound, ConnectionReadinessPlanner.Undeclared], states);
        Assert.Equal(states.Length, Enum.GetValues<ConnectionBindingState>().Length);
    }

    [Theory]
    [InlineData("conn-1", true, false, ConnectionReadinessPlanner.AlreadyBound)]
    [InlineData("conn-1", true, true, ConnectionReadinessPlanner.NoCandidate)]
    [InlineData("conn-1", false, false, ConnectionReadinessPlanner.NoCandidate)]
    [InlineData(" ", true, false, ConnectionReadinessPlanner.NoCandidate)]
    public void ARecordedBindingCountsOnlyWhenTheCatalogConfirmsIt(string recorded, bool exists, bool catalogUnavailable, string expected)
    {
        var item = Rules.Observe(ManagedAgentFixtures.Reference("cr_a", "shared_a", recorded) with { BoundConnectionExists = exists, CatalogUnavailable = catalogUnavailable });

        Assert.Equal(expected, item.State);
        Assert.Equal(expected == ConnectionReadinessPlanner.AlreadyBound ? recorded : null, item.ConnectionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithNobodySignedIn_NothingIsBoundWithoutAsking(string? principal)
    {
        var item = Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a", null, ManagedAgentFixtures.Candidate("conn-1")), principal);

        Assert.Equal(ConnectionReadinessPlanner.Unbound, item.State);
        Assert.Null(item.ConnectionId);
    }

    [Fact]
    public void ConnectedCandidatesComeBackInOrdinalIdOrder_AsTheCallersOwnInstances()
    {
        var lower = ManagedAgentFixtures.Candidate("conn-a");
        var upper = ManagedAgentFixtures.Candidate("Conn-b", "connected");
        var broken = ManagedAgentFixtures.Candidate("conn-0", "Disconnected");

        var item = Rules.Plan(ManagedAgentFixtures.Reference("cr_a", "shared_a", null, lower, broken, upper), "nobody@contoso.com");

        Assert.Collection(Rules.Connected([lower, broken, upper]), candidate => Assert.Same(upper, candidate), candidate => Assert.Same(lower, candidate));
        Assert.Collection(item.Candidates, candidate => Assert.Same(upper, candidate), candidate => Assert.Same(lower, candidate));
    }

    [Fact]
    public void AnAlreadyBoundReferenceStillCarriesTheCandidatesItCouldBeReboundTo()
    {
        var bound = ManagedAgentFixtures.Candidate("conn-b");
        var other = ManagedAgentFixtures.Candidate("conn-a");

        var item = Rules.Observe(ManagedAgentFixtures.Reference("cr_a", "shared_a", "conn-b", bound, ManagedAgentFixtures.Candidate("conn-0", "Error"), other));

        Assert.Equal(ConnectionReadinessPlanner.AlreadyBound, item.State);
        Assert.Empty(item.Candidates);
        Assert.Collection(item.ConnectedCandidates, candidate => Assert.Same(other, candidate), candidate => Assert.Same(bound, candidate));
        Assert.Same(other, ConnectionReadinessPlanner.Select(item, "CONN-A").Candidates.Single());
    }
}
