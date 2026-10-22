using Microsoft.Agents.Hosting.Cli.Core.Evaluations;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Stable, kebab-case error categories emitted by the evaluation commands.</summary>
public static class EvaluationErrorKinds
{
    /// <summary>The named test set does not exist in the environment.</summary>
    public const string TestSetNotFound = "evaluation-test-set-not-found";

    /// <summary>More than one test set carries the requested name.</summary>
    public const string TestSetAmbiguous = "evaluation-test-set-ambiguous";

    /// <summary>A test set with the requested name already exists.</summary>
    public const string TestSetAlreadyExists = "evaluation-test-set-already-exists";

    /// <summary>The requested run does not exist in the environment.</summary>
    public const string RunNotFound = "evaluation-run-not-found";

    /// <summary>The run cannot be deleted while the service is still working on it.</summary>
    public const string RunNotTerminal = "evaluation-run-not-terminal";

    /// <summary>The run has already finished, so there is nothing to stop.</summary>
    public const string RunNotCancellable = "evaluation-run-not-cancellable";

    /// <summary>The conversation file could not be read.</summary>
    public const string ConversationFileInvalid = "evaluation-conversation-file-invalid";

    /// <summary>The conversations exceed a limit the service enforces.</summary>
    public const string ConversationLimitExceeded = "evaluation-conversation-limit-exceeded";

    /// <summary>The run did not reach a terminal state within the wait.</summary>
    public const string RunTimedOut = "evaluation-run-timed-out";

    /// <summary>The service stored fewer conversations than were sent.</summary>
    public const string ConversationsNotStored = "evaluation-conversations-not-stored";

    /// <summary>The service returned no generated conversations.</summary>
    public const string ConversationsNotGenerated = "evaluation-conversations-not-generated";

    /// <summary>The test set holds no conversations to run.</summary>
    public const string TestSetEmpty = "evaluation-test-set-empty";

    /// <summary>Another writer changed a test set while it was being replaced.</summary>
    public const string ReplacementConflicted = "evaluation-replacement-conflicted";

    /// <summary>A start was accepted but the service never named the run it created.</summary>
    public const string RunNotIdentified = "evaluation-run-not-identified";

    /// <summary>A replacement removed conversations to make room and then could not refill the set.</summary>
    public const string ReplacementInterrupted = "evaluation-replacement-interrupted";
}

/// <summary>A failure raised by the <c>msagent agent eval</c> commands.</summary>
public sealed class EvaluationException : AgentHostingException
{
    private EvaluationException(int errorCode, string message, int exitCode, string errorKind, string remediation, Exception? innerException = null)
        : base(message, exitCode, errorKind, remediation, innerException, errorCode)
    {
    }

    /// <summary>No test set carries the requested name or id.</summary>
    /// <param name="selector">The name or id that was looked for.</param>
    public static EvaluationException TestSetNotFound(string selector) => new(
        ErrorCodes.Evaluation.TestSetNotFound,
        $"No evaluation test set matches '{selector}'.",
        ExitCodes.Usage,
        EvaluationErrorKinds.TestSetNotFound,
        "List the available test sets with `msagent agent eval testset list`.");

    /// <summary>Several test sets carry the requested name, so the command cannot choose one.</summary>
    /// <param name="name">The ambiguous name.</param>
    /// <param name="testSetIds">The ids that matched.</param>
    public static EvaluationException TestSetAmbiguous(string name, IReadOnlyList<string> testSetIds) => new(
        ErrorCodes.Evaluation.TestSetAmbiguous,
        $"{testSetIds.Count} evaluation test sets are named '{name}': {string.Join(", ", testSetIds)}.",
        ExitCodes.Usage,
        EvaluationErrorKinds.TestSetAmbiguous,
        "Pass the id instead of the name, and delete the duplicates with `msagent agent eval testset delete`.");

