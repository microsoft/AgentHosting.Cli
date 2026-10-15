namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// A resolved authority: the login host + tenant an MSAL request is issued against, plus the cloud it
/// belongs to. Adapted from PAC's <c>bolt.authentication.AuthorityInfo</c>. Unlike PAC, the
/// demonstrator does not carry a Dataverse-URL-derived authority; its ABS targets use cloud and
/// tenant only because ARM does not require a Dataverse challenge probe.
/// </summary>
public sealed record AuthorityInfo
{
    /// <summary>The absolute HTTPS authority URL (login host + tenant segment).</summary>
    public required Uri AuthorityUrl { get; init; }

    /// <summary>The cloud the authority host belongs to.</summary>
    public required CloudInstance Cloud { get; init; }

    /// <summary>
    /// The pinned tenant, when the authority targets a specific tenant; <see langword="null"/> when the
    /// authority uses the <c>organizations</c> pseudo-tenant.
    /// </summary>
    public Guid? TenantId { get; init; }
}
