using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionCommandHintsTests
{
    [Fact]
    public void ListConnections_SelectsTheAgentByIdentifierWhateverItsNameHolds()
    {
        const string project = @"C:\work\Sales Agents";

        var command = ConnectionCommandHints.ListConnections(new AgentDeploymentConnectionShowRequest { Project = project, AgentName = "Sales \"Bot\" $5 `x`", DeploymentName = "dev" }, "agent-1");

        Assert.Equal($"msagent deployment connection list --project {CommandLineArgument.Quote(project)} --agent-id agent-1 --deployment-name dev", command);
    }

    [Fact]
    public void ListConnectors_InAnEnvironment_AddressesTheEnvironmentInsteadOfTheDeployment()
    {
        var command = ConnectionCommandHints.ListConnectors(new AgentDeploymentConnectionShowRequest { AgentName = "Sales Bot", DeploymentName = "dev" }, "agent-1", "env one");

        Assert.Equal($"msagent deployment connection connectors --environment-id {CommandLineArgument.Quote("env one")} --filter <text>", command);
    }

    [Fact]
    public void DeleteConnection_InAnEnvironment_NamesTheConnectorTheDeleteAddressNeeds()
    {
        var command = ConnectionCommandHints.DeleteConnection("env-1", "shared_eventhubs", "conn-1");

        Assert.Equal("msagent deployment connection remove --environment-id env-1 --connector shared_eventhubs --connection-id conn-1", command);
    }

    [Fact]
    public void DeleteConnection_QuotesAnEnvironmentCarryingASpace()
    {
        Assert.Contains($"--environment-id {CommandLineArgument.Quote("env one")}", ConnectionCommandHints.DeleteConnection("env one", "shared_sql", "conn-1"), StringComparison.Ordinal);
    }

    [Fact]
    public void BindConnector_WithoutARequest_AsksForTheDeploymentItCannotKnow()
    {
        var command = ConnectionCommandHints.BindConnector("shared_eventhubs", "<id>");

        Assert.Equal("msagent deployment connection update --deployment-name <name> --connector shared_eventhubs --connection-id <id>", command);
    }

    [Theory]
    [InlineData("--connection-only")]
    [InlineData("connection add")]
    public void BindConnector_NeverSuggestsAShapeTheCommandRefuses(string refused)
    {
        Assert.DoesNotContain(refused, ConnectionCommandHints.BindConnector("shared_eventhubs", "<id>"), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteConnection_NeverOmitsTheConnector()
    {
        Assert.Contains("--connector ", ConnectionCommandHints.DeleteConnection("env-1", "shared_sql", "conn-1"), StringComparison.Ordinal);
    }
}
