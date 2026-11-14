using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwSteps
{
    public const string Slot = "swdev";

    public const string TestSlot = "swtest";

    private const string WorkflowType = AgentTypes.McsWorkflow;

    public static async Task<MsagentResult> RunAsync(SwRun run, IReadOnlyList<string> arguments, string? directory, string? stdin = null, TimeSpan? timeout = null)
    {
        var result = await run.Context.Msagent
            .RunAsync(arguments, directory, stdin, timeout ?? TimeSpan.FromMinutes(10), cancellationToken: run.Token)
            .ConfigureAwait(false);

        run.Services.AddTiming(new StandaloneTiming(run.Id, Verb(arguments), result.ExitCode, result.Duration.TotalSeconds));
        return result;
    }

    public static async Task EnsureDevSelectedAsync(SwRun run)
    {
        if (!run.Services.TryClaimEnvironmentSelection())
        {
            return;
        }

        var listed = await RunAsync(run, ["env", "list", "--json", "--non-interactive"], null).ConfigureAwait(false);
        var active = listed.ExitCode == 0
            ? listed.Json<EnvironmentListResult>().Environments.FirstOrDefault(environment => environment.IsActive)?.EnvironmentId
            : null;

        if (string.Equals(active, run.Dev, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        run.Context.Ledger.RecordConfigEdit("env-select", active, run.Id);
        run.Assert.ExitZero(
            await RunAsync(run, ["env", "select", run.Dev, "--non-interactive"], null).ConfigureAwait(false),
            "select the dev environment, which agent init registers records in");
    }

    public static Task<MsagentResult> RawScaffoldAsync(SwRun run, string directory, string name, params string[] extra) =>
        RunAsync(run, ["agent", "create", "--agent-type", WorkflowType, "--agent-name", name, "--environment-id", run.Dev, "--json", "--non-interactive", .. extra], directory, timeout: TimeSpan.FromMinutes(5));

    public static async Task<SwWorkspace> ScaffoldAsync(SwRun run, string? name = null, string? label = null)
    {
        var index = run.NextIndex();
        var directory = run.Context.Workspaces.EmptyDirectory(run.Id, label ?? $"s{index}");
        var result = await RawScaffoldAsync(run, directory, name ?? SwFixtures.NameFor(run, label ?? (index == 1 ? null : $"s{index}"))).ConfigureAwait(false);

        run.Assert.ExitZero(result, "agent create --agent-type MCSWorkflow");
        return FromCreated(result.Json<ManagedAgentCreateResult>(), SwShape.Scaffold, directory);
    }

    public static async Task<MsagentResult> RawInitAsync(SwRun run, string directory, string? name, params string[] extra)
    {
        await EnsureDevSelectedAsync(run).ConfigureAwait(false);

        string[] naming = name is null ? [] : ["--agent-name", name];
        return await RunAsync(run, ["agent", "init", "--agent-type", WorkflowType, .. naming, "--json", "--non-interactive", .. extra], directory, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
    }

    public static async Task<ManagedAgentInitResult> InitAsync(SwRun run, SwWorkspace workspace, string? name = null)
    {
        var result = await RawInitAsync(run, workspace.ProjectDirectory, name ?? workspace.DisplayName).ConfigureAwait(false);
        run.Assert.ExitZero(result, $"agent init ({workspace.Shape})");

        var initialised = result.Json<ManagedAgentInitResult>();
        workspace.AgentId = initialised.AgentId;
        workspace.EnvironmentId = initialised.EnvironmentId;

        if (!string.Equals(initialised.EnvironmentId, run.Dev, StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException($"agent init registered {workspace.DisplayName} in {initialised.EnvironmentId}, not the dev environment: the active environment changed under the run.");
        }

        return initialised;
    }

    public static Task<MsagentResult> RawCloneAsync(SwRun run, string directory, string workflowId, string sourceEnvironmentId, params string[] extra) =>
        RunAsync(run, ["agent", "create", "--agent-type", WorkflowType, "--mcs-clone-workflow-id", workflowId, "--mcs-clone-environment-id", sourceEnvironmentId, "--json", "--non-interactive", .. extra], directory, timeout: TimeSpan.FromMinutes(10));

    public static async Task<SwWorkspace> CloneAsync(SwRun run, string workflowId, string? sourceEnvironmentId = null, string? label = null)
    {
        await EnsureDevSelectedAsync(run).ConfigureAwait(false);

        var directory = run.Context.Workspaces.EmptyDirectory(run.Id, label ?? $"c{run.NextIndex()}");
        var result = await RawCloneAsync(run, directory, workflowId, sourceEnvironmentId ?? run.Dev).ConfigureAwait(false);

        run.Assert.ExitZero(result, "agent create --mcs-clone-workflow-id");
        return FromCreated(result.Json<ManagedAgentCreateResult>(), SwShape.Clone, directory);
    }

    public static Task<SwWorkspace> SecondActorAsync(SwRun run, SwWorkspace original) =>
        CloneAsync(run, original.WorkflowId, original.EnvironmentId ?? run.Dev, "actor");

    public static Task<MsagentResult> RawSlotAsync(SwRun run, SwWorkspace workspace, string name, string type = "dev", string? environmentId = null)
    {
        string[] environment = environmentId is { Length: > 0 } ? ["--environment-id", environmentId] : [];
        string[] agent = workspace.AgentId is { Length: > 0 } id ? ["--agent-id", id] : [];

        return RunAsync(run, ["deployment", "create", .. agent, "--deployment-name", name, "--deployment-type", type, .. environment, "--json", "--non-interactive"], workspace.ProjectDirectory, timeout: TimeSpan.FromMinutes(3));
    }

    public static async Task SlotAsync(SwRun run, SwWorkspace workspace, string name = Slot, string type = "dev", string? environmentId = null) =>
        run.Assert.ExitZero(await RawSlotAsync(run, workspace, name, type, environmentId).ConfigureAwait(false), $"deployment create {name}");

    public static async Task<MsagentResult> RawDeployAsync(SwRun run, SwWorkspace workspace, string slot = Slot, string? environmentId = null, params string[] extra)
    {
        RecordIntent(run, workspace, slot, environmentId ?? workspace.EnvironmentId ?? run.Dev);

        string[] agent = workspace.AgentId is { Length: > 0 } id ? ["--agent-id", id] : [];
        return await RunAsync(run, ["agent", "deploy", .. agent, "--deployment-name", slot, "--json", "--non-interactive", .. extra], workspace.ProjectDirectory, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);
    }

    public static async Task<ManagedAgentDeployResult> DeployAsync(SwRun run, SwWorkspace workspace, string slot = Slot, string? environmentId = null, params string[] extra)
    {
        var result = await RawDeployAsync(run, workspace, slot, environmentId, extra).ConfigureAwait(false);
        run.Assert.ExitZero(result, $"agent deploy --deployment-name {slot}");

        return result.Json<ManagedAgentDeployResult>();
    }

    public static async Task<(SwWorkspace Workspace, ManagedAgentDeployResult Deployed)> DeployedAsync(SwRun run, SwShape shape, bool? activated = null, string? label = null, params string[] deployArguments)
    {
        var workspace = SwFixtures.Materialize(run, shape, label, activated);
        await InitAsync(run, workspace).ConfigureAwait(false);
        await SlotAsync(run, workspace).ConfigureAwait(false);

        return (workspace, await DeployAsync(run, workspace, Slot, null, deployArguments).ConfigureAwait(false));
    }

    public static void RecordIntent(SwRun run, SwWorkspace workspace, string slot, string environmentId)
    {
        foreach (var folder in workspace.Folders)
        {
            var metadata = Path.Combine(folder, "metadata.yml");

            if (SwFixtures.MetadataValue(metadata, "workflowId") is { } id && Guid.TryParse(id, out _))
            {
                run.Context.Ledger.RecordStandaloneWorkflow(id, environmentId, SwFixtures.MetadataValue(metadata, "name") ?? workspace.DisplayName, workspace.ProjectDirectory, workspace.AgentId, slot, run.Id);
            }
        }

        foreach (var (logicalName, connectorId) in SwFixtures.References(workspace))
        {
            if (logicalName.StartsWith(StandaloneServices.ReferencePrefix, StringComparison.OrdinalIgnoreCase))
            {
                run.Context.Ledger.RecordStandaloneConnectionReference(logicalName, connectorId, environmentId, run.Id);
            }
        }
    }

    public static Task<MsagentResult> PullAsync(SwRun run, SwWorkspace workspace, string? slot = Slot, params string[] extra)
    {
        string[] agent = workspace.AgentId is { Length: > 0 } id ? ["--agent-id", id] : [];
        string[] deployment = slot is { Length: > 0 } name ? ["--deployment-name", name] : [];

        return RunAsync(run, ["agent", "pull", .. agent, .. deployment, "--json", "--non-interactive", .. extra], workspace.ProjectDirectory, timeout: TimeSpan.FromMinutes(10));
    }

    public static Task<MsagentResult> ListAsync(SwRun run, string? directory = null, string? environmentId = null, params string[] extra) =>
        RunAsync(run, ["agent", "list", "--agent-type", WorkflowType, "--environment-id", environmentId ?? run.Dev, "--json", "--non-interactive", .. extra], directory, timeout: TimeSpan.FromMinutes(10));

    public static async Task<ManagedAgentListResult> ListedAsync(SwRun run, string? directory = null, string? environmentId = null, params string[] extra)
    {
        var result = await ListAsync(run, directory, environmentId, extra).ConfigureAwait(false);
        run.Assert.ExitZero(result, "agent list --agent-type MCSWorkflow");

        return result.Json<ManagedAgentListResult>();
    }

    public static Task<MsagentResult> ShowAsync(SwRun run, string directory, params string[] extra) =>
        RunAsync(run, ["agent", "show", "--json", "--non-interactive", .. extra], directory, timeout: TimeSpan.FromMinutes(2));

    public static Task<MsagentResult> DeleteAsync(SwRun run, SwWorkspace workspace, string? typed, string? slot = null, params string[] extra)
    {
        string[] deployment = slot is { Length: > 0 } name ? ["--deployment-name", name] : [];

        return RunAsync(run, ["agent", "delete", "--agent-id", workspace.Agent, .. deployment, "--json", .. extra], workspace.ProjectDirectory, stdin: typed, timeout: TimeSpan.FromMinutes(5));
    }

    public static Task<MsagentResult> SlotsAsync(SwRun run, string directory, params string[] extra) =>
        RunAsync(run, ["deployment", "list", "--json", "--non-interactive", .. extra], directory, timeout: TimeSpan.FromMinutes(2));

    public static Task<MsagentResult> DeleteSlotAsync(SwRun run, SwWorkspace workspace, string slot, params string[] extra) =>
        RunAsync(run, ["deployment", "delete", "--agent-id", workspace.Agent, "--deployment-name", slot, "--json", .. extra], workspace.ProjectDirectory, stdin: slot);

    public static async Task<ManagedAgentShowResult?> RecordsAsync(SwRun run, string directory)
    {
        var result = await ShowAsync(run, directory).ConfigureAwait(false);

        return result.ExitCode == 0 ? result.Json<ManagedAgentShowResult>() : null;
    }

    public static bool ReportedActivated(ManagedAgentDeployResult deployed) =>
        string.Equals(deployed.WorkflowState, "published", StringComparison.OrdinalIgnoreCase) || deployed.Published;

    private static SwWorkspace FromCreated(ManagedAgentCreateResult created, SwShape shape, string directory)
    {
        var project = created.ProjectDirectory is { Length: > 0 } written && Directory.Exists(written) ? written : directory;
        var workflowRoot = created.WorkflowFolder is { Length: > 0 } folder
            ? Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!
            : project;

        return new SwWorkspace
        {
            Shape = shape,
            Root = directory,
            ProjectDirectory = project,
            WorkflowRoot = workflowRoot,
            WorkflowId = created.McsWorkflowId ?? throw new E2EException("agent create reported no workflow id."),
            DisplayName = created.DisplayName,
            AgentId = created.AgentId,
            EnvironmentId = created.EnvironmentId,
            HomeDeploymentName = created.DeploymentName,
        };
    }

    private static string Verb(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.TakeWhile(argument => !argument.StartsWith('-')).Take(3));
}
