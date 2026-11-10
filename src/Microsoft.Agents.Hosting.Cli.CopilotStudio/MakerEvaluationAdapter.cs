using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using CliClusterCategory = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;
using SyncClusterCategoryValue = Microsoft.CopilotStudio.Sync.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Implements maker evaluations over the Copilot Studio control plane's <c>makerevaluations</c> contract.</summary>
public sealed class MakerEvaluationAdapter : IAgentEvaluationPort, IDisposable
{
    private const string ApplicationSource = "Web";
    private const string MigrationQuery = "ApplyV2Migration=true&cli=true";
    private const string ClientQuery = "cli=true";
    private const string GeneralQualityGrader = "GeneralQualityGrader";
    private const string UserRole = "user";
    private const string AgentRole = "agent";

    private readonly ITokenProvider _tokenProvider;
    private readonly PowerPlatformCloudMap _cloudMap;
    private readonly SyncClusterCategoryValue _cluster;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Lock _gate = new();

    private HttpMessageHandler? _sharedHandler;

    /// <summary>Creates the adapter.</summary>
    /// <param name="tokenProvider">Supplies control-plane tokens.</param>
    /// <param name="cloudMap">Names the cloud in a refused gateway's failure.</param>
    /// <param name="cluster">The cluster of the active ring, which picks the control plane, its token audience and the gateways it serves.</param>
    /// <param name="handlerFactory">Creates the transport, replaced in tests.</param>
    public MakerEvaluationAdapter(ITokenProvider tokenProvider, PowerPlatformCloudMap cloudMap, CliClusterCategory cluster, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _cloudMap = cloudMap ?? throw new ArgumentNullException(nameof(cloudMap));
        _cluster = SyncClusterCategory.For(cluster);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvaluationRunSummary>> ListRunsAsync(CopilotEnvironmentContext environment, string agentId, int maximumCount, CancellationToken cancellationToken)
    {
        const string Operation = "list evaluation runs";

        using var document = await SendAsync(environment, agentId, HttpMethod.Get, $"?count={maximumCount}&{MigrationQuery}", body: null, Operation, cancellationToken).ConfigureAwait(false);

        return document?.RootElement.ValueKind == JsonValueKind.Array
            ? [.. document.RootElement.EnumerateArray().Select(ReadRunSummary)]
            : throw CopilotStudioException.ServiceError(Operation, "The service did not return a list of runs.");
    }

    /// <inheritdoc />
    public async Task<EvaluationRunDetail?> GetRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(environment, agentId, HttpMethod.Get, $"/{Uri.EscapeDataString(runId)}/details?{MigrationQuery}", body: null, $"read evaluation run {runId}", cancellationToken, allowMissing: true).ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;

        return new EvaluationRunDetail
        {
            Summary = ReadRunSummary(root),
            TestCases = root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("testCases", out var testCases) && testCases.ValueKind == JsonValueKind.Array
                ? [.. testCases.EnumerateArray().Select(ReadTestCase)]
                : [],
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvaluationTestSetSummary>> ListTestSetsAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        const string Operation = "list evaluation test sets";

        using var document = await SendAsync(environment, agentId, HttpMethod.Get, $"/testsets?{MigrationQuery}", body: null, Operation, cancellationToken).ConfigureAwait(false);

        return [.. TestComponents(document, Operation).Select(ReadTestSetSummary)];
    }

    /// <inheritdoc />
    public async Task<EvaluationTestSetDetail?> GetTestSetAsync(CopilotEnvironmentContext environment, string agentId, EvaluationTestSetSummary testSet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(testSet);

        var operation = $"read evaluation test set {testSet.TestSetId}";

        using var document = await SendAsync(environment, agentId, HttpMethod.Get, $"/testsets/{Uri.EscapeDataString(testSet.TestSetId)}/details?{MigrationQuery}", body: null, operation, cancellationToken, allowMissing: true).ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var conversations = new List<EvaluationConversation>();
        var componentIds = new List<string>();

        foreach (var component in TestComponents(document, operation))
        {
            if (ReadConversationComponent(component) is not { } parsed)
            {
                continue;
            }

            conversations.Add(parsed.Conversation with { Number = conversations.Count + 1 });
            componentIds.Add(parsed.ComponentId);
        }

        return new EvaluationTestSetDetail
        {
            Summary = testSet,
            Conversations = conversations,
            ConversationComponentIds = componentIds,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvaluationConversation>> GenerateConversationsAsync(CopilotEnvironmentContext environment, string agentId, int conversationCount, int turnCount, string locale, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["evaluationSetType"] = EvaluationSetTypes.MultiTurn,
            ["conversationCount"] = conversationCount,
            ["turnCount"] = turnCount,
            ["locale"] = locale,
        };

        using var document = await SendAsync(environment, agentId, HttpMethod.Post, $"/conversations/generate?{ClientQuery}", payload.ToJsonString(), "generate evaluation conversations", cancellationToken).ConfigureAwait(false);

        if (document is null || !document.RootElement.TryGetProperty("conversations", out var conversations) || conversations.ValueKind != JsonValueKind.Array)
        {
            throw CopilotStudioException.ServiceError("generate evaluation conversations", "The service returned no conversations.");
        }

        return [.. conversations.EnumerateArray().Select((conversation, index) => new EvaluationConversation
        {
            Number = index + 1,
            Source = EvaluationConversationSource.Generated,
            Exchanges = conversation.TryGetProperty("queries", out var queries) && queries.ValueKind == JsonValueKind.Array
                ? [.. queries.EnumerateArray().Select(query => new EvaluationExchange(EvaluationJson.String(query, "query") ?? string.Empty, EvaluationJson.String(query, "answer")))]
                : [],
        })];
    }

    /// <inheritdoc />
    public async Task<string> CreateTestSetAsync(CopilotEnvironmentContext environment, string agentId, string name, CancellationToken cancellationToken)
    {
        var schemaName = NewSchemaName();
        var definition = new JsonObject
        {
            ["$kind"] = "EvaluationSet",
            ["graders"] = new JsonArray(new JsonObject { ["$kind"] = GeneralQualityGrader, ["diagnostics"] = new JsonArray() }),
            ["diagnostics"] = new JsonArray(),
        };

        var added = await WriteComponentsAsync(environment, agentId, [Component(schemaName, name, definition, parentComponentId: null)], $"create evaluation test set '{name}'", cancellationToken).ConfigureAwait(false);

        return added.TryGetValue(schemaName, out var testSetId)
            ? testSetId
            : throw CopilotStudioException.ServiceError($"create evaluation test set '{name}'", "The service did not return the new test set's identifier.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AddConversationsAsync(CopilotEnvironmentContext environment, string agentId, string testSetId, IReadOnlyList<EvaluationConversation> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        if (conversations.Count == 0)
        {
            return [];
        }

        var displayOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ordered = conversations
            .Select((conversation, index) => (SchemaName: NewSchemaName(), Conversation: conversation, DisplayOrder: displayOrder - index))
            .ToArray();

        var added = await WriteComponentsAsync(
            environment,
            agentId,
            [.. ordered.Select(entry => ConversationComponent(entry.SchemaName, entry.Conversation, testSetId, entry.DisplayOrder))],
            $"add {conversations.Count} conversation(s) to test set {testSetId}",
            cancellationToken).ConfigureAwait(false);

        return [.. ordered.Select(entry => added.TryGetValue(entry.SchemaName, out var id) ? id : string.Empty).Where(id => id.Length > 0)];
    }

    /// <inheritdoc />
    public async Task<EvaluationRunSummary> StartRunAsync(CopilotEnvironmentContext environment, string agentId, EvaluationRunStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        var payload = new JsonObject
        {
            ["testSetId"] = start.TestSetId,
            ["clientRequestedEvaluationRunName"] = start.Name,
            ["runOnPublishedBot"] = start.RunOnPublishedBot,
        };

        if (!string.IsNullOrWhiteSpace(start.ConnectionId))
        {
            payload["mcsConnectionId"] = start.ConnectionId;
        }

        var started = await RunCommandAsync(environment, agentId, HttpMethod.Post, $"?{MigrationQuery}", payload.ToJsonString(), $"start evaluation run '{start.Name}'", cancellationToken).ConfigureAwait(false);

        return started is not null && !string.IsNullOrWhiteSpace(started.RunId)
            ? started
            : throw EvaluationException.RunNotIdentified(start.Name);
    }

    /// <inheritdoc />
    public async Task<EvaluationRunSummary> CancelRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken)
    {
        const string Operation = "stop evaluation run";

        return await RunCommandAsync(environment, agentId, HttpMethod.Patch, $"/{Uri.EscapeDataString(runId)}/cancel?{ClientQuery}", body: null, $"{Operation} {runId}", cancellationToken, runId).ConfigureAwait(false)
            ?? throw CopilotStudioException.ServiceError($"{Operation} {runId}", "The service returned no run.");
    }

    /// <inheritdoc />
    public async Task<EvaluationRunSummary> DeleteRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken)
    {
        const string Operation = "delete evaluation run";

        return await RunCommandAsync(environment, agentId, HttpMethod.Delete, $"/{Uri.EscapeDataString(runId)}?{ClientQuery}", body: null, $"{Operation} {runId}", cancellationToken, runId).ConfigureAwait(false)
            ?? throw CopilotStudioException.ServiceError($"{Operation} {runId}", "The service returned no run.");
    }

    /// <summary>
    /// Sends a request that changes a run, or nothing when the service answered with no body. A reply
    /// that does not name the run names the one the request addressed, which cannot be in doubt.
    /// </summary>
    private async Task<EvaluationRunSummary?> RunCommandAsync(CopilotEnvironmentContext environment, string agentId, HttpMethod method, string route, string? body, string operation, CancellationToken cancellationToken, string? addressed = null)
    {
        using var document = await SendAsync(environment, agentId, method, route, body, operation, cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var summary = ReadRunSummary(document.RootElement);

        return string.IsNullOrWhiteSpace(summary.RunId) && !string.IsNullOrWhiteSpace(addressed)
            ? summary with { RunId = addressed }
            : summary;
    }

    private async Task<IReadOnlyDictionary<string, string>> WriteComponentsAsync(CopilotEnvironmentContext environment, string agentId, IReadOnlyList<JsonObject> components, string operation, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["testComponents"] = new JsonArray([.. components.Select(component => (JsonNode)component)]) };

        using var document = await SendAsync(environment, agentId, HttpMethod.Post, $"/testcomponent?{MigrationQuery}", payload.ToJsonString(), operation, cancellationToken).ConfigureAwait(false);

        if (document is null || !document.RootElement.TryGetProperty("addedComponentsIdsBySchemaName", out var added) || added.ValueKind != JsonValueKind.Object)
        {
            throw CopilotStudioException.ServiceError(operation, "The service did not report which components it added.");
        }

        return added.EnumerateObject()
            .Where(entry => entry.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);
    }

    private static JsonObject ConversationComponent(string schemaName, EvaluationConversation conversation, string testSetId, long displayOrder)
    {
        var activities = new JsonArray();

        foreach (var exchange in conversation.Exchanges)
        {
            activities.Add(Activity(exchange.Question, UserRole));

            if (!string.IsNullOrWhiteSpace(exchange.Response))
            {
                activities.Add(Activity(exchange.Response, AgentRole));
            }
        }

        return Component(
            schemaName,
            conversation.DisplayName,
            new JsonObject
            {
                ["$kind"] = "MultiTurnEvaluationCase",
                ["source"] = conversation.Source.ToString(),
                ["activities"] = activities,
                ["diagnostics"] = new JsonArray(),
                ["extensionData"] = new JsonObject { ["displayOrder"] = displayOrder.ToString(CultureInfo.InvariantCulture) },
            },
            testSetId);
    }

    private static JsonObject Component(string schemaName, string displayName, JsonObject definition, string? parentComponentId)
    {
        var component = new JsonObject
        {
            ["$kind"] = "TestCaseComponent",
            ["schemaName"] = schemaName,
            ["definition"] = definition,
            ["displayName"] = displayName,
            ["description"] = displayName,
            ["category"] = "Testing",
            ["state"] = "Active",
        };

        if (parentComponentId is not null)
        {
            component["parentBotComponentId"] = parentComponentId;
        }

        return new JsonObject
        {
            ["$kind"] = "MakerEvaluationUpdateTestComponent",
            ["component"] = component,
            ["operationType"] = "Add",
        };
    }

    private static JsonObject Activity(string text, string role) => new()
    {
        ["$kind"] = "MultiTurnActivity",
        ["diagnostics"] = new JsonArray(),
        ["activity"] = new JsonObject
        {
            ["$kind"] = "Message",
            ["diagnostics"] = new JsonArray(),
            ["text"] = new JsonArray(new JsonObject
            {
                ["$kind"] = "TemplateLine",
                ["diagnostics"] = new JsonArray(),
                ["segments"] = new JsonArray(new JsonObject { ["$kind"] = "TextSegment", ["diagnostics"] = new JsonArray(), ["value"] = text }),
            }),
            ["speak"] = new JsonArray(),
            ["attachments"] = new JsonArray(),
            ["quickReplies"] = new JsonArray(),
            ["value"] = new JsonObject
            {
                ["$kind"] = "ValueExpression",
                ["diagnostics"] = new JsonArray(),
                ["literalValue"] = new JsonObject { ["from"] = new JsonObject { ["role"] = role } },
            },
        },
    };

    private static string NewSchemaName() => $"mspva_{Guid.NewGuid()}";

    private static JsonElement.ArrayEnumerator TestComponents(JsonDocument? document, string operation)
    {
        if (document is null)
        {
            throw CopilotStudioException.ServiceError(operation, "The service returned no test components.");
        }

        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("testComponents", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
        {
            root = wrapped;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw CopilotStudioException.ServiceError(operation, "The service did not return a list of test components.");
        }

        return root.EnumerateArray();
    }

    private static EvaluationTestSetSummary ReadTestSetSummary(JsonElement element)
    {
        var component = element.TryGetProperty("component", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : element;

        return new EvaluationTestSetSummary
        {
            TestSetId = EvaluationJson.String(component, "id") ?? string.Empty,
            DisplayName = EvaluationJson.String(component, "displayName") ?? string.Empty,
            ConversationCount = EvaluationJson.Int(element, "numberOfTestCases") ?? EvaluationJson.Int(component, "numberOfTestCases") ?? 0,
            EvaluationSetType = EvaluationJson.String(element, "evaluationSetType") ?? EvaluationJson.String(component, "evaluationSetType"),
            State = EvaluationJson.String(component, "state"),
        };
    }

    private static (EvaluationConversation Conversation, string ComponentId)? ReadConversationComponent(JsonElement element)
    {
        var component = element.TryGetProperty("component", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : element;

        if (!component.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!string.Equals(EvaluationJson.String(definition, "$kind"), "MultiTurnEvaluationCase", StringComparison.Ordinal))
        {
            return null;
        }

        var turns = new List<(string Text, string Role)>();

        if (definition.TryGetProperty("activities", out var activities) && activities.ValueKind == JsonValueKind.Array)
        {
            turns.AddRange(activities.EnumerateArray().Select(ReadTurn).Where(turn => turn.Text.Length > 0));
        }

        var conversation = new EvaluationConversation
        {
            Number = 1,
            Source = string.Equals(EvaluationJson.String(definition, "source"), nameof(EvaluationConversationSource.Generated), StringComparison.OrdinalIgnoreCase)
                ? EvaluationConversationSource.Generated
                : EvaluationConversationSource.Imported,
            Exchanges = ToExchanges(turns),
        };

        return (conversation, EvaluationJson.String(component, "id") ?? string.Empty);
    }

    private static List<EvaluationExchange> ToExchanges(List<(string Text, string Role)> turns)
    {
        var exchanges = new List<EvaluationExchange>();

        for (var index = 0; index < turns.Count; index++)
        {
            if (!string.Equals(turns[index].Role, UserRole, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasReply = index + 1 < turns.Count && string.Equals(turns[index + 1].Role, AgentRole, StringComparison.OrdinalIgnoreCase);

            exchanges.Add(new EvaluationExchange(turns[index].Text, hasReply ? turns[index + 1].Text : null));
        }

        return exchanges;
    }

    private static (string Text, string Role) ReadTurn(JsonElement element)
    {
        if (!element.TryGetProperty("activity", out var activity) || activity.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty);
        }

        var text = new StringBuilder();

        if (activity.TryGetProperty("text", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (!line.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var segment in segments.EnumerateArray())
                {
                    text.Append(EvaluationJson.String(segment, "value"));
                }
            }
        }

        var role = activity.TryGetProperty("value", out var value) && value.TryGetProperty("literalValue", out var literal) && literal.TryGetProperty("from", out var from)
            ? EvaluationJson.String(from, "role") ?? string.Empty
            : string.Empty;

        return (text.ToString(), role);
    }

    /// <summary>An identifier a property carries, or null when it is absent, blank, or another kind.</summary>
    private static string? Identifier(JsonElement element, string name) =>
        EvaluationJson.String(element, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static EvaluationRunSummary ReadRunSummary(JsonElement element) => new()
    {
        RunId = Identifier(element, "runId") ?? Identifier(element, "id") ?? string.Empty,
        Name = EvaluationJson.String(element, "name"),
        State = EvaluationJson.String(element, "state") ?? EvaluationJson.String(element, "executionState") ?? string.Empty,
        TestSetId = EvaluationJson.String(element, "testSetId"),
        TestCaseCount = EvaluationJson.Int(element, "testCaseCount") ?? EvaluationJson.Int(element, "TotalItemCount") ?? 0,
        StartTime = EvaluationJson.Timestamp(element, "startTime"),
        EndTime = EvaluationJson.Timestamp(element, "endTime"),
        RunOnPublishedBot = EvaluationJson.Bool(element, "runOnPublishedBot") ?? false,
        EvaluationSetType = EvaluationJson.String(element, "evaluationSetType"),
        Totals = ReadTotals(element),
        Credits = element.TryGetProperty("consumption", out var consumption) && consumption.ValueKind == JsonValueKind.Object && consumption.TryGetProperty("evaluators", out var evaluators)
            ? EvaluationJson.Double(evaluators, "totalCredits")
            : null,
    };

    private static EvaluationRunTotals? ReadTotals(JsonElement element)
    {
        if (!element.TryGetProperty("aggregatedGraderResults", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var totals = new EvaluationRunTotals();

        foreach (var result in results.EnumerateArray())
        {
            var count = EvaluationJson.Int(result, "count") ?? 0;

            totals = EvaluationJson.String(result, "name") switch
            {
                "totalSucceeded" => totals with { Succeeded = count },
                "totalFailed" => totals with { Failed = count },
                "totalInvalid" => totals with { Invalid = count },
                "totalErrors" => totals with { Errors = count },
                _ => totals,
            };
        }

        return totals.Total == 0 ? null : totals;
    }

    private static EvaluationTestCaseResult ReadTestCase(JsonElement element)
    {
        var queries = element.TryGetProperty("queries", out var rawQueries) && rawQueries.ValueKind == JsonValueKind.Array
            ? rawQueries.EnumerateArray().Select(query => new EvaluationQueryResult
            {
                Query = EvaluationJson.String(query, "query") ?? string.Empty,
                Answer = EvaluationJson.String(query, "answer"),
                ExecutionState = EvaluationJson.String(query, "executionState"),
            }).ToArray()
            : Array.Empty<EvaluationQueryResult>();

        var graderMetrics = element.TryGetProperty("graderMetrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object ? metrics : default;

        return new EvaluationTestCaseResult
        {
            TestCaseId = EvaluationJson.String(element, "id") ?? string.Empty,
            DisplayName = queries.Length > 0 ? queries[0].Query : null,
            ExecutionState = EvaluationJson.String(element, "executionState"),
            EvaluationResult = graderMetrics.ValueKind == JsonValueKind.Object
                ? EvaluationJson.String(graderMetrics, "evaluationResult")
                : element.TryGetProperty("metrics", out var caseMetrics) && caseMetrics.ValueKind == JsonValueKind.Object ? EvaluationJson.String(caseMetrics, "evaluationResult") : null,
            Dimensions = ReadDimensions(graderMetrics),
            Queries = queries,
        };
    }

    private static EvaluationQualityDimensions? ReadDimensions(JsonElement graderMetrics)
    {
        if (graderMetrics.ValueKind != JsonValueKind.Object || !graderMetrics.TryGetProperty("queryResponseMetrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var metric in metrics.EnumerateArray())
        {
            if (metric.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                return new EvaluationQualityDimensions
                {
                    Abstention = EvaluationJson.String(properties, "abstention"),
                    Relevance = EvaluationJson.String(properties, "relevance"),
                    Groundedness = EvaluationJson.String(properties, "groundedness"),
                    Completeness = EvaluationJson.String(properties, "completeness"),
                };
            }
        }

        return null;
    }

    private HttpMessageHandler SharedHandler()
    {
        lock (_gate)
        {
            return _sharedHandler ??= _handlerFactory();
        }
    }

    /// <summary>
    /// Sends one request, answering <see langword="null"/> only where absence is a real answer.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(CopilotEnvironmentContext environment, string agentId, HttpMethod method, string route, string? body, string operation, CancellationToken cancellationToken, bool allowMissing = false)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var advertised = environment.AgentManagementUrl ?? throw CopilotStudioException.AgentManagementEndpointMissing(environment.EnvironmentId);
        var endpoint = ControlPlaneAccess.RequireTrustedGateway(advertised, environment.EnvironmentId, _cloudMap, _cluster);
        var token = AcquiredTokenTenant.Require(
            await _tokenProvider.AcquireTokenSilentAsync(ControlPlaneAccess.TokenScopes(_cluster), environment.TenantId, cancellationToken).ConfigureAwait(false),
            environment.TenantId);

        using var client = new HttpClient(SharedHandler(), disposeHandler: false);
        using var request = new HttpRequestMessage(method, new Uri(endpoint, $"api/botmanagement/v2/environments/{Uri.EscapeDataString(environment.EnvironmentId)}/bots/{Uri.EscapeDataString(agentId)}/makerevaluations{route}"));
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

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && allowMissing)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CopilotStudioException.ServiceError(operation, Describe(response, payload));
        }

        return string.IsNullOrWhiteSpace(payload) ? null : JsonDocument.Parse(payload);
    }

    private static IEnumerable<(string Name, string Value)> RoutingHeaders(CopilotEnvironmentContext environment, string agentId) =>
    [
        ("x-cci-tenantid", environment.TenantId),
        ("x-cci-routing-tenantid", environment.TenantId),
        ("x-cci-bapenvironmentid", environment.EnvironmentId),
        ("x-cci-botid", agentId),
        ("x-cci-cdsbotid", agentId),
        ("x-cci-routing-botid", agentId),
        ("x-cci-applicationsource", ApplicationSource),
    ];

    private static string Describe(HttpResponseMessage response, string payload)
    {
        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}.";

        if (string.IsNullOrWhiteSpace(payload))
        {
            return status;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return EvaluationJson.String(error, "Message") is { Length: > 0 } message ? $"{status} {message}" : status;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var lower) && lower.ValueKind == JsonValueKind.Object)
            {
                return EvaluationJson.String(lower, "message") is { Length: > 0 } message ? $"{status} {message}" : status;
            }

            return status;
        }
        catch (JsonException)
        {
            return status;
        }
    }
}

/// <summary>The defensive readers the evaluation payload parsing shares.</summary>
internal static class EvaluationJson
{
    /// <summary>The string a property carries, or null when it is absent or another kind.</summary>
    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The integer a property carries, or null when it is absent or another kind.</summary>
    internal static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>The number a property carries, or null when it is absent or another kind.</summary>
    internal static double? Double(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    /// <summary>The boolean a property carries, or null when it is absent or another kind.</summary>
    internal static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>The timestamp a property carries, or null when it is absent or unparsable.</summary>
    internal static DateTimeOffset? Timestamp(JsonElement element, string name) =>
        String(element, name) is { Length: > 0 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : null;
}
