using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Resolves deployment-configured personal-Dev service endpoints. Operators cannot supply these
/// credential-bearing targets on the command line.
/// </summary>
public sealed record AgentHostingServiceTargets
{
    public const string DataPlaneEndpointVariableName = "AH_CLI_AMSDP_ENDPOINT";
    public const string ControlPlaneEndpointVariableName = "AH_CLI_AMS_ENDPOINT";

    public string? DataPlaneEndpoint { get; init; }
    public string? ControlPlaneEndpoint { get; init; }

    public Uri ResolveDataPlane() => ResolveEndpoint(
        DataPlaneEndpoint,
        DataPlaneEndpointVariableName,
        "Agent Management Service Data Plane");

    public Uri ResolveControlPlane() => ResolveEndpoint(
        ControlPlaneEndpoint,
        ControlPlaneEndpointVariableName,
        "Agent Management Service control plane");

    private static Uri ResolveEndpoint(string? raw, string settingName, string label)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw AgentDeployException.TargetNotConfigured(
                $"No {label} endpoint is configured ({settingName} is not set).",
                $"Set {settingName} to the deployed personal-Dev {label} endpoint.");
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw AgentDeployException.EndpointNotUsable(
                $"The configured {label} endpoint ({settingName}) is not an absolute HTTPS URI without embedded user information.",
                $"Set {settingName} to the deployed HTTPS endpoint.");
        }

        return endpoint;
    }
}
