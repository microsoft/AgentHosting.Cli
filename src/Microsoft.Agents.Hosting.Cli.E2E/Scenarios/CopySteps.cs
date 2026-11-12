using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Copy;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

public abstract record CloudSeed(string Name, string CloudId, string EnvironmentId);

public sealed record CopySeed(string Name, string CloudAgentId, string EnvironmentId, string SchemaName, string DisplayName, string ProjectDirectory, string RecordName) : CloudSeed(Name, CloudAgentId, EnvironmentId);

public sealed record WorkflowSeed(string Name, string WorkflowId, string EnvironmentId) : CloudSeed(Name, WorkflowId, EnvironmentId);

public sealed record CorpusSeed(CopySeed Seed, ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed, string CloneDirectory, double Seconds);

public static class CopySteps
{
    public const string DeploymentName = "copy-dev";
    public const int Usage = 2;
    public const int Failure = 1;

    public static readonly string[] CloneState = [".mcs", ".config"];

    private const string BaselinePool = "copy-baseline";
    private const string CorpusSeedPool = "copy-corpus-seed";
    private const string DeployedPool = "copy-deployed";
    private const string DeployedKey = "cli-copy";
    private const string DeployedWorkflowPool = "copy-deployed-workflow";
    private const string WorkflowSeedPool = "copy-workflow-seed";
    private const string WorkflowSeedName = "FixtureWorkflow";
    private const string SettingsFileName = "settings.mcs.yml";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(20);
    private static readonly string[] NonInteractive = ["--non-interactive"];
    private static readonly string[] Json = ["--json"];
    private static readonly string[] AllResources = ["--all-resources"];
    private static readonly string[] ProjectCopyLock = [AgentConfigLocator.ConfigDirectoryName, Path.Combine(AgentConfigLocator.ConfigDirectoryName, CopyProjectState.FolderName), Path.Combine(AgentConfigLocator.ConfigDirectoryName, CopyProjectState.FolderName, CopyProjectState.IgnoreFileName), Path.Combine(AgentConfigLocator.ConfigDirectoryName, CopyProjectState.FolderName, CopyProjectState.LockFileName)];

    public static string NewName(ScenarioContext context, string label = "copy") => $"ahe2e {context.ScenarioId} {label} {context.RunSuffix}";

    public static IReadOnlyList<string> LocalSource(string agentDirectory) => ["--project", agentDirectory];

    public static IReadOnlyList<string> LocalSource(Workspace workspace) => LocalSource(workspace.ProjectDirectory);

    public static IReadOnlyList<string> LocalSource(ManagedAgentCopyResult copy) => LocalSource(copy.WorkspaceDirectory);

    public static IReadOnlyList<string> CopyArguments(ScenarioContext context, IReadOnlyList<string> source, string name, string label) => [.. source, "--agent-name", name, .. source.Contains("--mcs-clone-agent-id") || source.Contains("--mcs-clone-workflow-id") ? new[] { "--project", context.Workspaces.EmptyDirectory(context.ScenarioId, label) } : []];

