using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationRunServiceTests
{
    private readonly FakeEvaluationPort _port = new();
    private readonly FakeEvaluationComponentStore _components = new();
    private readonly FakeEvaluationTargetResolver _targets = new();
    private readonly FakeCopilotFileSystem _files = new();
    private readonly ScriptedConfirmationPrompt _confirmation = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly RecordingMessagePresenter _presenter = new();
    private readonly AdvancingTestClock _clock = new();

    private EvaluationRunService Create()
    {
        var lookup = new EvaluationTestSetLookup(_port);
        var conversations = new EvaluationConversationProvider(_port, _files);
        var writes = new EvaluationTestSetService(_targets, lookup, conversations, _port, _components, _confirmation);

        return new EvaluationRunService(_targets, lookup, writes, conversations, _port, _files, _presenter, _clock, (delay, _) =>
        {
            _delays.Add(delay);
            _clock.Advance(delay);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RunAsync_RejectsTestSetCombinedWithCsv()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", FromCsv = "cases.csv" }, CancellationToken.None));

        Assert.Contains("--testset names an existing test set", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsTestSetCombinedWithGenerate()
    {
        await Assert.ThrowsAsync<UsageException>(() => Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Generate = 5 }, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_RejectsRequestWithNoSource()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => Create().RunAsync(new EvaluationRunRequest(), CancellationToken.None));

        Assert.Contains("--from-csv or --generate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_StartsExistingTestSetWithoutWriting()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal("evaluation-started", result.Status);
        Assert.Equal("testset-7", _port.LastStart!.TestSetId);
        Assert.Equal("Nightly", _port.LastStart.Name);
        Assert.Empty(_port.CreatedTestSetNames);
        Assert.Empty(_port.AddedConversations);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task RunAsync_ResolvesTestSetById()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        await Create().RunAsync(new EvaluationRunRequest { TestSet = "testset-7" }, CancellationToken.None);

        Assert.Equal("testset-7", _port.LastStart!.TestSetId);
    }

    [Fact]
    public async Task RunAsync_RefusesEmptyTestSet()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 0));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetEmpty, error.ErrorKind);
    }

    [Fact]
    public async Task RunAsync_ReportsMissingTestSet()
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
    }

    [Fact]
    public async Task RunAsync_RequiresNameWhenCreatingTestSet()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var error = await Assert.ThrowsAsync<UsageException>(() => Create().RunAsync(new EvaluationRunRequest { FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Contains("--name is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CreatesTestSetFromCsv()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi\n2,Bye,Later";

        var result = await Create().RunAsync(new EvaluationRunRequest { FromCsv = "/work/cases.csv", Name = " Nightly " }, CancellationToken.None);

        Assert.Equal("evaluation-started", result.Status);
        Assert.Equal(["Nightly"], _port.CreatedTestSetNames);
        Assert.Equal([("testset-new", 2)], _port.AddedConversations);
        Assert.Equal("testset-new", _port.LastStart!.TestSetId);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task RunAsync_RefusesExistingNameWithoutUpdateTestSet()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().RunAsync(new EvaluationRunRequest { FromCsv = "/work/cases.csv", Name = "Nightly" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetAlreadyExists, error.ErrorKind);
        Assert.Contains("--testset Nightly", error.Remediation!, StringComparison.Ordinal);
        Assert.Contains("--update-testset", error.Remediation!, StringComparison.Ordinal);
        Assert.Empty(_port.CreatedTestSetNames);
    }

    [Fact]
    public async Task RunAsync_ReplacesConversationsWhenUpdateTestSetGiven()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        await Create().RunAsync(new EvaluationRunRequest { FromCsv = "/work/cases.csv", Name = "Nightly", UpdateTestSet = true }, CancellationToken.None);

        Assert.Equal(["case-a", "case-b"], _components.Deleted);
        Assert.Equal([("testset-7", 1)], _port.AddedConversations);
        Assert.Equal("testset-7", _port.LastStart!.TestSetId);
        Assert.Empty(_port.CreatedTestSetNames);
    }

    [Fact]
    public async Task RunAsync_UpdateTestSetKeepsTheExistingConversationsWhenNothingIsGenerated()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().RunAsync(new EvaluationRunRequest { Generate = 3, Name = "Nightly", UpdateTestSet = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationsNotGenerated, error.ErrorKind);
        Assert.Empty(_components.Deleted);
        Assert.Empty(_port.AddedConversations);
        Assert.Null(_port.LastStart);
    }

    [Fact]
    public async Task GenerateAsync_RefusesAnEmptyGeneration()
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().GenerateAsync(new EvaluationGenerateRequest { Output = "/work/out.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationsNotGenerated, error.ErrorKind);
        Assert.Empty(_files.Written);
    }

    [Fact]
    public async Task RunAsync_RejectsANonPositiveWatchTimeoutBeforeTouchingTheTestSet()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a");

        var error = await Assert.ThrowsAsync<UsageException>(
            () => Create().RunAsync(new EvaluationRunRequest { FromCsv = "/work/cases.csv", Name = "Nightly", UpdateTestSet = true, Watch = true, TimeoutMinutes = 0 }, CancellationToken.None));

        Assert.Contains("--timeout-minutes must be a positive integer", error.Message, StringComparison.Ordinal);
        Assert.Null(_port.LastStart);
        Assert.Empty(_port.AddedConversations);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task RunAsync_CreatesTheNamedSetWhenTheNameReadsLikeAnotherSetsId()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var other = EvaluationScenarios.TestSet("Nightly", "Something Else", 2);
        _port.TestSets.Add(other);
        _port.TestSetDetails["Nightly"] = EvaluationScenarios.TestSetDetail(other, "case-a");

        await Create().RunAsync(new EvaluationRunRequest { FromCsv = "/work/cases.csv", Name = "Nightly", UpdateTestSet = true }, CancellationToken.None);

        Assert.Equal(["Nightly"], _port.CreatedTestSetNames);
        Assert.Empty(_components.Deleted);
        Assert.Equal("testset-new", _port.LastStart!.TestSetId);
    }

    [Fact]
    public async Task RunAsync_PassesConnectionAndPublishedThrough()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Connection = "conn-1", Published = true }, CancellationToken.None);

        Assert.Equal("conn-1", _port.LastStart!.ConnectionId);
        Assert.True(_port.LastStart.RunOnPublishedBot);
    }

    [Fact]
    public async Task RunAsync_WatchPollsUntilCompleted()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Queued));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Completed));

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, CancellationToken.None);

        Assert.Equal("evaluation-completed", result.Status);
        Assert.False(result.TimedOut);
        Assert.Equal(3, _port.GetRunCalls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], _delays);
    }

    [Theory]
    [InlineData(EvaluationRunStates.Cancelled)]
    [InlineData(EvaluationRunStates.Failed)]
    [InlineData(EvaluationRunStates.Deleted)]
    public async Task RunAsync_WatchStopsOnEveryTerminalState(string state)
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(state));

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, CancellationToken.None);

        Assert.Equal("evaluation-completed", result.Status);
        Assert.Equal(1, _port.GetRunCalls);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task RunAsync_WatchStopsOnEndTimeAloneForAnUnknownState()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail("SomethingNew", endTime: DateTimeOffset.UnixEpoch));

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, CancellationToken.None);

        Assert.Equal("evaluation-completed", result.Status);
        Assert.Equal(1, _port.GetRunCalls);
    }

    [Fact]
    public async Task RunAsync_WatchReportsTimeoutWithoutFailing()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        for (var index = 0; index < 32; index++)
        {
            _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));
        }

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true, TimeoutMinutes = 1 }, CancellationToken.None);

        Assert.Equal("evaluation-running", result.Status);
        Assert.True(result.TimedOut);
        Assert.Equal(12, _port.GetRunCalls);
    }

    [Fact]
    public async Task RunAsync_WatchFailsWhenRunDisappears()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.RunNotFound, error.ErrorKind);
    }

    [Fact]
    public async Task RunAsync_WatchHonoursCancellation()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));

        using var source = new CancellationTokenSource();
        var lookup = new EvaluationTestSetLookup(_port);
        var conversations = new EvaluationConversationProvider(_port, _files);
        var service = new EvaluationRunService(
            _targets,
            lookup,
            new EvaluationTestSetService(_targets, lookup, conversations, _port, _components, _confirmation),
            conversations,
            _port,
            _files,
            _presenter,
            _clock,
            (_, token) =>
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, source.Token));
    }

    [Fact]
    public async Task RunAsync_WatchWritesTheReportWhenOutputGiven()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Completed));

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true, Output = "/work/run.json" }, CancellationToken.None);

        Assert.Equal("/work/run.json", result.OutputPath);
        Assert.Contains("/work/run.json", _files.Written);
        Assert.Contains("\"runId\"", _files.Files["/work/run.json"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WatchWritesNothingWhenPollingGivesUp()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        for (var index = 0; index < 32; index++)
        {
            _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));
        }

        var result = await Create().RunAsync(
            new EvaluationRunRequest { TestSet = "Nightly", Watch = true, TimeoutMinutes = 1, Output = "/work/run.json" },
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Null(result.OutputPath);
        Assert.Empty(_files.Written);
    }

    [Theory]
    [InlineData("/work/run.md")]
    [InlineData("/work/run.txt")]
    [InlineData("/work/run")]
    public async Task RunAsync_WatchWritesJsonWhateverTheFileIsNamed(string output)
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Completed));

        await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true, Output = output }, CancellationToken.None);

        Assert.StartsWith("{", _files.Files[output].TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RefusesOutputWithoutWatchBeforeStartingTheRun()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        var error = await Assert.ThrowsAsync<UsageException>(
            () => Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Output = "/work/run.json" }, CancellationToken.None));

        Assert.Contains("--output", error.Message, StringComparison.Ordinal);
        Assert.Contains("--watch", error.Message, StringComparison.Ordinal);
        Assert.Null(_port.LastStart);
        Assert.Empty(_files.Written);
        Assert.Empty(_presenter.Messages);
    }

    [Fact]
    public async Task RunAsync_WithoutWatchWritesNothing()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Empty(_files.Written);
        Assert.Equal(0, _port.GetRunCalls);
        Assert.Empty(_presenter.Messages);
    }

    [Fact]
    public async Task RunAsync_WatchReportsProgressAsCasesAreEvaluated()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Queued));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress, testCases: [Case(null), Case(null)]));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress, testCases: [Case("Pass"), Case(null)]));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Completed, testCases: [Case("Pass"), Case("Fail")]));

        await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, CancellationToken.None);

        Assert.Equal(
            [
                "Evaluation run run-1 started against test set 'Nightly'. Waiting for results.",
                "  Queued.",
                "  InProgress: 0 of 2 conversation(s) evaluated.",
                "  InProgress: 1 of 2 conversation(s) evaluated.",
                "  Completed: 2 of 2 conversation(s) evaluated.",
            ],
            _presenter.Messages);
    }

    [Fact]
    public async Task RunAsync_WatchReportsAnUnchangedStateOnlyOnce()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Completed));

        await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly", Watch = true }, CancellationToken.None);

        Assert.Equal(
            [
                "Evaluation run run-1 started against test set 'Nightly'. Waiting for results.",
                "  InProgress.",
                "  Completed.",
            ],
            _presenter.Messages);
    }

    private static EvaluationTestCaseResult Case(string? evaluationResult) =>
        new() { TestCaseId = Guid.NewGuid().ToString(), EvaluationResult = evaluationResult };

    [Fact]
    public async Task RunAsync_ReportsTheIdentityTheStartAnswerLeavesOut()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        var result = await Create().RunAsync(new EvaluationRunRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal("Nightly", result.Run.Name);
        Assert.Equal("testset-7", result.Run.TestSetId);
        Assert.Equal("Nightly", result.TestSetName);
    }

    [Theory]
    [InlineData(EvaluationRunStates.Queued)]
    [InlineData(EvaluationRunStates.InProgress)]
    public async Task CancelAsync_KeepsTheIdentityTheStopAnswerLeavesOut(string state)
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.NamedRunDetail(state));

        var result = await Create().CancelAsync(new EvaluationCancelRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Equal(EvaluationRunStates.Cancelled, result.Run.State);
        Assert.Equal("Nightly", result.Run.Name);
        Assert.Equal("testset-7", result.Run.TestSetId);
        Assert.Equal("Conversation", result.Run.DataType);
        Assert.Equal(3, result.Run.TestCaseCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.Run.StartTime);
    }

    [Fact]
    public async Task DeleteAsync_KeepsTheIdentityTheDeleteAnswerLeavesOut()
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.NamedRunDetail(EvaluationRunStates.Completed));

        var result = await Create().DeleteAsync(new EvaluationDeleteRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Equal(EvaluationRunStates.Deleted, result.Run.State);
        Assert.Equal("Nightly", result.Run.Name);
        Assert.Equal("testset-7", result.Run.TestSetId);
        Assert.Equal("Conversation", result.Run.DataType);
    }

    [Fact]
    public async Task CancelAsync_RequiresRunId()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => Create().CancelAsync(new EvaluationCancelRequest(), CancellationToken.None));

        Assert.Contains("A run id is required", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EvaluationRunStates.Queued)]
    [InlineData(EvaluationRunStates.InProgress)]
    public async Task CancelAsync_StopsInFlightRun(string state)
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(state, "run-9"));

        var result = await Create().CancelAsync(new EvaluationCancelRequest { RunId = " run-9 " }, CancellationToken.None);

        Assert.Equal("evaluation-cancelled", result.Status);
        Assert.Equal(["run-9"], _port.CancelledRuns);
    }

    [Theory]
    [InlineData(EvaluationRunStates.Completed)]
    [InlineData(EvaluationRunStates.Cancelled)]
    [InlineData(EvaluationRunStates.Failed)]
    public async Task CancelAsync_RefusesFinishedRun(string state)
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(state, "run-9"));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().CancelAsync(new EvaluationCancelRequest { RunId = "run-9" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.RunNotCancellable, error.ErrorKind);
        Assert.Empty(_port.CancelledRuns);
    }

    [Fact]
    public async Task CancelAsync_ReportsMissingRun()
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().CancelAsync(new EvaluationCancelRequest { RunId = "run-9" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.RunNotFound, error.ErrorKind);
    }

    [Theory]
    [InlineData(EvaluationRunStates.Completed)]
    [InlineData(EvaluationRunStates.Cancelled)]
    [InlineData(EvaluationRunStates.Failed)]
    public async Task DeleteAsync_RemovesTerminalRun(string state)
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(state, "run-9"));

        var result = await Create().DeleteAsync(new EvaluationDeleteRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Equal("evaluation-deleted", result.Status);
        Assert.Equal(["run-9"], _port.DeletedRuns);
    }

    [Theory]
    [InlineData(EvaluationRunStates.Queued)]
    [InlineData(EvaluationRunStates.InProgress)]
    public async Task DeleteAsync_RefusesRunStillInFlight(string state)
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(state, "run-9"));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().DeleteAsync(new EvaluationDeleteRequest { RunId = "run-9" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.RunNotTerminal, error.ErrorKind);
        Assert.Contains("agent eval cancel --run-id run-9", error.Remediation!, StringComparison.Ordinal);
        Assert.Empty(_port.DeletedRuns);
    }

    [Fact]
    public async Task DeleteAsync_AcceptsRunCarryingOnlyAnEndTime()
    {
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail("SomethingNew", "run-9", DateTimeOffset.UnixEpoch));

        await Create().DeleteAsync(new EvaluationDeleteRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Equal(["run-9"], _port.DeletedRuns);
    }

    [Fact]
    public async Task CancelThenDeleteSucceedsInSequence()
    {
        var service = Create();
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.InProgress, "run-9"));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Cancelled, "run-9"));

        await service.CancelAsync(new EvaluationCancelRequest { RunId = "run-9" }, CancellationToken.None);
        await service.DeleteAsync(new EvaluationDeleteRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Equal(["run-9"], _port.CancelledRuns);
        Assert.Equal(["run-9"], _port.DeletedRuns);
    }

    [Fact]
    public async Task GenerateAsync_RejectsNonPositiveCount()
    {
        await Assert.ThrowsAsync<UsageException>(() => Create().GenerateAsync(new EvaluationGenerateRequest { Count = 0 }, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_RejectsNonPositiveTurns()
    {
        await Assert.ThrowsAsync<UsageException>(() => Create().GenerateAsync(new EvaluationGenerateRequest { Turns = -1 }, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_UsesPortalDefaults()
    {
        _port.Generated.Add(EvaluationScenarios.Conversation());

        await Create().GenerateAsync(new EvaluationGenerateRequest(), CancellationToken.None);

        Assert.Equal([(10, 3, "1033")], _port.GenerateCalls);
    }

    [Fact]
    public async Task GenerateAsync_ReportsEveryTurnItGenerated()
    {
        _port.Generated.Add(new EvaluationConversation
        {
            Number = 1,
            Source = EvaluationConversationSource.Generated,
            Exchanges = [new EvaluationExchange("First", "A"), new EvaluationExchange("Second", "B")],
        });

        var result = await Create().GenerateAsync(new EvaluationGenerateRequest(), CancellationToken.None);

        var conversation = Assert.Single(result.Conversations);
        Assert.Equal(["First", "Second"], conversation.Turns.Select(turn => turn.Query));
        Assert.Equal(["A", "B"], conversation.Turns.Select(turn => turn.Answer));
    }

    [Fact]
    public async Task GenerateAsync_WritesCsvWhenOutputGiven()
    {
        _port.Generated.Add(EvaluationScenarios.Conversation(1, "Hello", "Hi"));

        var result = await Create().GenerateAsync(new EvaluationGenerateRequest { Count = 20, Turns = 5, Output = "/work/out.csv" }, CancellationToken.None);

        Assert.Equal([(20, 5, "1033")], _port.GenerateCalls);
        Assert.Equal("/work/out.csv", result.OutputPath);
        Assert.Single(result.Conversations);
        Assert.Contains("Hello", _files.Files["/work/out.csv"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_WritesNothingWithoutOutput()
    {
        _port.Generated.Add(EvaluationScenarios.Conversation());

        var result = await Create().GenerateAsync(new EvaluationGenerateRequest(), CancellationToken.None);

        Assert.Null(result.OutputPath);
        Assert.Empty(_files.Written);
    }

    [Fact]
    public async Task EveryVerbNamesItselfWhenResolvingTheAgent()
    {
        var service = Create();
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));
        _port.Generated.Add(EvaluationScenarios.Conversation());
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Completed, "run-9"));
        _port.RunDetails.Enqueue(EvaluationScenarios.RunDetail(EvaluationRunStates.Queued, "run-9"));

        await service.RunAsync(new EvaluationRunRequest { TestSet = "Nightly" }, CancellationToken.None);
        await service.DeleteAsync(new EvaluationDeleteRequest { RunId = "run-9" }, CancellationToken.None);
        await service.CancelAsync(new EvaluationCancelRequest { RunId = "run-9" }, CancellationToken.None);
        await service.GenerateAsync(new EvaluationGenerateRequest(), CancellationToken.None);

        Assert.Equal(["agent eval run", "agent eval delete", "agent eval cancel", "agent eval generate"], _targets.Verbs);
    }
}

internal sealed class AdvancingTestClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan amount) => UtcNow += amount;
}
