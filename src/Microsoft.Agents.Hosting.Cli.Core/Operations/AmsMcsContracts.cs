using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Input for <c>msagent agent deploy --remote</c>, which deploys a commit from GRS to one of the project's deployments through AMS.</summary>
public sealed record McsAmsAgentDeployRequest
{
    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }

    /// <summary>The agent to deploy.</summary>
    public string? AgentId { get; init; }

    /// <summary>The agent to deploy, by name, used when no id was given.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment to deploy to.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment to deploy to, by name, used when no id was given.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The commit to deploy, full or short, or null for the commit AMS takes for the stage.</summary>
    public string? CommitId { get; init; }

    /// <summary>Whether to publish a Dev deploy, which a Test or Prod deploy does anyway.</summary>
    public bool Publish { get; init; }

    /// <summary>Whether a Dev deploy replaces Dev's edits after saving them, which a Test or Prod deploy does anyway.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Whether to return once AMS accepted the deploy.</summary>
    public bool Async { get; init; }
}

/// <summary>A deploy of a commit from GRS that AMS ran or accepted.</summary>
public sealed record McsAmsAgentDeployResult : ITextGridRenderable
{
    /// <summary>Schema version of this result.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>deployed once the operation succeeded, or deploying when the command did not wait.</summary>
    public required string Status { get; init; }

    /// <summary>The deployed agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The agent's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>MCSAgent or MCSWorkflow.</summary>
    public required string AgentType { get; init; }

    /// <summary>The deployment.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deployment's stage: dev, test or prod.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The environment the deployment targets.</summary>
    public required string TargetEnvironmentId { get; init; }

    /// <summary>The commit the operation deploys, or null while AMS has not named it.</summary>
    public string? CommitSha { get; init; }

    /// <summary>The deploy operation.</summary>
    public required string OperationId { get; init; }

    /// <summary>Whether the finished deploy published the agent, or null when the command did not wait or the deployment serves a standalone workflow, which activates instead.</summary>
    public bool? Published { get; init; }

    /// <summary>When the agent was last published, by this deploy or before it, or null when it never was or the service does not say.</summary>
    public DateTimeOffset? LastPublishedTime { get; init; }

    /// <summary>The commit on main that saved Dev's edits before the deploy, or null when the deploy saved none.</summary>
    public string? CapturedCommitSha { get; init; }

    /// <summary>Whether the deploy connected or refreshed the agent's folder, so that <c>msagent agent pull</c> reads the Copilot Studio agent of the Dev deployment.</summary>
    public bool WorkspaceConnected { get; init; }

    /// <summary>Why a Dev deploy left the agent's folder's connection as it was, because the folder doesn't hold exactly the deployed commit, or null.</summary>
    public string? WorkspaceNotice { get; init; }

    /// <summary>Customer-safe notices the operation reported, such as flows left in draft or other agents using a pushed collection.</summary>
    public IReadOnlyList<McsAmsNoticeView> Notices { get; init; } = [];

    /// <summary>Advisories that did not stop the deploy.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        Status == "deployed" ? $"Deployed {DisplayName} to {DeploymentName} ({DeploymentType}){(CommitSha is null ? string.Empty : $" from commit {McsAmsCommits.Short(CommitSha)}")}." : $"Deploy of {DisplayName} to {DeploymentName} ({DeploymentType}) accepted; it keeps running in AMS.",
        $"  commit       {TextGrid.DescribeOrDash(CommitSha)}",
        $"  operation    {OperationId}",
        $"  environment  {TargetEnvironmentId}",
        .. Published is { } published ? new[] { $"  published    {TextGrid.DescribePublished(published, LastPublishedTime)}" } : [],
        .. McsAmsNoticeView.Lines(Notices),
        .. Warnings.Select(warning => TextGrid.WarningLine("  ", warning)),
        .. Status == "deployed" ? [] : new[] { AmsAgentException.OperationShowHint(AgentId, OperationId) },
    ];
}

/// <summary>Input for <c>msagent agent operation show</c>.</summary>
public sealed record McsAmsOperationShowRequest
{
    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }

    /// <summary>The agent the operation belongs to.</summary>
    public string? AgentId { get; init; }

    /// <summary>The agent, by name, used when no id was given.</summary>
    public string? AgentName { get; init; }

    /// <summary>The agent's home environment, which selects the agent by --agent-id alone, outside its project.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The operation.</summary>
    public string? OperationId { get; init; }
}

/// <summary>One MCS operation.</summary>
public sealed record McsAmsOperationShowResult : ITextGridRenderable
{
    /// <summary>Schema version of this result.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The operation id.</summary>
    public required string OperationId { get; init; }

    /// <summary>Deploy or Capture.</summary>
    public required string Kind { get; init; }

    /// <summary>Accepted, Running, Succeeded or Failed.</summary>
    public required string Status { get; init; }

    /// <summary>The agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment the operation acts on.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The commit the operation reads.</summary>
    public string? SourceCommitSha { get; init; }

    /// <summary>The commit the operation produced.</summary>
    public string? ResultCommitSha { get; init; }

