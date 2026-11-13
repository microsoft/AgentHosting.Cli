using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The non-secret claims the CLI checks on a Power Platform API access token. Only the identifiers the
/// operator already supplies or selects are kept — audience, application, tenant, the caller's object
/// id in that tenant, and granted permissions. The token string, its segments, and its signature are
/// never carried here, so this record is safe to compare, assert on, and (if ever needed) log.
/// </summary>
public sealed record PpapiTokenClaims
{
    /// <summary>The <c>aud</c> claim.</summary>
    public string? Audience { get; init; }

    /// <summary>The <c>appid</c> claim (or <c>azp</c> for a v2.0 token).</summary>
    public string? ApplicationId { get; init; }

    /// <summary>The <c>tid</c> claim.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The <c>oid</c> claim: the caller's object id in the tenant the token was issued for. For a B2B
    /// guest that is the guest object in the resource tenant, not the object id of their home account.
    /// </summary>
    public string? ObjectId { get; init; }

    public string? IdentityType { get; init; }

    /// <summary>The permissions carried by the space-delimited <c>scp</c> claim.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Whether <paramref name="permission"/> is present, compared case-insensitively.</summary>
    public bool HasScope(string permission) =>
        Scopes.Any(s => string.Equals(s, permission, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the non-secret claim set out of a JWT access token without validating its signature.
///
/// Signature validation is the resource's job, not the client's — the CLI is not a token validator and
/// must never behave like one. What the CLI does need is to fail fast and clearly when the token it
/// just acquired is not the token the selected environment will accept, instead of sending it and
/// reading a 401 back. Reading is therefore deliberately narrow: split the compact JWT, base64url
/// decode the payload, and take four claims.
///
/// Every failure path throws <see cref="Microsoft.Agents.Hosting.Cli.Core.Errors.TokenClaimException"/> with a message that names no token
/// material — not the token, not a segment, not a fragment of one.
///
/// An encrypted (JWE) token is rejected rather than read: its second segment is an encrypted key, not
/// a payload, so decoding it yields a misleading parse failure instead of a clear one.
/// </summary>
public static class PpapiTokenClaimsReader
{
    private const int SignedSegmentCount = 3;
    private const int EncryptedSegmentCount = 5;
    private const int MaximumTokenLength = 128 * 1024;

    /// <summary>Reads the claim set from <paramref name="accessToken"/>.</summary>
    public static PpapiTokenClaims Read(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw Malformed("the access token is empty");
        }

        if (accessToken.Length > MaximumTokenLength)
        {
            throw Malformed("the access token exceeds the bounded claim-reader limit");
        }

        var segments = accessToken.Split('.');
        if (segments.Length == EncryptedSegmentCount)
        {
            throw Malformed("the access token is encrypted, so its claims cannot be read by this client");
        }

        if (segments.Length != SignedSegmentCount)
        {
            throw Malformed("the access token is not a compact JWT");
        }

        byte[] payload;
        try
        {
            payload = DecodeBase64Url(segments[1]);
        }
        catch (FormatException)
        {
            throw Malformed("the access token payload is not valid base64url");
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Malformed("the access token payload is not a JSON object");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Malformed("the access token payload contains duplicate claims");
            }
            if (root.TryGetProperty("idtyp", out var identityType)
                && identityType.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw Malformed("the principal mode claim is not a string");

            return new PpapiTokenClaims
            {
                Audience = ReadString(root, "aud"),
                ApplicationId = ReadString(root, "appid") ?? ReadString(root, "azp"),
                TenantId = ReadString(root, "tid"),
                ObjectId = ReadString(root, "oid"),
                IdentityType = ReadString(root, "idtyp"),
                Scopes = ReadScopes(root),
            };
        }
        catch (JsonException)
        {
            throw Malformed("the access token payload is not valid JSON");
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static IReadOnlyList<string> ReadScopes(JsonElement root)
    {
        var raw = ReadString(root, "scp");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static byte[] DecodeBase64Url(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        var padded = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            0 => normalized,
            _ => throw new FormatException("Invalid base64url length."),
        };

        return Convert.FromBase64String(padded);
    }

    private static TokenClaimException Malformed(string reason) =>
        new(
            $"The Power Platform API access token could not be read: {reason}.",
            TokenClaimFailure.Malformed,
            remediation: "Run `msagent auth clear` to clear owned authentication state, then `msagent auth create` for the intended cloud to obtain a fresh token, and retry.");
}

/// <summary>
/// Enforces the token-claim contract: a token is accepted only when
/// <c>aud</c> is the selected environment's Power Platform API audience, <c>appid</c> is AgentHostCLI,
/// <c>tid</c> is the selected environment's tenant, and <c>scp</c> carries the delegated permission
/// the command needs.
///
/// The tenant check is the client-side half of the same rule the service enforces: identity and target
/// must agree before a call is made, not after it is rejected.
/// </summary>
public sealed class PpapiTokenClaimsValidator
{
    private readonly string _expectedApplicationId;

    /// <param name="expectedApplicationId">
    /// The application id the token's <c>appid</c> must carry. Defaults to
    /// <see cref="Microsoft.Agents.Hosting.Cli.Core.Auth.PowerPlatformApiPermissions.AgentHostCliApplicationId"/>; tests supply their own.
    /// </param>
    public PpapiTokenClaimsValidator(string? expectedApplicationId = null)
    {
        _expectedApplicationId = string.IsNullOrWhiteSpace(expectedApplicationId)
            ? PowerPlatformApiPermissions.AgentHostCliApplicationId
            : expectedApplicationId.Trim();
    }

    /// <summary>
    /// Validates <paramref name="accessToken"/>'s claims and returns the non-secret claim set. Throws
    /// <see cref="Microsoft.Agents.Hosting.Cli.Core.Errors.TokenClaimException"/> on the first violated rule.
    /// </summary>
    public PpapiTokenClaims Validate(
        string accessToken,
        string expectedAudience,
        string expectedTenantId,
        string requiredPermission)
    {
        var claims = PpapiTokenClaimsReader.Read(accessToken);
        Validate(claims, expectedAudience, expectedTenantId, requiredPermission);
        return claims;
    }

    /// <summary>
    /// Validates the audience, application, and permission of a token acquired <b>before</b> an
    /// environment is selected — the environment inventory call, which is what discovers tenants in the
    /// first place and therefore has no tenant to bind to yet.
    ///
    /// This is a separate, explicitly named method rather than a blank tenant argument: a validator
    /// that silently drops a check when an argument is empty is indistinguishable from a bug.
    /// </summary>
    public PpapiTokenClaims ValidateWithoutTenantBinding(
        string accessToken,
        string expectedAudience,
        string requiredPermission)
    {
        var claims = PpapiTokenClaimsReader.Read(accessToken);
        ValidateAudienceAndApplication(claims, expectedAudience);
        ValidatePermission(claims, requiredPermission);
        return claims;
    }

    /// <summary>Validates an already-read claim set, including the tenant binding.</summary>
    public void Validate(
        PpapiTokenClaims claims,
        string expectedAudience,
        string expectedTenantId,
        string requiredPermission)
    {
        ArgumentNullException.ThrowIfNull(claims);

        ValidateAudienceAndApplication(claims, expectedAudience);

        // An unknown expected tenant is a rejection, not a reason to skip the check. Callers that
        // legitimately have no tenant yet use ValidateWithoutTenantBinding and say so.
        if (string.IsNullOrWhiteSpace(expectedTenantId) ||
            !string.Equals(claims.TenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new TokenClaimException(
                $"The access token was issued for tenant '{claims.TenantId ?? "<none>"}', but the selected environment's tenant is '{(string.IsNullOrWhiteSpace(expectedTenantId) ? "<unknown>" : expectedTenantId)}'.",
                TokenClaimFailure.WrongTenant,
                remediation: string.IsNullOrWhiteSpace(expectedTenantId)
                    ? "Re-select the environment with `msagent env select <environment-id>` so its tenant is recorded."
                    : $"Run `msagent auth create --tenant {expectedTenantId}` for the intended cloud with an account in that tenant, or select an environment in the signed-in tenant.");
        }

        ValidatePermission(claims, requiredPermission);
    }

    /// <summary>Checks the exact captured profile, resource tenant, and applicable authorization mode.</summary>
    public PpapiTokenClaims ValidateForProfile(
        string accessToken, string expectedAudience, string expectedTenantId,
        AuthProfile profile, IReadOnlyList<string> requiredPermissions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(requiredPermissions);
        var expectedClient = profile.CredentialMethod == AuthCredentialMethod.DefaultAzureCredential
            || profile.PrincipalType == AuthPrincipalType.ServicePrincipal
                ? profile.ClientId
                : _expectedApplicationId;
        if (string.IsNullOrWhiteSpace(expectedClient))
            throw new TokenClaimException("The captured authentication profile has no bound client application.",
                TokenClaimFailure.WrongApplication);
        var claims = PpapiTokenClaimsReader.Read(accessToken);
        new PpapiTokenClaimsValidator(expectedClient).ValidateAudienceAndApplication(claims, expectedAudience);
        if (string.IsNullOrWhiteSpace(expectedTenantId)
            || !string.Equals(claims.TenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
            throw new TokenClaimException("The access token does not belong to the captured resource tenant.",
                TokenClaimFailure.WrongTenant);

        var workload = profile.PrincipalType == AuthPrincipalType.ServicePrincipal;
        var appOnly = claims.Scopes.Count == 0
            && (claims.IdentityType is null || string.Equals(claims.IdentityType, "app", StringComparison.OrdinalIgnoreCase));
        var delegated = claims.Scopes.Count > 0
            && (claims.IdentityType is null || string.Equals(claims.IdentityType, "user", StringComparison.OrdinalIgnoreCase));
        if (workload ? !appOnly : profile.PrincipalType != AuthPrincipalType.User || !delegated)
            throw new TokenClaimException("The access token principal mode does not match the captured authentication profile.",
                TokenClaimFailure.WrongPrincipalMode);
        if (!workload)
        {
            foreach (var permission in requiredPermissions)
                ValidatePermission(claims, permission);
        }
        return claims;
    }

    private void ValidateAudienceAndApplication(PpapiTokenClaims claims, string expectedAudience)
    {
        if (!AudienceMatches(claims.Audience, expectedAudience))
        {
            throw new TokenClaimException(
                $"The access token was issued for audience '{claims.Audience ?? "<none>"}', not the selected environment's Power Platform API audience '{expectedAudience}'.",
                TokenClaimFailure.WrongAudience,
                remediation: "Re-select the environment with `msagent env select <environment-id>` so the token is requested for the environment's own audience.");
        }

        if (!string.Equals(claims.ApplicationId, _expectedApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new TokenClaimException(
                $"The access token was issued to application '{claims.ApplicationId ?? "<none>"}', not the AgentHostCLI application '{_expectedApplicationId}'.",
                TokenClaimFailure.WrongApplication,
                remediation: "Set AH_CLI_CLIENT_ID to the AgentHostCLI application id, then run `msagent auth create` again for the intended cloud.");
        }
    }

    private static void ValidatePermission(PpapiTokenClaims claims, string requiredPermission)
    {
        if (!claims.HasScope(requiredPermission))
        {
            // The token is valid for the platform; it simply does not carry the delegated permission
            // this operation needs. That is a permission denial, not a sign-in problem.
            throw new TokenClaimException(
                $"The access token does not carry the '{requiredPermission}' delegated permission required by this operation.",
                TokenClaimFailure.MissingPermission,
                remediation: $"Grant the AgentHostCLI application the '{requiredPermission}' delegated Power Platform API permission, then sign in again.");
        }
    }

    /// <summary>
    /// Audience comparison tolerates the trailing-slash difference between an <c>aud</c> issued as a
    /// resource URI and the same audience configured without the slash. It tolerates nothing else.
    /// </summary>
    private static bool AudienceMatches(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(
            actual.TrimEnd('/'),
            expected.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