    /// <summary>A test set already carries the requested name.</summary>
    /// <param name="name">The name that is taken.</param>
    /// <param name="conversationCount">How many conversations the existing set holds.</param>
    public static EvaluationException TestSetAlreadyExists(string name, int conversationCount) => new(
        ErrorCodes.Evaluation.TestSetAlreadyExists,
        $"Test set '{name}' already exists ({conversationCount} conversation(s)).",
        ExitCodes.Usage,
        EvaluationErrorKinds.TestSetAlreadyExists,
        $"Run it with `--testset {name}`, or replace its conversations with `--update-testset`.");

    /// <summary>The run does not exist in the environment.</summary>
    /// <param name="runId">The run that was looked for.</param>
    public static EvaluationException RunNotFound(string runId) => new(
        ErrorCodes.Evaluation.RunNotFound,
        $"No evaluation run matches '{runId}'.",
        ExitCodes.Usage,
        EvaluationErrorKinds.RunNotFound,
        "List the available runs with `msagent agent eval list`.");

    /// <summary>The run is still in flight, and the service refuses to delete it.</summary>
    /// <param name="runId">The run that was addressed.</param>
    /// <param name="state">The state the service reported.</param>
    public static EvaluationException RunNotTerminal(string runId, string state) => new(
        ErrorCodes.Evaluation.RunNotTerminal,
        $"Evaluation run '{runId}' is {state} and cannot be deleted while it is still running.",
        ExitCodes.Usage,
        EvaluationErrorKinds.RunNotTerminal,
        $"Stop it first with `msagent agent eval cancel --run-id {runId}`, then delete it.");

    /// <summary>The run has already finished, so stopping it would do nothing.</summary>
    /// <param name="runId">The run that was addressed.</param>
    /// <param name="state">The state the service reported.</param>
    public static EvaluationException RunNotCancellable(string runId, string state) => new(
        ErrorCodes.Evaluation.RunNotCancellable,
        $"Evaluation run '{runId}' is already {state}.",
        ExitCodes.Usage,
        EvaluationErrorKinds.RunNotCancellable,
        "Only a queued or in-progress run can be stopped.");

    /// <summary>The conversation file could not be parsed.</summary>
    /// <param name="path">The file that was read.</param>
    /// <param name="reason">What is wrong with it.</param>
    public static EvaluationException ConversationFileInvalid(string path, string reason) => new(
        ErrorCodes.Evaluation.ConversationFileInvalid,
        $"The conversation file '{path}' could not be read: {reason}",
        ExitCodes.Usage,
        EvaluationErrorKinds.ConversationFileInvalid,
        "Start from the Copilot Studio CSV template, which uses the columns conversationNumber, question and response.");

    /// <summary>The conversations exceed a limit the evaluation service enforces.</summary>
    /// <param name="reason">Which limit was exceeded.</param>
    public static EvaluationException ConversationLimitExceeded(string reason) => new(
        ErrorCodes.Evaluation.ConversationLimitExceeded,
        reason,
        ExitCodes.Usage,
        EvaluationErrorKinds.ConversationLimitExceeded,
        "Split the conversations across several test sets, or shorten them.");

    /// <summary>The run did not finish within the time the command waited.</summary>
    /// <param name="runId">The run that was polled.</param>
    /// <param name="waited">How long polling ran.</param>
    public static EvaluationException RunTimedOut(string runId, TimeSpan waited) => new(
        ErrorCodes.Evaluation.RunTimedOut,
        $"Evaluation run '{runId}' had not finished after {waited.TotalMinutes:0} minute(s).",
        ExitCodes.Failure,
        EvaluationErrorKinds.RunTimedOut,
        $"The run is still going. Check it later with `msagent agent eval show --run-id {runId}`.");

    /// <summary>The test set holds nothing to evaluate.</summary>
    /// <param name="name">The empty test set.</param>
    public static EvaluationException TestSetEmpty(string name) => new(
        ErrorCodes.Evaluation.TestSetEmpty,
        $"Test set '{name}' holds no conversations.",
        ExitCodes.Usage,
        EvaluationErrorKinds.TestSetEmpty,
        "Add conversations with `msagent agent eval testset update --from-csv`.");

