using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentDeleteTargetsTests
{
    private static readonly AgentDeploymentRecord[] Deployments =
    [
        ManagedAgentFixtures.Deployment("slot-1", name: "dev", environmentId: "env-1"),
        ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"),
        ManagedAgentFixtures.Deployment("slot-3", name: "test-again", stage: DeploymentStages.Test, environmentId: "ENV-TEST"),
        ManagedAgentFixtures.Deployment("slot-4", name: "prod", stage: DeploymentStages.Prod, environmentId: "env-prod"),
    ];

    private static IReadOnlyList<string> Orphaned(string agentEnvironmentId, string? deletedEnvironmentId, bool includeHome) => [.. ManagedAgentDeleteTargets.OrphanedBy(ManagedAgentFixtures.Agent(environmentId: agentEnvironmentId), Deployments, deletedEnvironmentId, includeHome).Select(orphan => orphan.ToString())];

    [Fact]
    public void OrphanedBy_ListsEveryOtherEnvironmentOnceInPromotionOrderWithTheHomeLast()
    {
        Assert.Equal(["env-test (deployment \"test\")", "env-prod (deployment \"prod\")", "env-home (the agent's home environment, which no deployment names)"], Orphaned("env-home", "ENV-1", includeHome: true));
    }

    [Fact]
    public void OrphanedBy_WithoutTheHome_ListsOnlyWhatTheDeploymentsName()
    {
        Assert.Equal(["env-test (deployment \"test\")", "env-prod (deployment \"prod\")"], Orphaned("env-home", "env-1", includeHome: false));
    }

    [Fact]
    public void OrphanedBy_NeverListsTheEnvironmentTheDeleteRemovesTheCopyFrom()
    {
        Assert.Equal(["env-1 (deployment \"dev\")", "env-test (deployment \"test\")", "env-prod (deployment \"prod\")"], Orphaned("env-home", "env-home", includeHome: true));
    }

    [Fact]
    public void OrphanedBy_WhenNothingIsDeleted_ListsEveryEnvironment()
    {
        Assert.Equal(["env-1 (deployment \"dev\")", "env-test (deployment \"test\")", "env-prod (deployment \"prod\")"], Orphaned("env-1", null, includeHome: false));
    }

    [Fact]
    public void OrphanedBy_OfARecordWithoutAHomeEnvironment_ListsNoHome()
    {
        Assert.Equal(["env-test (deployment \"test\")", "env-prod (deployment \"prod\")"], Orphaned(string.Empty, "env-1", includeHome: true));
    }

    [Fact]
    public void Resources_ListsEachWorkspaceResourceOnceInDeleteOrder()
    {
        CopilotCopyLink[] listed =
        [
            new(CopilotCopyIdentityKinds.Connector, "c0c0c0c0-1111-2222-3333-444444444444"),
            new(CopilotCopyIdentityKinds.ConnectionReference, "con_agent.cr.shared_teams"),
            new(CopilotCopyIdentityKinds.Workflow, "b0b0b0b0-1111-2222-3333-444444444444"),
            new(CopilotCopyIdentityKinds.ConnectionReference, "CON_AGENT.CR.SHARED_TEAMS"),
            new(CopilotCopyIdentityKinds.AIModel, "b0b0b0b0-1111-2222-3333-444444444444"),
            new(CopilotCopyIdentityKinds.Workflow, "a0a0a0a0-1111-2222-3333-444444444444"),
        ];

        Assert.Equal([listed[4], listed[2], listed[5], listed[1], listed[0]], ManagedAgentDeleteTargets.Resources(listed));
    }
}
