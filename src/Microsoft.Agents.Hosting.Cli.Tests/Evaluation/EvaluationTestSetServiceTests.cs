using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationTestSetServiceTests
{
    private readonly FakeEvaluationPort _port = new();
    private readonly FakeEvaluationComponentStore _components = new();
    private readonly FakeEvaluationTargetResolver _targets = new();
    private readonly FakeCopilotFileSystem _files = new();

    private EvaluationTestSetService Create(ScriptedConfirmationPrompt? confirmation = null) => new(
        _targets,
        new EvaluationTestSetLookup(_port),
        new EvaluationConversationProvider(_port, _files),
        _port,
        _components,
        confirmation ?? new ScriptedConfirmationPrompt());

    private EvaluationQueryService CreateQueries() => new(_targets, new EvaluationTestSetLookup(_port), _port, _files);

    [Fact]
    public async Task CreateAsync_RequiresName()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => Create().CreateAsync(new EvaluationTestSetCreateRequest { FromCsv = "cases.csv" }, CancellationToken.None));

        Assert.Contains("--name is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_WritesSetThenConversations()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi\n1,More,Sure\n2,Bye,Later";

        var result = await Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None);

        Assert.Equal("test-set-created", result.Status);
        Assert.Equal(["Nightly"], _port.CreatedTestSetNames);
        Assert.Equal([("testset-new", 2)], _port.AddedConversations);
        Assert.Equal(2, result.TestSet.ConversationCount);
        Assert.Equal("MultiTurn", result.TestSet.EvaluationSetType);
        Assert.Collection(result.Conversations, first => Assert.Equal(2, first.TurnCount), second => Assert.Equal(1, second.TurnCount));
    }

    [Fact]
    public async Task CreateAsync_RefusesDuplicateName()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetAlreadyExists, error.ErrorKind);
        Assert.Contains("4 conversation(s)", error.Message, StringComparison.Ordinal);
        Assert.Empty(_port.CreatedTestSetNames);
    }

    [Fact]
    public async Task CreateAsync_ReportsAmbiguousNameWithIds()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-a", "Nightly", 1));
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-b", "Nightly", 2));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetAmbiguous, error.ErrorKind);
        Assert.Contains("testset-a, testset-b", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_CreatesASetWhoseNameReadsLikeAnotherSetsId()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 4));

        var result = await Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "testset-7", FromCsv = "/work/cases.csv" }, CancellationToken.None);

        Assert.Equal("test-set-created", result.Status);
        Assert.Equal(["testset-7"], _port.CreatedTestSetNames);
    }

    [Fact]
    public async Task CreateAsync_GeneratesConversationsWhenAsked()
    {
        _port.Generated.Add(EvaluationScenarios.Conversation(1, "Hello"));
        _port.Generated.Add(EvaluationScenarios.Conversation(2, "Bye"));

        var result = await Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly", Generate = 2, Turns = 4 }, CancellationToken.None);

        Assert.Equal([(2, 4, "1033")], _port.GenerateCalls);
        Assert.Equal([("testset-new", 2)], _port.AddedConversations);
        Assert.Equal(2, result.Conversations.Count);
    }

    [Fact]
    public async Task CreateAsync_RejectsCsvCombinedWithGenerate()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var error = await Assert.ThrowsAsync<UsageException>(() => Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly", FromCsv = "/work/cases.csv", Generate = 2 }, CancellationToken.None));

        Assert.Contains("cannot be combined", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsRequestWithNoSource()
    {
        await Assert.ThrowsAsync<UsageException>(() => Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly" }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_ReportsMissingCsv()
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly", FromCsv = "/work/missing.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationFileInvalid, error.ErrorKind);
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_FailsWhenTheServiceStoredOnlySomeOfTheConversations()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi\n2,Bye,Later";
        _port.AcknowledgedConversations = 1;

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().CreateAsync(new EvaluationTestSetCreateRequest { Name = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationsNotStored, error.ErrorKind);
        Assert.Contains("Nightly", error.Message, StringComparison.Ordinal);
        Assert.Equal(["Nightly"], _port.CreatedTestSetNames);
    }

    [Fact]
    public async Task UpdateAsync_RefusesAnAppendThatWouldOverflowTheSet()
    {
        _files.Files["/work/cases.csv"] = string.Join(
            "\n",
            ["conversationNumber,question,response", .. Enumerable.Range(1, 5).Select(number => $"{number},Hello,Hi")]);

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(
            summary,
            [.. Enumerable.Range(1, EvaluationConversationLimits.MaximumConversations).Select(number => $"case-{number}")]);

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationLimitExceeded, error.ErrorKind);
        Assert.Contains($"{EvaluationConversationLimits.MaximumConversations}", error.Message, StringComparison.Ordinal);
        Assert.Empty(_port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_AcceptsAnAppendThatExactlyFillsTheSet()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var stored = EvaluationConversationLimits.MaximumConversations - 1;
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", stored);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, [.. Enumerable.Range(1, stored).Select(number => $"case-{number}")]);

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None);

        Assert.Equal([("testset-7", 1)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceIgnoresWhatTheSetHeldWhenCheckingCapacity()
    {
        _files.Files["/work/cases.csv"] = string.Join(
            "\n",
            ["conversationNumber,question,response", .. Enumerable.Range(1, EvaluationConversationLimits.MaximumConversations).Select(number => $"{number},Hello,Hi")]);

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(
            summary,
            [.. Enumerable.Range(1, EvaluationConversationLimits.MaximumConversations).Select(number => $"case-{number}")]);

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal([("testset-7", EvaluationConversationLimits.MaximumConversations)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_RequiresTestSet()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { FromCsv = "cases.csv" }, CancellationToken.None));

        Assert.Contains("--testset is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_AppendsWithoutDeletingByDefault()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        var result = await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None);

        Assert.Equal("test-set-updated", result.Status);
        Assert.Empty(_components.Deleted);
        Assert.Equal([("testset-7", 1)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceRemovesTheConversationsTheSetAlreadyHeld()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "testset-7", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal(["case-a", "case-b"], _components.Deleted);
        Assert.Equal([("testset-7", 1)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceKeepsTheConversationsItJustAdded()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");
        _port.OnAddConversations = () => _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b", "case-0");

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal(["case-a", "case-b"], _components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceRefusesToDeleteConversationsAnotherWriterAddedMeanwhile()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a");
        _port.OnAddConversations = () => _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-concurrent", "case-0");

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ReplacementConflicted, error.ErrorKind);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceFailsWhenTheSetCannotBeReadBackAfterTheWrite()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");
        _port.OnAddConversations = () => _port.TestSetDetails.Remove("testset-7");

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceWritesNothingWhenTheSetCannotBeReadBeforeTheWrite()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
        Assert.Empty(_port.AddedConversations);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_KeepsEveryConversationWhenTheServiceStoredOnlySomeOfThem()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi\n2,Bye,Later";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 1);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a");
        _port.AcknowledgedConversations = 1;

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationsNotStored, error.ErrorKind);
        Assert.DoesNotContain("left as it was", error.Message, StringComparison.Ordinal);
        Assert.Contains("part of what was asked for", error.Message, StringComparison.Ordinal);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceStoresTheNewConversationsBeforeRemovingTheOldOnes()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        var deletedWhenStored = -1;
        _port.OnAddConversations = () => deletedWhenStored = _components.Deleted.Count;

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal(0, deletedWhenStored);
        Assert.Equal(["case-a", "case-b"], _components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceKeepsTheExistingConversationsWhenTheWriteIsRefused()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");
        _port.AddConversationsThrows = new InvalidOperationException("the service refused the write");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceRefusesAnEmptyGenerationWithoutTouchingTheSet()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", Generate = 3, Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationsNotGenerated, error.ErrorKind);
        Assert.Empty(_components.Deleted);
        Assert.Empty(_port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceSkipsBlankComponentIds()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", string.Empty);

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal(["case-a"], _components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReportsUnknownTestSet()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
        Assert.Empty(_port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_FailsWhenTheSetCannotBeReadAfterTheWrite()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceStaysWithinTheLimitWhileSwappingAFullSet()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        var held = Held(EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, held);
        _port.EnforcedCapacity = EvaluationConversationLimits.MaximumConversations;
        _components.Port = _port;

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal(held, _components.Deleted);
        Assert.Equal([("testset-7", EvaluationConversationLimits.MaximumConversations)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceRemovesNothingBeforeTheWriteWhenBothSetsFit()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");
        _port.AddConversationsThrows = new InvalidOperationException("refused");
        _components.Port = _port;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceFreesOnlyAsMuchRoomAsTheLimitDemands()
    {
        _files.Files["/work/cases.csv"] = Csv(5);

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        var held = Held(EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, held);
        _port.EnforcedCapacity = EvaluationConversationLimits.MaximumConversations;
        _components.Port = _port;
        _port.OnAddConversations = () => Assert.Equal(5, _components.Deleted.Count);

        await Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None);

        Assert.Equal(held, _components.Deleted);
    }

    [Fact]
    public async Task UpdateAsync_ReplacePutsBackWhatItRemovedWhenTheWriteFails()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        var held = Held(EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, held);
        _components.Port = _port;

        var attempts = 0;
        _port.OnAddConversations = () => _port.AddConversationsThrows = ++attempts == 1 ? new InvalidOperationException("refused") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationConversationLimits.MaximumConversations, _components.Deleted.Count);
        Assert.Equal([("testset-7", EvaluationConversationLimits.MaximumConversations)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceReportsTheSetIsShortWhenThePutBackAlsoFails()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, Held(EvaluationConversationLimits.MaximumConversations));
        _port.AddConversationsThrows = new InvalidOperationException("refused");
        _components.Port = _port;

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ReplacementInterrupted, error.ErrorKind);
        Assert.Contains("20", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceConflictSaysWhenConversationsWereRemovedToMakeRoom()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, Held(EvaluationConversationLimits.MaximumConversations));
        _components.Port = _port;
        _port.OnAddConversations = () => _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-concurrent");

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ReplacementConflicted, error.ErrorKind);
        Assert.Contains("were not put back", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_ReplacePutsBackWhatItRemovedWhenAFreeingDeleteFails()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, Held(EvaluationConversationLimits.MaximumConversations));
        _components.Port = _port;
        _components.ThrowAfter = 3;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal([("testset-7", 3)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplacePutsBackWhatItRemovedWhenTheOperatorInterruptsIt()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, Held(EvaluationConversationLimits.MaximumConversations));
        _components.Port = _port;

        using var interrupt = new CancellationTokenSource();
        _port.OnAddConversations = interrupt.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, interrupt.Token));

        Assert.Equal([("testset-7", EvaluationConversationLimits.MaximumConversations)], _port.AddedConversations);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceSaysTheSetWasPutBackWhenAPartialWriteWasUndone()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, Held(EvaluationConversationLimits.MaximumConversations));
        _components.Port = _port;

        var writes = 0;
        _port.OnAddConversations = () => _port.AcknowledgedConversations = ++writes == 1 ? 3 : null;

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.ConversationsNotStored, error.ErrorKind);
        Assert.Contains("put back as it was", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("now holds part of what was asked for", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_ReplaceRemovesThePartialWriteItUndid()
    {
        _files.Files["/work/cases.csv"] = FullCsv();

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", EvaluationConversationLimits.MaximumConversations);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, Held(EvaluationConversationLimits.MaximumConversations));
        _components.Port = _port;

        var writes = 0;
        _port.OnAddConversations = () => _port.AcknowledgedConversations = ++writes == 1 ? 3 : null;

        await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Equal(["case-0", "case-1", "case-2"], _components.Deleted.Except(Held(EvaluationConversationLimits.MaximumConversations)));
    }

    [Fact]
    public async Task UpdateAsync_KeepsThePartialWriteWhenNothingHadToBeFreed()
    {
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi\n2,Bye,Later";

        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 1);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a");
        _port.AcknowledgedConversations = 1;
        _components.Port = _port;

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => Create().UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv", Replace = true }, CancellationToken.None));

        Assert.Contains("now holds part of what was asked for", error.Message, StringComparison.Ordinal);
        Assert.Empty(_components.Deleted);
    }

    private static string[] Held(int count) => [.. Enumerable.Range(0, count).Select(index => $"held-{index}")];

    private static string FullCsv() => Csv(EvaluationConversationLimits.MaximumConversations);

    private static string Csv(int conversations) =>
        string.Join('\n', ["conversationNumber,question,response", .. Enumerable.Range(1, conversations).Select(number => $"{number},Question {number},Answer {number}")]);

    [Fact]
    public async Task DeleteAsync_RequiresTestSet()
    {
        await Assert.ThrowsAsync<UsageException>(() => Create().DeleteAsync(new EvaluationTestSetDeleteRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_PromptsWithTheSetNameThenDeletes()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 3));

        var confirmation = new ScriptedConfirmationPrompt();
        var result = await Create(confirmation).DeleteAsync(new EvaluationTestSetDeleteRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal("test-set-deleted", result.Status);
        Assert.Equal(1, confirmation.Calls);
        Assert.Contains("3 conversation(s)", confirmation.LastDetail!, StringComparison.Ordinal);
        Assert.Equal(["testset-7"], _components.Deleted);
    }

    [Fact]
    public async Task DeleteAsync_DeclinedLeavesTheSetAlone()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 3));

        var result = await Create(new ScriptedConfirmationPrompt { Answer = false }).DeleteAsync(new EvaluationTestSetDeleteRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.Equal("testset-7", result.TestSetId);
        Assert.Empty(_components.Deleted);
    }

    [Fact]
    public async Task DeleteAsync_MismatchedNameLeavesTheSetAlone()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 3));

        var result = await Create(new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Mismatched }).DeleteAsync(new EvaluationTestSetDeleteRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal("name-mismatch", result.Status);
        Assert.Empty(_components.Deleted);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DeleteAsync_SkipsThePromptWhenConsentIsAlreadyGiven(bool force, bool nonInteractive)
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 3));

        var confirmation = new ScriptedConfirmationPrompt { Throws = true };
        var result = await Create(confirmation).DeleteAsync(new EvaluationTestSetDeleteRequest { TestSet = "Nightly", Force = force, NonInteractive = nonInteractive }, CancellationToken.None);

        Assert.Equal("test-set-deleted", result.Status);
        Assert.Equal(0, confirmation.Calls);
        Assert.Equal(["testset-7"], _components.Deleted);
    }

    [Fact]
    public async Task DeleteAsync_ReportsUnknownTestSet()
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(() => Create().DeleteAsync(new EvaluationTestSetDeleteRequest { TestSet = "Nightly" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
    }

    [Fact]
    public async Task ListAsync_RejectsNonPositiveLimit()
    {
        await Assert.ThrowsAsync<UsageException>(() => CreateQueries().ListAsync(new EvaluationListRequest { Limit = 0 }, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_AsksForOneExtraRunToDetectTruncation()
    {
        for (var index = 0; index < 5; index++)
        {
            _port.Runs.Add(new EvaluationRunSummary { RunId = $"run-{index}", State = EvaluationRunStates.Completed });
        }

        var result = await CreateQueries().ListAsync(new EvaluationListRequest { Limit = 3 }, CancellationToken.None);

        Assert.Equal(4, _port.RequestedRunCount);
        Assert.Equal(3, result.Runs.Count);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.Limit);
    }

    [Fact]
    public async Task ListAsync_DoesNotOverflowTheLargestLimitItAccepts()
    {
        await CreateQueries().ListAsync(new EvaluationListRequest { Limit = int.MaxValue }, CancellationToken.None);

        Assert.Equal(int.MaxValue, _port.RequestedRunCount);
    }

    [Fact]
    public async Task ListAsync_DefaultsToTwentyRuns()
    {
        var result = await CreateQueries().ListAsync(new EvaluationListRequest(), CancellationToken.None);

        Assert.Equal(21, _port.RequestedRunCount);
        Assert.Equal(20, result.Limit);
        Assert.False(result.Truncated);
        Assert.Equal("evaluations-listed", result.Status);
    }

    [Fact]
    public async Task ShowAsync_RequiresRunId()
    {
        await Assert.ThrowsAsync<UsageException>(() => CreateQueries().ShowAsync(new EvaluationShowRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task ShowAsync_ReportsMissingRun()
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(() => CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.RunNotFound, error.ErrorKind);
    }

    [Fact]
    public async Task ShowAsync_ProjectsEveryCase()
    {
        _port.RunDetails.Enqueue(new EvaluationRunDetail
        {
            Summary = new EvaluationRunSummary
            {
                RunId = "run-9",
                State = EvaluationRunStates.Completed,
                Credits = 13.58,
                Totals = new EvaluationRunTotals { Succeeded = 1, Failed = 1 },
            },
            TestCases =
            [
                new EvaluationTestCaseResult
                {
                    TestCaseId = "case-1",
                    DisplayName = "Refunds",
                    EvaluationResult = "Pass",
                    Dimensions = new EvaluationQualityDimensions { Relevance = "Pass", Groundedness = "Pass", Completeness = "Fail", Abstention = "Pass" },
                    Queries = [new EvaluationQueryResult { Query = "Hello", Answer = "Hi" }],
                },
            ],
        });

        var result = await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Equal("evaluation-shown", result.Status);
        Assert.Equal(0.5, result.Run.Score);
        Assert.Equal(13.58, result.Run.Credits);
        var testCase = Assert.Single(result.Cases);
        Assert.Equal("Refunds", testCase.DisplayName);
        Assert.Equal("Fail", testCase.Completeness);
        Assert.Equal("Hi", Assert.Single(testCase.Turns).Answer);
    }

    private void EnqueueRunWithCases(int caseCount)
    {
        _port.RunDetails.Enqueue(new EvaluationRunDetail
        {
            Summary = new EvaluationRunSummary { RunId = "run-9", State = EvaluationRunStates.Completed },
            TestCases =
            [
                .. Enumerable.Range(1, caseCount).Select(number => new EvaluationTestCaseResult
                {
                    TestCaseId = $"case-{number}",
                    DisplayName = $"question {number}",
                    EvaluationResult = "Pass",
                    Queries = [new EvaluationQueryResult { Query = $"question {number}", Answer = "replied" }],
                }),
            ],
        });
    }

    [Fact]
    public async Task ShowAsync_RequestsNoTranscriptByDefault()
    {
        EnqueueRunWithCases(3);

        var result = await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9" }, CancellationToken.None);

        Assert.Empty(result.TranscriptNumbers);
        Assert.Null(result.OutputPath);
        Assert.Empty(_files.Written);
    }

    [Fact]
    public async Task ShowAsync_RequestsEveryTranscript()
    {
        EnqueueRunWithCases(3);

        var result = await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Transcript = true }, CancellationToken.None);

        Assert.Equal([1, 2, 3], result.TranscriptNumbers);
    }

    [Fact]
    public async Task ShowAsync_RequestsOneTranscript()
    {
        EnqueueRunWithCases(3);

        var result = await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Conversation = 2 }, CancellationToken.None);

        Assert.Equal([2], result.TranscriptNumbers);
    }

    [Fact]
    public async Task ShowAsync_NarrowsToTheNamedConversationWhenBothWereGiven()
    {
        EnqueueRunWithCases(3);

        var result = await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Transcript = true, Conversation = 3 }, CancellationToken.None);

        Assert.Equal([3], result.TranscriptNumbers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task ShowAsync_RejectsAConversationOutsideTheResults(int conversation)
    {
        EnqueueRunWithCases(3);

        var error = await Assert.ThrowsAsync<UsageException>(() => CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Conversation = conversation }, CancellationToken.None));

        Assert.Contains("between 1 and 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowAsync_SaysSoWhenThereIsNoConversationToPrint()
    {
        EnqueueRunWithCases(0);

        var error = await Assert.ThrowsAsync<UsageException>(() => CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Conversation = 1 }, CancellationToken.None));

        Assert.Contains("holds no conversations", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowAsync_WritesMarkdownForANonJsonOutput()
    {
        EnqueueRunWithCases(1);

        var result = await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Output = "/work/run.md" }, CancellationToken.None);

        Assert.Equal("/work/run.md", result.OutputPath);
        Assert.StartsWith("# Evaluation run run-9", _files.Files["/work/run.md"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowAsync_WritesJsonForAJsonOutput()
    {
        EnqueueRunWithCases(1);

        await CreateQueries().ShowAsync(new EvaluationShowRequest { RunId = "run-9", Output = "/work/run.json" }, CancellationToken.None);

        Assert.Contains("\"runId\"", _files.Files["/work/run.json"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTestSetsAsync_ProjectsEverySet()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-a", "Nightly", 13));

        var result = await CreateQueries().ListTestSetsAsync(new EvaluationTestSetListRequest(), CancellationToken.None);

        Assert.Equal("test-sets-listed", result.Status);
        var row = Assert.Single(result.TestSets);
        Assert.Equal("testset-a", row.TestSetId);
        Assert.Equal(13, row.ConversationCount);
    }

    [Fact]
    public async Task ShowTestSetAsync_RequiresTestSet()
    {
        await Assert.ThrowsAsync<UsageException>(() => CreateQueries().ShowTestSetAsync(new EvaluationTestSetShowRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task ShowTestSetAsync_ReturnsConversations()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        var result = await CreateQueries().ShowTestSetAsync(new EvaluationTestSetShowRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal("test-set-shown", result.Status);
        Assert.Equal(2, result.Conversations.Count);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public async Task ShowTestSetAsync_ReadsTheAgentsTestSetListingOnce()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        await CreateQueries().ShowTestSetAsync(new EvaluationTestSetShowRequest { TestSet = "Nightly" }, CancellationToken.None);

        Assert.Equal(1, _port.ListTestSetsCalls);
        Assert.Equal(1, _port.GetTestSetCalls);
    }

    [Fact]
    public async Task ShowTestSetAsync_FailsWhenTheSetCannotBeRead()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        var error = await Assert.ThrowsAsync<EvaluationException>(() => CreateQueries().ShowTestSetAsync(new EvaluationTestSetShowRequest { TestSet = "Nightly" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
    }

    [Fact]
    public async Task ExportTestSetAsync_FailsRatherThanWriteAnEmptyFileWhenTheSetDisappeared()
    {
        _port.TestSets.Add(EvaluationScenarios.TestSet("testset-7", "Nightly", 2));

        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => CreateQueries().ExportTestSetAsync(new EvaluationTestSetExportRequest { TestSet = "Nightly", Output = "/work/out.csv" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.TestSetNotFound, error.ErrorKind);
        Assert.Empty(_files.Written);
    }

    [Fact]
    public async Task ExportTestSetAsync_RequiresOutput()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => CreateQueries().ExportTestSetAsync(new EvaluationTestSetExportRequest { TestSet = "Nightly" }, CancellationToken.None));

        Assert.Contains("--output is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportTestSetAsync_WritesCsvRoundTrippableByTheReader()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        var result = await CreateQueries().ExportTestSetAsync(new EvaluationTestSetExportRequest { TestSet = "Nightly", Output = "/work/out.csv" }, CancellationToken.None);

        Assert.Equal("test-set-exported", result.Status);
        Assert.Equal("/work/out.csv", result.OutputPath);
        Assert.Equal(2, EvaluationCsvReader.Read(_files.Files["/work/out.csv"], "out.csv").Count);
    }

    [Fact]
    public async Task ExportTestSetAsync_ReadsTheAgentsTestSetListingOnce()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a", "case-b");

        await CreateQueries().ExportTestSetAsync(new EvaluationTestSetExportRequest { TestSet = "Nightly", Output = "/work/out.csv" }, CancellationToken.None);

        Assert.Equal(1, _port.ListTestSetsCalls);
        Assert.Equal(1, _port.GetTestSetCalls);
    }

    [Fact]
    public async Task EveryTestSetVerbNamesItselfWhenResolvingTheAgent()
    {
        var summary = EvaluationScenarios.TestSet("testset-7", "Nightly", 2);
        _port.TestSets.Add(summary);
        _port.TestSetDetails["testset-7"] = EvaluationScenarios.TestSetDetail(summary, "case-a");
        _files.Files["/work/cases.csv"] = "conversationNumber,question,response\n1,Hello,Hi";

        var queries = CreateQueries();
        var writes = Create();

        await queries.ListTestSetsAsync(new EvaluationTestSetListRequest(), CancellationToken.None);
        await queries.ShowTestSetAsync(new EvaluationTestSetShowRequest { TestSet = "Nightly" }, CancellationToken.None);
        await queries.ExportTestSetAsync(new EvaluationTestSetExportRequest { TestSet = "Nightly", Output = "/work/out.csv" }, CancellationToken.None);
        await writes.UpdateAsync(new EvaluationTestSetUpdateRequest { TestSet = "Nightly", FromCsv = "/work/cases.csv" }, CancellationToken.None);
        await writes.DeleteAsync(new EvaluationTestSetDeleteRequest { TestSet = "Nightly", Force = true }, CancellationToken.None);
        await queries.ListAsync(new EvaluationListRequest(), CancellationToken.None);

        Assert.Equal(
            [
                "agent eval testset list",
                "agent eval testset show",
                "agent eval testset export",
                "agent eval testset update",
                "agent eval testset delete",
                "agent eval list",
            ],
            _targets.Verbs);
    }
}
