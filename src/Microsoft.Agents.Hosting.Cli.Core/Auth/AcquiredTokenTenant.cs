using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// Confirms an acquired token belongs to the tenant an operation targets, using the acquisition
/// metadata the Microsoft Authentication Library reports rather than the token's own contents.
///
/// The token stays opaque: a resource validates it, and this client does not. What this prevents is
/// narrower and entirely a client concern — sending a credential minted for one tenant to another
/// tenant's endpoint, which no downstream rejection can undo.
///
/// The library reports no tenant for some acquisitions; there is nothing to compare then, so the
/// token is used as acquired.
/// </summary>
public static class AcquiredTokenTenant
{
    /// <summary>Returns the access token, throwing when the library acquired it for another tenant.</summary>
    public static string Require(TokenAcquisitionResult acquisition, string? expectedTenantId)
    {
        ArgumentNullException.ThrowIfNull(acquisition);

        if (!string.IsNullOrWhiteSpace(expectedTenantId)
            && !string.IsNullOrWhiteSpace(acquisition.TenantId)
            && !string.Equals(acquisition.TenantId.Trim(), expectedTenantId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new TokenClaimException(
                $"The access token was acquired for tenant '{acquisition.TenantId}', but this operation targets tenant '{expectedTenantId}'.",
                TokenClaimFailure.WrongTenant,
                remediation: $"Run `msagent auth create --tenant {expectedTenantId}` for the intended cloud with an account in that tenant.");
        }

        return acquisition.AccessToken;
    }
}
