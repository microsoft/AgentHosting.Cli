using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsReadStepsTests : IDisposable
{
    private readonly AmsHarness _ams = new();

    public AmsReadStepsTests() => _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment()));

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task Show_AddsEachRegisteredAgentsRepositoryFolderAndDeploymentStates_FromOneRead()
    {
        _ams.AddAgent();
        _ams.AddDeployment();

        var result = await Steps(new FakeOperations { OnManagedAgentShow = _ => Shown() }).HandleAsync(new ManagedAgentShowRequest(), default);

        var details = Assert.Single(result.Ams!);
        Assert.Equal((McsRecords.AgentId, McsRecords.RepositoryId, AmsHarness.AgentFolder, McsRecords.GitUrl), (details.AgentId, details.RepositoryId, details.Folder, details.GitUrl));
        Assert.Equal([new AmsDeploymentState(McsRecords.DevDeploymentId, McsRecords.Commit, "Deployed")], details.Deployments);
        Assert.Equal([$"read-repository {McsRecords.RepositoryId} - with-deployments"], _ams.Service.Calls);
        Assert.Contains($"    gitUrl         {McsRecords.GitUrl}", result.ToTextLines());
        Assert.Contains($"        lastCommit        {McsRecords.Commit}", result.ToTextLines());
        Assert.Null(result.Warnings);
    }

    [Fact]
    public async Task Show_OfOnlyUnregisteredAgents_ReadsNothing_AndShowsADash()
    {
        _ams.AddAgent(registered: false);
        _ams.AddDeployment();

        var result = await Steps(new FakeOperations { OnManagedAgentShow = _ => Shown() }).HandleAsync(new ManagedAgentShowRequest(), default);

        Assert.Empty(result.Ams!);
        Assert.Empty(_ams.Service.Calls);
        Assert.Contains($"        lastCommit        {TextGrid.Dash}", result.ToTextLines());
    }

    [Fact]
    public async Task Show_WhenAmsCannotBeRead_KeepsTheLocalRows_WithAWarning()
    {
        _ams.AddAgent();
        _ams.AddDeployment();
        _ams.Service.RepositoryFailure = McsRecords.Status(503);

        var result = await Steps(new FakeOperations { OnManagedAgentShow = _ => Shown() }).HandleAsync(new ManagedAgentShowRequest(), default);

        Assert.Equal(["AMS couldn't be read: The Agent Management Service answered 503."], result.Warnings);
        Assert.Null(Assert.Single(result.Ams!).GitUrl);
        Assert.Single(result.Agents);
    }

    [Fact]
    public async Task DeploymentListAndShow_AddTheLastDeployedCommitAndState()
    {
        _ams.AddAgent();
        _ams.AddDeployment();
        var operations = new FakeOperations
        {
            OnAgentDeploymentList = _ => new AgentDeploymentListResult { Slots = [new AgentDeploymentListItem { AgentId = McsRecords.AgentId, DeploymentId = McsRecords.DevDeploymentId, DeploymentName = "contoso-dev", DeploymentType = "dev" }, new AgentDeploymentListItem { AgentId = "agent-2", DeploymentId = "slot-2", DeploymentName = "other", DeploymentType = "dev" }] },
            OnShowAgentDeployment = _ => new AgentDeploymentShowResult { AgentId = McsRecords.AgentId, AgentDisplayName = "Contoso HR", DeploymentId = McsRecords.DevDeploymentId, DeploymentName = "contoso-dev", DeploymentType = "dev" },
        };

        var listed = await Steps(operations).HandleAsync(new AgentDeploymentListRequest { Project = _ams.Root }, default);
        var shown = await Steps(operations).HandleAsync(new AgentDeploymentShowRequest { Project = _ams.Root }, default);

        Assert.True(listed.ShowsAms && shown.ShowsAms);
        Assert.Equal([(McsRecords.Commit, "Deployed"), (null, null)], listed.Slots.Select(slot => (slot.LastDeployedCommitSha, slot.State)));
        Assert.Equal((McsRecords.Commit, "Deployed"), (shown.LastDeployedCommitSha, shown.State));
        Assert.Equal(2, _ams.Service.Calls.Count);
    }

    private AmsReadSteps Steps(FakeOperations operations) => new(operations, _ams.Resolver, _ams.Targets, _ams.Service);

    private ManagedAgentShowResult Shown() => new() { ProjectDirectory = _ams.Root, ConfigPath = Path.Combine(_ams.Root, ".config", "agent.config.json"), Agents = [_ams.Agent()], Deployments = _ams.Deployments() };
}
