using System.Reflection;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DataverseUserAgentTests
{
    private static string Resolve()
    {
        var method = typeof(ServiceCollectionExtensions).GetMethod("DataverseUserAgent", BindingFlags.NonPublic | BindingFlags.Static);

        return (string)method!.Invoke(null, null)!;
    }

    [Fact]
    public void TheUserAgentNamesTheCliAndItsVersion()
    {
        var userAgent = Resolve();

        Assert.StartsWith("AgentHost.Cli-", userAgent, StringComparison.Ordinal);
        Assert.NotEqual("AgentHost.Cli-", userAgent);
    }

    [Fact]
    public void TheUserAgentCarriesNoBuildMetadata() =>
        Assert.DoesNotContain('+', Resolve());

    [Fact]
    public void TheUserAgentIsAValidHeaderToken()
    {
        var userAgent = Resolve();

        Assert.DoesNotContain(' ', userAgent);
        Assert.DoesNotContain('/', userAgent);
    }
}
