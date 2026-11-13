using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Scope selection belongs to the resource boundary, not to the credential provider.</summary>
public sealed class ProfileResourceTokenProvider : IProfileResourceTokenProvider
{
    private readonly IProfileCredentialProvider _credentials;
    private readonly PpapiTokenClaimsValidator _validator;

    public ProfileResourceTokenProvider(IProfileCredentialProvider credentials, PpapiTokenClaimsValidator? validator = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _validator = validator ?? new();
    }

    public async Task<ValidatedResourceToken> AcquireAsync(
        EnvironmentResolutionContext context, string audience, string tenantId,
        IReadOnlyList<string> requiredPermissions, CancellationToken cancellationToken,
        IReadOnlyList<string>? explicitScopes = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var target = PowerPlatformApiTargetSelection.Require(context.Target);
        if (context.Profile.Cloud != target.Cloud || context.Profile.Ring != target.Ring)
            throw new CloudRetargetedException("The captured profile and resource target disagree.");
        if (!string.Equals(audience.TrimEnd('/'), target.Audience.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new TokenClaimException("The requested audience does not match the captured resource target.", TokenClaimFailure.WrongAudience);
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new TokenClaimException("The resource tenant must be explicitly captured.", TokenClaimFailure.WrongTenant);
        var defaultOnly = context.Profile.PrincipalType == AuthPrincipalType.ServicePrincipal
            || context.Profile.CredentialMethod == AuthCredentialMethod.DefaultAzureCredential;
        IReadOnlyList<string> scopes = explicitScopes ?? (defaultOnly
            ? [audience.TrimEnd('/') + "/.default"]
            : new PpapiScopeBuilder().DefaultScopes(audience));
        var token = await _credentials.AcquireAsync(context.Profile, scopes, tenantId, cancellationToken).ConfigureAwait(false);
        var claims = _validator.ValidateForProfile(token.AccessToken, audience, tenantId, context.Profile, requiredPermissions);
        return new(token, claims, context, tenantId);
    }
}
