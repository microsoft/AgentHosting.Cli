using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ConsoleProgressPolicyTests
{
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(false, "false", true)]
    [InlineData(false, "FALSE", true)]
    [InlineData(false, "0", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "false", false)]
    [InlineData(true, "true", false)]
    [InlineData(false, "true", false)]
    [InlineData(false, "TRUE", false)]
    [InlineData(false, "1", false)]
    [InlineData(false, "yes", false)]
    public void ShouldShowProgress_OnlyOnAnInteractiveTerminalOutsideContinuousIntegration(bool errorRedirected, string? continuousIntegration, bool expected) =>
        Assert.Equal(expected, ConsoleProgressPolicy.ShouldShowProgress(errorRedirected, continuousIntegration));
}
