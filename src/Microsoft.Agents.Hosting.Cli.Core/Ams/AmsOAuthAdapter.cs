using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using static Microsoft.Agents.Hosting.Cli.Core.Ams.AmsJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Manages the OAuth connections of the Azure Bot Service instance AMS bound to a code-agent deployment, through the AMS control plane.</summary>
public sealed class AmsOAuthAdapter : IAmsOAuthTransport
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    private readonly AmsRequestSender _sender;

    /// <summary>Creates the adapter over a client whose handler must not follow redirects.</summary>
    public AmsOAuthAdapter(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator? claimsValidator = null, IProfileResourceTokenProvider? profileTokens = null) => _sender = new AmsRequestSender(httpClient, tokens, claimsValidator, profileTokens);

    /// <inheritdoc />
    public async Task<IReadOnlyList<OAuthProviderDefinition>> ListProvidersAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await SendAsync(HttpMethod.Get, AmsRoutes.OAuthProviders(request.ServiceTarget, request.AgentId, request.DeploymentId), request, PowerPlatformApiPermissions.AppsRead, "OAuth provider list", connectionName: null, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "OAuth provider list", "a valid provider list", root => GetRequiredArray(root, "value").Select(ParseProvider).ToArray());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentOAuthConnection>> ListAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await SendAsync(HttpMethod.Get, AmsRoutes.OAuthConnections(request.ServiceTarget, request.AgentId, request.DeploymentId), request, PowerPlatformApiPermissions.AppsRead, "OAuth connection list", connectionName: null, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "OAuth connection list", "a valid connection list", root => GetRequiredArray(root, "value").Select(ParseConnection).ToArray());
    }

    /// <inheritdoc />
    public async Task<DeploymentOAuthConnection> GetAsync(AmsOAuthConnectionTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await SendAsync(HttpMethod.Get, ConnectionRoute(request.Deployment, request.ConnectionName), request.Deployment, PowerPlatformApiPermissions.AppsRead, "OAuth connection read", request.ConnectionName, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "OAuth connection read", "a valid connection", ParseConnection);
    }

    /// <inheritdoc />
    public async Task<AmsOAuthCreateResult> CreateAsync(AmsOAuthCreateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        using var content = Content(request.Settings, request.ConnectionName, request.ServiceProviderName);
        var response = await SendAsync(HttpMethod.Post, AmsRoutes.OAuthConnections(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId), deployment, PowerPlatformApiPermissions.AppsUpdate, "OAuth connection create", request.ConnectionName, content, cancellationToken, outcomeMayBeIndeterminate: true).ConfigureAwait(false);
        return new AmsOAuthCreateResult(Parse(response, "OAuth connection create", "a valid connection", ParseConnection), IsCreated: response.StatusCode == HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<DeploymentOAuthConnection> UpdateAsync(AmsOAuthUpdateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var content = Content(request.Settings, name: null, serviceProviderName: null);
        var response = await SendAsync(HttpMethod.Patch, ConnectionRoute(request.Deployment, request.ConnectionName), request.Deployment, PowerPlatformApiPermissions.AppsUpdate, "OAuth connection update", request.ConnectionName, content, cancellationToken, outcomeMayBeIndeterminate: true).ConfigureAwait(false);
        return Parse(response, "OAuth connection update", "a valid connection", ParseConnection);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(AmsOAuthConnectionTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await SendAsync(HttpMethod.Delete, ConnectionRoute(request.Deployment, request.ConnectionName), request.Deployment, PowerPlatformApiPermissions.AppsUpdate, "OAuth connection delete", request.ConnectionName, content: null, cancellationToken, outcomeMayBeIndeterminate: true).ConfigureAwait(false);
    }

    private Task<AmsResponse> SendAsync(HttpMethod method, Uri uri, AmsDeploymentReadTransportRequest deployment, string permission, string phase, string? connectionName, HttpContent? content, CancellationToken cancellationToken, bool outcomeMayBeIndeterminate = false) =>
        _sender.SendAsync(AmsBotServiceCall.Create(method, uri, deployment, permission, phase, content, (status, code, serviceMessage) => Refusal(status, code, serviceMessage, connectionName), outcomeMayBeIndeterminate), cancellationToken);

    private static (string Message, int ExitCode, string ErrorKind, string Remediation)? Refusal(int status, string? code, string? serviceMessage, string? connectionName)
    {
        bool Is(string expected) => AmsBotServiceCall.IsCode(code, expected);
        return status switch
        {
            (int)HttpStatusCode.NotFound when Is("OAuthConnectionNotFound") => ($"The Azure Bot Service instance of the deployment has no OAuth connection named '{connectionName}'.", ExitCodes.Usage, "oauth-connection-not-found", "Create the connection with `msagent deployment oauth create`, or list the connections with `msagent deployment oauth list`."),
            (int)HttpStatusCode.BadRequest when Is("InvalidOAuthConnectionRequest") => ($"AMS refused the OAuth connection request: {serviceMessage ?? "it does not satisfy the rules of the provider."}", ExitCodes.Usage, "invalid-oauth-request", "Correct the request as the message describes. `msagent deployment oauth list-providers` lists the providers and parameters AMS accepts."),
            (int)HttpStatusCode.ServiceUnavailable when Is("AgentBotServiceOAuthFailed") => ("Azure Bot Service was temporarily unavailable, so AMS could not complete the OAuth operation.", ExitCodes.Failure, "oauth-operation-failed", "Retry the command. If it changed a connection, check the connection with `msagent deployment oauth show` before you change it again."),
            (int)HttpStatusCode.BadGateway when Is("AgentBotServiceOAuthFailed") => ("Azure Bot Service refused the OAuth operation AMS sent.", ExitCodes.Failure, "oauth-operation-failed", "Retry the command; if it fails again, ask the AMS team to investigate with the correlation identifiers."),
            _ => null,
        };
    }

    private static Uri ConnectionRoute(AmsDeploymentReadTransportRequest deployment, string connectionName) => AmsRoutes.OAuthConnection(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId, connectionName);

    private static JsonContent Content(AmsOAuthSettings settings, string? name, string? serviceProviderName) => JsonContent.Create(new ConnectionWireRequest { Name = name, ServiceProviderName = serviceProviderName, ClientId = settings.ClientId, ClientSecret = settings.ClientSecret, Scopes = settings.Scopes, Parameters = settings.Parameters.Count == 0 ? null : settings.Parameters }, options: RequestJson);

    private static T Parse<T>(AmsResponse response, string phase, string subject, Func<JsonElement, T> read) => AmsBotServiceCall.Read(response, phase, subject, "OAuth connection", read);

    private static OAuthProviderDefinition ParseProvider(JsonElement root)
    {
        var name = GetRequiredString(root, "serviceProviderName");
        return new()
        {
            Id = GetRequiredString(root, "id"),
            Name = name,
            DisplayName = GetOptionalString(root, "displayName") ?? name,
            Parameters = GetOptionalArray(root, "parameters")?.Select(ParseProviderParameter).ToArray() ?? [],
        };
    }

    private static OAuthProviderParameterDefinition ParseProviderParameter(JsonElement root)
    {
        var name = GetRequiredString(root, "name");
        return new()
        {
            Name = name,
            Type = GetOptionalString(root, "type") ?? "string",
            DisplayName = GetOptionalString(root, "displayName") ?? name,
            Description = GetOptionalString(root, "description"),
            DefaultValue = GetOptionalString(root, "default"),
            IsRequired = GetOptionalBoolean(root, "required") ?? false,
        };
    }

    private static DeploymentOAuthConnection ParseConnection(JsonElement root) => new()
    {
        ResourceId = GetRequiredString(root, "id"),
        ConnectionName = GetRequiredString(root, "name"),
        ServiceProviderId = GetRequiredString(root, "serviceProviderId"),
        ServiceProviderDisplayName = GetOptionalString(root, "serviceProviderDisplayName") ?? GetOptionalString(root, "serviceProviderName") ?? string.Empty,
        ClientId = GetOptionalString(root, "clientId"),
        Scopes = DeploymentOAuthParameters.ParseScopes(GetOptionalString(root, "scopes")),
        Parameters = ParseParameters(GetOptionalProperty(root, "parameters")),
    };

    private static Dictionary<string, string?> ParseParameters(JsonElement? parameters) => parameters switch
    {
        null => new(StringComparer.Ordinal),
        { ValueKind: JsonValueKind.Object } values => values.EnumerateObject().ToDictionary(parameter => parameter.Name, parameter => parameter.Value.ValueKind switch { JsonValueKind.String => parameter.Value.GetString(), JsonValueKind.Null => null, _ => throw new FormatException("parameters is invalid.") }, StringComparer.Ordinal),
        _ => throw new FormatException("parameters is invalid."),
    };

    private sealed record ConnectionWireRequest
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ServiceProviderName { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ClientId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ClientSecret { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Scopes { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyDictionary<string, string?>? Parameters { get; init; }
    }
}
