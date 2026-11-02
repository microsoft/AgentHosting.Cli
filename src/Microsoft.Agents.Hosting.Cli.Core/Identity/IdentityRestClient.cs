using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

public sealed record IdentityRestResponse(int StatusCode, string? Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

public interface IIdentityRestClient
{
    Task<IdentityRestResponse> SendGraphAsync(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyList<string> scopes,
        string targetTenantId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null);

    Task<IdentityRestResponse> SendArmAsync(
        HttpMethod method,
        string path,
        object? body,
        string targetTenantId,
        CancellationToken cancellationToken);
}

/// <summary>Raw Graph and ARM transport for identity provisioning.</summary>
public sealed class IdentityRestClient : IIdentityRestClient
{
    private const int MaximumAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri GraphEndpoint = new(IdentityProvisioningConfig.GraphEndpoint);
    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokenProvider;
    private readonly IGraphConsentTokenProvider? _graphConsentTokenProvider;
    private readonly IArmConsentTokenProvider? _armConsentTokenProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;

    public IdentityRestClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        IGraphConsentTokenProvider? graphConsentTokenProvider = null,
        IArmConsentTokenProvider? armConsentTokenProvider = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _graphConsentTokenProvider = graphConsentTokenProvider;
        _armConsentTokenProvider = armConsentTokenProvider;
        _retryDelay = retryDelay ?? Task.Delay;
    }

    public async Task<IdentityRestResponse> SendGraphAsync(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyList<string> scopes,
        string targetTenantId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var uri = BuildGraphUri(path);
        // When a consent-capable Graph provider is wired (App Registration provisioning) it
        // acquires the token through a separate consenting client; otherwise fall back to the silent
        // provider (the Blueprint path and any first-party-preauthorized caller are unchanged). ARM is
        // handled symmetrically by its own consent provider in SendArmAsync below.
        var token = _graphConsentTokenProvider is not null
            ? await _graphConsentTokenProvider
                .AcquireGraphTokenAsync(scopes, targetTenantId, cancellationToken)
                .ConfigureAwait(false)
            : await _tokenProvider
                .AcquireTokenSilentAsync(scopes, targetTenantId, cancellationToken)
                .ConfigureAwait(false);
        return await SendAsync(
            method,
            uri,
            body,
            token.AccessToken,
            headers,
            cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildGraphUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A Microsoft Graph request path is required.", nameof(path));
        }

        var uri = !path.StartsWith('/') &&
            Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(GraphEndpoint, path);
        if (!string.Equals(uri.Scheme, GraphEndpoint.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, GraphEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != GraphEndpoint.Port ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "An absolute Microsoft Graph request URI must use the configured Graph origin.",
                nameof(path));
        }

        return uri;
    }

    public async Task<IdentityRestResponse> SendArmAsync(
        HttpMethod method,
        string path,
        object? body,
        string targetTenantId,
        CancellationToken cancellationToken)
    {
        // Managed-identity provisioning (FederatedCredentials/UserManagedIdentity) reaches ARM, which the
        // CLI first-party app is not preauthorized for. When an ARM consent provider is wired (App
        // Registration and Blueprint provisioning) it acquires the token through a separate consenting
        // client; otherwise fall back to the silent provider for any preauthorized caller.
        var token = _armConsentTokenProvider is not null
            ? await _armConsentTokenProvider
                .AcquireArmTokenAsync(targetTenantId, cancellationToken)
                .ConfigureAwait(false)
            : await _tokenProvider
                .AcquireTokenSilentAsync(IdentityProvisioningConfig.ArmResource, targetTenantId, cancellationToken)
                .ConfigureAwait(false);
        var uri = BuildArmUri(path);
        return await SendAsync(method, uri, body, token.AccessToken, headers: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IdentityRestResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        object? body,
        string accessToken,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        try
        {
            var serializedBody = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                using var request = CreateRequest(method, uri, accessToken, serializedBody, headers);
                try
                {
                    using var response = await _httpClient
                        .SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    var responseBody = await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (attempt < MaximumAttempts && ShouldRetry(method, (int)response.StatusCode))
                    {
                        await _retryDelay(
                            ResolveRetryDelay(response, attempt),
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return new IdentityRestResponse(
                        (int)response.StatusCode,
                        string.IsNullOrWhiteSpace(responseBody) ? null : responseBody);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested &&
                          attempt < MaximumAttempts &&
                          IsRepeatableProvisioningOperation(method))
                {
                    await _retryDelay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                    when (attempt < MaximumAttempts && IsRepeatableProvisioningOperation(method))
                {
                    await _retryDelay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("Identity REST retry loop completed unexpectedly.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TransportFailure(uri, "timed out");
        }
        catch (HttpRequestException)
        {
            throw TransportFailure(uri, "could not reach the service");
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string accessToken,
        string? serializedBody,
        IReadOnlyDictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (serializedBody is not null)
        {
            request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static bool ShouldRetry(HttpMethod method, int statusCode)
    {
        if (statusCode == 429)
        {
            return true;
        }

        var transientServiceFailure = statusCode is 502 or 503 or 504;
        return transientServiceFailure && IsRepeatableProvisioningOperation(method);
    }

    private static bool IsRepeatableProvisioningOperation(HttpMethod method) =>
        method == HttpMethod.Get ||
        method == HttpMethod.Put ||
        method == HttpMethod.Delete ||
        method == HttpMethod.Patch;

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

    private static Uri BuildArmUri(string path)
    {
        var segments = path.TrimStart('/').Split('/');
        var escapedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri(
            $"{IdentityProvisioningConfig.ArmResource}/{escapedPath}?api-version={IdentityProvisioningConfig.ManagedIdentityApiVersion}");
    }

    private static TargetRequestException TransportFailure(Uri uri, string reason) =>
        new(
            $"The identity service request to '{uri.Host}' {reason}. Check network connectivity and retry the command.",
            ExitCodes.Failure,
            TargetErrorKind.RequestFailed,
            status: 0);
}
