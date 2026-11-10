using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Resolves a registry user from checked resource-tenant claims, never from home-account metadata.
/// </summary>
public sealed class ManagedAgentPrincipalResolver
{
    private readonly IAuthProfileStore _profiles;
    private readonly ITokenProvider? _tokens;
    private readonly IProfileResourceTokenProvider? _resourceTokens;

    /// <summary>Creates the resolver.</summary>
    /// <param name="profiles">The signed-in profile store.</param>
    /// <param name="tokens">Legacy injected resource-token acquisition for embedded hosts.</param>
    /// <param name="resourceTokens">Explicit profile-bound acquisition for the captured operation.</param>
    public ManagedAgentPrincipalResolver(IAuthProfileStore profiles, ITokenProvider? tokens = null,
        IProfileResourceTokenProvider? resourceTokens = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _tokens = tokens;
        _resourceTokens = resourceTokens;
    }

    /// <summary>Resolves the caller, or <see langword="null"/> when nobody is signed in.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The caller's principal.</returns>
    public async Task<PrincipalReference?> ResolveAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        if (profile is null) return null;
        return await ResolveAsync(new CopilotEnvironmentContext
        {
            EnvironmentId = string.Empty, TenantId = profile.TenantId ?? string.Empty,
            DataverseOrganization = new Uri("https://unused.invalid"),
            Context = new(profile, PowerPlatformApiTargetSelection.TargetFor(profile.Cloud, profile.Ring)),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CopilotEnvironmentContext> BindUserAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        var context = environment.Context
            ?? throw new TokenClaimException("The operation has no captured authentication context.", TokenClaimFailure.MissingObjectId);
        RequireUser(context.Profile);
        if (environment.ResourcePrincipal is { } existing && existing.Context == context
            && string.Equals(existing.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase))
            return environment;

        PpapiTokenClaims claims;
        if (_resourceTokens is not null)
        {
            var token = await _resourceTokens.AcquireAsync(context, context.Target.Audience, environment.TenantId,
                [PowerPlatformApiPermissions.AppsRead], cancellationToken).ConfigureAwait(false);
            if (token.Context != context || !string.Equals(token.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase))
                throw new TokenClaimException("The resource token is bound to another authentication context.", TokenClaimFailure.WrongPrincipalMode);
            claims = token.Claims;
        }
        else if (_tokens is not null)
        {
            if (_tokens is ProfileBoundTokenProvider bound && bound.Context != context)
                throw new TokenClaimException("The token provider is bound to another authentication context.", TokenClaimFailure.WrongPrincipalMode);
            var token = await _tokens.AcquireTokenSilentAsync(context.Target.Audience, environment.TenantId, cancellationToken).ConfigureAwait(false);
            claims = new PpapiTokenClaimsValidator().ValidateForProfile(token.AccessToken, context.Target.Audience,
                environment.TenantId, context.Profile, [PowerPlatformApiPermissions.AppsRead]);
        }
        else
        {
            throw new TokenClaimException("A validated resource token is required to resolve the product principal.",
                TokenClaimFailure.MissingObjectId);
        }
        if (!Guid.TryParse(claims.ObjectId, out var oid) || oid == Guid.Empty)
            throw new TokenClaimException("The resource token has no usable tenant-local principal object id.",
                TokenClaimFailure.MissingObjectId);
        return environment with { ResourcePrincipal = new(context, environment.TenantId, oid.ToString("D"),
            string.IsNullOrWhiteSpace(context.Profile.Username) ? null : context.Profile.Username) };
    }

    public async Task<PrincipalReference?> ResolveAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        var bound = await BindUserAsync(environment, cancellationToken).ConfigureAwait(false);
        return new() { ObjectId = bound.ResourcePrincipal!.ObjectId, ObjectType = PrincipalObjectTypes.User };
    }

    public async Task<string?> ResolveUsernameAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        // Legacy standalone consent callers carry no profile context. Capture their metadata once;
        // resolved product contexts always use the validated resource binding below.
        if (environment.Context is null)
            return await ResolveUsernameAsync(cancellationToken).ConfigureAwait(false);
        var bound = await BindUserAsync(environment, cancellationToken).ConfigureAwait(false);
        return bound.ResourcePrincipal!.Username;
    }

    private static void RequireUser(AuthProfile profile)
    {
        if (profile.PrincipalType != AuthPrincipalType.User) throw new UserPrincipalRequiredException();
    }

    /// <summary>Resolves the caller's sign-in name, or <see langword="null"/> when nobody is signed in.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The caller's user principal name.</returns>
    public async Task<string?> ResolveUsernameAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        if (profile is not null) RequireUser(profile);
        return string.IsNullOrWhiteSpace(profile?.Username) ? null : profile.Username;
    }

    /// <summary>Extracts the object id from an MSAL home-account id.</summary>
    /// <param name="accountKey">The account key, normally <c>{objectId}.{tenantId}</c>.</param>
    /// <returns>The object id, or the whole key when it carries no tenant segment.</returns>
    public static string ObjectIdOf(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        var separator = accountKey.IndexOf('.', StringComparison.Ordinal);

        return separator > 0 ? accountKey[..separator] : accountKey;
    }
}
