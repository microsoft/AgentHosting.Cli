using System.Collections.Frozen;
using Microsoft.Agents.Hosting.Cli.Core.Auth;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// The Power Platform core-services cluster category. Mirrors
/// <c>CopilotStudio.Sync/Models.cs</c>, itself taken from CoreFramework's <c>ClusterCategory</c>.
/// </summary>
public enum CoreServicesClusterCategory
{
    Exp = 0,
    Dev = 1,
    Test = 2,
    Preprod = 3,
    FirstRelease = 4,
    Prod = 5,
    Gov = 6,
    High = 7,
    DoD = 8,
    Mooncake = 9,
    Ex = 10,
    Rx = 11,
    Prv = 12,
    Local = 13,
    GovFR = 14,
}

/// <summary>
/// Resolves the Power Apps hosts a cluster category serves.
///
/// The sovereign and production entries mirror the shipping Copilot Studio extension —
/// <c>connections/connectionCreation.ts getPlayerBaseUrl</c> and
/// <c>Dataverse/PowerAppsClient.cs ResolveHost</c>. The non-production rings deliberately do not:
/// the extension sends every one of them to the preview player, which rejects a preprod environment
/// with <c>EnvironmentIdInvalid</c>. Each ring has its own player, confirmed against the live hosts
/// and against the <c>clientUris.maker</c> the environment record itself reports.
/// </summary>
public static class CoreServicesCluster
{
    private const string PreviewPlayer = "apps.preview.powerapps.com";
    private const string ProdPlayer = "apps.powerapps.com";
    private const string ProdApi = "api.powerapps.com";
    private const string ProdMaker = "make.powerapps.com";
    private const string PreviewMaker = "make.preview.powerapps.com";
    private const string TestManagedAppsPlayer = "play.test.managedapps.cloud.dev.microsoft";

