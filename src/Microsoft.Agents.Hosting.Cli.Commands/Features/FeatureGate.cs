using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Features;

/// <summary>Enforces a feature on a mixed command route that cannot be removed as one metadata node.</summary>
public static class FeatureGate
{
    /// <summary>Reports whether a route's rollout control is enabled.</summary>
    public static bool IsEnabled(IServiceProvider services, FeatureName featureName)
    {
        ArgumentNullException.ThrowIfNull(services);

        var featureFlags = (IFeatureFlags?)services.GetService(typeof(IFeatureFlags))
            ?? throw new CommandConfigurationException("No feature-control service is registered.");

        return featureFlags.IsFeatureEnabled(featureName);
    }

    /// <summary>Rejects direct dispatch when a route's rollout control is disabled.</summary>
    public static void RequireEnabled(
        IServiceProvider services,
        FeatureName featureName,
        string surface,
        string? guidance = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);

        if (!IsEnabled(services, featureName))
        {
            throw new CommandUsageException(
                $"{surface} is disabled by the {featureName} rollout control. " +
                "Feature controls are availability switches, not authorization boundaries." +
                (string.IsNullOrWhiteSpace(guidance) ? string.Empty : $" {guidance}"));
        }
    }
}
