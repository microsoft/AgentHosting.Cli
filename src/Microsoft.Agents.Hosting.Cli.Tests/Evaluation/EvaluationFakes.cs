using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

internal sealed class FakeEvaluationPort : IAgentEvaluationPort
{
    public List<EvaluationRunSummary> Runs { get; } = [];

    public List<EvaluationTestSetSummary> TestSets { get; } = [];

    public Dictionary<string, EvaluationTestSetDetail> TestSetDetails { get; } = [];

    public Queue<EvaluationRunDetail?> RunDetails { get; } = new();

    public List<EvaluationConversation> Generated { get; } = [];

    public List<string> CreatedTestSetNames { get; } = [];

    public List<(string TestSetId, int Count)> AddedConversations { get; } = [];

    public List<string> CancelledRuns { get; } = [];

    public List<string> DeletedRuns { get; } = [];

    public List<(int Count, int Turns, string Locale)> GenerateCalls { get; } = [];

    public EvaluationRunStart? LastStart { get; private set; }

    public int ListRunsCalls { get; private set; }

    public int RequestedRunCount { get; private set; }

    public int GetRunCalls { get; private set; }

    public int GetTestSetCalls { get; private set; }

    public int ListTestSetsCalls { get; private set; }

    public Exception? AddConversationsThrows { get; set; }

    public int? AcknowledgedConversations { get; set; }

    public Action? OnAddConversations { get; set; }

    public int? EnforcedCapacity { get; set; }

    public HashSet<string> Removed { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _stored = [];

    public string NextTestSetId { get; set; } = "testset-new";

    public string StartedRunId { get; set; } = "run-1";

    public Task<IReadOnlyList<EvaluationRunSummary>> ListRunsAsync(CopilotEnvironmentContext environment, string agentId, int maximumCount, CancellationToken cancellationToken)
    {
        ListRunsCalls++;
        RequestedRunCount = maximumCount;
        return Task.FromResult<IReadOnlyList<EvaluationRunSummary>>([.. Runs.Take(maximumCount)]);
    }

    public Task<EvaluationRunDetail?> GetRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken)
    {
        GetRunCalls++;
        return Task.FromResult(RunDetails.Count > 0 ? RunDetails.Dequeue() : null);
    }

