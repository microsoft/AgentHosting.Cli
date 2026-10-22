using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class ConnectionProbe
{
    private const string PowerAppsHost = "api.powerapps.com";
    private const string PowerAppsResource = "https://service.powerapps.com/";
    private const string ConnectedStatus = "Connected";
    private const int PageLimit = 50;

    private readonly AzTokens _tokens;
    private readonly Transcript _transcript;
    private readonly SessionCache<string, IReadOnlySet<string>> _usable = new();

    public ConnectionProbe(AzTokens tokens, Transcript transcript)
    {
        _tokens = tokens;
        _transcript = transcript;
    }

    public Task<IReadOnlySet<string>> UsableConnectorsAsync(string environmentId, CancellationToken cancellationToken) => _usable.GetAsync(environmentId, () => ListUsableAsync(environmentId, cancellationToken));

    private async Task<IReadOnlySet<string>> ListUsableAsync(string environmentId, CancellationToken cancellationToken)
    {
        DataverseProbe.RequireSafeSegment(environmentId, "environment id");

        var token = await _tokens.TokenAsync(PowerAppsResource, cancellationToken).ConfigureAwait(false);
        var signedIn = SignedInUser(token);
        var usable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri? next = new($"https://{PowerAppsHost}/providers/Microsoft.PowerApps/connections?api-version=2016-11-01&$filter=environment%20eq%20%27{environmentId}%27");

        for (var page = 0; next is not null && page < PageLimit; page++)
        {
            var response = await OracleHttp.GetAsync(next, token, _transcript, cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new E2EException($"The Power Apps API refused the connection listing: {(int)response.Status}.");
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;

            if (Property(root, "value") is { ValueKind: JsonValueKind.Array } connections)
            {
                usable.UnionWith(connections.EnumerateArray().Select(connection => UsableConnector(Property(connection, "properties"), signedIn)).OfType<string>());
            }

            next = OracleHttp.NextPage(Text(root, "nextLink"), next, "Power Apps");
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

    private static string SignedInUser(string token)
    {
        var parts = token.Split('.');

        if (parts.Length != 3)
        {
            throw new E2EException("az returned a Power Apps token that is not a JWT.");
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=')));

        return Text(document.RootElement, "upn") ?? Text(document.RootElement, "unique_name") ?? throw new E2EException("The Power Apps token names no signed-in user.");
    }

    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
