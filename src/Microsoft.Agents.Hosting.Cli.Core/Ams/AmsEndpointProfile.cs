using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Resolves the endpoint, token audience and route shape of every Agent Management Service call, honoring the host's test-only endpoint overrides.</summary>
public sealed record AmsEndpointProfile
{
    /// <summary>The host variable that points data-plane calls at a direct service endpoint.</summary>
    public const string DataPlaneEndpointVariable = "AH_CLI_AMSDP_ENDPOINT";

    /// <summary>The host variable that points control-plane calls at a direct service endpoint.</summary>
    public const string ControlPlaneEndpointVariable = "AH_CLI_AMS_ENDPOINT";

    /// <summary>The data-plane endpoint override as supplied, or null to use the environment's Power Platform API host.</summary>
    public string? DataPlaneEndpoint { get; init; }

    /// <summary>The control-plane endpoint override as supplied, or null to use the ring's trusted Power Platform API endpoint.</summary>
    public string? ControlPlaneEndpoint { get; init; }

    /// <summary>Reads both overrides through the host's variable reader, leaving unset ones on the Power Platform API.</summary>
    public static AmsEndpointProfile FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        return new AmsEndpointProfile { DataPlaneEndpoint = DeploymentRequestValidator.Trim(readVariable(DataPlaneEndpointVariable)), ControlPlaneEndpoint = DeploymentRequestValidator.Trim(readVariable(ControlPlaneEndpointVariable)) };
    }

    /// <summary>Returns the trusted tenant-scoped endpoint that control-plane calls use without an override.</summary>
    public static Uri TrustedControlPlaneEndpoint(PowerPlatformApiTargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return PowerPlatformCloudMap.ForTarget(target).RequireTrusted(target.GlobalEndpoint, "Agent Management Service control plane");
    }

    /// <summary>Resolves the endpoint of a data-plane call in the agent's home environment.</summary>
    public AmsServiceTarget ResolveDataPlane(PowerPlatformEnvironment homeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(homeEnvironment);

        return Resolve(DataPlaneEndpoint, DataPlaneEndpointVariable, homeEnvironment, homeEnvironment.ApiEndpoint, AmsRouteForm.EnvironmentHost);
    }

    /// <summary>Resolves the endpoint of a control-plane call addressed through the agent's home environment.</summary>
    public AmsServiceTarget ResolveControlPlane(PowerPlatformEnvironment homeEnvironment, Uri trustedControlPlaneEndpoint)
    {
        ArgumentNullException.ThrowIfNull(homeEnvironment);
        ArgumentNullException.ThrowIfNull(trustedControlPlaneEndpoint);

        return Resolve(ControlPlaneEndpoint, ControlPlaneEndpointVariable, homeEnvironment, trustedControlPlaneEndpoint, AmsRouteForm.TenantHost);
    }

    private static AmsServiceTarget Resolve(string? endpointOverride, string endpointVariable, PowerPlatformEnvironment homeEnvironment, Uri defaultEndpoint, AmsRouteForm defaultRouteForm)
    {
        var overrideEndpoint = string.IsNullOrWhiteSpace(endpointOverride) ? null : Validate(endpointOverride, endpointVariable);

        return new AmsServiceTarget { Endpoint = overrideEndpoint ?? defaultEndpoint, Audience = homeEnvironment.ApiAudience, RouteForm = overrideEndpoint is null ? defaultRouteForm : AmsRouteForm.Direct, TenantId = homeEnvironment.TenantId, EnvironmentId = homeEnvironment.EnvironmentId, Context = homeEnvironment.Context };
    }

    private static Uri Validate(string endpointOverride, string endpointVariable)
    {
        if (!Uri.TryCreate(endpointOverride.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw AgentDeployException.EndpointNotUsable($"{endpointVariable} is not an absolute uri.", "Supply an absolute https endpoint, for example https://<host>.");
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentDeployException.EndpointNotUsable($"{endpointVariable} uses scheme '{endpoint.Scheme}', and a delegated token is only ever sent over https.", "Supply an https endpoint.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw AgentDeployException.EndpointNotUsable($"{endpointVariable} carries embedded user information.", "Supply an https endpoint with no embedded credentials.");
        }

        return string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) ? endpoint : throw AgentDeployException.EndpointNotUsable($"{endpointVariable} carries a query or fragment.", "Supply the endpoint's scheme, host and optional path only.");
    }
}
