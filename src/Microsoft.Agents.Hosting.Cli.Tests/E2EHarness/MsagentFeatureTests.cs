using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class MsagentFeatureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "msagent-features-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("code-agent", MsagentFeatures.AzureBotService)]
    [InlineData("abs", MsagentFeatures.AzureBotService)]
    [InlineData("identity", MsagentFeatures.Identity)]
    public void EachAzureFeatureTurnsOnTheRolloutFeatureItsCommandsNeed(string feature, string? msagentFeature) =>
        Assert.Equal(new[] { msagentFeature }, ScenarioCatalogue.All().Where(scenario => scenario.Feature == feature).Select(scenario => scenario.MsagentFeature).Distinct());

    [Fact]
    public void EveryOtherFeatureKeepsTheOperatorsRolloutFeatures() =>
        Assert.All(ScenarioCatalogue.All().Where(scenario => !ScenarioCatalogue.AzurePack.Contains(scenario.Feature)), scenario => Assert.Null(scenario.MsagentFeature));

    [Fact]
    public async Task TheConsentProbeRunsAbsWithAzureBotServiceTurnedOn()
    {
        var fake = new FakeMsagent(Path.Combine(_directory, "msagent"), "{}");
        var config = new E2EConfig { TenantId = Guid.NewGuid().ToString(), Azure = new AzureConfig { SubscriptionId = Guid.NewGuid().ToString(), ResourceGroup = "e2e-group" } };

        Assert.True(await HarnessEnvironment.ArmConsentGrantedAsync(new MsagentCli(fake.ExecutablePath, new Transcript(_directory), Path.Combine(_directory, "dumps")), config, CancellationToken.None));

        Assert.StartsWith("abs preview ", Assert.Single(fake.Invocations), StringComparison.Ordinal);
        Assert.StartsWith($"{MsagentFeatures.AzureBotService}|", Assert.Single(fake.Environments), StringComparison.Ordinal);
    }
}
