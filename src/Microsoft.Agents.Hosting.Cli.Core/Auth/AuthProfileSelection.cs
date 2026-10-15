using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>How a stored active profile reconciles against the accounts visible in the MSAL cache.</summary>
public enum ActiveProfileState
{
    /// <summary>No active profile is selected — the operator is signed out.</summary>
    None,

    /// <summary>The active profile's account is present in the MSAL cache.</summary>
    Active,

    /// <summary>The active profile points at an account that is no longer in the MSAL cache.</summary>
    Stale,
}

/// <summary>The result of reconciling the active profile against the MSAL cache.</summary>
public sealed record ActiveProfileReconciliation(ActiveProfileState State, AuthProfile? Profile);

/// <summary>
/// Pure profile-selection and reconciliation logic, isolated from persistence so it is unit-testable
/// without any file or MSAL dependency. It adapts the selection semantics of PAC's
/// <c>AuthProfilesManagerBase</c> to the demonstrator's single-active profile model.
/// </summary>
public static class AuthProfileSelection
{
    /// <summary>
    /// The <see cref="AuthProfile.FlowType"/> value stamped for an OS-broker (WAM) login. Kept in
    /// sync with the login flow mapping (<c>AgentHostingOperations.ToFlowType</c>) and the silent
    /// token path (<c>MsalSilentTokenProvider</c>).
    /// </summary>
    private const string BrokerFlowType = "broker";

    private const string DeviceCodeFlowType = "device-code";

    /// <summary>
    /// Resolves a switch selector to a single stored profile's account key. A selector that exactly
    /// matches a home-account id wins outright (the durable key); otherwise it is matched
    /// case-insensitively against usernames. Multiple username matches are an ambiguity error.
    /// </summary>
    /// <returns>The resolved <see cref="AuthProfile.AccountKey"/>.</returns>
    public static string ResolveAccountKey(IReadOnlyList<AuthProfile> profiles, string selector)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new ArgumentException("Account selector is required.", nameof(selector));
        }

        var trimmed = selector.Trim();

        // Exact home-account-id match is authoritative.
        var byKey = profiles.FirstOrDefault(p => string.Equals(p.AccountKey, trimmed, StringComparison.Ordinal));
        if (byKey is not null)
        {
            return byKey.AccountKey;
        }

        var byUsername = profiles
            .Where(p => string.Equals(p.Username, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byUsername.Count switch
        {
            1 => byUsername[0].AccountKey,
            0 => throw new AuthenticationException(
                $"No AgentHost profile matches account '{selector}'.",
                ExitCodes.Usage,
                remediation: "Run `msagent auth status` to list stored accounts."),
            _ => throw new AuthenticationException(
                $"Account '{selector}' matches {byUsername.Count} stored profiles; specify the exact home-account id instead.",
                ExitCodes.Usage,
                remediation: "Run `msagent auth status` to see each account's home-account id, then `msagent auth switch` with that id."),
        };
    }

    /// <summary>
    /// True when the active profile was established through the OS broker (WAM). A broker/OS-account
    /// profile holds its account in the OS broker, not the enumerable MSAL file cache — so
    /// <c>GetAccountsAsync()</c> on a non-broker client structurally cannot see it. This mirrors PAC's
    /// <c>AuthTokenStore</c>, which segregates <c>OperatingSystem</c>
    /// profiles and never enumerates accounts for them (it uses the <c>OperatingSystemAccount</c>
    /// sentinel). <c>auth status</c> therefore trusts a broker profile rather than reconciling it
    /// against an enumeration that would falsely report <c>stale</c> after a successful broker login;
    /// silent acquisition still succeeds via the OS-account fallback (<c>MsalSilentTokenProvider</c>).
    /// </summary>
    public static bool IsBrokerProfile(AuthProfile? profile) =>
        profile is not null && string.Equals(profile.FlowType, BrokerFlowType, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeviceCodeProfile(AuthProfile? profile) =>
        profile is not null && string.Equals(profile.FlowType, DeviceCodeFlowType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="selector"/> names the account whose home-account id is exactly <paramref name="homeAccountId"/> or whose username is <paramref name="username"/> in any case.</summary>
    public static bool NamesAccount(string? selector, string? homeAccountId, string? username) =>
        !string.IsNullOrWhiteSpace(selector)
        && (string.Equals(homeAccountId, selector, StringComparison.Ordinal) || string.Equals(username, selector, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reconciles the active profile against the accounts currently visible in the MSAL cache. This is
    /// how <c>auth status</c> reports a stale pointer explicitly rather than silently claiming
    /// "signed out." Only browser and device-code profiles reach this path;
    /// broker profiles are trusted without enumeration (see <see cref="IsBrokerProfile"/>).
    /// </summary>
    public static ActiveProfileReconciliation Reconcile(
        AuthProfile? activeProfile,
        IReadOnlyList<AuthAccount> cacheAccounts)
    {
        ArgumentNullException.ThrowIfNull(cacheAccounts);

        if (activeProfile is null)
        {
            return new ActiveProfileReconciliation(ActiveProfileState.None, null);
        }

        var present = cacheAccounts.Any(a =>
            string.Equals(a.HomeAccountId, activeProfile.AccountKey, StringComparison.Ordinal));

        return present
            ? new ActiveProfileReconciliation(ActiveProfileState.Active, activeProfile)
            : new ActiveProfileReconciliation(ActiveProfileState.Stale, activeProfile);
    }
}
