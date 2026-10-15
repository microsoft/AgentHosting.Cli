using Microsoft.Agents.Hosting.Cli.Core.Auth;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The resolved, non-secret configuration the MSAL adapters need, assembled at the composition root
/// from the process environment. Core never reads the environment; the host resolves
/// <c>AH_CLI_CLIENT_ID</c>, <c>AH_CLI_CONFIG_DIR</c>,
/// and <c>AH_CLI_ALLOW_UNPROTECTED_CACHE</c> and passes their values here. It carries no token or
/// secret material.
/// </summary>
public sealed record AgentHostAuthOptions
{
    /// <summary>Raw <c>AH_CLI_CLIENT_ID</c> value (validated lazily by the client-app configuration).</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Raw <c>AH_CLI_GRAPH_CLIENT_ID</c> value: the consent-capable public client used to acquire
    /// Microsoft Graph tokens for identity provisioning (delegated consent-on-first-use). When
    /// <see langword="null"/> the adapter falls back to <c>GraphDelegatedConsent.DefaultGraphClientId</c>
    /// (the Microsoft Graph Command Line Tools client — Microsoft-preauthorized for Graph). It is
    /// deliberately separate from
    /// <see cref="ClientId"/> because Graph consent must not be requested against the CLI first-party app.
    /// </summary>
    public string? GraphClientId { get; init; }
    public string? GraphRedirectUri { get; init; }

    /// <summary>
    /// Raw <c>AH_CLI_ARM_CLIENT_ID</c> value: the consent-capable public client used to acquire Azure
    /// Resource Manager tokens for managed-identity provisioning (the ARM counterpart of
    /// <see cref="GraphClientId"/>). When <see langword="null"/> the adapter falls back to
    /// <c>ArmDelegatedConsent.DefaultArmClientId</c> (the well-known Azure CLI public client,
    /// Microsoft-preauthorized for ARM). It is separate from <see cref="ClientId"/> because ARM, like
    /// Graph, cannot be consented against the CLI first-party app.
    /// </summary>
    public string? ArmClientId { get; init; }
    public string? ArmRedirectUri { get; init; }

    /// <summary>The resolved AgentHost config directory the profile store and token cache live under.</summary>
    public required string ConfigDirectory { get; init; }

    /// <summary>The resolved <c>AH_CLI_ALLOW_UNPROTECTED_CACHE</c> opt-in for unprotected cache persistence.</summary>
    public bool AllowUnprotectedCache { get; init; }

    /// <summary>The target cloud. Public is the only ABS target in this demonstrator.</summary>
    public CloudInstance Cloud { get; init; } = CloudInstance.Public;

    /// <summary>Whether interactive identity UI is allowed (false under <c>--non-interactive</c>).</summary>
    public bool InteractionAllowed { get; init; } = true;

    /// <summary>The writer that receives sign-in diagnostics under <c>--debug</c>, or <see langword="null"/> to stay silent.</summary>
    public TextWriter? DebugOutput { get; init; }

    /// <summary>The AgentHost auth profile file name, stored under <see cref="ConfigDirectory"/>.</summary>
    public const string ProfilesFileName = "auth-profiles-csharp.json";

    /// <summary>
    /// The active-environment selection file name, stored under <see cref="ConfigDirectory"/>. It is
    /// kept separate from the profile store because environment selection is target state, not
    /// identity state, and the two have different lifetimes.
    /// </summary>
    public const string EnvironmentSelectionFileName = "environment-selection-csharp.json";
}
