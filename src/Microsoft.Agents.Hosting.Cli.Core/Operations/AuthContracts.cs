using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Scoped operator authentication request that the command host populates from the global and auth flags and passes to <see cref="IAgentHostingOperations.LoginAsync"/>.</summary>
public sealed record AuthLoginRequest
{
    /// <summary>Account UPN/email used as a login hint (from <c>--account</c>).</summary>
    public string? Account { get; init; }

    /// <summary>Explicit target tenant id (from <c>--tenant</c>); <see langword="null"/> uses the default authority.</summary>
    public string? Tenant { get; init; }

    /// <summary>Resource/audience the token targets (from <c>--resource</c>); defaults to Azure Resource Manager.</summary>
    public string? Resource { get; init; }

    /// <summary>
    /// Explicit delegated scopes requested at login. Mutually exclusive with <see cref="Resource"/>;
    /// values are passed to MSAL unchanged.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Request the device-code flow (from <c>--device-code</c>).</summary>
    public bool DeviceCode { get; init; }

    /// <summary>Forbid any interactive identity UI (from <c>--non-interactive</c>).</summary>
    public bool NonInteractive { get; init; }

    /// <summary>
    /// The Power Platform API ring this sign-in targets (from <c>--cloud</c>). When absent the
    /// profile keeps the ring it already had, and a new profile defaults to production.
    /// </summary>
    public PowerPlatformApiRing? Ring { get; init; }
}

/// <summary>Result of a completed <see cref="IAgentHostingOperations.LoginAsync"/> call.</summary>
public sealed record AuthLoginResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (for example <c>signed-in</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The signed-in account UPN/email, when known.</summary>
    public string? Account { get; init; }

    /// <summary>The tenant the acquired session is homed in, when known.</summary>
    public string? TenantId { get; init; }

    /// <summary>The Power Platform API ring this sign-in targets.</summary>
    public string? Cloud { get; init; }

    /// <summary>Whether a selected environment was discarded because the active account or cloud changed.</summary>
    public bool EnvironmentCleared { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
        ClearedEnvironmentNotice.Append(
            string.IsNullOrWhiteSpace(Cloud) ? $"Signed in as {Account}." : $"Signed in as {Account} ({Cloud}).",
            EnvironmentCleared);
}

/// <summary>
/// Non-interactive report of the current sign-in state. Producing this never acquires a token or
/// opens identity UI.
/// </summary>
public sealed record AuthStatusReport : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (for example <c>signed-in</c> / <c>signed-out</c>).</summary>
    public required string Status { get; init; }

    /// <summary>Whether an active account is present in the cache.</summary>
    public bool SignedIn { get; init; }

    /// <summary>The active account UPN/email, when signed in.</summary>
    public string? Account { get; init; }

    /// <summary>The active account's home tenant, when signed in.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The active environment id, when one is selected. Environment selection is durable CLI state, so
    /// status is where an operator confirms which environment later commands will target.
    /// </summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The active environment's display name, when one is selected.</summary>
    public string? EnvironmentDisplayName { get; init; }

    /// <summary>The active environment's tenant, when one is selected.</summary>
    public string? EnvironmentTenantId { get; init; }

    /// <summary>
    /// Whether the selected environment is still bound to the active account's tenant:
    /// <see langword="null"/> when no environment is selected, and <see langword="false"/> when a
    /// selection exists that no command will accept. Reporting the selection without this flag would
    /// show an unusable target as a healthy one.
    /// </summary>
    public bool? EnvironmentTenantBound { get; init; }

    /// <summary>The Power Platform API ring the active account targets.</summary>
    public string? Cloud { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            SignedIn ? $"Signed in as {Account} (tenant {TenantId})." : "Not signed in.",
            $"Cloud: {Cloud ?? "prod"}.",
            EnvironmentId is null
                ? "No environment selected."
                : $"Active environment: {EnvironmentDisplayName} ({EnvironmentId}).",
        };

        if (EnvironmentId is not null && EnvironmentTenantBound == false)
        {
            lines.Add(SignedIn
                ? $"  Not bound to the signed-in account: the environment is in tenant {Describe(EnvironmentTenantId)} and the account is in tenant {Describe(TenantId)}. Commands that need an environment will fail until you sign in to that tenant or select another environment."
                : "  Not bound to an account: sign in before running a command that needs an environment.");
        }

        return lines;
    }

    private static string Describe(string? value) => string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
}

/// <summary>Summary of the active profile after a switch. Contains no token material.</summary>
public sealed record AuthProfileSummary : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (for example <c>switched</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The now-active account UPN/email or home account id.</summary>
    public string? Account { get; init; }

    /// <summary>The now-active account's home tenant, when known.</summary>
    public string? TenantId { get; init; }

    /// <summary>The Power Platform API ring the now-active account targets.</summary>
    public string? Cloud { get; init; }

    /// <summary>Whether a selected environment was discarded because the active account or cloud changed.</summary>
    public bool EnvironmentCleared { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
        ClearedEnvironmentNotice.Append(
            string.IsNullOrWhiteSpace(Cloud) ? $"Active account: {Account}." : $"Active account: {Account} ({Cloud}).",
            EnvironmentCleared);
}

/// <summary>The line every account-changing command uses to report a discarded environment selection.</summary>
internal static class ClearedEnvironmentNotice
{
    public const string Text =
        "Cleared the selected environment because the active account or cloud changed. Run `msagent env select` to choose one.";

    public static IReadOnlyList<string> Append(string headline, bool environmentCleared) =>
        environmentCleared ? [headline, Text] : [headline];
}

/// <summary>Result of clearing the local session and cached accounts.</summary>
public sealed record AuthLogoutResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (for example <c>signed-out</c>).</summary>
    public required string Status { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => ["Signed out."];
}

/// <summary>The git hosts <c>msagent auth setup-git</c> pointed git's credential helper at.</summary>
public sealed record AuthSetupGitResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Always git-configured.</summary>
    public string Status { get; init; } = "git-configured";

    /// <summary>The host patterns git signs in to with msagent.</summary>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => ["Configured git to sign in with msagent.", $"  hosts: {string.Join(", ", Hosts)}"];
}

/// <summary>The credential lines <c>msagent auth git-credential</c> answers git with, which never appear in a JSON envelope.</summary>
public sealed record AuthGitCredentialResult : IRawOutputResult
{
    /// <summary>The key=value lines git reads, empty when msagent has no credential for the request.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> CredentialLines { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => CredentialLines;
}
