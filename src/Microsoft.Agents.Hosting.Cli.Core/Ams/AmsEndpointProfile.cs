using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>A test-only endpoint and token audience that replace the Power Platform API host for one Agent Management Service family.</summary>
/// <param name="EndpointVariable">The environment variable that supplied the endpoint, named in errors.</param>
/// <param name="Endpoint">The endpoint as supplied.</param>
/// <param name="Audience">The token audience the endpoint accepts, or null for the environment's own audience.</param>
public sealed record AmsEndpointOverride(string EndpointVariable, string? Endpoint, string? Audience);

/// <summary>Resolves the endpoint, token audience and route shape of every Agent Management Service call, honoring test-only overrides.</summary>
public sealed record AmsEndpointProfile
{
    /// <summary>The variable that points non on-behalf-of data-plane calls at a direct service endpoint.</summary>
    public const string DataPlaneEndpointVariable = "MSAGENT_AMS_DATAPLANE_ENDPOINT";

    /// <summary>The variable that names the token audience of the data-plane override.</summary>
    public const string DataPlaneAudienceVariable = "MSAGENT_AMS_DATAPLANE_AUDIENCE";

    /// <summary>The variable that points on-behalf-of data-plane calls at a gateway endpoint that carries the ticket.</summary>
    public const string OnBehalfOfEndpointVariable = "MSAGENT_AMS_ONBEHALFOF_ENDPOINT";

    /// <summary>The variable that names the token audience of the on-behalf-of override.</summary>
    public const string OnBehalfOfAudienceVariable = "MSAGENT_AMS_ONBEHALFOF_AUDIENCE";

    /// <summary>The variable that points control-plane calls at a direct service endpoint.</summary>
    public const string ControlPlaneEndpointVariable = "MSAGENT_AMS_CONTROLPLANE_ENDPOINT";

    /// <summary>The variable that names the token audience of the control-plane override.</summary>
    public const string ControlPlaneAudienceVariable = "MSAGENT_AMS_CONTROLPLANE_AUDIENCE";

    /// <summary>The variable that names the exact host of a personal Dev cluster's Git Repository Service, which git sign-in trusts besides the ring's environment hosts.</summary>
    public const string GitHostVariable = "MSAGENT_GIT_HOST";

    /// <summary>The override for data-plane calls that need no on-behalf-of exchange.</summary>
    public AmsEndpointOverride? DataPlane { get; init; }

    /// <summary>The override for data-plane calls that need the on-behalf-of exchange.</summary>
    public AmsEndpointOverride? OnBehalfOf { get; init; }

    /// <summary>The override for control-plane calls.</summary>
    public AmsEndpointOverride? ControlPlane { get; init; }

    /// <summary>The exact host of a personal Dev cluster's Git Repository Service that git sign-in trusts, or null when unset.</summary>
    public string? GitHost { get; init; }

