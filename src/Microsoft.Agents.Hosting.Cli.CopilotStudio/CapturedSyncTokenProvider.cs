using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

// Generic Sync credentials are not PPAPI validated resource/product-principal tokens.
internal sealed class CapturedSyncTokenProvider(
    IProfileCredentialProvider credentials,
    EnvironmentResolutionContext context) : ITokenProvider
{
    internal static ITokenProvider For(ITokenProvider legacy, IProfileCredentialProvider? credentials, EnvironmentResolutionContext? context)
    {
        if (context is null)
            return legacy;
        PowerAppsServiceTarget.RequireContext(context);
        return new CapturedSyncTokenProvider(credentials ?? throw new TokenClaimException(
            "The captured service operation requires a profile credential provider.", TokenClaimFailure.WrongPrincipalMode), context);
    }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? tenantId, CancellationToken cancellationToken)
    {
        var scope = PacResourceScopeNormalizer.ForProfile(context.Profile).ToDefaultScope(resource);
        return AcquireTokenSilentAsync([scope], tenantId, cancellationToken);
    }

    public async Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? tenantId, CancellationToken cancellationToken)
    {
        PowerAppsServiceTarget.RequireContext(context);
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new TokenClaimException("The captured service operation requires an explicit tenant.", TokenClaimFailure.WrongTenant);
        var acquisition = await credentials.AcquireAsync(context.Profile, scopes, tenantId, cancellationToken).ConfigureAwait(false);
        AcquiredTokenTenant.Require(acquisition, tenantId);
        return acquisition;
    }

    internal static PowerPlatformCloudMap CloudMap(CopilotEnvironmentContext environment, PowerPlatformCloudMap legacy)
    {
        if (environment.AgentManagementUrl is { IsAbsoluteUri: true } gateway
            && (gateway.Query.Length != 0 || gateway.Fragment.Length != 0))
            throw new EndpointTrustException("The Sync gateway must not contain a query or URI fragment.");
        if (environment.Context is not { } context)
            return legacy;
        PowerAppsServiceTarget.RequireContext(context);
        return PowerPlatformCloudMap.ForTarget(context.Target);
    }

    internal static void RequireUser(CopilotEnvironmentContext environment)
    {
        if (environment.Context is { } context)
        {
            PowerAppsServiceTarget.RequireContext(context);
            if (context.Profile.PrincipalType != AuthPrincipalType.User)
                throw new UserPrincipalRequiredException();
            _ = AgentHostSyncAuthProvider.IslandAudienceFor(PowerAppsServiceTarget.ClusterFor(context));
        }
    }
}
