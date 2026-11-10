using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Typed, secret-free failures raised while working with MCS agents in AMS mode.</summary>
public sealed class AmsAgentException : AgentHostingException
{
    private AmsAgentException(int errorCode, string message, int exitCode, string errorKind, string remediation, Exception? innerException = null, TimeSpan? retryAfter = null)
        : base(message, exitCode, errorKind, remediation, innerException, errorCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the service asked the caller to wait before retrying, when it said so.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Creates the failure for a request the Agent Management Service rejected with a known code.</summary>
    /// <param name="errorCode">The stable CLI error code.</param>
    /// <param name="message">The message, already carrying correlation identifiers.</param>
    /// <param name="exitCode">The process exit code.</param>
    /// <param name="errorKind">The stable error kind.</param>
    /// <param name="remediation">What the operator can do next.</param>
    /// <param name="retryAfter">The wait the service asked for, when it asked for one.</param>
    /// <param name="multilineGuidance">Whether the remediation holds one way out per line.</param>
    public static AmsAgentException Rejected(int errorCode, string message, int exitCode, string errorKind, string remediation, TimeSpan? retryAfter = null, bool multilineGuidance = false) =>
        new(errorCode, message, exitCode, errorKind, remediation, retryAfter: retryAfter) { HasMultilineGuidance = multilineGuidance };

    /// <summary>Creates the failure for an agent AMS does not know, which a command that works through AMS needs registered.</summary>
    /// <param name="displayName">The agent's name.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="command">The command that needs it registered.</param>
    /// <param name="projectDirectory">The project folder that holds the agent.</param>
    public static AmsAgentException AgentNotRegistered(string displayName, string agentId, string command, string projectDirectory) =>
        new(ErrorCodes.AmsAgent.AgentNotRegistered, $"AMS doesn't know agent {TerminalText.Sanitize(displayName)} ({agentId}), so `{command}` can't run.", ExitCodes.Usage, "agent-not-registered", $"Run `msagent agent init` in {TerminalText.Sanitize(projectDirectory)} to register it.");

    /// <summary>Creates the failure for a project folder below its git repository's root.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="repositoryRoot">The working tree's root.</param>
    public static AmsAgentException ProjectNotRepositoryRoot(string projectDirectory, string repositoryRoot) =>
        new(ErrorCodes.AmsAgent.ProjectNotRepositoryRoot, $"The project folder {TerminalText.Sanitize(projectDirectory)} is inside the git repository at {TerminalText.Sanitize(repositoryRoot)}, and with AMS mode on a project folder must be its repository's root.", ExitCodes.Usage, "project-not-repository-root", $"Use {TerminalText.Sanitize(repositoryRoot)} as the project folder, or a folder outside any git repository.");

    /// <summary>Creates the failure for a project folder that holds files that are not part of the project.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="entries">The entries that are not part of the project.</param>
    public static AmsAgentException ProjectFolderNotDedicated(string projectDirectory, IReadOnlyList<string> entries) =>
        new(ErrorCodes.AmsAgent.ProjectFolderNotDedicated, $"{TerminalText.Sanitize(projectDirectory)} holds files that aren't part of the project: {FirstFive(entries)}.", ExitCodes.Usage, "project-folder-not-dedicated", "Move them out of the folder, or use an empty or new project folder.");

    /// <summary>Creates the failure for a project folder whose remote is not a GRS repository.</summary>
    /// <param name="remote">The remote, worded as its name and url, or the remotes when none is named origin.</param>
    public static AmsAgentException NotAGrsRepository(string remote) =>
        new(ErrorCodes.AmsAgent.NotAGrsRepository, $"The folder's remote {TerminalText.Sanitize(remote)} isn't a GRS repository, and AMS deploys only from GRS.", ExitCodes.Usage, "not-a-grs-repository", "Use another folder, or remove the remote.");

    /// <summary>Creates the failure for a --repository-id that differs from the repository the project folder's remote names, or that holds the project's registered agents.</summary>
    /// <param name="requestedRepositoryId">The --repository-id value.</param>
    /// <param name="projectRepositoryId">The repository the remote names, or that holds the registered agents.</param>
    /// <param name="registered">Whether the project's registered agents name the repository, rather than the folder's remote.</param>
    public static AmsAgentException RepositoryMismatch(string requestedRepositoryId, string projectRepositoryId, bool registered = false) =>
        new(ErrorCodes.AmsAgent.RepositoryMismatch, $"--repository-id {TerminalText.Sanitize(requestedRepositoryId)} differs from repository {projectRepositoryId} that {(registered ? "holds the project's registered agents" : "the folder's remote names")}.", ExitCodes.Usage, "repository-mismatch", "Drop --repository-id, or use another folder.");

    /// <summary>Creates the failure for an agent folder that already exists on the repository's main branch.</summary>
    /// <param name="folder">The agent's folder.</param>
    /// <param name="repository">The repository, worded as its name or id.</param>
    public static AmsAgentException FolderExistsInRepository(string folder, string repository) =>
        new(ErrorCodes.AmsAgent.FolderExistsInRepository, $"Folder {TerminalText.Sanitize(folder)} already exists on main of repository {TerminalText.Sanitize(repository)}.", ExitCodes.Usage, "folder-exists-in-repository", "Pick another name, or clone the repository and run `msagent agent init`.");

    /// <summary>Creates the failure for linking a repository that would overwrite local files.</summary>
    /// <param name="repository">The repository, worded as its name or id.</param>
    /// <param name="detail">What would be overwritten.</param>
    public static AmsAgentException RepositoryWouldOverwrite(string repository, string detail) =>
        new(ErrorCodes.AmsAgent.RepositoryWouldOverwrite, $"Linking repository {TerminalText.Sanitize(repository)} would overwrite {TerminalText.Sanitize(detail)}.", ExitCodes.Usage, "repository-would-overwrite", "Move them, or use an empty folder.");

    /// <summary>Creates the failure for a .mcs cache git tracks inside folders agent init would restore.</summary>
    /// <param name="folders">The folders whose .mcs git tracks.</param>
    public static AmsAgentException McsFolderInRepository(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return McsFolder("git tracks", folders, "restored", $"Run {Removals(folders, "git rm -r")}, commit and push, then run `msagent agent init` again; the next deploy to Dev, `msagent agent deploy`, or `msagent agent deploy --remote` when the folder holds exactly the commit it deploys, connects the folder and rebuilds the cache.");
    }

    /// <summary>Creates the failure for a .mcs cache git tracks inside folders the command would commit.</summary>
    /// <param name="folders">The folders whose .mcs git tracks.</param>
    public static AmsAgentException McsFolderTracked(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return McsFolder("git tracks", folders, "changed", $"Run {Removals(folders, "git rm -r --cached")}, then commit and run the command again.");
    }

    /// <summary>Creates the failure for a .mcs cache that commits msagent would push add or change.</summary>
    /// <param name="folders">The folders whose .mcs those commits hold.</param>
    public static AmsAgentException McsFolderInCommits(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return McsFolder("Commits msagent would push hold", folders, "changed", "Remove it from those commits, for example with `git filter-repo --invert-paths --path-glob \"*.mcs/*\"` where git filter-repo is installed, or squash them into one commit without it. Then run the command again.");
    }

    /// <summary>Creates the failure for a project folder that is itself the agent's folder.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    public static AmsAgentException WorkspaceAtProjectRoot(string projectDirectory) =>
        new(ErrorCodes.AmsAgent.WorkspaceAtProjectRoot, $"The project folder {TerminalText.Sanitize(projectDirectory)} is itself the agent's folder, and with AMS mode on each agent is a top-level folder of the project.", ExitCodes.Usage, "workspace-at-project-root", "Run `msagent agent init` from the parent folder.");

    /// <summary>Creates the failure for a git command that failed.</summary>
    /// <param name="command">The git command, such as <c>git clone</c>.</param>
    /// <param name="directory">The folder git ran in.</param>
    /// <param name="reason">git's reason, already on one line.</param>
    public static AmsAgentException GitCommandFailed(string command, string directory, string reason) =>
        new(ErrorCodes.AmsAgent.GitCommandFailed, $"`{command}` failed in {TerminalText.Sanitize(directory)}: {reason}", ExitCodes.Failure, "git-command-failed", "Fix the problem git reports, then run the command again.");

    /// <summary>Creates the failure for an agent whose AMS record changed between msagent's read and its delete.</summary>
    /// <param name="displayName">The agent's name.</param>
    /// <param name="agentId">The agent.</param>
    public static AmsAgentException AgentRecordChanged(string displayName, string agentId) =>
        new(ErrorCodes.AmsAgent.AgentRecordChanged, $"AMS's record of agent {TerminalText.Sanitize(displayName)} ({agentId}) changed while msagent deleted it, so AMS kept it and nothing was removed from .config.", ExitCodes.Failure, "agent-record-changed", "Run the same command again.");

    /// <summary>Creates the failure for a pull of a folder with uncommitted changes whose commit never synced with Copilot Studio.</summary>
    /// <param name="folder">The folder, relative to the working tree's root.</param>
    /// <param name="head">The commit the folder holds.</param>
    public static AmsAgentException UnsyncedCommitHasChanges(string folder, string head) =>
        new(ErrorCodes.AmsAgent.UnsyncedCommitHasChanges, $"{TerminalText.Sanitize(folder)} has uncommitted changes, and the commit it holds ({McsAmsCommits.Short(head)}) has never synced with Copilot Studio, so a pull can't tell them apart from Copilot Studio's.", ExitCodes.Usage, "unsynced-commit-has-changes", "Commit or stash them, then run `msagent agent pull` again.");

    /// <summary>Creates the failure for a pull of a folder whose commit never synced with Copilot Studio and lacks the commit Copilot Studio was last synced from.</summary>
    /// <param name="folder">The folder, relative to the working tree's root.</param>
    /// <param name="head">The commit the folder holds.</param>
    /// <param name="branch">The branch checked out, or null when HEAD is detached.</param>
    /// <param name="synced">The commit Copilot Studio was last synced from.</param>
    /// <param name="syncedBranch">The branch that commit was synced from, or null when it is unknown.</param>
    public static AmsAgentException SyncedCommitNotMerged(string folder, string head, string? branch, string synced, string? syncedBranch) =>
        new(ErrorCodes.AmsAgent.SyncedCommitNotMerged, $"{TerminalText.Sanitize(folder)} holds {CommitOf(head, branch)}, which has never synced with Copilot Studio and doesn't contain {CommitOf(synced, syncedBranch)}, the commit Copilot Studio was last synced from.", ExitCodes.Usage, "synced-commit-not-merged", $"Merge that commit (`git merge {McsAmsCommits.Short(synced)}`), then run `msagent agent pull` again.");

    /// <summary>Creates the failure for a test-only endpoint override that cannot carry a delegated token.</summary>
    /// <param name="message">Why the value cannot be used, naming the environment variable that supplied it.</param>
    /// <param name="remediation">What to supply instead.</param>
    public static AmsAgentException EndpointOverrideNotUsable(string message, string remediation) =>
        new(ErrorCodes.AmsAgent.EndpointOverrideNotUsable, message, ExitCodes.Failure, "endpoint-untrusted", remediation);

    /// <summary>Creates the failure for an on-behalf-of operation that would reach the plain data-plane override.</summary>
    /// <param name="operation">The command that needs the on-behalf-of exchange.</param>
    /// <param name="dataPlaneVariable">The variable naming the plain data-plane override.</param>
    /// <param name="onBehalfOfVariable">The variable naming the on-behalf-of override.</param>
    public static AmsAgentException OnBehalfOfEndpointRequired(string operation, string dataPlaneVariable, string onBehalfOfVariable) =>
        new(
            ErrorCodes.AmsAgent.OnBehalfOfEndpointRequired,
            $"`{operation}` needs the Agent Management Service to act on your behalf, which only works through the Power Platform API gateway, and {dataPlaneVariable} points every data-plane call at a direct service endpoint that carries no gateway ticket.",
            ExitCodes.Usage,
            "on-behalf-of-endpoint-required",
            $"Set {onBehalfOfVariable} to a gateway endpoint that carries the ticket, or unset {dataPlaneVariable} to use the Power Platform API front door.");

    /// <summary>Creates the failure reported when the operator stops waiting for an operation.</summary>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation that keeps running.</param>
    /// <param name="innerException">The cancellation that stopped the wait.</param>
    public static AmsAgentException OperationWaitInterrupted(string agentId, string operationId, Exception innerException) =>
        new(
            ErrorCodes.AmsAgent.OperationWaitInterrupted,
            $"Stopped waiting for operation {operationId}. The operation keeps running in the Agent Management Service.",
            ExitCodes.Failure,
            "operation-wait-interrupted",
            OperationShowHint(agentId, operationId),
            innerException);

    /// <summary>Creates the failure reported when an operation outlives the wait or stops reporting progress.</summary>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation that is still unfinished.</param>
    /// <param name="reason">Why waiting stopped.</param>
    public static AmsAgentException OperationWaitTimedOut(string agentId, string operationId, string reason) =>
        new(
            ErrorCodes.AmsAgent.OperationWaitTimedOut,
            $"Stopped waiting for operation {operationId}: {reason}",
            ExitCodes.Failure,
            "operation-wait-timeout",
            OperationShowHint(agentId, operationId));

    /// <summary>Creates the failure for an agent that has no Dev deployment yet, naming the commands that create one and deploy to it.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="displayName">The agent's name.</param>
    public static AmsAgentException NoDevDeployment(string agentId, string displayName) =>
        new(ErrorCodes.AmsAgent.AgentNotEligible, $"Agent {agentId} has no Dev deployment yet.", ExitCodes.Usage, "agent-not-eligible", string.Join(Environment.NewLine, DeploymentCommandHints.CreateDevDeployment(displayName, $"--agent-id {agentId}", $"--deployment-name {DeploymentCommandHints.DevDeploymentName}"))) { HasMultilineGuidance = true };

    /// <summary>The hint that points at the command that reports an operation's state.</summary>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation to show.</param>
    public static string OperationShowHint(string agentId, string operationId) =>
        $"Run `msagent agent operation show --agent-id {agentId} --operation-id {operationId}` to check its state.";

    internal static string FirstFive(IReadOnlyList<string> entries) => TerminalText.Sanitize(string.Join(", ", entries.Take(5)) + (entries.Count > 5 ? $" and {entries.Count - 5} more" : string.Empty));

    internal static string CommitOf(string commit, string? branch) => branch is null ? McsAmsCommits.Short(commit) : $"{McsAmsCommits.Short(commit)} ({TerminalText.Sanitize(branch)})";

    private static AmsAgentException McsFolder(string holder, IReadOnlyList<string> folders, string undone, string remediation) => new(ErrorCodes.AmsAgent.McsFolderInRepository, $"{holder} a .mcs folder inside {FirstFive(folders)}, a cache that must come from the cloud, so nothing was {undone}.", ExitCodes.Usage, "mcs-folder-in-repository", remediation);

    private static string Removals(IReadOnlyList<string> folders, string command) => string.Join(" and ", folders.Select(folder => $"`{command} {DeploymentCommandHints.Quoted($"{folder}/.mcs")}`"));
}
