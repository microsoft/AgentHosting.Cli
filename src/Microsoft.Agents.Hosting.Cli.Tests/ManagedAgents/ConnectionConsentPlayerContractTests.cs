using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

/// <summary>
/// Pins which player contract each ring gets. The rings that moved to the Microsoft Apps player use
/// <c>/connection/new</c> with the environment as <c>ms_envId</c>; the rest keep the Power Apps
/// player's <c>/appframework/e/{env}/connections/new</c>.
/// </summary>
public sealed class ConnectionConsentPlayerContractTests
{
    private const string Env = "7dd33ec4-beaa-e13b-98c0-f44e43656430";

    private static Uri Link(CoreServicesClusterCategory category) =>
        ConnectionConsentLink.Build(category, Env, "shared_msnweather", new Uri("http://127.0.0.1:53871/callback"), "n1");

    [Theory]
    [InlineData(CoreServicesClusterCategory.Preprod, "play.preprod.managedapps.cloud.dev.microsoft")]
    [InlineData(CoreServicesClusterCategory.Test, "play.test.managedapps.cloud.dev.microsoft")]
    [InlineData(CoreServicesClusterCategory.Dev, "play.test.managedapps.cloud.dev.microsoft")]
    public void TheRingsThatMovedUseTheManagedAppsPlayer(CoreServicesClusterCategory category, string host)
    {
        var link = Link(category);

        Assert.Equal(host, link.Host);
        Assert.Equal("/connection/new", link.AbsolutePath);
        Assert.Contains($"ms_envId={Env}", link.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("appframework", link.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Prod)]
    [InlineData(CoreServicesClusterCategory.FirstRelease)]
    [InlineData(CoreServicesClusterCategory.Gov)]
    [InlineData(CoreServicesClusterCategory.High)]
    [InlineData(CoreServicesClusterCategory.DoD)]
    [InlineData(CoreServicesClusterCategory.Mooncake)]
    public void EveryOtherRingKeepsThePowerAppsPlayer(CoreServicesClusterCategory category)
    {
        var link = Link(category);

        Assert.Equal(CoreServicesCluster.PlayerHost(category), link.Host);
        Assert.Equal($"/appframework/e/{Env}/connections/new", link.AbsolutePath);
        Assert.DoesNotContain("ms_envId", link.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionIsNotMovedWhileTheOwningTeamStillPointsItAtThePreviewPlayer()
    {
        Assert.Null(CoreServicesCluster.ManagedAppsPlayerHost(CoreServicesClusterCategory.Prod));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Preprod)]
    [InlineData(CoreServicesClusterCategory.Prod)]
    public void BothContractsCarryTheSameRequestParameters(CoreServicesClusterCategory category)
    {
        var query = Link(category).Query;

        Assert.Contains("connector=shared_msnweather", query, StringComparison.Ordinal);
        Assert.Contains("nonce=n1", query, StringComparison.Ordinal);
        Assert.Contains("v=1", query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("http://127.0.0.1:53871/callback"), query, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManagedAppsLinkOmitsAnAbsentCallbackAndNonce()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Preprod, Env, "shared_msnweather", null, null);

        Assert.DoesNotContain("callbackUrl", link.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", link.Query, StringComparison.Ordinal);
        Assert.Contains($"ms_envId={Env}", link.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderPathConnectorIsReducedToItsIdOnBothContracts()
    {
        foreach (var category in new[] { CoreServicesClusterCategory.Preprod, CoreServicesClusterCategory.Prod })
        {
            var link = ConnectionConsentLink.Build(
                category, Env, "/providers/Microsoft.PowerApps/apis/shared_office365users", null, null);

            Assert.Contains("connector=shared_office365users", link.Query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoRingSendsAPreprodEnvironmentToThePreviewPlayerThatRejectsIt()
    {
        Assert.NotEqual("apps.preview.powerapps.com", Link(CoreServicesClusterCategory.Preprod).Host);
    }
}
