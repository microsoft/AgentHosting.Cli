using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>Steps the standalone workflow scenarios share, so a deploy is described once.</summary>
public static class WorkflowSteps
{
    /// <summary>The deployment slot every workflow scenario creates.</summary>
    public const string DeploymentName = "e2ewf";

    private static readonly string[] WorkflowType = ["--agent-type", AgentTypes.McsWorkflow];

    /// <summary>Scaffolds a workflow from the template into a directory.</summary>
    public static async Task<ManagedAgentCreateResult> ScaffoldAsync(ScenarioContext context, string directory, string name)
    {
        var result = await context.Msagent.RunAsync(
            ["agent", "create", .. WorkflowType, "--agent-name", name, "--environment-id", context.DevEnvironmentId, "--json", "--non-interactive"],
            directory,
            timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent create --agent-type MCSWorkflow");
        context.Ledger.RecordLocalWorkspace(directory, context.ScenarioId);

        return Track(context, result.Json<ManagedAgentCreateResult>(), directory);
    }

    /// <summary>Registers a workflow that already sits on disk.</summary>
    public static async Task<ManagedAgentInitResult> InitAsync(ScenarioContext context, Workspace workspace)
    {
        var result = await context.Msagent.RunAsync(
            ["agent", "init", .. WorkflowType, "--agent-name", workspace.AgentName, "--json", "--non-interactive"],
            workspace.ProjectDirectory).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent init --agent-type MCSWorkflow");
        context.Ledger.RecordLocalWorkspace(workspace.Root, context.ScenarioId);

        var initialised = result.Json<ManagedAgentInitResult>();
        Record(context, initialised.McsWorkflowId, initialised.AgentId, initialised.DisplayName, workspace.ProjectDirectory);

        return initialised;
    }

    /// <summary>Clones a cloud workflow into a fresh directory.</summary>
    public static async Task<ManagedAgentCreateResult> CloneAsync(ScenarioContext context, string workflowId, string label)
    {
        var directory = context.Workspaces.EmptyDirectory(context.ScenarioId, label);

        var result = await context.Msagent.RunAsync(
            ["agent", "create", .. WorkflowType, "--mcs-clone-workflow-id", workflowId, "--mcs-clone-environment-id", context.DevEnvironmentId, "--json", "--non-interactive"],
            directory,
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

        context.Assert.ExitZero(result, $"clone back ({label})");
        context.Ledger.RecordLocalWorkspace(directory, context.ScenarioId);

        return result.Json<ManagedAgentCreateResult>();
    }

    /// <summary>Creates the deployment slot, then deploys, asserting both.</summary>
    public static async Task<ManagedAgentDeployResult> DeployAsync(ScenarioContext context, Workspace workspace, string agentId, params string[] extraArguments)
    {
        context.Assert.ExitZero(
            await McsSteps.CreateSlotAsync(context, workspace, DeploymentName, context.DevEnvironmentId).ConfigureAwait(false),
            "deployment create for a workflow");

        return await DeployOnlyAsync(context, workspace, agentId, extraArguments).ConfigureAwait(false);
    }

    /// <summary>Deploys an already-slotted workflow, for the scenarios that deploy twice.</summary>
    public static async Task<ManagedAgentDeployResult> DeployOnlyAsync(ScenarioContext context, Workspace workspace, string agentId, params string[] extraArguments)
    {
        var result = await RawDeployAsync(context, workspace, agentId, extraArguments).ConfigureAwait(false);
        context.Assert.ExitZero(result, "agent deploy");

        var deployed = result.Json<ManagedAgentDeployResult>();
        Record(context, deployed.McsWorkflowId, agentId, deployed.DisplayName, workspace.ProjectDirectory);

        return deployed;
    }

    /// <summary>Deploys without asserting, for the scenarios whose point is the failure.</summary>
    public static Task<MsagentResult> RawDeployAsync(ScenarioContext context, Workspace workspace, string agentId, params string[] extraArguments) =>
        context.Msagent.RunAsync(
            ["agent", "deploy", "--deployment-name", DeploymentName, "--agent-id", agentId, "--json", "--non-interactive", .. extraArguments],
            workspace.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(10));

    /// <summary>Pulls the published definition, which replaces local edits only when <c>--overwrite</c> is supplied.</summary>
    public static Task<MsagentResult> PullAsync(ScenarioContext context, Workspace workspace, string agentId, params string[] extraArguments) =>
        context.Msagent.RunAsync(
            ["agent", "pull", "--deployment-name", DeploymentName, "--agent-id", agentId, "--json", "--non-interactive", .. extraArguments],
            workspace.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(10));

    /// <summary>Lists the workflows an environment holds, from the directory holding the local records.</summary>
    public static Task<MsagentResult> ListAsync(ScenarioContext context, string? workingDirectory = null, params string[] extraArguments) =>
        context.Msagent.RunAsync(
            ["agent", "list", .. WorkflowType, "--environment-id", context.DevEnvironmentId, "--json", "--non-interactive", .. extraArguments],
            workingDirectory,
            timeout: TimeSpan.FromMinutes(5));

    /// <summary>Deletes a workflow, piping the typed confirmation.</summary>
    public static Task<MsagentResult> DeleteAsync(ScenarioContext context, Workspace workspace, string agentId, string displayName, string? confirmation = null, string? deploymentName = null) =>
        context.Msagent.RunAsync(
            deploymentName is { Length: > 0 } deployment
                ? ["agent", "delete", "--agent-id", agentId, "--deployment-name", deployment, "--json"]
                : ["agent", "delete", "--agent-id", agentId, "--json"],
            workspace.ProjectDirectory,
            stdin: confirmation ?? displayName,
            timeout: TimeSpan.FromMinutes(5));

    /// <summary>The workflow folder a workspace holds, found by its trailing identifier.</summary>
    public static WorkflowFolder Folder(string workspaceRoot) => WorkflowWorkspace.FindAll(workspaceRoot).Single();

    /// <summary>A workspace addressing the directory a scaffold or clone wrote into.</summary>
    public static Workspace WorkspaceFor(Workspace source, string projectDirectory, string? workflowId = null) =>
        source with { ProjectDirectory = projectDirectory, WorkflowId = workflowId ?? source.WorkflowId };

    private static ManagedAgentCreateResult Track(ScenarioContext context, ManagedAgentCreateResult created, string directory)
    {
        Record(context, created.McsWorkflowId, created.AgentId, created.DisplayName, created.ProjectDirectory is { Length: > 0 } project && Directory.Exists(project) ? project : directory);

        return created;
    }

    private static void Record(ScenarioContext context, string? workflowId, string agentId, string? displayName, string projectDirectory)
    {
        if (workflowId is { Length: > 0 } id)
        {
            context.Ledger.RecordMcsWorkflow(id, context.DevEnvironmentId, displayName, projectDirectory, agentId, context.ScenarioId);
        }
    }
}
