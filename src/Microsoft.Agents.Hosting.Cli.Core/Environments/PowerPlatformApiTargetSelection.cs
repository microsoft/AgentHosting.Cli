using System.Collections.Frozen;
using Microsoft.Agents.Hosting.Cli.Core.Auth;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>Resolves the registered operator cloud targets without accepting arbitrary coordinates.</summary>
public static class PowerPlatformApiTargetSelection
{
    private sealed record TargetDescriptor(string Token, PowerPlatformApiTargetConfig Target);

    private static readonly TargetDescriptor[] Descriptors =
    [
        new("Public", PowerPlatformApiTargetConfig.Public),
        new("Preprod", PowerPlatformApiTargetConfig.Preprod),
        new("Test", PowerPlatformApiTargetConfig.Test),
        new("UsGov", ProductionTarget(CloudInstance.UsGov)),
        new("UsGovHigh", ProductionTarget(CloudInstance.UsGovHigh)),
        new("UsGovDod", ProductionTarget(CloudInstance.UsGovDod)),
        new("China", ProductionTarget(CloudInstance.China)),
        new("UsNat", ProductionTarget(CloudInstance.UsNat)),
        new("UsSec", ProductionTarget(CloudInstance.UsSec)),
    ];

    private static readonly FrozenDictionary<string, TargetDescriptor> ByToken =
        Descriptors.ToFrozenDictionary(descriptor => descriptor.Token, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<(CloudInstance, PowerPlatformApiRing), TargetDescriptor> ByCoordinates =
        Descriptors.ToFrozenDictionary(descriptor => (descriptor.Target.Cloud, descriptor.Target.Ring));

    /// <summary>The canonical nine cloud tokens, excluding legacy production aliases.</summary>
    public static IReadOnlyList<string> AcceptedTokens { get; } =
        Array.AsReadOnly(Descriptors.Select(descriptor => descriptor.Token).ToArray());

    /// <summary>Resolves an operator token, ignoring case and surrounding whitespace.</summary>
    public static bool TryResolve(string? token, out PowerPlatformApiTargetConfig target, out string? error)
    {
        var normalized = string.IsNullOrWhiteSpace(token) ? "Public" : token.Trim();
        if (normalized.Equals("prod", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("production", StringComparison.OrdinalIgnoreCase))
            normalized = "Public";

        if (ByToken.TryGetValue(normalized, out var descriptor))
        {
            target = descriptor.Target;
            error = null;
            return true;
        }

        target = PowerPlatformApiTargetConfig.Public;
        error = $"'{normalized}' is not a Power Platform API cloud. Supply one of: {string.Join(", ", AcceptedTokens)}.";
        return false;
    }

    /// <summary>Returns the fixed target; sovereign clouds support only production.</summary>
    public static PowerPlatformApiTargetConfig TargetFor(
        CloudInstance cloud, PowerPlatformApiRing ring = PowerPlatformApiRing.Production) =>
        Describe(cloud, ring).Target;

    /// <summary>The canonical operator token for a registered cloud and ring.</summary>
    public static string CanonicalToken(
        CloudInstance cloud, PowerPlatformApiRing ring = PowerPlatformApiRing.Production) =>
        Describe(cloud, ring).Token;

    /// <summary>Rejects supplied coordinates that differ from their registered target.</summary>
    public static PowerPlatformApiTargetConfig Require(PowerPlatformApiTargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var registered = TargetFor(target.Cloud, target.Ring);
        var endpoint = target.GlobalEndpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps
            || !endpoint.IsDefaultPort || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Power Platform API endpoints must use HTTPS without user information, a non-default port, query, or fragment.", nameof(target));
        return registered == target
            ? registered
            : throw new ArgumentException("Power Platform API coordinates must match the registered cloud target.", nameof(target));
    }

    private static TargetDescriptor Describe(CloudInstance cloud, PowerPlatformApiRing ring) =>
        ByCoordinates.TryGetValue((cloud, ring), out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(cloud), cloud, $"No Power Platform API target is registered for {cloud}/{ring}.");

    private static PowerPlatformApiTargetConfig ProductionTarget(CloudInstance cloud)
    {
        var endpoint = new Uri("https://" + PowerPlatformCloudMap.ForCloud(cloud).TrustedHostSuffixes[0]);
        return new()
        {
            Cloud = cloud,
            Ring = PowerPlatformApiRing.Production,
            GlobalEndpoint = endpoint,
            Audience = endpoint.AbsoluteUri,
            EnvironmentsApiVersion = PowerPlatformApiTargetConfig.Public.EnvironmentsApiVersion,
        };
    }
}
