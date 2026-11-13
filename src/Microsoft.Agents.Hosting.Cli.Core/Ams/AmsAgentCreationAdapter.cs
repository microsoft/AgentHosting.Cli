using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// The raw HTTP transport for AMS agent creation through either the Power Platform API front door
/// or a direct AMSDataPlane Dev endpoint.
/// </summary>
public sealed class AmsAgentCreationAdapter : IAmsAgentCreationTransport
{
    public const string ApiVersion = "1";
    public const string IdempotencyKeyHeaderName = "Idempotency-Key";

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly PpapiTokenClaimsValidator _claimsValidator;
    private readonly string _binaryName;
    private readonly IProfileResourceTokenProvider? _profileTokens;

    public AmsAgentCreationAdapter(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        PpapiTokenClaimsValidator? claimsValidator = null,
        string binaryName = "msagent",
        IProfileResourceTokenProvider? profileTokens = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _claimsValidator = claimsValidator ?? new PpapiTokenClaimsValidator();
        _binaryName = string.IsNullOrWhiteSpace(binaryName) ? "msagent" : binaryName;
        _profileTokens = profileTokens;
    }

    public async Task<AmsAgentCreationResult> CreateAsync(
        AmsAgentCreationTransportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = BuildCollectionUrl(request);

        if (_profileTokens is null) ResourceTokenContext.RequireBinding(_tokenProvider, request.Context);
        var token = _profileTokens is not null
            ? (await _profileTokens.AcquireAsync(ResourceTokenContext.Require(request.Context),
                request.Audience, request.TenantId, [PowerPlatformApiPermissions.AppsCreate], cancellationToken).ConfigureAwait(false)).Token
            : await _tokenProvider.AcquireTokenSilentAsync(request.Audience, request.TenantId, cancellationToken).ConfigureAwait(false);

        if (_profileTokens is null) ResourceTokenContext.ValidateLegacy(_claimsValidator, _tokenProvider, request.Context,
            token.AccessToken,
            request.Audience,
            request.TenantId,
            [PowerPlatformApiPermissions.AppsCreate]);

        var clientRequestId = Guid.NewGuid().ToString();
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation(
            AmsPackagePublicationAdapter.ClientRequestIdHeaderName,
            clientRequestId);
        message.Headers.TryAddWithoutValidation(IdempotencyKeyHeaderName, request.IdempotencyKey);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var body = JsonSerializer.Serialize(new CreateWireRequest
        {
            DisplayName = request.DisplayName,
            Description = request.Description,
            AgentType = request.AgentType,
            Language = request.Language,
            LanguageVersion = request.LanguageVersion,
            Origin = request.Origin,
        }, WireJson);
        message.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw AgentCreationException.TimedOut(clientRequestId, request.IdempotencyKey, ex);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                throw AgentCreationException.RedirectRejected((int)response.StatusCode, request.Endpoint.Host);
            }

            string responseText;
            try
            {
                responseText = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw AgentCreationException.TimedOut(clientRequestId, request.IdempotencyKey, ex);
            }
            catch (InvalidOperationException ex)
            {
                throw AgentCreationException.ResponseDecodingFailed(
                    clientRequestId,
                    request.IdempotencyKey,
                    ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw AmsAgentCreationErrorMap.Map(
                    (int)response.StatusCode,
                    responseText,
                    HttpMethod.Post.Method,
                    url,
                    _binaryName,
                    clientRequestId,
                    HeaderValue(response, "x-ms-request-id"));
            }

            return Parse(responseText);
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        (int)status is >= 300 and <= 399;

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    internal static string BuildCollectionUrl(AmsAgentCreationTransportRequest request)
    {
        var endpoint = request.Endpoint.ToString().TrimEnd('/');
        var path = request.RouteMode switch
        {
            AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor => "agenthosting/agents",
            AmsAgentCreationRouteMode.DirectDataPlane =>
                $"agenthosting/tenants/{Escape(request.TenantId)}/environments/{Escape(request.EnvironmentId)}/agents",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.RouteMode,
                "Unknown AMS agent-create route mode."),
        };

        return $"{endpoint}/{path}?api-version={ApiVersion}";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static AmsAgentCreationResult Parse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            return new AmsAgentCreationResult
            {
                AgentId = RequiredString(root, "id"),
                TenantId = RequiredString(root, "tenantId"),
                EnvironmentId = RequiredString(root, "environmentId"),
                DisplayName = RequiredString(root, "displayName"),
                RepositoryId = RequiredString(root, "repositoryId"),
            };
        }
        catch (JsonException)
        {
            throw AgentCreationException.InvalidResponse();
        }
        catch (InvalidOperationException)
        {
            throw AgentCreationException.InvalidResponse();
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidOperationException($"Missing required response property '{name}'.");
    }

    private sealed record CreateWireRequest
    {
        public required string DisplayName { get; init; }
        public string? Description { get; init; }
        public required int AgentType { get; init; }
        public required int Language { get; init; }
        public required string LanguageVersion { get; init; }
        public required string Origin { get; init; }
    }
}
