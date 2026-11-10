namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The route shape an Agent Management Service endpoint expects.</summary>
public enum AmsRouteForm
{
    /// <summary>The environment-scoped Power Platform API front door, which injects the tenant and environment route segments.</summary>
    EnvironmentHost,

    /// <summary>The tenant-scoped Power Platform API host, which needs the environment route segment.</summary>
    TenantHost,

    /// <summary>A direct service or gateway endpoint, which needs the tenant and environment route segments.</summary>
    Direct,
}

/// <summary>One resolved Agent Management Service endpoint with the token audience and route shape it expects.</summary>
public sealed record AmsServiceTarget
{
    /// <summary>The endpoint every request of the call is sent to.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The token audience the endpoint accepts.</summary>
    public required string Audience { get; init; }

    /// <summary>The route shape the endpoint expects.</summary>
    public required AmsRouteForm RouteForm { get; init; }

    /// <summary>The tenant that owns the environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The environment the route addresses, which is the agent's home environment.</summary>
    public required string EnvironmentId { get; init; }
}
