using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal sealed class ProfileCredentialBinding
{
    public Guid TenantId { get; init; }
    public Guid ClientId { get; init; }
    public Guid ObjectId { get; init; }
    public AuthPrincipalType PrincipalType { get; init; }
    public ProfileAzureSourceId? AzureSource { get; init; }
    public AuthCredentialMethod Method { get; init; }
    public CloudInstance Cloud { get; init; }
    public PowerPlatformApiRing Ring { get; init; }
    public string? Authority { get; init; }
    public string? ConnectionUrl { get; init; }
    public string? CertificatePath { get; init; }

    internal static string ReadTenantId(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3 || token.Length > 65536) throw new JsonException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return GuidClaim(document.RootElement, "tid").ToString("D");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw Failure(ErrorCodes.Authentication.TokenClaimMalformed); }
    }

    internal static ProfileCredentialBinding FromToken(ProfileTokenResult result, ProfileTokenRequest request)
    {
        try
        {
            var parts = result.AccessToken.Split('.');
            if (parts.Length != 3 || result.AccessToken.Length > 65536) throw new JsonException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload), new JsonDocumentOptions { MaxDepth = 16 });
            var claims = document.RootElement;
            var tenant = GuidClaim(claims, "tid");
            var client = GuidClaim(claims, claims.TryGetProperty("appid", out _) ? "appid" : "azp");
            var principal = GuidClaim(claims, "oid");
            if (claims.TryGetProperty("appid", out _) && claims.TryGetProperty("azp", out _) && GuidClaim(claims, "azp") != client)
                throw Failure(ErrorCodes.Authentication.TokenClaimWrongApplication);
            if (request.ClientId != Guid.Empty && request.ClientId != client)
                throw Failure(ErrorCodes.Authentication.TokenClaimWrongApplication);
            if (!Guid.TryParse(result.TenantId, out var returnedTenant) || returnedTenant != tenant ||
                request.Authority.TenantId is { } requestedTenant && requestedTenant != tenant)
                throw Failure(ErrorCodes.Authentication.TokenClaimWrongTenant);
            var audience = claims.GetProperty("aud").GetString();
            if (!MatchesAudience(audience, request.Scopes))
                throw Failure(ErrorCodes.Authentication.TokenClaimWrongAudience);
            var user = claims.TryGetProperty("scp", out var scp) && scp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(scp.GetString());
            var idtyp = claims.TryGetProperty("idtyp", out var type) ? type.GetString() : null;
            user |= idtyp == "user";
            if (ProfileCredentialValidation.IsConfidential(request.Method) && user)
                throw new AuthenticationException("The token does not represent the requested application principal.");
            if (request.Method == AuthCredentialMethod.DefaultAzureCredential)
            {
                var source = result.AzureSource ?? throw new AuthProfileStateException("The acquired Azure credential source pin is missing.");
                if (!Enum.IsDefined(source)) throw new AuthProfileStateException("The acquired Azure credential source pin is invalid.");
                if (!ProfileAzureCredentialSourceFactory.IsDeveloper(source) && user)
                    throw new AuthenticationException("The baseline Azure credential did not represent an application principal.");
                if (ProfileAzureCredentialSourceFactory.IsDeveloper(source) && !user && idtyp != "app")
                    throw new AuthenticationException("The developer Azure token does not provide a provable principal type.");
            }
            return new()
            {
                TenantId = tenant, ClientId = client, ObjectId = principal,
                PrincipalType = user ? AuthPrincipalType.User : AuthPrincipalType.ServicePrincipal,
                AzureSource = result.AzureSource, Method = request.Method, Cloud = request.Authority.Cloud, Ring = request.Ring,
                Authority = request.Authority.AuthorityUrl.AbsoluteUri, ConnectionUrl = request.ConnectionUrl,
                CertificatePath = request.CertificatePath is null ? null : Path.GetFullPath(request.CertificatePath),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw Failure(ErrorCodes.Authentication.TokenClaimMalformed);
        }
    }

    internal void EnsureSame(ProfileCredentialBinding next)
    {
        if (TenantId != next.TenantId || ClientId != next.ClientId || ObjectId != next.ObjectId || PrincipalType != next.PrincipalType ||
            AzureSource != next.AzureSource || Method != next.Method || Cloud != next.Cloud || Ring != next.Ring ||
            !Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || !Uri.TryCreate(next.Authority, UriKind.Absolute, out var nextAuthority) ||
            authority.GetLeftPart(UriPartial.Authority) != nextAuthority.GetLeftPart(UriPartial.Authority))
            throw new AuthenticationException("The acquired credential identity differs from its persisted binding.",
                errorCode: ErrorCodes.Authentication.ActiveAccountMismatch);
    }

    internal void EnsureSnapshot(AuthProfile profile)
    {
        if (!Guid.TryParse(profile.TenantId, out var tenant) || tenant != TenantId ||
            !Guid.TryParse(profile.ClientId, out var client) || client != ClientId ||
            Method != profile.CredentialMethod || Cloud != profile.Cloud || Ring != profile.Ring ||
            PrincipalType != profile.PrincipalType || Authority != profile.Authority ||
            ConnectionUrl != profile.ConnectionUrl || CertificatePath != profile.CertificatePath)
            throw new AuthenticationException("The supplied profile snapshot differs from its protected credential binding.",
                errorCode: ErrorCodes.Authentication.ActiveAccountMismatch);
    }

    private static Guid GuidClaim(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var parsed) && parsed != Guid.Empty ? parsed : throw Failure(ErrorCodes.Authentication.TokenClaimMalformed);

    private static bool MatchesAudience(string? audience, IReadOnlyList<string> scopes)
    {
        if (string.IsNullOrEmpty(audience)) return false;
        foreach (var scope in scopes)
        {
            var resource = scope.EndsWith("/.default", StringComparison.Ordinal) ? scope[..^"/.default".Length].TrimEnd('/') : scope;
            if (string.Equals(audience.TrimEnd('/'), resource, StringComparison.OrdinalIgnoreCase)) return true;
            // Entra can return the resource application id instead of its URI audience.
            if (resource == "https://api.powerplatform.com" && audience == "0ddb742a-e7dc-4899-a31e-80e797ec7144") return true;
            if (resource is "https://api.preprod.powerplatform.com" or "https://api.test.powerplatform.com" &&
                audience == "8578e004-a5c6-46e7-913e-12f58912df43") return true;
            if (string.Equals(resource, "https://graph.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(audience, "00000003-0000-0000-c000-000000000000", StringComparison.OrdinalIgnoreCase)) return true;
            if (resource.StartsWith("api://", StringComparison.Ordinal) && string.Equals(resource[6..], audience, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static AuthenticationException Failure(int code) =>
        new("The acquired token could not be bound to the requested credential, tenant, and resource.", errorCode: code);
}

internal sealed class ProtectedProfileCredential
{
    public string? Secret { get; init; }
    public ProfileCredentialBinding? Binding { get; init; }
}
