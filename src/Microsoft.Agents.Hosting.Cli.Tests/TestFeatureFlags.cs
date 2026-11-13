using Microsoft.Agents.Hosting.Cli.Commands.Features;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal sealed class TestFeatureFlags(params FeatureName[] enabled) : IFeatureFlags
{
    internal static IFeatureFlags AllProductSurfaces { get; } = new TestFeatureFlags(
        FeatureName.AmsFeatures,
        FeatureName.AzureBotServiceFeatures,
        FeatureName.IdentityFeatures,
        FeatureName.LegacyAuth,
        FeatureName.AppUserSupport);

    internal static IFeatureFlags Everything { get; } = new TestFeatureFlags([.. Enum.GetValues<FeatureName>()]);

    public bool IsFeatureEnabled(FeatureName featureName) => enabled.Contains(featureName);
}
