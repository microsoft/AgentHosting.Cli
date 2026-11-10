using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

public sealed record AmsFolder(string Project, string AgentId, string Folder);

public sealed record AmsHome(string Project, string AgentId, string Name, string Folder, string RepositoryId, ManagedAgentCreateResult Created)
{
    public AmsFolder Agent => new(Project, AgentId, Folder);
}

public sealed record AmsHomeAgent(string AgentId, string Name, string Folder, string? MainBefore, ManagedAgentCreateResult Created);

public sealed record AmsHomeDeployments(string Dev, string Test, string Prod, string TestName);

public sealed record AmsDevDeploy(string BotId, string CommitSha, string Projection, string? ProjectionCommit);

public sealed record AmsTestPublished(string CommitSha, string Name);

public sealed record AmsDevAgent(string Project, string AgentId, string Name, string Folder, string RepositoryId, ManagedAgentCreateResult Created, string DevDeploymentId)
{
    public AmsFolder Agent => new(Project, AgentId, Folder);
}

public sealed record AmsOffScaffold(string Project, string Name, string Folder, ManagedAgentCreateResult Created);

public sealed record AmsOffSource(string Project, string AgentId, string Name, string Folder, string? SchemaName, string? BotId, string? WorkflowId, ManagedAgentCreateResult Created, AgentDeploymentCreateResult Dev, ManagedAgentDeployResult Deployed);

public static class AmsMcsSteps
{
    public const string HomeSetup = "the home project P, with agent A and repository R";

    public const string AgentBSetup = "agent B in P";

    public const string DeploymentsSetup = "A's deployments development, test-ring and production";

    public const string DevDeploySetup = "A's local Dev deploy and the capture after it";

    public const string TestPublishedSetup = "a commit of A that Test published";

    public const string WorkflowsSetup = "the workflow project, with workflow repository W";

    public const string RemoteSetup = "agent X, with its repository and a Dev deployment";

    public const string CopiesSetup = "agent Z, with its repository and a Dev deployment";

    public const string Branch = "main";

    private const string Description = "MCS-on-AMS E2E check";

    private const string InitialPrompt = "Answer questions about the MCS-on-AMS E2E check.";

    public static readonly TimeSpan LongCall = TimeSpan.FromMinutes(10);

    public static AmsMcsServices Services(ScenarioContext context) => context.AmsMcs ?? throw new E2EException($"{context.ScenarioId} runs only with --packs ams.");