    /// <summary>The commit that saved Dev's edits before a deploy replaced them.</summary>
    public string? CapturedCommitSha { get; init; }

    /// <summary>The newer commit a capture merged Dev's edits with.</summary>
    public string? MergedCommitSha { get; init; }

    /// <summary>Who started the operation.</summary>
    public string? StartedBy { get; init; }

    /// <summary>When the operation started.</summary>
    public DateTimeOffset? StartedTime { get; init; }

    /// <summary>When the operation last reported progress.</summary>
    public DateTimeOffset? LastHeartbeatTime { get; init; }

    /// <summary>When the operation finished.</summary>
    public DateTimeOffset? CompletedTime { get; init; }

    /// <summary>The recorded failure code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The recorded customer-safe failure message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The recorded customer-safe failure details, each as code and message.</summary>
    public IReadOnlyList<string> ErrorDetails { get; init; } = [];

    /// <summary>Customer-safe notices.</summary>
    public IReadOnlyList<McsAmsNoticeView> Notices { get; init; } = [];

    /// <summary>What to do about a failed operation, as the command that waited for it advises.</summary>
    public string? Remediation { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Operation {OperationId} ({Kind}): {Status}",
        $"  agent          {AgentId}",
        $"  deployment     {TextGrid.DescribeOrDash(DeploymentId)}",
        $"  source commit  {TextGrid.DescribeOrDash(SourceCommitSha)}",
        $"  result commit  {TextGrid.DescribeOrDash(ResultCommitSha)}",
        .. CapturedCommitSha is null ? [] : new[] { $"  captured       {CapturedCommitSha}" },
        .. MergedCommitSha is null ? [] : new[] { $"  merged with    {MergedCommitSha}" },
        $"  started by     {TextGrid.DescribeOrDash(StartedBy)}",
        $"  started        {TextGrid.DescribeTimestamp(StartedTime)}",
        $"  heartbeat      {TextGrid.DescribeTimestamp(LastHeartbeatTime)}",
        $"  completed      {TextGrid.DescribeTimestamp(CompletedTime)}",
        .. ErrorCode is null && ErrorMessage is null ? [] : new[] { $"  error          {string.Join(": ", new[] { ErrorCode, ErrorMessage }.Where(part => !string.IsNullOrWhiteSpace(part)))}" },
        .. ErrorDetails.Select(detail => $"                 {detail}"),
        .. Remediation is null ? [] : Remediation.Split(Environment.NewLine).Select((line, index) => index == 0 ? $"  remediation    {line}" : $"                 {line}"),
        .. Notices.Select(notice => $"  warning        {notice.Message}"),
    ];
}

/// <summary>A customer-safe operation notice, with the text the CLI shows for it.</summary>
public sealed record McsAmsNoticeView
{
    /// <summary>FlowLeftInDraft, CollectionSharedWithOtherBots, PushWarning, or a kind this build does not know.</summary>
    public required string Kind { get; init; }

    /// <summary>The flow or collection the notice is about, or the subject of a push warning's finding.</summary>
    public string? Target { get; init; }

    /// <summary>The unbound connection references, or the other agents that use the collection.</summary>
    public IReadOnlyList<string> RelatedItems { get; init; } = [];

    /// <summary>The code of a push warning's finding, such as PromptRefused.</summary>
    public string? Code { get; init; }

    /// <summary>The service's reason for a push warning's finding, already redacted.</summary>
    public string? Detail { get; init; }

    /// <summary>The notice as the CLI words it.</summary>
    public required string Message { get; init; }

    /// <summary>The warning lines a list of notices renders as.</summary>
    /// <param name="notices">The notices.</param>
    public static IEnumerable<string> Lines(IEnumerable<McsAmsNoticeView> notices) => notices.Select(notice => TextGrid.WarningLine("  ", notice.Message));
}

/// <summary>Input for <c>msagent agent list --view ams</c>.</summary>
public sealed record McsAmsAgentListRequest
{
    /// <summary>The listing options, which the cloud view shares.</summary>
    public required ManagedAgentListRequest Options { get; init; }
}

/// <summary>Input for <c>msagent agent capture</c>, which commits the Copilot Studio content of the agent's Dev deployment to GRS.</summary>
public sealed record McsAmsAgentCaptureRequest
{
    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }

    /// <summary>The agent whose Copilot Studio content is captured.</summary>
    public string? AgentId { get; init; }

    /// <summary>The agent, by name, used when no id was given.</summary>
    public string? AgentName { get; init; }

    /// <summary>The agent's home environment, which selects the agent by --agent-id alone, outside its project.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The commit message, or null for AMS's.</summary>
    public string? Message { get; init; }

    /// <summary>The side that wins every conflict with newer commits, or null to fail on a conflict.</summary>
    public string? Resolve { get; init; }

    /// <summary>Whether to return once AMS accepted the capture.</summary>
    public bool Async { get; init; }
}

