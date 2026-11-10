using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentResultTextTests
{
    private const string AgentId = "c6796048-5c47-419e-a78d-d336919838a8";

    [Fact]
    public void DeploymentListText_NamesTheDeploymentIdTheSelectorsNeed()
    {
        var result = new AgentDeploymentListResult
        {
            AgentId = AgentId,
            Slots =
            [
                new AgentDeploymentListItem
                {
                    DeploymentId = "a49141fd-4f6a-4c50-9f6a-ab61f5a26aea",
                    DeploymentName = "dev",
                    DeploymentType = "dev",
                    EnvironmentId = "env-1",
                },
            ],
        };

        var lines = result.ToTextLines();

        Assert.Contains(lines, line => line.Contains("DEPLOYMENT ID", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("a49141fd-4f6a-4c50-9f6a-ab61f5a26aea", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("AGENT ID", StringComparison.Ordinal));
    }

    [Fact]
    public void DeploymentListText_WithoutAnAgentScope_AddsTheAgentColumns()
    {
        var result = new AgentDeploymentListResult
        {
            Slots =
            [
                new AgentDeploymentListItem
                {
                    DeploymentId = "a49141fd-4f6a-4c50-9f6a-ab61f5a26aea",
                    DeploymentName = "dev",
                    DeploymentType = "dev",
                    EnvironmentId = "env-1",
                    AgentId = AgentId,
                    AgentName = "Repro Bot",
                },
            ],
        };

        var lines = result.ToTextLines();

        Assert.Contains("1 deployment(s) across 1 agent(s).", lines);
        Assert.Contains(lines, line => line.Contains("AGENT", StringComparison.Ordinal) && line.Contains("AGENT ID", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Repro Bot", StringComparison.Ordinal) && line.Contains(AgentId, StringComparison.Ordinal));
    }

    [Fact]
    public void DeploymentListText_WithNoDeploymentsAndNoScope_SaysTheProjectHasNone()
    {
        Assert.Equal(["This project has no deployments."], new AgentDeploymentListResult().ToTextLines());
    }

    private static ManagedAgentCreateResult CreateResult(string createdFrom) => new()
    {
        AgentId = AgentId,
        DisplayName = "NB1",
        AgentType = "MCSAgent",
        SchemaName = "crf9a_nb1_sEulfr",
        McsAgentId = "b516419b-6db7-4fe4-878f-0f96162a7b90",
        TenantId = "tenant-1",
        EnvironmentId = "fa5c1b48-9fac-e55e-a7f2-f6d063859d08",
        ProjectDirectory = @"C:\TempMCS\ahmcs4",
        ConfigPath = @"C:\TempMCS\ahmcs4\.config\agent.config.json",
        CreatedFrom = createdFrom,
        DeploymentName = "dev",
        DeploymentType = "dev",
        ClonedComponentCount = 13,
    };

    private static ManagedAgentInitResult InitResult() => new()
    {
        AgentId = AgentId,
        DisplayName = "NB1",
        AgentType = "MCSAgent",
        TenantId = "tenant-1",
        EnvironmentId = "fa5c1b48-9fac-e55e-a7f2-f6d063859d08",
        ProjectDirectory = @"C:\TempMCS\ahmcs4",
        ConfigPath = @"C:\TempMCS\ahmcs4\.config\agent.config.json",
    };

    [Fact]
    public void CloneText_CarriesTheManagedAgentId() =>
        Assert.Contains($"  agent id    {AgentId}", CreateResult("clone").ToTextLines());

    [Theory]
    [InlineData("agent-created", true)]
    [InlineData("joined", false)]
    public void CloneText_CountsTheComponentsTheCloneWrote_ButAJoinWroteNone(string status, bool counted) =>
        Assert.Equal(counted, (CreateResult("clone") with { Status = status }).ToTextLines().Contains("  components  13 written"));

    [Fact]
    public void ScaffoldText_CarriesTheManagedAgentId() =>
        Assert.Contains($"  agent id  {AgentId}", CreateResult("scaffold").ToTextLines());

    [Fact]
    public void InitText_CarriesTheManagedAgentId() =>
        Assert.Contains($"  agent id  {AgentId}", InitResult().ToTextLines());

    [Fact]
    public void InitText_DoesNotRepeatTheAgentIdInTheHeader() =>
        Assert.Equal($"Registered NB1 in {@"C:\TempMCS\ahmcs4"}.", InitResult().ToTextLines()[0]);

    [Fact]
    public void CloneText_ReportsTheAgentIdBeforeTheProject()
    {
        var lines = CreateResult("clone").ToTextLines();

        Assert.True(lines.ToList().FindIndex(line => line.Contains("agent id", StringComparison.Ordinal))
            < lines.ToList().FindIndex(line => line.Contains("project", StringComparison.Ordinal)));
    }
}
