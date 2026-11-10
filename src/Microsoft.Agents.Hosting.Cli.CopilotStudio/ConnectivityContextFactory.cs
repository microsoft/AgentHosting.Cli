using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

internal sealed class ConnectivityContextFactory(ITokenProvider tokenProvider, PowerPlatformApiTargetConfig apiTarget)
{
    private static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();

    private TokenAcquisitionResult? _cachedToken;

    private string? _cachedTokenTenantId;

    /// <summary>Creates the context for an environment, reusing the token cached for its tenant until shortly before it expires.</summary>
    public async Task<PowerAppsContext> CreateAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return new PowerAppsContext { AccessToken = await AccessTokenAsync(environment.TenantId, cancellationToken).ConfigureAwait(false), EnvironmentId = environment.EnvironmentId, PowerPlatformApiEndpoint = PowerPlatformApiDiscovery.GetEnvironmentEndpoint(apiTarget, environment.EnvironmentId) };
    }

    private async Task<string> AccessTokenAsync(string? tenantId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedToken is { } cached && string.Equals(_cachedTokenTenantId, tenantId, StringComparison.OrdinalIgnoreCase) && cached.ExpiresOn - TokenRenewalMargin > DateTimeOffset.UtcNow)
            {
                return cached.AccessToken;
            }
        }

        var acquired = await tokenProvider.AcquireTokenSilentAsync(apiTarget.Audience, tenantId, cancellationToken).ConfigureAwait(false);
        var token = AcquiredTokenTenant.Require(acquired, tenantId);

        lock (_gate)
        {
            _cachedToken = acquired;
            _cachedTokenTenantId = tenantId;
        }

        return token;
    }
}