    /// <summary>The generator returned nothing, so there is no conversation to store or run.</summary>
    public static EvaluationException ConversationsNotGenerated() => new(
        ErrorCodes.Evaluation.ConversationsNotGenerated,
        "Copilot Studio generated no conversations, so nothing was changed.",
        ExitCodes.Failure,
        EvaluationErrorKinds.ConversationsNotGenerated,
        "Try again, or supply the conversations yourself with `--from-csv`.");

    /// <summary>The service acknowledged fewer conversations than were sent.</summary>
    /// <param name="sent">How many conversations were sent.</param>
    /// <param name="stored">How many the service acknowledged.</param>
    /// <param name="name">The test set that was written to.</param>
    /// <param name="kept">Whether the conversations the service did store were left in the set.</param>
    public static EvaluationException ConversationsNotStored(int sent, int stored, string name, bool kept = true) => new(
        ErrorCodes.Evaluation.ConversationsNotStored,
        kept
            ? $"Copilot Studio stored {stored} of the {sent} conversation(s) sent, so test set '{name}' now holds part of what was asked for."
            : $"Copilot Studio stored {stored} of the {sent} conversation(s) sent. The part that landed was removed again and test set '{name}' was put back as it was.",
        ExitCodes.Failure,
        EvaluationErrorKinds.ConversationsNotStored,
        kept
            ? $"Read it with `msagent agent eval testset show --testset {name}` to see what landed, then remove or re-send what is missing. Do not evaluate it until it holds what you expect: a run is charged for whatever it finds."
            : $"Nothing was changed. Send the conversations again, and read the set with `msagent agent eval testset show --testset {name}` if you want to confirm it.");

    /// <summary>Conversations this command did not add appeared while it was replacing a set.</summary>
    /// <param name="name">The test set being replaced.</param>
    /// <param name="removedToMakeRoom">How many conversations were removed before the write, to stay within the limit.</param>
    public static EvaluationException ReplacementConflicted(string name, int removedToMakeRoom) => new(
        ErrorCodes.Evaluation.ReplacementConflicted,
        removedToMakeRoom == 0
            ? $"Test set '{name}' was changed by something else while it was being replaced, so the conversations it already held were kept."
            : $"Test set '{name}' was changed by something else while it was being replaced. {removedToMakeRoom} conversation(s) had been removed to stay within the {EvaluationConversationLimits.MaximumConversations}-conversation limit and were not put back.",
        ExitCodes.Failure,
        EvaluationErrorKinds.ReplacementConflicted,
        "Read the set with `msagent agent eval testset show` to see what it holds now, then replace it again.");

    /// <summary>The service accepted a run but never named it, so nothing can address it.</summary>
    /// <param name="name">The name the run was started under.</param>
    public static EvaluationException RunNotIdentified(string name) => new(
        ErrorCodes.Evaluation.RunNotIdentified,
        $"Copilot Studio accepted the run '{name}' but did not say which run it created, so nothing can address it.",
        ExitCodes.Failure,
        EvaluationErrorKinds.RunNotIdentified,
        $"A charged run may be in flight. List the agent's runs with `msagent agent eval list` and stop any unexpected '{name}' with `msagent agent eval cancel --run-id <id>`.");

    /// <summary>Room was made for a replacement that then failed, leaving the set short.</summary>
    /// <param name="name">The test set that was being replaced.</param>
    /// <param name="missing">How many conversations the set is short by.</param>
    /// <param name="innerException">The failure that stopped the replacement.</param>
    public static EvaluationException ReplacementInterrupted(string name, int missing, Exception innerException) => new(
        ErrorCodes.Evaluation.ReplacementInterrupted,
        $"Test set '{name}' had conversations removed to make room for a replacement that then failed, and {missing} of them could not be put back.",
        ExitCodes.Failure,
        EvaluationErrorKinds.ReplacementInterrupted,
        $"Read the set with `msagent agent eval testset show --testset {name}` to see what it holds now, then re-send the conversations it should hold. Do not evaluate it until it does: a run is charged for whatever it finds.",
        innerException);
}
