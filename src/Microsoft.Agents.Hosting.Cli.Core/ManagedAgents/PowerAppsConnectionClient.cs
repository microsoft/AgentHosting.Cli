using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

public sealed record PowerAppsConnectionResponse(HttpStatusCode StatusCode, string Body, ServiceCorrelation? Correlation = null)
{
    public bool Oversized { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    public string CorrelationDetail => Identifiers.Describe();

    private ServiceCorrelation Identifiers => Correlation ?? ServiceCorrelation.None;

    public string Correlated(string message) => Identifiers.Annotate(message);
}

/// <summary>Sends connection requests to the Power Apps connections API as the signed-in user.</summary>
public sealed class PowerAppsConnectionClient : IDisposable
{
    private const string PowerAppsResource = "https://api.bap.microsoft.com";
    private const string ConnectionsApiVersion = "2016-11-01";

    private const int MaxResponseBodyBytes = 1024 * 1024;

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(100);

    /// <summary>The landing page used when no local page can be served.</summary>
    public const string NeutralConsentRedirectUrl = "https://www.microsoft.com/";

    private static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(2);

    private readonly ITokenProvider _tokenProvider;
    private readonly IProfileCredentialProvider? _profileCredentials;
    private readonly Func<CoreServicesClusterCategory> _cluster;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly TextWriter? _diagnostics;
    private readonly TimeSpan _requestTimeout;
    private readonly Lock _gate = new();

    private HttpMessageHandler? _sharedHandler;
    private TokenAcquisitionResult? _cachedToken;
    private string? _cachedTokenTenantId;
    private EnvironmentResolutionContext? _cachedTokenContext;

    /// <summary>Creates the client.</summary>
    /// <param name="tokenProvider">Supplies the Power Apps token the API accepts.</param>
    /// <param name="cluster">The cluster category whose API host requests are sent to.</param>
    /// <param name="handlerFactory">Creates the transport, replaced in tests.</param>
    /// <param name="diagnostics">Receives the redacted detail of what the service answered. Omitted unless the operator asked for it.</param>
    /// <param name="requestTimeout">How long one request may take, headers and body together. Defaults to 100 seconds.</param>
    /// <param name="profileCredentials">Acquires service credentials for captured profiles; required for non-legacy contexts.</param>
    public PowerAppsConnectionClient(ITokenProvider tokenProvider, CoreServicesClusterCategory cluster, Func<HttpMessageHandler>? handlerFactory = null, TextWriter? diagnostics = null, TimeSpan? requestTimeout = null,
        IProfileCredentialProvider? profileCredentials = null)
        : this(tokenProvider, () => cluster, handlerFactory, diagnostics, requestTimeout, profileCredentials)
    {
    }

    /// <summary>Creates a client that resolves its legacy cluster only when a request needs it.</summary>
    public static PowerAppsConnectionClient CreateDeferred(ITokenProvider tokenProvider, Func<CoreServicesClusterCategory> cluster,
        Func<HttpMessageHandler>? handlerFactory = null, TextWriter? diagnostics = null, TimeSpan? requestTimeout = null,
        IProfileCredentialProvider? profileCredentials = null) =>
        new(tokenProvider, cluster, handlerFactory, diagnostics, requestTimeout, profileCredentials);