/// <summary>A capture AMS ran or accepted.</summary>
public sealed record McsAmsAgentCaptureResult : ITextGridRenderable
{
    /// <summary>Schema version of this result.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>captured once the operation produced a commit, unchanged when Dev held nothing to commit, or capturing when the command did not wait.</summary>
    public required string Status { get; init; }

    /// <summary>The agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The Dev deployment whose content was captured.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The capture operation.</summary>
    public required string OperationId { get; init; }

    /// <summary>The commit the capture produced.</summary>
    public string? CommitSha { get; init; }

    /// <summary>The commit Dev held, which the capture is based on.</summary>
    public string? BaseCommitSha { get; init; }

    /// <summary>The newer commit the capture merged Dev's edits with, or null when it merged none.</summary>
    public string? MergedCommitSha { get; init; }

    /// <summary>Customer-safe notices the operation reported.</summary>
    public IReadOnlyList<McsAmsNoticeView> Notices { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        Status switch
        {
            "captured" => $"Captured Dev of agent {AgentId} as commit {McsAmsCommits.Short(CommitSha!)}.",
            "unchanged" => $"Dev of agent {AgentId} holds nothing to capture.",
            _ => $"Capture of Dev of agent {AgentId} accepted; it keeps running in AMS.",
        },
        $"  commit       {TextGrid.DescribeOrDash(CommitSha)}",
        $"  base         {TextGrid.DescribeOrDash(BaseCommitSha)}",
        .. MergedCommitSha is null ? [] : new[] { $"  merged       {MergedCommitSha}" },
        $"  deployment   {DeploymentId}",
        $"  operation    {OperationId}",
        .. McsAmsNoticeView.Lines(Notices),
        .. Status == "capturing" ? new[] { AmsAgentException.OperationShowHint(AgentId, OperationId) } : [],
    ];
}

/// <summary>Words commits the way AMS results show them.</summary>
public static class McsAmsCommits
{
    /// <summary>The first seven characters of a commit, which git accepts as its short form.</summary>
    /// <param name="commitSha">The full commit id.</param>
    public static string Short(string commitSha) => commitSha.Length > 7 ? commitSha[..7] : commitSha;
}

/// <summary>What AMS reports about one deployment.</summary>
/// <param name="DeploymentId">The deployment.</param>
/// <param name="LastDeployedCommitSha">The commit the deployment last deployed, or null.</param>
/// <param name="State">NotDeployed or Deployed.</param>
public sealed record AmsDeploymentState(string DeploymentId, string? LastDeployedCommitSha, string? State);

/// <summary>What AMS holds for one registered agent, as <c>msagent agent show</c> reports it.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="RepositoryId">The GRS repository.</param>
/// <param name="Folder">The agent's folder in the repository.</param>
/// <param name="GitUrl">The repository clone url, or null when AMS couldn't be read.</param>
/// <param name="Deployments">What AMS reports about each of the agent's deployments.</param>
public sealed record AmsAgentDetails(string AgentId, string RepositoryId, string Folder, string? GitUrl, IReadOnlyList<AmsDeploymentState> Deployments);

/// <summary>An agent <c>msagent agent init</c> restored from AMS.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="DisplayName">The agent's name.</param>
/// <param name="Folder">The agent's folder.</param>
public sealed record AmsRestoredAgent(string AgentId, string DisplayName, string Folder);

/// <summary>What AMS mode added to <c>msagent agent create</c> or <c>msagent agent init</c>: the registration, the repository, and the commit that holds the agent.</summary>
public sealed record AmsAgentRegistration
{
    /// <summary>Whether this command registered an agent in AMS.</summary>
    public bool Registered { get; init; }

    /// <summary>The GRS repository.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>GRS's display name of the repository, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryName { get; init; }

    /// <summary>Whether this command created the repository.</summary>
    public bool RepositoryCreated { get; init; }

    /// <summary>The repository clone url.</summary>
    public required string GitUrl { get; init; }

    /// <summary>The agent's folder in the repository.</summary>
    public required string Folder { get; init; }

    /// <summary>The commit that holds the agent, which this command made or tried to push, or null when it did neither.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Whether the commit was pushed.</summary>
    public bool Pushed { get; init; }

    /// <summary>The branch the commit was pushed to.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>The agents <c>msagent agent init</c> restored from AMS.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<AmsRestoredAgent>? Restored { get; init; }

    /// <summary>Advisories that did not stop the command, such as a failed push.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>The lines main's create and init results append, in their column style.</summary>
    public IEnumerable<string> ToTextLines() =>
    [
        $"  repository   {RepositoryId}{(RepositoryName is null ? string.Empty : $" ({RepositoryName})")}{(RepositoryCreated ? ", created" : string.Empty)}",
        $"  git url      {GitUrl}",
        $"  commit       {(CommitSha is null ? "none (nothing to commit)" : $"{McsAmsCommits.Short(CommitSha)} ({(Pushed ? $"pushed to {Branch}" : "not pushed")})")}",
        .. (Restored ?? []).Select(agent => $"  restored     {agent.DisplayName} ({agent.AgentId}) in {agent.Folder}"),
        .. (Warnings ?? []).Select(TextGrid.StatusWarningLine),
    ];
}
