using System.Diagnostics;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed record FlowApiFlow(string Name, string? DisplayName, string? State, string? WorkflowEntityId);

public sealed record FlowRunOutcome(int TriggerStatus, string? RunStatus, string? Detail, TimeSpan Elapsed)
{
    public bool Succeeded => string.Equals(RunStatus, "Succeeded", StringComparison.OrdinalIgnoreCase);
}

public sealed class FlowRunner
{
    private const string FlowResource = "https://service.flow.microsoft.com/";
    private const string ApiVersion = "2016-11-01";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly AzTokens _tokens;
    private readonly Transcript _transcript;
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public FlowRunner(AzTokens tokens, Transcript transcript)
    {
        _tokens = tokens;
        _transcript = transcript;
    }

    public void AllowRunsOf(string workflowId)
    {
        lock (_gate)
        {
            _owned.Add(workflowId);
        }
    }

    public async Task<bool> CanListAsync(string environmentId, CancellationToken cancellationToken)
    {
        var token = await _tokens.TokenAsync(FlowResource, cancellationToken).ConfigureAwait(false);
        var response = await OracleHttp.GetAsync(FlowsUri(environmentId), token, _transcript, cancellationToken).ConfigureAwait(false);

        return response.Ok;
    }

    public async Task<FlowApiFlow?> FindAsync(string environmentId, string workflowId, CancellationToken cancellationToken)
    {
        var token = await _tokens.TokenAsync(FlowResource, cancellationToken).ConfigureAwait(false);
        Uri? next = FlowsUri(environmentId);

        for (var page = 0; next is not null && page < 10; page++)
        {
            var response = await OracleHttp.GetAsync(next, token, _transcript, cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new E2EException($"The Power Automate API refused the flow listing: {(int)response.Status}.");
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;

            if (root.TryGetProperty("value", out var flows))
            {
                foreach (var flow in flows.EnumerateArray())
                {
                    var properties = flow.TryGetProperty("properties", out var found) ? found : default;
                    var entityId = Text(properties, "workflowEntityId");

                    if (string.Equals(entityId, workflowId, StringComparison.OrdinalIgnoreCase) && Text(flow, "name") is { Length: > 0 } name)
                    {
                        return new FlowApiFlow(name, Text(properties, "displayName"), Text(properties, "state"), entityId);
                    }
                }
            }

            next = Text(root, "nextLink") is { Length: > 0 } link && Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
        }

        return null;
    }

    public async Task<FlowRunOutcome> RunAsync(string environmentId, string workflowId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_owned.Contains(workflowId))
            {
                throw new E2EException($"Refused to run {workflowId}: only a flow the suite deployed to be run may be started.");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow + timeout;
        var flow = await FindAsync(environmentId, workflowId, cancellationToken).ConfigureAwait(false);

        while (flow is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            flow = await FindAsync(environmentId, workflowId, cancellationToken).ConfigureAwait(false);
        }

        if (flow is null)
        {
            return new FlowRunOutcome(0, null, "the flow never appeared in the Power Automate API", stopwatch.Elapsed);
        }

        DataverseProbe.RequireSafeSegment(flow.Name, "flow name");

        var token = await _tokens.TokenAsync(FlowResource, cancellationToken).ConfigureAwait(false);
        var earlier = await RunIdsAsync(environmentId, flow.Name, token, cancellationToken).ConfigureAwait(false);

        var trigger = await OracleHttp.PostFlowRunAsync(
            new Uri($"{EnvironmentBase(environmentId)}/flows/{flow.Name}/triggers/manual/run?api-version={ApiVersion}"),
            token,
            _transcript,
            cancellationToken).ConfigureAwait(false);

        if (!trigger.Ok)
        {
            return new FlowRunOutcome((int)trigger.Status, null, Truncate(trigger.Body), stopwatch.Elapsed);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            var (runId, status) = await NewestRunAsync(environmentId, flow.Name, token, earlier, cancellationToken).ConfigureAwait(false);

            if (status is not null and not "Running" and not "Waiting")
            {
                return new FlowRunOutcome((int)trigger.Status, status, runId, stopwatch.Elapsed);
            }
        }

        return new FlowRunOutcome((int)trigger.Status, "TimedOut", "the run did not finish within the wait", stopwatch.Elapsed);
    }

    private async Task<HashSet<string>> RunIdsAsync(string environmentId, string flowName, string token, CancellationToken cancellationToken)
    {
        var response = await OracleHttp.GetAsync(RunsUri(environmentId, flowName), token, _transcript, cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            return [];
        }

        using var document = JsonDocument.Parse(response.Body);

        return document.RootElement.TryGetProperty("value", out var runs)
            ? runs.EnumerateArray().Select(run => Text(run, "name")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private async Task<(string? RunId, string? Status)> NewestRunAsync(string environmentId, string flowName, string token, HashSet<string> earlier, CancellationToken cancellationToken)
    {
        var response = await OracleHttp.GetAsync(RunsUri(environmentId, flowName), token, _transcript, cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
        {
            return (null, null);
        }

        using var document = JsonDocument.Parse(response.Body);

        if (!document.RootElement.TryGetProperty("value", out var runs))
        {
            return (null, null);
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (Text(run, "name") is { } runId && !earlier.Contains(runId))
            {
                var properties = run.TryGetProperty("properties", out var found) ? found : default;
                return (runId, Text(properties, "status"));
            }
        }

        return (null, null);
    }

    private static string EnvironmentBase(string environmentId)
    {
        DataverseProbe.RequireSafeSegment(environmentId, "environment id");

        return $"https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/{environmentId}";
    }

    private static Uri FlowsUri(string environmentId) => new($"{EnvironmentBase(environmentId)}/flows?api-version={ApiVersion}");

    private static Uri RunsUri(string environmentId, string flowName) => new($"{EnvironmentBase(environmentId)}/flows/{flowName}/runs?api-version={ApiVersion}");

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}
