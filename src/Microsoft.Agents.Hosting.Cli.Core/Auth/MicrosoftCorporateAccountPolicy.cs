using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// PAC's corporate-account protection, adapted to the demonstrator. PAC's
/// <c>AuthTokenStore.AcquireTokenForPublicClientFlow</c> blocks a
/// <b>browser (non-broker) interactive</b> result when the account is a Microsoft employee — a user in
/// the MSIT tenant (<c>72f988bf-86f1-41af-91ab-2d7cd011db47</c>) whose UPN ends with
/// <c>@microsoft.com</c> — because such requests must go through the OS broker (WAM). Broker and
/// device-code flows are exempt.
///
/// This is pure so the enforcement is unit-testable with a fake account and no token acquisition.
/// </summary>
public static class MicrosoftCorporateAccountPolicy
{
    /// <summary>
    /// The Microsoft (MSIT) corporate tenant id. From PAC's
    /// <c>AuthTokenStore.MicrosoftTenantId</c>.
    /// </summary>
    public const string MicrosoftTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    private const string CorporateDomainSuffix = "@microsoft.com";

    /// <summary>
    /// Whether <paramref name="account"/> is a Microsoft corporate account (MSIT tenant +
    /// <c>@microsoft.com</c> UPN). The comparison mirrors PAC: tenant id compared ordinal
    /// case-insensitively, UPN suffix matched case-insensitively.
    /// </summary>
    public static bool IsMicrosoftCorporateAccount(AuthAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var tenantMatches = string.Equals(account.TenantId, MicrosoftTenantId, StringComparison.OrdinalIgnoreCase);
        var domainMatches = account.Username?.EndsWith(CorporateDomainSuffix, StringComparison.OrdinalIgnoreCase) == true;

        return tenantMatches && domainMatches;
    }

    /// <summary>
    /// Enforces the protection for a completed login. When <paramref name="flow"/> is the browser
    /// interactive flow and <paramref name="account"/> is a Microsoft corporate account, throws
    /// <see cref="CorporateAccountRequiresBrokerException"/>. Broker and device-code flows return
    /// without a check.
    /// </summary>
    public static void EnforceAfterLogin(AuthLoginFlow flow, AuthAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (flow != AuthLoginFlow.BrowserInteractive)
        {
            return;
        }

        if (IsMicrosoftCorporateAccount(account))
        {
            throw new CorporateAccountRequiresBrokerException(
                "A Microsoft corporate account was signed in through the system browser. Microsoft " +
                "employees must authenticate through the OS broker (WAM), not the browser.");
        }
    }
}