    public Task<IReadOnlyList<EvaluationTestSetSummary>> ListTestSetsAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ListTestSetsCalls++;
        return Task.FromResult<IReadOnlyList<EvaluationTestSetSummary>>(TestSets);
    }

    public Task<EvaluationTestSetDetail?> GetTestSetAsync(CopilotEnvironmentContext environment, string agentId, EvaluationTestSetSummary testSet, CancellationToken cancellationToken)
    {
        GetTestSetCalls++;
        return Task.FromResult(TestSetDetails.GetValueOrDefault(testSet.TestSetId));
    }

    public Task<IReadOnlyList<EvaluationConversation>> GenerateConversationsAsync(CopilotEnvironmentContext environment, string agentId, int conversationCount, int turnCount, string locale, CancellationToken cancellationToken)
    {
        GenerateCalls.Add((conversationCount, turnCount, locale));
        return Task.FromResult<IReadOnlyList<EvaluationConversation>>(Generated);
    }

    public Task<string> CreateTestSetAsync(CopilotEnvironmentContext environment, string agentId, string name, CancellationToken cancellationToken)
    {
        CreatedTestSetNames.Add(name);
        return Task.FromResult(NextTestSetId);
    }

    public Task<IReadOnlyList<string>> AddConversationsAsync(CopilotEnvironmentContext environment, string agentId, string testSetId, IReadOnlyList<EvaluationConversation> conversations, CancellationToken cancellationToken)
    {
        OnAddConversations?.Invoke();

        if (AddConversationsThrows is not null)
        {
            return Task.FromException<IReadOnlyList<string>>(AddConversationsThrows);
        }

        if (EnforcedCapacity is { } capacity && StoredCount(testSetId) + conversations.Count > capacity)
        {
            return Task.FromException<IReadOnlyList<string>>(
                new InvalidOperationException($"The test set already holds {StoredCount(testSetId)} conversation(s) and {conversations.Count} more would carry it past {capacity}."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<string>>(cancellationToken);
        }

        AddedConversations.Add((testSetId, conversations.Count));

        var ids = conversations.Take(AcknowledgedConversations ?? conversations.Count).Select((_, index) => $"case-{index}").ToArray();

        if (!_stored.TryGetValue(testSetId, out var active))
        {
            active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _stored[testSetId] = active;
        }

        active.UnionWith(ids);

        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    private int StoredCount(string testSetId) =>
        (TestSetDetails.TryGetValue(testSetId, out var detail) ? detail.ConversationComponentIds.Where(id => id.Length > 0) : [])
            .Concat(_stored.TryGetValue(testSetId, out var active) ? active : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(id => !Removed.Contains(id));

    /// <summary>
    /// Answers a start the way the service does: with the run's identifier and state, and nothing
    /// that was already in the request.
    /// </summary>
    public Task<EvaluationRunSummary> StartRunAsync(CopilotEnvironmentContext environment, string agentId, EvaluationRunStart start, CancellationToken cancellationToken)
    {
        LastStart = start;
        return Task.FromResult(new EvaluationRunSummary { RunId = StartedRunId, State = EvaluationRunStates.Queued });
    }

    public Task<EvaluationRunSummary> CancelRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken)
    {
        CancelledRuns.Add(runId);
        return Task.FromResult(new EvaluationRunSummary { RunId = runId, State = EvaluationRunStates.Cancelled });
    }

    public Task<EvaluationRunSummary> DeleteRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken)
    {
        DeletedRuns.Add(runId);
        return Task.FromResult(new EvaluationRunSummary { RunId = runId, State = EvaluationRunStates.Deleted });
    }
}

internal sealed class FakeEvaluationComponentStore : IEvaluationComponentStore
{
    public List<string> Deleted { get; } = [];

    public FakeEvaluationPort? Port { get; set; }

    public int? ThrowAfter { get; set; }

    public Task DeleteAsync(CopilotEnvironmentContext environment, string componentId, CancellationToken cancellationToken)
    {
        if (Deleted.Count == ThrowAfter)
        {
            return Task.FromException(new InvalidOperationException($"The service refused to delete {componentId}."));
        }

        Deleted.Add(componentId);
        Port?.Removed.Add(componentId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeEvaluationTargetResolver : IEvaluationTargetResolver
{
    public static readonly CopilotEnvironmentContext Environment = new()
    {
        EnvironmentId = "env-1",
        TenantId = EnvironmentFixtures.ContosoTenant,
        DataverseOrganization = new Uri("https://org.crm.dynamics.com"),
        AgentManagementUrl = new Uri("https://powervamg.us-il301.gateway.prod.island.powerapps.com/"),
    };

    public static readonly EvaluationTarget Target = new(Environment, "bot-1", "Contoso Helpdesk");

    public List<string> Verbs { get; } = [];

    public EvaluationSelector? LastSelector { get; private set; }

    public Task<EvaluationTarget> ResolveAsync(EvaluationSelector selector, string verb, CancellationToken cancellationToken)
    {
        Verbs.Add(verb);
        LastSelector = selector;
        return Task.FromResult(Target);
    }
}

internal static class EvaluationScenarios
{
    public static EvaluationConversation Conversation(int number = 1, string question = "What is the refund window?", string? response = null) =>
        new() { Number = number, Exchanges = [new EvaluationExchange(question, response)] };

    public static EvaluationTestSetSummary TestSet(string testSetId = "testset-1", string displayName = "Nightly", int conversationCount = 3) =>
        new() { TestSetId = testSetId, DisplayName = displayName, ConversationCount = conversationCount };

    public static EvaluationTestSetDetail TestSetDetail(EvaluationTestSetSummary summary, params string[] conversationComponentIds) => new()
    {
        Summary = summary,
        Conversations = [.. conversationComponentIds.Select((_, index) => Conversation(index + 1))],
        ConversationComponentIds = conversationComponentIds,
    };

    public static EvaluationRunDetail RunDetail(string state, string runId = "run-1", DateTimeOffset? endTime = null, params EvaluationTestCaseResult[] testCases) => new()
    {
        Summary = new EvaluationRunSummary { RunId = runId, State = state, EndTime = endTime },
        TestCases = testCases,
    };

    public static EvaluationRunDetail NamedRunDetail(string state, string runId = "run-9") => new()
    {
        Summary = new EvaluationRunSummary
        {
            RunId = runId,
            State = state,
            Name = "Nightly",
            TestSetId = "testset-7",
            EvaluationSetType = EvaluationSetTypes.MultiTurn,
            TestCaseCount = 3,
            StartTime = DateTimeOffset.UnixEpoch,
        },
    };
}
