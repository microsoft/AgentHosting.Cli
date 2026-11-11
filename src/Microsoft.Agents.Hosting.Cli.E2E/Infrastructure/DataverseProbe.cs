using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class AzTokens
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
            .RunAsync(startInfo, arguments, stdin: null, TimeSpan.FromMinutes(2), transcript: null, cancellationToken: cancellationToken)
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

public static class OracleHttp
{
    private const string FlowApiHost = "api.flow.microsoft.com";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(100) };

    public static async Task<OracleResponse> GetAsync(Uri uri, string token, Transcript? transcript, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        return await SendAsync(request, token, transcript, cancellationToken).ConfigureAwait(false);
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

    private static async Task<OracleResponse> SendAsync(HttpRequestMessage request, string token, Transcript? transcript, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var stopwatch = Stopwatch.StartNew();
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        transcript?.Note($"oracle {request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)} -> {(int)response.StatusCode} in {stopwatch.Elapsed.TotalSeconds:N1}s");

        return new OracleResponse(response.StatusCode, body);
    }
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

public sealed class DataverseProbe
{
    private const string PowerAppsResource = "https://service.powerapps.com/";
    private const string WorkflowColumns = "workflowid,name,statecode,statuscode,category,createdon,modifiedon,description";
    private const string ReferenceColumns = "connectionreferenceid,connectionreferencelogicalname,connectorid,connectionid,statecode";
    private const string UnmanagedFlowFilter = "category eq 5 and type eq 1 and ismanaged eq false";
    private const string ManagedFlowFilter = "category eq 5 and type eq 1 and ismanaged eq true";

    private readonly AzTokens _tokens;
    private readonly Transcript _transcript;
    private readonly Dictionary<string, Uri> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public DataverseProbe(AzTokens tokens, Transcript transcript)
    {
        _tokens = tokens;
        _transcript = transcript;
    }

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

    private async Task<OracleResponse> GetAsync(string environmentId, string relative, CancellationToken cancellationToken)
    {
        var instance = await InstanceAsync(environmentId, cancellationToken).ConfigureAwait(false);
        var token = await _tokens.TokenAsync(instance.GetLeftPart(UriPartial.Authority), cancellationToken).ConfigureAwait(false);

        return await OracleHttp
            .GetAsync(new Uri(instance, "/api/data/v9.2/" + relative), token, _transcript, cancellationToken)
            .ConfigureAwait(false);
    }

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
