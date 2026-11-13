using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Implements agent inventory and lifecycle over the Dataverse Web API, using the sync library's
/// client where it exposes an operation and direct Web API calls where it does not.
/// </summary>
public sealed class DataverseCatalogAdapter : ICopilotCatalogPort
{
    private const string BotsRoute = "api/data/v9.2/bots";
    private const string BotComponentsRoute = "api/data/v9.2/botcomponents";

    private const int ComponentDeleteConcurrency = 4;

    private const int DependencyRetryAttempts = 6;

    private static readonly string[] TransientDeleteCodes = ["0x8004f01f", "0x80040216"];

    private static readonly TimeSpan DependencyRetryInterval = TimeSpan.FromSeconds(15);

    private static readonly FrozenDictionary<int, string> StatusNames = new Dictionary<int, string>
    {
        [1] = "Provisioned",
        [2] = "Deprovisioned",
        [3] = "Provisioning",
        [4] = "ProvisionFailed",
        [5] = "MissingLicense",
    }.ToFrozenDictionary();

    private readonly ISyncDataverseClient _dataverseClient;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;
    private readonly DataverseWebApiClient _client;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the adapter.</summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the target organization.</param>
    /// <param name="delay">Waits between delete retries. Replaced in tests so retrying is instant.</param>
    public DataverseCatalogAdapter(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
        _client = new DataverseWebApiClient(_dataverseClient, _httpClientAccessor);
        _delay = delay ?? ((interval, cancellationToken) => Task.Delay(interval, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CopilotAgentSummary>> ListAgentsAsync(CopilotEnvironmentContext environment, int? maximumCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var route = $"{BotsRoute}?$select=botid,schemaname,name,statuscode,modifiedon,ismanaged";
        var rows = await _client.ReadCollectionAsync(environment, maximumCount > 0 ? $"{route}&$top={maximumCount}" : route, ToSummary, cancellationToken, maximumCount).ConfigureAwait(false);

        return [.. rows.OrderBy(agent => agent.SchemaName, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    /// <summary>
    /// Resolves an identifier or schema name to an agent that exists.
    ///
    /// A well-formed identifier is still checked against the service. Returning it unread would let a
    /// syntactically valid but nonexistent id be recorded as a clone source, and the failure would then
    /// surface much later as a vanished agent rather than as the typo it was.
    /// </summary>
    public async Task<string?> ResolveAgentIdAsync(CopilotEnvironmentContext environment, string botIdOrSchemaName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (Guid.TryParse(botIdOrSchemaName, out var parsed))
        {
            using var document = await SendAsync(environment, HttpMethod.Get, $"{BotsRoute}({parsed})?$select=botid", cancellationToken).ConfigureAwait(false);

            return document is null ? null : parsed.ToString();
        }

        using var lease = await DataverseTargeting.EnterAsync(_dataverseClient, cancellationToken).ConfigureAwait(false);
        using var scope = DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);
        var resolved = await _dataverseClient.GetAgentIdBySchemaNameAsync(botIdOrSchemaName, cancellationToken).ConfigureAwait(false);

        return resolved == Guid.Empty ? null : resolved.ToString();
    }

    /// <inheritdoc />
    public async Task<CopilotAgentSummary> GetAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        using var document = await SendAsync(environment, HttpMethod.Get, $"{BotsRoute}({agentId})?$select=botid,schemaname,name,statuscode,modifiedon,ismanaged", cancellationToken).ConfigureAwait(false);

        return document is null
            ? throw CopilotStudioException.AgentNotFound(agentId)
            : ToSummary(document.RootElement);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Dataverse reports the agent as still referenced for a short window after a deploy, so the whole
    /// delete is retried: each attempt re-reads the component list, because a stale list is what leaves
    /// the reference behind.
    /// </remarks>
    public async Task DeleteAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DeleteComponentsThenAgentAsync(environment, agentId, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CopilotStudioException failure) when (attempt < DependencyRetryAttempts && IsDependencyConflict(failure))
            {
                await _delay(DependencyRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DeleteComponentsThenAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        var components = await ReadComponentIdsAsync(environment, agentId, cancellationToken).ConfigureAwait(false);

        foreach (var batch in components.Chunk(ComponentDeleteConcurrency))
        {
            await Task.WhenAll(batch.Select(componentId => DeleteComponentAsync(environment, componentId, cancellationToken))).ConfigureAwait(false);
        }

        using var _ = await SendAsync(environment, HttpMethod.Delete, $"{BotsRoute}({agentId})", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognises the two codes Dataverse returns when a component delete races its own dependency
    /// graph: <c>0x8004f01f</c> for a dependency still in place, and <c>0x80040216</c> for a related
    /// row that has already gone. Both clear once the graph settles, and both re-read the component
    /// list on the next attempt.
    /// </summary>
    private static bool IsDependencyConflict(CopilotStudioException failure) =>
        TransientDeleteCodes.Any(code => failure.Message.Contains(code, StringComparison.OrdinalIgnoreCase));

    private async Task DeleteComponentAsync(CopilotEnvironmentContext environment, string componentId, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(environment, HttpMethod.Delete, $"{BotComponentsRoute}({componentId})", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The components belonging to an agent.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadComponentIdsAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken) =>
        [.. (await _client.ReadCollectionAsync(
            environment,
            $"{BotComponentsRoute}?$select=botcomponentid&$filter=_parentbotid_value eq {agentId}",
            element => ReadString(element, "botcomponentid"),
            cancellationToken).ConfigureAwait(false)).OfType<string>()];

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetPublishedOnAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        using var document = await SendAsync(environment, HttpMethod.Get, $"{BotsRoute}({agentId})?$select=publishedon", cancellationToken).ConfigureAwait(false);

        return document is null
            ? throw CopilotStudioException.AgentNotFound(agentId)
            : ReadPublishedOn(document.RootElement);
    }

    /// <inheritdoc />
    public async Task PublishAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        using var _ = await SendAsync(environment, HttpMethod.Post, $"{BotsRoute}({agentId})/Microsoft.Dynamics.CRM.PvaPublish", cancellationToken, "{}").ConfigureAwait(false);
    }

    private async Task<JsonDocument?> SendAsync(CopilotEnvironmentContext environment, HttpMethod method, string relativeUrl, CancellationToken cancellationToken, string? jsonBody = null) =>
        await _client.SendAsync(environment, method, relativeUrl, cancellationToken, jsonBody).ConfigureAwait(false);

    private static CopilotAgentSummary ToSummary(JsonElement element) => new()
    {
        AgentId = ReadString(element, "botid") ?? string.Empty,
        SchemaName = ReadString(element, "schemaname") ?? string.Empty,
        DisplayName = ReadString(element, "name") ?? string.Empty,
        State = ReadStatus(element),
        LastChangedOn = ReadTimestamp(element, "modifiedon"),
        IsManaged = ReadBoolean(element, "ismanaged"),
    };

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadStatus(JsonElement element) =>
        element.TryGetProperty("statuscode", out var status) && status.ValueKind == JsonValueKind.Number
            ? StatusNames.TryGetValue(status.GetInt32(), out var name) ? name : status.GetInt32().ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? ReadString(JsonElement element, string name) => DataverseWebApiClient.ReadString(element, name);

    private static DateTimeOffset? ReadPublishedOn(JsonElement element) => ReadTimestamp(element, "publishedon");

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : null;
}
