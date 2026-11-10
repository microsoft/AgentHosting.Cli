using System.Net;
using System.Net.Http.Headers;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Sends Agent Management Service requests with a validated delegated token, giving a route the service runs on the caller's behalf a token that lasts as long as the service needs it.</summary>
public sealed class AmsDelegatedRequestSender
{
    /// <summary>The least remaining lifetime a token needs on a route the service runs on the caller's behalf: the service's 20-minute time cap, plus its 5-minute expiry margin, plus 10 minutes of slack.</summary>
    public static readonly TimeSpan OnBehalfOfMinimumLifetime = TimeSpan.FromMinutes(35);

    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokens;
    private readonly PpapiTokenClaimsValidator _claimsValidator;

    /// <summary>Creates the sender over a client that does not follow redirects.</summary>
    /// <param name="httpClient">The client requests are sent with.</param>
    /// <param name="tokens">Acquires and renews delegated tokens.</param>
    /// <param name="claimsValidator">Validates token claims before a token leaves the process.</param>
    public AmsDelegatedRequestSender(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator claimsValidator)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _claimsValidator = claimsValidator ?? throw new ArgumentNullException(nameof(claimsValidator));
    }

    /// <summary>Sends a request with a validated token and, on a route the service runs on the caller's behalf, renews a token the service finds expires too soon and sends the same request once more.</summary>
    /// <param name="target">The endpoint, token audience and tenant of the request.</param>
    /// <param name="permission">The delegated permission the token must carry.</param>
    /// <param name="onBehalfOf">Whether the service runs the route on the caller's behalf.</param>
    /// <param name="createRequest">Builds the request, with the same headers and body each time.</param>
    /// <param name="completionOption">When the send completes.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The response to the last request sent.</returns>
    public async Task<HttpResponseMessage> SendAsync(AmsServiceTarget target, string permission, bool onBehalfOf, Func<HttpRequestMessage> createRequest, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(createRequest);

        var response = await SendWithAsync(await AcquireAsync(target, onBehalfOf, cancellationToken).ConfigureAwait(false), target, permission, createRequest, completionOption, cancellationToken).ConfigureAwait(false);
        if (!onBehalfOf || !await RefusesTokenLifetimeAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        response.Dispose();
        return await SendWithAsync(await _tokens.RenewTokenSilentAsync(target.Audience, target.TenantId, cancellationToken).ConfigureAwait(false), target, permission, createRequest, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RefusesTokenLifetimeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        try
        {
            return AmsMcsOperationErrorMap.RefusesTokenLifetime(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<TokenAcquisitionResult> AcquireAsync(AmsServiceTarget target, bool onBehalfOf, CancellationToken cancellationToken)
    {
        var token = await _tokens.AcquireTokenSilentAsync(target.Audience, target.TenantId, cancellationToken).ConfigureAwait(false);
        return onBehalfOf && token.ExpiresOn - DateTimeOffset.UtcNow < OnBehalfOfMinimumLifetime ? await _tokens.RenewTokenSilentAsync(target.Audience, target.TenantId, cancellationToken).ConfigureAwait(false) : token;
    }

    private async Task<HttpResponseMessage> SendWithAsync(TokenAcquisitionResult token, AmsServiceTarget target, string permission, Func<HttpRequestMessage> createRequest, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        _claimsValidator.Validate(token.AccessToken, target.Audience, target.TenantId, permission);

        using var request = createRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }
}
