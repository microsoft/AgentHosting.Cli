using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;
using CoreCluster = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Supplies the sync library with bearer tokens for whichever audience it asks for, by delegating to
/// the CLI's existing silent token provider.
/// </summary>
public sealed class AgentHostSyncAuthProvider : ISyncAuthProvider
{
    private readonly ITokenProvider _tokenProvider;
    private readonly IProfileCredentialProvider? _profileCredentials;
    private readonly PowerPlatformApiTargetConfig? _legacyTarget;

    /// <summary>Creates a provider backed by <paramref name="tokenProvider"/>.</summary>
    /// <param name="tokenProvider">The CLI's silent, multi-audience token provider.</param>
    /// <param name="profileCredentials">Acquires generic service credentials for captured profiles.</param>
    public AgentHostSyncAuthProvider(ITokenProvider tokenProvider, IProfileCredentialProvider? profileCredentials = null)
        : this(tokenProvider, profileCredentials, null)
    {
    }

    internal AgentHostSyncAuthProvider(ITokenProvider tokenProvider, IProfileCredentialProvider? profileCredentials,
        PowerPlatformApiTargetConfig? legacyTarget)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _profileCredentials = profileCredentials;
        _legacyTarget = legacyTarget;
    }

    /// <summary>
    /// Acquires a bearer token for <paramref name="audience"/>, in the tenant the current operation
    /// targets.
    ///
    /// The audience alone does not say which tenant a token should come from. An operator signed in to
    /// several tenants would otherwise be served whichever the cache offered first, so a command aimed
    /// at one tenant's environment could carry another tenant's token. The tenant is read from the
    /// operation's own scope rather than from state on this provider, which is a singleton shared by
    /// every concurrent operation.
    /// </summary>
    /// <param name="audience">The audience the sync library needs a token for.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The bearer token value.</returns>
    public async Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audience);

        var target = DataverseOperationScope.Capture();
        var tenantId = target?.TenantId;
        if (target?.Context is { } context)
        {
            DataverseTargeting.RequireTarget(target);
            var cloud = PowerPlatformCloudMap.ForTarget(context.Target);
            if (!cloud.IsTrustedDataverseOrganization(audience))
            {
                // The bundled SDK's internal IslandControlPlaneService.GetTokenAudience declares
                // resource IDs, not HTTPS destinations. Keep its exact per-cluster resource form.
                var expectedAudience = IslandAudienceFor(PowerAppsServiceTarget.ClusterFor(context)).OriginalString;
                if (!audience.IsAbsoluteUri || !string.Equals(audience.OriginalString, expectedAudience, StringComparison.Ordinal))
                    throw new EndpointTrustException("The Sync resource audience does not match the captured authentication target.");
            }
        }
        else if (_legacyTarget is { } legacyTarget && !PowerPlatformCloudMap.ForTarget(legacyTarget).IsTrustedDataverseOrganization(audience))
        {
            _ = PowerAppsServiceTarget.ClusterFor(legacyTarget);
        }
        var tokens = CapturedSyncTokenProvider.For(_tokenProvider, _profileCredentials, target?.Context);

        return AcquiredTokenTenant.Require(
            await tokens.AcquireTokenSilentAsync(audience.ToString(), tenantId, cancellationToken).ConfigureAwait(false),
            tenantId);
    }

    internal static Uri IslandAudienceFor(CoreCluster cluster) => new(cluster switch
    {
        CoreCluster.Test or CoreCluster.Preprod => "api://a522f059-bb65-47c0-8934-7db6e5286414",
        CoreCluster.Prod => "api://96ff4394-9197-43aa-b393-6a41652e21f8",
        CoreCluster.Gov => "api://9315aedd-209b-43b3-b149-2abff6a95d59",
        CoreCluster.High => "api://69c6e40c-465f-4154-987d-da5cba10734e",
        CoreCluster.DoD => "api://bd4a9f18-e349-4c74-a6b7-65dd465ea9ab",
        _ => throw new EndpointTrustException("The Sync Island resource audience is not verified for this target."),
    });
}
