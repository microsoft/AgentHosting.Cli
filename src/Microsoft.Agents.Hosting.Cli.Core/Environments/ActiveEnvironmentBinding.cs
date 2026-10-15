using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// The persisted environment selection together with whether it is still bound to the account that is
/// signed in <em>now</em>.
///
/// <para>
/// The two are reported together because they are only meaningful together. A selection whose tenant
/// no longer matches the active account is not a selection any command can use, but it is also not
/// nothing: it is what the operator chose, and it is the only way to explain why commands are
/// failing. Status shows it and says it is unbound; commands refuse it.
/// </para>
/// </summary>
public sealed record ActiveEnvironmentBinding
{
    /// <summary>The persisted selection, whose endpoint has already been revalidated.</summary>
    public required ActiveEnvironment Environment { get; init; }

    /// <summary>Whether any account is signed in at all.</summary>
    public required bool IsSignedIn { get; init; }

    /// <summary>The active account's username, when one is signed in.</summary>
    public string? AccountUsername { get; init; }

    /// <summary>The active account's tenant, when one is signed in.</summary>
    public string? AccountTenantId { get; init; }

    /// <summary>
    /// Whether the selection's tenant matches the active account's tenant. A blank tenant on either
    /// side is unbound rather than bound: a comparison that passes because it lacks information is not
    /// a check.
    /// </summary>
    public required bool IsBound { get; init; }
}
