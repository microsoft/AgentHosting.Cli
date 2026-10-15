using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

/// <summary>
/// The sharing scenarios address the agent by its local record id, which is neither its display name
/// nor the cloud agent's id. These pin the selector they are built from, and the prerequisites each
/// scenario declares, because both are invisible until a live run reaches them.
/// </summary>
public sealed class ShareScenarioTests
{
    private static Scenario Scenario(string id) =>
        McsShareScenarios.All().SingleOrDefault(scenario => scenario.Id == id)
            ?? throw new InvalidOperationException($"{id} is not a sharing scenario.");

    [Fact]
    public void TheSessionAgentIsAddressedByItsRecordIdRatherThanItsNameOrItsCloudId()
    {
        var session = new SessionAgent(
            SessionAgentKind.ScaffoldedCli,
            new Workspace { ScenarioId = "s", Root = "r", ProjectDirectory = "p", AgentName = "ahe2e CLI sCli-1234", SchemaName = "cr123_ahe2e" },
            new ManagedAgentDeployResult
            {
                AgentId = "11111111-1111-1111-1111-111111111111",
                DisplayName = "ahe2e CLI sCli-1234",
                DeploymentId = "d",
                DeploymentName = "e2e",
                DeploymentType = "dev",
                Action = "push",
                TargetEnvironmentId = "22222222-2222-2222-2222-222222222222",
                TargetMcsAgentId = "33333333-3333-3333-3333-333333333333",
                SchemaName = "cr123_ahe2e",
            },
            "e2e");

        Assert.Equal("11111111-1111-1111-1111-111111111111", session.AgentId);
        Assert.NotEqual(session.Workspace.AgentName, session.AgentId);
        Assert.NotEqual(session.CloudAgentId, session.AgentId);
    }

    [Theory]
    [InlineData("MA-73")]
    [InlineData("MA-74")]
    [InlineData("MA-84")]
    public void ACaseTheCommandLineRejectsOnItsOwnNeedsNoEnvironment(string id) => Assert.Empty(Scenario(id).Requires);

    [Theory]
    [InlineData("MA-75")]
    [InlineData("MA-76")]
    [InlineData("MA-77")]
    [InlineData("MA-78")]
    [InlineData("MA-79")]
    [InlineData("MA-83")]
    [InlineData("MA-85")]
    public void AScenarioThatTouchesSomebodysAccessRequiresTheConfiguredShareUser(string id) =>
        Assert.Contains(Capability.McsShareUser, Scenario(id).Requires);

    [Theory]
    [InlineData("MA-70")]
    [InlineData("MA-71")]
    [InlineData("MA-72")]
    [InlineData("MA-80")]
    [InlineData("MA-81")]
    [InlineData("MA-82")]
    public void AScenarioThatOnlyReadsOrTogglesOrgWideAccessDoesNotNeedAShareUser(string id) =>
        Assert.DoesNotContain(Capability.McsShareUser, Scenario(id).Requires);

    [Fact]
    public void EveryGrantingScenarioIsReversibleRatherThanReadOnly() =>
        Assert.All(
            McsShareScenarios.All().Where(scenario => scenario.Requires.Contains(Capability.McsShareUser) && scenario.Id != "MA-79"),
            scenario => Assert.Equal(SafetyTier.Reversible, scenario.Tier));

    [Fact]
    public void AnalyticsOnlyAccessIsExercised() => Assert.Contains("view", Scenario("MA-85").Title, StringComparison.Ordinal);

    [Fact]
    public void EverySharingScenarioIsScheduledWithTheAgentSuite()
    {
        var scheduled = McsAgentScenarios.All().Select(scenario => scenario.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(McsShareScenarios.All(), scenario => Assert.Contains(scenario.Id, scheduled));
    }

    [Fact]
    public void EverySharingScenarioHasItsOwnId()
    {
        var ids = McsShareScenarios.All().Select(scenario => scenario.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
