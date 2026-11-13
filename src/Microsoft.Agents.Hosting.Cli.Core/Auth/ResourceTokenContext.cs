using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

internal static class ResourceTokenContext
{
    internal static EnvironmentResolutionContext Require(EnvironmentResolutionContext? context) =>
        context ?? throw new AuthenticationContextException("The resource request has no captured authentication context.");

    internal static void RequireBinding(ITokenProvider tokens, EnvironmentResolutionContext? context)
    {
        if (tokens is ProfileBoundTokenProvider bound && context is not null && context != bound.Context)
            throw new AuthenticationContextException("The token provider is bound to another authentication context.");
    }

    internal static void ValidateLegacy(PpapiTokenClaimsValidator validator, ITokenProvider tokens,
        EnvironmentResolutionContext? context, string accessToken, string audience, string tenantId,
        IReadOnlyList<string> requiredPermissions)
    {
        var bound = tokens as ProfileBoundTokenProvider;
        if (bound is not null)
        {
            RequireBinding(tokens, context);
            validator.ValidateForProfile(accessToken, audience, tenantId, bound.Context.Profile, requiredPermissions);
            return;
        }
        foreach (var permission in requiredPermissions)
            validator.Validate(accessToken, audience, tenantId, permission);
    }
}
