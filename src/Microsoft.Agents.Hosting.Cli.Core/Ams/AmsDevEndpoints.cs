using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// An explicit, paired personal-DEV route override. It changes service destinations only, never
/// environment discovery, tenant binding, or the Power Platform API token audience.
/// </summary>
public sealed class AmsDevEndpoints
{
    private const RegexOptions PatternOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const string AliasPattern = @"(?<alias>[a-z0-9]+(?:-[a-z0-9]+)*)";
    private static readonly Regex ControlPlanePattern = new(
        @"\Ahttps://parp-agentmanagementservice-" + AliasPattern +
        @"\.(?<region>[a-z]{2,12})-cm[0-9]{3}\.gateway\.test\.cm\.powerapps\.com/?\z",
        PatternOptions);
    private static readonly Regex DataPlanePattern = new(
        @"\Ahttps://parp-agentmanagementservicedataplane-" + AliasPattern +
        @"\.(?<region>[a-z]{2,12})-il[0-9]{3}\.gateway\.test\.island\.powerapps\.com/?\z",
        PatternOptions);

    private AmsDevEndpoints(Uri? controlPlane, Uri? dataPlane)
    {
        ControlPlane = controlPlane;
        DataPlane = dataPlane;
    }

    public static AmsDevEndpoints Unconfigured { get; } = new(null, null);

    public Uri? ControlPlane { get; }
    public Uri? DataPlane { get; }

    public static AmsDevEndpoints FromConfiguration(
        PowerPlatformApiRing ring,
        string? controlPlane,
        string? dataPlane)
    {
        if (string.IsNullOrEmpty(controlPlane) && string.IsNullOrEmpty(dataPlane))
        {
            return Unconfigured;
        }

        if (string.IsNullOrEmpty(controlPlane) || string.IsNullOrEmpty(dataPlane))
        {
            throw new ArgumentException(
                "AH_CLI_AMS_ENDPOINT and AH_CLI_AMSDP_ENDPOINT must both be configured, or both be unset.");
        }

        if (ring != PowerPlatformApiRing.Test)
        {
            throw new ArgumentException(
                "Personal AMS endpoint overrides require an active Test-ring profile. " +
                "Unset both overrides before selecting another ring.");
        }

        var (controlUri, controlMatch) = Validate(
            controlPlane, ControlPlanePattern, "AH_CLI_AMS_ENDPOINT", "cm", "cm");
        var (dataUri, dataMatch) = Validate(
            dataPlane, DataPlanePattern, "AH_CLI_AMSDP_ENDPOINT", "il", "island");
        if (!string.Equals(
                controlMatch.Groups["alias"].Value,
                dataMatch.Groups["alias"].Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                controlMatch.Groups["region"].Value,
                dataMatch.Groups["region"].Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "AH_CLI_AMS_ENDPOINT and AH_CLI_AMSDP_ENDPOINT must name the same personal alias and region.");
        }

        return new AmsDevEndpoints(controlUri, dataUri);
    }

    private static (Uri Endpoint, Match Match) Validate(
        string value,
        Regex pattern,
        string setting,
        string clusterPlane,
        string gatewayPlane)
    {
        // Match the original text so URI normalization cannot hide paths, ports, or delimiters.
        var match = value.Length <= 253 ? pattern.Match(value) : Match.Empty;
        if (!match.Success ||
            !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Host.Split('.').Any(label => label.Length > 63))
        {
            throw new ArgumentException(
                $"{setting} must be a personal HTTPS Test {clusterPlane} service origin " +
                $"under gateway.test.{gatewayPlane}.powerapps.com, with a non-empty alias. " +
                "Credentials, explicit ports, paths other than '/', query strings, and fragments are not allowed.");
        }

        return (endpoint, match);
    }
}
