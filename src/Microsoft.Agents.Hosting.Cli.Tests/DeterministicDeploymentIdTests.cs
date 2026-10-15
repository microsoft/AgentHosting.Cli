using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeterministicDeploymentIdTests
{
    [Theory]
    [InlineData("2f9e1bf7-8ddb-461a-bbee-04b077950000")]
    [InlineData("2F9E1BF7-8DDB-461A-BBEE-04B077950000")]
    [InlineData("{2f9e1bf7-8ddb-461a-bbee-04b077950000}")]
    [InlineData("2f9e1bf78ddb461abbee04b077950000")]
    public void Compute_EquivalentGuidSpellings_ProduceSameId(string agentId)
    {
        var canonical = DeterministicDeploymentId.Compute("2f9e1bf7-8ddb-461a-bbee-04b077950000", "dev1");
        Assert.Equal(canonical, DeterministicDeploymentId.Compute(agentId, "dev1"));
    }

    [Fact]
    public void Compute_DifferentGuid_ProducesDifferentId()
    {
        var a = DeterministicDeploymentId.Compute("2f9e1bf7-8ddb-461a-bbee-04b077950000", "dev1");
        var b = DeterministicDeploymentId.Compute("11111111-1111-1111-1111-111111111111", "dev1");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_NonGuidAgentId_UsedVerbatim()
    {
        // A non-GUID agent id has no canonical form; it must hash by its trimmed text.
        var a = DeterministicDeploymentId.Compute("agent-one", "dev1");
        var b = DeterministicDeploymentId.Compute("  agent-one  ", "dev1");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("ProdBot")]
    [InlineData("PRODBOT")]
    [InlineData("  prodbot  ")]
    public void Compute_CaseOnlyNameDifferences_ProduceSameId(string name)
    {
        // Azure Bot Service names are case-insensitive, so names differing only in case address the same
        // bot and must resolve to a single deployment id (otherwise two records adopt one bot and
        // deleting either deletes the shared bot).
        var canonical = DeterministicDeploymentId.Compute("2f9e1bf7-8ddb-461a-bbee-04b077950000", "prodbot");
        Assert.Equal(canonical, DeterministicDeploymentId.Compute("2f9e1bf7-8ddb-461a-bbee-04b077950000", name));
    }
}
