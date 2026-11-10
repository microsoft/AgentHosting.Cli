using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal static class ProfileCredentialValidation
{
    internal static void Target(AuthProfile profile)
    {
        try
        {
            var target = PowerPlatformApiTargetSelection.TargetFor(profile.Cloud, profile.Ring);
            if (!string.IsNullOrWhiteSpace(profile.ConnectionUrl))
            {
                if (!Uri.TryCreate(profile.ConnectionUrl, UriKind.Absolute, out var endpoint))
                    throw new ArgumentException("An absolute profile endpoint is required.", nameof(profile));
                PowerPlatformApiTargetSelection.Require(target with { GlobalEndpoint = endpoint });
            }
            if (!string.IsNullOrWhiteSpace(profile.Authority))
            {
                if (!Uri.TryCreate(profile.Authority, UriKind.Absolute, out var authority)
                    || authority.Query.Length != 0 || authority.Fragment.Length != 0 || !HasAuthorityTenantPath(authority))
                    throw new ArgumentException("An absolute profile authority with a tenant path and no query or fragment is required.", nameof(profile));
                AgentHostAuthorityResolver.ResolveFromUrl(profile.Cloud, authority);
            }
        }
        catch (ArgumentException)
        {
            throw new AuthProfileStateException("The profile authority and endpoint must match its registered cloud target.");
        }
    }

    private static bool HasAuthorityTenantPath(Uri authority)
    {
        var segments = authority.AbsolutePath.Split('/', 4);
        if (segments[^1].Length == 0) segments = segments[..^1];
        if (segments.Length is < 2 or > 3 || segments[0].Length != 0
            || segments.Length == 3 && !segments[2].Equals("v2.0", StringComparison.OrdinalIgnoreCase))
            return false;
        var tenant = segments[1];
        return tenant.Equals("common", StringComparison.OrdinalIgnoreCase)
            || tenant.Equals(AgentHostAuthorityResolver.OrganizationsTenant, StringComparison.OrdinalIgnoreCase)
            || Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty
            || tenant.Contains('.') && Uri.CheckHostName(tenant) == UriHostNameType.Dns;
    }

    internal static AuthCredentialMethod Method(AuthCreateRequest r, bool brokerSupported, bool interactionAllowed)
    {
        AuthCreateInput.Validate(r, interactionAllowed);
        var password = r.Username is not null || r.Password is not null;
        var method = r.ClientSecret is not null ? AuthCredentialMethod.ClientSecret
            : r.CertificateDiskPath is not null ? AuthCredentialMethod.Certificate
            : r.ManagedIdentity ? AuthCredentialMethod.DefaultAzureCredential
            : r.GitHubFederated ? AuthCredentialMethod.GitHubFederated
            : r.AzureDevOpsFederated ? AuthCredentialMethod.AzureDevOpsFederated
            : password ? AuthCredentialMethod.Password
            : r.DeviceCode ? AuthCredentialMethod.DeviceCode
            : brokerSupported ? AuthCredentialMethod.Broker : AuthCredentialMethod.Browser;
        return method;
    }

    internal static bool IsInteractive(AuthCredentialMethod m) => m is AuthCredentialMethod.Broker or AuthCredentialMethod.Browser or AuthCredentialMethod.DeviceCode;
    internal static bool IsPublicUser(AuthCredentialMethod m) => IsInteractive(m) || m == AuthCredentialMethod.Password;
    internal static bool IsConfidential(AuthCredentialMethod m) => !IsPublicUser(m) && m != AuthCredentialMethod.DefaultAzureCredential;
    internal static Guid ParseGuid(string? value, string field) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : throw new UsageException($"A nonzero GUID is required for {field}.");

    internal static string[] Scopes(AuthCreateRequest request)
    {
        if (request.Resource is not null && request.Scopes.Count != 0)
            throw new UsageException("Resource and explicit scopes cannot be combined.");
        if (request.Scopes.Count != 0) return Scopes(request.Scopes);
        try
        {
            var normalizer = new PacResourceScopeNormalizer();
            return Scopes([normalizer.ToDefaultScope(request.Resource ?? request.Target.Audience)]);
        }
        catch (ArgumentException) { throw new UsageException("An absolute resource URI is required."); }
    }

    internal static string[] Scopes(IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var result = scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0) throw new UsageException("At least one nonempty authentication scope is required.");
        if (result.Any(s => s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            throw new UsageException("An authentication scope cannot contain whitespace or control characters.");
        return result;
    }

    internal static void DefaultScopes(IReadOnlyList<string> scopes)
    {
        if (scopes.Count != 1 || !scopes[0].EndsWith("/.default", StringComparison.Ordinal) ||
            !Uri.TryCreate(scopes[0][..^"/.default".Length], UriKind.Absolute, out _))
            throw new UsageException("This credential method supports one resource .default scope, not explicit delegated scopes.");
    }
}
