using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class CoreServicesClusterTests
{
    public static TheoryData<CoreServicesClusterCategory> AllCategories()
    {
        var data = new TheoryData<CoreServicesClusterCategory>();

        foreach (var category in Enum.GetValues<CoreServicesClusterCategory>())
        {
            data.Add(category);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void EveryCategoryResolvesAHostForEveryRole(CoreServicesClusterCategory category)
    {
        Assert.False(string.IsNullOrWhiteSpace(CoreServicesCluster.PlayerHost(category)));
        Assert.False(string.IsNullOrWhiteSpace(CoreServicesCluster.ApiHost(category)));
        Assert.False(string.IsNullOrWhiteSpace(CoreServicesCluster.MakerHost(category)));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Exp)]
    [InlineData(CoreServicesClusterCategory.Dev)]
    [InlineData(CoreServicesClusterCategory.Prv)]
    public void TheUnreachableNonProductionCategoriesKeepThePreviewPlayer(CoreServicesClusterCategory category)
    {
        Assert.Equal("apps.preview.powerapps.com", CoreServicesCluster.PlayerHost(category));
        Assert.Equal("make.preview.powerapps.com", CoreServicesCluster.MakerHost(category));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Preprod, "apps.preprod.powerapps.com", "make.preprod.powerapps.com")]
    [InlineData(CoreServicesClusterCategory.Test, "apps.test.powerapps.com", "make.test.powerapps.com")]
    public void EachSelectableRingHasItsOwnPlayerAndMaker(CoreServicesClusterCategory category, string player, string maker)
    {
        Assert.Equal(player, CoreServicesCluster.PlayerHost(category));
        Assert.Equal(maker, CoreServicesCluster.MakerHost(category));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Preprod)]
    [InlineData(CoreServicesClusterCategory.Test)]
    public void ASelectableRingNeverSharesTheProductionOrPreviewPlayer(CoreServicesClusterCategory category)
    {
        Assert.NotEqual("apps.powerapps.com", CoreServicesCluster.PlayerHost(category));
        Assert.NotEqual("apps.preview.powerapps.com", CoreServicesCluster.PlayerHost(category));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Prod)]
    [InlineData(CoreServicesClusterCategory.FirstRelease)]
    [InlineData(CoreServicesClusterCategory.Local)]
    public void ProductionCategoriesUseTheProductionPlayer(CoreServicesClusterCategory category)
    {
        Assert.Equal("apps.powerapps.com", CoreServicesCluster.PlayerHost(category));
        Assert.Equal("make.powerapps.com", CoreServicesCluster.MakerHost(category));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Exp)]
    [InlineData(CoreServicesClusterCategory.Dev)]
    [InlineData(CoreServicesClusterCategory.Test)]
    [InlineData(CoreServicesClusterCategory.Preprod)]
    [InlineData(CoreServicesClusterCategory.FirstRelease)]
    [InlineData(CoreServicesClusterCategory.Prod)]
    [InlineData(CoreServicesClusterCategory.Prv)]
    [InlineData(CoreServicesClusterCategory.Local)]
    public void EveryPublicCategorySharesOneConnectionsApiHost(CoreServicesClusterCategory category)
    {
        Assert.Equal("api.powerapps.com", CoreServicesCluster.ApiHost(category));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Gov, "apps.gov.powerapps.us", "gov.api.powerapps.us", "make.gov.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.GovFR, "apps.gov.powerapps.us", "gov.api.powerapps.us", "make.gov.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.High, "apps.high.powerapps.us", "high.api.powerapps.us", "make.high.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.DoD, "play.apps.appsplatform.us", "api.apps.appsplatform.us", "make.apps.appsplatform.us")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "apps.powerapps.cn", "api.powerapps.cn", "make.powerapps.cn")]
    public void SovereignCategoriesKeepTheirOwnHosts(CoreServicesClusterCategory category, string player, string api, string maker)
    {
        Assert.Equal(player, CoreServicesCluster.PlayerHost(category));
        Assert.Equal(api, CoreServicesCluster.ApiHost(category));
        Assert.Equal(maker, CoreServicesCluster.MakerHost(category));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, CoreServicesClusterCategory.Prod)]
    [InlineData(PowerPlatformApiRing.Preprod, CoreServicesClusterCategory.Preprod)]
    [InlineData(PowerPlatformApiRing.Test, CoreServicesClusterCategory.Test)]
    public void ThePublicCloudResolvesACategoryPerRing(PowerPlatformApiRing ring, CoreServicesClusterCategory expected)
    {
        Assert.Equal(expected, CoreServicesCluster.For(CloudInstance.Public, ring));
    }

    [Theory]
    [InlineData(CloudInstance.UsGov, CoreServicesClusterCategory.Gov)]
    [InlineData(CloudInstance.UsGovHigh, CoreServicesClusterCategory.High)]
    [InlineData(CloudInstance.UsGovDod, CoreServicesClusterCategory.DoD)]
    [InlineData(CloudInstance.China, CoreServicesClusterCategory.Mooncake)]
    public void ASovereignCloudResolvesTheSameCategoryInEveryRing(CloudInstance cloud, CoreServicesClusterCategory expected)
    {
        foreach (var ring in Enum.GetValues<PowerPlatformApiRing>())
        {
            Assert.Equal(expected, CoreServicesCluster.For(cloud, ring));
        }
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, CoreServicesClusterCategory.Prod)]
    [InlineData(PowerPlatformApiRing.Preprod, CoreServicesClusterCategory.Preprod)]
    [InlineData(PowerPlatformApiRing.Test, CoreServicesClusterCategory.Test)]
    public void ATargetResolvesTheCategoryItsCloudAndRingIdentify(PowerPlatformApiRing ring, CoreServicesClusterCategory expected)
    {
        Assert.Equal(expected, CoreServicesCluster.For(PowerPlatformApiRingSelection.TargetFor(ring)));
    }

    [Fact]
    public void ATargetIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => CoreServicesCluster.For(null!));
    }

    [Fact]
    public void ACloudWithNoCategoryInTheSelectedRingIsRejected()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => CoreServicesCluster.For((CloudInstance)999, PowerPlatformApiRing.Production));

        Assert.Contains("cluster category", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Preprod, "play.preprod.managedapps.cloud.dev.microsoft", "/connection/new")]
    [InlineData(PowerPlatformApiRing.Test, "play.test.managedapps.cloud.dev.microsoft", "/connection/new")]
    [InlineData(PowerPlatformApiRing.Production, "apps.powerapps.com", "/appframework/e/env-1/connections/new")]
    public void AConsentLinkTargetsTheSelectedRingsPlayer(PowerPlatformApiRing ring, string expectedHost, string expectedPath)
    {
        var link = ConnectionConsentLink.Build(
            CoreServicesCluster.For(PowerPlatformApiRingSelection.TargetFor(ring)),
            "env-1",
            "shared_onedriveforbusiness",
            null,
            null);

        Assert.Equal(expectedHost, link.Host);
        Assert.Equal(expectedPath, link.AbsolutePath);
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Preprod)]
    [InlineData(PowerPlatformApiRing.Test)]
    public void ANonProductionRingReadsTheProductionConnectionsApi(PowerPlatformApiRing ring)
    {
        Assert.Equal(
            ConnectionConsentLink.ApiHostFor(CoreServicesClusterCategory.Prod),
            ConnectionConsentLink.ApiHostFor(CoreServicesCluster.For(PowerPlatformApiRingSelection.TargetFor(ring))));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Preprod, "make.preprod.powerapps.com")]
    [InlineData(PowerPlatformApiRing.Test, "make.test.powerapps.com")]
    [InlineData(PowerPlatformApiRing.Production, "make.powerapps.com")]
    public void AMakerLinkTargetsTheSelectedRingsPortal(PowerPlatformApiRing ring, string expectedHost)
    {
        var link = ConnectionConsentLink.BuildMakerLink(
            CoreServicesCluster.For(PowerPlatformApiRingSelection.TargetFor(ring)),
            "env-1",
            "/providers/Microsoft.PowerApps/apis/shared_teams");

        Assert.Equal(expectedHost, link.Host);
        Assert.Equal("/environments/env-1/connections/available", link.AbsolutePath);
        Assert.Contains("apiName=shared_teams", link.Query, StringComparison.Ordinal);
    }
}
