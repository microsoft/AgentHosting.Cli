using Microsoft.Agents.Hosting.Cli.Core;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Assert the CLI identity without initializing authentication or Azure clients. Version and help
/// rendering are owned by System.CommandLine and exercised in <see cref="CommandHostTests"/>.
/// </summary>
public class FoundationTests
{
    [Fact]
    public void CommandName_IsMsagent()
    {
        Assert.Equal("msagent", AgentHostingCliInfo.CommandName);
    }

    [Fact]
    public void ProductName_IsMicrosoftAgentHostingCli()
    {
        Assert.Equal("Microsoft AgentHosting CLI", AgentHostingCliInfo.ProductName);
    }
}
