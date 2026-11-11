using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentResolverTests
{
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
