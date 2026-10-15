using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal static class PublicClientApplicationExtensions
{
    public static Task<IEnumerable<IAccount>> GetAccountsAsync(this IPublicClientApplication client, CancellationToken cancellationToken) => ((ClientApplicationBase)client).GetAccountsAsync(cancellationToken);
}
