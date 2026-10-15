using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Implements agent sharing over the Copilot Studio control plane's <c>shareV2</c> contract, which is
/// what the portal writes and reads. 
/// </summary>
public sealed class IslandSharingAdapter : IAgentSharingPort, IDisposable
{
    /// <summary>
    /// The delegated scope the control plane PVA accepts. 
    /// </summary>
    private const string DelegatedScope = "api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation";

    private const string ApplicationSource = "Web";
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ITokenProvider _tokenProvider;
    private readonly PowerPlatformCloudMap _cloudMap;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Lock _gate = new();

    private HttpMessageHandler? _sharedHandler;

    /// <summary>Creates the adapter.</summary>
    /// <param name="tokenProvider">Supplies control-plane tokens.</param>
    /// <param name="cloudMap">Decides which gateways this cloud serves, and may be given a token.</param>
    /// <param name="handlerFactory">Creates the transport, replaced in tests.</param>
    public IslandSharingAdapter(ITokenProvider tokenProvider, PowerPlatformCloudMap cloudMap, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _cloudMap = cloudMap ?? throw new ArgumentNullException(nameof(cloudMap));
        _handlerFactory = handlerFactory ?? TransportDefaults.CreateRedirectRejectingHandler;
    }

    /// <summary>Disposes the transport shared by every request this adapter sent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _sharedHandler?.Dispose();
            _sharedHandler = null;
        }
    }

    /// <summary>
    /// The transport every request sends through.
    /// </summary>
    private HttpMessageHandler SharedHandler()
    {
        lock (_gate)
        {
            return _sharedHandler ??= _handlerFactory();
        }
    }

    /// <inheritdoc />
    public async Task<AgentSharingState> ReadAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        var operation = OperationOf(HttpMethod.Get, agentId);

        return Parse(await SendAsync(environment, agentId, HttpMethod.Get, body: null, operation, cancellationToken).ConfigureAwait(false), operation);
    }

    /// <inheritdoc />
    public async Task<AgentSharingState> WriteAsync(CopilotEnvironmentContext environment, string agentId, AgentSharingState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var payload = await SendAsync(environment, agentId, HttpMethod.Post, ToRequestBody(state), OperationOf(HttpMethod.Post, agentId), cancellationToken).ConfigureAwait(false);

        return TryParse(payload) ?? await ReadAsync(environment, agentId, cancellationToken).ConfigureAwait(false);
    }

    private static string OperationOf(HttpMethod method, string agentId) => $"{method.Method} shareV2 for agent {agentId}";

    private async Task<string> SendAsync(CopilotEnvironmentContext environment, string agentId, HttpMethod method, string? body, string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var advertised = environment.AgentManagementUrl ?? throw CopilotStudioException.AgentManagementEndpointMissing(environment.EnvironmentId);
        var endpoint = _cloudMap.RequireTrustedAgentManagementGateway(advertised, $"environment '{environment.EnvironmentId}' Copilot Studio agent-management");
        var token = AcquiredTokenTenant.Require(
            await _tokenProvider.AcquireTokenSilentAsync([DelegatedScope], environment.TenantId, cancellationToken).ConfigureAwait(false),
            environment.TenantId);

        var url = new Uri(endpoint, $"api/botmanagement/v1/environments/{Uri.EscapeDataString(environment.EnvironmentId)}/bots/{Uri.EscapeDataString(agentId)}/shareV2");

        using var client = new HttpClient(SharedHandler(), disposeHandler: false);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add("Accept", "application/json");

        foreach (var (name, value) in RoutingHeaders(environment, agentId))
        {
            request.Headers.Add(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? payload
            : throw CopilotStudioException.ServiceError(operation, Describe(response, payload));
    }

    /// <summary>
    /// The routing headers the control-plane gateway requires. 
    /// </summary>
    private static IEnumerable<(string Name, string Value)> RoutingHeaders(CopilotEnvironmentContext environment, string agentId) =>
    [
        ("x-cci-tenantid", environment.TenantId),
        ("x-cci-routing-tenantid", environment.TenantId),
        ("x-cci-botid", agentId),
        ("x-cci-cdsbotid", agentId),
        ("x-cci-routing-botid", agentId),
        ("x-cci-applicationsource", ApplicationSource),
    ];

    private static string ToRequestBody(AgentSharingState state) => JsonSerializer.Serialize(
        new ShareV2Payload
        {
            ShareWithOrg = state.ShareWithOrg,
            StoreDiscoverable = state.StoreDiscoverable,
            EnableActivitySharing = state.EnableActivitySharing,
            Targets =
            [
                .. state.Targets.Select(target => new ShareV2Target
                {
                    ObjectId = target.ObjectId,
                    ObjectIdType = target.ObjectIdType,
                    SharingType = target.SharingTypes,
                    ClientId = target.ClientId,
                }),
            ],
        },
        SerializerOptions);

    /// <summary>
    /// Projects the state the service reported.
    /// </summary>
    private static AgentSharingState Parse(string payload, string operation) =>
        TryParse(payload) ?? throw CopilotStudioException.SharingStateNotReturned(operation);

    /// <summary>The state a payload describes, or <see langword="null"/> when it describes none.</summary>
    private static AgentSharingState? TryParse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || TryParseDocument(payload) is not { } document)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("shareWithOrg", out var shareWithOrg)
                || shareWithOrg.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || ReadFlag(root, "storeDiscoverable") is not { } storeDiscoverable
                || ReadFlag(root, "enableActivitySharing") is not { } enableActivitySharing
                || !root.TryGetProperty("targets", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var targets = new List<AgentShareTarget>();

            foreach (var value in values.EnumerateArray())
            {
                if (ToTarget(value) is not { } target)
                {
                    return null;
                }

                targets.Add(target);
            }

            return new AgentSharingState
            {
                ShareWithOrg = shareWithOrg.ValueKind == JsonValueKind.True,
                StoreDiscoverable = storeDiscoverable,
                EnableActivitySharing = enableActivitySharing,
                Targets = targets,
            };
        }
    }

    private static JsonDocument? TryParseDocument(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A boolean setting the snapshot carries.
    /// </summary>
    private static bool? ReadFlag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => null,
        };
    }

    /// <summary>
    /// Projects one target, keeping the access levels the service spelled rather than the subset this build understands.
    /// </summary>
    private static AgentShareTarget? ToTarget(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || DataverseWebApiClient.ReadString(value, "objectId") is not { } objectId
            || !Guid.TryParse(objectId, out var principal)
            || !value.TryGetProperty("sharingType", out var sharingTypes)
            || sharingTypes.ValueKind != JsonValueKind.Array
            || sharingTypes.EnumerateArray().Any(sharingType => sharingType.ValueKind != JsonValueKind.String)
            || ReadOptionalString(value, "objectIdType") is not { } objectIdType
            || ReadOptionalString(value, "clientId") is not { } clientId)
        {
            return null;
        }

        return new AgentShareTarget
        {
            ObjectId = principal.ToString(),
            SharingTypes = [.. sharingTypes.EnumerateArray().Select(sharingType => sharingType.GetString()!)],
            ObjectIdType = objectIdType.Value ?? AgentShareTarget.PrincipalObjectIdType,
            ClientId = clientId.Value,
        };
    }

    /// <summary>
    /// A string field the target may omit, wrapped so an absent value and one this build cannot read.
    /// </summary>
    private static Optional<string?>? ReadOptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field))
        {
            return new Optional<string?>(null);
        }

        return field.ValueKind switch
        {
            JsonValueKind.String => new Optional<string?>(field.GetString()),
            JsonValueKind.Null => new Optional<string?>(null),
            _ => null,
        };
    }

    private readonly record struct Optional<T>(T Value);

    /// <summary>
    /// Describes a failed response without echoing the body.
    /// </summary>
    private static string Describe(HttpResponseMessage response, string payload)
    {
        var status = string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}.");

        if (string.IsNullOrWhiteSpace(payload))
        {
            return status;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return status;
            }

            if (root.TryGetProperty("Error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("Code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return error.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.String
                    ? $"{status} {code.GetString()}: {message.GetString()}"
                    : $"{status} {code.GetString()}";
            }

            return DescribeValidationFailure(root) is { } validation ? $"{status} {validation}" : status;
        }
        catch (JsonException)
        {
            return status;
        }
    }

    /// <summary>The validation summary and the fields it faulted, or null when the payload is not one.</summary>
    private static string? DescribeValidationFailure(JsonElement root)
    {
        if (root.TryGetProperty("title", out var title) is false || title.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (root.TryGetProperty("errors", out var errors) is false || errors.ValueKind != JsonValueKind.Object)
        {
            return title.GetString();
        }

        var fields = errors.EnumerateObject().Select(field => field.Name).ToArray();

        return fields.Length == 0 ? title.GetString() : $"{title.GetString()} Fields: {string.Join(", ", fields)}.";
    }

    private sealed record ShareV2Payload
    {
        public bool ShareWithOrg { get; init; }

        public object? SendNotification { get; init; }

        public object? SendTeamsNotification { get; init; }

        public bool StoreDiscoverable { get; init; }

        public IReadOnlyList<ShareV2Target> Targets { get; init; } = [];

        public object? DeltaToken { get; init; }

        public bool EnableActivitySharing { get; init; }

        public bool IsApprovedFromRequest { get; init; }
    }

    private sealed record ShareV2Target
    {
        public required string ObjectId { get; init; }

        public string ObjectIdType { get; init; } = "Object";

        public required IReadOnlyList<string> SharingType { get; init; }

        public object? ClientId { get; init; }
    }
}
