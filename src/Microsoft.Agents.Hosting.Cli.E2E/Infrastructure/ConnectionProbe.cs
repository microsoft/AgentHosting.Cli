using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class ConnectionProbe
{
    private const string ConnectedStatus = "Connected";
    private const int PageLimit = 50;

    private readonly IAccessTokens _tokens;
    private readonly Transcript _transcript;
    private readonly SessionCache<string, IReadOnlySet<string>> _usable = new();
    private readonly PowerPlatformApiTargetConfig _target;
    private readonly HttpMessageInvoker? _client;

    public ConnectionProbe(IAccessTokens tokens, Transcript transcript, PowerPlatformApiTargetConfig? target = null, HttpMessageInvoker? client = null)
    {
        _tokens = tokens;
        _transcript = transcript;
        _target = target ?? PowerPlatformApiTargetConfig.Public;
        _client = client;
    }

    public Task<IReadOnlySet<string>> UsableConnectorsAsync(string environmentId, CancellationToken cancellationToken) => _usable.GetAsync(environmentId, () => ListUsableAsync(environmentId, cancellationToken));

    private async Task<IReadOnlySet<string>> ListUsableAsync(string environmentId, CancellationToken cancellationToken)
    {
        DataverseProbe.RequireSafeSegment(environmentId, "environment id");

        var token = await _tokens.TokenAsync(_target.Audience, cancellationToken).ConfigureAwait(false);
        var signedIn = SignedInUser(token);
        var usable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri? next = new(PowerPlatformApiDiscovery.GetEnvironmentEndpoint(_target, environmentId), $"connectivity/connections?api-version=1&$filter=environment%20eq%20%27{environmentId}%27");

        for (var page = 0; next is not null && page < PageLimit; page++)
        {
            var response = await OracleHttp.GetAsync(next, token, _transcript, cancellationToken, _client).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new E2EException($"The Power Platform API refused the connection listing: {(int)response.Status}.");
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;

            if (Property(root, "value") is { ValueKind: JsonValueKind.Array } connections)
            {
                usable.UnionWith(connections.EnumerateArray().Select(connection => UsableConnector(Property(connection, "properties"), signedIn)).OfType<string>());
            }

            next = OracleHttp.NextPage(Text(root, "nextLink"), next, "Power Platform");
        }

        return usable;
    }

    private static string? UsableConnector(JsonElement properties, string signedIn)
    {
        var connected = Property(properties, "statuses") is { ValueKind: JsonValueKind.Array } statuses && statuses.EnumerateArray().Any(status => string.Equals(Text(status, "status"), ConnectedStatus, StringComparison.OrdinalIgnoreCase));

        return connected && string.Equals(Text(Property(properties, "createdBy"), "userPrincipalName"), signedIn, StringComparison.OrdinalIgnoreCase)
            ? Text(properties, "apiId")?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            : null;
    }

    private static string SignedInUser(string token) => AccessTokenPayload.Read(token) is { } payload ? Text(payload, "upn") ?? Text(payload, "unique_name") ?? throw new E2EException("The Power Platform API token names no signed-in user.") : throw new E2EException("The Power Platform API token is not a JWT.");

    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
