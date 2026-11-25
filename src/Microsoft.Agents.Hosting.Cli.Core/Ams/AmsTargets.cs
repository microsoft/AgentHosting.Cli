using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Resolves the Agent Management Service endpoints that the calls for an agent's home environment go to.</summary>
public sealed class AmsTargets
{
    private readonly AmsEndpointProfile _endpoints;
    private readonly Uri _controlPlaneEndpoint;

    /// <summary>Creates the resolver for the signed-in ring's Power Platform API.</summary>
    public AmsTargets(AmsEndpointProfile endpoints, PowerPlatformApiTargetConfig powerPlatformApi)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _controlPlaneEndpoint = AmsEndpointProfile.TrustedControlPlaneEndpoint(powerPlatformApi);
    }

    /// <summary>Returns the data-plane endpoint of the agent's home environment.</summary>
    public AmsServiceTarget DataPlane(PowerPlatformEnvironment homeEnvironment) => _endpoints.ResolveDataPlane(homeEnvironment);

    /// <summary>Returns the control-plane endpoint addressed through the agent's home environment, on the cloud of the sign-in that resolved it.</summary>
    public AmsServiceTarget ControlPlane(PowerPlatformEnvironment homeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(homeEnvironment);

        return _endpoints.ResolveControlPlane(homeEnvironment, homeEnvironment.Context is { } context ? AmsEndpointProfile.TrustedControlPlaneEndpoint(context.Target) : _controlPlaneEndpoint);
    }
}
