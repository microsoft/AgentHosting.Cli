using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// Maps an MSAL <see cref="IAccount"/> / <see cref="AuthenticationResult"/> to the token-free
/// <see cref="AuthAccount"/> port record. The stable switch key is the MSAL home-account id identifier
/// (<c>HomeAccountId.Identifier</c>); the home tenant is <c>HomeAccountId.TenantId</c>. No token
/// material is copied.
/// </summary>
internal static class AuthAccountMapper
{
    public static AuthAccount FromAccount(IAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AuthAccount
        {
            Username = account.Username,
            HomeAccountId = account.HomeAccountId?.Identifier ?? string.Empty,
            TenantId = account.HomeAccountId?.TenantId,
        };
    }

    public static AuthAccount FromResult(
        AuthenticationResult result,
        IAuthenticationResultObserver? observer = null,
        string? clientId = null) =>
        FromResult(
            result,
            AuthenticationResultTenantObserver.BeginObservation(observer),
            clientId);

    public static AuthAccount FromResult(
        AuthenticationResult result,
        AuthenticationResultObservation? observation,
        string? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        AuthAccount account = FromAccount(result.Account);
        AuthenticatedTenantContext? context =
            AuthenticationResultTenantObserver.Observe(
                result,
                observation,
                clientId);
        return context is { } authenticated
            ? account with
            {
                AuthenticatedTenantId = authenticated.TenantId,
                PrincipalObjectId = authenticated.PrincipalObjectId,
                AuthenticatedClientId = authenticated.ClientId,
            }
            : account;
    }
}
