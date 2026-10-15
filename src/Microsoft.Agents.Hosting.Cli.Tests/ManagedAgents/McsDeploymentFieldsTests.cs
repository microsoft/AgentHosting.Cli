using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class McsDeploymentFieldsTests
{
    private static AgentDeploymentRecord Mcs() => new()
    {
        DeploymentId = "slot-1",
        AgentId = "agent-1",
        AgentType = AgentTypes.McsAgent,
        DeploymentType = DeploymentStages.Dev,
        DeploymentName = "dev",
        TenantId = "tenant-1",
        HomeEnvironmentId = "env-home",
        EnvironmentId = "env-1",
        EntityTag = "1",
    };

    [Fact]
    public void AnMcsDeploymentReadsItsEnvironment()
    {
        Assert.Equal("env-1", Mcs().McsEnvironmentId());
    }

    [Fact]
    public void AnMcsDeploymentReadsItsHomeEnvironment()
    {
        Assert.Equal("env-home", Mcs().McsHomeEnvironmentId());
    }

    [Fact]
    public void AMissingEnvironmentFailsLoudlyRatherThanSilently()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => (Mcs() with { EnvironmentId = null }).McsEnvironmentId());

        Assert.Contains("slot-1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingHomeEnvironmentFailsLoudlyRatherThanSilently()
    {
        Assert.Throws<InvalidOperationException>(() => (Mcs() with { HomeEnvironmentId = null }).McsHomeEnvironmentId());
    }

    [Fact]
    public void ANullRecordIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ((AgentDeploymentRecord)null!).McsEnvironmentId());
    }
}
