using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// The deployment module's OAuth-connection adapter — the real implementation of
/// <see cref="IDeploymentOAuthTransport"/> for the <c>msagent deployment oauth</c> subgroup: listing the
/// available Bot Service auth providers and reading/upserting/deleting a bot's OAuth connection
/// settings. It extends <see cref="DeploymentArmClient"/> for its transport (and inherits the shared
/// Azure resolvers), so it only implements the connection API calls here.
/// </summary>
public sealed class DeploymentOAuthAdapter : DeploymentArmClient, IDeploymentOAuthTransport
{
    /// <summary>Creates the OAuth-connection adapter.</summary>
    /// <param name="httpClient">The transport; a fake handler drives it offline in tests.</param>
    /// <param name="tokenProvider">The bearer-token seam, kept free of MSAL/Azure types.</param>
    /// <param name="config">The ARM target; defaults to Azure public cloud.</param>
    /// <param name="binaryName">The CLI name surfaced in error remediation text.</param>
    public DeploymentOAuthAdapter(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        DeploymentTargetConfig? config = null,
        string binaryName = "msagent")
        : base(httpClient, tokenProvider, config, binaryName)
    {
    }

    public async Task<DeploymentOAuthConnection?> GetOAuthConnectionAsync(
        string connectionName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url = OAuthConnectionUrl(botResourceId, connectionName);
        try
        {
            var body = await RequestAsync(
                HttpMethod.Get,
                url,
                body: null,
                targetTenantId,
                cancellationToken).ConfigureAwait(false);

            return MapConnection(ParseObject(body));
        }
        catch (TargetRequestException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<DeploymentOAuthConnection> UpsertOAuthConnectionAsync(
        BotServiceOAuthUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var url = OAuthConnectionUrl(request.BotResourceId, request.ConnectionName);

        var properties = new Dictionary<string, object?>
        {
            ["serviceProviderId"] = request.ServiceProviderId,
            ["parameters"] = request.Parameters
                .Select(p => new { key = p.Key, value = p.Value })
                .ToArray(),
        };

        if (request.ClientId is not null)
        {
            properties["clientId"] = request.ClientId;
        }

        if (request.ClientSecret is not null)
        {
            properties["clientSecret"] = request.ClientSecret;
        }

        if (request.Scopes is not null)
        {
            properties["scopes"] = request.Scopes;
        }

        var payload = new Dictionary<string, object?>
        {
            ["location"] = "global",
            ["properties"] = properties,
        };

        var body = await RequestAsync(
            HttpMethod.Put,
            url,
            payload,
            request.TenantId,
            cancellationToken).ConfigureAwait(false);

        return MapConnection(ParseObject(body));
    }

    public async Task<bool> DeleteOAuthConnectionAsync(
        string connectionName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url = OAuthConnectionUrl(botResourceId, connectionName);
        try
        {
            await RequestAsync(
                HttpMethod.Delete,
                url,
                body: null,
                targetTenantId,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TargetRequestException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<DeploymentOAuthConnection>> ListOAuthConnectionsAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var connections = new List<DeploymentOAuthConnection>();
        var next = OAuthConnectionsUrl(botResourceId);;
        while (!string.IsNullOrWhiteSpace(next))
        {
            var body = await RequestAsync(
                HttpMethod.Get,
                next,
                body: null,
                targetTenantId,
                cancellationToken).ConfigureAwait(false);
            connections.AddRange(ParseConnectionList(body));
            next = ParseNextLink(body);
        }

        return connections;
    }

    public async Task<IReadOnlyList<OAuthProviderDefinition>> ListServiceProvidersAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{Config.ArmEndpoint}/subscriptions/{Uri.EscapeDataString(subscriptionId.Trim())}" +
            $"/providers/Microsoft.BotService/listAuthServiceProviders?api-version={Config.BotServiceApiVersion}";
        var body = await RequestAsync(HttpMethod.Post, url, body: null, targetTenantId, cancellationToken).ConfigureAwait(false);

        return ParseServiceProviders(body);
    }

    private string OAuthConnectionUrl(string botResourceId, string connectionName)
    {
        var normalizedConnectionName = DeploymentOAuthPlan.ValidateConnectionName(connectionName);

        return
            $"{Config.ArmEndpoint}{BotServiceResourceId.ToRequestPath(botResourceId)}" +
            $"/connections/{Uri.EscapeDataString(normalizedConnectionName)}" +
            $"?api-version={Config.BotServiceApiVersion}";
    }

    private string OAuthConnectionsUrl(string botResourceId) =>
        $"{Config.ArmEndpoint}{BotServiceResourceId.ToRequestPath(botResourceId)}/connections" +
        $"?api-version={Config.BotServiceApiVersion}";

    private static DeploymentOAuthConnection MapConnection(JsonElement element)
    {
        var properties = GetObject(element, "properties", isOptional: false)!.Value;

        var connectionName = GetString(element, "name");
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            throw InvalidResponse("The response did not contain name");
        }

        var serviceProviderId = GetString(properties, "serviceProviderId");
        if (string.IsNullOrWhiteSpace(serviceProviderId))
        {
            throw InvalidResponse("The response did not contain properties.serviceProviderId");
        }

        var serviceProviderDisplayName = GetString(properties, "serviceProviderDisplayName");
        if (string.IsNullOrWhiteSpace(serviceProviderDisplayName))
        {
            throw InvalidResponse("The response did not contain properties.serviceProviderDisplayName");
        }

        return DeploymentOAuthParameters.Sanitize(
            new DeploymentOAuthConnection
            {
                ConnectionName = connectionName,
                ServiceProviderId = serviceProviderId,
                ServiceProviderDisplayName = serviceProviderDisplayName,
                ClientId = GetString(properties, "clientId"),
                Scopes = ParseScopes(GetString(properties, "scopes")),
                Parameters = ParseConnectionParameters(properties),
                ResourceId = GetString(element, "id")!,
                RawArmResponse = element.Clone(),
            },
            provider: null,
            includeArmResponse: true);
    }

    private static string[] ParseScopes(string? scopes) =>
        string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static Dictionary<string, string?> ParseConnectionParameters(JsonElement properties)
    {
        var parameters = GetArray(properties, "parameters", isOptional: true);
        if (parameters is null)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var parsed = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in parameters.Value.EnumerateArray())
        {
            var key = GetString(entry, "key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                parsed[key] = GetString(entry, "value");
            }
        }

        return parsed;
    }

    private static List<DeploymentOAuthConnection> ParseConnectionList(string? body)
    {
        var connections = new List<DeploymentOAuthConnection>();
        foreach (var element in ParseArray(body))
        {
            connections.Add(MapConnection(element));
        }

        return connections;
    }

    private static List<OAuthProviderDefinition> ParseServiceProviders(string? body)
    {
        var providers = new List<OAuthProviderDefinition>();
        foreach (var element in ParseArray(body))
        {
            var properties = GetObject(element, "properties", isOptional: true);
            if (properties is null)
            {
                continue;
            }

            var id = GetString(properties.Value, "id");
            var name = GetString(properties.Value, "serviceProviderName");
            var displayName = GetString(properties.Value, "displayName");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            providers.Add(new OAuthProviderDefinition
            {
                Id = id,
                Name = name,
                DisplayName = displayName,
                Parameters = ParseProviderParameters(properties.Value),
            });
        }

        return providers;
    }

    private static List<OAuthProviderParameterDefinition> ParseProviderParameters(JsonElement properties)
    {
        var parameters = GetArray(properties, "parameters", isOptional: true);
        if (parameters is null)
        {
            return [];
        }

        var results = new List<OAuthProviderParameterDefinition>();
        foreach (var parameter in parameters.Value.EnumerateArray())
        {
            var type = GetString(parameter, "type");
            var name = GetString(parameter, "name");
            var displayName = GetString(parameter, "displayName");
            if (string.IsNullOrWhiteSpace(type) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var required = false;
            var metadata = GetObject(parameter, "metadata", isOptional: true);
            var constraints = metadata is null
                ? null
                : GetObject(metadata.Value, "constraints", isOptional: true);
            if (constraints is not null &&
                constraints.Value.TryGetProperty("required", out var requiredElement) &&
                requiredElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                required = requiredElement.GetBoolean();
            }

            results.Add(new OAuthProviderParameterDefinition
            {
                Type = type,
                Name = name,
                DisplayName = displayName,
                Description = GetString(parameter, "description"),
                DefaultValue = GetScalar(parameter, "default"),
                IsRequired = required,
            });
        }

        return results;
    }

}
