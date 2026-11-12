using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentResolverTests
{
    [Theory]
    [InlineData(AgentTypes.McsAgent, "Source Agent", true)]
    [InlineData(AgentTypes.McsWorkflow, "Source Flow", true)]
    [InlineData(AgentTypes.McsAgent, "Gone Agent", false)]
    [InlineData(AgentTypes.McsWorkflow, "Gone Flow", false)]
    [InlineData(AgentTypes.McsAgent, "../outside", false)]
    [InlineData(AgentTypes.McsWorkflow, "../outside", false)]
    public void FindWorkspaceDirectory_FindsAnAgentOrWorkflowFolderAndNothingElse(string agentType, string agentLocation, bool found)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var project = Path.Combine(workspace.Root, "project");
        workspace.AddFile(Path.Combine("project", "Source Agent", AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Source Agent\"\nschemaName: \"con_source\"\n");
        workspace.AddFile(Path.Combine("outside", AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Outside Agent\"\nschemaName: \"con_outside\"\n");
        WorkflowFixtures.Write(Path.Combine(project, "Source Flow"), "Source Flow");
        WorkflowFixtures.Write(Path.Combine(workspace.Root, "outside"), "Outside Flow");

        Assert.Equal(found ? Path.Combine(project, agentLocation) : null, ManagedAgentFixtures.AgentResolver(ManagedAgentFixtures.Store()).FindWorkspaceDirectory(project, ManagedAgentFixtures.Agent() with { AgentType = agentType, AgentLocation = agentLocation }));
    }
    [Theory]
    [InlineData("env-1")]
    [InlineData("")]
    public void TargetEnvironmentId_PrefersTheDeploymentsEnvironment(string recordEnvironmentId) =>
        Assert.Equal("env-2", ManagedAgentResolver.TargetEnvironmentId(ManagedAgentFixtures.Agent(environmentId: recordEnvironmentId), ManagedAgentFixtures.Deployment(environmentId: "env-2")));

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", "")]
    public void TargetEnvironmentId_OfARecordWithNoEnvironment_NamesTheRecord(string recordEnvironmentId, string? deploymentEnvironmentId)
    {
        var deployment = deploymentEnvironmentId is null ? null : ManagedAgentFixtures.Deployment(environmentId: deploymentEnvironmentId);

        var failure = Assert.Throws<ManagedAgentException>(() => ManagedAgentResolver.TargetEnvironmentId(ManagedAgentFixtures.Agent(environmentId: recordEnvironmentId), deployment));

        Assert.Equal("agent-environment-missing", failure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Contains("'Contoso Agent' (agent-1)", failure.Message, StringComparison.Ordinal);
    }
}
