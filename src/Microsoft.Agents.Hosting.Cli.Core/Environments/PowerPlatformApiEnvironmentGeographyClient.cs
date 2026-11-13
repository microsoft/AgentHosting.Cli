using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// Reads <c>geoLongName</c> from the environment-scoped
/// <c>GET /gateway/cluster?api-version=1</c> response.
/// </summary>
public sealed class PowerPlatformApiEnvironmentGeographyClient :
    IEnvironmentGeographyClient
{
    private readonly HttpClient _http;
    private readonly ISelectedAccountTokenProvider? _tokenProvider;
    private readonly PowerPlatformApiTargetConfig _target;
    private readonly PpapiTokenClaimsValidator _claimsValidator;
    private readonly IProfileResourceTokenProvider? _profileTokens;
    private readonly EnvironmentResolutionContext? _context;

    /// <summary>Creates a geography client bound to an explicit profile and resource target.</summary>
    /// <param name="httpClient">The injected HTTP transport.</param>
    /// <param name="profileTokens">Profile-bound resource acquisition.</param>
    /// <param name="context">The immutable authentication coordinates.</param>
    public PowerPlatformApiEnvironmentGeographyClient(HttpClient httpClient,
        IProfileResourceTokenProvider profileTokens, EnvironmentResolutionContext context)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profileTokens = profileTokens ?? throw new ArgumentNullException(nameof(profileTokens));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _target = context.Target;
        _claimsValidator = new();
    }

    public PowerPlatformApiEnvironmentGeographyClient(
        HttpClient httpClient,
        ISelectedAccountTokenProvider tokenProvider,
        PowerPlatformApiTargetConfig target,
        PpapiTokenClaimsValidator claimsValidator,
        IProfileResourceTokenProvider? profileTokens = null,
        EnvironmentResolutionContext? context = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider
            ?? throw new ArgumentNullException(nameof(tokenProvider));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _claimsValidator = claimsValidator
            ?? throw new ArgumentNullException(nameof(claimsValidator));
        _profileTokens = profileTokens;
        _context = context;
    }

    public async Task<string?> GetGeographyAsync(
        ResolvedEnvironmentTarget target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.EnvironmentId)
            || string.IsNullOrWhiteSpace(target.TenantId))
        {
            return null;
        }

        var captured = _profileTokens is not null ? ResourceTokenContext.Require(_context) : null;
        var resourceTarget = captured?.Target ?? _target;
        TokenAcquisitionResult? token = _profileTokens is not null
            ? (await _profileTokens.AcquireAsync(
                captured!,
                resourceTarget.Audience, target.TenantId, [PowerPlatformApiPermissions.EnvironmentsRead], cancellationToken).ConfigureAwait(false)).Token
            : await _tokenProvider!
            .TryAcquireTokenSilentAsync(
                resourceTarget.Audience,
                target.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        if (token is null)
        {
            return null;
        }

        if (_profileTokens is null) _claimsValidator.Validate(
            token.AccessToken,
            resourceTarget.Audience,
            target.TenantId,
            PowerPlatformApiPermissions.EnvironmentsRead);

        Uri environmentEndpoint =
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(
                resourceTarget,
                target.EnvironmentId);
        var requestUri = new Uri(
            environmentEndpoint,
            "/gateway/cluster?api-version=1");
        using var request =
            new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using HttpResponseMessage response =
            await _http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "The Power Platform API cluster metadata request failed.",
                inner: null,
                response.StatusCode);
        }

        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "geoLongName",
                    out JsonElement geography)
                || geography.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(geography.GetString()))
            {
                throw new InvalidDataException(
                    "The Power Platform API cluster metadata response did not contain geoLongName.");
            }

            return geography.GetString()!.Trim();
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException(
                "The Power Platform API cluster metadata response was not valid JSON.",
                failure);
        }
    }
}
