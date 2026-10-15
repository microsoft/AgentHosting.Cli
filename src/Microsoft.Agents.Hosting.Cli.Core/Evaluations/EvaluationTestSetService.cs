using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>The test-set write verbs: <c>testset create</c>, <c>testset update</c> and <c>testset delete</c>.</summary>
public sealed class EvaluationTestSetService
{
    /// <summary>How long putting back the conversations removed to make room is given, on its own token.</summary>
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromMinutes(2);

    private readonly IEvaluationTargetResolver _targets;
    private readonly EvaluationTestSetLookup _testSets;
    private readonly EvaluationConversationProvider _conversations;
    private readonly IAgentEvaluationPort _evaluations;
    private readonly IEvaluationComponentStore _components;
    private readonly IConfirmationPrompt _confirmation;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the agent being addressed.</param>
    /// <param name="testSets">Resolves a test set from a name or id.</param>
    /// <param name="conversations">Reads or generates the conversations to store.</param>
    /// <param name="evaluations">Writes test sets and their conversations.</param>
    /// <param name="components">Deletes test sets and conversations.</param>
    /// <param name="confirmation">Confirms a destructive delete.</param>
    public EvaluationTestSetService(IEvaluationTargetResolver targets, EvaluationTestSetLookup testSets, EvaluationConversationProvider conversations, IAgentEvaluationPort evaluations, IEvaluationComponentStore components, IConfirmationPrompt confirmation)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _testSets = testSets ?? throw new ArgumentNullException(nameof(testSets));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    /// <summary>Creates a test set and fills it with conversations.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationTestSetResult> CreateAsync(EvaluationTestSetCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("--name is required: it names the test set being created.");
        }

        var target = await _targets.ResolveAsync(request, "agent eval testset create", cancellationToken).ConfigureAwait(false);

        if (await _testSets.FindByNameAsync(target, request.Name, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            throw EvaluationException.TestSetAlreadyExists(existing.DisplayName, existing.ConversationCount);
        }

        var conversations = await _conversations.ResolveAsync(target, request.FromCsv, request.Generate, request.Turns, cancellationToken).ConfigureAwait(false);
        var testSetId = await CreateAsync(target, request.Name.Trim(), conversations, cancellationToken).ConfigureAwait(false);

        return Describe(target, testSetId, request.Name.Trim(), conversations, "test-set-created");
    }

    /// <summary>Adds conversations to an existing test set, optionally replacing the ones it holds.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationTestSetResult> UpdateAsync(EvaluationTestSetUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TestSet))
        {
            throw new UsageException("--testset is required: it names the test set to change, by name or id.");
        }

        var target = await _targets.ResolveAsync(request, "agent eval testset update", cancellationToken).ConfigureAwait(false);
        var summary = await _testSets.RequireAsync(target, request.TestSet, cancellationToken).ConfigureAwait(false);
        var conversations = await _conversations.ResolveAsync(target, request.FromCsv, request.Generate, request.Turns, cancellationToken).ConfigureAwait(false);

        await ReplaceConversationsAsync(target, summary, conversations, request.Replace, cancellationToken).ConfigureAwait(false);

        var detail = await ReadAsync(target, summary.TestSetId, cancellationToken).ConfigureAwait(false);

        return new EvaluationTestSetResult
        {
            Status = "test-set-updated",
            EnvironmentId = target.Environment.EnvironmentId,
            McsAgentId = target.McsAgentId,
            TestSet = EvaluationTestSetRow.From(detail.Summary),
            Conversations = [.. detail.Conversations.Select(EvaluationConversationRow.From)],
        };
    }

    /// <summary>Deletes a test set and the conversations it holds.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationTestSetDeleteResult> DeleteAsync(EvaluationTestSetDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TestSet))
        {
            throw new UsageException("--testset is required: it names the test set to delete, by name or id.");
        }

        var target = await _targets.ResolveAsync(request, "agent eval testset delete", cancellationToken).ConfigureAwait(false);
        var summary = await _testSets.RequireAsync(target, request.TestSet, cancellationToken).ConfigureAwait(false);

        var outcome = await ConfirmationPolicy.ConfirmAsync(
            _confirmation,
            request.Force || request.NonInteractive,
            $"This deletes test set '{summary.DisplayName}' and the {summary.ConversationCount} conversation(s) it holds. Evaluation runs that used it are unaffected.",
            summary.DisplayName,
            cancellationToken).ConfigureAwait(false);

        if (outcome != ConfirmationOutcome.Confirmed)
        {
            return new EvaluationTestSetDeleteResult
            {
                Status = outcome == ConfirmationOutcome.Mismatched ? "name-mismatch" : "cancelled",
                EnvironmentId = target.Environment.EnvironmentId,
                TestSetId = summary.TestSetId,
                DisplayName = summary.DisplayName,
            };
        }

        await _components.DeleteAsync(target.Environment, summary.TestSetId, cancellationToken).ConfigureAwait(false);

        return new EvaluationTestSetDeleteResult
        {
            EnvironmentId = target.Environment.EnvironmentId,
            TestSetId = summary.TestSetId,
            DisplayName = summary.DisplayName,
        };
    }

    /// <summary>
    /// Creates a test set and adds its conversations.
    /// </summary>
    /// <param name="target">The agent the set belongs to.</param>
    /// <param name="name">The name to record.</param>
    /// <param name="conversations">The conversations to store.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<string> CreateAsync(EvaluationTarget target, string name, IReadOnlyList<EvaluationConversation> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(conversations);

        var testSetId = await _evaluations.CreateTestSetAsync(target.Environment, target.McsAgentId, name, cancellationToken).ConfigureAwait(false);

        await AddAsync(target, testSetId, conversations, name, cancellationToken).ConfigureAwait(false);

        return testSetId;
    }

    private async Task<IReadOnlyList<string>> AddAsync(EvaluationTarget target, string testSetId, IReadOnlyList<EvaluationConversation> conversations, string displayName, CancellationToken cancellationToken)
    {
        var added = await _evaluations.AddConversationsAsync(target.Environment, target.McsAgentId, testSetId, conversations, cancellationToken).ConfigureAwait(false);

        return added.Count >= conversations.Count
            ? added
            : throw EvaluationException.ConversationsNotStored(conversations.Count, added.Count, displayName);
    }

    /// <summary>
    /// Adds conversations to a set, removing the ones it already held once the new ones are stored.
    ///
    /// <para>The additions are written first so a refused or interrupted write leaves the set as it
    /// was. Deleting first would empty the set whenever the write that refills it never lands, and
    /// nothing the service keeps could restore it.</para>
    ///
    /// <para>Adding first would also carry the set past the limit it may hold whenever the old and
    /// new conversations together exceed it, and the service would refuse the write. Just enough of
    /// the outgoing conversations are therefore removed beforehand to leave room, and no more; the
    /// rest are removed afterwards as before. A write that fails after room was made puts back what
    /// it removed, so the only set left short is one whose restore also failed, which is reported.</para>
    ///
    /// <para>Only the conversations this call saw before its own write are removed, and the set is
    /// read again afterwards to check that nothing else appeared. A replacement running at the same
    /// time as another one therefore refuses rather than deleting the conversations that one just
    /// added: the service offers no way to make the two writes one, so a conflict it cannot resolve
    /// is reported instead of guessed at.</para>
    /// </summary>
    /// <param name="target">The agent the set belongs to.</param>
    /// <param name="testSet">The set to change.</param>
    /// <param name="conversations">The conversations to add.</param>
    /// <param name="replace">Whether to remove the conversations the set already holds.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task ReplaceConversationsAsync(EvaluationTarget target, EvaluationTestSetSummary testSet, IReadOnlyList<EvaluationConversation> conversations, bool replace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(testSet);
        ArgumentNullException.ThrowIfNull(conversations);

        var stored = await StoredAsync(target, testSet, cancellationToken).ConfigureAwait(false);

        EvaluationConversationLimits.ValidateTotal(replace ? 0 : stored.Count, conversations.Count, testSet.DisplayName);

        if (!replace)
        {
            await AddAsync(target, testSet.TestSetId, conversations, testSet.DisplayName, cancellationToken).ConfigureAwait(false);
            return;
        }

        var freed = stored.Take(Overflow(stored.Count, conversations.Count)).ToArray();
        var added = await FreeThenAddAsync(target, testSet, conversations, freed, cancellationToken).ConfigureAwait(false);
        var now = await StoredAsync(target, testSet, cancellationToken).ConfigureAwait(false);

        if (now.Select(conversation => conversation.ComponentId).Except(stored.Select(conversation => conversation.ComponentId).Concat(added), StringComparer.OrdinalIgnoreCase).Any())
        {
            throw EvaluationException.ReplacementConflicted(testSet.DisplayName, freed.Length);
        }

        foreach (var conversation in stored.Skip(freed.Length))
        {
            await _components.DeleteAsync(target.Environment, conversation.ComponentId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How many of the outgoing conversations must go before the incoming ones will fit.</summary>
    private static int Overflow(int stored, int incoming) =>
        Math.Clamp(stored + incoming - EvaluationConversationLimits.MaximumConversations, 0, stored);

    /// <summary>
    /// Makes room for the replacement and stores it, putting back whatever it removed if it cannot.
    /// </summary>
    private async Task<IReadOnlyList<string>> FreeThenAddAsync(EvaluationTarget target, EvaluationTestSetSummary testSet, IReadOnlyList<EvaluationConversation> conversations, IReadOnlyList<StoredConversation> freed, CancellationToken cancellationToken)
    {
        var removed = new List<StoredConversation>();

        try
        {
            foreach (var conversation in freed)
            {
                await _components.DeleteAsync(target.Environment, conversation.ComponentId, cancellationToken).ConfigureAwait(false);
                removed.Add(conversation);
            }

            var added = await _evaluations.AddConversationsAsync(target.Environment, target.McsAgentId, testSet.TestSetId, conversations, cancellationToken).ConfigureAwait(false);

            if (added.Count >= conversations.Count)
            {
                return added;
            }

            foreach (var componentId in removed.Count > 0 ? added : [])
            {
                await _components.DeleteAsync(target.Environment, componentId, cancellationToken).ConfigureAwait(false);
            }

            throw EvaluationException.ConversationsNotStored(conversations.Count, added.Count, testSet.DisplayName, kept: removed.Count == 0);
        }
        catch (Exception failure) when (removed.Count > 0)
        {
            await RestoreAsync(target, testSet, removed, failure).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Puts back the conversations removed to make room.
    /// </summary>
    private async Task RestoreAsync(EvaluationTarget target, EvaluationTestSetSummary testSet, IReadOnlyList<StoredConversation> removed, Exception failure)
    {
        var restorable = removed.Where(conversation => conversation.Content is not null).Select(conversation => conversation.Content!).ToArray();

        if (restorable.Length < removed.Count)
        {
            throw EvaluationException.ReplacementInterrupted(testSet.DisplayName, removed.Count, failure);
        }

        using var window = new CancellationTokenSource(RestoreWindow);

        int restored;

        try
        {
            restored = (await _evaluations.AddConversationsAsync(target.Environment, target.McsAgentId, testSet.TestSetId, restorable, window.Token).ConfigureAwait(false)).Count;
        }
        catch (Exception)
        {
            throw EvaluationException.ReplacementInterrupted(testSet.DisplayName, removed.Count, failure);
        }

        if (restored < removed.Count)
        {
            throw EvaluationException.ReplacementInterrupted(testSet.DisplayName, removed.Count - restored, failure);
        }
    }

    /// <summary>A conversation the set holds, with the content needed to put it back.</summary>
    private sealed record StoredConversation(string ComponentId, EvaluationConversation? Content);

    private async Task<IReadOnlyList<StoredConversation>> StoredAsync(EvaluationTarget target, EvaluationTestSetSummary testSet, CancellationToken cancellationToken)
    {
        var detail = await _evaluations.GetTestSetAsync(target.Environment, target.McsAgentId, testSet, cancellationToken).ConfigureAwait(false)
            ?? throw EvaluationException.TestSetNotFound(testSet.DisplayName);

        return
        [
            .. detail.ConversationComponentIds
                .Select((id, index) => new StoredConversation(id, index < detail.Conversations.Count ? detail.Conversations[index] : null))
                .Where(conversation => conversation.ComponentId.Length > 0),
        ];
    }

    private async Task<EvaluationTestSetDetail> ReadAsync(EvaluationTarget target, string testSetId, CancellationToken cancellationToken) =>
        await _testSets.FindAsync(target, testSetId, cancellationToken).ConfigureAwait(false) is { } refreshed
            ? await _evaluations.GetTestSetAsync(target.Environment, target.McsAgentId, refreshed, cancellationToken).ConfigureAwait(false)
                ?? throw EvaluationException.TestSetNotFound(testSetId)
            : throw EvaluationException.TestSetNotFound(testSetId);

    private static EvaluationTestSetResult Describe(EvaluationTarget target, string testSetId, string displayName, IReadOnlyList<EvaluationConversation> conversations, string status) => new()
    {
        Status = status,
        EnvironmentId = target.Environment.EnvironmentId,
        McsAgentId = target.McsAgentId,
        TestSet = new EvaluationTestSetRow
        {
            TestSetId = testSetId,
            DisplayName = displayName,
            ConversationCount = conversations.Count,
            EvaluationSetType = EvaluationSetTypes.MultiTurn,
        },
        Conversations = [.. conversations.Select(EvaluationConversationRow.From)],
    };
}