    public static string BesideSource(Workspace source, string name) => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source.ProjectDirectory))!, WorkspaceFolderNames.ForProject(name.Trim()));

    public static IReadOnlyList<string> CloudSource(CopySeed seed) => CloudSource(seed.CloudAgentId, seed.EnvironmentId);

    public static IReadOnlyList<string> CloudSource(string cloudAgentId, string environmentId) => ["--environment-id", environmentId, "--mcs-clone-agent-id", cloudAgentId];

    public static IReadOnlyList<string> CloudSource(WorkflowSeed seed) => CloudWorkflowSource(seed.WorkflowId, seed.EnvironmentId);

    public static IReadOnlyList<string> CloudSource(ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed) => IsWorkflow(copy) ? CloudWorkflowSource(CloudWorkflowId(deployed), copy.EnvironmentId) : CloudSource(CloudAgentId(deployed), copy.EnvironmentId);

    public static IReadOnlyList<string> CloudWorkflowSource(string workflowId, string environmentId) => ["--environment-id", environmentId, "--mcs-clone-workflow-id", workflowId];

    public static Task<MsagentResult> RawCopyAsync(ScenarioContext context, IReadOnlyList<string> arguments, string? workingDirectory = null, string? stdin = null, bool interactive = false, bool json = true, ProcessInterruption? interruption = null) =>
        context.Msagent.RunAsync(["agent", "copy", .. arguments, .. (json ? Json : []), .. (interactive ? [] : NonInteractive)], workingDirectory, stdin, CommandTimeout, interruption, context.CancellationToken);

    public static async Task<ManagedAgentCopyResult> CopyAsync(ScenarioContext context, IReadOnlyList<string> source, string label = "copy", string? newName = null)
    {
        await McsSteps.PinEnvironmentAsync(context).ConfigureAwait(false);

        var result = await RawCopyAsync(context, CopyArguments(context, source, newName ?? NewName(context, label), label)).ConfigureAwait(false);
        context.Assert.ExitZero(result, $"agent copy ({label})");

        return RequireHome(context, result.Json<ManagedAgentCopyResult>());
    }

    public static ManagedAgentCopyResult RequireHome(ScenarioContext context, ManagedAgentCopyResult copy)
    {
        RequireDevEnvironment(context, copy.EnvironmentId, copy.DisplayName);

        return copy;
    }

    public static bool IsWorkflow(ManagedAgentCopyResult copy) => copy.AgentType == AgentTypes.McsWorkflow;

    public static string Labelled(ManagedAgentCopyResult copy, string check) => IsWorkflow(copy) ? $"{check} (workflow copy {copy.DisplayName})" : check;

    public static Workspace WorkspaceOf(ScenarioContext context, ManagedAgentCopyResult copy) => new()
    {
        ScenarioId = context.ScenarioId,
        Root = copy.ProjectDirectory,
        ProjectDirectory = copy.ProjectDirectory,
        AgentName = copy.DisplayName,
        SchemaName = copy.SchemaName ?? string.Empty,
        WorkflowId = copy.McsWorkflowId,
        RecordId = copy.AgentId,
    };

    public static Task<MsagentResult> CreateDeploymentAsync(ScenarioContext context, ManagedAgentCopyResult copy) =>
        McsSteps.CreateSlotAsync(context, WorkspaceOf(context, copy), DeploymentName, context.DevEnvironmentId, "dev", "--agent-name", copy.DisplayName);

    public static Task<MsagentResult> RawDeployAsync(ScenarioContext context, ManagedAgentCopyResult copy, params string[] extraArguments) => DeployCommandAsync(context, copy, DeploymentName, extraArguments);

    public static async Task<MsagentResult> DeployCommandAsync(ScenarioContext context, ManagedAgentCopyResult copy, string deploymentName, IReadOnlyList<string> extraArguments)
    {
        if (IsWorkflow(copy))
        {
            WorkflowSteps.Record(context, copy.McsWorkflowId, copy.AgentId, copy.DisplayName, copy.ProjectDirectory);
        }
        else
        {
            context.Ledger.RecordCopy(SchemaNameOf(copy), copy.DisplayName, copy.EnvironmentId, copy.ProjectDirectory, copy.WorkspaceDirectory, copy.AgentId, deploymentName, context.ScenarioId);
        }

        var result = await McsSteps.DeployCommandAsync(context, WorkspaceOf(context, copy), deploymentName, null, ["--agent-name", copy.DisplayName, .. extraArguments], CommandTimeout).ConfigureAwait(false);

        if (result.ExitCode == 0 && result.Json<ManagedAgentDeployResult>().TargetMcsAgentId is { Length: > 0 } agentId)
        {
            context.Ledger.ResolveMcsAgent(SchemaNameOf(copy), agentId);
        }

        return result;
    }

    public static async Task<ManagedAgentDeployResult> DeployAsync(ScenarioContext context, ManagedAgentCopyResult copy, params string[] extraArguments)
    {
        var result = await RawDeployAsync(context, copy, extraArguments).ConfigureAwait(false);
        context.Assert.ExitZero(result, $"deploy {copy.DisplayName}");

        return result.Json<ManagedAgentDeployResult>();
    }

    public static async Task<ManagedAgentCopyResult> RegisteredAsync(ScenarioContext context, IReadOnlyList<string> source, string label = "copy", string? newName = null)
    {
        var copy = await CopyAsync(context, source, label, newName).ConfigureAwait(false);
        context.Assert.ExitZero(await CreateDeploymentAsync(context, copy).ConfigureAwait(false), $"deployment create ({label})");

        return copy;
    }

    public static async Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed)> DeployedAsync(ScenarioContext context, IReadOnlyList<string> source, string label = "copy", string? newName = null)
    {
        var copy = await RegisteredAsync(context, source, label, newName).ConfigureAwait(false);
        var deployed = await DeployAsync(context, copy).ConfigureAwait(false);
        await AssertFirstDeployAsync(context, copy, deployed).ConfigureAwait(false);

        return (copy, deployed);
    }

    public static async Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed, string Clone)> VerifiedFirstDeployAsync(ScenarioContext context, IReadOnlyList<string> source, string label = "copy", string? newName = null)
    {
        var (copy, deployed) = await DeployedAsync(context, source, label, newName).ConfigureAwait(false);

        context.Assert.False(deployed.Published, Labelled(copy, "the first deploy leaves the copy unpublished"));

        return (copy, deployed, await AssertIsolatedAsync(context, copy, deployed).ConfigureAwait(false));
    }

    public static async Task AssertFirstDeployAsync(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed)
    {
        if (IsWorkflow(copy))
        {
            context.Assert.True(deployed.TargetAgentCreated, $"the first deploy creates the flow of the workflow copy {copy.DisplayName}");
            context.Assert.Equal(copy.McsWorkflowId, deployed.McsWorkflowId, $"the first deploy of the workflow copy {copy.DisplayName} reports the copy's flow id");
            context.Assert.False(string.Equals(deployed.McsWorkflowId, copy.SourceMcsWorkflowId, StringComparison.OrdinalIgnoreCase), $"the flow of the workflow copy {copy.DisplayName} is a new one, not its source's");
            context.Assert.Equal(deployed.Published ? WorkflowStateProjection.Published : WorkflowStateProjection.Draft, deployed.WorkflowState, $"the first deploy leaves the workflow copy {copy.DisplayName} a draft unless it publishes it");
            AssertRegistered(context, copy, deployed);
            await AssertOneCloudWorkflowAsync(context, copy, deployed, $"exactly one flow is named for the workflow copy {copy.DisplayName}, and it carries the copy's id").ConfigureAwait(false);
            return;
        }

        context.Assert.True(deployed.TargetAgentCreated, "the first deploy creates the copy's own agent");
        context.Assert.False(string.Equals(CloudAgentId(deployed), copy.SourceMcsAgentId, StringComparison.OrdinalIgnoreCase), "the copy's agent is a new one, not its source's");
        AssertRegistered(context, copy, deployed);
        await AssertOneCloudAgentAsync(context, copy, deployed, "exactly one agent carries the copy's new id and its schema name").ConfigureAwait(false);
        await AssertNoStrayWorkflowsAsync(context, copy).ConfigureAwait(false);
    }

    public static async Task AssertRedeployedAsync(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult first, ManagedAgentDeployResult again)
    {
        if (IsWorkflow(copy))
        {
            context.Assert.Equal(CloudWorkflowId(first), again.McsWorkflowId, $"the redeploy targets the flow of the workflow copy {copy.DisplayName}");
            context.Assert.False(again.TargetAgentCreated, $"the redeploy of the workflow copy {copy.DisplayName} creates no flow");
            AssertRegistered(context, copy, again);
            await AssertOneCloudWorkflowAsync(context, copy, again, $"no duplicate flow is named for the workflow copy {copy.DisplayName}").ConfigureAwait(false);
            return;
        }

        context.Assert.Equal(CloudAgentId(first), again.TargetMcsAgentId, "the redeploy targets the copy's own agent");
        context.Assert.False(again.TargetAgentCreated, "the redeploy creates no agent");
        AssertRegistered(context, copy, again);
        await AssertOneCloudAgentAsync(context, copy, again, "no duplicate agent").ConfigureAwait(false);
    }

    public static async Task AssertNoStrayWorkflowsAsync(ScenarioContext context, ManagedAgentCopyResult copy)
    {
        var owned = OwnedIdentities(copy, CopyIdentityKinds.Workflow);
        var named = (await context.Cloud.WorkflowsAsync(copy.EnvironmentId, context.CancellationToken).ConfigureAwait(false)).Where(workflow => workflow.DisplayName.StartsWith($"{copy.DisplayName} - ", StringComparison.Ordinal));

        context.Assert.NoDifferences([.. named.Where(workflow => !owned.Contains(workflow.WorkflowId, StringComparer.OrdinalIgnoreCase)).Select(workflow => $"{workflow.WorkflowId} ({workflow.DisplayName}) is no workflow of the copy")], "the environment holds no workflow named for the copy beyond its own");
    }

    public static CopySeed SeedOf(string name, ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed) => new(name, CloudAgentId(deployed), deployed.TargetEnvironmentId, SchemaNameOf(copy), copy.DisplayName, copy.ProjectDirectory, copy.DisplayName);

    public static string CloudAgentId(ManagedAgentDeployResult deployed) => deployed.TargetMcsAgentId ?? throw new E2EException($"{deployed.DisplayName} was deployed without a cloud agent id.");

    public static string CloudWorkflowId(ManagedAgentDeployResult deployed) => deployed.McsWorkflowId ?? throw new E2EException($"{deployed.DisplayName} was deployed without a cloud workflow id.");

    public static async Task<ManagedAgentCopyResult> NeverDeployedCopyAsync(ScenarioContext context, string label = "copy") =>
        await RegisteredAsync(context, LocalSource(await FixtureSourceAsync(context, label: $"{label}-source").ConfigureAwait(false)), label).ConfigureAwait(false);

    public static Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed)> DeployedCopyAsync(ScenarioContext context) =>
        context.Session.PooledAsync(DeployedPool, DeployedKey, async () => await DeployedAsync(context, CloudSource(await FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false)), "pooled").ConfigureAwait(false));

    public static Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed)> DeployedWorkflowCopyAsync(ScenarioContext context) =>
        context.Session.PooledAsync(DeployedWorkflowPool, WorkflowSeedName, async () => await DeployedAsync(context, CloudSource(await WorkflowSeedAsync(context).ConfigureAwait(false)), "pooled-workflow").ConfigureAwait(false));

    public static void ConsumeDeployedCopy(ScenarioContext context) => context.Session.ConsumePooled(DeployedPool, DeployedKey);

    public static Task<MsagentResult> ShowAgentAsync(ScenarioContext context, ManagedAgentCopyResult copy, bool json = true) =>
        context.Msagent.RunAsync(["agent", "show", "--agent-name", copy.DisplayName, .. NonInteractive, .. (json ? Json : [])], copy.ProjectDirectory, cancellationToken: context.CancellationToken);

    public static Task<MsagentResult> ShowDeploymentAsync(ScenarioContext context, ManagedAgentCopyResult copy) =>
        context.Msagent.RunAsync(["deployment", "show", "--agent-name", copy.DisplayName, "--deployment-name", DeploymentName, .. NonInteractive, .. Json], copy.ProjectDirectory, cancellationToken: context.CancellationToken);

    public static Task<MsagentResult> PullAsync(ScenarioContext context, ManagedAgentCopyResult copy) =>
        context.Msagent.RunAsync(["agent", "pull", "--agent-name", copy.DisplayName, .. Json, .. NonInteractive], copy.ProjectDirectory, timeout: CommandTimeout, cancellationToken: context.CancellationToken);

    public static ManagedAgentCopyResult Relocated(ManagedAgentCopyResult copy, string projectDirectory)
    {
        string Moved(string path) => Path.Combine(projectDirectory, Path.GetRelativePath(copy.ProjectDirectory, path));

        return copy with { ProjectDirectory = projectDirectory, WorkspaceDirectory = Moved(copy.WorkspaceDirectory), ConfigPath = Moved(copy.ConfigPath) };
    }

    public static Task<MsagentResult> DeleteAsync(ScenarioContext context, ManagedAgentCopyResult copy, bool allResources, string? confirmation = null) =>
        context.Msagent.RunAsync(["agent", "delete", "--agent-name", copy.DisplayName, .. Json, .. (allResources ? AllResources : [])], copy.ProjectDirectory, confirmation ?? copy.DisplayName, CommandTimeout, cancellationToken: context.CancellationToken);

    public static AgentConfigDocument Registry(string configPath) => JsonSerializer.Deserialize(File.ReadAllText(configPath), AgentConfigJsonContext.Default.AgentConfigDocument) ?? throw new E2EException($"{configPath} holds no registry.");

    public static void RewriteRegistry(string configPath, Func<AgentConfigDocument, AgentConfigDocument> change) => File.WriteAllText(configPath, JsonSerializer.Serialize(change(Registry(configPath)), AgentConfigJsonContext.Default.AgentConfigDocument));

    public static void AssertRegistered(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult? deployed)
    {
        var records = Registry(copy.ConfigPath).ManagedAgents.Where(agent => string.Equals(agent.DisplayName, copy.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();

        context.Assert.True(records.Length == 1 && string.Equals(records[0].AgentId, copy.AgentId, StringComparison.OrdinalIgnoreCase), $"{copy.ConfigPath} registers {copy.DisplayName} exactly once, under its record id");
        context.Assert.Equal(copy.EnvironmentId, records[0].EnvironmentId, Labelled(copy, "the record's home is the copy's environment"));

        if (IsWorkflow(copy))
        {
            context.Assert.Equal(AgentTypes.McsWorkflow, records[0].AgentType, $"the workflow copy {copy.DisplayName} is registered as a workflow");
            context.Assert.Equal(copy.McsWorkflowId, records[0].McsWorkflowId, $"the record of the workflow copy {copy.DisplayName} names the copy's own flow id");
            return;
        }

        context.Assert.Equal(deployed?.TargetMcsAgentId ?? string.Empty, records[0].McsAgentId ?? string.Empty, "the record names a cloud agent exactly when the copy was deployed, and it is the deployed one");
    }

    public static async Task<CopySeed> FixtureSeedAsync(ScenarioContext context, SessionAgentKind kind)
    {
        await McsSteps.PinEnvironmentAsync(context).ConfigureAwait(false);

        var agent = await McsSteps.SessionAsync(context, kind).ConfigureAwait(false);
        RequireDevEnvironment(context, agent.EnvironmentId, $"the {kind} session agent");

        return new CopySeed(kind.ToString(), agent.CloudAgentId, agent.EnvironmentId, agent.Deployed.SchemaName ?? agent.Workspace.SchemaName, DisplayNameOf(agent.Workspace.ProjectDirectory), agent.Workspace.ProjectDirectory, agent.Workspace.AgentName);
    }

    public static Task<WorkflowSeed> WorkflowSeedAsync(ScenarioContext context) =>
        context.Session.PooledAsync(WorkflowSeedPool, WorkflowSeedName, async () =>
        {
            var source = await WorkflowSourceAsync(context, "workflow-seed").ConfigureAwait(false);
            var deployed = await WorkflowSteps.DeployAsync(context, source, source.RecordId!).ConfigureAwait(false);
            RequireDevEnvironment(context, deployed.TargetEnvironmentId, deployed.DisplayName);

            return new WorkflowSeed(WorkflowSeedName, CloudWorkflowId(deployed), deployed.TargetEnvironmentId);
        });

    public static Task<CorpusSeed> CorpusSeedAsync(ScenarioContext context, string agentFolder) =>
        context.Session.SettledAsync(CorpusSeedPool, agentFolder, async () =>
        {
            var source = await CorpusSourceAsync(context, agentFolder).ConfigureAwait(false);
            var before = TreeSnapshot.Take(source.Root);
            var ((copy, deployed, clone), seconds) = await TimeAsync(() => VerifiedFirstDeployAsync(context, LocalSource(source), "seed")).ConfigureAwait(false);
            AssertLocalSourceIntact(context, source, before);

            return new CorpusSeed(SeedOf(agentFolder, copy, deployed), copy, deployed, clone, seconds);
        });

    public static void AssertLocalSourceIntact(ScenarioContext context, Workspace source, TreeSnapshot before) => context.Assert.NoDifferences(TreeSnapshot.Take(source.Root).DifferencesFrom(before), $"the local source {source.AgentName} is byte-identical");

    public static IReadOnlyList<string> LeftBesideTheProjectLock(string project) => LeftBesideTheLock(project, project);

    public static IReadOnlyList<string> LeftInCopyState(string project) => LeftBesideTheLock(project, CopyProjectState.Root(project));

    public static async Task<(T Value, double Seconds)> TimeAsync<T>(Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = await action().ConfigureAwait(false);

        return (value, stopwatch.Elapsed.TotalSeconds);
    }

    public static async Task<Workspace> CorpusSourceAsync(ScenarioContext context, string agentFolder, string label = "corpus")
    {
        await McsSteps.PinEnvironmentAsync(context).ConfigureAwait(false);

        return await InitialisedAsync(context, context.Workspaces.CorpusCopy(context.ScenarioId, agentFolder, label)).ConfigureAwait(false);
    }

    public static async Task<Workspace> FixtureSourceAsync(ScenarioContext context, bool classic = false, string label = "source")
    {
        await McsSteps.PinEnvironmentAsync(context).ConfigureAwait(false);

        return await InitialisedAsync(context, classic ? context.Workspaces.ClassicAgent(context.ScenarioId, label) : context.Workspaces.CliAgent(context.ScenarioId, label)).ConfigureAwait(false);
    }

    public static async Task<Workspace> WorkflowSourceAsync(ScenarioContext context, string label = "workflow-source", string? parentDirectory = null)
    {
        var source = context.Workspaces.Workflow(context.ScenarioId, label, parentDirectory);
        var initialised = await WorkflowSteps.InitAsync(context, source).ConfigureAwait(false);
        RequireDevEnvironment(context, initialised.EnvironmentId, source.AgentName);

        return source with { RecordId = initialised.AgentId };
    }

    public static async Task<Workspace> ClonedSourceAsync(ScenarioContext context, CopySeed seed, string label = "clone")
    {
        await McsSteps.PinEnvironmentAsync(context).ConfigureAwait(false);

        var (directory, created) = await McsSteps.ClonedAsync(context, seed.CloudAgentId, seed.EnvironmentId, label).ConfigureAwait(false);
        RequireDevEnvironment(context, created.EnvironmentId, created.DisplayName);

        return McsSteps.CreatedWorkspace(context.ScenarioId, directory, created, seed.SchemaName) with { DeployForbidden = true };
    }

    public static Task<TreeSnapshot> BaselineAsync(ScenarioContext context, CloudSeed seed) =>
        context.Session.PooledAsync(BaselinePool, seed.CloudId, async () => TreeSnapshot.Take(await CloneSeedAsync(context, seed, $"baseline-{seed.Name}").ConfigureAwait(false), CloneState));

    public static async Task AssertSourceIntactAsync(ScenarioContext context, CloudSeed seed)
    {
        var baseline = await BaselineAsync(context, seed).ConfigureAwait(false);

        context.Assert.NoDifferences(TreeSnapshot.Take(await CloneSeedAsync(context, seed, $"recheck-{seed.Name}").ConfigureAwait(false), CloneState).DifferencesFrom(baseline), $"{seed.Name} is unchanged in the cloud");
    }

    public static Task<string> CloneAsync(ScenarioContext context, string agentId, string environmentId, string label) => McsSteps.CloneAsync(context, agentId, environmentId, Unique(label));

    public static async Task AssertOneCloudAgentAsync(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed, string check)
    {
        var (withId, withSchemaName) = await context.Cloud.CountAgentsAsync(copy.EnvironmentId, CloudAgentId(deployed), SchemaNameOf(copy), context.CancellationToken).ConfigureAwait(false);

        context.Assert.True(withId == 1 && withSchemaName == 1, $"{check} ({withId} by id, {withSchemaName} by schema name)");
    }

    public static Task AssertAgentGoneAsync(ScenarioContext context, ManagedAgentCopyResult copy, string agentId, string check) => context.Assert.EventuallyAsync(async () => await context.Cloud.CountAgentsAsync(copy.EnvironmentId, agentId, SchemaNameOf(copy), context.CancellationToken).ConfigureAwait(false) is (0, 0), TimeSpan.FromMinutes(3), check);

    public static Task AssertWorkflowGoneAsync(ScenarioContext context, string environmentId, string workflowId, string check) => context.Assert.EventuallyAsync(async () => await context.Cloud.WorkflowExistsAsync(environmentId, workflowId, context.CancellationToken).ConfigureAwait(false) == false, TimeSpan.FromMinutes(3), check);

    public static async Task AssertWorkflowsExistAsync(ScenarioContext context, string environmentId, IEnumerable<string> workflowIds, string owner)
    {
        foreach (var workflow in workflowIds)
        {
            context.Assert.True(await context.Cloud.WorkflowExistsAsync(environmentId, workflow, context.CancellationToken).ConfigureAwait(false) == true, $"{owner}'s workflow {workflow} exists");
        }
    }

    public static async Task AssertNothingSentAsync(ScenarioContext context, ManagedAgentCopyResult copy, string check)
    {
        var workflows = OwnedIdentities(copy, CopyIdentityKinds.Workflow);

        if (!IsWorkflow(copy))
        {
            context.Assert.False((await context.Cloud.AgentsAsync(copy.EnvironmentId, context.CancellationToken).ConfigureAwait(false)).Any(agent => string.Equals(agent.SchemaName, copy.SchemaName, StringComparison.OrdinalIgnoreCase)), $"{check}: no agent carries the copy's schema name {copy.SchemaName}");
        }

        context.Assert.NoDifferences([.. (await context.Cloud.WorkflowsAsync(copy.EnvironmentId, context.CancellationToken).ConfigureAwait(false)).Where(workflow => workflows.Contains(workflow.WorkflowId, StringComparer.OrdinalIgnoreCase)).Select(workflow => $"{workflow.WorkflowId} ({workflow.DisplayName}) exists")], $"{check}: no workflow of the copy exists");
    }

    public static async Task<string> AssertIsolatedAsync(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed)
    {
        var clone = IsWorkflow(copy) ? await CloneWorkflowAsync(context, CloudWorkflowId(deployed), "workflow-isolation").ConfigureAwait(false) : await CloneAsync(context, CloudAgentId(deployed), copy.EnvironmentId, "isolation").ConfigureAwait(false);

        context.Assert.NoDifferences(SourceIdentityMentions(clone, copy), $"the deployed {copy.DisplayName} names none of its source's identities");

        return clone;
    }

    public static IReadOnlyList<string> OwnedIdentities(ManagedAgentCopyResult copy, string kind) => [.. copy.Identities.Where(identity => identity.Kind == kind).Select(identity => identity.Destination)];

    public static IReadOnlyList<string> SourceSchemaMentions(ManagedAgentCopyResult copy) => copy.SourceSchemaName is { } sourceSchemaName ? IdentityMentions(copy.WorkspaceDirectory, [sourceSchemaName]) : [];

    public static IReadOnlyList<string> SourceIdentityMentions(string directory, ManagedAgentCopyResult copy) => IdentityMentions(directory, copy.Identities.Select(identity => identity.Source));

    public static IReadOnlyList<string> IdentityMentions(string directory, IEnumerable<string> identities) =>
        identities.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } distinct
            ? [.. FileTree.Matches(directory, new Regex($"(?<![A-Za-z0-9_])(?:{string.Join('|', distinct.OrderByDescending(identity => identity.Length).Select(Regex.Escape))})(?![A-Za-z0-9_])", RegexOptions.IgnoreCase), CloneState).Select(found => $"{Path.GetRelativePath(directory, found.Path)} names {string.Join(", ", found.Values)}")]
            : [];

    public static string DisplayNameOf(string workspaceDirectory) => ScalarOf(Path.Combine(workspaceDirectory, SettingsFileName), "displayName");

    public static string WorkflowMetadataOf(string workspaceDirectory, string key) => ScalarOf(WorkflowSteps.Folder(workspaceDirectory).MetadataPath, key);

    private static async Task<Workspace> InitialisedAsync(ScenarioContext context, Workspace source)
    {
        var result = await McsSteps.InitAsync(context, source).ConfigureAwait(false);
        context.Assert.ExitZero(result, $"agent init {source.AgentName}");

        var initialised = result.Json<ManagedAgentInitResult>();
        RequireDevEnvironment(context, initialised.EnvironmentId, source.AgentName);

        return source with { RecordId = initialised.AgentId };
    }

    private static void RequireDevEnvironment(ScenarioContext context, string? environmentId, string subject)
    {
        if (!string.Equals(environmentId, context.DevEnvironmentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException($"Safety stop: {subject} is bound to {environmentId}, not the dev environment {context.DevEnvironmentId}, so nothing is copied or deployed from it.");
        }
    }

    private static string SchemaNameOf(ManagedAgentCopyResult copy) => copy.SchemaName ?? throw new E2EException($"{copy.DisplayName} is a {copy.AgentType} copy, which has no schema name.");

    private static async Task AssertOneCloudWorkflowAsync(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed, string check)
    {
        var named = (await context.Cloud.WorkflowsAsync(copy.EnvironmentId, context.CancellationToken).ConfigureAwait(false)).Where(workflow => string.Equals(workflow.DisplayName, copy.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();

        context.Assert.True(named is [var only] && string.Equals(only.WorkflowId, CloudWorkflowId(deployed), StringComparison.OrdinalIgnoreCase), $"{check} ({named.Length} flows carry the name)");
    }

    private static Task<string> CloneSeedAsync(ScenarioContext context, CloudSeed seed, string label) => seed is WorkflowSeed ? CloneWorkflowAsync(context, seed.CloudId, label) : CloneAsync(context, seed.CloudId, seed.EnvironmentId, label);

    private static async Task<string> CloneWorkflowAsync(ScenarioContext context, string workflowId, string label) => (await WorkflowSteps.CloneAsync(context, workflowId, Unique(label)).ConfigureAwait(false)).ProjectDirectory;

    private static string Unique(string label) => $"{label}-{Guid.NewGuid().ToString()[..6]}";

    private static string ScalarOf(string yamlPath, string key) => File.ReadLines(yamlPath).Select(line => line.Split(':', 2)).First(parts => parts.Length == 2 && parts[0] == key)[1].Trim();

    private static IReadOnlyList<string> LeftBesideTheLock(string project, string directory) => [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Select(entry => Path.GetRelativePath(project, entry)).Where(entry => !ProjectCopyLock.Contains(entry, StringComparer.OrdinalIgnoreCase)).Select(entry => $"{entry} is left behind")];
}
