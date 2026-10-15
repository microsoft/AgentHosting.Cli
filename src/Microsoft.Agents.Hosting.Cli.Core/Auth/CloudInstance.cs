namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The sovereign/national cloud an authority targets. Adapted from PAC's
/// <c>bolt.system.CloudInstance</c>: the demonstrator keeps the
/// cloud-to-login-host abstraction rather than hard-coding the public login host throughout the code,
/// so a future non-public target only touches <see cref="AgentHostAuthorityResolver"/>.
///
/// PAC's Dataverse-only <c>Preprod</c> and <c>Test</c> members are intentionally dropped: this
/// demonstrator's only ABS target is public cloud and it has no Dataverse test rings, so carrying them
/// would imply support the foundation does not provide.
/// </summary>
public enum CloudInstance
{
    /// <summary>Azure commercial (public) cloud. The only ABS target in this demonstrator.</summary>
    Public,

    /// <summary>US Government (GCC) cloud.</summary>
    UsGov,

    /// <summary>US Government High (GCC High) cloud.</summary>
    UsGovHigh,

    /// <summary>US Government DoD cloud.</summary>
    UsGovDod,

    /// <summary>Azure China (operated by 21Vianet).</summary>
    China,
}