    private PowerAppsConnectionClient(ITokenProvider tokenProvider, Func<CoreServicesClusterCategory> cluster, Func<HttpMessageHandler>? handlerFactory, TextWriter? diagnostics, TimeSpan? requestTimeout,
        IProfileCredentialProvider? profileCredentials)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _profileCredentials = profileCredentials;
        _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        _handlerFactory = handlerFactory ?? TransportDefaults.CreateRedirectRejectingHandler;
        _diagnostics = diagnostics;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>Disposes the transport shared by every request this client sent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _sharedHandler?.Dispose();
            _sharedHandler = null;
        }
    }

    /// <summary>Creates an unauthenticated connection for a connector.</summary>
    /// <param name="environment">The environment the connection is created in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="connectionName">The name to create the connection under.</param>
    /// <param name="parameterSetName">The authentication parameter set to create under, when the connector declares sets.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the service answered.</returns>
    public Task<PowerAppsConnectionResponse> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, string connectionName, string? parameterSetName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return SendAsync(HttpMethod.Put, ConnectionUri(environment, connectorId, connectionName), environment, CreateBody(environment.EnvironmentId, parameterSetName), cancellationToken);
    }

    /// <summary>Asks the service for the links that authenticate a connection.</summary>
    /// <param name="environment">The environment the connection lives in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="connectionName">The connection to authenticate.</param>
    /// <param name="redirectUrl">The page the browser lands on once consent completes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the service answered.</returns>
    public Task<PowerAppsConnectionResponse> GetConsentLinkAsync(CopilotEnvironmentContext environment, string connectorId, string connectionName, string redirectUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return SendAsync(
            HttpMethod.Post,
            SubResourceUri(environment, connectorId, connectionName, "getConsentLink"),
            environment,
            new JsonObject { ["redirectUrl"] = string.IsNullOrWhiteSpace(redirectUrl) ? NeutralConsentRedirectUrl : redirectUrl }.ToJsonString(),
            cancellationToken);
    }

    /// <summary>Redeems the authorization code a consent experience reported.</summary>
    /// <param name="environment">The environment the connection lives in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="connectionName">The connection the code authenticates.</param>
    /// <param name="code">The authorization code the consent redirect carried.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the service answered.</returns>
    public async Task<PowerAppsConnectionResponse> ConfirmConsentCodeAsync(CopilotEnvironmentContext environment, string connectorId, string connectionName, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var uri = SubResourceUri(environment, connectorId, connectionName, "confirmConsentCode");
        var (objectId, tenantId) = SignedInPrincipal(await AccessTokenAsync(environment, cancellationToken).ConfigureAwait(false));
        var body = new JsonObject { ["code"] = code };

        if (!string.IsNullOrWhiteSpace(objectId))
        {
            body["objectId"] = objectId;
        }

        var claimed = string.IsNullOrWhiteSpace(tenantId) ? environment.TenantId : tenantId;

        if (!string.IsNullOrWhiteSpace(claimed))
        {
            body["tenantId"] = claimed;
        }

        return await SendAsync(
            HttpMethod.Post,
            uri,
            environment,
            body.ToJsonString(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The caller as the target tenant knows them, read from the very token that will carry the
    /// request.
    /// </summary>
    private static (string? ObjectId, string? TenantId) SignedInPrincipal(string accessToken)
    {
        try
        {
            var claims = PpapiTokenClaimsReader.Read(accessToken);

            return (claims.ObjectId, claims.TenantId);
        }
        catch (TokenClaimException)
        {
            return (null, null);
        }
    }

    /// <summary>Reads one connection.</summary>
    /// <param name="environment">The environment the connection lives in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="connectionName">The connection to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the service answered.</returns>
    public Task<PowerAppsConnectionResponse> ReadConnectionAsync(CopilotEnvironmentContext environment, string connectorId, string connectionName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return SendAsync(HttpMethod.Get, ConnectionUri(environment, connectorId, connectionName), environment, null, cancellationToken);
    }

    /// <summary>Deletes one connection.</summary>
    /// <param name="environment">The environment the connection lives in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="connectionName">The connection to delete.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the service answered.</returns>
    public Task<PowerAppsConnectionResponse> DeleteConnectionAsync(CopilotEnvironmentContext environment, string connectorId, string connectionName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return SendAsync(HttpMethod.Delete, ConnectionUri(environment, connectorId, connectionName), environment, null, cancellationToken);
    }

    private static string CreateBody(string environmentId, string? parameterSetName)
    {
        var properties = new JsonObject { ["environment"] = new JsonObject { ["name"] = environmentId } };

        if (!string.IsNullOrWhiteSpace(parameterSetName))
        {
            properties["connectionParametersSet"] = new JsonObject { ["name"] = parameterSetName, ["values"] = new JsonObject() };
        }

        return new JsonObject { ["properties"] = properties }.ToJsonString();
    }

    private Uri ConnectionUri(CopilotEnvironmentContext environment, string connectorId, string connectionName) =>
        new($"{ApiRoot(environment, connectorId)}/connections/{Uri.EscapeDataString(ConnectionConsentLink.NormalizeResourceId(connectionName))}?{Query(environment.EnvironmentId)}");

    private Uri SubResourceUri(CopilotEnvironmentContext environment, string connectorId, string connectionName, string action) =>
        new($"{ApiRoot(environment, connectorId)}/connections/{Uri.EscapeDataString(ConnectionConsentLink.NormalizeResourceId(connectionName))}/{action}?{Query(environment.EnvironmentId)}");

    private string ApiRoot(CopilotEnvironmentContext environment, string connectorId) =>
        $"https://{ConnectionConsentLink.ApiHostFor(environment.Context is { } context ? PowerAppsServiceTarget.ClusterFor(context) : _cluster())}/providers/Microsoft.PowerApps/apis/{Uri.EscapeDataString(ConnectionConsentLink.NormalizeResourceId(connectorId))}";

    private static string Query(string environmentId) =>
        $"api-version={ConnectionsApiVersion}&$filter={Uri.EscapeDataString($"environment eq '{environmentId}'")}";

    private async Task<PowerAppsConnectionResponse> SendAsync(HttpMethod method, Uri uri, CopilotEnvironmentContext environment, string? body, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(environment, cancellationToken).ConfigureAwait(false);
        var correlation = new ServiceCorrelation(Guid.NewGuid().ToString(), null);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        bounded.CancelAfter(_requestTimeout);

        using var client = new HttpClient(SharedHandler(), disposeHandler: false);
        using var request = new HttpRequestMessage(method, uri);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(ServiceCorrelation.ClientRequestIdHeader, correlation.ClientRequestId);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
            var answer = await ReadBodyAsync(response, bounded.Token).ConfigureAwait(false);
            var answered = new PowerAppsConnectionResponse(
                response.StatusCode,
                answer.Text,
                correlation with { ServiceRequestId = ServiceCorrelation.ReadServiceRequestId(response) })
            {
                Oversized = answer.Oversized,
            };

            Trace(method, uri, $"HTTP {(int)answered.StatusCode} {answered.CorrelationDetail} {Describe(answered)}");

            return answered;
        }
        catch (Exception failure)
        {
            Trace(method, uri, $"{failure.GetType().Name} {correlation.Describe()} {failure.Message}");

            throw;
        }
    }

    private static string Describe(PowerAppsConnectionResponse response) =>
        response.Oversized
            ? $"response larger than {MaxResponseBodyBytes} bytes, discarded unread"
            : PowerAppsConnectionBody.Redacted(response.Body);

    private static async Task<ResponseBody> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[MaxResponseBodyBytes + 1];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await content.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled > MaxResponseBodyBytes
            ? new ResponseBody(string.Empty, true)
            : new ResponseBody(Encoding.UTF8.GetString(buffer, 0, filled), false);
    }

    private readonly record struct ResponseBody(string Text, bool Oversized);

    private void Trace(HttpMethod method, Uri uri, string outcome)
    {
        if (_diagnostics is { } diagnostics)
        {
            diagnostics.WriteLine($"[connections] {method} {uri.GetLeftPart(UriPartial.Path)} -> {outcome.Trim()}");
        }
    }

    private async Task<string> AccessTokenAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        var tenantId = environment.TenantId;
        var context = environment.Context;
        var resource = context is null ? PowerAppsResource : PowerAppsServiceTarget.AudienceFor(context);
        if (context is not null && (_profileCredentials is null || string.IsNullOrWhiteSpace(tenantId)))
            throw new TokenClaimException("A captured PowerApps operation requires profile credentials and an explicit tenant.", TokenClaimFailure.WrongPrincipalMode);
        lock (_gate)
        {
            if (_cachedToken is { } cached
                && _cachedTokenContext == context
                && string.Equals(_cachedTokenTenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                && cached.ExpiresOn - TokenRenewalMargin > DateTimeOffset.UtcNow)
            {
                return cached.AccessToken;
            }
        }

        TokenAcquisitionResult acquired;
        if (context is null)
        {
            acquired = await _tokenProvider.AcquireTokenSilentAsync(resource, tenantId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var scope = PacResourceScopeNormalizer.ForProfile(context.Profile).ToDefaultScope(resource);
            acquired = await _profileCredentials!.AcquireAsync(context.Profile, [scope], tenantId, cancellationToken).ConfigureAwait(false);
        }
        var token = AcquiredTokenTenant.Require(acquired, tenantId);

        lock (_gate)
        {
            _cachedToken = acquired;
            _cachedTokenTenantId = tenantId;
            _cachedTokenContext = context;
        }

        return token;
    }

    private HttpMessageHandler SharedHandler()
    {
        lock (_gate)
        {
            return _sharedHandler ??= _handlerFactory();
        }
    }
}

/// <summary>Reads the fields the connection flows need out of a connections API response.</summary>
public static class PowerAppsConnectionResource
{
    private const string ErrorStatus = "Error";

    private const string ConnectedStatus = "Connected";

    private static readonly string[] UnauthenticatedCodes = ["Unauthenticated", "Unauthorized"];

    /// <summary>Parses a response body, or returns <see langword="null"/> when it is not JSON.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The parsed document, or <see langword="null"/>.</returns>
    public static JsonDocument? Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body, PowerAppsConnectionBody.Limits);
        }
        catch (Exception failure) when (PowerAppsConnectionBody.IsUnreadable(failure))
        {
            return null;
        }
    }

    /// <summary>The resource's display name, when it carries one.</summary>
    /// <param name="resource">A connector or connection resource.</param>
    /// <returns>The display name, or <see langword="null"/>.</returns>
    public static string? DisplayNameOf(JsonElement resource) => Text(Properties(resource), "displayName");

    /// <summary>The link that signs a connection in through a browser, when the service offered one.</summary>
    /// <param name="consentResponse">A <c>getConsentLink</c> response.</param>
    /// <returns>The consent link, or <see langword="null"/>.</returns>
    public static Uri? ConsentLinkOf(JsonElement consentResponse) => Absolute(Text(consentResponse, "consentLink"));

    /// <summary>Whether a connection reports that it still needs to be signed in.</summary>
    /// <param name="connection">A connection resource.</param>
    /// <returns><see langword="true"/> when the service reported an unauthenticated status.</returns>
    public static bool ReportsUnauthenticated(JsonElement connection) =>
        FirstStatus(connection) is { } status && ErrorOf(status) is { } error && UnauthenticatedCodes.Contains(Text(error, "code"), StringComparer.OrdinalIgnoreCase);

    /// <summary>The error code a connection's first status reports, when it carries a failure.</summary>
    /// <param name="connection">A connection resource.</param>
    /// <returns>The reported error code, or <see langword="null"/> when the connection reports no failure.</returns>
    public static string? ErrorCodeOf(JsonElement connection)
    {
        if (FirstStatus(connection) is not { } status)
        {
            return null;
        }

        if (ErrorOf(status) is { } error)
        {
            var code = Text(error, "code");

            return string.IsNullOrWhiteSpace(code) ? ErrorStatus : code;
        }

        return string.Equals(Text(status, "status"), ErrorStatus, StringComparison.OrdinalIgnoreCase) ? ErrorStatus : null;
    }

    /// <summary>
    /// Whether a connection is usable as created, with nothing left to sign in or supply.
    ///
    /// A body that does not describe a connection at all proves nothing: an empty or unrecognised
    /// payload is not evidence that the connection exists, let alone that it is signed in.
    /// </summary>
    /// <param name="connection">A connection resource.</param>
    /// <returns><see langword="true"/> when the service described a connection carrying no failing status.</returns>
    public static bool ReportsReady(JsonElement connection)
    {
        if (Properties(connection) is not { ValueKind: JsonValueKind.Object } properties
            || !properties.TryGetProperty("statuses", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var described = false;

        foreach (var status in statuses.EnumerateArray())
        {
            if (status.ValueKind != JsonValueKind.Object
                || ErrorOf(status) is not null
                || !string.Equals(Text(status, "status"), ConnectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            described = true;
        }

        return described;
    }

    private static JsonElement Properties(JsonElement resource) =>
        resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object ? properties : default;

    private static JsonElement? FirstStatus(JsonElement connection)
    {
        if (Properties(connection) is not { ValueKind: JsonValueKind.Object } properties || !properties.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = statuses.EnumerateArray();

        return entries.MoveNext() && entries.Current.ValueKind == JsonValueKind.Object ? entries.Current : null;
    }

    private static JsonElement? ErrorOf(JsonElement status) =>
        status.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error : null;

    private static string? Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? TextOf(value) : null;

    private static string? TextOf(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (Exception failure) when (PowerAppsConnectionBody.IsUnreadable(failure))
        {
            return null;
        }
    }

    private static Uri? Absolute(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var parsed) ? parsed : null;
}