    private static readonly FrozenDictionary<CoreServicesClusterCategory, string> Players =
        new Dictionary<CoreServicesClusterCategory, string>
        {
            [CoreServicesClusterCategory.Gov] = "apps.gov.powerapps.us",
            [CoreServicesClusterCategory.GovFR] = "apps.gov.powerapps.us",
            [CoreServicesClusterCategory.High] = "apps.high.powerapps.us",
            [CoreServicesClusterCategory.DoD] = "play.apps.appsplatform.us",
            [CoreServicesClusterCategory.Mooncake] = "apps.powerapps.cn",
            [CoreServicesClusterCategory.Ex] = "apps.powerapps.eaglex.ic.gov",
            [CoreServicesClusterCategory.Rx] = "apps.powerapps.microsoft.scloud",
            [CoreServicesClusterCategory.Preprod] = "apps.preprod.powerapps.com",
            [CoreServicesClusterCategory.Test] = "apps.test.powerapps.com",
            [CoreServicesClusterCategory.Exp] = PreviewPlayer,
            [CoreServicesClusterCategory.Dev] = PreviewPlayer,
            [CoreServicesClusterCategory.Prv] = PreviewPlayer,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<CoreServicesClusterCategory, string> Apis =
        new Dictionary<CoreServicesClusterCategory, string>
        {
            [CoreServicesClusterCategory.Gov] = "gov.api.powerapps.us",
            [CoreServicesClusterCategory.GovFR] = "gov.api.powerapps.us",
            [CoreServicesClusterCategory.High] = "high.api.powerapps.us",
            [CoreServicesClusterCategory.DoD] = "api.apps.appsplatform.us",
            [CoreServicesClusterCategory.Mooncake] = "api.powerapps.cn",
            [CoreServicesClusterCategory.Ex] = "api.powerapps.eaglex.ic.gov",
            [CoreServicesClusterCategory.Rx] = "api.powerapps.microsoft.scloud",
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<CoreServicesClusterCategory, string> Makers =
        new Dictionary<CoreServicesClusterCategory, string>
        {
            [CoreServicesClusterCategory.Gov] = "make.gov.powerapps.us",
            [CoreServicesClusterCategory.GovFR] = "make.gov.powerapps.us",
            [CoreServicesClusterCategory.High] = "make.high.powerapps.us",
            [CoreServicesClusterCategory.DoD] = "make.apps.appsplatform.us",
            [CoreServicesClusterCategory.Mooncake] = "make.powerapps.cn",
            [CoreServicesClusterCategory.Preprod] = "make.preprod.powerapps.com",
            [CoreServicesClusterCategory.Test] = "make.test.powerapps.com",
            [CoreServicesClusterCategory.Exp] = PreviewMaker,
            [CoreServicesClusterCategory.Dev] = PreviewMaker,
            [CoreServicesClusterCategory.Prv] = PreviewMaker,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<CoreServicesClusterCategory, string> ManagedAppsPlayers =
        new Dictionary<CoreServicesClusterCategory, string>
        {
            [CoreServicesClusterCategory.Dev] = TestManagedAppsPlayer,
            [CoreServicesClusterCategory.Test] = TestManagedAppsPlayer,
            [CoreServicesClusterCategory.Preprod] = "play.preprod.managedapps.cloud.dev.microsoft",
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<CloudInstance, CoreServicesClusterCategory> SovereignCategories =
        new Dictionary<CloudInstance, CoreServicesClusterCategory>
        {
            [CloudInstance.UsGov] = CoreServicesClusterCategory.Gov,
            [CloudInstance.UsGovHigh] = CoreServicesClusterCategory.High,
            [CloudInstance.UsGovDod] = CoreServicesClusterCategory.DoD,
            [CloudInstance.China] = CoreServicesClusterCategory.Mooncake,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<PowerPlatformApiRing, CoreServicesClusterCategory> RingCategories =
        new Dictionary<PowerPlatformApiRing, CoreServicesClusterCategory>
        {
            [PowerPlatformApiRing.Production] = CoreServicesClusterCategory.Prod,
            [PowerPlatformApiRing.Preprod] = CoreServicesClusterCategory.Preprod,
            [PowerPlatformApiRing.Test] = CoreServicesClusterCategory.Test,
        }.ToFrozenDictionary();

    /// <summary>The cluster category a target's cloud and ring identify.</summary>
    /// <param name="cloud">The identity cloud.</param>
    /// <param name="ring">The deployment ring, which only distinguishes public cloud.</param>
    public static CoreServicesClusterCategory For(CloudInstance cloud, PowerPlatformApiRing ring)
    {
        if (SovereignCategories.TryGetValue(cloud, out var sovereign))
        {
            return sovereign;
        }

        return cloud == CloudInstance.Public && RingCategories.TryGetValue(ring, out var category)
            ? category
            : throw new ArgumentOutOfRangeException(nameof(cloud), cloud, $"No cluster category serves {cloud} in the {ring} ring.");
    }

    /// <summary>The cluster category a target identifies.</summary>
    /// <param name="target">The resolved Power Platform API target.</param>
    public static CoreServicesClusterCategory For(PowerPlatformApiTargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return For(target.Cloud, target.Ring);
    }

    /// <summary>The Power Apps player host, which serves the connection-creation page.</summary>
    /// <param name="category">The cluster category.</param>
    public static string PlayerHost(CoreServicesClusterCategory category) =>
        Players.TryGetValue(category, out var player) ? player : ProdPlayer;

    /// <summary>
    /// The Microsoft Apps player host serving a ring that has moved to the managed-apps player, or
    /// <see langword="null"/> when the ring is still served by the Power Apps player.
    /// </summary>
    /// <param name="category">The cluster category.</param>
    /// <returns>The managed-apps player host, or <see langword="null"/>.</returns>
    public static string? ManagedAppsPlayerHost(CoreServicesClusterCategory category) =>
        ManagedAppsPlayers.TryGetValue(category, out var player) ? player : null;

    /// <summary>The Power Apps API host, which serves the connections API.</summary>
    /// <param name="category">The cluster category.</param>
    public static string ApiHost(CoreServicesClusterCategory category) =>
        Apis.TryGetValue(category, out var api) ? api : ProdApi;

    /// <summary>The maker portal host, which serves the create-by-hand page.</summary>
    /// <param name="category">The cluster category.</param>
    public static string MakerHost(CoreServicesClusterCategory category) =>
        Makers.TryGetValue(category, out var maker) ? maker : ProdMaker;
}
