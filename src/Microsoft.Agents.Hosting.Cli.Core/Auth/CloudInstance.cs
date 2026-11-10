namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The sovereign/national cloud an authority targets. Adapted from PAC's
/// <c>bolt.system.CloudInstance</c>: the CLI keeps the
/// cloud-to-login-host abstraction rather than hard-coding the public login host throughout the code,
/// so authorities are resolved by <see cref="Microsoft.Agents.Hosting.Cli.Core.Auth.AgentHostAuthorityResolver"/>.
///
/// Public-cloud <c>Preprod</c> and <c>Test</c> are represented separately by
/// <see cref="Microsoft.Agents.Hosting.Cli.Core.Environments.PowerPlatformApiRing"/>.
/// </summary>
public enum CloudInstance
{
    /// <summary>Azure commercial (public) cloud.</summary>
    Public,

    /// <summary>US Government (GCC) cloud.</summary>
    UsGov,

    /// <summary>US Government High (GCC High) cloud.</summary>
    UsGovHigh,

    /// <summary>US Government DoD cloud.</summary>
    UsGovDod,

    /// <summary>Azure China (operated by 21Vianet).</summary>
    China,

    /// <summary>United States national cloud.</summary>
    UsNat,

    /// <summary>United States secure cloud.</summary>
    UsSec,
}
