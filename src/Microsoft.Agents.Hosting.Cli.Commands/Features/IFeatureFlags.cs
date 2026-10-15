namespace Microsoft.Agents.Hosting.Cli.Commands.Features;

/// <summary>Resolves shipped feature defaults and supported runtime overrides.</summary>
public interface IFeatureFlags
{
    /// <summary>Whether a feature is enabled for this process.</summary>
    bool IsFeatureEnabled(FeatureName featureName);
}
