using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Maps every Agent Management Service MCS error code, from a rejected request or a failed operation, to a typed CLI failure.</summary>
public static class AmsMcsOperationErrorMap
{
    /// <summary>The audience label used in fallback messages.</summary>
    public const string AudienceLabel = "Agent Management Service";

    private const string OperationFailedCode = "McsOperationFailed";

    private const string OnBehalfOfUnavailableCode = "McsOnBehalfOfUnavailable";

    private const string CaptureConflictCode = "McsCaptureConflict";

    private const string DevEditsNotInCommitCode = "McsDevEditsNotInCommit";

    private const string SourceConflictCode = "McsSourceConflict";

    private const string SourceValidationFailedCode = "McsSourceValidationFailed";

    private const string BotClaimedCode = "McsBotClaimed";

    private const string InvalidFieldValueCode = "InvalidAgentFieldValue";

    private const string MissingFieldCode = "MissingAgentField";

    private const string CaptureConflictDetailCode = "CaptureConflict";

    private const string CaptureKind = "Capture";

    private const string DeployKind = "Deploy";

    private const string CaptureOutcome = "Nothing was captured";

    private const string DeployOutcome = "Nothing was deployed";

    private const string DeployOrCaptureOutcome = "Nothing was deployed or captured";

    private const string WorkflowConflictReason = "the workflow changed in a commit after the one Dev runs, and a workflow capture doesn't merge";

    private const string NewerCommitPlaceholder = "<newer-commit>";

    private const string RequestedCommitPlaceholder = "<requested-commit>";

    private const string CapturedCommitPlaceholder = "<captured-commit>";

    private const string DevEditsNotInCommitRemediation = "Run `git pull`, then deploy the saved commit or a later one, or run the command again with --overwrite.";

    private static readonly string CaptureConflictRemediation = string.Join(Environment.NewLine, "Capture again with `--resolve cloud`; the other changes of both sides merge as usual.", $"Deploy the latest commit to Dev with `{DeploymentCommandHints.RemoteDeploy("--deployment-name <dev-deployment>")} --overwrite`, which first saves Dev's edits on its snapshot branch.");

