using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public interface IAccessTokens
{
    Task<string> TokenAsync(string resource, CancellationToken cancellationToken);
}

public sealed class AzTokens : IAccessTokens
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly string? _tenantId;
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresOn)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public AzTokens(string? tenantId) => _tenantId = tenantId;

    public async Task<string> TokenAsync(string resource, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(resource, out var cached) && cached.ExpiresOn - RefreshMargin > DateTimeOffset.UtcNow)
            {
                return cached.Token;
            }
        }

        var executable = AzureCli.Path ?? throw new E2EException("az was not found, so the standalone-workflow oracle has no token.");
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string[] arguments = _tenantId is { Length: > 0 } tenant
            ? ["account", "get-access-token", "--resource", resource, "--tenant", tenant, "--output", "json"]
            : ["account", "get-access-token", "--resource", resource, "--output", "json"];

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = await ProcessRunner
            .RunAsync(startInfo, arguments, stdin: null, TimeSpan.FromMinutes(2), transcript: null, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new E2EException($"az account get-access-token --resource {resource} exited {result.ExitCode}: {FirstLine(result.StdErr)}");
        }

        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement;

        var token = root.TryGetProperty("accessToken", out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new E2EException($"az returned no access token for {resource}.");

        var expiresOn = ExpiresOn(root) ?? DateTimeOffset.UtcNow.AddMinutes(30);

        lock (_gate)
        {
            _cache[resource] = (token, expiresOn);
        }

        return token;
    }

    private static DateTimeOffset? ExpiresOn(JsonElement root)
    {
        if (!root.TryGetProperty("expires_on", out var epoch))
        {
            return null;
        }

        if (epoch.ValueKind == JsonValueKind.Number && epoch.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return epoch.ValueKind == JsonValueKind.String
            && long.TryParse(epoch.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? DateTimeOffset.FromUnixTimeSeconds(parsed)
                : null;
    }

    private static string FirstLine(string value) => Redaction.Apply(value.Trim().Split('\n')[0].Trim());
}

public sealed record OracleResponse(HttpStatusCode Status, string Body)
{
    public bool Ok => (int)Status is >= 200 and < 300;

    public bool NotFound => Status == HttpStatusCode.NotFound;
}

public static partial class OracleHttp
{
    private const string FlowApiHost = "api.flow.microsoft.com";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(100) };

    public static async Task<OracleResponse> GetAsync(Uri uri, string token, Transcript? transcript, CancellationToken cancellationToken, HttpMessageInvoker? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        return await SendAsync(request, token, transcript, cancellationToken, client).ConfigureAwait(false);
    }

    public static async Task<OracleResponse> WriteAsync(HttpMethod method, Uri uri, string? json, string token, Transcript? transcript, CancellationToken cancellationToken, HttpMessageInvoker? client = null)
    {
        if ((method != HttpMethod.Patch && method != HttpMethod.Delete) || !WritableRow().IsMatch(uri.AbsolutePath))
        {
            throw new E2EException($"Refused a {method} request to {uri.GetLeftPart(UriPartial.Path)}: the oracle writes only a bot, a bot component, a flow, a connector or a connection reference, each by its id.");
        }

        using var request = new HttpRequestMessage(method, uri) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

        return await SendAsync(request, token, transcript, cancellationToken, client).ConfigureAwait(false);
    }

    public static async Task<OracleResponse> DeleteRepositoryAsync(Uri gitUrl, string token, Transcript? transcript, CancellationToken cancellationToken, HttpMessageInvoker? client = null)
    {
        if (!GrsUrls.IsRepository(gitUrl))
        {
            throw new E2EException($"Refused a DELETE request to {gitUrl.GetLeftPart(UriPartial.Path)}: the oracle deletes only a GRS repository, by its git URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, gitUrl);

        return await SendAsync(request, token, transcript, cancellationToken, client).ConfigureAwait(false);
    }

    public static async Task<HttpStatusCode> ReachAsync(Uri uri, TimeSpan timeout, Transcript transcript, CancellationToken cancellationToken, HttpMessageInvoker? client = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await (client ?? Client).SendAsync(request, limit.Token).ConfigureAwait(false);

        transcript.Note($"reach GET {uri.GetLeftPart(UriPartial.Path)} -> {(int)response.StatusCode}");
        return response.StatusCode;
    }

    public static Uri? NextPage(string? nextLink, Uri currentPage, string apiName) =>
        nextLink is not { Length: > 0 }
            ? null
            : Uri.TryCreate(nextLink, UriKind.Absolute, out var next) && next.Scheme == Uri.UriSchemeHttps && next.IsDefaultPort && next.UserInfo.Length == 0 && string.Equals(next.Host, currentPage.Host, StringComparison.OrdinalIgnoreCase)
                ? next
                : throw new E2EException($"The {apiName} API returned an untrusted pagination URL.");

    internal static async Task<OracleResponse> PostFlowRunAsync(Uri uri, string token, Transcript? transcript, CancellationToken cancellationToken)
    {
        if (!string.Equals(uri.Host, FlowApiHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith("/run", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Contains("/triggers/", StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException($"Refused a non-GET request to {uri.GetLeftPart(UriPartial.Path)}: the oracle only reads, apart from starting a run of a flow the suite owns.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        return await SendAsync(request, token, transcript, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OracleResponse> SendAsync(HttpRequestMessage request, string token, Transcript? transcript, CancellationToken cancellationToken, HttpMessageInvoker? client = null)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var stopwatch = Stopwatch.StartNew();
        using var response = await (client ?? Client).SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        transcript?.Note($"oracle {request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)} -> {(int)response.StatusCode} in {stopwatch.Elapsed.TotalSeconds:N1}s");

        return new OracleResponse(response.StatusCode, body);
    }

    [GeneratedRegex(@"^/api/data/v9\.2/(bots|botcomponents|workflows|connectors|connectionreferences)\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)$", RegexOptions.IgnoreCase)]
    private static partial Regex WritableRow();
}

public sealed record DataverseWorkflow
{
    public required string WorkflowId { get; init; }

    public string? Name { get; init; }

    public int StateCode { get; init; }

    public int StatusCode { get; init; }

    public int Category { get; init; }

    public DateTimeOffset? CreatedOn { get; init; }

    public DateTimeOffset? ModifiedOn { get; init; }

    public string? Description { get; init; }

    public string? ClientData { get; init; }

    public bool IsActivated => StateCode == 1;
}

public sealed record DataverseConnectionReference
{
    public required string Id { get; init; }

    public required string LogicalName { get; init; }

    public string? ConnectorId { get; init; }

    public string? ConnectionId { get; init; }

    public int StateCode { get; init; }

    public bool Bound => ConnectionId is { Length: > 0 };
}

public sealed record DataverseBot
{
    public required string BotId { get; init; }

    public string? Name { get; init; }

    public string? SchemaName { get; init; }

    public string? Template { get; init; }
}

public sealed record DataverseBotComponent
{
    public required string ComponentId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }
}

public sealed record DataverseConnector
{
    public required string ConnectorId { get; init; }

    public string? Name { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? InternalId { get; init; }

    public static string Api(string internalId) => $"/providers/Microsoft.PowerApps/apis/{internalId}";
}

public sealed class DataverseProbe
{
    private const string PowerAppsResource = "https://service.powerapps.com/";
    private const string WorkflowColumns = "workflowid,name,statecode,statuscode,category,createdon,modifiedon,description";
    private const string ReferenceColumns = "connectionreferenceid,connectionreferencelogicalname,connectorid,connectionid,statecode";
    private const string UnmanagedFlowFilter = "category eq 5 and type eq 1 and ismanaged eq false";
    private const string ManagedFlowFilter = "category eq 5 and type eq 1 and ismanaged eq true";
    private const string BotColumns = "botid,name,schemaname,template";
    private const string ComponentColumns = "botcomponentid,name,description";
    private const string ConnectorColumns = "connectorid,name,displayname,description,connectorinternalid";
    private const string ConnectorDefinitionColumns = "name,displayname,description,openapidefinition,connectionparameters,connectionparametersets,policytemplateinstances,iconbrandcolor,iconblob";
    private const string DefaultPublisherPrefix = "new";
    private const int BotDeleteAttempts = 5;

    private readonly IAccessTokens _tokens;
    private readonly Transcript _transcript;
    private readonly Dictionary<string, Uri> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly HttpMessageInvoker? _client;

    public DataverseProbe(IAccessTokens tokens, Transcript transcript, IReadOnlyDictionary<string, Uri>? organizations = null, HttpMessageInvoker? client = null)
    {
        _tokens = tokens;
        _transcript = transcript;
        _client = client;

        foreach (var (environmentId, organization) in organizations ?? new Dictionary<string, Uri>())
        {
            _instances[environmentId] = organization;
        }
    }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<Uri> InstanceAsync(string environmentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(environmentId, out var known))
            {
                return known;
            }
        }

        RequireSafeSegment(environmentId, "environment id");

        var token = await _tokens.TokenAsync(PowerAppsResource, cancellationToken).ConfigureAwait(false);
        var uri = new Uri($"https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments/{environmentId}?api-version=2021-04-01");
        var response = await OracleHttp.GetAsync(uri, token, _transcript, cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new E2EException($"The environment lookup for {environmentId} returned {(int)response.Status}.");
        }

        using var document = JsonDocument.Parse(response.Body);

        var instance = document.RootElement.TryGetProperty("properties", out var properties)
            && properties.TryGetProperty("linkedEnvironmentMetadata", out var linked)
            && linked.TryGetProperty("instanceUrl", out var address)
            && address.GetString() is { Length: > 0 } text
            && Uri.TryCreate(text, UriKind.Absolute, out var parsed)
                ? parsed
                : throw new E2EException($"Environment {environmentId} has no Dataverse instance.");

        lock (_gate)
        {
            _instances[environmentId] = instance;
        }

        return instance;
    }

    public async Task<bool> CanReadAsync(string environmentId, CancellationToken cancellationToken) =>
        (await GetAsync(environmentId, "WhoAmI", cancellationToken).ConfigureAwait(false)).Ok;

    public async Task<DataverseWorkflow?> WorkflowAsync(string environmentId, string workflowId, bool includeDefinition, CancellationToken cancellationToken)
    {
        var id = Guid.TryParse(workflowId, out var parsed)
            ? parsed
            : throw new E2EException($"'{workflowId}' is not a workflow id.");

        var columns = includeDefinition ? WorkflowColumns + ",clientdata" : WorkflowColumns;
        var response = await GetAsync(environmentId, $"workflows({id:D})?$select={columns}", cancellationToken).ConfigureAwait(false);

        if (response.NotFound)
        {
            return null;
        }

        Require(response, "the workflow read");

        using var document = JsonDocument.Parse(response.Body);
        return ReadWorkflow(document.RootElement);
    }

    public Task<IReadOnlyList<DataverseWorkflow>> WorkflowsNamedAsync(string environmentId, string name, CancellationToken cancellationToken) =>
        WorkflowsWhereAsync(environmentId, $"name eq '{Literal(name)}' and category eq 5", cancellationToken);

    public Task<IReadOnlyList<DataverseWorkflow>> WorkflowsPrefixedAsync(string environmentId, string prefix, CancellationToken cancellationToken) =>
        WorkflowsWhereAsync(environmentId, $"startswith(name,'{Literal(prefix)}') and category eq 5", cancellationToken);

    public Task<IReadOnlyList<DataverseWorkflow>> UnmanagedFlowsAsync(string environmentId, CancellationToken cancellationToken) =>
        WorkflowsWhereAsync(environmentId, UnmanagedFlowFilter, cancellationToken);

    public async Task<DataverseWorkflow?> ManagedFlowAsync(string environmentId, CancellationToken cancellationToken) =>
        (await WorkflowsWhereAsync(environmentId, ManagedFlowFilter, cancellationToken).ConfigureAwait(false)) is [var first, ..] ? first : null;

    public async Task<DataverseConnectionReference?> ConnectionReferenceAsync(string environmentId, string logicalName, CancellationToken cancellationToken) =>
        (await ConnectionReferencesWhereAsync(environmentId, $"connectionreferencelogicalname eq '{Literal(logicalName)}'", cancellationToken).ConfigureAwait(false))
            is [var first, ..] ? first : null;

    public Task<IReadOnlyList<DataverseConnectionReference>> ConnectionReferencesPrefixedAsync(string environmentId, string prefix, CancellationToken cancellationToken) =>
        ConnectionReferencesWhereAsync(environmentId, $"startswith(connectionreferencelogicalname,'{Literal(prefix)}')", cancellationToken);

    public Task<IReadOnlyList<DataverseBot>> BotsNamedAsync(string environmentId, string schemaName, CancellationToken cancellationToken) => RowsAsync(environmentId, $"bots?$select={BotColumns}&$filter={Uri.EscapeDataString($"schemaname eq '{Literal(schemaName)}'")}", ReadBot, "the bot query", cancellationToken);

    public Task<DataverseBot?> BotAsync(string environmentId, string botId, CancellationToken cancellationToken) => RowAsync(environmentId, $"bots({RowId(botId, "bot")})?$select={BotColumns}", ReadBot, "the bot read", cancellationToken);

    public Task<IReadOnlyList<string>> BotComponentIdsAsync(string environmentId, string botId, CancellationToken cancellationToken) => RowsAsync(environmentId, $"botcomponents?$select=botcomponentid&$filter={Uri.EscapeDataString($"_parentbotid_value eq {RowId(botId, "bot")}")}", row => Text(row, "botcomponentid") ?? string.Empty, "the bot component query", cancellationToken);

    public async Task<IReadOnlyList<DataverseWorkflow>> FlowsNamedExactlyAsync(string environmentId, string name, CancellationToken cancellationToken) => [.. (await WorkflowsNamedAsync(environmentId, name, cancellationToken).ConfigureAwait(false)).Where(flow => string.Equals(flow.Name, name, StringComparison.Ordinal))];

    public Task<DataverseBot?> RenameBotAsync(string environmentId, string botId, string name, CancellationToken cancellationToken) => PatchAsync(environmentId, $"bots({RowId(botId, "bot")})", new JsonObject { ["name"] = name }, "the bot rename", () => BotAsync(environmentId, botId, cancellationToken), cancellationToken);

    public Task<DataverseWorkflow?> RenameFlowAsync(string environmentId, string workflowId, string name, CancellationToken cancellationToken) => PatchAsync(environmentId, $"workflows({RowId(workflowId, "workflow")})", new JsonObject { ["name"] = name }, "the flow rename", () => WorkflowAsync(environmentId, workflowId, includeDefinition: false, cancellationToken), cancellationToken);

    public async Task<DataverseBotComponent?> EditComponentDescriptionAsync(string environmentId, string botId, string name, string description, CancellationToken cancellationToken)
    {
        var named = await RowsAsync(environmentId, $"botcomponents?$select={ComponentColumns}&$filter={Uri.EscapeDataString($"_parentbotid_value eq {RowId(botId, "bot")} and name eq '{Literal(name)}'")}", ReadComponent, "the bot component query", cancellationToken).ConfigureAwait(false);
        var row = named is [var component] ? $"botcomponents({RowId(component.ComponentId, "bot component")})" : throw new E2EException($"Bot {botId} holds {named.Count} components named {name}, not one.");

        return await PatchAsync(environmentId, row, new JsonObject { ["description"] = description }, "the bot component edit", () => RowAsync(environmentId, $"{row}?$select={ComponentColumns}", ReadComponent, "the bot component read", cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PublisherPrefixAsync(string environmentId, CancellationToken cancellationToken) => (await RowsAsync(environmentId, $"publishers?$select=customizationprefix&$filter={Uri.EscapeDataString("isreadonly eq false")}", row => Text(row, "customizationprefix"), "the publisher query", cancellationToken).ConfigureAwait(false)).FirstOrDefault(prefix => prefix is { Length: > 0 } && !string.Equals(prefix, DefaultPublisherPrefix, StringComparison.OrdinalIgnoreCase)) ?? throw new E2EException($"Environment {environmentId} has no writable publisher whose prefix isn't {DefaultPublisherPrefix}, so a custom connector can't be named.");

    public Task<DataverseConnector?> ConnectorAsync(string environmentId, string connectorId, CancellationToken cancellationToken) => RowAsync(environmentId, $"connectors({RowId(connectorId, "connector")})?$select={ConnectorColumns}", ReadConnector, "the connector read", cancellationToken);

    public Task<IReadOnlyList<DataverseConnector>> ConnectorsNamedAsync(string environmentId, string name, CancellationToken cancellationToken) => RowsAsync(environmentId, $"connectors?$select={ConnectorColumns}&$filter={Uri.EscapeDataString($"name eq '{Literal(name)}'")}", ReadConnector, "the connector query", cancellationToken);

    public async Task<DataverseConnector?> EditConnectorDescriptionAsync(string environmentId, string connectorId, string description, CancellationToken cancellationToken)
    {
        var row = $"connectors({RowId(connectorId, "connector")})";
        var current = await GetAsync(environmentId, $"{row}?$select={ConnectorDefinitionColumns}", cancellationToken).ConfigureAwait(false);
        Require(current, "the connector read");
        var read = JsonNode.Parse(current.Body)!.AsObject();
        var body = new JsonObject(ConnectorDefinitionColumns.Split(',').Where(column => read[column] is not null).Select(column => KeyValuePair.Create<string, JsonNode?>(column, read[column]!.DeepClone())));
        body["description"] = description;

        return await PatchAsync(environmentId, row, body, "the connector edit", () => ConnectorAsync(environmentId, connectorId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataverseConnectionReference?> PointConnectionReferenceAsync(string environmentId, string logicalName, string connectorInternalId, CancellationToken cancellationToken)
    {
        var reference = await ConnectionReferenceAsync(environmentId, logicalName, cancellationToken).ConfigureAwait(false) ?? throw new E2EException($"Environment {environmentId} holds no connection reference named {logicalName}.");

        return await PatchAsync(environmentId, $"connectionreferences({RowId(reference.Id, "connection reference")})", new JsonObject { ["connectorid"] = DataverseConnector.Api(connectorInternalId) }, "the connection reference edit", () => ConnectionReferenceAsync(environmentId, logicalName, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DataverseConnector>> DeleteConnectorsNamedAsync(string environmentId, string name, CancellationToken cancellationToken) => DeleteRowsAsync(environmentId, () => ConnectorsNamedAsync(environmentId, name, cancellationToken), connector => Task.FromResult($"connectors({RowId(connector.ConnectorId, "connector")})"), "the connector delete", cancellationToken);

    public Task<IReadOnlyList<DataverseConnectionReference>> DeleteConnectionReferencesNamedAsync(string environmentId, string logicalName, CancellationToken cancellationToken) => DeleteRowsAsync(environmentId, () => ConnectionReferencesNamedExactlyAsync(environmentId, logicalName, cancellationToken), reference => Task.FromResult($"connectionreferences({RowId(reference.Id, "connection reference")})"), "the connection reference delete", cancellationToken);

    public async Task<bool> DeleteBotAsync(string environmentId, string botId, string schemaName, CancellationToken cancellationToken)
    {
        var id = RowId(botId, "bot");

        if (await BotAsync(environmentId, id, cancellationToken).ConfigureAwait(false) is not { } bot)
        {
            return false;
        }

        if (!string.Equals(bot.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException($"Refused to delete bot {id}: its schema name is {bot.SchemaName}, not {schemaName}.");
        }

        foreach (var component in await BotComponentIdsAsync(environmentId, id, cancellationToken).ConfigureAwait(false))
        {
            Require(Gone(await WriteAsync(environmentId, HttpMethod.Delete, $"botcomponents({RowId(component, "bot component")})", null, cancellationToken).ConfigureAwait(false)), "the bot component delete");
        }

        for (var attempt = 1; ; attempt++)
        {
            var deleted = Gone(await WriteAsync(environmentId, HttpMethod.Delete, $"bots({id})", null, cancellationToken).ConfigureAwait(false));

            if (deleted.Ok || attempt == BotDeleteAttempts)
            {
                Require(deleted, "the bot delete");
                return true;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<DataverseWorkflow>> DeleteFlowsNamedAsync(string environmentId, string name, CancellationToken cancellationToken) => DeleteRowsAsync(environmentId, () => FlowsNamedExactlyAsync(environmentId, name, cancellationToken), flow => TurnedOffAsync(environmentId, flow, cancellationToken), "the flow delete", cancellationToken);

    private async Task<string> TurnedOffAsync(string environmentId, DataverseWorkflow flow, CancellationToken cancellationToken)
    {
        var row = $"workflows({RowId(flow.WorkflowId, "workflow")})";

        if (flow.IsActivated)
        {
            Require(await WriteAsync(environmentId, HttpMethod.Patch, row, new JsonObject { ["statecode"] = 0, ["statuscode"] = 1 }, cancellationToken).ConfigureAwait(false), "turning the flow off");
        }

        return row;
    }

    private async Task<IReadOnlyList<T>> DeleteRowsAsync<T>(string environmentId, Func<Task<IReadOnlyList<T>>> rows, Func<T, Task<string>> row, string what, CancellationToken cancellationToken)
    {
        foreach (var each in await rows().ConfigureAwait(false))
        {
            Require(Gone(await WriteAsync(environmentId, HttpMethod.Delete, await row(each).ConfigureAwait(false), null, cancellationToken).ConfigureAwait(false)), what);
        }

        return await rows().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DataverseWorkflow>> WorkflowsWhereAsync(string environmentId, string filter, CancellationToken cancellationToken)
    {
        var response = await GetAsync(
            environmentId,
            $"workflows?$select={WorkflowColumns}&$filter={Uri.EscapeDataString(filter)}&$top=500",
            cancellationToken).ConfigureAwait(false);

        Require(response, "the workflow query");

        using var document = JsonDocument.Parse(response.Body);

        return document.RootElement.TryGetProperty("value", out var rows)
            ? [.. rows.EnumerateArray().Select(ReadWorkflow)]
            : [];
    }

    private async Task<IReadOnlyList<DataverseConnectionReference>> ConnectionReferencesWhereAsync(string environmentId, string filter, CancellationToken cancellationToken)
    {
        var response = await GetAsync(
            environmentId,
            $"connectionreferences?$select={ReferenceColumns}&$filter={Uri.EscapeDataString(filter)}&$top=500",
            cancellationToken).ConfigureAwait(false);

        Require(response, "the connection reference query");

        using var document = JsonDocument.Parse(response.Body);

        return document.RootElement.TryGetProperty("value", out var rows)
            ? [.. rows.EnumerateArray().Select(ReadReference)]
            : [];
    }

    private Task<OracleResponse> GetAsync(string environmentId, string relative, CancellationToken cancellationToken) => CallAsync(environmentId, relative, (uri, token) => OracleHttp.GetAsync(uri, token, _transcript, cancellationToken, _client), cancellationToken);

    private Task<OracleResponse> WriteAsync(string environmentId, HttpMethod method, string relative, JsonObject? body, CancellationToken cancellationToken) => CallAsync(environmentId, relative, (uri, token) => OracleHttp.WriteAsync(method, uri, body?.ToJsonString(), token, _transcript, cancellationToken, _client), cancellationToken);

    private async Task<OracleResponse> CallAsync(string environmentId, string relative, Func<Uri, string, Task<OracleResponse>> call, CancellationToken cancellationToken)
    {
        var instance = await InstanceAsync(environmentId, cancellationToken).ConfigureAwait(false);
        var token = await _tokens.TokenAsync(instance.GetLeftPart(UriPartial.Authority), cancellationToken).ConfigureAwait(false);

        return await call(new Uri(instance, "/api/data/v9.2/" + relative), token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> RowsAsync<T>(string environmentId, string relative, Func<JsonElement, T> read, string what, CancellationToken cancellationToken)
    {
        var response = await GetAsync(environmentId, relative, cancellationToken).ConfigureAwait(false);
        Require(response, what);

        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.TryGetProperty("value", out var rows) ? [.. rows.EnumerateArray().Select(read)] : [];
    }

    private async Task<T?> RowAsync<T>(string environmentId, string relative, Func<JsonElement, T> read, string what, CancellationToken cancellationToken) where T : class
    {
        var response = await GetAsync(environmentId, relative, cancellationToken).ConfigureAwait(false);

        if (response.NotFound)
        {
            return null;
        }

        Require(response, what);

        using var document = JsonDocument.Parse(response.Body);
        return read(document.RootElement);
    }

    private async Task<T> PatchAsync<T>(string environmentId, string row, JsonObject body, string what, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        Require(await WriteAsync(environmentId, HttpMethod.Patch, row, body, cancellationToken).ConfigureAwait(false), what);

        return await read().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DataverseConnectionReference>> ConnectionReferencesNamedExactlyAsync(string environmentId, string logicalName, CancellationToken cancellationToken) => [.. (await ConnectionReferencesPrefixedAsync(environmentId, logicalName, cancellationToken).ConfigureAwait(false)).Where(reference => string.Equals(reference.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase))];

    private static DataverseBot ReadBot(JsonElement row) => new()
    {
        BotId = Text(row, "botid") ?? string.Empty,
        Name = Text(row, "name"),
        SchemaName = Text(row, "schemaname"),
        Template = Text(row, "template"),
    };

    private static DataverseBotComponent ReadComponent(JsonElement row) => new()
    {
        ComponentId = Text(row, "botcomponentid") ?? string.Empty,
        Name = Text(row, "name"),
        Description = Text(row, "description"),
    };

    private static DataverseConnector ReadConnector(JsonElement row) => new()
    {
        ConnectorId = Text(row, "connectorid") ?? string.Empty,
        Name = Text(row, "name"),
        DisplayName = Text(row, "displayname"),
        Description = Text(row, "description"),
        InternalId = Text(row, "connectorinternalid"),
    };

    private static string RowId(string id, string what) => Guid.TryParse(id, out var parsed) ? parsed.ToString("D") : throw new E2EException($"'{id}' is not a {what} id.");

    private static OracleResponse Gone(OracleResponse response) => response.NotFound ? response with { Status = HttpStatusCode.NoContent } : response;

    private static DataverseWorkflow ReadWorkflow(JsonElement row) => new()
    {
        WorkflowId = Text(row, "workflowid") ?? string.Empty,
        Name = Text(row, "name"),
        StateCode = Number(row, "statecode"),
        StatusCode = Number(row, "statuscode"),
        Category = Number(row, "category"),
        CreatedOn = Timestamp(row, "createdon"),
        ModifiedOn = Timestamp(row, "modifiedon"),
        Description = Text(row, "description"),
        ClientData = Text(row, "clientdata"),
    };

    private static DataverseConnectionReference ReadReference(JsonElement row) => new()
    {
        Id = Text(row, "connectionreferenceid") ?? string.Empty,
        LogicalName = Text(row, "connectionreferencelogicalname") ?? string.Empty,
        ConnectorId = Text(row, "connectorid"),
        ConnectionId = Text(row, "connectionid"),
        StateCode = Number(row, "statecode"),
    };

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;

    private static DateTimeOffset? Timestamp(JsonElement row, string name) =>
        Text(row, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void Require(OracleResponse response, string what)
    {
        if (!response.Ok)
        {
            throw new E2EException($"Dataverse refused {what}: {(int)response.Status} {Truncate(response.Body)}");
        }
    }

    internal static void RequireSafeSegment(string value, string what)
    {
        if (value.Length == 0 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new E2EException($"'{value}' is not a valid {what}.");
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}