    /// <summary>Reads every override from environment variables; unset variables keep the Power Platform API host.</summary>
    /// <param name="readVariable">Reads one environment variable.</param>
    public static AmsEndpointProfile FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        return new AmsEndpointProfile
        {
            DataPlane = Read(readVariable, DataPlaneEndpointVariable, DataPlaneAudienceVariable),
            OnBehalfOf = Read(readVariable, OnBehalfOfEndpointVariable, OnBehalfOfAudienceVariable),
            ControlPlane = Read(readVariable, ControlPlaneEndpointVariable, ControlPlaneAudienceVariable),
            GitHost = DeploymentRequestValidator.Trim(readVariable(GitHostVariable)),
        };
    }

    /// <summary>Resolves the endpoint for a data-plane call that needs no on-behalf-of exchange.</summary>
    /// <param name="environment">The agent's home environment.</param>
    public AmsServiceTarget ResolveDataPlane(PowerPlatformEnvironment environment) => Resolve(DataPlane, environment, environment.ApiEndpoint, AmsRouteForm.EnvironmentHost);

    /// <summary>Resolves the endpoint for a data-plane call the service runs on the caller's behalf, which never reaches the plain data-plane override.</summary>
    /// <param name="environment">The agent's home environment.</param>
    /// <param name="operation">The command that needs the exchange, named in the error.</param>
    public AmsServiceTarget ResolveOnBehalfOf(PowerPlatformEnvironment environment, string operation)
    {
        RequireOnBehalfOf(operation);
        return Resolve(OnBehalfOf, environment, environment.ApiEndpoint, AmsRouteForm.EnvironmentHost);
    }

    /// <summary>Rejects a call the service runs on the caller's behalf while only the plain data-plane override is set, because that endpoint carries no gateway ticket.</summary>
    /// <param name="operation">The command that needs the exchange, named in the error.</param>
    public void RequireOnBehalfOf(string operation)
    {
        if (OnBehalfOf is null && DataPlane is not null)
        {
            throw AmsAgentException.OnBehalfOfEndpointRequired(operation, DataPlane.EndpointVariable, OnBehalfOfEndpointVariable);
        }
    }

    /// <summary>Resolves the endpoint for a control-plane call addressed through the agent's home environment.</summary>
    /// <param name="homeEnvironment">The agent's home environment.</param>
    /// <param name="trustedGlobalEndpoint">The trusted tenant-scoped Power Platform API endpoint.</param>
    public AmsServiceTarget ResolveControlPlane(PowerPlatformEnvironment homeEnvironment, Uri trustedGlobalEndpoint) => Resolve(ControlPlane, homeEnvironment, trustedGlobalEndpoint, AmsRouteForm.TenantHost);

    /// <summary>The trusted tenant-scoped endpoint control-plane calls use without an override.</summary>
    /// <param name="target">The Power Platform API ring the CLI targets.</param>
    public static Uri TrustedControlPlaneEndpoint(PowerPlatformApiTargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return PowerPlatformCloudMap.ForTarget(target).RequireTrusted(target.GlobalEndpoint, "Agent Management Service control plane");
    }

    private static AmsEndpointOverride? Read(Func<string, string?> readVariable, string endpointVariable, string audienceVariable)
    {
        var endpoint = readVariable(endpointVariable);
        var audience = readVariable(audienceVariable);

        return string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(audience) ? null : new AmsEndpointOverride(endpointVariable, endpoint, audience);
    }

    private static AmsServiceTarget Resolve(AmsEndpointOverride? endpointOverride, PowerPlatformEnvironment environment, Uri defaultEndpoint, AmsRouteForm defaultRouteForm)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(defaultEndpoint);

        return endpointOverride is null
            ? new AmsServiceTarget { Endpoint = defaultEndpoint, Audience = environment.ApiAudience, RouteForm = defaultRouteForm, TenantId = environment.TenantId, EnvironmentId = environment.EnvironmentId }
            : new AmsServiceTarget { Endpoint = Validate(endpointOverride), Audience = string.IsNullOrWhiteSpace(endpointOverride.Audience) ? environment.ApiAudience : endpointOverride.Audience.Trim(), RouteForm = AmsRouteForm.Direct, TenantId = environment.TenantId, EnvironmentId = environment.EnvironmentId };
    }

    private static Uri Validate(AmsEndpointOverride endpointOverride)
    {
        var subject = $"{endpointOverride.EndpointVariable}:";
        if (string.IsNullOrWhiteSpace(endpointOverride.Endpoint))
        {
            throw AmsAgentException.EndpointOverrideNotUsable($"{subject} is not set, but its audience variable is.", "Set the endpoint too, or unset the audience variable.");
        }

        var endpoint = AmsEndpointValidation.RequireHttps(endpointOverride.Endpoint, subject, subject, AmsAgentException.EndpointOverrideNotUsable);
        return string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) ? endpoint : throw AmsAgentException.EndpointOverrideNotUsable($"{subject} carries a query or fragment.", "Supply the endpoint's scheme, host and optional path only.");
    }
}