    private static readonly Entry FieldRejected = new(ErrorCodes.AmsAgent.RequestRejected, ExitCodes.Usage, "ams-request-rejected", "The Agent Management Service refused a value msagent sent.", "Correct the value the service detail names, such as the agent folder, then run the command again.", true);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["McsCommitNotAllowedForStage"] = new(ErrorCodes.AmsAgent.CommitNotAllowedForStage, ExitCodes.Usage, "mcs-commit-not-allowed-for-stage", "Test takes only a commit Dev deployed, and this commit never was.", $"Deploy the commit to Dev first with `{DeploymentCommandHints.RemoteDeploy("--deployment-name <dev-deployment>", "<commit>")}`, or drop --commit-id to take Dev's last deploy.", true),
        ["McsCommitNotAccepted"] = new(ErrorCodes.AmsAgent.CommitNotAccepted, ExitCodes.Usage, "mcs-commit-not-accepted", "A Prod deploy takes the commit Test deployed last, not --commit-id.", "Drop --commit-id.", false),
        ["McsDeploymentStageEnvironmentInvalid"] = new(ErrorCodes.AmsAgent.DeploymentStageEnvironmentInvalid, ExitCodes.Usage, "mcs-deployment-stage-environment-invalid", "The deployment's stage does not fit its environment: an agent has one Dev deployment in its home environment, and each Test or Prod deployment targets another environment in the same tenant, cloud and geography.", "Choose another --environment-id or --deployment-type.", false),
        ["McsSourceDeploymentNotReady"] = new(ErrorCodes.AmsAgent.SourceDeploymentNotReady, ExitCodes.Failure, "mcs-source-deployment-not-ready", "Nothing was deployed to the preceding stage yet, so there's no commit to promote.", $"Deploy to the preceding stage first with `{DeploymentCommandHints.RemoteDeploy("--deployment-name <preceding-deployment>")}`.", false),
        ["McsDeploymentOperationInProgress"] = new(ErrorCodes.AmsAgent.DeploymentOperationInProgress, ExitCodes.Failure, "mcs-deployment-operation-in-progress", "Another operation is already running on this deployment.", "Wait for that operation to finish, then run the command again.", false),
        ["McsOperationCapacityExceeded"] = new(ErrorCodes.AmsAgent.OperationCapacityExceeded, ExitCodes.Failure, "mcs-operation-capacity-exceeded", "The Agent Management Service has no free worker for this operation right now.", "Wait a minute, then run the same command again.", false),
        [SourceConflictCode] = new(ErrorCodes.AmsAgent.SourceConflict, ExitCodes.Usage, "mcs-source-conflict", "The agent's source kept changing in the repository while the operation ran.", "Run the command again; it takes the newest commit.", true),
        [CaptureConflictCode] = new(ErrorCodes.AmsAgent.CaptureConflict, ExitCodes.Usage, "mcs-capture-conflict", ConflictMessage(CaptureOutcome, null, null), CaptureConflictRemediation, false),
        [DevEditsNotInCommitCode] = new(ErrorCodes.AmsAgent.DevEditsNotInCommit, ExitCodes.Usage, "mcs-dev-edits-not-in-commit", DevEditsNotInCommitMessage(null, null), DevEditsNotInCommitRemediation, false),
        ["McsCaptureBaseUnknown"] = new(ErrorCodes.AmsAgent.CaptureBaseUnknown, ExitCodes.Failure, "mcs-capture-base-unknown", "AMS doesn't know which commit Dev holds.", "Run `msagent agent deploy` to Dev from your clone, which records the commit Dev holds, then run the command again; a remote deploy with --overwrite replaces Dev's content instead.", true),
        ["McsRepositoryNotWritable"] = new(ErrorCodes.AmsAgent.RepositoryNotWritable, ExitCodes.Failure, "mcs-repository-not-writable", "You can't write to the repository.", "The repository's owner grants you write access in GRS; then run the command again.", false),
        ["McsRepositoryNotFound"] = new(ErrorCodes.AmsAgent.RepositoryNotFound, ExitCodes.Usage, "mcs-repository-not-found", "The repository doesn't exist in the home environment, or you can't read it.", "Check --repository-id; the repository's owner shares it in GRS.", false),
        ["McsRepositoryNotSupported"] = new(ErrorCodes.AmsAgent.RepositoryNotSupported, ExitCodes.Usage, "mcs-repository-not-supported", "The repository isn't an active repository of the home environment that can hold MCS agents: it may belong to another environment.", "Select the repository's environment, or use another repository.", false),
        ["McsAgentFolderBound"] = new(ErrorCodes.AmsAgent.AgentFolderBound, ExitCodes.Usage, "mcs-agent-folder-bound", "Another AMS agent already holds this folder of the repository.", "Choose another name.", true),
        [BotClaimedCode] = new(ErrorCodes.AmsAgent.BotClaimed, ExitCodes.Usage, "mcs-bot-claimed", "Another AMS agent already holds this Copilot Studio agent or flow.", "Clone that agent's repository and run `msagent agent init` there.", true),
        ["McsBotNotFound"] = new(ErrorCodes.AmsAgent.BotNotFound, ExitCodes.Usage, "mcs-bot-not-found", "The Copilot Studio agent or flow doesn't exist in the environment, or you can't read it.", "Check the agent or flow and its environment, then run the command again.", true),
        ["McsDeploymentIdConflict"] = new(ErrorCodes.AmsAgent.DeploymentIdConflict, ExitCodes.Failure, "mcs-deployment-id-conflict", "AMS already holds this deployment id with other details.", "Check the deployment's record in .config with `msagent deployment show`, then run the command again with the same inputs.", true),
        ["McsAgentIdConflict"] = new(ErrorCodes.AmsAgent.AgentIdConflict, ExitCodes.Failure, "mcs-agent-id-conflict", "AMS already holds this agent id with other details.", "Check the agent's record in .config, then run the command again with the same inputs.", true),
        [SourceValidationFailedCode] = new(ErrorCodes.AmsAgent.SourceValidationFailed, ExitCodes.Usage, "mcs-source-validation-failed", "The Agent Management Service rejected the agent source.", "Fix the reported problems in the agent or collection folders, commit and push, then run the command again.", true),
        ["McsSourceTooLarge"] = new(ErrorCodes.AmsAgent.SourceTooLarge, ExitCodes.Usage, "mcs-source-too-large", "The agent's content exceeds the size the repository accepts for a commit, so nothing was committed.", "Remove large or unneeded files from the agent and its collection folders, then run the command again.", false),
        ["McsSourceCheckFailed"] = new(ErrorCodes.AmsAgent.SourceCheckFailed, ExitCodes.Usage, "mcs-source-check-failed", "The repository checks rejected the agent source, for example because it holds a secret or an oversized file.", "Remove the reported content, then run the command again.", true),
        [OnBehalfOfUnavailableCode] = new(ErrorCodes.AmsAgent.OnBehalfOfUnavailable, ExitCodes.Failure, "mcs-on-behalf-of-unavailable", "The Agent Management Service could not act on your behalf: the request reached it without a gateway ticket, or the token exchange failed.", "Run `msagent auth login` and retry. If it persists, the request is not reaching the service through the Power Platform API gateway.", false),
        [OperationFailedCode] = new(ErrorCodes.AmsAgent.OperationFailed, ExitCodes.Failure, "mcs-operation-failed", "The operation failed in Copilot Studio.", "Fix the reported problem, then retry.", true),
        ["McsCaptureAgentNotInCloud"] = new(ErrorCodes.AmsAgent.CaptureAgentNotInCloud, ExitCodes.Failure, "mcs-capture-agent-not-in-cloud", "The Dev deployment has no Copilot Studio agent or workflow yet.", $"Deploy to it first with `{DeploymentCommandHints.AgentDeploy("--agent-id <agent-id>", "--deployment-name <dev-deployment>")}`, then capture.", false),
        [InvalidFieldValueCode] = FieldRejected,
        [MissingFieldCode] = FieldRejected,
    };

    private static readonly Specialization CallerTokenExpiresTooSoon = new(OnBehalfOfUnavailableCode, "CallerTokenExpiresTooSoon", Entries[OnBehalfOfUnavailableCode] with { Message = "The Agent Management Service could not act on your behalf, because the sign-in token sent with the request expires too soon for the operation.", Remediation = "Your sign-in token could not be renewed with enough lifetime for the operation. Run `msagent auth login` and retry.", IncludesServiceDetail = true });

    private static readonly Entry CaptureKeptMoving = Entries[SourceConflictCode] with { Message = $"{CaptureOutcome}: new commits kept arriving while the capture merged Dev's edits.", Remediation = "Capture again; it merges with the newest commit.", IncludesServiceDetail = false };

    private static readonly Entry WorkflowCaptureConflict = Entries[SourceConflictCode] with { Message = $"{CaptureOutcome}: {WorkflowConflictReason}.", Remediation = "Deploy the latest commit to Dev, which replaces Dev's edits, or make the same edits in your clone, commit and push them.", IncludesServiceDetail = false };

    private static readonly Entry DeployKeptMoving = Entries[SourceConflictCode] with { Message = $"{DeployOutcome}: new commits kept arriving while the deploy captured Dev's edits.", Remediation = "Deploy again; the capture merges with the newest commit.", IncludesServiceDetail = false };

    private static readonly Entry WorkflowDeployConflict = Entries[SourceConflictCode] with { Message = $"{DeployOutcome}: {WorkflowConflictReason}, so Dev's edits weren't saved.", Remediation = "To keep Dev's edits, make them in your clone, commit and push them. Then run the command again with --overwrite, which replaces Dev's edits with the commit.", IncludesServiceDetail = false };

    private static readonly Entry CommitNotFound = new(ErrorCodes.AmsAgent.CommitNotFound, ExitCodes.Usage, "commit-not-found", "The commit isn't in the repository, or doesn't hold the agent's folder.", "Push the commit, or pass another --commit-id.", true);

    private static readonly Specialization[] Specializations =
    [
        new(SourceValidationFailedCode, "CommitNotFound", CommitNotFound),
        new(SourceValidationFailedCode, "PathNotFound", CommitNotFound),
        new(BotClaimedCode, "RepositoryId", Entries[BotClaimedCode] with { Remediation = "Clone that agent's repository and run `msagent agent init` there. AMS kept the repository it created for this agent, which the service detail names: pass it as --repository-id to use it." }),
        new(OperationFailedCode, "SchemaNameMissing", Entries[OperationFailedCode] with { Remediation = "Add the agent's schema name to its settings, commit and push, then run the command again." }),
        CallerTokenExpiresTooSoon,
    ];

    /// <summary>Maps a non-success response, falling back to the shared status-driven mapping for codes outside the MCS contract.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="bodyText">The raw response body.</param>
    /// <param name="method">The request method, for the fallback message.</param>
    /// <param name="url">The request url, for the fallback message.</param>
    /// <param name="correlation">The request's correlation identifiers.</param>
    /// <param name="retryAfter">The wait the service asked for, when it asked for one.</param>
    public static AgentHostingException Map(int status, string? bodyText, string method, Uri url, ServiceCorrelation correlation, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(correlation);

        var error = AmsMcsWire.ReadErrorBody(bodyText);
        if (MapKnown(error, correlation, retryAfter) is { } mapped)
        {
            return mapped;
        }

        if (string.Equals(error?.Code?.Trim(), "MissingUserIdentity", StringComparison.OrdinalIgnoreCase))
        {
            return AmsAgentException.Rejected(ErrorCodes.Authentication.NotSignedIn, Annotated($"The {AudienceLabel} did not receive a user identity for this request.", correlation), ExitCodes.NotSignedIn, "ams-missing-user-identity", "Run `msagent auth login` and retry.");
        }

        var fallback = TargetRequestException.MapTargetResponseError(status, bodyText, method, url.ToString(), AudienceLabel, "msagent");
        return new TargetRequestException(Annotated(fallback.Message, correlation), fallback.ExitCode, fallback.Kind, fallback.Status);
    }

    /// <summary>Maps the failure a finished operation recorded.</summary>
    /// <param name="operation">The failed operation.</param>
    /// <param name="requestedCommitSha">The commit a deploy was asked to deploy, which a refusal of Dev's edits names, or null for any other operation.</param>
    /// <param name="agentType">The agent's type, which words a capture's or a deploy's refusal for a standalone workflow, or null when the command did not read it.</param>
    public static AmsAgentException OperationFailed(AmsMcsOperationRecord operation, string? requestedCommitSha = null, string? agentType = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Create(OperationEntry(operation, requestedCommitSha, agentType), operation.Error, $"(operation {operation.OperationId}).", null);
    }

    /// <summary>What to do about the failure a finished operation recorded, as the command that waited for it advises.</summary>
    /// <param name="operation">The failed operation.</param>
    /// <param name="agentType">The agent's type, which words a capture's or a deploy's refusal for a standalone workflow, or null when it is not known.</param>
    public static string OperationRemediation(AmsMcsOperationRecord operation, string? agentType = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return OperationEntry(operation, null, agentType).Remediation;
    }

    internal static string? ServiceDetailSentence(AmsMcsOperationError? error) => TerminalText.ServiceDetail(ServiceDetail(error)) is { Length: > 0 } detail ? $"Service detail: {detail}" : null;

    internal static bool RefusesTokenLifetime(string? bodyText) => AmsMcsWire.ReadErrorBody(bodyText) is { Code: { } code } error && CallerTokenExpiresTooSoon.Matches(code.Trim(), error.Details);

    internal static string Annotated(string sentence, ServiceCorrelation correlation) => correlation.Describe().Length > 0 ? correlation.Annotate(sentence.TrimEnd('.')) : sentence;

    private static AmsAgentException? MapKnown(AmsMcsOperationError? error, ServiceCorrelation correlation, TimeSpan? retryAfter) => Find(error) is { } entry ? Create(Worded(entry, error, null, null, null) ?? entry, error, correlation.Annotate(string.Empty).Trim(), retryAfter) : null;

    private static Entry OperationEntry(AmsMcsOperationRecord operation, string? requestedCommitSha, string? agentType)
    {
        if (Find(operation.Error) is { } entry)
        {
            return Worded(entry, operation.Error, operation, requestedCommitSha, agentType) ?? entry with { IncludesServiceDetail = true };
        }

        return operation.Error?.Code?.Trim() is { Length: > 0 } code ? Entries[OperationFailedCode] with { Message = $"The {operation.Kind} operation failed with {TerminalText.Sanitize(code)}." } : Entries[OperationFailedCode];
    }

    private static Entry? Worded(Entry entry, AmsMcsOperationError? error, AmsMcsOperationRecord? operation, string? requestedCommitSha, string? agentType) => entry.ErrorCode switch
    {
        ErrorCodes.AmsAgent.CaptureConflict when IsKind(operation, DeployKind) => entry with { Message = ConflictMessage(DeployOrCaptureOutcome, error, operation!.MergedCommitSha), Remediation = DeployConflictRemediation(requestedCommitSha ?? operation.SourceCommitSha) },
        ErrorCodes.AmsAgent.CaptureConflict => entry with { Message = ConflictMessage(CaptureOutcome, error, operation?.MergedCommitSha) },
        ErrorCodes.AmsAgent.DevEditsNotInCommit => entry with { Message = DevEditsNotInCommitMessage(requestedCommitSha ?? operation?.SourceCommitSha, operation?.CapturedCommitSha) },
        ErrorCodes.AmsAgent.SourceConflict when IsKind(operation, CaptureKind) => AgentTypes.IsMcsWorkflow(agentType) ? WorkflowCaptureConflict : CaptureKeptMoving,
        ErrorCodes.AmsAgent.SourceConflict when IsKind(operation, DeployKind) => AgentTypes.IsMcsWorkflow(agentType) ? WorkflowDeployConflict : DeployKeptMoving,
        _ => null,
    };

    private static bool IsKind(AmsMcsOperationRecord? operation, string kind) => string.Equals(operation?.Kind, kind, StringComparison.OrdinalIgnoreCase);

    private static string ConflictMessage(string outcome, AmsMcsOperationError? error, string? commitSha) => $"{outcome}: Dev's edits and commit {commitSha ?? NewerCommitPlaceholder} changed the same lines{(ConflictingPaths(error) is { Length: > 0 } paths ? $" in {paths}" : string.Empty)}.";

    private static string ConflictingPaths(AmsMcsOperationError? error) => TerminalText.ServiceDetail(string.Join(", ", error?.Details.Where(detail => string.Equals(detail.Code?.Trim(), CaptureConflictDetailCode, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(detail.Message)).Select(detail => detail.Message) ?? []));

    private static string DeployConflictRemediation(string? requestedCommitSha) => string.Join(Environment.NewLine, "Capture with `--resolve cloud`, then deploy again; the other changes of both sides merge as usual.", $"Deploy commit {requestedCommitSha ?? RequestedCommitPlaceholder} with --overwrite, which first saves Dev's edits on its snapshot branch.");

    private static string DevEditsNotInCommitMessage(string? requestedCommitSha, string? capturedCommitSha) => $"{DeployOutcome}: Dev had edits that commit {requestedCommitSha ?? RequestedCommitPlaceholder} doesn't hold. They were saved on main as commit {capturedCommitSha ?? CapturedCommitPlaceholder}.";

    private static Entry? Find(AmsMcsOperationError? error)
    {
        var code = error?.Code?.Trim();
        if (error is null || string.IsNullOrEmpty(code))
        {
            return null;
        }

        return Specializations.FirstOrDefault(specialization => specialization.Matches(code, error.Details))?.Entry ?? Entries.GetValueOrDefault(code);
    }

    private static string ServiceDetail(AmsMcsOperationError? error) => string.Join("; ", new[] { error?.Message }.Concat(error?.Details.Select(McsAmsViews.DetailText) ?? []).Where(part => !string.IsNullOrWhiteSpace(part)));

    private static AmsAgentException Create(Entry entry, AmsMcsOperationError? error, string suffix, TimeSpan? retryAfter) => AmsAgentException.Rejected(entry.ErrorCode, Sentences(entry.Message, entry.IncludesServiceDetail ? ServiceDetailSentence(error) : null, suffix), entry.ExitCode, entry.ErrorKind, entry.Remediation, retryAfter, entry.Remediation.Contains(Environment.NewLine, StringComparison.Ordinal));

    private static string Sentences(params string?[] parts) => string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private sealed record Entry(int ErrorCode, int ExitCode, string ErrorKind, string Message, string Remediation, bool IncludesServiceDetail);

    private sealed record Specialization(string Code, string DetailCode, Entry Entry)
    {
        public bool Matches(string code, IReadOnlyList<AmsMcsOperationErrorDetail> details) => string.Equals(Code, code, StringComparison.OrdinalIgnoreCase) && details.Any(detail => string.Equals(DetailCode, detail.Code?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