    public static async Task<AmsMcsServices> StartAsync(ScenarioContext context)
    {
        var ams = Services(context);
        await ams.EnsureRingAsync(context.Ledger, context.ScenarioId, context.CancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(ams.WorkspaceRoot!);
        return ams;
    }

    public static string Folder(ScenarioContext context, string label) => Path.Combine(Services(context).WorkspaceRoot!, context.ScenarioId, label);

    public static string EmptyFolder(ScenarioContext context, string label)
    {
        var folder = Folder(context, label);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string Name(ScenarioContext context, string label) => $"ahe2e {label} {context.RunSuffix}";

    public static string[] ScaffoldArguments(ScenarioContext context, string name, string project, params string[] extra) => ["agent", "create", "--agent-type", AgentTypes.McsAgent, "--agent-name", name, "--description", Description, "--initial-prompt", InitialPrompt, "--environment-id", Services(context).Config.DevEnvironmentId!, "--project", project, .. extra];

    public static string[] WorkflowArguments(ScenarioContext context, string name, string project, params string[] extra) => ["agent", "create", "--agent-type", AgentTypes.McsWorkflow, "--agent-name", name, "--environment-id", Services(context).Config.DevEnvironmentId!, "--project", project, .. extra];

    public static string[] CloneArguments(ScenarioContext context, AmsOffSource source, string project, params string[] extra) => source.WorkflowId is { } workflowId ? CloneCommand(context, ["--agent-type", AgentTypes.McsWorkflow, "--mcs-clone-workflow-id", workflowId], project, extra) : CloneBotArguments(context, source.BotId!, project, extra);

    public static string[] CloneBotArguments(ScenarioContext context, string botId, string project, params string[] extra) => CloneCommand(context, ["--agent-type", AgentTypes.McsAgent, "--mcs-clone-agent-id", botId], project, extra);

    public static string[] InitArguments(string project, params string[] extra) => ["agent", "init", "--mcs-agent-source", project, .. extra];

    public static string[] RemoteArguments(string project, string agentId, string deploymentId, params string[] extra) => ["agent", "deploy", "--remote", "--project", project, "--agent-id", agentId, "--deployment-id", deploymentId, .. extra];

    public static string[] PullArguments(string project, string agentId) => ["agent", "pull", "--project", project, "--agent-id", agentId];

    public static string[] CaptureArguments(string project, string agentId) => ["agent", "capture", "--project", project, "--agent-id", agentId];

    public static Task<MsagentResult> RawAsync(ScenarioContext context, MsagentCli msagent, IReadOnlyList<string> arguments, TimeSpan? timeout = null) => msagent.RunAsync([.. arguments, "--json", "--non-interactive"], Services(context).WorkspaceRoot, timeout: timeout ?? AmsMcsServices.CallTimeout, cancellationToken: context.CancellationToken);

    public static async Task<T> RunAsync<T>(ScenarioContext context, MsagentCli msagent, string check, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var result = await RawAsync(context, msagent, arguments, timeout).ConfigureAwait(false);
        context.Assert.ExitZero(result, check);
        return result.Json<T>();
    }

    public static async Task<T> RecordedAsync<T>(ScenarioContext context, MsagentCli msagent, string check, string project, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var result = await RawAsync(context, msagent, arguments, timeout).ConfigureAwait(false);
        Services(context).Record(context.Ledger, result, project, msagent == Services(context).Off, context.ScenarioId);
        context.Assert.ExitZero(result, check);
        return result.Json<T>();
    }

    public static async Task<MsagentOutcome> RefusedAsync(ScenarioContext context, string project, IReadOnlyList<string> arguments, int errorCode, string check, int? exitCode = null, string? fragment = null)
    {
        var result = await RawAsync(context, Services(context).On, arguments, LongCall).ConfigureAwait(false);
        Services(context).Record(context.Ledger, result, project, amsOff: false, context.ScenarioId);
        return context.Assert.Refused(result, errorCode, check, exitCode, fragment);
    }

    public static async Task RefusedBeforeQuestionAsync(ScenarioContext context, string project, IReadOnlyList<string> arguments, int errorCode, string check, string fragment)
    {
        var ams = Services(context);
        var result = await ams.On.RunAsync([.. arguments, "--json"], ams.WorkspaceRoot, stdin: "n", timeout: LongCall, cancellationToken: context.CancellationToken).ConfigureAwait(false);
        ams.Record(context.Ledger, result, project, amsOff: false, context.ScenarioId);
        context.Assert.Refused(result, errorCode, check, 2, fragment);
        context.Assert.False(AmsMcsText.HasQuestion(result.Output), $"{check}: nothing was asked first, so the answer n reached no question");
    }

    public static async Task InitUndoneAsync(ScenarioContext context, string what, string outer, string project, IReadOnlyList<string> extra, int errorCode, string check, string fragment)
    {
        var before = AmsMcsFiles.Fingerprint(outer);
        await RefusedAsync(context, project, InitArguments(project, [.. extra]), errorCode, check, 2, fragment).ConfigureAwait(false);
        context.Assert.True(AmsMcsFiles.Fingerprint(outer) == before, $"the failed init {what} removed the clone: {(before is null ? "neither the folder nor its missing parents exist" : "the folder stays empty")}", $"{outer} before={before ?? "<missing>"} after={AmsMcsFiles.Fingerprint(outer) ?? "<missing>"}");
    }

    public static Task<ManagedAgentCreateResult> CreateAsync(ScenarioContext context, MsagentCli msagent, string project, string name, params string[] extra) => RecordedAsync<ManagedAgentCreateResult>(context, msagent, $"agent create {name}", project, ScaffoldArguments(context, name, project, extra), LongCall);

    public static async Task<ManagedAgentCreateResult> CreateWorkflowAsync(ScenarioContext context, string project, string name, params string[] extra)
    {
        var ams = Services(context);
        var created = await RecordedAsync<ManagedAgentCreateResult>(context, ams.On, $"agent create {name}", project, WorkflowArguments(context, name, project, extra), LongCall).ConfigureAwait(false);
        RecordFlows(context, created.WorkflowName, ams.Config.DevEnvironmentId);
        return created;
    }

    public static Task<ManagedAgentCreateResult> CloneAsync(ScenarioContext context, AmsOffSource source, string project, params string[] extra) => RecordedAsync<ManagedAgentCreateResult>(context, Services(context).On, $"agent create, cloning {source.Name}", project, CloneArguments(context, source, project, extra), LongCall);

    public static Task<ManagedAgentInitResult> InitAsync(ScenarioContext context, string check, string project, params string[] extra) => RecordedAsync<ManagedAgentInitResult>(context, Services(context).On, check, project, InitArguments(project, extra), LongCall);

    public static AmsAgentRegistration Registered(ScenarioContext context, ManagedAgentCreateResult created) => Registration(context, created.Ams, $"agent create {created.DisplayName} returned AMS's registration", created.Status);

    public static AmsAgentRegistration Registered(ScenarioContext context, ManagedAgentInitResult initialized) => Registration(context, initialized.Ams, $"agent init {initialized.DisplayName} returned AMS's registration", initialized.Status);

    public static Task<AgentDeploymentCreateResult> DeploymentCreateAsync(ScenarioContext context, MsagentCli msagent, string project, string agentId, string deploymentName, string deploymentType, string? environmentId)
    {
        string[] environment = environmentId is null ? [] : ["--environment-id", environmentId];

        return RecordedAsync<AgentDeploymentCreateResult>(context, msagent, $"deployment create {deploymentName} ({deploymentType})", project, ["deployment", "create", "--project", project, "--agent-id", agentId, "--deployment-name", deploymentName, "--deployment-type", deploymentType, .. environment]);
    }

    public static Task<ManagedAgentDeployResult> DeployAsync(ScenarioContext context, MsagentCli msagent, string project, string agentId, string deploymentId, params string[] extra) => RecordedAsync<ManagedAgentDeployResult>(context, msagent, $"local deploy of {agentId} to {deploymentId}", project, ["agent", "deploy", "--project", project, "--agent-id", agentId, "--deployment-id", deploymentId, .. extra], LongCall);

    public static Task<McsAmsAgentDeployResult> RemoteDeployAsync(ScenarioContext context, MsagentCli msagent, string project, string agentId, string deploymentId, params string[] extra) => RunAsync<McsAmsAgentDeployResult>(context, msagent, $"remote deploy of {agentId} to {deploymentId} {string.Join(' ', extra)}".TrimEnd(), RemoteArguments(project, agentId, deploymentId, extra), LongCall);

    public static Task<ManagedAgentPullResult> PullAsync(ScenarioContext context, MsagentCli msagent, string project, string agentId) => RunAsync<ManagedAgentPullResult>(context, msagent, "agent pull", PullArguments(project, agentId), LongCall);

    public static Task<McsAmsAgentCaptureResult> CaptureAsync(ScenarioContext context, string project, string agentId) => RunAsync<McsAmsAgentCaptureResult>(context, Services(context).On, "agent capture", CaptureArguments(project, agentId), LongCall);

    public static Task<ManagedAgentShowResult> ShowAsync(ScenarioContext context, string project, string agentId) => RunAsync<ManagedAgentShowResult>(context, Services(context).On, "agent show", ["agent", "show", "--project", project, "--agent-id", agentId]);

    public static async Task<MsagentResult> ShowTextAsync(ScenarioContext context, string project, string agentId)
    {
        var ams = Services(context);
        var shown = await ams.On.RunAsync(["agent", "show", "--project", project, "--agent-id", agentId, "--no-color", "--non-interactive"], ams.WorkspaceRoot, cancellationToken: context.CancellationToken).ConfigureAwait(false);
        context.Assert.ExitZero(shown, "agent show (text)");
        return shown;
    }

    public static Task<AgentDeploymentListResult> SlotsAsync(ScenarioContext context, string project, string agentId) => RunAsync<AgentDeploymentListResult>(context, Services(context).On, "deployment list", ["deployment", "list", "--project", project, "--agent-id", agentId]);

    public static async Task<AgentDeploymentListItem?> SlotAsync(ScenarioContext context, string project, string agentId, string deploymentId) => AmsMcsResults.Slot(await SlotsAsync(context, project, agentId).ConfigureAwait(false), deploymentId);

    public static Task<ManagedAgentDeleteResult> DeleteAsync(ScenarioContext context, MsagentCli msagent, string check, string project, string agentId, params string[] extra) => RunAsync<ManagedAgentDeleteResult>(context, msagent, check, ["agent", "delete", "--project", project, "--agent-id", agentId, .. extra], LongCall);

    public static Task<AgentDeploymentConnectionResult> ConnectionsAsync(ScenarioContext context, string verb, string project, string agentId, string deploymentId) => RunAsync<AgentDeploymentConnectionResult>(context, Services(context).On, $"deployment connection {verb}", ["deployment", "connection", verb, "--project", project, "--agent-id", agentId, "--deployment-id", deploymentId], LongCall);

    public static async Task<ManagedAgentListResult> AmsAgentsAsync(ScenarioContext context, string? repositoryId, string? agentType = null)
    {
        var ams = Services(context);
        var result = await ams.ListAgentsAsync(ams.Config.DevEnvironmentId!, repositoryId, agentType, context.CancellationToken).ConfigureAwait(false);
        context.Assert.ExitZero(result, $"agent list --view ams{(repositoryId is null ? string.Empty : $" --repository-id {repositoryId}")}{(agentType is null ? string.Empty : $" --agent-type {agentType}")}");
        var listed = result.Json<ManagedAgentListResult>();
        context.Assert.False(listed.Truncated, "the AMS list holds every agent, within --limit 1000");
        return listed;
    }

    public static async Task<string> AmsAgentIdsAsync(ScenarioContext context, params string[] repositoryIds)
    {
        List<string> ids = [];

        foreach (var repositoryId in repositoryIds)
        {
            ids.AddRange(AmsMcsResults.Ids((await AmsAgentsAsync(context, repositoryId).ConfigureAwait(false)).Agents));
        }

        return string.Join(',', ids);
    }

    public static async Task UnknownAsync(ScenarioContext context, MsagentCli msagent, string form, IReadOnlyList<string> arguments) => context.Assert.Refused(await RawAsync(context, msagent, arguments).ConfigureAwait(false), 99002, $"{form} is unknown with the flag off (Usage, 99002, exit 2)", 2, "Unrecognized command or argument");

    public static Task<string?> RemoteMainAsync(ScenarioContext context, string directory) => Services(context).Git.RemoteHeadAsync(directory, Branch, context.CancellationToken);

    public static async Task SyncAsync(ScenarioContext context, string project)
    {
        var git = Services(context).Git;
        await git.PullAsync(project, Branch, context.CancellationToken).ConfigureAwait(false);
        var tip = await git.TipAsync(project, Branch, context.CancellationToken).ConfigureAwait(false);
        context.Assert.True(tip.HoldsRemote, $"git pull --ff-only leaves {project} holding main's head, with nothing uncommitted", $"head={tip.Head} main={tip.Remote} status={GitTip.Describe(tip.Status)}");
    }

    public static async Task<string> CommitDisplayNameAsync(ScenarioContext context, string project, string folder, string name, string branch = Branch, bool push = true)
    {
        var git = Services(context).Git;
        AmsMcsFiles.SetDisplayName(AmsMcsFiles.SettingsPath(project, folder), name);
        var commit = await git.CommitAsync(project, $"ahe2e: displayName {name}", [AmsMcsFiles.SettingsEntry(folder)], context.CancellationToken).ConfigureAwait(false);

        if (push)
        {
            await git.PushAsync(project, branch, context.CancellationToken).ConfigureAwait(false);
        }

        return commit;
    }

    public static async Task PushRootSettingsAsync(ScenarioContext context, string project)
    {
        var git = Services(context).Git;
        AmsMcsFiles.SetRootSettings(project, Name(context, "Root"));
        await git.CommitAsync(project, "ahe2e: an agent's settings at the repository root", [WorkspaceFileNames.Settings], context.CancellationToken).ConfigureAwait(false);
        await git.PushAsync(project, Branch, context.CancellationToken).ConfigureAwait(false);
    }

    public static async Task PushRemovalAsync(ScenarioContext context, string project, params string[] paths)
    {
        var git = Services(context).Git;
        await git.RemoveAsync(project, paths, cached: false, context.CancellationToken).ConfigureAwait(false);
        await git.CommitStagedAsync(project, $"ahe2e: remove {string.Join(", ", paths)}", context.CancellationToken).ConfigureAwait(false);
        await git.PushAsync(project, Branch, context.CancellationToken).ConfigureAwait(false);
    }

    public static void AssertSyncFilesKept(ScenarioContext context, string label, string folder, string before)
    {
        var after = AmsMcsFiles.SyncFilesHash(folder);
        context.Assert.True(before.Length > 0 && after == before, $"{label} rewrote neither settings.mcs.yml nor agent.sync.yaml (P28; the project has no git)", $"before={before} after={after}");
    }

    public static async Task AssertPulledAsync(ScenarioContext context, string label, AmsFolder agent, ManagedAgentPullResult pulled, string name)
    {
        context.Assert.True(pulled.Status == "agent-pulled", $"{label}: agent-pulled", $"status={pulled.Status} changes={pulled.ChangeCount}");
        await AssertOnlySettingsChangedAsync(context, label, agent, name).ConfigureAwait(false);
    }

    public static async Task AssertOnlySettingsChangedAsync(ScenarioContext context, string label, AmsFolder agent, string name)
    {
        var git = Services(context).Git;
        var status = await git.StatusAsync(agent.Project, [], context.CancellationToken).ConfigureAwait(false);
        var modified = await git.IsModifiedAsync(agent.Project, AmsMcsFiles.SettingsEntry(agent.Folder), context.CancellationToken).ConfigureAwait(false);
        var displayName = AmsMcsText.DisplayName(AmsMcsFiles.ReadSettings(Path.Combine(agent.Project, agent.Folder)));
        context.Assert.True(displayName == name && status is [var only] && only.StartsWith(" M ", StringComparison.Ordinal) && modified, $"{label}: displayName is '{name}', and git shows only settings.mcs.yml modified, unstaged", $"displayName='{displayName}' git status: {GitTip.Describe(status)}");
    }

    public static async Task<MsagentOutcome> PullKeepsFolderAsync(ScenarioContext context, string label, AmsFolder agent, string phrase)
    {
        var settings = AmsMcsFiles.SettingsPath(agent.Project, agent.Folder);
        var file = AmsMcsFiles.FileHash(settings);
        var cache = AmsMcsFiles.CacheFingerprint(agent.Project, agent.Folder);
        var shelf = McsShelf.Describe(GitCli.ShelfOf(agent.Project, agent.Folder));
        var result = await RawAsync(context, Services(context).On, PullArguments(agent.Project, agent.AgentId), LongCall).ConfigureAwait(false);
        var outcome = MsagentOutcome.Read(result);
        var shelfAfter = McsShelf.Describe(GitCli.ShelfOf(agent.Project, agent.Folder));
        context.Assert.True(result.ExitCode != 0 && !outcome.Success && AmsMcsText.Says(outcome.FailureText, phrase) && file is not null && AmsMcsFiles.FileHash(settings) == file && AmsMcsFiles.CacheFingerprint(agent.Project, agent.Folder) == cache && shelfAfter == shelf, $"{label}: agent pull fails (non-zero exit), saying {phrase}; settings.mcs.yml, .mcs and the shelf are as they were", $"exit={result.ExitCode} errorCode={outcome.ErrorCode} message={outcome.FailureText.Trim()} | shelf before: {shelf} | after: {shelfAfter}");
        return outcome;
    }

    public static async Task AssertCleanAfterDeployAsync(ScenarioContext context, string project, string folder)
    {
        var status = await Services(context).Git.StatusAsync(project, [folder], context.CancellationToken).ConfigureAwait(false);
        context.Assert.True(status.Count == 0, $"the local deploy left {folder} clean in git (P28)", $"git status --porcelain -- {folder}: {string.Join("; ", status)}");
    }

    public static async Task RenameBotAsync(ScenarioContext context, string botId, string name)
    {
        var ams = Services(context);
        AssertRenamed(context, "bot", botId, (await ams.Dataverse.RenameBotAsync(ams.Config.DevEnvironmentId!, botId, name, context.CancellationToken).ConfigureAwait(false))?.Name, name);
    }

    public static async Task RenameFlowAsync(ScenarioContext context, string workflowId, string name)
    {
        var ams = Services(context);
        RecordFlows(context, name, ams.Config.DevEnvironmentId);
        AssertRenamed(context, "flow", workflowId, (await ams.Dataverse.RenameFlowAsync(ams.Config.DevEnvironmentId!, workflowId, name, context.CancellationToken).ConfigureAwait(false))?.Name, name);
    }

    public static void RecordConnector(ScenarioContext context, AmsCustomConnector connector)
    {
        var dev = Services(context).Config.DevEnvironmentId!;
        context.Ledger.RecordAmsConnector(connector.Name, dev, context.ScenarioId);
        context.Ledger.RecordAmsConnectionReference(connector.ConnectionReference, dev, context.ScenarioId);
    }

    public static async Task<string?> BotNameAsync(ScenarioContext context, string? botId) => botId is null ? null : (await Services(context).Dataverse.BotAsync(Services(context).Config.DevEnvironmentId!, botId, context.CancellationToken).ConfigureAwait(false))?.Name;

    public static async Task<DataverseWorkflow?> TestFlowAsync(ScenarioContext context, string name) => await Services(context).Dataverse.FlowsNamedExactlyAsync(Services(context).Config.TestEnvironmentId!, name, context.CancellationToken).ConfigureAwait(false) is [var flow, ..] ? flow : null;

    public static void RecordFlows(ScenarioContext context, string? name, params string?[] environmentIds)
    {
        if (name is not { Length: > 0 })
        {
            return;
        }

        foreach (var environmentId in environmentIds.OfType<string>())
        {
            context.Ledger.RecordAmsFlow(name, environmentId, context.ScenarioId);
        }
    }

    public static async Task AssertCloneAsync(ScenarioContext context, string directory, string? gitUrl, string? commitSha, string subject, IReadOnlyList<string> folders, string? parentSha, bool newRepository, string parentLabel = "the previous GRS main")
    {
        var ams = Services(context);
        var clone = await ams.Git.InspectAsync(directory, ams.Config.GitHost!, context.CancellationToken).ConfigureAwait(false);

        context.Assert.True(clone.IsRootOf(directory), $"{directory} is the root of a git clone", $"toplevel={clone.TopLevel}");
        context.Assert.True(clone.OriginIs(gitUrl), "origin is the repository's git url", $"origin={clone.Origin} gitUrl={gitUrl}");
        context.Assert.True(clone.HeadIs(commitSha, subject), $"HEAD is the pushed commit '{subject}'", $"head={clone.Head?.Sha} '{clone.Head?.Subject}' commitSha={commitSha}");

        if (parentSha is not null)
        {
            context.Assert.True(clone.ParentIs(parentSha), $"the commit's parent is {parentLabel}", $"parent={clone.Parent?.Sha} expected={parentSha}");
        }

        if (newRepository)
        {
            AssertGrsInitialParent(context, clone);
        }

        context.Assert.True(clone.ChangesOnly(folders), $"compared with its parent, the commit changes only {string.Join(", ", folders)} and .gitignore", $"files={clone.Changed?.Count} outside={string.Join(", ", clone.ChangedOutside(folders))}");
        context.Assert.True(clone.IsClean, "git status is clean", string.Join("; ", clone.Status ?? ["git status failed"]));
        context.Assert.True(clone.KeepsConfigOut, ".gitignore keeps .config/agent.config.json out of git", $"tracked={string.Join(", ", clone.TrackedConfig ?? [])}");
        context.Assert.True(clone.KeepsCacheOut, ".gitignore also holds .mcs/, and git tracks nothing under a .mcs folder (P31)", $"tracked={string.Join(", ", clone.TrackedCache ?? [])}");
        context.Assert.True(clone.HasMsagentHelper(ams.Config.GitHost!), $".git/config resets the credential helpers for {ams.Config.GitHost} and sets msagent's", $"helpers={string.Join(" | ", clone.Helpers ?? [])}");
        AssertPushedMain(context, clone, commitSha);
    }

    public static async Task AssertPushedAsync(ScenarioContext context, string directory, string? commitSha, string? parentSha)
    {
        var ams = Services(context);
        var clone = await ams.Git.InspectAsync(directory, ams.Config.GitHost!, context.CancellationToken).ConfigureAwait(false);
        AssertPushedMain(context, clone, commitSha);
        context.Assert.True(clone.Head is not null && GitCommits.Match(clone.Head.Sha, commitSha), "HEAD is the pushed commit", $"head={clone.Head?.Sha} commitSha={commitSha}");

        if (parentSha is null)
        {
            AssertGrsInitialParent(context, clone);
        }
        else
        {
            context.Assert.True(clone.ParentIs(parentSha), "the commit's parent is the previous GRS main", $"parent={clone.Parent?.Sha} expected={parentSha}");
        }
    }

    public static async Task HoldsCommitAsync(ScenarioContext context, string check, AmsFolder agent, string? commit)
    {
        var git = Services(context).Git;
        var head = await git.RevParseAsync(agent.Project, "HEAD", context.CancellationToken).ConfigureAwait(false);
        var status = await git.StatusAsync(agent.Project, [agent.Folder], context.CancellationToken).ConfigureAwait(false);
        context.Assert.True(GitCommits.Match(head, commit) && status.Count == 0, check, $"head={head} expected={commit} git status: {GitTip.Describe(status)}");
    }

    public static void AssertConnected(ScenarioContext context, string label, AmsFolder agent, McsAmsAgentDeployResult deployed, string? botId)
    {
        var ams = Services(context);
        var facts = AmsMcsFiles.Connection(agent.Project, agent.Folder, agent.AgentId);
        context.Assert.True(deployed.WorkspaceConnected && string.IsNullOrEmpty(deployed.WorkspaceNotice) && facts.BindsTo(botId, ams.Config.DevEnvironmentId, ams.DevOrganization), $"{label}: workspaceConnected is true, with no workspaceNotice; .mcs\\conn.json is bound to the Dev bot in Dev and .mcs\\.gitignore holds *; .config's mcsAgentId is that bot (P30)", $"workspaceConnected={deployed.WorkspaceConnected} workspaceNotice='{deployed.WorkspaceNotice}' {facts.Describe()}; bot={botId}; warnings={string.Join(" | ", deployed.Warnings)}");
    }

    public static void AssertNotConnected(ScenarioContext context, string label, McsAmsAgentDeployResult deployed) => context.Assert.True(!deployed.WorkspaceConnected && string.IsNullOrEmpty(deployed.WorkspaceNotice), $"{label} connects nothing and says nothing about the folder: workspaceConnected isn't true, and there's no workspaceNotice (P30)", $"workspaceConnected={deployed.WorkspaceConnected} workspaceNotice='{deployed.WorkspaceNotice}'");

    public static void AssertLeftAlone(ScenarioContext context, string label, AmsFolder agent, McsAmsAgentDeployResult deployed, AmsFolderState before, AmsNoticeReason reason, bool connected, string? held = null, string? deployedSha = null)
    {
        var after = AmsFolderState.Read(agent.Project, agent.Folder, agent.AgentId);
        context.Assert.True(!deployed.WorkspaceConnected && before.Kept(after, connected) && AmsMcsText.Explains(deployed.WorkspaceNotice, reason, held, deployedSha) && AmsMcsText.AdvisesPull(deployed.WorkspaceNotice, connected), $"{label}: the folder was left alone: workspaceConnected isn't true, {(connected ? ".mcs is exactly as it was" : ".mcs is still absent")}, .config's mcsAgentId is unchanged, and workspaceNotice gives the reason ({reason}) and {(connected ? "says agent pull brings the deployed changes into it" : "says msagent didn't connect the folder")} (P30)", $"workspaceConnected={deployed.WorkspaceConnected} {before.Describe(after)} held={GitCommits.Short(held)} deployed={GitCommits.Short(deployedSha)} workspaceNotice='{deployed.WorkspaceNotice}' warnings={string.Join(" | ", deployed.Warnings)}");
    }

    public static async Task AssertPullUpToDateAsync(ScenarioContext context, string label, AmsFolder agent)
    {
        var ams = Services(context);
        var before = await ams.Git.StatusAsync(agent.Project, [], context.CancellationToken).ConfigureAwait(false);
        var pulled = await PullAsync(context, ams.On, agent.Project, agent.AgentId).ConfigureAwait(false);
        var after = await ams.Git.StatusAsync(agent.Project, [], context.CancellationToken).ConfigureAwait(false);
        context.Assert.True(pulled.Status == "up-to-date" && before.Count == 0 && after.Count == 0, $"{label}: agent pull is up-to-date, and git status stays clean (P30)", $"status={pulled.Status} changes={pulled.ChangeCount} | git status after the deploy: {GitTip.Describe(before)}; after the pull: {GitTip.Describe(after)}");
    }

    public static async Task PullRefusedAsync(ScenarioContext context, MsagentCli msagent, AmsFolder agent, string label)
    {
        var amsOn = msagent != Services(context).Off;
        var outcome = context.Assert.Refused(await RawAsync(context, msagent, PullArguments(agent.Project, agent.AgentId), LongCall).ConfigureAwait(false), 10011, $"{label}: agent pull{(amsOn ? string.Empty : " with AMS off")} fails with WorkspaceDisconnected (10011, exit 2)", 2);
        context.Assert.True(amsOn ? AmsMcsText.NamesAmsRemedy(outcome.FailureText) : AmsMcsText.NamesMainRemedy(outcome.FailureText), amsOn ? $"{label}: with AMS on, the remedy names agent capture and git pull" : $"{label}: with AMS off, it's main's text, which doesn't name agent capture", outcome.FailureText);
    }

    public static async Task AssertRestoredAsync(ScenarioContext context, string label, string project, ManagedAgentInitResult initialized, string? head, AmsHome home, AmsHomeDeployments deployments, AmsDevDeploy dev, AmsHomeAgent agentB)
    {
        var ams = Services(context);
        var expected = AmsMcsResults.HeldIn(await AmsAgentsAsync(context, home.RepositoryId).ConfigureAwait(false), project);
        var expectedIds = AmsMcsResults.Ids(expected);
        var restoredIds = AmsMcsResults.RestoredIds(initialized.Ams);
        context.Assert.True(initialized.Status == "agent-initialized" && expectedIds.Count > 0 && expectedIds.SequenceEqual(restoredIds) && AmsIds.Same(initialized.Ams?.RepositoryId, home.RepositoryId) && initialized.Ams?.RepositoryCreated == false, $"{label}: init restored every AMS agent whose folder the clone holds, from the repository, and created no repository", $"expected={string.Join(',', expectedIds)} restored={string.Join(',', restoredIds)} repository={initialized.Ams?.RepositoryId}");

        var config = AmsMcsFiles.ReadAgentConfig(project);
        var mismatched = AmsMcsFiles.Mismatched(config, expected, home.RepositoryId);
        context.Assert.True(mismatched.Count == 0, $"{label}: .config restores each of them with its AMS id, type, repository, main and folder", string.Join("; ", mismatched));
        var unlowered = AmsIds.NotLowercase(config!, AmsMcsResults.Ids(expected));
        context.Assert.True(unlowered.Count == 0, $"{label}: the restored .config ids (agents, repository, bot, tenant, environments, deployments) are lowercase GUIDs, whatever case AMS answered in", string.Join("; ", unlowered));

        var homeConfig = AmsMcsFiles.ReadAgentConfig(home.Project);

        foreach (var (agentId, owner, required) in new (string AgentId, string Owner, bool Required)[] { (home.AgentId, "A", true), (agentB.AgentId, "B", false) })
        {
            var mine = AmsMcsFiles.DeploymentSignature(homeConfig, agentId);
            var theirs = AmsMcsFiles.DeploymentSignature(config, agentId);
            context.Assert.True((mine.Length > 0 || !required) && mine == theirs, $"{label}: .config holds {owner}'s deployments with the home project's ids, names and stages", $"home={mine} restored={theirs}");
        }

        var devRow = AmsMcsFiles.Deployment(config, deployments.Dev);
        var headNow = await ams.Git.RevParseAsync(project, "HEAD", context.CancellationToken).ConfigureAwait(false);
        var status = await ams.Git.StatusAsync(project, [], context.CancellationToken).ConfigureAwait(false);
        context.Assert.True(AmsIds.Same(AmsMcsFiles.Agent(config, home.AgentId)?.McsAgentId, dev.BotId) && devRow?.LastDeployedTime is not null && GitCommits.Match(headNow, head) && status.Count == 0, $"{label}: the restore wrote A's bot and the Dev deploy time; HEAD is unchanged and git status is clean", $"mcsAgentId={AmsMcsFiles.Agent(config, home.AgentId)?.McsAgentId} bot={dev.BotId} lastDeployedTime={devRow?.LastDeployedTime} head={GitCommits.Short(head)}->{GitCommits.Short(headNow)} status={GitTip.Describe(status)}");

        var held = expected.Where(row => AmsIds.Same(row.AgentType, AgentTypes.McsAgent) && AmsMcsFiles.RecordedBot(project, row.AgentId!) is not null).Select(row => new AmsFolder(project, row.AgentId!, row.AgentFolder!)).ToArray();
        var unbound = held.Where(agent => !IsBoundToItsDevBot(context, AmsMcsFiles.Connection(project, agent.Folder, agent.AgentId))).Select(agent => agent.Folder).ToArray();
        context.Assert.True(held.Any(agent => AmsIds.Same(agent.AgentId, home.AgentId)) && unbound.Length == 0 && initialized.Connected == held.Any(agent => AmsIds.Same(agent.AgentId, initialized.AgentId)), $"{label}: the restore connected each restored agent Dev holds, A among them: its folder's .mcs\\conn.json is bound to its Dev bot, and connected reads yes exactly when Dev holds the agent the result names", $"held={string.Join(',', held.Select(agent => agent.Folder))} unbound={string.Join(',', unbound)} connected={initialized.Connected} agent={initialized.AgentId} warnings={string.Join(" | ", initialized.Ams?.Warnings ?? [])}");

        foreach (var agent in held)
        {
            await AssertPullUpToDateAsync(context, $"{label}: {agent.Folder}, at once", agent).ConfigureAwait(false);
        }
    }

    public static async Task<AmsFolder> TeammateAsync(ScenarioContext context, string label, AmsFolder agent, string repositoryId, IReadOnlyList<string> agentIds, string? notConnected = null)
    {
        var mate = Folder(context, "mate");
        var restored = await InitAsync(context, "a teammate's agent init --repository-id into a missing folder", mate, "--repository-id", repositoryId).ConfigureAwait(false);
        var connection = AmsMcsFiles.Connection(mate, agent.Folder, agent.AgentId);
        var expected = notConnected is null ? IsBoundToItsDevBot(context, connection) : AmsMcsFiles.CacheFingerprint(mate, agent.Folder) is null && AmsMcsText.Mentions(restored.Ams?.Warnings, $"{notConnected}, so msagent didn't connect the folder to Dev");
        context.Assert.True(restored.Status == "agent-initialized" && AmsMcsResults.Restores(restored.Ams, agentIds) && File.Exists(AmsMcsFiles.SettingsPath(mate, agent.Folder)) && expected, notConnected is null ? $"(setup) the teammate's folder holds {label}, restored from the repository, and the init connected it: .mcs\\conn.json is bound to the Dev bot" : $"(setup) the teammate's folder holds {label}, restored from the repository, and no .mcs: the init's notice says the commit Dev holds {notConnected}, so it didn't connect the folder", $"status={restored.Status} restored={string.Join(',', AmsMcsResults.RestoredIds(restored.Ams))} expected={string.Join(',', agentIds)} {connection.Describe()} warnings={string.Join(" | ", restored.Ams?.Warnings ?? [])}");
        return agent with { Project = mate };
    }

    public static async Task DisconnectAsync(ScenarioContext context, AmsFolder agent)
    {
        var git = Services(context).Git;
        Directory.Delete(Path.Combine(agent.Project, agent.Folder, WorkflowStateCache.FolderName), recursive: true);

        foreach (var shelf in GitCli.ReadShelves(agent.Project).Where(shelf => shelf.Names(agent.Folder)))
        {
            Directory.Delete(shelf.KeyDirectory, recursive: true);
        }

        await git.RestoreAsync(agent.Project, [agent.Folder], context.CancellationToken).ConfigureAwait(false);
        var status = await git.StatusAsync(agent.Project, [agent.Folder], context.CancellationToken).ConfigureAwait(false);
        context.Assert.True(AmsMcsFiles.CacheFingerprint(agent.Project, agent.Folder) is null && !GitCli.ReadShelves(agent.Project).Any(shelf => shelf.Names(agent.Folder)) && status.Count == 0, "(setup) the teammate's folder is a plain clone's again, as when init's connect fails: no .mcs, no copy of one, and nothing uncommitted", $"{McsShelf.Describe(GitCli.ShelfOf(agent.Project, agent.Folder))} git status: {GitTip.Describe(status)}");
    }

    public static async Task<AmsOffScaffold> AmsOffScaffoldAsync(ScenarioContext context, string label, bool workflow = false)
    {
        var ams = Services(context);
        var project = Folder(context, label);
        var name = Name(context, label);
        var created = await RecordedAsync<ManagedAgentCreateResult>(context, ams.Off, $"agent create {name} with AMS off", project, workflow ? WorkflowArguments(context, name, project) : ScaffoldArguments(context, name, project), LongCall).ConfigureAwait(false);
        return new AmsOffScaffold(project, name, AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(project), created.AgentId)!.AgentLocation, created);
    }

    public static async Task<AmsOffSource> AmsOffSourceAsync(ScenarioContext context, string label, bool workflow = false)
    {
        var ams = Services(context);
        var scaffold = await AmsOffScaffoldAsync(context, label, workflow).ConfigureAwait(false);
        var dev = await DeploymentCreateAsync(context, ams.Off, scaffold.Project, scaffold.Created.AgentId, "development", "dev", null).ConfigureAwait(false);
        var folder = Path.Combine(scaffold.Project, scaffold.Folder);
        var kept = AmsMcsFiles.SyncFilesHash(folder);
        var deployed = await DeployAsync(context, ams.Off, scaffold.Project, scaffold.Created.AgentId, dev.DeploymentId).ConfigureAwait(false);
        AssertSyncFilesKept(context, $"the AMS-off deploy of '{scaffold.Name}'", folder, kept);
        var target = workflow ? deployed.McsWorkflowId ?? scaffold.Created.McsWorkflowId : deployed.TargetMcsAgentId;
        context.Assert.True(Guid.TryParse(target, out _) && AmsMcsText.AmsWarnings(deployed.Warnings).Count == 0, $"the AMS-off deploy made the {(workflow ? "workflow" : "bot")} in Dev, with no AMS warning", $"target={target} warnings={string.Join(" | ", deployed.Warnings)}");
        RecordFlows(context, workflow ? deployed.WorkflowName ?? scaffold.Created.WorkflowName : null, ams.Config.DevEnvironmentId);
        return new AmsOffSource(scaffold.Project, scaffold.Created.AgentId, scaffold.Name, scaffold.Folder, deployed.SchemaName ?? scaffold.Created.SchemaName, workflow ? null : target, workflow ? target : null, scaffold.Created, dev, deployed);
    }

    public static Task<AmsHome> HomeAsync(ScenarioContext context) => Services(context).Pool.Setup<AmsHome>(HomeSetup).GetAsync(() => NewProjectAsync(context, "home", "Home A", workflow: false));

    public static Task<AmsHome> WorkflowsAsync(ScenarioContext context) => Services(context).Pool.Setup<AmsHome>(WorkflowsSetup).GetAsync(() => NewProjectAsync(context, "workflows", "Flow", workflow: true));

    public static Task<AmsDevAgent> RemoteAsync(ScenarioContext context) => DevAgentAsync(context, RemoteSetup, "remote", "Connect");

    public static Task<AmsDevAgent> CopiesAsync(ScenarioContext context) => DevAgentAsync(context, CopiesSetup, "copies", "Shelf");

    public static Task<AmsHomeAgent> AgentBAsync(ScenarioContext context) => Services(context).Pool.Setup<AmsHomeAgent>(AgentBSetup).GetAsync(async () =>
    {
        var home = await HomeAsync(context).ConfigureAwait(false);
        await SyncAsync(context, home.Project).ConfigureAwait(false);
        var mainBefore = await RemoteMainAsync(context, home.Project).ConfigureAwait(false);
        var name = Name(context, "Home B");
        var created = await CreateAsync(context, Services(context).On, home.Project, name).ConfigureAwait(false);
        return new AmsHomeAgent(created.AgentId, name, Registered(context, created).Folder, mainBefore, created);
    });

    public static Task<AmsHomeDeployments> DeploymentsAsync(ScenarioContext context) => Services(context).Pool.Setup<AmsHomeDeployments>(DeploymentsSetup).GetAsync(async () =>
    {
        var ams = Services(context);
        var home = await HomeAsync(context).ConfigureAwait(false);
        var dev = await DeploymentCreateAsync(context, ams.On, home.Project, home.AgentId, "development", "dev", null).ConfigureAwait(false);
        var test = await DeploymentCreateAsync(context, ams.On, home.Project, home.AgentId, "test-ring", "test", ams.Config.TestEnvironmentId).ConfigureAwait(false);
        var prod = await DeploymentCreateAsync(context, ams.On, home.Project, home.AgentId, "production", "prod", ams.Config.ProdEnvironmentId).ConfigureAwait(false);
        return new AmsHomeDeployments(dev.DeploymentId, test.DeploymentId, prod.DeploymentId, "test-ring");
    });

    public static Task<AmsDevDeploy> DevDeployedAsync(ScenarioContext context) => Services(context).Pool.Setup<AmsDevDeploy>(DevDeploySetup).GetAsync(async () =>
    {
        var ams = Services(context);
        var home = await HomeAsync(context).ConfigureAwait(false);
        var deployments = await DeploymentsAsync(context).ConfigureAwait(false);
        await SyncAsync(context, home.Project).ConfigureAwait(false);
        var head = await ams.Git.RevParseAsync(home.Project, "HEAD", context.CancellationToken).ConfigureAwait(false);
        var timeBefore = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(home.Project), deployments.Dev)?.LastDeployedTime;
        var deployed = await DeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev).ConfigureAwait(false);
        context.Assert.True(Guid.TryParse(deployed.TargetMcsAgentId, out _) && AmsMcsText.AmsWarnings(deployed.Warnings).Count == 0, "the local Dev deploy made the bot in Dev, and AMS took its record without a warning", $"bot={deployed.TargetMcsAgentId} created={deployed.TargetAgentCreated} warnings={string.Join(" | ", deployed.Warnings)}");
        await AssertCleanAfterDeployAsync(context, home.Project, home.Folder).ConfigureAwait(false);
        var slot = await SlotAsync(context, home.Project, home.AgentId, deployments.Dev).ConfigureAwait(false);
        context.Assert.True(string.Equals(slot?.State, "Deployed", StringComparison.OrdinalIgnoreCase) && GitCommits.Match(slot?.LastDeployedCommitSha, head), $"AMS records the exact deploy: Dev is Deployed at HEAD {head}", $"state={slot?.State} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var config = AmsMcsFiles.ReadAgentConfig(home.Project);
        var timeAfter = AmsMcsFiles.Deployment(config, deployments.Dev)?.LastDeployedTime;
        context.Assert.True(AmsIds.Same(AmsMcsFiles.Agent(config, home.AgentId)?.McsAgentId, deployed.TargetMcsAgentId) && AmsMcsResults.Later(timeAfter, timeBefore), ".config holds the bot and a new Dev deploy time", $"mcsAgentId={AmsMcsFiles.Agent(config, home.AgentId)?.McsAgentId} lastDeployedTime={timeBefore}->{timeAfter}");
        var projection = await CaptureAsync(context, home.Project, home.AgentId).ConfigureAwait(false);
        context.Assert.True(projection.Status is "captured" or "unchanged", "the capture after the deploy is captured or unchanged", $"status={projection.Status} commit={projection.CommitSha} base={projection.BaseCommitSha}");
        var main = await RemoteMainAsync(context, home.Project).ConfigureAwait(false);

        if (projection.Status == "captured")
        {
            await ams.Git.PullAsync(home.Project, Branch, context.CancellationToken).ConfigureAwait(false);
            var headNow = await ams.Git.RevParseAsync(home.Project, "HEAD", context.CancellationToken).ConfigureAwait(false);
            context.Assert.True(GitCommits.Match(projection.CommitSha, main) && GitCommits.Match(projection.BaseCommitSha, head) && GitCommits.Match(headNow, projection.CommitSha), "captured: the projection commit is GRS main, based on the deployed commit, and git pull brought it", $"commit={projection.CommitSha} base={projection.BaseCommitSha} deployed={head} main={main} head={headNow}");
        }
        else
        {
            var slotNow = await SlotAsync(context, home.Project, home.AgentId, deployments.Dev).ConfigureAwait(false);
            context.Assert.True(GitCommits.Match(main, head) && GitCommits.Match(slotNow?.LastDeployedCommitSha, head), "unchanged: no commit was made; GRS main and AMS's Dev still hold the deployed commit", $"main={main} lastDeployedCommitSha={slotNow?.LastDeployedCommitSha} deployed={head}");
        }

        return new AmsDevDeploy(deployed.TargetMcsAgentId!, head!, projection.Status, projection.Status == "captured" ? projection.CommitSha : null);
    });

    public static Task<AmsTestPublished> TestPublishedAsync(ScenarioContext context) => Services(context).Pool.Setup<AmsTestPublished>(TestPublishedSetup).GetAsync(async () =>
    {
        var ams = Services(context);
        var home = await HomeAsync(context).ConfigureAwait(false);
        var deployments = await DeploymentsAsync(context).ConfigureAwait(false);
        await DevDeployedAsync(context).ConfigureAwait(false);
        await SyncAsync(context, home.Project).ConfigureAwait(false);
        var name = Name(context, "Tested");
        var commit = await CommitDisplayNameAsync(context, home.Project, home.Folder, name).ConfigureAwait(false);
        await RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev).ConfigureAwait(false);
        var test = await RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Test).ConfigureAwait(false);
        context.Assert.True(test.Published == true && GitCommits.Match(test.CommitSha, commit), "Test deployed and published the new head", $"commitSha={test.CommitSha} published={test.Published}");
        return new AmsTestPublished(test.CommitSha!, name);
    });

    private static AmsAgentRegistration Registration(ScenarioContext context, AmsAgentRegistration? registration, string check, string status)
    {
        context.Assert.True(registration is not null, check, $"status={status}");
        return registration!;
    }

    private static string[] CloneCommand(ScenarioContext context, string[] clone, string project, string[] extra)
    {
        var dev = Services(context).Config.DevEnvironmentId!;

        return ["agent", "create", .. clone, "--mcs-clone-environment-id", dev, "--environment-id", dev, "--project", project, .. extra];
    }

    private static bool IsBoundToItsDevBot(ScenarioContext context, AmsConnectionFacts connection) => connection.BindsTo(connection.RecordedBot, Services(context).Config.DevEnvironmentId, Services(context).DevOrganization);

    private static void AssertGrsInitialParent(ScenarioContext context, GitCloneFacts clone) => context.Assert.True(clone.ParentIsGrsInitialCommit, $"the add commit's parent is GRS's initial commit: by {GitCloneFacts.GrsAuthor}, with no parent, holding only {GitCloneFacts.GrsReadme}", $"parent={clone.Parent?.Sha} author={clone.Parent?.AuthorEmail} itsParents={string.Join(' ', clone.Parent?.Parents ?? [])} files={string.Join(',', clone.ParentTree ?? [])}");

    private static void AssertRenamed(ScenarioContext context, string what, string id, string? actual, string name) => context.Assert.True(string.Equals(actual, name, StringComparison.Ordinal), $"the Dev {what} is renamed to '{name}' in Dataverse (a portal edit)", $"{what}={id} name='{actual}'");

    private static void AssertPushedMain(ScenarioContext context, GitCloneFacts clone, string? commitSha) => context.Assert.True(clone.PushedMain(commitSha), "GRS main is the pushed commit", $"main={clone.RemoteMain} commitSha={commitSha}");

    private static async Task<AmsHome> NewProjectAsync(ScenarioContext context, string directory, string label, bool workflow)
    {
        var ams = Services(context);
        var project = Path.Combine(ams.WorkspaceRoot!, directory);
        var name = Name(context, label);
        context.Ledger.RecordLocalWorkspace(project, context.ScenarioId);
        var created = workflow ? await CreateWorkflowAsync(context, project, name).ConfigureAwait(false) : await CreateAsync(context, ams.On, project, name).ConfigureAwait(false);
        var registration = Registered(context, created);
        return new AmsHome(project, created.AgentId, name, registration.Folder, registration.RepositoryId, created);
    }

    private static Task<AmsDevAgent> DevAgentAsync(ScenarioContext context, string setup, string directory, string label) => Services(context).Pool.Setup<AmsDevAgent>(setup).GetAsync(async () =>
    {
        var created = await NewProjectAsync(context, directory, label, workflow: false).ConfigureAwait(false);
        var dev = await DeploymentCreateAsync(context, Services(context).On, created.Project, created.AgentId, "development", "dev", null).ConfigureAwait(false);
        return new AmsDevAgent(created.Project, created.AgentId, created.Name, created.Folder, created.RepositoryId, created.Created, dev.DeploymentId);
    });
}
