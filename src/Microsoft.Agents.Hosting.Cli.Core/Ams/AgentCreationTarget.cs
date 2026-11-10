using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Resolves the endpoint and route mode used by <c>msagent agent create</c>. Normal operation uses the
/// trusted environment API front door; an embedding or test host may explicitly supply a direct
/// AMSDataPlane endpoint.
/// </summary>
public sealed record AgentCreationTarget
{
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

        return new AgentCreationTargetResolution
        {
            Endpoint = AmsEndpointValidation.RequireHttps(Endpoint, "The supplied Agent Management Service Data Plane endpoint", "The configured Agent Management Service Data Plane endpoint", AgentCreationException.EndpointNotUsable),
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
