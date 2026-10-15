using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataverseRequestTimeoutTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static AgentHostDataverseHttpClientAccessor Create()
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new ScriptedHttpMessageHandler());
        accessor.SetDataverseOrganization(new Uri(Organization));

        return accessor;
    }

    [Fact]
    public void AClientCarriesTheStatedRequestTimeout()
    {
        using var accessor = Create();
        using var client = accessor.CreateClient();

        Assert.Equal(AgentHostDataverseHttpClientAccessor.RequestTimeout, client.Timeout);
    }

    [Fact]
    public void AClientDoesNotInheritTheHundredSecondDefault()
    {
        using var accessor = Create();
        using var client = accessor.CreateClient();

        Assert.True(client.Timeout > TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void TheTimeoutMatchesThePowerPlatformServiceCallConvention()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), AgentHostDataverseHttpClientAccessor.RequestTimeout);
    }

    [Fact]
    public void EveryClientTheAccessorCreatesCarriesTheTimeout()
    {
        using var accessor = Create();
        using var first = accessor.CreateClient();
        using var second = accessor.CreateClient();

        Assert.Equal(AgentHostDataverseHttpClientAccessor.RequestTimeout, first.Timeout);
        Assert.Equal(AgentHostDataverseHttpClientAccessor.RequestTimeout, second.Timeout);
    }
}
