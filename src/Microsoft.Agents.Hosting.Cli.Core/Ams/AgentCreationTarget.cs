using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Resolves the endpoint and route mode used by <c>msagent agent create</c>. Normal operation uses the
/// trusted environment API front door; deployment configuration may override it for direct
/// AMSDataPlane Dev-island proof.
/// </summary>
public sealed record AgentCreationTarget
{
    public const string EndpointVariableName = "AH_CLI_AMSDP_ENDPOINT";

    public string? Endpoint { get; init; }

    public AgentCreationTargetResolution Resolve(Uri environmentEndpoint)
    {
        ArgumentNullException.ThrowIfNull(environmentEndpoint);

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            return new AgentCreationTargetResolution
            {
                Endpoint = environmentEndpoint,
                RouteMode = AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor,
            };
        }

        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw AgentCreationException.EndpointNotUsable(
                $"The configured Agent Management Service Data Plane endpoint ({EndpointVariableName}) is not an absolute uri.",
                $"Set {EndpointVariableName} to an absolute https endpoint, for example https://<host>.");
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCreationException.EndpointNotUsable(
                $"The configured Agent Management Service Data Plane endpoint uses scheme '{endpoint.Scheme}', and a delegated token is only ever sent over https.",
                $"Set {EndpointVariableName} to an https endpoint.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw AgentCreationException.EndpointNotUsable(
                "The configured Agent Management Service Data Plane endpoint carries embedded user information.",
                $"Set {EndpointVariableName} to an https endpoint with no embedded credentials.");
        }

        return new AgentCreationTargetResolution
        {
            Endpoint = endpoint,
            RouteMode = AmsAgentCreationRouteMode.DirectDataPlane,
        };
    }
}

/// <summary>The resolved endpoint plus the route shape expected by that endpoint.</summary>
public sealed record AgentCreationTargetResolution
{
    public required Uri Endpoint { get; init; }
    public required AmsAgentCreationRouteMode RouteMode { get; init; }
}
