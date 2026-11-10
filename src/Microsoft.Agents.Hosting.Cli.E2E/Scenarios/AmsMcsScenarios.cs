using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

public static class AmsMcsScenarios
{
    private static readonly ScenarioBuilder Builder = new(AmsMcsServices.Feature);

    private static readonly Capability[] Ring = [Capability.AmsMcs, Capability.AmsMcsRing, Capability.AmsMcsWorkspaceRoot];

    private static readonly Capability[] RingOracle = [.. Ring, Capability.AmsMcsOracle];

    private static readonly Capability[] Home = [.. Ring, Capability.Git];

    private static readonly Capability[] HomeOracle = [.. Home, Capability.AmsMcsOracle];

    private static readonly string[] LocalState = [WorkflowStateCache.FolderName, ".git"];

    public static IReadOnlyDictionary<string, AmsRepositoryUse> Repositories { get; } = new Dictionary<string, AmsRepositoryUse>(StringComparer.Ordinal)
    {
        ["AM-02"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-03"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-04"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-06"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-07"] = new([AmsMcsSteps.WorkflowsSetup], 0),
        ["AM-08"] = new([], 2),
        ["AM-09"] = new([AmsMcsSteps.HomeSetup], 1),
        ["AM-10"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-11"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-12"] = new([AmsMcsSteps.HomeSetup], 1),
        ["AM-13"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-14"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-15"] = new([AmsMcsSteps.WorkflowsSetup], 0),
        ["AM-16"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-18"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-19"] = new([AmsMcsSteps.RemoteSetup], 1),
        ["AM-20"] = new([AmsMcsSteps.CopiesSetup], 0),
        ["AM-21"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-25"] = new([AmsMcsSteps.RemoteSetup], 0),
        ["AM-26"] = new([AmsMcsSteps.CopiesSetup], 0),
        ["AM-28"] = new([AmsMcsSteps.HomeSetup], 0),
        ["AM-29"] = new([], 1),
        ["AM-32"] = new([], 2),
        ["AM-33"] = new([], 1),
        ["AM-35"] = new([AmsMcsSteps.WorkflowsSetup], 0),
        ["AM-36"] = new([], 1),
        ["AM-37"] = new([AmsMcsSteps.HomeSetup, AmsMcsSteps.WorkflowsSetup], 0),
    };

    public static IEnumerable<Scenario> All() =>
    [
        FlagOff(),
        CreateIntoAnEmptyFolder(),
        SecondAgentInTheProject(),
        CreateWithRepositoryId(),
        RepositoryGrsDoesNotHave(),
        CloneIntoTheHomeProject(),
        Workflows(),
        Init(),
        Deployments(),
        LocalDevDeployAndCapture(),
        RemoteDevDeploys(),
        RemoteTest(),
        RemoteProd(),
        Connections(),
        Teammate(),
        Delete(),
        PullWithAmsOn(),
        PullAfterRemoteDeploy(),
        CopiesPerCommit(),
        CreateOrder(),
        RemoteDeployCases(),
        CopyCases(),
        AgentsAndWorkflows(),
        CacheInHistory(),
        UnknownBot(),
        ConnectorEdit(),
        WorkflowEdit(),
        CloneOfAHeldBot(),
        CloneIntoAnotherRepository(),
    ];

    private static Scenario FlagOff() => Builder.Make("AM-01", "the flag off: the AMS-only forms are unknown, and create, deploy, pull, init and delete are main's", RingOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var gate = AmsMcsSteps.Folder(context, "gate");
        var repositoryId = Guid.NewGuid().ToString();

        foreach (var (form, arguments) in new (string Form, string[] Arguments)[] { ("agent deploy --remote", ["agent", "deploy", "--remote", "--project", gate, "--agent-id", Guid.NewGuid().ToString()]), ("agent capture", ["agent", "capture", "--project", gate, "--agent-id", Guid.NewGuid().ToString()]), ("agent operation show", ["agent", "operation", "show", "--operation-id", Guid.NewGuid().ToString(), "--project", gate]), ("agent list --view ams", ["agent", "list", "--view", "ams", "--environment-id", ams.Config.DevEnvironmentId!]), ("agent create --repository-id", AmsMcsSteps.ScaffoldArguments(context, AmsMcsSteps.Name(context, "Gate"), gate, "--repository-id", repositoryId)), ("agent init --repository-id", ["agent", "init", "--agent-name", AmsMcsSteps.Name(context, "Gate"), "--mcs-agent-source", gate, "--repository-id", repositoryId]) })
        {
            await AmsMcsSteps.UnknownAsync(context, ams.Off, form, arguments);
        }

        context.Assert.True(AmsMcsFiles.IsMissingOrEmpty(gate), "the unknown forms wrote nothing", gate);

        var main = await AmsMcsSteps.AmsOffSourceAsync(context, "Main");
        var agent = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(main.Project), main.AgentId);
        context.Assert.True(main.Created.Ams is null && !Path.Exists(Path.Combine(main.Project, ".git")) && !Path.Exists(Path.Combine(main.Project, ".gitignore")) && agent is not null && agent.SourceControlMetadata is null, "main's create: no ams in the result, no .git or .gitignore, no sourceControlMetadata in .config", $"ams={main.Created.Ams is not null} agent={agent is not null}");
        context.Assert.True((main.Dev.Warnings ?? []).Count == 0, "deployment create gives no AMS warning", string.Join(" | ", main.Dev.Warnings ?? []));
        context.Assert.True(main.Deployed.TargetAgentCreated, "main's deploy created the bot", $"bot={main.BotId} created={main.Deployed.TargetAgentCreated}");

        var pulled = await AmsMcsSteps.PullAsync(context, ams.Off, main.Project, main.AgentId);
        context.Assert.True(pulled.Status is "agent-pulled" or "up-to-date", "main's pull: agent-pulled or up-to-date", $"status={pulled.Status}");

        var initProject = AmsMcsSteps.Folder(context, "init");
        FixtureCopies.CopyTree(Path.Combine(main.Project, main.Folder), Path.Combine(initProject, main.Folder), keepLocalState: false);
        var initialized = await AmsMcsSteps.RunAsync<ManagedAgentInitResult>(context, ams.Off, "agent init with AMS off", ["agent", "init", "--agent-name", AmsMcsSteps.Name(context, "Main Init"), "--mcs-agent-source", initProject]);
        context.Assert.True(initialized.Status == "agent-initialized" && initialized.Ams is null && !Path.Exists(Path.Combine(initProject, ".git")), "main's init: agent-initialized, no ams in the result, no .git", $"status={initialized.Status} ams={initialized.Ams is not null}");

        var deleted = await AmsMcsSteps.DeleteAsync(context, ams.Off, "agent delete with AMS off", main.Project, main.AgentId, "--deployment-id", main.Dev.DeploymentId);
        var bots = await ams.Dataverse.BotsNamedAsync(ams.Config.DevEnvironmentId!, main.SchemaName!, context.CancellationToken);
        context.Assert.True(deleted.Status == "agent-deleted" && deleted.AmsRecordDeleted is null && bots.Count == 0, "main's delete: agent-deleted, no amsRecordDeleted, and the bot is gone from Dev", $"status={deleted.Status} amsRecordDeleted={deleted.AmsRecordDeleted} bots={bots.Count}");

        var listed = await AmsMcsSteps.AmsAgentsAsync(context, null);
        context.Assert.True(AmsMcsResults.Holding(listed, main.AgentId).Count + AmsMcsResults.Holding(listed, initialized.AgentId).Count == 0, "AMS holds no record of either agent", $"rows={listed.Agents.Count}");
    });

    private static Scenario CreateIntoAnEmptyFolder() => Builder.Make("AM-02", "agent create into an empty folder: a new repository on GRS's initial commit, the same ids in AMS, and agent show", Home, SafetyTier.Creating, smoke: true, run: async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var created = home.Created.Ams!;
        context.Assert.True(home.Created.Status == "agent-created" && created.Registered && created.RepositoryCreated && created.Pushed && AmsIds.Same(created.Branch, AmsMcsSteps.Branch) && GitCommits.IsSha(created.CommitSha) && Guid.TryParse(created.RepositoryId, out _) && created.Folder.Length > 0, "the result: agent-created, registered, a new repository, pushed to main", $"repository={created.RepositoryId} folder={created.Folder} commit={created.CommitSha} pushed={created.Pushed} branch={created.Branch}");
        context.Assert.True(GrsUrls.Names(created.GitUrl, ams.Config.GitHost, context.Config.TenantId, ams.Config.DevEnvironmentId, created.RepositoryId), "the git url is GRS's route for the repository in Dev", created.GitUrl);

        var metadata = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(home.Project), home.AgentId)?.SourceControlMetadata;
        context.Assert.True(AmsIds.Same(metadata?.RepositoryId, home.RepositoryId) && AmsIds.Same(metadata?.Branch, AmsMcsSteps.Branch) && AmsIds.Same(metadata?.Directory, home.Folder), ".config records the repository, main and the folder", $"repository={metadata?.RepositoryId} branch={metadata?.Branch} directory={metadata?.Directory}");

        var listed = await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId);
        context.Assert.True(listed.Agents.Count == 1 && AmsMcsResults.Holding(listed, home.AgentId, home.Folder).Count == 1 && AmsIds.Same(listed.Agents[0].RepositoryId, home.RepositoryId), "AMS holds the agent, with the same id and folder", AmsMcsResults.Describe(listed));

        await AmsMcsSteps.AssertCloneAsync(context, home.Project, created.GitUrl, created.CommitSha, $"msagent: add MCSAgent {home.Name}", [home.Folder], null, newRepository: true);

        var details = AmsMcsResults.Details(await AmsMcsSteps.ShowAsync(context, home.Project, home.AgentId), home.AgentId);
        context.Assert.True(AmsIds.Same(details?.RepositoryId, home.RepositoryId) && AmsIds.Same(details?.Folder, home.Folder) && AmsIds.Same(details?.GitUrl, created.GitUrl), "agent show reports AMS's repository, folder and git url", $"repositoryId={details?.RepositoryId} folder={details?.Folder} gitUrl={details?.GitUrl}");

        var text = await AmsMcsSteps.ShowTextAsync(context, home.Project, home.AgentId);
        context.Assert.True(AmsIds.Same(AmsMcsText.Word(text.StdOut, "repositoryId"), home.RepositoryId) && AmsIds.Same(AmsMcsText.Value(text.StdOut, "folder"), home.Folder) && AmsIds.Same(AmsMcsText.Word(text.StdOut, "gitUrl"), created.GitUrl), "agent show's text names the repository, folder and git url", $"repositoryId={AmsMcsText.Word(text.StdOut, "repositoryId")} folder={AmsMcsText.Value(text.StdOut, "folder")} gitUrl={AmsMcsText.Word(text.StdOut, "gitUrl")}");
    });

    private static Scenario SecondAgentInTheProject() => Builder.Make("AM-03", "a second agent in the home project joins its repository, on the previous main", Home, SafetyTier.Creating, async context =>
    {
        await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var agentB = await AmsMcsSteps.AgentBAsync(context);
        var created = agentB.Created.Ams!;
        context.Assert.True(agentB.Created.Status == "agent-created" && AmsIds.Same(created.RepositoryId, home.RepositoryId) && !created.RepositoryCreated && created.Registered && created.Pushed && created.Folder.Length > 0 && !AmsIds.Same(created.Folder, home.Folder) && GitCommits.IsSha(created.CommitSha), "B joined the same repository in a new folder, registered and pushed", $"repository={created.RepositoryId} created={created.RepositoryCreated} folder={created.Folder} commit={created.CommitSha}");

        await AmsMcsSteps.AssertCloneAsync(context, home.Project, created.GitUrl, created.CommitSha, $"msagent: add MCSAgent {agentB.Name}", [agentB.Folder], agentB.MainBefore, newRepository: false);

        var listed = await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId);
        context.Assert.True(AmsMcsResults.Holding(listed, home.AgentId, home.Folder).Count == 1 && AmsMcsResults.Holding(listed, agentB.AgentId, agentB.Folder).Count == 1, "AMS lists A and B in the repository, each with its folder", AmsMcsResults.Describe(listed));

        var metadata = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(home.Project), agentB.AgentId)?.SourceControlMetadata;
        context.Assert.True(AmsIds.Same(metadata?.RepositoryId, home.RepositoryId) && AmsIds.Same(metadata?.Directory, agentB.Folder), ".config records B in the same repository with its folder", $"repository={metadata?.RepositoryId} directory={metadata?.Directory}");
    });

    private static Scenario CreateWithRepositoryId() => Builder.Make("AM-04", "agent create --repository-id into a new folder clones and pushes; answering n writes nothing", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var before = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var join = AmsMcsSteps.Folder(context, "join");
        var name = AmsMcsSteps.Name(context, "Join");
        var joined = await AmsMcsSteps.CreateAsync(context, ams.On, join, name, "--repository-id", home.RepositoryId);
        var created = AmsMcsSteps.Registered(context, joined);
        context.Assert.True(joined.Status == "agent-created" && AmsIds.Same(created.RepositoryId, home.RepositoryId) && created is { RepositoryCreated: false, Registered: true, Pushed: true }, "the result: the existing repository, registered and pushed", $"repository={created.RepositoryId} created={created.RepositoryCreated} pushed={created.Pushed}");
        context.Assert.True(AmsMcsFiles.ReadSettings(Path.Combine(join, home.Folder)) is not null, "the new folder is a clone of the repository, with A's folder in it", Path.Combine(join, home.Folder));

        await AmsMcsSteps.AssertCloneAsync(context, join, created.GitUrl, created.CommitSha, $"msagent: add MCSAgent {name}", [created.Folder], before, newRepository: false);

        var agents = AmsMcsFiles.ReadAgentConfig(join)?.ManagedAgents ?? [];
        context.Assert.True(agents.Count == 1 && AmsIds.Same(agents[0].AgentId, joined.AgentId), "the new folder's .config holds only the new agent", string.Join(", ", agents.Select(agent => agent.AgentId)));

        var decline = AmsMcsSteps.Folder(context, "decline");
        var declineName = AmsMcsSteps.Name(context, "Decline");
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var countBefore = (await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId)).Agents.Count;
        var answered = await ams.On.RunAsync([.. AmsMcsSteps.ScaffoldArguments(context, declineName, decline, "--repository-id", home.RepositoryId), "--json"], ams.WorkspaceRoot, stdin: "n", timeout: AmsMcsServices.CallTimeout, cancellationToken: context.CancellationToken);
        var outcome = ams.Record(context.Ledger, answered, decline, amsOff: false, context.ScenarioId);
        context.Assert.True(outcome.Status == "cancelled" && answered.ExitCode == 0 && AmsMcsText.AsksToClone(answered.StdErr, declineName), "msagent asked to clone and add the agent; n cancels: status cancelled, exit 0", $"status={outcome.Status} exit={answered.ExitCode} stderr={answered.StdErr.Trim()}");

        var countAfter = (await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId)).Agents.Count;
        var mainAfter = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        context.Assert.True(AmsMcsFiles.IsMissingOrEmpty(decline) && countAfter == countBefore && AmsIds.Same(mainAfter, mainBefore), "no wrote nothing: the folder is missing or empty, AMS and GRS main are unchanged", $"agents={countBefore}->{countAfter} main={mainBefore}->{mainAfter}");
    });

    private static Scenario RepositoryGrsDoesNotHave() => Builder.Make("AM-05", "agent create with a repository GRS doesn't have fails with RepositoryNotFound and writes nothing", Ring, SafetyTier.ReadOnly, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var project = AmsMcsSteps.Folder(context, "missing");
        var name = AmsMcsSteps.Name(context, "Missing");
        var result = await AmsMcsSteps.RawAsync(context, ams.On, AmsMcsSteps.ScaffoldArguments(context, name, project, "--repository-id", Guid.NewGuid().ToString()), AmsMcsSteps.LongCall);
        ams.Record(context.Ledger, result, project, amsOff: false, context.ScenarioId);
        context.Assert.Refused(result, 19010, "agent create with a repository id GRS doesn't have fails with RepositoryNotFound (19010)");

        var named = AmsMcsResults.Named(await AmsMcsSteps.AmsAgentsAsync(context, null), name);
        context.Assert.True(AmsMcsFiles.IsMissingOrEmpty(project) && named.Count == 0, "nothing was written: the folder is missing or empty, and AMS has no agent of that name", $"rows={named.Count}");
    });

    private static Scenario CloneIntoTheHomeProject() => Builder.Make("AM-06", "a clone of a Dev bot inside the home project joins its repository; capture and a remote Dev deploy work; a second clone fails with AlreadyRegistered before any question, naming the first", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var source = await AmsMcsSteps.AmsOffSourceAsync(context, "Source");
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var created = await AmsMcsSteps.CloneAsync(context, source, home.Project);
        var registration = AmsMcsSteps.Registered(context, created);
        string[] warnings = [.. created.Warnings, .. registration.Warnings ?? []];
        context.Assert.True(created.CreatedFrom == "clone" && AmsIds.Same(created.McsAgentId, source.BotId) && created.DeploymentType == "dev" && Guid.TryParse(created.DeploymentId, out _) && registration is { Registered: true, RepositoryCreated: false, Pushed: true } && AmsIds.Same(registration.RepositoryId, home.RepositoryId) && AmsMcsText.AmsWarnings(warnings).Count == 0, "a clone of the bot with a Dev deployment, which joins R (repositoryCreated false) and is pushed, without an AMS warning", $"createdFrom={created.CreatedFrom} mcsAgentId={created.McsAgentId} deployment={created.DeploymentId} ({created.DeploymentType}) repository={registration.RepositoryId} created={registration.RepositoryCreated} pushed={registration.Pushed} warnings={string.Join(" | ", warnings)}");

        var agent = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(home.Project), created.AgentId);
        context.Assert.True(AmsIds.Same(agent?.McsAgentId, source.BotId) && AmsIds.Same(agent?.SourceControlMetadata?.RepositoryId, home.RepositoryId) && AmsIds.Same(agent?.SourceControlMetadata?.Directory, registration.Folder), ".config binds the bot and the repository, with the clone's folder", $"mcsAgentId={agent?.McsAgentId} repository={agent?.SourceControlMetadata?.RepositoryId} directory={agent?.SourceControlMetadata?.Directory}");
        await AmsMcsSteps.AssertCloneAsync(context, home.Project, registration.GitUrl, registration.CommitSha, $"msagent: add MCSAgent {created.DisplayName}", [registration.Folder], mainBefore, newRepository: false);

        var slot = await AmsMcsSteps.SlotAsync(context, home.Project, created.AgentId, created.DeploymentId!);
        context.Assert.True(slot?.State is not null && GitCommits.Match(slot.LastDeployedCommitSha, registration.CommitSha), "AMS's Dev deployment holds the clone's pushed commit (the cloud base)", $"state={slot?.State} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var rows = AmsMcsResults.Holding(await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId), created.AgentId, registration.Folder);
        context.Assert.True(rows.Count == 1 && AmsIds.Same(rows[0].HomeDeploymentId, created.DeploymentId), "AMS lists the clone with its folder and its Dev deployment as the home deployment", string.Join(", ", rows.Select(row => $"{row.AgentFolder} homeDeploymentId={row.HomeDeploymentId}")));

        var capture = await AmsMcsSteps.CaptureAsync(context, home.Project, created.AgentId);
        context.Assert.True(capture.Status is "captured" or "unchanged" && (string.IsNullOrEmpty(capture.BaseCommitSha) || GitCommits.Match(capture.BaseCommitSha, registration.CommitSha)), "agent capture right after the clone knows the base: captured or unchanged, based on the clone's commit", $"status={capture.Status} base={capture.BaseCommitSha} commit={capture.CommitSha}");
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var remote = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, created.AgentId, created.DeploymentId!);
        var main = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, created.AgentId, created.DeploymentId!);
        context.Assert.True(remote.Status == "deployed" && GitCommits.Match(remote.CommitSha, main) && GitCommits.Match(slot?.LastDeployedCommitSha, main), "a remote Dev deploy without --overwrite deployed GRS main, which AMS records", $"commitSha={remote.CommitSha} main={main} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");

        await AmsMcsSteps.SyncAsync(context, home.Project);
        var configBefore = File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(home.Project));
        var statusBefore = await ams.Git.StatusAsync(home.Project, [], context.CancellationToken);
        var entriesBefore = AmsMcsFiles.TopEntries(home.Project);
        var countBefore = (await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId)).Agents.Count;
        await AmsMcsSteps.RefusedBeforeQuestionAsync(context, home.Project, AmsMcsSteps.CloneArguments(context, source, home.Project, "--agent-name", AmsMcsSteps.Name(context, "Second")), 10009, "a second clone of the bot into P, under a name of its own, fails with AlreadyRegistered (10009, exit 2) before any question, naming the first clone, which P's .config holds", $"already registered as {created.DisplayName} ({created.AgentId})");
        var countAfter = (await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId)).Agents.Count;
        var statusAfter = await ams.Git.StatusAsync(home.Project, [], context.CancellationToken);
        context.Assert.True(File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(home.Project)).SequenceEqual(configBefore) && statusAfter.SequenceEqual(statusBefore) && AmsMcsFiles.TopEntries(home.Project) == entriesBefore && countAfter == countBefore, "the refused clone was undone: P's .config, git status and top-level entries, and the number of AMS agents in R, are unchanged", $"entries={AmsMcsFiles.TopEntries(home.Project)} agents={countBefore}->{countAfter} git status: {GitTip.Describe(statusAfter)}");
    });

    private static Scenario Workflows() => Builder.Make("AM-07", "a workflow scaffolded into a new repository W, and a clone of a Dev workflow that joins W; AMS lists both, and a remote deploy connects no folder; a second clone into W's project joins the first clone", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var source = await AmsMcsSteps.AmsOffSourceAsync(context, "FlowSource", workflow: true);
        var flow = await ams.Dataverse.WorkflowAsync(ams.Config.DevEnvironmentId!, source.WorkflowId!, includeDefinition: false, context.CancellationToken);
        context.Assert.True(AmsIds.Same(flow?.WorkflowId, source.WorkflowId), "the source workflow exists in Dev", $"workflow={source.WorkflowId} name={flow?.Name}");

        var workflows = await AmsMcsSteps.WorkflowsAsync(context);
        var scaffold = workflows.Created.Ams!;
        context.Assert.True(workflows.Created.Status == "agent-created" && workflows.Created.AgentType == AgentTypes.McsWorkflow && scaffold is { Registered: true, RepositoryCreated: true, Pushed: true } && GrsUrls.Names(scaffold.GitUrl, ams.Config.GitHost, context.Config.TenantId, ams.Config.DevEnvironmentId, scaffold.RepositoryId), "a workflow scaffold registered in a new repository W and pushed, on GRS's route for it", $"agentType={workflows.Created.AgentType} repository={scaffold.RepositoryId} folder={scaffold.Folder} pushed={scaffold.Pushed} gitUrl={scaffold.GitUrl}");
        await AmsMcsSteps.AssertCloneAsync(context, workflows.Project, scaffold.GitUrl, scaffold.CommitSha, $"msagent: add MCSWorkflow {workflows.Name}", [workflows.Folder], null, newRepository: true);
        var listed = await AmsMcsSteps.AmsAgentsAsync(context, workflows.RepositoryId);
        context.Assert.True(listed.Agents.Count == 1 && AmsMcsResults.Holding(listed, workflows.AgentId, workflows.Folder, AgentTypes.McsWorkflow).Count == 1, "AMS holds the workflow with the same id, folder and type", AmsMcsResults.Describe(listed));

        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, workflows.Project);
        var cloneProject = AmsMcsSteps.Folder(context, "clone");
        var cloned = await AmsMcsSteps.CloneAsync(context, source, cloneProject, "--repository-id", workflows.RepositoryId);
        var registration = AmsMcsSteps.Registered(context, cloned);
        string[] warnings = [.. cloned.Warnings, .. registration.Warnings ?? []];
        context.Assert.True(cloned.CreatedFrom == "clone" && AmsIds.Same(cloned.McsWorkflowId, source.WorkflowId) && cloned.DeploymentType == "dev" && Guid.TryParse(cloned.DeploymentId, out _) && registration is { Registered: true, RepositoryCreated: false, Pushed: true } && AmsIds.Same(registration.RepositoryId, workflows.RepositoryId) && AmsMcsText.AmsWarnings(warnings).Count == 0, "a clone of the workflow with a Dev deployment, which joins W (repositoryCreated false) and is pushed, without an AMS warning", $"createdFrom={cloned.CreatedFrom} workflow={cloned.McsWorkflowId} deployment={cloned.DeploymentId} repository={registration.RepositoryId} created={registration.RepositoryCreated} warnings={string.Join(" | ", warnings)}");
        await AmsMcsSteps.AssertCloneAsync(context, cloneProject, registration.GitUrl, registration.CommitSha, $"msagent: add MCSWorkflow {cloned.DisplayName}", [registration.Folder], mainBefore, newRepository: false);
        var slot = await AmsMcsSteps.SlotAsync(context, cloneProject, cloned.AgentId, cloned.DeploymentId!);
        context.Assert.True(slot?.State is not null && GitCommits.Match(slot.LastDeployedCommitSha, registration.CommitSha), "AMS's Dev deployment holds the clone's pushed commit", $"state={slot?.State} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var capture = await AmsMcsSteps.CaptureAsync(context, cloneProject, cloned.AgentId);
        context.Assert.True(capture.Status is "captured" or "unchanged", "agent capture of the workflow clone knows the base: captured or unchanged", $"status={capture.Status} base={capture.BaseCommitSha}");
        await AmsMcsSteps.SyncAsync(context, cloneProject);
        var remote = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, cloneProject, cloned.AgentId, cloned.DeploymentId!);
        var main = await AmsMcsSteps.RemoteMainAsync(context, cloneProject);
        context.Assert.True(remote.Status == "deployed" && GitCommits.Match(remote.CommitSha, main), "a remote Dev deploy of the workflow without --overwrite deployed GRS main", $"commitSha={remote.CommitSha} main={main}");
        AmsMcsSteps.AssertNotConnected(context, "a workflow's remote Dev deploy", remote);
        listed = await AmsMcsSteps.AmsAgentsAsync(context, workflows.RepositoryId);
        context.Assert.True(AmsMcsResults.Holding(listed, workflows.AgentId, workflows.Folder, AgentTypes.McsWorkflow).Count == 1 && AmsMcsResults.Holding(listed, cloned.AgentId, registration.Folder, AgentTypes.McsWorkflow).Count == 1, "AMS lists both workflows in W, each with its folder and type", AmsMcsResults.Describe(listed));

        await AmsMcsSteps.SyncAsync(context, workflows.Project);
        mainBefore = await AmsMcsSteps.RemoteMainAsync(context, workflows.Project);
        var entriesBefore = AmsMcsFiles.TopEntries(workflows.Project);
        var agentsBefore = await AmsMcsSteps.AmsAgentIdsAsync(context, workflows.RepositoryId);
        var joined = await AmsMcsSteps.CloneAsync(context, source, workflows.Project, "--agent-name", AmsMcsSteps.Name(context, "Second"));
        var record = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(workflows.Project), cloned.AgentId);
        string[] joinWarnings = [.. joined.Warnings, .. joined.Ams?.Warnings ?? []];
        context.Assert.True(joined.Status == "joined" && AmsIds.Same(joined.AgentId, cloned.AgentId) && joined.Ams is { Joined: true, Registered: false } && AmsIds.Same(record?.SourceControlMetadata?.Directory, registration.Folder) && AmsIds.Same(record?.McsWorkflowId, source.WorkflowId), "a second clone of the workflow into W's project, which git pull gave the first clone's folder but not its record, joins the first clone: status joined, and W's .config gains its record, with its AMS id", $"status={joined.Status} agentId={joined.AgentId} first={cloned.AgentId} joined={joined.Ams?.Joined} directory={record?.SourceControlMetadata?.Directory} workflow={record?.McsWorkflowId} warnings={string.Join(" | ", joinWarnings)}");
        var mainAfter = await AmsMcsSteps.RemoteMainAsync(context, workflows.Project);
        var agentsAfter = await AmsMcsSteps.AmsAgentIdsAsync(context, workflows.RepositoryId);
        var status = await ams.Git.StatusAsync(workflows.Project, [], context.CancellationToken);
        context.Assert.True(agentsAfter == agentsBefore && GitCommits.Match(mainAfter, mainBefore) && status.Count == 0 && AmsMcsFiles.TopEntries(workflows.Project) == entriesBefore, "AMS lists the same agents in W, GRS main doesn't move, and git status stays clean", $"agents={agentsBefore} -> {agentsAfter} main={mainBefore}->{mainAfter} entries={AmsMcsFiles.TopEntries(workflows.Project)} git status: {GitTip.Describe(status)}");
    });

    private static Scenario Init() => Builder.Make("AM-08", "agent init: a new repository, an attach with --repository-id, refused attaches that leave the folder as it was, and a working tree with history", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var scaffoldN = await AmsMcsSteps.AmsOffScaffoldAsync(context, "Init N");
        var scaffoldA = await AmsMcsSteps.AmsOffScaffoldAsync(context, "Init A");

        var newProject = AmsMcsSteps.Folder(context, "new");
        AmsMcsFiles.CopyWithout(Path.Combine(scaffoldN.Project, scaffoldN.Folder), Path.Combine(newProject, scaffoldN.Folder), LocalState);
        var initN = await AmsMcsSteps.InitAsync(context, "agent init of a folder with no repository (a new repository)", newProject, "--agent-name", scaffoldN.Name);
        var registrationN = AmsMcsSteps.Registered(context, initN);
        context.Assert.True(initN.Status == "agent-initialized" && registrationN is { Registered: true, RepositoryCreated: true, Pushed: true } && AmsIds.Same(registrationN.Folder, scaffoldN.Folder) && GrsUrls.Names(registrationN.GitUrl, ams.Config.GitHost, context.Config.TenantId, ams.Config.DevEnvironmentId, registrationN.RepositoryId), "init registered N in a new repository and pushed it", $"repository={registrationN.RepositoryId} folder={registrationN.Folder} pushed={registrationN.Pushed} gitUrl={registrationN.GitUrl}");
        await AmsMcsSteps.AssertCloneAsync(context, newProject, registrationN.GitUrl, registrationN.CommitSha, $"msagent: add MCSAgent {scaffoldN.Name}", [scaffoldN.Folder], null, newRepository: true);

        var attachProject = AmsMcsSteps.Folder(context, "attach");
        AmsMcsFiles.CopyWithout(Path.Combine(scaffoldA.Project, scaffoldA.Folder), Path.Combine(attachProject, scaffoldA.Folder), LocalState);
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, newProject);
        var attached = await AmsMcsSteps.InitAsync(context, "agent init --repository-id attaches A to the repository", attachProject, "--agent-name", scaffoldA.Name, "--repository-id", registrationN.RepositoryId);
        var registrationA = AmsMcsSteps.Registered(context, attached);
        var restored = AmsMcsResults.RestoredIds(registrationA);
        context.Assert.True(attached.Status == "agent-initialized" && AmsIds.Same(registrationA.RepositoryId, registrationN.RepositoryId) && registrationA is { RepositoryCreated: false, Registered: true, Pushed: true } && AmsIds.Same(registrationA.Folder, scaffoldA.Folder) && restored is [var only] && AmsIds.Same(only, initN.AgentId), "the attach registered A in the repository, restored N, which the repository brings, and pushed", $"repository={registrationA.RepositoryId} created={registrationA.RepositoryCreated} folder={registrationA.Folder} restored={string.Join(',', restored)} pushed={registrationA.Pushed}");
        await AmsMcsSteps.AssertCloneAsync(context, attachProject, registrationA.GitUrl, registrationA.CommitSha, $"msagent: add MCSAgent {scaffoldA.Name}", [scaffoldA.Folder], mainBefore, newRepository: false);
        var config = AmsMcsFiles.ReadAgentConfig(attachProject);
        var agentA = AmsMcsFiles.Agent(config, attached.AgentId);
        var agentN = AmsMcsFiles.Agent(config, initN.AgentId);
        context.Assert.True(File.Exists(AmsMcsFiles.SettingsPath(attachProject, scaffoldN.Folder)) && config?.ManagedAgents.Count == 2 && AmsIds.Same(agentA?.SourceControlMetadata?.RepositoryId, registrationN.RepositoryId) && AmsIds.Same(agentA?.SourceControlMetadata?.Directory, scaffoldA.Folder) && AmsIds.Same(agentN?.SourceControlMetadata?.RepositoryId, registrationN.RepositoryId) && AmsIds.Same(agentN?.SourceControlMetadata?.Directory, scaffoldN.Folder), "N's folder is checked out beside A, and .config holds A and the restored N, each in the repository with its folder", string.Join(", ", (config?.ManagedAgents ?? []).Select(agent => $"{agent.DisplayName}@{agent.SourceControlMetadata?.Directory}")));
        var unlowered = AmsIds.NotLowercase(config!, [initN.AgentId]);
        context.Assert.True(unlowered.Count == 0, "the restored N's .config ids are lowercase GUIDs, whatever case AMS answered in", string.Join("; ", unlowered));
        var listed = await AmsMcsSteps.AmsAgentsAsync(context, registrationN.RepositoryId);
        context.Assert.True(AmsMcsResults.Holding(listed, initN.AgentId, scaffoldN.Folder).Count == 1 && AmsMcsResults.Holding(listed, attached.AgentId, scaffoldA.Folder).Count == 1, "AMS lists N and A in the repository, each with its folder", AmsMcsResults.Describe(listed));

        var collide = AmsMcsSteps.Folder(context, "collide");
        AmsMcsFiles.CopyWithout(Path.Combine(scaffoldN.Project, scaffoldN.Folder), Path.Combine(collide, scaffoldN.Folder), LocalState);
        AmsMcsFiles.AddYamlComment(AmsMcsFiles.SettingsPath(collide, scaffoldN.Folder), $"ahe2e collision {context.RunSuffix}");
        var before = AmsMcsFiles.Fingerprint(collide);
        await AmsMcsSteps.RefusedAsync(context, collide, AmsMcsSteps.InitArguments(collide, "--agent-name", AmsMcsSteps.Name(context, "Init C"), "--repository-id", registrationN.RepositoryId), 19058, "an attach that would overwrite N's files fails with RepositoryWouldOverwrite (19058, exit 2), naming the folder", 2, $"{scaffoldN.Folder}/");
        context.Assert.True(AmsMcsFiles.Fingerprint(collide) == before, "the refused attach left the folder exactly as before: no .git, no .config, the same files", collide);

        var scaffoldD = await AmsMcsSteps.AmsOffScaffoldAsync(context, "Init D");
        await AmsMcsSteps.SyncAsync(context, newProject);
        var developmentN = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, newProject, initN.AgentId, "development", "dev", null);
        await AmsMcsSteps.DeployAsync(context, ams.On, newProject, initN.AgentId, developmentN.DeploymentId);
        var tracked = $"{scaffoldN.Folder}/{WorkflowStateCache.FolderName}/{AmsMcsFiles.ConnectionFileName}";
        await ams.Git.AddAsync(newProject, [tracked], force: true, context.CancellationToken);
        await ams.Git.CommitStagedAsync(newProject, $"ahe2e: track msagent's {tracked}, unchanged", context.CancellationToken);
        await ams.Git.PushAsync(newProject, AmsMcsSteps.Branch, context.CancellationToken);
        var undoProject = AmsMcsSteps.Folder(context, "undo");
        AmsMcsFiles.CopyWithout(Path.Combine(scaffoldD.Project, scaffoldD.Folder), Path.Combine(undoProject, scaffoldD.Folder), LocalState);
        File.WriteAllText(Path.Combine(undoProject, AmsMcsFiles.IgnoreFileName), "bin/\n");
        before = AmsMcsFiles.Fingerprint(undoProject);
        await AmsMcsSteps.RefusedAsync(context, undoProject, AmsMcsSteps.InitArguments(undoProject, "--agent-name", scaffoldD.Name, "--repository-id", registrationN.RepositoryId), 19059, "an attach whose repository tracks a file under .mcs fails after the checkout with McsFolderInRepository (19059, exit 2), naming N's folder", 2, scaffoldN.Folder);
        var named = AmsMcsResults.Named(await AmsMcsSteps.AmsAgentsAsync(context, registrationN.RepositoryId), scaffoldD.Name);
        context.Assert.True(AmsMcsFiles.Fingerprint(undoProject) == before && named.Count == 0, "the failure after the checkout left the folder exactly as before (.gitignore included), and AMS has no D", $"{undoProject} rows={named.Count}");

        await AmsMcsSteps.PushRootSettingsAsync(context, newProject);
        var rootProject = AmsMcsSteps.Folder(context, "root");
        AmsMcsFiles.CopyWithout(Path.Combine(scaffoldD.Project, scaffoldD.Folder), Path.Combine(rootProject, scaffoldD.Folder), LocalState);
        before = AmsMcsFiles.Fingerprint(rootProject);
        await AmsMcsSteps.RefusedAsync(context, rootProject, AmsMcsSteps.InitArguments(rootProject, "--agent-name", scaffoldD.Name, "--repository-id", registrationN.RepositoryId), 19060, "an attach whose repository root is an agent's folder fails after the checkout with WorkspaceAtProjectRoot (19060, exit 2)", 2, "is itself the agent's folder");
        named = AmsMcsResults.Named(await AmsMcsSteps.AmsAgentsAsync(context, registrationN.RepositoryId), scaffoldD.Name);
        context.Assert.True(AmsMcsFiles.Fingerprint(rootProject) == before && named.Count == 0, "the P4 refusal after the attach took the checkout back: the folder is exactly as before, and AMS has no D", $"{rootProject} rows={named.Count}");

        var scaffoldM = await AmsMcsSteps.AmsOffScaffoldAsync(context, "Init M");
        var historyProject = AmsMcsSteps.Folder(context, "history");
        AmsMcsFiles.CopyWithout(Path.Combine(scaffoldM.Project, scaffoldM.Folder), Path.Combine(historyProject, scaffoldM.Folder), LocalState);
        await ams.Git.InitAsync(historyProject, context.CancellationToken);
        var firstLocal = await ams.Git.CommitAsync(historyProject, "ahe2e: local history 1", [scaffoldM.Folder], context.CancellationToken);
        AmsMcsFiles.AddYamlComment(AmsMcsFiles.SettingsPath(historyProject, scaffoldM.Folder), $"ahe2e local history {context.RunSuffix}");
        var localHead = await ams.Git.CommitAsync(historyProject, "ahe2e: local history 2", [scaffoldM.Folder], context.CancellationToken);
        var initM = await AmsMcsSteps.InitAsync(context, "agent init of a working tree with commits and no remote (MQ7)", historyProject, "--agent-name", scaffoldM.Name);
        var registrationM = AmsMcsSteps.Registered(context, initM);
        context.Assert.True(initM.Status == "agent-initialized" && registrationM is { Registered: true, RepositoryCreated: true, Pushed: true } && AmsIds.Same(registrationM.Folder, scaffoldM.Folder), "init registered M in a new repository and pushed it", $"repository={registrationM.RepositoryId} folder={registrationM.Folder} pushed={registrationM.Pushed} warnings={string.Join(" | ", registrationM.Warnings ?? [])}");
        await AmsMcsSteps.AssertCloneAsync(context, historyProject, registrationM.GitUrl, registrationM.CommitSha, $"msagent: add MCSAgent {scaffoldM.Name}", [scaffoldM.Folder], localHead, newRepository: false, "the local history's head");
        await ams.Git.FetchAsync(historyProject, context.CancellationToken);
        var mainCommits = await ams.Git.RevListAsync(historyProject, "origin/main", context.CancellationToken);
        var localCommits = await ams.Git.RevListAsync(historyProject, "HEAD", context.CancellationToken);
        var authors = await ams.Git.LogAsync(historyProject, "origin/main", "%ae", context.CancellationToken);
        var mainFiles = await ams.Git.FilesAsync(historyProject, "origin/main", context.CancellationToken) ?? [];
        context.Assert.True(localCommits.Count == 3 && mainCommits.SequenceEqual(localCommits) && mainCommits.Contains(firstLocal) && mainCommits.Contains(localHead) && !authors.Contains(GitCloneFacts.GrsAuthor, StringComparer.OrdinalIgnoreCase) && !mainFiles.Contains(GitCloneFacts.GrsReadme), "GRS main equals the local history (its two commits and the add commit); the leased push replaced GRS's initial commit: no README.md, no commit by grs@powerplatform.com", $"main={string.Join(',', mainCommits.Select(GitCommits.Short))} local={string.Join(',', localCommits.Select(GitCommits.Short))} authors={string.Join(',', authors.Distinct())}");
    });

    private static Scenario Deployments() => Builder.Make("AM-10", "deployment create for Dev, Test and Prod, refused creates, a rename and a delete", Home, SafetyTier.Creating, smoke: true, run: async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var deployments = await AmsMcsSteps.DeploymentsAsync(context);
        var config = AmsMcsFiles.ReadAgentConfig(home.Project);
        var details = AmsMcsResults.Details(await AmsMcsSteps.ShowAsync(context, home.Project, home.AgentId), home.AgentId);
        var slots = await AmsMcsSteps.SlotsAsync(context, home.Project, home.AgentId);

        foreach (var (deploymentId, deploymentType, environmentId) in new (string Id, string Type, string? Environment)[] { (deployments.Dev, "dev", null), (deployments.Test, "test", ams.Config.TestEnvironmentId), (deployments.Prod, "prod", ams.Config.ProdEnvironmentId) })
        {
            var row = AmsMcsFiles.Deployment(config, deploymentId);
            var slot = AmsMcsResults.Slot(slots, deploymentId);
            var inAgentShow = AmsMcsResults.DeploymentIds(details).Count(candidate => AmsIds.Same(candidate, deploymentId)) == 1;
            context.Assert.True(Guid.TryParse(deploymentId, out _) && AmsIds.Same(row?.DeploymentType, deploymentType) && (environmentId is null || AmsIds.Same(row?.EnvironmentId, environmentId)) && slot?.State is not null && inAgentShow, $"the {deploymentType} deployment has the same id in .config and in AMS", $"id={deploymentId} type={row?.DeploymentType} environment={row?.EnvironmentId} state={slot?.State} lastDeployedCommitSha={slot?.LastDeployedCommitSha} inAgentShow={inAgentShow}");
        }

        var before = File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(home.Project));
        var amsBefore = AmsMcsResults.DeploymentIds(details);
        context.Assert.Refused(await AmsMcsSteps.RawAsync(context, ams.On, ["deployment", "create", "--project", home.Project, "--agent-id", home.AgentId, "--deployment-name", "development-2", "--deployment-type", "dev"]), 13011, "a second Dev deployment is refused: AMS answers 409 PreconditionFailed (TargetRequestFailed 13011, exit 1)", 1, "PreconditionFailed");
        context.Assert.Refused(await AmsMcsSteps.RawAsync(context, ams.On, ["deployment", "create", "--project", home.Project, "--agent-id", home.AgentId, "--deployment-name", "test-home", "--deployment-type", "test", "--environment-id", ams.Config.DevEnvironmentId!]), 13011, "a Test deployment in the home environment, which Dev already uses, is refused: 409 PreconditionFailed (13011, exit 1)", 1, "PreconditionFailed");
        var amsAfter = AmsMcsResults.DeploymentIds(AmsMcsResults.Details(await AmsMcsSteps.ShowAsync(context, home.Project, home.AgentId), home.AgentId));
        context.Assert.True(File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(home.Project)).SequenceEqual(before) && amsBefore.Count > 0 && amsAfter.SequenceEqual(amsBefore), "the refused creates wrote nothing: .config is byte-for-byte the same, and AMS holds the same deployments", $"ams deployments: {string.Join(',', amsBefore)} -> {string.Join(',', amsAfter)}");

        var renamed = await AmsMcsSteps.RunAsync<AgentDeploymentUpdateResult>(context, ams.On, "deployment update --new-name", ["deployment", "update", "--project", home.Project, "--deployment-id", deployments.Test, "--new-name", "test-renamed"]);
        var testRow = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(home.Project), deployments.Test);
        context.Assert.True(renamed.Status == "deployment-updated" && renamed.DeploymentName == "test-renamed" && (renamed.Warnings ?? []).Count == 0 && testRow?.DeploymentName == "test-renamed", "renamed in .config, with no AMS warning", $"name={renamed.DeploymentName} config={testRow?.DeploymentName} warnings={string.Join(" | ", renamed.Warnings ?? [])}");
        await ams.Pool.Setup<AmsHomeDeployments>(AmsMcsSteps.DeploymentsSetup).ReplaceAsync(deployments with { TestName = "test-renamed" });

        var deleted = await AmsMcsSteps.RunAsync<AgentDeploymentDeleteResult>(context, ams.On, "deployment delete production", ["deployment", "delete", "--project", home.Project, "--deployment-id", deployments.Prod]);
        var inConfig = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(home.Project), deployments.Prod) is not null;
        var inAms = AmsMcsResults.DeploymentIds(AmsMcsResults.Details(await AmsMcsSteps.ShowAsync(context, home.Project, home.AgentId), home.AgentId)).Any(candidate => AmsIds.Same(candidate, deployments.Prod));
        context.Assert.True(deleted.Status == "deployment-deleted" && (deleted.Warnings ?? []).Count == 0 && !inConfig && !inAms, "deleted from .config and from AMS, with no warning", $"status={deleted.Status} inConfig={inConfig} inAms={inAms} warnings={string.Join(" | ", deleted.Warnings ?? [])}");

        var production = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, home.Project, home.AgentId, "production", "prod", ams.Config.ProdEnvironmentId);
        context.Assert.True(Guid.TryParse(production.DeploymentId, out _) && !AmsIds.Same(production.DeploymentId, deployments.Prod), "the new production deployment has a new id", $"old={deployments.Prod} new={production.DeploymentId}");
        await ams.Pool.Setup<AmsHomeDeployments>(AmsMcsSteps.DeploymentsSetup).ReplaceAsync(deployments with { TestName = "test-renamed", Prod = production.DeploymentId });
    });

    private static Scenario LocalDevDeployAndCapture() => Builder.Make("AM-11", "a local Dev deploy and its exact record, then a portal rename captured on main", HomeOracle, SafetyTier.Creating, smoke: true, run: async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var dev = await AmsMcsSteps.DevDeployedAsync(context);
        var before = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var portal = AmsMcsSteps.Name(context, "Portal");
        await AmsMcsSteps.RenameBotAsync(context, dev.BotId, portal);

        var capture = await AmsMcsSteps.CaptureAsync(context, home.Project, home.AgentId);
        var main = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        context.Assert.True(capture.Status == "captured" && GitCommits.Match(capture.CommitSha, main) && !GitCommits.Match(main, before), "captured as a new commit on GRS main", $"status={capture.Status} commit={capture.CommitSha} main={before}->{main}");

        await ams.Git.PullAsync(home.Project, AmsMcsSteps.Branch, context.CancellationToken);
        var head = await ams.Git.RevParseAsync(home.Project, "HEAD", context.CancellationToken);
        var displayName = AmsMcsText.DisplayName(AmsMcsFiles.ReadSettings(Path.Combine(home.Project, home.Folder)));
        context.Assert.True(GitCommits.Match(head, capture.CommitSha) && string.Equals(displayName, portal, StringComparison.Ordinal), "git pull brings the capture: HEAD is the commit, and displayName is the portal's name", $"head={head} displayName={displayName}");
    });

    private static Scenario RemoteDevDeploys() => Builder.Make("AM-12", "remote Dev deploys and the Dev guard: a clean head connects the folder, others leave it alone with a notice, 19050 and 19051; a bound bot with no cloud base gives 19038 and 19064", HomeOracle, SafetyTier.Creating, smoke: true, run: async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var deployments = await AmsMcsSteps.DeploymentsAsync(context);
        var dev = await AmsMcsSteps.DevDeployedAsync(context);
        var settings = AmsMcsFiles.SettingsPath(home.Project, home.Folder);
        var entry = AmsMcsFiles.SettingsEntry(home.Folder);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var c1 = await ams.Git.RevParseAsync(home.Project, "HEAD", context.CancellationToken);
        var c1Name = AmsMcsText.DisplayName(File.ReadAllText(settings));

        var head12 = AmsMcsSteps.Name(context, "Head12");
        var h1 = await AmsMcsSteps.CommitDisplayNameAsync(context, home.Project, home.Folder, head12);
        var timeBefore = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(home.Project), deployments.Dev)?.LastDeployedTime;
        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev);
        var slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Dev);
        var bot = await AmsMcsSteps.BotNameAsync(context, dev.BotId);
        var timeAfter = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(home.Project), deployments.Dev)?.LastDeployedTime;
        context.Assert.True(deployed.Status == "deployed" && GitCommits.IsSha(deployed.CommitSha) && GitCommits.Match(deployed.CommitSha, h1) && string.IsNullOrEmpty(deployed.CapturedCommitSha) && bot == head12 && GitCommits.Match(slot?.LastDeployedCommitSha, h1) && AmsMcsResults.Later(timeAfter, timeBefore), "deployed the head: commitSha is the requested head (no capture), the bot's name, AMS's record, .config's time", $"commitSha={deployed.CommitSha} capturedCommitSha={deployed.CapturedCommitSha} bot='{bot}' lastDeployedCommitSha={slot?.LastDeployedCommitSha} lastDeployedTime={timeBefore}->{timeAfter}");
        AmsMcsSteps.AssertConnected(context, "the deploy of the head refreshed A's folder", home.Agent, deployed, dev.BotId);
        await AmsMcsSteps.AssertPullUpToDateAsync(context, "right after the deploy of the head", home.Agent);

        AmsMcsFiles.AddYamlComment(settings, $"ahe2e local change {context.RunSuffix}");
        var before = AmsFolderState.Read(home.Project, home.Folder, home.AgentId);
        var dirty = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev, "--commit-id", GitCommits.Short(h1));
        context.Assert.True(dirty.Status == "deployed" && GitCommits.IsSha(dirty.CommitSha) && GitCommits.Match(dirty.CommitSha, h1) && string.IsNullOrEmpty(dirty.CapturedCommitSha) && AmsMcsText.WarnsLocalChanges(dirty.Warnings, h1), "deployed the head again: commitSha is the head in full (no capture), and the local change is warned about", $"commitSha={dirty.CommitSha} capturedCommitSha={dirty.CapturedCommitSha} warnings={string.Join(" | ", dirty.Warnings)}");
        AmsMcsSteps.AssertLeftAlone(context, "HEAD is the deployed head, but with an uncommitted change", home.Agent, dirty, before, AmsNoticeReason.Uncommitted, connected: true);
        var kept = File.ReadAllText(settings);
        context.Assert.True(AmsMcsText.DisplayName(kept) == head12 && kept.Contains($"# ahe2e local change {context.RunSuffix}", StringComparison.Ordinal), "the deploy changed no content file: settings.mcs.yml keeps the head's name and the uncommitted change", $"displayName='{AmsMcsText.DisplayName(kept)}'");
        await ams.Git.RestoreAsync(home.Project, [entry], context.CancellationToken);

        before = AmsFolderState.Read(home.Project, home.Folder, home.AgentId);
        var older = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev, "--commit-id", GitCommits.Short(c1));
        bot = await AmsMcsSteps.BotNameAsync(context, dev.BotId);
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Dev);
        context.Assert.True(older.Status == "deployed" && GitCommits.IsSha(older.CommitSha) && GitCommits.Match(older.CommitSha, c1) && string.IsNullOrEmpty(older.CapturedCommitSha) && !AmsMcsText.Mentions(older.Warnings, "local changes") && bot == c1Name && GitCommits.Match(slot?.LastDeployedCommitSha, c1), "deployed c1: commitSha is the requested c1 in full (no capture), no local-changes warning, the bot has c1's name, and AMS records c1", $"commitSha={older.CommitSha} capturedCommitSha={older.CapturedCommitSha} bot='{bot}' expected='{c1Name}' lastDeployedCommitSha={slot?.LastDeployedCommitSha} warnings={string.Join(" | ", older.Warnings)}");
        AmsMcsSteps.AssertLeftAlone(context, "the folder holds the head, not c1", home.Agent, older, before, AmsNoticeReason.OtherCommit, connected: true, h1, c1);
        var pulled = await AmsMcsSteps.PullAsync(context, ams.On, home.Project, home.AgentId);
        var pulledName = AmsMcsText.DisplayName(File.ReadAllText(settings));
        context.Assert.True(pulled.Status == "agent-pulled" && pulledName == c1Name, "agent pull brings c1's change into the folder: agent-pulled, and settings.mcs.yml has c1's displayName", $"status={pulled.Status} changes={pulled.ChangeCount} displayName='{pulledName}' expected='{c1Name}'");
        await ams.Git.RestoreAsync(home.Project, [home.Folder], context.CancellationToken);
        var restored = await ams.Git.StatusAsync(home.Project, [], context.CancellationToken);
        context.Assert.True(restored.Count == 0 && AmsMcsText.DisplayName(File.ReadAllText(settings)) == head12, "(setup) the folder is back at the head: git status is clean, and displayName is the head's", $"git status: {GitTip.Describe(restored)}");

        var portalA = AmsMcsSteps.Name(context, "Portal12a");
        await AmsMcsSteps.RenameBotAsync(context, dev.BotId, portalA);
        var tipBefore = await ams.Git.CapturesTipAsync(home.Project, deployments.Dev, context.CancellationToken);
        await AmsMcsSteps.RefusedAsync(context, home.Project, AmsMcsSteps.RemoteArguments(home.Project, home.AgentId, deployments.Dev), 19050, "a remote Dev deploy of the head fails with CaptureConflict (19050): nothing deployed or captured", null, "Nothing was deployed or captured");
        bot = await AmsMcsSteps.BotNameAsync(context, dev.BotId);
        var main = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var tip = await ams.Git.CapturesTipAsync(home.Project, deployments.Dev, context.CancellationToken);
        context.Assert.True(bot == portalA && GitCommits.Match(main, h1) && tip == tipBefore, "the bot keeps the edit; GRS main and the captures branch are unchanged", $"bot='{bot}' main={main} captures={tipBefore}->{tip}");

        var overwritten = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev, "--overwrite");
        bot = await AmsMcsSteps.BotNameAsync(context, dev.BotId);
        tip = await ams.Git.CapturesTipAsync(home.Project, deployments.Dev, context.CancellationToken);
        var saved = tip is null ? null : AmsMcsText.DisplayName(await ams.Git.ShowFileAsync(home.Project, tip, entry, context.CancellationToken));
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Dev);
        context.Assert.True(overwritten.Status == "deployed" && GitCommits.Match(overwritten.CommitSha, h1) && string.IsNullOrEmpty(overwritten.CapturedCommitSha) && bot == head12 && GitCommits.Match(slot?.LastDeployedCommitSha, h1) && tip is not null && tip != tipBefore && saved == portalA, "--overwrite deployed the requested head over the edit (commitSha is the head; the edit went to an ams/captures snapshot, not a captured commit)", $"commitSha={overwritten.CommitSha} capturedCommitSha={overwritten.CapturedCommitSha} bot='{bot}' lastDeployedCommitSha={slot?.LastDeployedCommitSha} captures={tipBefore}->{tip} savedName='{saved}'");
        AmsMcsSteps.AssertConnected(context, "the folder holds the head again, clean, so the --overwrite deploy of the head refreshed it", home.Agent, overwritten, dev.BotId);

        var portalB = AmsMcsSteps.Name(context, "Portal12b");
        await AmsMcsSteps.RenameBotAsync(context, dev.BotId, portalB);
        var refused = await AmsMcsSteps.RefusedAsync(context, home.Project, AmsMcsSteps.RemoteArguments(home.Project, home.AgentId, deployments.Dev, "--commit-id", c1!), 19051, "a remote Dev deploy --commit-id c1 fails with DevEditsNotInCommit (19051, exit 2) after saving the edit on main", 2);
        var savedCommit = AmsMcsText.SavedOnMain(refused.ErrorMessage);
        await ams.Git.FetchAsync(home.Project, context.CancellationToken);
        main = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Dev);
        bot = await AmsMcsSteps.BotNameAsync(context, dev.BotId);
        saved = savedCommit is null ? null : AmsMcsText.DisplayName(await ams.Git.ShowFileAsync(home.Project, savedCommit, entry, context.CancellationToken));
        context.Assert.True(savedCommit is not null && GitCommits.Match(main, savedCommit) && saved == portalB && bot == portalB && GitCommits.Match(slot?.LastDeployedCommitSha, h1), "the message says the edits were saved on main as commit X; X is GRS main and holds the edit; the bot keeps it, and Dev still holds the head", $"saved={savedCommit} main={main} savedName='{saved}' bot='{bot}' lastDeployedCommitSha={slot?.LastDeployedCommitSha} message={refused.ErrorMessage}");

        var portalC = AmsMcsSteps.Name(context, "Portal12c");
        await AmsMcsSteps.RenameBotAsync(context, dev.BotId, portalC);
        before = AmsFolderState.Read(home.Project, home.Folder, home.AgentId);
        var merged = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev);
        await ams.Git.FetchAsync(home.Project, context.CancellationToken);
        main = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Dev);
        bot = await AmsMcsSteps.BotNameAsync(context, dev.BotId);
        var mainName = main is null ? null : AmsMcsText.DisplayName(await ams.Git.ShowFileAsync(home.Project, main, entry, context.CancellationToken));
        context.Assert.True(merged.Status == "deployed" && GitCommits.IsSha(merged.CapturedCommitSha) && GitCommits.IsSha(merged.CommitSha) && GitCommits.Match(merged.CommitSha, merged.CapturedCommitSha) && GitCommits.Match(merged.CapturedCommitSha, main) && GitCommits.Match(slot?.LastDeployedCommitSha, main) && bot == portalC && mainName == portalC, "the guard deployed its capture: commitSha equals capturedCommitSha, which is the new GRS main and what AMS records; the bot and the commit keep the edit", $"commitSha={merged.CommitSha} capturedCommitSha={merged.CapturedCommitSha} main={main} lastDeployedCommitSha={slot?.LastDeployedCommitSha} bot='{bot}' mainName='{mainName}'");
        AmsMcsSteps.AssertLeftAlone(context, "the folder holds the head, not the capture the guard deployed", home.Agent, merged, before, AmsNoticeReason.OtherCommit, connected: true, h1, merged.CommitSha);
        var lastCommit = AmsMcsText.LastCommit((await AmsMcsSteps.ShowTextAsync(context, home.Project, home.AgentId)).StdOut, deployments.Dev);
        context.Assert.True(GitCommits.Match(lastCommit, merged.CapturedCommitSha), "agent show's lastCommit for Dev is the deployed capture", $"lastCommit={lastCommit}");

        await AmsMcsSteps.SyncAsync(context, home.Project);
        var head = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) after git pull, the folder holds GRS main exactly", home.Agent, head);
        var final = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev);
        context.Assert.True(final.Status == "deployed" && GitCommits.Match(final.CommitSha, head) && string.IsNullOrEmpty(final.CapturedCommitSha), "deployed the head (the capture): commitSha is GRS main, no new capture", $"commitSha={final.CommitSha} capturedCommitSha={final.CapturedCommitSha} main={head}");
        AmsMcsSteps.AssertConnected(context, "the deploy of the head the folder holds refreshed it", home.Agent, final, dev.BotId);
        await AmsMcsSteps.AssertPullUpToDateAsync(context, "right after that deploy", home.Agent);

        var source = await AmsMcsSteps.AmsOffSourceAsync(context, "Base");
        context.Assert.True(AmsIds.Same(AmsMcsFiles.RecordedBot(source.Project, source.AgentId), source.BotId), "the AMS-off deploy bound the bot in .config", $"mcsAgentId={AmsMcsFiles.RecordedBot(source.Project, source.AgentId)} bot={source.BotId}");
        var initialized = await AmsMcsSteps.InitAsync(context, "agent init registers the deployed project (register-only)", source.Project);
        var registration = AmsMcsSteps.Registered(context, initialized);
        var agent = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(source.Project), source.AgentId);
        context.Assert.True(initialized.Status == "agent-initialized" && AmsIds.Same(initialized.AgentId, source.AgentId) && registration is { Registered: true, RepositoryCreated: true, Pushed: true } && AmsIds.Same(agent?.SourceControlMetadata?.RepositoryId, registration.RepositoryId), "init registered the same agent in a new repository and pushed it", $"agentId={initialized.AgentId} repository={registration.RepositoryId} pushed={registration.Pushed}");
        await AmsMcsSteps.AssertPushedAsync(context, source.Project, registration.CommitSha, null);
        var baseSlot = await AmsMcsSteps.SlotAsync(context, source.Project, source.AgentId, source.Dev.DeploymentId);
        context.Assert.True(baseSlot?.State is not null && string.IsNullOrEmpty(baseSlot.LastDeployedCommitSha), "AMS holds the Dev deployment, with no commit", $"state={baseSlot?.State} lastDeployedCommitSha={baseSlot?.LastDeployedCommitSha}");
        var probe = MsagentOutcome.Read(await AmsMcsSteps.RawAsync(context, ams.On, AmsMcsSteps.CaptureArguments(source.Project, source.AgentId), AmsMcsSteps.LongCall));

        if (probe.ErrorCode == 19038)
        {
            var noGit = AmsMcsSteps.Folder(context, "base-without-git");
            AmsMcsFiles.CopyWithout(source.Project, noGit, [".git"]);
            var folder = Path.Combine(noGit, source.Folder);
            var keptHash = AmsMcsFiles.SyncFilesHash(folder);
            var bound = await AmsMcsSteps.DeployAsync(context, ams.On, noGit, source.AgentId, source.Dev.DeploymentId);
            context.Assert.True(AmsIds.Same(bound.TargetMcsAgentId, source.BotId) && AmsMcsText.Mentions(bound.Warnings, "holds no git commit"), "(setup) AMS had no bot for Dev yet (CaptureAgentNotInCloud, 19038), so a local Dev deploy from a copy without git bound the same bot and warned that the folder holds no git commit", $"bot={bound.TargetMcsAgentId} warnings={string.Join(" | ", bound.Warnings)}");
            AmsMcsSteps.AssertSyncFilesKept(context, "the deploy from the copy without git", folder, keptHash);
            probe = MsagentOutcome.Read(await AmsMcsSteps.RawAsync(context, ams.On, AmsMcsSteps.CaptureArguments(source.Project, source.AgentId), AmsMcsSteps.LongCall));
        }

        context.Assert.True(probe.ErrorCode == 19064, "capture fails with CaptureBaseUnknown (19064): AMS doesn't know which commit Dev holds", $"errorCode={probe.ErrorCode} status={probe.Status} message={probe.FailureText}");
        var baseTimeBefore = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(source.Project), source.Dev.DeploymentId)?.LastDeployedTime;
        await AmsMcsSteps.RefusedAsync(context, source.Project, AmsMcsSteps.RemoteArguments(source.Project, source.AgentId, source.Dev.DeploymentId), 19064, "a remote Dev deploy without --overwrite to a bound bot with no cloud base fails with CaptureBaseUnknown (19064, exit 1)", 1);
        baseSlot = await AmsMcsSteps.SlotAsync(context, source.Project, source.AgentId, source.Dev.DeploymentId);
        var baseTimeAfter = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(source.Project), source.Dev.DeploymentId)?.LastDeployedTime;
        context.Assert.True(string.IsNullOrEmpty(baseSlot?.LastDeployedCommitSha) && baseTimeAfter == baseTimeBefore, "nothing was deployed: no commit in AMS, .config's time unchanged", $"lastDeployedCommitSha={baseSlot?.LastDeployedCommitSha} lastDeployedTime={baseTimeBefore}->{baseTimeAfter}");
        var overwrittenBase = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, source.Project, source.AgentId, source.Dev.DeploymentId, "--overwrite");
        var baseMain = await AmsMcsSteps.RemoteMainAsync(context, source.Project);
        baseSlot = await AmsMcsSteps.SlotAsync(context, source.Project, source.AgentId, source.Dev.DeploymentId);
        context.Assert.True(overwrittenBase.Status == "deployed" && GitCommits.Match(overwrittenBase.CommitSha, baseMain) && string.IsNullOrEmpty(overwrittenBase.CapturedCommitSha) && GitCommits.Match(baseSlot?.LastDeployedCommitSha, baseMain), "--overwrite deployed GRS main (commitSha is the requested commit; no capture), which AMS records", $"commitSha={overwrittenBase.CommitSha} capturedCommitSha={overwrittenBase.CapturedCommitSha} main={baseMain} lastDeployedCommitSha={baseSlot?.LastDeployedCommitSha}");
        var capture = await AmsMcsSteps.CaptureAsync(context, source.Project, source.AgentId);
        context.Assert.True(capture.Status is "captured" or "unchanged", "agent capture now knows the base: captured or unchanged", $"status={capture.Status}");
    });

    private static Scenario RemoteTest() => Builder.Make("AM-13", "remote Test: a commit Dev deployed is accepted, one it never did is refused (19002), and the default is Dev's newest; no folder is connected", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var deployments = await AmsMcsSteps.DeploymentsAsync(context);
        var dev = await AmsMcsSteps.DevDeployedAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var dev13 = AmsMcsSteps.Name(context, "Dev13");
        var c13a = await AmsMcsSteps.CommitDisplayNameAsync(context, home.Project, home.Folder, dev13);
        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Dev);
        var slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Dev);
        context.Assert.True(deployed.Status == "deployed" && GitCommits.Match(slot?.LastDeployedCommitSha, c13a) && deployed.Published != true, "Dev holds c13a, unpublished", $"commitSha={deployed.CommitSha} published={deployed.Published} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var c13b = await AmsMcsSteps.CommitDisplayNameAsync(context, home.Project, home.Folder, AmsMcsSteps.Name(context, "Never13"));

        var first = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Test, "--commit-id", dev.CommitSha);
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Test);
        context.Assert.True(first.Status == "deployed" && GitCommits.Match(first.CommitSha, dev.CommitSha) && first.Published == true && GitCommits.Match(slot?.LastDeployedCommitSha, dev.CommitSha), "Test deployed and published the commit the local Dev deploy recorded: Dev deploys count even when unpublished", $"commitSha={first.CommitSha} published={first.Published} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var connection = AmsMcsFiles.Connection(home.Project, home.Folder, home.AgentId);
        context.Assert.True(!first.WorkspaceConnected && string.IsNullOrEmpty(first.WorkspaceNotice) && connection.Names(dev.BotId, ams.Config.DevEnvironmentId), "a Test deploy connects nothing and says nothing about the folder: workspaceConnected isn't true, there's no workspaceNotice, and A's .mcs\\conn.json is still bound to the Dev bot in Dev (P30)", $"workspaceConnected={first.WorkspaceConnected} workspaceNotice='{first.WorkspaceNotice}' {connection.Describe()}; Dev bot={dev.BotId}");
        await AmsMcsSteps.RefusedAsync(context, home.Project, AmsMcsSteps.RemoteArguments(home.Project, home.AgentId, deployments.Test, "--commit-id", c13b), 19002, "a remote Test deploy --commit-id of a commit Dev never deployed fails with CommitNotAllowedForStage (19002, exit 2)", 2);
        slot = await AmsMcsSteps.SlotAsync(context, home.Project, home.AgentId, deployments.Test);
        context.Assert.True(GitCommits.Match(slot?.LastDeployedCommitSha, dev.CommitSha), "Test still holds the earlier commit", $"lastDeployedCommitSha={slot?.LastDeployedCommitSha}");

        var newest = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Test);
        var testBots = await ams.Dataverse.BotsNamedAsync(ams.Config.TestEnvironmentId!, home.Created.SchemaName!, context.CancellationToken);
        context.Assert.True(newest.Status == "deployed" && GitCommits.Match(newest.CommitSha, c13a) && newest.Published == true && testBots.Count == 1 && testBots[0].Name == dev13, "Test deployed and published c13a, Dev's newest; its bot in Test has c13a's name", $"commitSha={newest.CommitSha} published={newest.Published} bots={AmsMcsResults.Describe(testBots)}");
        await ams.Pool.Setup<AmsTestPublished>(AmsMcsSteps.TestPublishedSetup).ReplaceAsync(new AmsTestPublished(newest.CommitSha!, dev13));
    });

    private static Scenario RemoteProd() => Builder.Make("AM-14", "remote Prod takes Test's newest published commit; --commit-id is refused (19063); nothing published to Test fails (19004), then works once it is", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var deployments = await AmsMcsSteps.DeploymentsAsync(context);
        var tested = await AmsMcsSteps.TestPublishedAsync(context);
        var prod = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Prod);
        var prodBots = await ams.Dataverse.BotsNamedAsync(ams.Config.ProdEnvironmentId!, home.Created.SchemaName!, context.CancellationToken);
        context.Assert.True(prod.Status == "deployed" && GitCommits.Match(prod.CommitSha, tested.CommitSha) && prod.Published == true && prodBots.Count == 1 && prodBots[0].Name == tested.Name, "Prod deployed and published Test's newest published commit; its bot in Prod has that commit's name", $"commitSha={prod.CommitSha} expected={tested.CommitSha} published={prod.Published} bots={AmsMcsResults.Describe(prodBots)}");
        await AmsMcsSteps.RefusedAsync(context, home.Project, AmsMcsSteps.RemoteArguments(home.Project, home.AgentId, deployments.Prod, "--commit-id", tested.CommitSha), 19063, "a remote Prod deploy --commit-id fails with CommitNotAccepted (19063, exit 2)", 2);

        await AmsMcsSteps.SyncAsync(context, home.Project);
        var c14 = await AmsMcsSteps.CommitDisplayNameAsync(context, home.Project, home.Folder, AmsMcsSteps.Name(context, "Test14"));
        var local = await AmsMcsSteps.DeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Test);
        context.Assert.True(!local.Published && AmsMcsText.AmsWarnings(local.Warnings).Count == 0, "the local Test deploy without --publish is unpublished, and AMS took its record", $"published={local.Published} warnings={string.Join(" | ", local.Warnings)}");
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, home.Project, home.Folder);
        var again = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, home.AgentId, deployments.Prod);
        context.Assert.True(again.Status == "deployed" && GitCommits.Match(again.CommitSha, tested.CommitSha) && !GitCommits.Match(again.CommitSha, c14), "Prod still takes the published commit, not the unpublished local Test deploy", $"commitSha={again.CommitSha} published={tested.CommitSha} unpublished={c14}");

        var agentB = await AmsMcsSteps.AgentBAsync(context);
        var test = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, home.Project, agentB.AgentId, "test-ring", "test", ams.Config.TestEnvironmentId);
        var production = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, home.Project, agentB.AgentId, "production", "prod", ams.Config.ProdEnvironmentId);
        var productionArguments = AmsMcsSteps.RemoteArguments(home.Project, agentB.AgentId, production.DeploymentId);
        await AmsMcsSteps.RefusedAsync(context, home.Project, productionArguments, 19004, "a remote Prod deploy of B, which Test never published, fails with SourceDeploymentNotReady (19004, exit 1)", 1);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var unpublished = await AmsMcsSteps.DeployAsync(context, ams.On, home.Project, agentB.AgentId, test.DeploymentId);
        context.Assert.True(!unpublished.Published, "B's local Test deploy without --publish is unpublished", $"published={unpublished.Published}");
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, home.Project, agentB.Folder);
        await AmsMcsSteps.RefusedAsync(context, home.Project, productionArguments, 19004, "a remote Prod deploy of B still fails with SourceDeploymentNotReady (19004, exit 1): an unpublished Test deploy doesn't count", 1);
        var published = await AmsMcsSteps.DeployAsync(context, ams.On, home.Project, agentB.AgentId, test.DeploymentId, "--publish");
        context.Assert.True(published.Published, "B's local Test deploy with --publish is published", $"published={published.Published} warnings={string.Join(" | ", published.Warnings)}");
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, home.Project, agentB.Folder);
        var head = await ams.Git.RevParseAsync(home.Project, "HEAD", context.CancellationToken);
        var productionDeploy = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, home.Project, agentB.AgentId, production.DeploymentId);
        context.Assert.True(productionDeploy.Status == "deployed" && GitCommits.Match(productionDeploy.CommitSha, head) && productionDeploy.Published == true, "Prod deployed and published the commit Test published, P's HEAD", $"commitSha={productionDeploy.CommitSha} head={head} published={productionDeploy.Published}");
    });

    private static Scenario Connections() => Builder.Make("AM-15", "a standalone workflow attached to W with agent init --repository-id, then its connections on Test: a draft, a bind that turns it on, and a redeploy that keeps it", [.. HomeOracle, Capability.AmsMcsConnection], SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var workflows = await AmsMcsSteps.WorkflowsAsync(context);
        var project = AmsMcsSteps.Folder(context, "connection");
        var name = $"ahe2eConn{context.RunSuffix}";
        var reference = $"new_ahe2ec{context.RunSuffix}_msnweather";
        var workspace = Path.Combine(project, name);
        FixtureCopies.CopyWorkflow(Path.Combine(HarnessEnvironment.ProjectDirectory, "fixtures", Capabilities.StandaloneFixturesFolder, SwFixtures.FolderOf(SwShape.ClassicAgentFlow)), workspace, name, Guid.NewGuid(), keepLocalState: false);
        AmsMcsFiles.RenameConnectionReference(workspace, "new_sharedmsnweather_08889", reference);
        var expected = AmsMcsResults.Ids((await AmsMcsSteps.AmsAgentsAsync(context, workflows.RepositoryId)).Agents);
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, workflows.Project);
        AmsMcsSteps.RecordFlows(context, name, ams.Config.DevEnvironmentId, ams.Config.TestEnvironmentId);
        var initialized = await AmsMcsSteps.RecordedAsync<ManagedAgentInitResult>(context, ams.On, "agent init of a standalone workflow with a connection reference, attached to W", project, ["agent", "init", "--agent-type", AgentTypes.McsWorkflow, "--agent-name", name, "--mcs-workflow-source", project, "--repository-id", workflows.RepositoryId], AmsMcsSteps.LongCall);
        var registration = AmsMcsSteps.Registered(context, initialized);
        context.Assert.True(initialized.Status == "agent-initialized" && registration is { Registered: true, RepositoryCreated: false, Pushed: true } && AmsIds.Same(registration.RepositoryId, workflows.RepositoryId) && expected.Count > 0 && AmsMcsResults.Restores(registration, expected), "the workflow joins W (repositoryCreated false) and is pushed, and W's workflows are restored beside it", $"repository={registration.RepositoryId} created={registration.RepositoryCreated} folder={registration.Folder} expected={string.Join(',', expected)} restored={string.Join(',', AmsMcsResults.RestoredIds(registration))}");
        await AmsMcsSteps.AssertPushedAsync(context, project, registration.CommitSha, mainBefore);

        var development = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, project, initialized.AgentId, "development", "dev", null);
        var test = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, project, initialized.AgentId, "test-ring", "test", ams.Config.TestEnvironmentId);
        var preview = await AmsMcsSteps.ConnectionsAsync(context, "list", project, initialized.AgentId, test.DeploymentId);
        var advice = $"Create an MSN Weather connection in Test by hand at {AmsMcsResults.ConsentLink(preview) ?? ams.ConnectionLink}, signed in as msagent's user, then run --only AM-15 again. msagent creates none non-interactively, and binds only a connection that user can use.";
        context.Assert.True(AmsMcsResults.Usable(preview), "Test already has an MSN Weather connection to bind (msagent can't create one non-interactively)", $"deployment connection list shows {preview.Connections.Count} connection references for {name}: {AmsMcsResults.Describe(preview)}. With none there's nothing to bind; otherwise: {advice}");
        await AmsMcsSteps.RemoteDeployAsync(context, ams.On, project, initialized.AgentId, development.DeploymentId);
        var first = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, project, initialized.AgentId, test.DeploymentId);
        var flow = await AmsMcsSteps.TestFlowAsync(context, name);
        context.Assert.True(first.Status == "deployed" && AmsMcsResults.NoticeNames(first, "FlowLeftInDraft", reference) && flow?.StateCode == 0, $"the first remote Test deploy left the flow in draft, and the FlowLeftInDraft notice's relatedItems name the unbound reference {reference}", $"notices={AmsMcsResults.DescribeNotices(first)} statecode={flow?.StateCode}");
        var updated = await AmsMcsSteps.ConnectionsAsync(context, "update", project, initialized.AgentId, test.DeploymentId);
        flow = await AmsMcsSteps.TestFlowAsync(context, name);
        context.Assert.True(AmsMcsResults.AllBound(updated) && updated.Ready && flow?.StateCode == 1, "the reference is bound, the deployment is ready, and the flow is on", $"{AmsMcsResults.Describe(updated)} ready={updated.Ready} statecode={flow?.StateCode}. If the reference isn't bound: {advice}");
        var connectionId = AmsMcsResults.Bound(updated)[0].ConnectionId;
        var again = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, project, initialized.AgentId, test.DeploymentId);
        var listed = await AmsMcsSteps.ConnectionsAsync(context, "list", project, initialized.AgentId, test.DeploymentId);
        flow = await AmsMcsSteps.TestFlowAsync(context, name);
        context.Assert.True(again.Status == "deployed" && AmsMcsResults.Notices(again, "FlowLeftInDraft").Count == 0 && AmsMcsResults.Holds(listed, connectionId) && flow?.StateCode == 1, "the redeploy kept the binding and the flow on, with no draft notice", $"notices={AmsMcsResults.DescribeNotices(again)} connection={connectionId} {AmsMcsResults.Describe(listed)} statecode={flow?.StateCode}");
    });

    private static Scenario Teammate() => Builder.Make("AM-09", "a teammate, two ways: git clone then agent init, or agent init --repository-id alone, each restoring and connecting the agents Dev holds; P4 and an empty repository fail and remove the clone", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var deployments = await AmsMcsSteps.DeploymentsAsync(context);
        var dev = await AmsMcsSteps.DevDeployedAsync(context);
        var agentB = await AmsMcsSteps.AgentBAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var helper = await ams.Git.LastHelperAsync(home.Project, ams.Config.GitHost!, context.CancellationToken);
        var mate = AmsMcsSteps.Folder(context, "mate");
        await ams.Git.CloneWithHelperAsync(home.Created.Ams!.GitUrl, mate, ams.Config.GitHost!, helper, context.CancellationToken);
        var headBefore = await ams.Git.RevParseAsync(mate, "HEAD", context.CancellationToken);
        var initialized = await AmsMcsSteps.InitAsync(context, "agent init in the teammate's clone", mate);
        await AmsMcsSteps.AssertRestoredAsync(context, "git clone, then agent init", mate, initialized, headBefore, home, deployments, dev, agentB);
        var deployed = await AmsMcsSteps.DeployAsync(context, ams.On, mate, home.AgentId, deployments.Dev);
        context.Assert.True(AmsIds.Same(deployed.TargetMcsAgentId, dev.BotId) && !deployed.TargetAgentCreated && AmsMcsText.AmsWarnings(deployed.Warnings).Count == 0, "the teammate's first local Dev deploy, without --overwrite, updated the same bot, created none, and AMS took the record", $"bot={deployed.TargetMcsAgentId} created={deployed.TargetAgentCreated} warnings={string.Join(" | ", deployed.Warnings)}");
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, mate, home.Folder);
        var slot = await AmsMcsSteps.SlotAsync(context, mate, home.AgentId, deployments.Dev);
        context.Assert.True(GitCommits.Match(slot?.LastDeployedCommitSha, headBefore), "AMS records the teammate's HEAD as Dev's commit", $"lastDeployedCommitSha={slot?.LastDeployedCommitSha} head={headBefore}");

        var oneCommand = AmsMcsSteps.Folder(context, "one-command");
        var initArguments = AmsMcsSteps.InitArguments(oneCommand, "--repository-id", home.RepositoryId);
        var declined = await ams.On.RunAsync([.. initArguments, "--json"], ams.WorkspaceRoot, stdin: "n", timeout: AmsMcsServices.CallTimeout, cancellationToken: context.CancellationToken);
        var declinedOutcome = ams.Record(context.Ledger, declined, oneCommand, amsOff: false, context.ScenarioId);
        context.Assert.True(declinedOutcome.Status == "cancelled" && declined.ExitCode == 0 && AmsMcsText.AsksToRestore(declined.StdErr, home.RepositoryId) && !Path.Exists(oneCommand), "without --non-interactive, msagent asks to clone the repository and restore its agents; n cancels and creates no folder", $"status={declinedOutcome.Status} exit={declined.ExitCode} stderr={declined.StdErr.Trim()}");
        var main = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var oneCommandResult = await AmsMcsSteps.RawAsync(context, ams.On, initArguments, AmsMcsSteps.LongCall);
        ams.Record(context.Ledger, oneCommandResult, oneCommand, amsOff: false, context.ScenarioId);
        context.Assert.ExitZero(oneCommandResult, "agent init --repository-id into a missing folder (one command), --non-interactive, no --agent-name");
        var restored = oneCommandResult.Json<ManagedAgentInitResult>();
        context.Assert.True(restored.Status == "agent-initialized" && !AmsMcsText.HasQuestion(oneCommandResult.StdErr), "--non-interactive cloned without asking", $"status={restored.Status} stderr={oneCommandResult.StdErr.Trim()}");
        var clone = await ams.Git.InspectAsync(oneCommand, ams.Config.GitHost!, context.CancellationToken);
        context.Assert.True(clone.IsRootOf(oneCommand) && clone.OriginIs(home.Created.Ams!.GitUrl) && GitCommits.Match(clone.Head?.Sha, main), "the folder is a clone of the repository at GRS main", $"toplevel={clone.TopLevel} origin={clone.Origin} head={clone.Head?.Sha} main={main}");
        context.Assert.True(clone.HasMsagentHelper(ams.Config.GitHost!), $".git/config resets the credential helpers for {ams.Config.GitHost} and sets msagent's", $"helpers={string.Join(" | ", clone.Helpers ?? [])}");
        await AmsMcsSteps.AssertRestoredAsync(context, "agent init --repository-id", oneCommand, restored, clone.Head?.Sha, home, deployments, dev, agentB);
        var twoCommands = AmsMcsFiles.RestoreSignature(AmsMcsFiles.ReadAgentConfig(mate));
        var oneCommandSignature = AmsMcsFiles.RestoreSignature(AmsMcsFiles.ReadAgentConfig(oneCommand));
        context.Assert.True(twoCommands is not null && twoCommands == oneCommandSignature, "both teammate paths restore the same agents, folders, bots and deployments", $"git clone + init: {twoCommands} | init --repository-id: {oneCommandSignature}");

        var emptySource = AmsMcsSteps.Folder(context, "empty-source");
        var throwaway = await AmsMcsSteps.CreateAsync(context, ams.On, emptySource, AmsMcsSteps.Name(context, "Empty"));
        var emptyRepository = AmsMcsSteps.Registered(context, throwaway);
        await AmsMcsSteps.AssertPushedAsync(context, emptySource, emptyRepository.CommitSha, null);
        await AmsMcsSteps.PushRootSettingsAsync(context, emptySource);
        var rootOuter = AmsMcsSteps.Folder(context, "root");
        var rootProject = Path.Combine(rootOuter, "work", "teammate");
        await AmsMcsSteps.RefusedAsync(context, rootProject, AmsMcsSteps.InitArguments(rootProject, "--repository-id", emptyRepository.RepositoryId), 19060, "agent init --repository-id of a repository whose root is an agent's folder fails after the clone with WorkspaceAtProjectRoot (19060, exit 2)", 2, "is itself the agent's folder");
        context.Assert.True(!Path.Exists(rootOuter), "the P4 refusal after the clone removed the clone, the folder and its missing parents", rootOuter);

        await AmsMcsSteps.DeleteAsync(context, ams.On, "(setup) agent delete the throwaway agent", emptySource, throwaway.AgentId);
        await AmsMcsSteps.PushRemovalAsync(context, emptySource, emptyRepository.Folder, WorkspaceFileNames.Settings);
        var files = await ams.Git.FilesAsync(emptySource, "HEAD", context.CancellationToken);
        var left = await AmsMcsSteps.AmsAgentsAsync(context, emptyRepository.RepositoryId);
        context.Assert.True(GitCloneFacts.HoldsOnlySeed(files) && left.Agents.Count == 0, "(setup) the repository is empty: AMS holds no agent of it, and its main holds only .gitignore and GRS's README.md", $"files={string.Join(',', files ?? [])} agents={left.Agents.Count}");
        var emptyFolder = AmsMcsSteps.EmptyFolder(context, "empty-existing");

        foreach (var (what, outer, project, extra, code, text) in new (string What, string Outer, string Project, string[] Extra, int Code, string Text)[] { ("into an empty folder", emptyFolder, emptyFolder, [], 99002, "requires --agent-name"), ("into a missing folder with missing parents", AmsMcsSteps.Folder(context, "empty-missing"), Path.Combine(AmsMcsSteps.Folder(context, "empty-missing"), "work", "teammate"), [], 99002, "requires --agent-name"), ("with --agent-name, into a missing folder with missing parents", AmsMcsSteps.Folder(context, "empty-named"), Path.Combine(AmsMcsSteps.Folder(context, "empty-named"), "work", "teammate"), ["--agent-name", AmsMcsSteps.Name(context, "Named")], 10008, "holds no MCS project") })
        {
            await AmsMcsSteps.InitUndoneAsync(context, what, outer, project, ["--repository-id", emptyRepository.RepositoryId, .. extra], code, $"the init {what} fails with main's own error ({code}, exit 2): the clone ran and was taken back", text);
        }
    });

    private static Scenario Delete() => Builder.Make("AM-16", "agent delete removes the Copilot Studio agent and AMS's and .config's records; the folder and GRS keep it; a repeat is already absent; a teammate's init without --agent-name then names that folder", HomeOracle, SafetyTier.Creating, smoke: true, run: async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var created = await AmsMcsSteps.CreateAsync(context, ams.On, home.Project, AmsMcsSteps.Name(context, "Del"));
        var registration = AmsMcsSteps.Registered(context, created);
        var folder = registration.Folder;
        var dev = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, home.Project, created.AgentId, "development", "dev", null);
        var deployed = await AmsMcsSteps.DeployAsync(context, ams.On, home.Project, created.AgentId, dev.DeploymentId);
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, home.Project, folder);
        var schemaName = deployed.SchemaName ?? created.SchemaName!;
        context.Assert.True((await ams.Dataverse.BotsNamedAsync(ams.Config.DevEnvironmentId!, schemaName, context.CancellationToken)).Count == 1, "the bot exists in Dev", $"bot={deployed.TargetMcsAgentId} schemaName={schemaName}");

        var configBefore = File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(home.Project));
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var deleted = await AmsMcsSteps.DeleteAsync(context, ams.On, "agent delete", home.Project, created.AgentId);
        context.Assert.True(deleted.Status == "agent-deleted" && !deleted.McsAgentAlreadyAbsent && deleted.AmsRecordDeleted == true && AmsIds.Same(deleted.RepositoryId, home.RepositoryId) && deleted.RemovedAgentDeployments == 1, "agent-deleted: the bot, AMS's record and the deployment row", $"status={deleted.Status} alreadyAbsent={deleted.McsAgentAlreadyAbsent} amsRecordDeleted={deleted.AmsRecordDeleted} repositoryId={deleted.RepositoryId} removedAgentDeployments={deleted.RemovedAgentDeployments}");

        var bots = await ams.Dataverse.BotsNamedAsync(ams.Config.DevEnvironmentId!, schemaName, context.CancellationToken);
        var rows = AmsMcsResults.Holding(await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId), created.AgentId);
        var config = AmsMcsFiles.ReadAgentConfig(home.Project);
        var folderStays = AmsMcsFiles.ReadSettings(Path.Combine(home.Project, folder)) is not null;
        await ams.Git.FetchAsync(home.Project, context.CancellationToken);
        var inGrs = await ams.Git.ExistsAsync(home.Project, $"origin/{AmsMcsSteps.Branch}:{folder}/settings.mcs.yml", context.CancellationToken);
        var mainAfter = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        context.Assert.True(bots.Count == 0 && rows.Count == 0 && AmsMcsFiles.Agent(config, created.AgentId) is null && AmsMcsFiles.Deployments(config, created.AgentId).Count == 0 && folderStays && inGrs && GitCommits.Match(mainAfter, mainBefore), "the bot and AMS's row are gone, .config forgets the agent; the folder and GRS keep it", $"bots={bots.Count} amsRows={rows.Count} folderStays={folderStays} inGrs={inGrs} main={mainBefore}->{mainAfter}");

        File.WriteAllBytes(AmsMcsFiles.AgentConfigPath(home.Project), configBefore);
        var again = await AmsMcsSteps.DeleteAsync(context, ams.On, "agent delete again from the restored .config, the bot and AMS's records being gone", home.Project, created.AgentId, "--deployment-id", dev.DeploymentId);
        var forgotten = AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(home.Project), created.AgentId) is null;
        context.Assert.True(again.Status == "agent-deleted" && again.McsAgentAlreadyAbsent && again.AmsRecordDeleted == true && forgotten, "the repeat takes AMS's 404 as deleted: agent-deleted, already absent, amsRecordDeleted, .config cleared", $"status={again.Status} alreadyAbsent={again.McsAgentAlreadyAbsent} amsRecordDeleted={again.AmsRecordDeleted} forgotten={forgotten}");

        var mate = AmsMcsSteps.EmptyFolder(context, "mate");
        await AmsMcsSteps.InitUndoneAsync(context, "of R without --agent-name into an empty folder", mate, mate, ["--repository-id", home.RepositoryId], 99002, "a teammate's agent init --repository-id R without --agent-name, while R holds the deleted agent's folder, fails after its clone with 99002 (exit 2), naming that folder, which AMS doesn't list, and R (U16)", $"AMS doesn't list the agent in folder {folder} for repository {registration.RepositoryName ?? home.RepositoryId}, so `msagent agent init` would register it as a new agent. Pass --agent-name to do that, or remove the folder from the repository if its agent was deleted.");
        await AmsMcsSteps.SyncAsync(context, home.Project);
        await AmsMcsSteps.PushRemovalAsync(context, home.Project, folder);
    });

    private static Scenario PullWithAmsOn() => Builder.Make("AM-18", "agent pull with AMS on is main's pull: it works with every AMS endpoint unreachable, and GRS doesn't change", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var created = await AmsMcsSteps.CreateAsync(context, ams.On, home.Project, AmsMcsSteps.Name(context, "Pull"));
        var folder = AmsMcsSteps.Registered(context, created).Folder;
        var dev = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, home.Project, created.AgentId, "development", "dev", null);
        var deployed = await AmsMcsSteps.DeployAsync(context, ams.On, home.Project, created.AgentId, dev.DeploymentId);
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, home.Project, folder);
        var pulledName = AmsMcsSteps.Name(context, "Pulled");
        await AmsMcsSteps.RenameBotAsync(context, deployed.TargetMcsAgentId!, pulledName);
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);

        try
        {
            var pulled = await AmsMcsSteps.PullAsync(context, ams.Unreachable, home.Project, created.AgentId);
            var displayName = AmsMcsText.DisplayName(AmsMcsFiles.ReadSettings(Path.Combine(home.Project, folder)));
            var changed = await ams.Git.StatusAsync(home.Project, [folder], context.CancellationToken);
            var mainAfter = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
            context.Assert.True(pulled.Status == "agent-pulled" && string.Equals(displayName, pulledName, StringComparison.Ordinal) && changed.Count > 0 && GitCommits.Match(mainAfter, mainBefore), "main's pull: the portal's name is in the folder, uncommitted; GRS main is unchanged", $"status={pulled.Status} displayName={displayName} changed={changed.Count} main={mainBefore}->{mainAfter}");
        }
        finally
        {
            await ams.Git.RestoreAsync(home.Project, [folder], context.CancellationToken);
        }
    });

    private static Scenario PullAfterRemoteDeploy() => Builder.Make("AM-19", "pull after a remote deploy (P30 to P32): a deploy of the commit a clean folder holds connects it, a teammate's init merges a portal edit and connects its folder, another branch's commit leaves an unconnected teammate's folder alone (10011), the connected folder pulls the change, and a downloaded knowledge file keeps its CRLF bytes", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var x = await AmsMcsSteps.RemoteAsync(context);
        var ignore = AmsMcsFiles.ReadRootIgnore(x.Project);
        context.Assert.True(AmsMcsFiles.HasLine(ignore, AmsMcsFiles.ConfigIgnoreLine) && AmsMcsFiles.HasLine(ignore, AmsMcsFiles.CacheIgnoreLine), $"the root .gitignore holds {AmsMcsFiles.ConfigIgnoreLine} and {AmsMcsFiles.CacheIgnoreLine} (P31)", $".gitignore: {string.Join(" | ", ignore ?? [])}");
        context.Assert.True(AmsMcsFiles.CacheFingerprint(x.Project, x.Folder) is null, "(setup) X's folder has no .mcs: nothing has connected it yet", Path.Combine(x.Project, x.Folder, WorkflowStateCache.FolderName));
        await AmsMcsSteps.PullRefusedAsync(context, ams.On, x.Agent, "before any deploy");
        await AmsMcsSteps.PullRefusedAsync(context, ams.Off, x.Agent, "before any deploy");

        var main = await AmsMcsSteps.RemoteMainAsync(context, x.Project);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) X's folder holds GRS main exactly: HEAD is main's head, and the folder is clean", x.Agent, main);
        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, x.Project, x.AgentId, x.DevDeploymentId);
        var bots = await ams.Dataverse.BotsNamedAsync(ams.Config.DevEnvironmentId!, x.Created.SchemaName!, context.CancellationToken);
        var slot = await AmsMcsSteps.SlotAsync(context, x.Project, x.AgentId, x.DevDeploymentId);
        context.Assert.True(deployed.Status == "deployed" && bots.Count == 1 && GitCommits.Match(deployed.CommitSha, main) && GitCommits.Match(slot?.LastDeployedCommitSha, main), "the deploy made X's bot in Dev (one bot with its schema name) and deployed GRS main, which AMS records", $"commitSha={deployed.CommitSha} main={main} bots={AmsMcsResults.Describe(bots)} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var botId = bots[0].BotId;
        AmsMcsSteps.AssertConnected(context, "the deploy connected X's folder, which had no .mcs", x.Agent, deployed, botId);
        await AmsMcsSteps.AssertPullUpToDateAsync(context, "right after the first remote deploy", x.Agent);

        var portal = AmsMcsSteps.Name(context, "Portal19");
        await AmsMcsSteps.RenameBotAsync(context, botId, portal);
        await AmsMcsSteps.AssertPulledAsync(context, "the pull after the portal edit brought only it", x.Agent, await AmsMcsSteps.PullAsync(context, ams.On, x.Project, x.AgentId), portal);

        var teammate = await AmsMcsSteps.TeammateAsync(context, "X", x.Agent, x.RepositoryId, [x.AgentId]);
        await AmsMcsSteps.AssertOnlySettingsChangedAsync(context, "the teammate's init merged the portal rename into the folder as an uncommitted change", teammate, portal);
        await AmsMcsSteps.DisconnectAsync(context, teammate);
        await AmsMcsSteps.PullRefusedAsync(context, ams.On, teammate, "the teammate");
        var captured = await AmsMcsSteps.CaptureAsync(context, teammate.Project, x.AgentId);
        await ams.Git.PullAsync(teammate.Project, AmsMcsSteps.Branch, context.CancellationToken);
        var captureCommit = await ams.Git.RevParseAsync(teammate.Project, "HEAD", context.CancellationToken);
        main = await AmsMcsSteps.RemoteMainAsync(context, teammate.Project);
        var mateSettings = AmsMcsFiles.SettingsPath(teammate.Project, x.Folder);
        var mateName = AmsMcsText.DisplayName(File.ReadAllText(mateSettings));
        context.Assert.True(captured.Status == "captured" && GitCommits.Match(captured.CommitSha, captureCommit) && GitCommits.Match(main, captureCommit) && mateName == portal, "the teammate follows the remedy, agent capture then git pull: the portal edit reached the teammate's folder; HEAD is the capture commit, GRS main; displayName is the portal's name", $"status={captured.Status} commit={captured.CommitSha} head={captureCommit} main={main} displayName='{mateName}'");

        var branch = $"ahe2e/am19-{context.RunSuffix}";
        var branchName = AmsMcsSteps.Name(context, "Branch19");
        await ams.Git.SwitchAsync(teammate.Project, branch, create: true, context.CancellationToken);
        var b1 = await AmsMcsSteps.CommitDisplayNameAsync(context, teammate.Project, x.Folder, branchName, branch);
        await ams.Git.SwitchAsync(teammate.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        var remoteBranch = await ams.Git.RemoteHeadAsync(teammate.Project, branch, context.CancellationToken);
        var checkedOut = await ams.Git.CurrentBranchAsync(teammate.Project, context.CancellationToken);
        var mateHead = await ams.Git.RevParseAsync(teammate.Project, "HEAD", context.CancellationToken);
        context.Assert.True(GitCommits.Match(remoteBranch, b1) && checkedOut == AmsMcsSteps.Branch && GitCommits.Match(mateHead, captureCommit), $"(setup) GRS holds branch {branch} at its commit b1, and the teammate has main checked out again", $"branch={remoteBranch} b1={b1} checkedOut={checkedOut} head={mateHead}");
        var before = AmsFolderState.Read(teammate.Project, x.Folder, x.AgentId);
        var branchDeploy = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, teammate.Project, x.AgentId, x.DevDeploymentId, "--commit-id", b1);
        var bot = await AmsMcsSteps.BotNameAsync(context, botId);
        slot = await AmsMcsSteps.SlotAsync(context, teammate.Project, x.AgentId, x.DevDeploymentId);
        context.Assert.True(branchDeploy.Status == "deployed" && GitCommits.Match(branchDeploy.CommitSha, b1) && string.IsNullOrEmpty(branchDeploy.CapturedCommitSha) && bot == branchName && GitCommits.Match(slot?.LastDeployedCommitSha, b1), "deployed b1: commitSha is b1 (no capture: Dev had no pending edit), the bot has the branch's name, and AMS's Dev holds b1", $"commitSha={branchDeploy.CommitSha} capturedCommitSha={branchDeploy.CapturedCommitSha} bot='{bot}' lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        AmsMcsSteps.AssertLeftAlone(context, "the teammate's folder holds main, not b1", teammate, branchDeploy, before, AmsNoticeReason.OtherCommit, connected: false, captureCommit, b1);
        await AmsMcsSteps.PullRefusedAsync(context, ams.On, teammate, "so the teammate");
        var mateHeadNow = await ams.Git.RevParseAsync(teammate.Project, "HEAD", context.CancellationToken);
        var checkedOutNow = await ams.Git.CurrentBranchAsync(teammate.Project, context.CancellationToken);
        var mateNameNow = AmsMcsText.DisplayName(File.ReadAllText(mateSettings));
        context.Assert.True(GitCommits.Match(mateHeadNow, captureCommit) && checkedOutNow == AmsMcsSteps.Branch && mateNameNow == portal, "the teammate's working tree is still main's: HEAD is the capture commit on main, and displayName is the portal's name, not the branch's", $"head={mateHeadNow} branch={checkedOutNow} displayName='{mateNameNow}'");

        await ams.Git.RestoreAsync(x.Project, [x.Folder], context.CancellationToken);
        await ams.Git.PullAsync(x.Project, AmsMcsSteps.Branch, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) X's folder is on main again: HEAD is the capture commit, and the folder is clean", x.Agent, captureCommit);
        var connection = AmsMcsFiles.Connection(x.Project, x.Folder, x.AgentId);
        context.Assert.True(connection.Names(botId, ams.Config.DevEnvironmentId), "(setup) X's folder is still connected: .mcs\\conn.json is bound to the bot", $"{connection.Describe()}; bot={botId}");
        await AmsMcsSteps.AssertPulledAsync(context, "agent pull in X's connected folder on main brings b1's change in, with no line-ending-only modification", x.Agent, await AmsMcsSteps.PullAsync(context, ams.On, x.Project, x.AgentId), branchName);

        main = await AmsMcsSteps.RemoteMainAsync(context, teammate.Project);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) the teammate's folder holds GRS main exactly", teammate, main);
        var again = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, teammate.Project, x.AgentId, x.DevDeploymentId);
        bot = await AmsMcsSteps.BotNameAsync(context, botId);
        context.Assert.True(again.Status == "deployed" && GitCommits.Match(again.CommitSha, main) && string.IsNullOrEmpty(again.CapturedCommitSha) && bot == portal, "deployed main's head, the capture commit (no new capture); the bot has the portal's name again", $"commitSha={again.CommitSha} main={main} capturedCommitSha={again.CapturedCommitSha} bot='{bot}'");
        AmsMcsSteps.AssertConnected(context, "the deploy connected the teammate's folder", teammate, again, botId);
        await AmsMcsSteps.AssertPullUpToDateAsync(context, "the teammate, right after that deploy", teammate);

        var notes = AmsMcsSteps.Folder(context, "notes");
        var created = await AmsMcsSteps.CreateAsync(context, ams.On, notes, AmsMcsSteps.Name(context, "Notes"));
        var y = new AmsFolder(notes, created.AgentId, AmsMcsSteps.Registered(context, created).Folder);
        var fileName = "ahe2e-notes.txt";
        var text = $"Notes of the MCS-on-AMS E2E check {context.RunSuffix}.\nLine two.\nLine three.\n";
        await ams.Git.SetConfigAsync(notes, "core.autocrlf", "true", context.CancellationToken);
        await ams.Git.SetConfigAsync(notes, "core.safecrlf", "false", context.CancellationToken);
        var (content, component) = AmsMcsFiles.AddKnowledgeFile(notes, y.Folder, fileName, text);
        var k1 = await ams.Git.CommitAsync(notes, $"ahe2e: add the knowledge file {fileName}", [content, component], context.CancellationToken);
        await ams.Git.PushAsync(notes, AmsMcsSteps.Branch, context.CancellationToken);
        var contentPath = Path.Combine(notes, content);
        File.Delete(contentPath);
        File.Delete(Path.Combine(notes, component));
        await ams.Git.RestoreAsync(notes, [content, component], context.CancellationToken);

        var development = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, notes, y.AgentId, "development", "dev", null);
        var first = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, notes, y.AgentId, development.DeploymentId);
        context.Assert.True(first.Status == "deployed" && GitCommits.Match(first.CommitSha, k1) && first.WorkspaceConnected, "(setup) the first remote Dev deploy of Y, which uploads the knowledge file, deployed GRS main, which Y's folder holds, and connected the folder", $"commitSha={first.CommitSha} k1={k1} workspaceConnected={first.WorkspaceConnected} workspaceNotice='{first.WorkspaceNotice}' warnings={string.Join(" | ", first.Warnings)}");
        var projection = await AmsMcsSteps.CaptureAsync(context, notes, y.AgentId);
        await ams.Git.PullAsync(notes, AmsMcsSteps.Branch, context.CancellationToken);
        var notesHead = await ams.Git.RevParseAsync(notes, "HEAD", context.CancellationToken);
        var notesMain = await AmsMcsSteps.RemoteMainAsync(context, notes);
        context.Assert.True(projection.Status is "captured" or "unchanged" && GitCommits.Match(notesHead, notesMain), "(setup) agent capture right after the deploy is unchanged, or saved Dev's projection on main, which git pull brought into Y's folder", $"status={projection.Status} commit={projection.CommitSha} head={notesHead} main={notesMain}");
        var blobSize = await ams.Git.BlobSizeAsync(notes, $"HEAD:{content}", context.CancellationToken);
        var status = await ams.Git.StatusAsync(notes, [], context.CancellationToken);
        context.Assert.True(blobSize == Encoding.UTF8.GetByteCount(text) && AmsMcsFiles.HoldsWithCrlf(contentPath, text) && status.Count == 0, "(setup) the knowledge file is committed with LF and checked out with CRLF (core.autocrlf=true), and git status is clean", $"blob={blobSize} bytes (LF: {Encoding.UTF8.GetByteCount(text)}) git status: {GitTip.Describe(status)}");
        var checkout = AmsMcsFiles.FileHash(contentPath);

        var notesBranch = $"ahe2e/am19-notes-{context.RunSuffix}";
        var notesBranchName = AmsMcsSteps.Name(context, "Notes Branch");
        await ams.Git.SwitchAsync(notes, notesBranch, create: true, context.CancellationToken);
        var y2 = await AmsMcsSteps.CommitDisplayNameAsync(context, notes, y.Folder, notesBranchName, notesBranch);
        await ams.Git.SwitchAsync(notes, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        var second = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, notes, y.AgentId, development.DeploymentId, "--commit-id", y2);
        context.Assert.True(second.Status == "deployed" && GitCommits.Match(second.CommitSha, y2) && string.IsNullOrEmpty(second.CapturedCommitSha) && !second.WorkspaceConnected, "(setup) deployed y2, which uploads the knowledge file again; Y's folder, on main, doesn't hold it, so the deploy left its connection as it was", $"commitSha={second.CommitSha} y2={y2} capturedCommitSha={second.CapturedCommitSha} workspaceConnected={second.WorkspaceConnected} workspaceNotice='{second.WorkspaceNotice}'");
        var beforePull = AmsMcsFiles.FileHash(contentPath);
        var pulled = await AmsMcsSteps.PullAsync(context, ams.On, notes, y.AgentId);
        var afterPull = AmsMcsFiles.FileHash(contentPath);
        var listed = await ams.Git.StatusAsync(notes, [content], context.CancellationToken);
        var pulledName = AmsMcsText.DisplayName(File.ReadAllText(AmsMcsFiles.SettingsPath(notes, y.Folder)));
        status = await ams.Git.StatusAsync(notes, [], context.CancellationToken);
        context.Assert.True(pulled.Status == "agent-pulled" && pulledName == notesBranchName && checkout is not null && beforePull == checkout && afterPull == checkout && AmsMcsFiles.HoldsWithCrlf(contentPath, text) && listed.Count == 0, "agent pull brought y2's displayName, and the knowledge file it downloaded again kept its bytes: still CRLF, the SHA256 it had at checkout, and git status doesn't list it", $"status={pulled.Status} changes={pulled.ChangeCount} displayName='{pulledName}' knowledge file: checkout={checkout} beforePull={beforePull} afterPull={afterPull} listed={GitTip.Describe(listed)} git status: {GitTip.Describe(status)}");
    });

    private static Scenario CopiesPerCommit() => Builder.Make("AM-20", "a copy of .mcs per commit (P33): pull uses the commit's copy, a folder behind is put back with git restore, an uncommitted change or a diverged HEAD is refused, and agent delete forgets the copies", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var z = await AmsMcsSteps.CopiesAsync(context);
        var c1 = await AmsMcsSteps.RemoteMainAsync(context, z.Project);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) Z's folder holds GRS main, c1, exactly: HEAD is main's head, and the folder is clean", z.Agent, c1);
        var shelf = GitCli.ShelfOf(z.Project, z.Folder);
        context.Assert.True(AmsMcsFiles.CacheFingerprint(z.Project, z.Folder) is null && McsShelf.IsEmpty(shelf), "(setup) Z's folder has no .mcs and no copy of one: nothing has connected it yet", McsShelf.Describe(shelf));

        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, z.Project, z.AgentId, z.DevDeploymentId);
        var bots = await ams.Dataverse.BotsNamedAsync(ams.Config.DevEnvironmentId!, z.Created.SchemaName!, context.CancellationToken);
        context.Assert.True(deployed.Status == "deployed" && bots.Count == 1 && GitCommits.Match(deployed.CommitSha, c1) && deployed.WorkspaceConnected, "(setup) the deploy made Z's bot in Dev, deployed c1, and connected the folder, which holds c1", $"commitSha={deployed.CommitSha} c1={c1} bots={AmsMcsResults.Describe(bots)} workspaceConnected={deployed.WorkspaceConnected} workspaceNotice='{deployed.WorkspaceNotice}'");
        shelf = GitCli.ShelfOf(z.Project, z.Folder);
        context.Assert.True(shelf is not null && shelf.KeyIsValid && shelf.Names(z.Folder) && shelf.Holds(c1) && GitCommits.Match(shelf.Live, c1), "the connect saved a copy of .mcs for c1 in the folder's shelf .git\\msagent\\mcs\\<key>, whose key is 16 hex digits: <key>\\<c1> holds files, index.json names the folder and lists c1, and live is c1", $"c1={GitCommits.Short(c1)} {McsShelf.Describe(shelf)}");
        var shelfDirectory = shelf!.KeyDirectory;

        var branch = "ahe2e/am20-skill";
        var branchName = AmsMcsSteps.Name(context, "Branch20");
        await ams.Git.SwitchAsync(z.Project, branch, create: true, context.CancellationToken);
        var b1 = await AmsMcsSteps.CommitDisplayNameAsync(context, z.Project, z.Folder, branchName, branch);
        var branchDeploy = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, z.Project, z.AgentId, z.DevDeploymentId, "--commit-id", b1);
        var bot = await AmsMcsSteps.BotNameAsync(context, bots[0].BotId);
        context.Assert.True(branchDeploy.Status == "deployed" && GitCommits.Match(branchDeploy.CommitSha, b1) && string.IsNullOrEmpty(branchDeploy.CapturedCommitSha) && bot == branchName && branchDeploy.WorkspaceConnected, $"deployed b1, the head of {branch}: commitSha is b1 (no capture), the bot has the branch's name, and the folder, which holds b1, is connected", $"commitSha={branchDeploy.CommitSha} b1={b1} capturedCommitSha={branchDeploy.CapturedCommitSha} bot='{bot}' workspaceConnected={branchDeploy.WorkspaceConnected} workspaceNotice='{branchDeploy.WorkspaceNotice}'");
        shelf = GitCli.ShelfOf(z.Project, z.Folder);
        context.Assert.True(shelf is not null && shelf.Holds(b1) && shelf.Holds(c1), "that connect saved a copy for b1, beside c1's", $"b1={GitCommits.Short(b1)} {McsShelf.Describe(shelf)}");

        await ams.Git.SwitchAsync(z.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) Z's folder is on main again: HEAD is c1, and the folder is clean", z.Agent, c1);
        await AmsMcsSteps.AssertPulledAsync(context, "on main, agent pull puts c1's copy in place and brings b1's deploy in", z.Agent, await AmsMcsSteps.PullAsync(context, ams.On, z.Project, z.AgentId), branchName);
        await ams.Git.RestoreAsync(z.Project, [z.Folder], context.CancellationToken);
        await ams.Git.SwitchAsync(z.Project, branch, create: false, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, $"(setup) after git restore and git switch {branch}, Z's folder holds b1 exactly", z.Agent, b1);
        await AmsMcsSteps.AssertPullUpToDateAsync(context, $"on {branch}, agent pull puts b1's copy in place", z.Agent);

        var teammate = await AmsMcsSteps.TeammateAsync(context, "Z", z.Agent, z.RepositoryId, [z.AgentId], "isn't HEAD or an ancestor of it");
        var mateShelf = GitCli.ShelfOf(teammate.Project, z.Folder);
        context.Assert.True(McsShelf.IsEmpty(mateShelf), "(setup) the teammate's folder has no copy of .mcs either", McsShelf.Describe(mateShelf));
        await ams.Git.SwitchAsync(teammate.Project, branch, create: false, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, $"(setup) the teammate switched to {branch}: the folder holds b1 exactly", teammate, b1);
        var mateDeploy = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, teammate.Project, z.AgentId, z.DevDeploymentId, "--commit-id", b1);
        mateShelf = GitCli.ShelfOf(teammate.Project, z.Folder);
        context.Assert.True(mateDeploy.Status == "deployed" && GitCommits.Match(mateDeploy.CommitSha, b1) && mateDeploy.WorkspaceConnected && mateShelf is not null && mateShelf.Holds(b1) && mateShelf.Copies.Count == 1, "(setup) that deploy of b1 connected the teammate's folder and saved its only copy, b1's", $"commitSha={mateDeploy.CommitSha} capturedCommitSha={mateDeploy.CapturedCommitSha} workspaceConnected={mateDeploy.WorkspaceConnected} workspaceNotice='{mateDeploy.WorkspaceNotice}' {McsShelf.Describe(mateShelf)}");

        var diverged = "ahe2e/am20-diverged";
        var mateSettings = AmsMcsFiles.SettingsPath(teammate.Project, z.Folder);
        await ams.Git.SwitchAsync(teammate.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        await ams.Git.SwitchAsync(teammate.Project, diverged, create: true, context.CancellationToken);
        AmsMcsFiles.AddYamlComment(mateSettings, $"ahe2e on {diverged} {context.RunSuffix}");
        var d1 = await ams.Git.CommitAsync(teammate.Project, $"ahe2e: a comment on {diverged}", [AmsMcsFiles.SettingsEntry(z.Folder)], context.CancellationToken);
        AmsMcsFiles.AddYamlComment(mateSettings, $"ahe2e uncommitted {context.RunSuffix}");
        await AmsMcsSteps.PullKeepsFolderAsync(context, $"neither d1, the head of {diverged}, nor an ancestor has a copy, and the folder has an uncommitted change", teammate, "commit or stash");
        await ams.Git.RestoreAsync(teammate.Project, [z.Folder], context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) git restore discarded the uncommitted change: the teammate's folder holds d1 exactly", teammate, d1);
        var split = await AmsMcsSteps.PullKeepsFolderAsync(context, "clean, with no copy at or below d1, which isn't an ancestor of b1 (diverged)", teammate, "git merge");
        var status = await ams.Git.StatusAsync(teammate.Project, [], context.CancellationToken);
        context.Assert.True(status.Count == 0, "and the diverged folder's git status stays clean", $"git status: {GitTip.Describe(status)} namesB1={AmsMcsText.NamesCommit(split.FailureText, b1)}");

        await ams.Git.SwitchAsync(teammate.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) the teammate's folder is on main: HEAD is c1, and the folder is clean", teammate, c1);
        var ancestor = await ams.Git.IsAncestorAsync(teammate.Project, c1!, b1, context.CancellationToken);
        mateShelf = GitCli.ShelfOf(teammate.Project, z.Folder);
        context.Assert.True(ancestor && mateShelf is not null && !mateShelf.Holds(c1) && mateShelf.Holds(b1), "(setup) main is behind: c1 has no copy in the teammate's shelf, and it's an ancestor of b1, which has one", $"merge-base --is-ancestor c1 b1: {ancestor} | {McsShelf.Describe(mateShelf)}");
        var behind = await AmsMcsSteps.PullAsync(context, ams.On, teammate.Project, z.AgentId);
        context.Assert.True(AmsMcsText.Mentions(behind.Warnings, "git restore"), "agent pull on main, which is behind b1, restores b1's version of the folder (git restore --source) and pulls: exit 0, with a warning that names git restore", $"status={behind.Status} warnings={string.Join(" | ", behind.Warnings)}");
        await AmsMcsSteps.AssertOnlySettingsChangedAsync(context, "after that restore", teammate, branchName);
        mateShelf = GitCli.ShelfOf(teammate.Project, z.Folder);
        context.Assert.True(mateShelf is not null && mateShelf.Holds(c1) && mateShelf.Holds(b1), "that pull saved a copy for c1, main's head, beside b1's", $"c1={GitCommits.Short(c1)} {McsShelf.Describe(mateShelf)}");

        var deleted = await AmsMcsSteps.DeleteAsync(context, ams.On, "agent delete Z, in its first folder", z.Project, z.AgentId, "--deployment-id", z.DevDeploymentId);
        var left = GitCli.ShelfOf(z.Project, z.Folder);
        context.Assert.True(deleted.Status == "agent-deleted" && !Directory.Exists(shelfDirectory) && left is null, "agent delete deleted Z and forgot its shelf in the folder it ran in: .git\\msagent\\mcs\\<key> is gone, and no index.json there names the folder", $"status={deleted.Status} amsRecordDeleted={deleted.AmsRecordDeleted} | {shelfDirectory}: {(Directory.Exists(shelfDirectory) ? "still there" : "gone")} | {McsShelf.Describe(left)}");
        await ams.Git.SwitchAsync(z.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        await AmsMcsSteps.SyncAsync(context, z.Project);
        await AmsMcsSteps.PushRemovalAsync(context, z.Project, z.Folder);
    });

    private static Scenario CreateOrder() => Builder.Make("AM-21", "the create order: a bad schema name or template fails with main's usage error before any AMS work, and nothing asks", Home, SafetyTier.ReadOnly, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var countBefore = (await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId)).Agents.Count;

        foreach (var (what, label, extra, text) in new (string What, string Label, string[] Extra, string Text)[] { ("a bad --schema-name", "Schema", ["--schema-name", "not valid"], "The schema name 'not valid' contains unsupported characters. Use letters, digits, and the characters _ - . { } !"), ("--mcs-agent-type CLI --init-template default", "Template", ["--mcs-agent-type", "CLI", "--init-template", "default"], "--init-template applies to --mcs-agent-type Classic. The cli-copilot scaffold has a single shape.") })
        {
            var project = AmsMcsSteps.Folder(context, label.ToLowerInvariant());
            var refused = await ams.On.RunAsync([.. AmsMcsSteps.ScaffoldArguments(context, AmsMcsSteps.Name(context, label), project, ["--repository-id", home.RepositoryId, .. extra]), "--json"], ams.WorkspaceRoot, timeout: TimeSpan.FromSeconds(120), cancellationToken: context.CancellationToken);
            var outcome = ams.Record(context.Ledger, refused, project, amsOff: false, context.ScenarioId);
            context.Assert.Refused(refused, 99002, $"agent create --repository-id with {what}, without --non-interactive and with stdin closed, fails with main's usage error (99002, exit 2)", 2, text);
            context.Assert.True(!AmsMcsText.HasQuestion(refused.Output) && outcome.Status != "cancelled", $"{what}: no question was asked: msagent ended by itself within 120s, wrote no [Y/n] prompt and didn't report cancelled", $"exit={refused.ExitCode} {refused.Duration.TotalSeconds:N1}s status={outcome.Status}");
            context.Assert.True(AmsMcsFiles.IsMissingOrEmpty(project), $"{what}: the target folder is missing or empty", project);
        }

        var countAfter = (await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId)).Agents.Count;
        var mainAfter = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        context.Assert.True(countAfter == countBefore && AmsIds.Same(mainAfter, mainBefore), "nothing reached AMS or GRS: the repository has as many AMS agents, and GRS main is unchanged", $"agents={countBefore}->{countAfter} main={mainBefore}->{mainAfter}");
    });

    private static Scenario RemoteDeployCases() => Builder.Make("AM-25", "P30's other cases on X: an --async remote Dev deploy, then one with a commit that isn't pushed, one from a copy of the project without .git, and one with git off PATH; each leaves the folder's connection to Dev as it was", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var x = await AmsMcsSteps.RemoteAsync(context);
        await ams.Git.RestoreAsync(x.Project, [x.Folder], context.CancellationToken);
        await AmsMcsSteps.SyncAsync(context, x.Project);
        var main = await AmsMcsSteps.RemoteMainAsync(context, x.Project);
        var connected = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, x.Project, x.AgentId, x.DevDeploymentId);
        context.Assert.True(connected.Status == "deployed" && GitCommits.Match(connected.CommitSha, main), "(setup) the remote Dev deploy deployed GRS main, which X's folder holds", $"commitSha={connected.CommitSha} main={main}");
        AmsMcsSteps.AssertConnected(context, "(setup) so it connected X's folder", x.Agent, connected, AmsMcsFiles.RecordedBot(x.Project, x.AgentId));

        var head = await ams.Git.RevParseAsync(x.Project, "HEAD", context.CancellationToken);
        var configBefore = File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(x.Project));
        var before = AmsFolderState.Read(x.Project, x.Folder, x.AgentId);
        var started = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, x.Project, x.AgentId, x.DevDeploymentId, "--async");
        context.Assert.True(started.Status == "deploying" && !string.IsNullOrEmpty(started.OperationId) && !started.WorkspaceConnected && string.IsNullOrEmpty(started.WorkspaceNotice), "the --async remote Dev deploy returns deploying with an operation id and no workspaceNotice", $"status={started.Status} operationId={started.OperationId} workspaceConnected={started.WorkspaceConnected} workspaceNotice='{started.WorkspaceNotice}'");
        var operation = await ams.WaitForOperationAsync(x.Project, x.AgentId, started.OperationId, AmsMcsSteps.LongCall, TimeSpan.FromSeconds(10), context.CancellationToken);
        var headAfter = await ams.Git.RevParseAsync(x.Project, "HEAD", context.CancellationToken);
        var status = await ams.Git.StatusAsync(x.Project, [], context.CancellationToken);
        var after = AmsFolderState.Read(x.Project, x.Folder, x.AgentId);
        context.Assert.True(GitCommits.Match(headAfter, head) && status.Count == 0 && before.Kept(after, connected: true) && File.ReadAllBytes(AmsMcsFiles.AgentConfigPath(x.Project)).SequenceEqual(configBefore), "once agent operation show reports it done, the folder, its .mcs and .config are as before", $"operation={operation.Status} head={GitCommits.Short(head)}->{GitCommits.Short(headAfter)} git status: {GitTip.Describe(status)} {before.Describe(after)}");

        await ams.Git.SwitchAsync(x.Project, $"ahe2e/am25-local-{context.RunSuffix}", create: true, context.CancellationToken);

        try
        {
            var unpushed = await AmsMcsSteps.CommitDisplayNameAsync(context, x.Project, x.Folder, AmsMcsSteps.Name(context, "Local25"), push: false);
            before = AmsFolderState.Read(x.Project, x.Folder, x.AgentId);
            var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, x.Project, x.AgentId, x.DevDeploymentId);
            context.Assert.True(deployed.Status == "deployed" && GitCommits.Match(deployed.CommitSha, main) && AmsMcsText.WarnsLocalChanges(deployed.Warnings, deployed.CommitSha), "the remote Dev deploy from a folder whose HEAD isn't pushed deployed GRS main, and warned that the local changes aren't in it", $"commitSha={deployed.CommitSha} main={main} head={unpushed} warnings={string.Join(" | ", deployed.Warnings)}");
            AmsMcsSteps.AssertLeftAlone(context, "the folder holds a commit that isn't pushed to GRS", x.Agent, deployed, before, AmsNoticeReason.Unpushed, connected: true);
        }
        finally
        {
            await ams.Git.SwitchAsync(x.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        }

        var copy = x.Agent with { Project = AmsMcsSteps.Folder(context, "no-git") };
        AmsMcsFiles.CopyWithout(x.Project, copy.Project, [".git"]);

        foreach (var (what, msagent, folder) in new (string What, MsagentCli Msagent, AmsFolder Folder)[] { ("from a copy of the project without .git", ams.On, copy), ("with git off PATH", ams.NoGit, x.Agent) })
        {
            before = AmsFolderState.Read(folder.Project, folder.Folder, folder.AgentId);
            var deployed = await AmsMcsSteps.RemoteDeployAsync(context, msagent, folder.Project, x.AgentId, x.DevDeploymentId);
            context.Assert.True(deployed.Status == "deployed" && GitCommits.Match(deployed.CommitSha, main), $"the remote Dev deploy {what} deployed GRS main", $"commitSha={deployed.CommitSha} main={main}");
            AmsMcsSteps.AssertLeftAlone(context, $"the deploy {what}: git can't tell which commit the folder holds", folder, deployed, before, AmsNoticeReason.GitCannotTell, connected: true);
        }

        await AmsMcsSteps.HoldsCommitAsync(context, "X's folder is back at the pushed head", x.Agent, main);
    });

    private static Scenario CopyCases() => Builder.Make("AM-26", "P33's other cases in Z's repository: a clone with --repository-id and a local Dev deploy each save a copy for HEAD; a HEAD with no copy of its own pulls with its nearest ancestor's; a folder behind with an uncommitted change is refused", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var z = await AmsMcsSteps.CopiesAsync(context);
        var source = await AmsMcsSteps.AmsOffSourceAsync(context, "Shelf Source");
        var project = AmsMcsSteps.Folder(context, "clone");
        var cloned = await AmsMcsSteps.CloneAsync(context, source, project, "--repository-id", z.RepositoryId);
        var registration = AmsMcsSteps.Registered(context, cloned);
        var agent = new AmsFolder(project, cloned.AgentId, registration.Folder);
        var head = await ams.Git.RevParseAsync(project, "HEAD", context.CancellationToken);
        context.Assert.True(registration is { Registered: true, RepositoryCreated: false, Pushed: true } && AmsIds.Same(registration.RepositoryId, z.RepositoryId) && GitCommits.Match(head, registration.CommitSha), "the clone joined Z's repository with --repository-id, and HEAD is its pushed commit", $"repository={registration.RepositoryId} created={registration.RepositoryCreated} head={head} commitSha={registration.CommitSha}");
        var shelf = GitCli.ShelfOf(project, agent.Folder);
        context.Assert.True(shelf is not null && shelf.KeyIsValid && shelf.Names(agent.Folder) && shelf.IdentifiesAs(ams.Config.DevEnvironmentId, source.BotId) && GitCommits.Match(shelf.Live, head) && shelf.Holds(head) && shelf.Complete && shelf.NewestFirst, "the clone saved a copy of .mcs for HEAD under .git\\msagent\\mcs: index.json names the folder, its identity is Dev and the Copilot Studio agent, live is HEAD, and each copy has its commit, branch and savedAt, newest first", $"head={GitCommits.Short(head)} {McsShelf.Describe(shelf)}");

        var branch = "ahe2e/am26-a";
        var note = "ahe2e-am26.txt";
        await ams.Git.SwitchAsync(project, branch, create: true, context.CancellationToken);
        File.WriteAllText(Path.Combine(project, note), $"ahe2e {context.RunSuffix}\n");
        var a1 = await ams.Git.CommitAsync(project, $"ahe2e: {note} on {branch}", [note], context.CancellationToken);
        await ams.Git.PushAsync(project, branch, context.CancellationToken);
        await AmsMcsSteps.DeployAsync(context, ams.On, project, agent.AgentId, cloned.DeploymentId!);
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, project, agent.Folder);
        shelf = GitCli.ShelfOf(project, agent.Folder);
        context.Assert.True(shelf is not null && GitCommits.Match(shelf.Live, a1) && shelf.Holds(a1) && shelf.Holds(head) && shelf.Complete && shelf.NewestFirst, $"the local Dev deploy on {branch} saved a copy for HEAD, a1, beside the clone's; live is a1", $"a1={GitCommits.Short(a1)} {McsShelf.Describe(shelf)}");

        await ams.Git.SwitchAsync(project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) back on main, the folder holds the clone's commit exactly", agent, head);
        await AmsMcsSteps.DeployAsync(context, ams.On, project, agent.AgentId, cloned.DeploymentId!);
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, project, agent.Folder);
        shelf = GitCli.ShelfOf(project, agent.Folder);
        context.Assert.True(shelf is not null && GitCommits.Match(shelf.Live, head) && shelf.Holds(head) && shelf.Holds(a1) && shelf.Complete && shelf.NewestFirst, "the local Dev deploy on main took HEAD's own copy and saved it again: live is HEAD, both copies are kept, newest first", $"head={GitCommits.Short(head)} {McsShelf.Describe(shelf)}");

        var next = AmsMcsSteps.Name(context, "Next26");
        var n1 = await AmsMcsSteps.CommitDisplayNameAsync(context, project, agent.Folder, next);
        shelf = GitCli.ShelfOf(project, agent.Folder);
        context.Assert.True(shelf is not null && !shelf.Holds(n1) && shelf.Holds(head), "(setup) n1, the new HEAD on main, has no copy of its own, and its parent, the clone's commit, has one", McsShelf.Describe(shelf));
        var pulled = await AmsMcsSteps.PullAsync(context, ams.On, project, agent.AgentId);
        var name = AmsMcsText.DisplayName(AmsMcsFiles.ReadSettings(Path.Combine(project, agent.Folder)));
        shelf = GitCli.ShelfOf(project, agent.Folder);
        context.Assert.True(pulled.Status == "up-to-date" && name == next && !AmsMcsText.Mentions(pulled.Warnings, "git restore") && shelf is not null && shelf.Holds(n1) && GitCommits.Match(shelf.Live, n1), "a HEAD with no copy of its own pulls with its nearest ancestor's copy: up-to-date, n1's name kept, no git restore; the pull saved a copy for n1, now live", $"status={pulled.Status} changes={pulled.ChangeCount} displayName='{name}' warnings={string.Join(" | ", pulled.Warnings)} {McsShelf.Describe(shelf)}");

        var teammate = await AmsMcsSteps.TeammateAsync(context, "every agent of Z's repository", agent, z.RepositoryId, AmsMcsResults.Ids((await AmsMcsSteps.AmsAgentsAsync(context, z.RepositoryId)).Agents));
        await AmsMcsSteps.DisconnectAsync(context, teammate);
        var mateBranch = "ahe2e/am26-b";
        await ams.Git.SwitchAsync(teammate.Project, mateBranch, create: true, context.CancellationToken);
        var b1 = await AmsMcsSteps.CommitDisplayNameAsync(context, teammate.Project, agent.Folder, AmsMcsSteps.Name(context, "Branch26"), mateBranch);
        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, teammate.Project, agent.AgentId, cloned.DeploymentId!, "--commit-id", b1);
        var mateShelf = GitCli.ShelfOf(teammate.Project, agent.Folder);
        context.Assert.True(deployed.Status == "deployed" && GitCommits.Match(deployed.CommitSha, b1) && deployed.WorkspaceConnected && mateShelf is not null && mateShelf.Holds(b1), $"(setup) the teammate's remote Dev deploy of b1, the head of {mateBranch}, connected the folder and saved a copy for b1", $"commitSha={deployed.CommitSha} workspaceConnected={deployed.WorkspaceConnected} workspaceNotice='{deployed.WorkspaceNotice}' {McsShelf.Describe(mateShelf)}");
        await ams.Git.SwitchAsync(teammate.Project, AmsMcsSteps.Branch, create: false, context.CancellationToken);
        await AmsMcsSteps.HoldsCommitAsync(context, "(setup) the teammate's folder is on main again: HEAD is n1, and the folder is clean", teammate, n1);
        var ancestor = await ams.Git.IsAncestorAsync(teammate.Project, n1, b1, context.CancellationToken);
        mateShelf = GitCli.ShelfOf(teammate.Project, agent.Folder);
        context.Assert.True(ancestor && mateShelf is not null && !mateShelf.Holds(n1) && mateShelf.Holds(b1), "(setup) main is behind: n1 has no copy in the teammate's shelf, and it's an ancestor of b1, which has one", $"merge-base --is-ancestor n1 b1: {ancestor} | {McsShelf.Describe(mateShelf)}");
        AmsMcsFiles.AddYamlComment(AmsMcsFiles.SettingsPath(teammate.Project, agent.Folder), $"ahe2e uncommitted {context.RunSuffix}");
        await AmsMcsSteps.PullKeepsFolderAsync(context, "a folder that is behind, with an uncommitted change", teammate, "commit or stash");
    });

    private static Scenario AgentsAndWorkflows() => Builder.Make("AM-28", "agents and workflows in one repository: a workflow created in P joins R through the folder's remote, another joins it with --repository-id, AMS lists both kinds, and a teammate's init restores them all", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        await AmsMcsSteps.SyncAsync(context, home.Project);
        var mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var nameW1 = AmsMcsSteps.Name(context, "Flow W1");
        var w1 = await AmsMcsSteps.CreateWorkflowAsync(context, home.Project, nameW1);
        var registrationW1 = AmsMcsSteps.Registered(context, w1);
        context.Assert.True(w1.Status == "agent-created" && w1.AgentType == AgentTypes.McsWorkflow && registrationW1 is { Registered: true, RepositoryCreated: false, Pushed: true } && AmsIds.Same(registrationW1.RepositoryId, home.RepositoryId), "W1, created in P, joined R through the folder's remote (repositoryCreated false) and was pushed", $"agentType={w1.AgentType} repository={registrationW1.RepositoryId} created={registrationW1.RepositoryCreated} folder={registrationW1.Folder} pushed={registrationW1.Pushed}");
        await AmsMcsSteps.AssertCloneAsync(context, home.Project, registrationW1.GitUrl, registrationW1.CommitSha, $"msagent: add MCSWorkflow {nameW1}", [registrationW1.Folder], mainBefore, newRepository: false);

        mainBefore = await AmsMcsSteps.RemoteMainAsync(context, home.Project);
        var flowProject = AmsMcsSteps.Folder(context, "flow");
        var nameW2 = AmsMcsSteps.Name(context, "Flow W2");
        var w2 = await AmsMcsSteps.CreateWorkflowAsync(context, flowProject, nameW2, "--repository-id", home.RepositoryId);
        var registrationW2 = AmsMcsSteps.Registered(context, w2);
        context.Assert.True(w2.Status == "agent-created" && w2.AgentType == AgentTypes.McsWorkflow && registrationW2 is { Registered: true, RepositoryCreated: false, Pushed: true } && AmsIds.Same(registrationW2.RepositoryId, home.RepositoryId), "W2, created with --repository-id R into a new folder, joined R (repositoryCreated false) and was pushed", $"agentType={w2.AgentType} repository={registrationW2.RepositoryId} created={registrationW2.RepositoryCreated} folder={registrationW2.Folder} pushed={registrationW2.Pushed}");
        await AmsMcsSteps.AssertCloneAsync(context, flowProject, registrationW2.GitUrl, registrationW2.CommitSha, $"msagent: add MCSWorkflow {nameW2}", [registrationW2.Folder], mainBefore, newRepository: false);

        var listed = await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId);
        context.Assert.True(AmsMcsResults.Holding(listed, home.AgentId, home.Folder, AgentTypes.McsAgent).Count == 1 && AmsMcsResults.Holding(listed, w1.AgentId, registrationW1.Folder, AgentTypes.McsWorkflow).Count == 1 && AmsMcsResults.Holding(listed, w2.AgentId, registrationW2.Folder, AgentTypes.McsWorkflow).Count == 1, "AMS lists R's agents and both workflows, each with its type and folder", AmsMcsResults.Describe(listed));
        var workflowsOnly = await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId, AgentTypes.McsWorkflow);
        context.Assert.True(AmsMcsResults.OnlyOfType(workflowsOnly, AgentTypes.McsWorkflow) && AmsMcsResults.Holding(workflowsOnly, w1.AgentId, registrationW1.Folder).Count == 1 && AmsMcsResults.Holding(workflowsOnly, w2.AgentId, registrationW2.Folder).Count == 1, "with --agent-type MCSWorkflow, AMS lists only the workflows, both of them", AmsMcsResults.Describe(workflowsOnly));
        var mismatched = AmsMcsFiles.Mismatched(AmsMcsFiles.ReadAgentConfig(home.Project), [.. AmsMcsResults.Holding(listed, home.AgentId), .. AmsMcsResults.Holding(listed, w1.AgentId)], home.RepositoryId);
        context.Assert.True(mismatched.Count == 0, "P's .config holds both kinds, A and W1, each with its type, R, main and its own folder", string.Join("; ", mismatched));

        var mate = AmsMcsSteps.EmptyFolder(context, "mate");
        var restored = await AmsMcsSteps.InitAsync(context, "a teammate's agent init --repository-id R into an empty folder", mate, "--repository-id", home.RepositoryId);
        listed = await AmsMcsSteps.AmsAgentsAsync(context, home.RepositoryId);
        context.Assert.True(restored.Status == "agent-initialized" && AmsMcsResults.Restores(restored.Ams, AmsMcsResults.Ids(listed.Agents)), "the teammate's init restores every agent and workflow of R", $"restored={string.Join(',', AmsMcsResults.RestoredIds(restored.Ams))} listed={AmsMcsResults.Describe(listed)}");
        mismatched = AmsMcsFiles.Mismatched(AmsMcsFiles.ReadAgentConfig(mate), listed.Agents, home.RepositoryId);
        context.Assert.True(mismatched.Count == 0, "the teammate's .config holds each of them with its type, R, main and its folder", string.Join("; ", mismatched));
    });

    private static Scenario CacheInHistory() => Builder.Make("AM-29", "agent init refuses a working tree whose commits hold a .mcs folder, before any question; once the history is squashed it succeeds, and GRS holds no .mcs in its tree or its history", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var project = AmsMcsSteps.Folder(context, "history");
        var name = AmsMcsSteps.Name(context, "Cached");
        const string folder = "wf";
        FixtureCopies.CopyWorkflow(Path.Combine(HarnessEnvironment.ProjectDirectory, "fixtures", Capabilities.StandaloneFixturesFolder, SwFixtures.FolderOf(SwShape.ButtonNoop)), Path.Combine(project, folder), name, Guid.NewGuid(), keepLocalState: false);
        await ams.Git.InitAsync(project, context.CancellationToken);
        await ams.Git.CommitAsync(project, "ahe2e: a standalone workflow", [folder], context.CancellationToken);
        var cache = AmsMcsFiles.WriteWorkflowState(project, folder);
        await ams.Git.AddAsync(project, [cache], force: true, context.CancellationToken);
        var head = await ams.Git.CommitStagedAsync(project, $"ahe2e: track {cache}", context.CancellationToken);
        string[] arguments = ["agent", "init", "--agent-type", AgentTypes.McsWorkflow, "--agent-name", name, "--mcs-workflow-source", project];
        var files = AmsMcsFiles.Fingerprint(Path.Combine(project, folder));
        var entries = AmsMcsFiles.TopEntries(project);

        var refused = await ams.On.RunAsync([.. arguments, "--json"], ams.WorkspaceRoot, stdin: "n", timeout: AmsMcsServices.CallTimeout, cancellationToken: context.CancellationToken);
        ams.Record(context.Ledger, refused, project, amsOff: false, context.ScenarioId);
        context.Assert.Refused(refused, 19059, "agent init, without --non-interactive, of a working tree whose second commit adds wf/.mcs/workflow-state.json fails with McsFolderInRepository's history wording (19059, exit 2)", 2, $"Commits msagent would push hold a .mcs folder inside {folder}");
        var named = AmsMcsResults.Named(await AmsMcsSteps.AmsAgentsAsync(context, null), name);
        var headAfter = await ams.Git.RevParseAsync(project, "HEAD", context.CancellationToken);
        context.Assert.True(!AmsMcsText.HasQuestion(refused.StdErr) && named.Count == 0 && GitCommits.Match(headAfter, head) && AmsMcsFiles.Fingerprint(Path.Combine(project, folder)) == files && AmsMcsFiles.TopEntries(project) == entries, "the refusal came before any question: nothing was asked, AMS lists no such agent, and HEAD and the folder are as they were", $"rows={named.Count} head={GitCommits.Short(head)}->{GitCommits.Short(headAfter)} entries={AmsMcsFiles.TopEntries(project)} stderr={refused.StdErr.Trim()}");

        await ams.Git.RemoveAsync(project, [$"{folder}/{WorkflowStateCache.FolderName}"], cached: true, context.CancellationToken);
        await ams.Git.SquashAsync(project, "ahe2e: a standalone workflow, squashed", context.CancellationToken);
        var commits = await ams.Git.RevListAsync(project, "HEAD", context.CancellationToken);
        var history = await ams.Git.CacheHistoryAsync(project, context.CancellationToken);
        context.Assert.True(commits.Count == 1 && history.Count == 0 && File.Exists(Path.Combine(project, cache)), "(setup) the history is squashed into one commit without .mcs, and the file stays on disk, untracked", $"commits={commits.Count} history={string.Join(',', history)}");

        var initialized = await AmsMcsSteps.RecordedAsync<ManagedAgentInitResult>(context, ams.On, "agent init of the squashed working tree", project, arguments, AmsMcsSteps.LongCall);
        var registration = AmsMcsSteps.Registered(context, initialized);
        context.Assert.True(initialized.Status == "agent-initialized" && registration is { Registered: true, RepositoryCreated: true, Pushed: true } && AmsIds.Same(registration.Folder, folder), "init registered the workflow in a new repository and pushed it", $"repository={registration.RepositoryId} folder={registration.Folder} pushed={registration.Pushed}");
        var fresh = AmsMcsSteps.Folder(context, "fresh");
        await ams.Git.CloneWithHelperAsync(registration.GitUrl, fresh, ams.Config.GitHost!, await ams.Git.LastHelperAsync(project, ams.Config.GitHost!, context.CancellationToken), context.CancellationToken);
        var clone = await ams.Git.InspectAsync(fresh, ams.Config.GitHost!, context.CancellationToken);
        history = await ams.Git.CacheHistoryAsync(fresh, context.CancellationToken);
        context.Assert.True(GitCommits.Match(clone.Head?.Sha, registration.CommitSha) && clone.TrackedCache is { Count: 0 } && history.Count == 0, "a fresh clone of the repository holds no .mcs in its tree or its history", $"head={clone.Head?.Sha} commitSha={registration.CommitSha} tracked={string.Join(',', clone.TrackedCache ?? [])} history={string.Join(',', history)}");
    });

    private static Scenario UnknownBot() => Builder.Make("AM-32", "a remote Dev deploy never takes over a bot AMS doesn't know: after an AMS-off deploy, a Dev edit and agent init it fails with CaptureBaseUnknown (19064) and the edit stays; after a local deploy the next one captures first; --overwrite takes the bot over", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var dev = ams.Config.DevEnvironmentId!;
        var known = await AmsMcsSteps.AmsOffSourceAsync(context, "Known");
        var edit = AmsMcsSteps.Name(context, "Edit32");
        await AmsMcsSteps.RenameBotAsync(context, known.BotId!, edit);
        var initialized = await AmsMcsSteps.InitAsync(context, "agent init registers the AMS-off project (register-only)", known.Project);
        var registration = AmsMcsSteps.Registered(context, initialized);
        context.Assert.True(initialized.Status == "agent-initialized" && AmsIds.Same(initialized.AgentId, known.AgentId) && registration is { Registered: true, RepositoryCreated: true, Pushed: true }, "init registered the same agent in a new repository and pushed it", $"agentId={initialized.AgentId} repository={registration.RepositoryId} pushed={registration.Pushed}");
        var timeBefore = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(known.Project), known.Dev.DeploymentId)?.LastDeployedTime;
        await AmsMcsSteps.RefusedAsync(context, known.Project, AmsMcsSteps.RemoteArguments(known.Project, known.AgentId, known.Dev.DeploymentId), 19064, "a remote Dev deploy without --overwrite, to the bot the AMS-off deploy made, fails with CaptureBaseUnknown (19064, exit 1)", 1);
        var bots = await ams.Dataverse.BotsNamedAsync(dev, known.SchemaName!, context.CancellationToken);
        var slot = await AmsMcsSteps.SlotAsync(context, known.Project, known.AgentId, known.Dev.DeploymentId);
        var timeAfter = AmsMcsFiles.Deployment(AmsMcsFiles.ReadAgentConfig(known.Project), known.Dev.DeploymentId)?.LastDeployedTime;
        context.Assert.True(bots is [var bot] && AmsIds.Same(bot.BotId, known.BotId) && bot.Name == edit && string.IsNullOrEmpty(slot?.LastDeployedCommitSha) && timeAfter == timeBefore, "nothing was deployed: the bot keeps the Dev edit and no other bot has its schema name, AMS records no commit, and .config's time is unchanged", $"bots={AmsMcsResults.Describe(bots)} lastDeployedCommitSha={slot?.LastDeployedCommitSha} lastDeployedTime={timeBefore}->{timeAfter}");

        await AmsMcsSteps.SyncAsync(context, known.Project);
        var head = await ams.Git.RevParseAsync(known.Project, "HEAD", context.CancellationToken);
        var local = await AmsMcsSteps.DeployAsync(context, ams.On, known.Project, known.AgentId, known.Dev.DeploymentId, "--overwrite");
        context.Assert.True(AmsIds.Same(local.TargetMcsAgentId, known.BotId) && !local.TargetAgentCreated && AmsMcsText.AmsWarnings(local.Warnings).Count == 0, "a local Dev deploy with AMS on, --overwrite over the Dev edit, updated the same bot, created none, and AMS took the record", $"bot={local.TargetMcsAgentId} created={local.TargetAgentCreated} warnings={string.Join(" | ", local.Warnings)}");
        await AmsMcsSteps.AssertCleanAfterDeployAsync(context, known.Project, known.Folder);
        slot = await AmsMcsSteps.SlotAsync(context, known.Project, known.AgentId, known.Dev.DeploymentId);
        context.Assert.True(GitCommits.Match(slot?.LastDeployedCommitSha, head), "AMS records HEAD as Dev's commit, so it knows the cloud base", $"lastDeployedCommitSha={slot?.LastDeployedCommitSha} head={head}");

        var second = AmsMcsSteps.Name(context, "Edit32b");
        await AmsMcsSteps.RenameBotAsync(context, known.BotId!, second);
        var captured = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, known.Project, known.AgentId, known.Dev.DeploymentId);
        await ams.Git.FetchAsync(known.Project, context.CancellationToken);
        var main = await AmsMcsSteps.RemoteMainAsync(context, known.Project);
        var mainName = AmsMcsText.DisplayName(await ams.Git.ShowFileAsync(known.Project, "origin/main", AmsMcsFiles.SettingsEntry(known.Folder), context.CancellationToken));
        var botName = await AmsMcsSteps.BotNameAsync(context, known.BotId);
        context.Assert.True(captured.Status == "deployed" && GitCommits.IsSha(captured.CapturedCommitSha) && GitCommits.Match(captured.CommitSha, captured.CapturedCommitSha) && GitCommits.Match(captured.CapturedCommitSha, main) && mainName == second && botName == second, "the next remote Dev deploy captured the new Dev edit first and deployed it: capturedCommitSha is GRS main, which holds the edit, and the bot keeps it", $"commitSha={captured.CommitSha} capturedCommitSha={captured.CapturedCommitSha} main={main} mainName='{mainName}' bot='{botName}'");

        var taken = await AmsMcsSteps.AmsOffSourceAsync(context, "Taken");
        await AmsMcsSteps.RenameBotAsync(context, taken.BotId!, AmsMcsSteps.Name(context, "Edit32c"));
        AmsMcsSteps.Registered(context, await AmsMcsSteps.InitAsync(context, "agent init registers the second AMS-off project (register-only)", taken.Project));
        var overwritten = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, taken.Project, taken.AgentId, taken.Dev.DeploymentId, "--overwrite");
        var takenMain = await AmsMcsSteps.RemoteMainAsync(context, taken.Project);
        var takenBots = await ams.Dataverse.BotsNamedAsync(dev, taken.SchemaName!, context.CancellationToken);
        var takenSlot = await AmsMcsSteps.SlotAsync(context, taken.Project, taken.AgentId, taken.Dev.DeploymentId);
        context.Assert.True(overwritten.Status == "deployed" && GitCommits.Match(overwritten.CommitSha, takenMain) && string.IsNullOrEmpty(overwritten.CapturedCommitSha) && takenBots is [var takenBot] && AmsIds.Same(takenBot.BotId, taken.BotId) && takenBot.Name == taken.Name && GitCommits.Match(takenSlot?.LastDeployedCommitSha, takenMain), "--overwrite took the bot over: it deployed GRS main with no capture over the Dev edit, into the same bot and no other, and AMS records main", $"commitSha={overwritten.CommitSha} capturedCommitSha={overwritten.CapturedCommitSha} main={takenMain} bots={AmsMcsResults.Describe(takenBots)} lastDeployedCommitSha={takenSlot?.LastDeployedCommitSha}");
        var capture = await AmsMcsSteps.CaptureAsync(context, taken.Project, taken.AgentId);
        context.Assert.True(capture.Status is "captured" or "unchanged", "agent capture now knows the base: captured or unchanged", $"status={capture.Status}");
    });

    private static Scenario ConnectorEdit() => Builder.Make("AM-33", "a Dev edit to a classic agent's custom connector is captured by the next remote Dev deploy: the operation names a captured commit that holds the edit, and Dev keeps it", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var dev = ams.Config.DevEnvironmentId!;
        var project = AmsMcsSteps.Folder(context, "connector");
        var created = await AmsMcsSteps.CreateAsync(context, ams.On, project, AmsMcsSteps.Name(context, "Connector"), "--mcs-agent-type", "Classic");
        var registration = AmsMcsSteps.Registered(context, created);
        context.Assert.True(created.SchemaName is { Length: > 0 }, "(setup) the classic agent has a schema name", $"schemaName={created.SchemaName}");
        var development = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, project, created.AgentId, "development", "dev", null);
        var connector = AmsCustomConnector.For(await ams.Dataverse.PublisherPrefixAsync(dev, context.CancellationToken), created.SchemaName!, AmsMcsSteps.Name(context, "connector"));
        AmsMcsSteps.RecordConnector(context, connector);
        connector.AddTo(Path.Combine(HarnessEnvironment.ProjectDirectory, "fixtures", AmsCustomConnector.FixtureFolder), Path.Combine(project, registration.Folder));
        await ams.Git.CommitAsync(project, "ahe2e: a custom connector, its connection reference and a tool", [registration.Folder], context.CancellationToken);
        await ams.Git.PushAsync(project, AmsMcsSteps.Branch, context.CancellationToken);
        var pushed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, project, created.AgentId, development.DeploymentId);
        var found = await ams.Dataverse.ConnectorsNamedAsync(dev, connector.Name, context.CancellationToken);
        context.Assert.True(pushed.Status == "deployed" && found is [{ InternalId.Length: > 0 }], "(setup) the remote Dev deploy pushed the classic agent, which made the custom connector in Dev", $"status={pushed.Status} connectors={found.Count}");
        var row = found[0];
        var reference = await ams.Dataverse.PointConnectionReferenceAsync(dev, connector.ConnectionReference, row.InternalId!, context.CancellationToken);
        context.Assert.True(reference?.ConnectorId == DataverseConnector.Api(row.InternalId!), "(setup) the connection reference names the connector's generated internal id, as the portal's would", $"connectorid={reference?.ConnectorId} internalId={row.InternalId}");
        var projection = await AmsMcsSteps.CaptureAsync(context, project, created.AgentId);
        await AmsMcsSteps.SyncAsync(context, project);
        context.Assert.True(projection.Status == "captured" && AmsMcsFiles.ComponentMetadataPath(project, registration.Folder, CustomConnectorLayout.FolderName, row.ConnectorId) is not null, "(setup) agent capture committed Dev's projection, with the connector's folder, and git pull brought it", $"status={projection.Status} commit={projection.CommitSha}");
        var stored = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, project, created.AgentId, development.DeploymentId);
        var main = await AmsMcsSteps.RemoteMainAsync(context, project);
        context.Assert.True(stored.Status == "deployed" && GitCommits.Match(stored.CommitSha, main), "(setup) a remote Dev deploy of GRS main, after which AMS holds the change probe", $"commitSha={stored.CommitSha} capturedCommitSha={stored.CapturedCommitSha} main={main}");

        var description = $"ahe2e Dev edit {context.RunSuffix}";
        var edited = await ams.Dataverse.EditConnectorDescriptionAsync(dev, row.ConnectorId, description, context.CancellationToken);
        context.Assert.True(edited?.Description == description, "the connector's description is edited in Dev (a portal edit)", $"connector={row.ConnectorId} description='{edited?.Description}'");
        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, project, created.AgentId, development.DeploymentId);
        await ams.Git.FetchAsync(project, context.CancellationToken);
        main = await AmsMcsSteps.RemoteMainAsync(context, project);
        var metadata = AmsMcsFiles.ComponentMetadata(await ams.Git.FilesAsync(project, "origin/main", context.CancellationToken) ?? [], registration.Folder, CustomConnectorLayout.FolderName, row.ConnectorId);
        var saved = metadata is null ? null : AmsCustomConnector.DescriptionOf(await ams.Git.ShowFileAsync(project, "origin/main", metadata, context.CancellationToken));
        var kept = (await ams.Dataverse.ConnectorAsync(dev, row.ConnectorId, context.CancellationToken))?.Description;
        context.Assert.True(deployed.Status == "deployed" && GitCommits.IsSha(deployed.CapturedCommitSha) && GitCommits.Match(deployed.CommitSha, deployed.CapturedCommitSha) && GitCommits.Match(deployed.CapturedCommitSha, main) && saved == description && kept == description, "the remote Dev deploy of the same commit captured the connector edit first: capturedCommitSha is GRS main, whose metadata.yml holds the edited description, and Dev keeps it", $"commitSha={deployed.CommitSha} capturedCommitSha={deployed.CapturedCommitSha} main={main} metadata={metadata} saved='{saved}' dev='{kept}'");
    });

    private static Scenario WorkflowEdit() => Builder.Make("AM-35", "a Dev rename of W's flow is captured by the next remote Dev deploy and deployed; once main changes the workflow, a remote Dev deploy is refused without --overwrite, and with it overwrites the edit", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var dev = ams.Config.DevEnvironmentId!;
        var workflows = await AmsMcsSteps.WorkflowsAsync(context);
        await AmsMcsSteps.SyncAsync(context, workflows.Project);
        var development = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, workflows.Project, workflows.AgentId, "development", "dev", null);
        var first = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, workflows.Project, workflows.AgentId, development.DeploymentId);
        var flows = await ams.Dataverse.FlowsNamedExactlyAsync(dev, workflows.Created.WorkflowName ?? workflows.Name, context.CancellationToken);
        context.Assert.True(first.Status == "deployed" && flows.Count == 1, "(setup) the first remote Dev deploy of W made its flow in Dev", $"status={first.Status} flows={flows.Count}");
        var flowId = flows[0].WorkflowId;
        var renamed = AmsMcsSteps.Name(context, "Renamed35");
        await AmsMcsSteps.RenameFlowAsync(context, flowId, renamed);
        var captured = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, workflows.Project, workflows.AgentId, development.DeploymentId);
        await ams.Git.FetchAsync(workflows.Project, context.CancellationToken);
        var main = await AmsMcsSteps.RemoteMainAsync(context, workflows.Project);
        var metadata = AmsMcsFiles.ComponentMetadata(await ams.Git.FilesAsync(workflows.Project, "origin/main", context.CancellationToken) ?? [], workflows.Folder, WorkflowWorkspace.WorkflowsFolderName, flowId);
        var savedName = metadata is null ? null : AmsMcsText.WorkflowName(await ams.Git.ShowFileAsync(workflows.Project, "origin/main", metadata, context.CancellationToken));
        var flow = await ams.Dataverse.WorkflowAsync(dev, flowId, includeDefinition: false, context.CancellationToken);
        context.Assert.True(captured.Status == "deployed" && GitCommits.IsSha(captured.CapturedCommitSha) && GitCommits.Match(captured.CommitSha, captured.CapturedCommitSha) && GitCommits.Match(captured.CapturedCommitSha, main) && savedName == renamed && flow?.Name == renamed, "the remote Dev deploy captured the rename first and deployed it: capturedCommitSha is GRS main, whose workflow metadata holds the rename, and the flow keeps it", $"commitSha={captured.CommitSha} capturedCommitSha={captured.CapturedCommitSha} main={main} metadata={metadata} savedName='{savedName}' flow='{flow?.Name}'");

        await AmsMcsSteps.SyncAsync(context, workflows.Project);
        var local = AmsMcsFiles.ComponentMetadataPath(workflows.Project, workflows.Folder, WorkflowWorkspace.WorkflowsFolderName, flowId);
        context.Assert.True(local is not null, "(setup) git pull brought the captured workflow into W's project", $"folder={workflows.Folder} flow={flowId}");
        AmsMcsFiles.AddYamlComment(local!, $"ahe2e main changes the workflow {context.RunSuffix}");
        var changed = await ams.Git.CommitAsync(workflows.Project, "ahe2e: main changes the workflow", [Path.GetRelativePath(workflows.Project, local!).Replace(Path.DirectorySeparatorChar, '/')], context.CancellationToken);
        await ams.Git.PushAsync(workflows.Project, AmsMcsSteps.Branch, context.CancellationToken);
        var edit = AmsMcsSteps.Name(context, "Edited35");
        await AmsMcsSteps.RenameFlowAsync(context, flowId, edit);
        var refusal = await AmsMcsSteps.RefusedAsync(context, workflows.Project, AmsMcsSteps.RemoteArguments(workflows.Project, workflows.AgentId, development.DeploymentId), 19008, "with main changed since the cloud base, a remote Dev deploy without --overwrite is refused (SourceConflict, 19008, exit 2): the capture can't commit onto main", 2);
        context.Assert.True(AmsMcsText.Says(refusal.Remediation, "--overwrite"), "the refusal's remediation names --overwrite, because a workflow capture doesn't merge", $"remediation={refusal.Remediation}");
        flow = await ams.Dataverse.WorkflowAsync(dev, flowId, includeDefinition: false, context.CancellationToken);
        main = await AmsMcsSteps.RemoteMainAsync(context, workflows.Project);
        var slot = await AmsMcsSteps.SlotAsync(context, workflows.Project, workflows.AgentId, development.DeploymentId);
        context.Assert.True(flow?.Name == edit && GitCommits.Match(main, changed) && GitCommits.Match(slot?.LastDeployedCommitSha, captured.CapturedCommitSha), "nothing was deployed or captured: the flow keeps the Dev edit, GRS main is main's change, and AMS's Dev still holds the capture", $"flow='{flow?.Name}' main={main} changed={changed} lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
        var overwritten = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, workflows.Project, workflows.AgentId, development.DeploymentId, "--overwrite");
        flow = await ams.Dataverse.WorkflowAsync(dev, flowId, includeDefinition: false, context.CancellationToken);
        slot = await AmsMcsSteps.SlotAsync(context, workflows.Project, workflows.AgentId, development.DeploymentId);
        context.Assert.True(overwritten.Status == "deployed" && GitCommits.Match(overwritten.CommitSha, changed) && string.IsNullOrEmpty(overwritten.CapturedCommitSha) && flow?.Name == renamed && GitCommits.Match(slot?.LastDeployedCommitSha, changed), "--overwrite deployed main over the Dev edit, as today: commitSha is main's change with no capture, the flow has main's name again, and AMS records main", $"commitSha={overwritten.CommitSha} capturedCommitSha={overwritten.CapturedCommitSha} flow='{flow?.Name}' lastDeployedCommitSha={slot?.LastDeployedCommitSha}");
    });

    private static Scenario CloneOfAHeldBot() => Builder.Make("AM-36", "a clone of a held Dev bot into an empty folder joins the agent that holds it: Dev's edits merge into main's as uncommitted changes, with conflict markers where they collide, and the folder is connected; the same command again fails with AlreadyRegistered, and in a plain git clone it restores the holder without cloning", HomeOracle, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var dev = ams.Config.DevEnvironmentId!;
        var holder = AmsMcsSteps.Folder(context, "holder");
        var created = await AmsMcsSteps.CreateAsync(context, ams.On, holder, AmsMcsSteps.Name(context, "Merge"), "--mcs-agent-type", "Classic");
        var registration = AmsMcsSteps.Registered(context, created);
        var folder = registration.Folder;
        var development = await AmsMcsSteps.DeploymentCreateAsync(context, ams.On, holder, created.AgentId, "development", "dev", null);
        var deployed = await AmsMcsSteps.RemoteDeployAsync(context, ams.On, holder, created.AgentId, development.DeploymentId);
        var bots = await ams.Dataverse.BotsNamedAsync(dev, created.SchemaName!, context.CancellationToken);
        context.Assert.True(deployed.Status == "deployed" && bots.Count == 1, "(setup) the remote Dev deploy made the classic agent's bot in Dev, which its Dev deployment holds", $"status={deployed.Status} bots={AmsMcsResults.Describe(bots)}");
        var botId = bots[0].BotId;
        var projection = await AmsMcsSteps.CaptureAsync(context, holder, created.AgentId);
        await AmsMcsSteps.SyncAsync(context, holder);
        context.Assert.True(projection.Status is "captured" or "unchanged", "(setup) agent capture right after the deploy is unchanged, or saved Dev's projection on main, which git pull brought", $"status={projection.Status} commit={projection.CommitSha}");

        (string File, string Name)[] topics = [("Greeting", "Greeting"), ("ThankYou", "Thank you"), ("Escalate", "Escalate")];
        var devEdit = AmsMcsSteps.Name(context, "Dev edit");
        var mainEdit = AmsMcsSteps.Name(context, "main edit");

        foreach (var (_, name) in new[] { topics[0], topics[2] })
        {
            var edited = await ams.Dataverse.EditComponentDescriptionAsync(dev, botId, name, devEdit, context.CancellationToken);
            context.Assert.True(edited?.Description == devEdit, $"(setup) topic {name}'s description is edited in Dev (a portal edit)", $"description='{edited?.Description}'");
        }

        foreach (var (file, _) in new[] { topics[1], topics[2] })
        {
            AmsMcsFiles.SetDescription(AmsMcsFiles.TopicPath(holder, folder, file), mainEdit);
        }

        await ams.Git.CommitAsync(holder, "ahe2e: main edits topics B and C", [AmsMcsFiles.TopicEntry(folder, topics[1].File), AmsMcsFiles.TopicEntry(folder, topics[2].File)], context.CancellationToken);
        await ams.Git.PushAsync(holder, AmsMcsSteps.Branch, context.CancellationToken);
        var main = await AmsMcsSteps.RemoteMainAsync(context, holder);
        var agentsBefore = await AmsMcsSteps.AmsAgentIdsAsync(context, registration.RepositoryId);

        var project = AmsMcsSteps.EmptyFolder(context, "join");
        var other = AmsMcsSteps.Name(context, "Other");
        var arguments = AmsMcsSteps.CloneBotArguments(context, botId, project, "--agent-name", other);
        var joined = await AmsMcsSteps.RecordedAsync<ManagedAgentCreateResult>(context, ams.On, "agent create, cloning the held bot into an empty folder", project, arguments, AmsMcsSteps.LongCall);
        string[] warnings = [.. joined.Warnings, .. joined.Ams?.Warnings ?? []];
        var connection = AmsMcsFiles.Connection(project, folder, created.AgentId);
        context.Assert.True(joined.Status == "joined" && AmsIds.Same(joined.AgentId, created.AgentId) && joined.Ams is { Joined: true, Registered: false } && joined.WorkspaceConnected == true && connection.BindsTo(botId, dev, ams.DevOrganization) && AmsMcsText.Mentions(warnings, $"--agent-name '{other}' is ignored"), "the clone joined the agent that holds the bot: status joined, the holder's AMS id, a warning that --agent-name is ignored, and the folder connected to the Dev bot", $"status={joined.Status} agentId={joined.AgentId} holder={created.AgentId} joined={joined.Ams?.Joined} registered={joined.Ams?.Registered} workspaceConnected={joined.WorkspaceConnected} {connection.Describe()} warnings={string.Join(" | ", warnings)}");

        var clone = await ams.Git.InspectAsync(project, ams.Config.GitHost!, context.CancellationToken);
        var config = AmsMcsFiles.ReadAgentConfig(project);
        var deployments = AmsMcsFiles.DeploymentSignature(config, created.AgentId);
        var holderDeployments = AmsMcsFiles.DeploymentSignature(AmsMcsFiles.ReadAgentConfig(holder), created.AgentId);
        context.Assert.True(clone.IsRootOf(project) && clone.OriginIs(registration.GitUrl) && GitCommits.Match(clone.Head?.Sha, main) && AmsMcsFiles.Agent(config, created.AgentId) is not null && deployments.Length > 0 && deployments == holderDeployments, "the folder is a clone of R at GRS main, and .config holds the holder with its AMS id and deployments", $"toplevel={clone.TopLevel} origin={clone.Origin} head={clone.Head?.Sha} main={main} deployments={deployments} holder's={holderDeployments}");

        var texts = topics.Select(topic => File.ReadAllText(AmsMcsFiles.TopicPath(project, folder, topic.File))).ToArray();
        List<bool> modified = [];

        foreach (var (file, _) in topics)
        {
            modified.Add(await ams.Git.IsModifiedAsync(project, AmsMcsFiles.TopicEntry(folder, file), context.CancellationToken));
        }

        var conflicted = AmsMcsFiles.TopicEntry(folder, topics[2].File);
        context.Assert.True(AmsMcsText.Description(texts[0]) == devEdit && AmsMcsText.Description(texts[1]) == mainEdit && AmsMcsText.HasConflictMarkers(texts[2]) && texts[2].Contains(devEdit, StringComparison.Ordinal) && texts[2].Contains(mainEdit, StringComparison.Ordinal) && modified is [true, false, true] && joined.Ams?.ConflictedFiles is [var only] && only == conflicted && AmsMcsText.Mentions(warnings, conflicted), "topic A holds Dev's edit and topic B main's, and topic C, which both edited, holds conflict markers around both edits; A and C are uncommitted changes, and the warning names C", $"A='{AmsMcsText.Description(texts[0])}' B='{AmsMcsText.Description(texts[1])}' C has markers: {AmsMcsText.HasConflictMarkers(texts[2])} modified={string.Join(',', modified)} merged={string.Join(',', joined.Ams?.MergedFiles ?? [])} conflicted={string.Join(',', joined.Ams?.ConflictedFiles ?? [])}");

        context.Assert.Refused(await AmsMcsSteps.RawAsync(context, ams.On, AmsMcsSteps.PullArguments(project, created.AgentId), AmsMcsSteps.LongCall), 10022, "agent pull right after the join refuses topic C until its conflict markers are resolved (WorkspaceFilesUnreadable, 10022, exit 2)", 2, $"topics/{topics[2].File}.mcs.yml");
        await ams.Git.RestoreAsync(project, [conflicted], context.CancellationToken);
        var statusBefore = await ams.Git.StatusAsync(project, [], context.CancellationToken);
        var pulled = await AmsMcsSteps.PullAsync(context, ams.On, project, created.AgentId);
        var statusAfter = await ams.Git.StatusAsync(project, [], context.CancellationToken);
        context.Assert.True(pulled.Status == "up-to-date" && statusAfter.SequenceEqual(statusBefore) && statusAfter.Count == 1, "once topic C takes main's version, agent pull finds nothing to pull and leaves Dev's merged edit of topic A as it is", $"status={pulled.Status} changes={pulled.ChangeCount} git status: {GitTip.Describe(statusBefore)} -> {GitTip.Describe(statusAfter)}");
        var agentsAfter = await AmsMcsSteps.AmsAgentIdsAsync(context, registration.RepositoryId);
        var mainAfter = await AmsMcsSteps.RemoteMainAsync(context, holder);
        context.Assert.True(agentsAfter == agentsBefore && GitCommits.Match(mainAfter, main), "AMS's agents in R and GRS main didn't change", $"agents={agentsBefore} -> {agentsAfter} main={main}->{mainAfter}");

        await AmsMcsSteps.RefusedBeforeQuestionAsync(context, project, arguments, 10009, "the same command again fails with AlreadyRegistered (10009, exit 2) before any question, naming the holder, which the folder's .config holds", $"already registered as {created.DisplayName}");

        var plain = AmsMcsSteps.Folder(context, "plain");
        await ams.Git.CloneWithHelperAsync(registration.GitUrl, plain, ams.Config.GitHost!, await ams.Git.LastHelperAsync(holder, ams.Config.GitHost!, context.CancellationToken), context.CancellationToken);
        var head = await ams.Git.RevParseAsync(plain, "HEAD", context.CancellationToken);
        var restored = await AmsMcsSteps.RecordedAsync<ManagedAgentCreateResult>(context, ams.On, "the same command without --agent-name, in a plain git clone of R", plain, AmsMcsSteps.CloneBotArguments(context, botId, plain), AmsMcsSteps.LongCall);
        var headAfter = await ams.Git.RevParseAsync(plain, "HEAD", context.CancellationToken);
        string[] restoredWarnings = [.. restored.Warnings, .. restored.Ams?.Warnings ?? []];
        context.Assert.True(restored.Status == "joined" && restored.Ams is { Joined: true } && AmsIds.Same(restored.AgentId, created.AgentId) && AmsMcsFiles.Agent(AmsMcsFiles.ReadAgentConfig(plain), created.AgentId) is not null && GitCommits.Match(headAfter, head) && !AmsMcsText.Mentions(restoredWarnings, "--agent-name"), "in a plain git clone of R, the same command without --agent-name joins by restoring the holder into .config, without cloning: HEAD is still the clone's", $"status={restored.Status} agentId={restored.AgentId} head={head}->{headAfter} warnings={string.Join(" | ", restoredWarnings)}");
    });

    private static Scenario CloneIntoAnotherRepository() => Builder.Make("AM-37", "a clone of a held bot into a clone of another repository, or with --repository-id naming another repository, fails with CannotJoinHolder before any question, and changes nothing", Home, SafetyTier.Creating, async context =>
    {
        var ams = await AmsMcsSteps.StartAsync(context);
        var home = await AmsMcsSteps.HomeAsync(context);
        await AmsMcsSteps.DeploymentsAsync(context);
        var dev = await AmsMcsSteps.DevDeployedAsync(context);
        var workflows = await AmsMcsSteps.WorkflowsAsync(context);
        var clone = AmsMcsSteps.Folder(context, "other-clone");
        await ams.Git.CloneWithHelperAsync(workflows.Created.Ams!.GitUrl, clone, ams.Config.GitHost!, await ams.Git.LastHelperAsync(workflows.Project, ams.Config.GitHost!, context.CancellationToken), context.CancellationToken);
        var empty = AmsMcsSteps.EmptyFolder(context, "other-id");
        var entriesBefore = AmsMcsFiles.TopEntries(clone);
        var statusBefore = await ams.Git.StatusAsync(clone, [], context.CancellationToken);
        var agentsBefore = await AmsMcsSteps.AmsAgentIdsAsync(context, home.RepositoryId, workflows.RepositoryId);

        foreach (var (what, project, extra, reason) in new (string What, string Project, string[] Extra, string Reason)[] { ("into a plain git clone of W", clone, [], $"is a clone of repository {workflows.RepositoryId}"), ("into an empty folder, with --repository-id naming W", empty, ["--repository-id", workflows.RepositoryId], $"--repository-id names repository {workflows.RepositoryId}") })
        {
            await AmsMcsSteps.RefusedBeforeQuestionAsync(context, project, AmsMcsSteps.CloneBotArguments(context, dev.BotId, project, extra), 19077, $"a clone of A's Dev bot, which A holds in R, {what} fails with CannotJoinHolder (19077, exit 2) before any question", reason);
        }

        var statusAfter = await ams.Git.StatusAsync(clone, [], context.CancellationToken);
        var agentsAfter = await AmsMcsSteps.AmsAgentIdsAsync(context, home.RepositoryId, workflows.RepositoryId);
        context.Assert.True(AmsMcsFiles.TopEntries(clone) == entriesBefore && !File.Exists(AmsMcsFiles.AgentConfigPath(clone)) && statusAfter.SequenceEqual(statusBefore) && AmsMcsFiles.IsMissingOrEmpty(empty) && agentsAfter == agentsBefore, "nothing changed: the clone of W keeps its entries and git status and has no .config, the empty folder stays empty, and AMS lists the same agents in R and W", $"entries={entriesBefore} -> {AmsMcsFiles.TopEntries(clone)} git status: {GitTip.Describe(statusBefore)} -> {GitTip.Describe(statusAfter)} empty={AmsMcsFiles.TopEntries(empty)} agents={agentsBefore} -> {agentsAfter}");
    });
}
