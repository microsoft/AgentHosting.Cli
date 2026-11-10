using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Resolves an agent's home environment and the Agent Management Service endpoints every AMS call of the agent goes to.</summary>
public sealed class AmsTargets
{
    private readonly AmsEndpointProfile _endpoints;
    private readonly EnvironmentSelectionService _environments;
    private readonly Uri _controlPlaneEndpoint;

    /// <summary>Creates the resolver.</summary>
    /// <param name="endpoints">Resolves the data-plane, on-behalf-of and control-plane endpoints.</param>
    /// <param name="environments">Resolves the home environment.</param>
    /// <param name="powerPlatformApi">The signed-in ring's Power Platform API, whose control plane is trusted.</param>
    public AmsTargets(AmsEndpointProfile endpoints, EnvironmentSelectionService environments, PowerPlatformApiTargetConfig powerPlatformApi)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _controlPlaneEndpoint = AmsEndpointProfile.TrustedControlPlaneEndpoint(powerPlatformApi);
    }

    /// <summary>Resolves an agent's home environment, from the active selection when it is the home and from the environment list otherwise.</summary>
    /// <param name="environmentId">The home environment.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    public async Task<PowerPlatformEnvironment> HomeAsync(string environmentId, CancellationToken cancellationToken)
    {
        var active = await _environments.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return await _environments.ResolveForCommandAsync(string.Equals(active?.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase) ? null : environmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the active environment, the home of the agents a command registers when no agent names one.</summary>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    public Task<PowerPlatformEnvironment> ActiveAsync(CancellationToken cancellationToken) => _environments.ResolveForCommandAsync(null, cancellationToken);

    /// <summary>The data-plane endpoint of the home environment.</summary>
    /// <param name="home">The agent's home environment.</param>
    public AmsServiceTarget DataPlane(PowerPlatformEnvironment home) => _endpoints.ResolveDataPlane(home);

    /// <summary>The data-plane endpoint that acts on the caller's behalf, which calls that read or write Dataverse need.</summary>
    /// <param name="home">The agent's home environment.</param>
    /// <param name="operation">The command, named when no such endpoint is usable.</param>
    public AmsServiceTarget OnBehalfOf(PowerPlatformEnvironment home, string operation) => _endpoints.ResolveOnBehalfOf(home, operation);

    /// <summary>The control-plane endpoint addressed through the home environment.</summary>
    /// <param name="home">The agent's home environment.</param>
    public AmsServiceTarget ControlPlane(PowerPlatformEnvironment home) => _endpoints.ResolveControlPlane(home, _controlPlaneEndpoint);
}
