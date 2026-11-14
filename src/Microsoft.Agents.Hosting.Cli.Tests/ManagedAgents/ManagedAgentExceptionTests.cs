using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentExceptionTests
{
    [Fact]
    public void DescribeChain_AppendsAnInnerMessageTheOuterDoesNotCarry()
    {
        var failure = new InvalidOperationException("the deploy stopped", new HttpRequestException("the service is unavailable"));

        Assert.Equal("InvalidOperationException: the deploy stopped -> HttpRequestException: the service is unavailable", ManagedAgentException.DescribeChain(failure));
    }

    [Fact]
    public void DescribeChain_LeavesOutAnInnerMessageTheOuterAlreadyCarries()
    {
        var failure = CopilotStudioException.BootstrapReattachFailed("agent-1", "cr1_copy", new InvalidOperationException("the connector was renamed"));

        Assert.Equal(failure.Message, ManagedAgentException.DescribeChain(failure));
    }
}
