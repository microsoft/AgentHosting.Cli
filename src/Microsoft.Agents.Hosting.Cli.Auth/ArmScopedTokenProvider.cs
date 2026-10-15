using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>Supplies Azure Resource Manager tokens from the ARM-preauthorized consent client, delegating every other resource to the silent provider.</summary>
public sealed class ArmScopedTokenProvider : ITokenProvider
{
    private readonly IArmConsentTokenProvider _armConsentTokenProvider;
    private readonly ITokenProvider _silentTokenProvider;

    /// <summary>Creates the provider.</summary>
    /// <param name="armConsentTokenProvider">Acquires ARM tokens through the consent-capable client.</param>
    /// <param name="silentTokenProvider">Serves every resource other than ARM.</param>
    public ArmScopedTokenProvider(IArmConsentTokenProvider armConsentTokenProvider, ITokenProvider silentTokenProvider)
    {
        _armConsentTokenProvider = armConsentTokenProvider ?? throw new ArgumentNullException(nameof(armConsentTokenProvider));
        _silentTokenProvider = silentTokenProvider ?? throw new ArgumentNullException(nameof(silentTokenProvider));
    }

    /// <inheritdoc />
    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
        IsAzureResourceManager(resource)
            ? _armConsentTokenProvider.AcquireArmTokenAsync(targetTenantId, cancellationToken)
            : _silentTokenProvider.AcquireTokenSilentAsync(resource, targetTenantId, cancellationToken);

    /// <inheritdoc />
    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
        _silentTokenProvider.AcquireTokenSilentAsync(scopes, targetTenantId, cancellationToken);

    private static bool IsAzureResourceManager(string resource) =>
        string.Equals(resource?.TrimEnd('/'), IdentityProvisioningConfig.ArmResource.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
