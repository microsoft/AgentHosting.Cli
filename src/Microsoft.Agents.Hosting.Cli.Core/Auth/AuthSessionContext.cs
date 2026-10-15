namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The resolved, non-secret account context the <see cref="Operations.AgentHostingOperations"/> facade
/// records on a profile when a login succeeds. The host resolves these values at the composition root
/// (client id from <c>AH_CLI_CLIENT_ID</c>, cloud, and the default authority) and injects them. Core
/// never reads the environment itself, and this record carries no token or secret material.
/// </summary>
public sealed record AuthSessionContext
{
    /// <summary>The AgentHost public-client application id the session was created under, when configured.</summary>
    public string? ClientId { get; init; }

    /// <summary>The cloud the session targets. Public cloud is the only ABS target in this demonstrator.</summary>
    public CloudInstance Cloud { get; init; } = CloudInstance.Public;

    /// <summary>The default authority URL the login was homed against, when known.</summary>
    public string? Authority { get; init; }
}
