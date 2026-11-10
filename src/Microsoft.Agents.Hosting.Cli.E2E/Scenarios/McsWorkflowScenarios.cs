using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>The standalone Copilot Studio workflow lifecycle, which creates no agent of its own.</summary>
public static class McsWorkflowScenarios
{
    private static readonly ScenarioBuilder Builder = new("mcs-workflow");

    private static readonly Capability[] Dev = [Capability.Auth, Capability.McsDev];
    private static readonly Capability[] DevWorkflow = [Capability.Auth, Capability.McsDev, Capability.WorkspaceWorkflow];

    /// <summary>Every mcs-workflow scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All() =>
    [
        .. LocalAuthoring(),
        .. CreateAndDeploy(),
        .. InitAndDeploy(),
        .. CloneFromCloud(),
        .. RoundTrip(),
        .. Listing(),
        .. Delete(),
    ];

    private static IEnumerable<Scenario> LocalAuthoring()
    {
        yield return Builder.Make("MW-01", "agent create --agent-type MCSWorkflow scaffolds a workflow", Dev, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);
            var created = await WorkflowSteps.ScaffoldAsync(context, directory, Name(context)).ConfigureAwait(false);

            context.Assert.Equal(AgentTypes.McsWorkflow, created.AgentType, "the record is a workflow");
            context.Assert.Equal("scaffold", created.CreatedFrom, "it was scaffolded rather than cloned");
            context.Assert.True(Guid.TryParse(created.McsWorkflowId, out _), "a workflow id was generated");
            context.Assert.True(created.CreatedFiles.Count > 0, "the template wrote files");
            context.Assert.True(
                created.CreatedFiles.All(file => File.Exists(Path.Combine(created.ProjectDirectory, file))),
                "every created file is reported relative to the project directory");
            context.Assert.True(
                created.CreatedFiles.Any(file => file.EndsWith(WorkflowWorkspace.DefinitionFileName, StringComparison.Ordinal)),
                "workflow.json is reported as created");
            context.Assert.True(
                created.CreatedFiles.Any(file => file.EndsWith(WorkflowWorkspace.MetadataFileName, StringComparison.Ordinal)),
                "metadata.yml is reported as created");
            context.Assert.True(Directory.Exists(created.WorkflowFolder), "the workflow folder exists");
            context.Assert.FileExists(Path.Combine(created.WorkflowFolder!, WorkflowWorkspace.MetadataFileName), "metadata.yml was written");
            context.Assert.FileExists(Path.Combine(created.WorkflowFolder!, WorkflowWorkspace.DefinitionFileName), "workflow.json was written");
        });

        yield return Builder.Make("MW-02", "a scaffold writes no agent project and no .mcs cache", Dev, SafetyTier.Creating, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);
            var created = await WorkflowSteps.ScaffoldAsync(context, directory, Name(context)).ConfigureAwait(false);

            context.Assert.False(Directory.Exists(Path.Combine(created.ProjectDirectory, ".mcs")), "no cloud-mirror cache is written");
            context.Assert.False(File.Exists(Path.Combine(created.ProjectDirectory, "settings.mcs.yml")), "no agent project file is written");
            context.Assert.FileExists(created.ConfigPath, "the record is written to the project config");
        });

        yield return Builder.Make("MW-03", "agent create --agent-type MCSWorkflow requires a name", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", AgentTypes.McsWorkflow, "--json", "--non-interactive"],
                context.Workspaces.EmptyDirectory(context.ScenarioId)).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a nameless scaffold is a usage error");
            context.Assert.Contains(result, "--agent-name", "the message names the option to supply");
        });
    }

    private static IEnumerable<Scenario> CreateAndDeploy()
    {
        yield return Builder.Make("MW-10", "a scaffolded workflow deploys and publishes", Dev, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var (workspace, created) = await ScaffoldedAsync(context).ConfigureAwait(false);
            var deployed = await WorkflowSteps.DeployAsync(context, workspace, created.AgentId).ConfigureAwait(false);

            context.Assert.Equal(created.McsWorkflowId, deployed.McsWorkflowId, "the deploy targets the scaffolded workflow");
            context.Assert.Equal(CopilotStudioStates.Published, deployed.WorkflowState, "a workflow with no unbound connection is published");
            context.Assert.True(deployed.UnboundConnectionCount == 0, "the template declares no connection references");

            await context.Assert.EventuallyAsync(
                async () => await context.Cloud.WorkflowExistsAsync(context.DevEnvironmentId, created.McsWorkflowId!, context.CancellationToken).ConfigureAwait(false) == true,
                TimeSpan.FromMinutes(2),
                "the flow is listed in the environment").ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> InitAndDeploy()
    {
        yield return Builder.Make("MW-20", "agent init registers a workflow already on disk", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var workspace = context.Workspaces.Workflow(context.ScenarioId);
            var initialised = await WorkflowSteps.InitAsync(context, workspace).ConfigureAwait(false);

            context.Assert.Equal(AgentTypes.McsWorkflow, initialised.AgentType, "the record is a workflow");
            context.Assert.Equal(workspace.WorkflowId, initialised.McsWorkflowId, "the record addresses the workflow on disk");
            context.Assert.False(Directory.Exists(Path.Combine(workspace.ProjectDirectory, ".mcs")), "init writes no cloud-mirror cache");
        });

        yield return Builder.Make("MW-21", "an initialised workflow deploys and publishes", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, workflowId) = await InitialisedAsync(context).ConfigureAwait(false);
            var deployed = await WorkflowSteps.DeployAsync(context, workspace, agentId).ConfigureAwait(false);

            context.Assert.Equal(workflowId, deployed.McsWorkflowId, "the deploy targets the initialised workflow");
            context.Assert.Equal(CopilotStudioStates.Published, deployed.WorkflowState, "the fixture publishes");

            await context.Assert.EventuallyAsync(
                async () => await context.Cloud.WorkflowExistsAsync(context.DevEnvironmentId, workflowId, context.CancellationToken).ConfigureAwait(false) == true,
                TimeSpan.FromMinutes(2),
                "the flow is listed in the environment").ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> CloneFromCloud()
    {
        yield return Builder.Make("MW-30", "agent create --mcs-clone-workflow-id clones a deployed workflow", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, workflowId) = await InitialisedAsync(context).ConfigureAwait(false);
            await WorkflowSteps.DeployAsync(context, workspace, agentId).ConfigureAwait(false);

            var cloned = await WorkflowSteps.CloneAsync(context, workflowId, "clone").ConfigureAwait(false);

            context.Assert.Equal("clone", cloned.CreatedFrom, "it was cloned rather than scaffolded");
            context.Assert.Equal(workflowId, cloned.McsWorkflowId, "the clone addresses the same cloud flow");
            context.Assert.Equal(workspace.AgentName, cloned.DisplayName, "a clone inherits the source name");
            context.Assert.True(Directory.Exists(cloned.WorkflowFolder), "the clone wrote the workflow folder");
            context.Assert.FileExists(Path.Combine(cloned.WorkflowFolder!, WorkflowWorkspace.DefinitionFileName), "the clone wrote workflow.json");
            context.Assert.Equal("dev", cloned.DeploymentType, "a clone records its home deployment");
        });
    }

    private static IEnumerable<Scenario> RoundTrip()
    {
        yield return Builder.Make("MW-40", "pull reports no change when nothing was published since the deploy", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, _) = await DeployedAsync(context).ConfigureAwait(false);

            var result = await WorkflowSteps.PullAsync(context, workspace, agentId).ConfigureAwait(false);
            context.Assert.ExitZero(result, "agent pull");

            context.Assert.True(result.Json<ManagedAgentPullResult>().ChangeCount == 0, "an unchanged workflow pulls nothing");
        });

        yield return Builder.Make("MW-41", "an ordinary pull keeps a local edit and --overwrite restores the published definition", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, _) = await DeployedAsync(context).ConfigureAwait(false);
            var definitionPath = WorkflowSteps.Folder(workspace.ProjectDirectory).DefinitionPath;
            var marker = $"ahe2e-{context.RunSuffix}";

            EditDescription(WorkflowSteps.Folder(workspace.ProjectDirectory).MetadataPath, marker);

            var redeployed = await WorkflowSteps.DeployOnlyAsync(context, workspace, agentId).ConfigureAwait(false);
            context.Assert.Equal("update", redeployed.Action, "the second deploy updates rather than creates");

            var before = File.ReadAllText(definitionPath);
            File.WriteAllText(definitionPath, "{}");

            var refused = await WorkflowSteps.PullAsync(context, workspace, agentId).ConfigureAwait(false);
            context.Assert.ExitCode(refused, ExitCodes.Usage, "an ordinary pull refuses to discard a local edit");
            context.Assert.Equal("{}", File.ReadAllText(definitionPath), "the refused pull left the local edit alone");

            var pulled = await WorkflowSteps.PullAsync(context, workspace, agentId, "--overwrite").ConfigureAwait(false);
            context.Assert.ExitZero(pulled, "agent pull --overwrite after a local clobber");

            context.Assert.True(pulled.Json<ManagedAgentPullResult>().ChangeCount == 1, "the clobbered definition is one change");
            context.Assert.Equal(before, File.ReadAllText(definitionPath), "pull --overwrite restored the published definition");
            context.Assert.FileContains(WorkflowSteps.Folder(workspace.ProjectDirectory).MetadataPath, marker, "the deployed description came back");
        });
    }

    private static IEnumerable<Scenario> Listing()
    {
        yield return Builder.Make("MW-50", "agent list --agent-type MCSWorkflow lists a deployed workflow", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, workflowId) = await DeployedAsync(context).ConfigureAwait(false);

            var result = await WorkflowSteps.ListAsync(context, workspace.ProjectDirectory).ConfigureAwait(false);
            context.Assert.ExitZero(result, "agent list --agent-type MCSWorkflow");

            var row = result.Json<ManagedAgentListResult>().Agents.FirstOrDefault(agent => string.Equals(agent.McsWorkflowId, workflowId, StringComparison.OrdinalIgnoreCase));

            context.Assert.True(row is not null, "the deployed flow is listed");
            context.Assert.Equal(AgentTypes.McsWorkflow, row!.AgentType, "the row is a workflow");
            context.Assert.Equal(agentId, row.AgentId, "the row carries the local record that owns it");
            context.Assert.Equal(CopilotStudioStates.Published, row.WorkflowState, "a deployed flow lists as published");
            context.Assert.True(row.DeploymentSlotCount >= 1, "the row counts its deployment slots");
            context.Assert.Equal(workspace.AgentName, row.DisplayName, "the row carries the workflow name");
        });

        yield return Builder.Make("MW-51", "--filter narrows the listing by name", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, _, workflowId) = await DeployedAsync(context).ConfigureAwait(false);

            var result = await WorkflowSteps.ListAsync(context, workspace.ProjectDirectory, "--filter", workspace.AgentName).ConfigureAwait(false);
            context.Assert.ExitZero(result, "agent list --filter");

            var listed = result.Json<ManagedAgentListResult>().Agents;

            context.Assert.True(listed.Any(agent => string.Equals(agent.McsWorkflowId, workflowId, StringComparison.OrdinalIgnoreCase)), "the filter keeps the matching flow");
            context.Assert.True(listed.All(agent => (agent.DisplayName ?? string.Empty).Contains(workspace.AgentName, StringComparison.OrdinalIgnoreCase)), "every row matches the filter");
        });

        yield return Builder.Make("MW-52", "a local-only workflow is not listed", Dev, SafetyTier.Creating, run: async context =>
        {
            var (workspace, created) = await ScaffoldedAsync(context).ConfigureAwait(false);

            var result = await WorkflowSteps.ListAsync(context, workspace.ProjectDirectory).ConfigureAwait(false);
            context.Assert.ExitZero(result, "agent list --agent-type MCSWorkflow");

            context.Assert.False(
                result.Json<ManagedAgentListResult>().Agents.Any(agent => string.Equals(agent.McsWorkflowId, created.McsWorkflowId, StringComparison.OrdinalIgnoreCase)),
                "a workflow that was never deployed is not listed");
        });
    }

    private static IEnumerable<Scenario> Delete()
    {
        yield return Builder.Make("MW-60", "a mistyped confirmation deletes nothing", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, workflowId) = await DeployedAsync(context).ConfigureAwait(false);

            var result = await WorkflowSteps.DeleteAsync(context, workspace, agentId, workspace.AgentName, "not-the-name").ConfigureAwait(false);

            context.Assert.ExitZero(result, "a refused confirmation is not an error");
            context.Assert.Equal("name-mismatch", result.Json<ManagedAgentDeleteResult>().Status, "the run reports the mismatch");
            context.Assert.True(
                await context.Cloud.WorkflowExistsAsync(context.DevEnvironmentId, workflowId, context.CancellationToken).ConfigureAwait(false) == true,
                "the flow is still there");
        });

        yield return Builder.Make("MW-61", "agent delete removes the cloud flow and leaves local files alone", DevWorkflow, SafetyTier.Creating, run: async context =>
        {
            var (workspace, agentId, workflowId) = await DeployedAsync(context).ConfigureAwait(false);
            var definitionPath = WorkflowSteps.Folder(workspace.ProjectDirectory).DefinitionPath;

            var result = await WorkflowSteps.DeleteAsync(context, workspace, agentId, workspace.AgentName).ConfigureAwait(false);
            context.Assert.ExitZero(result, "agent delete");

            context.Ledger.MarkDeleted(workflowId);
            context.Assert.FileExists(definitionPath, "local files are not touched");

            await context.Assert.EventuallyAsync(
                async () => await context.Cloud.WorkflowExistsAsync(context.DevEnvironmentId, workflowId, context.CancellationToken).ConfigureAwait(false) == false,
                TimeSpan.FromMinutes(2),
                "the flow is gone from the environment").ConfigureAwait(false);
        });
    }

    private static async Task<(Workspace Workspace, ManagedAgentCreateResult Created)> ScaffoldedAsync(ScenarioContext context)
    {
        var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);
        var created = await WorkflowSteps.ScaffoldAsync(context, directory, Name(context)).ConfigureAwait(false);

        return (new Workspace
        {
            ScenarioId = context.ScenarioId,
            Root = directory,
            ProjectDirectory = created.ProjectDirectory,
            AgentName = created.DisplayName,
            SchemaName = string.Empty,
            WorkflowId = created.McsWorkflowId,
        }, created);
    }

    private static async Task<(Workspace Workspace, string AgentId, string WorkflowId)> InitialisedAsync(ScenarioContext context)
    {
        var workspace = context.Workspaces.Workflow(context.ScenarioId);
        var initialised = await WorkflowSteps.InitAsync(context, workspace).ConfigureAwait(false);

        return (workspace, initialised.AgentId, initialised.McsWorkflowId!);
    }

    private static async Task<(Workspace Workspace, string AgentId, string WorkflowId)> DeployedAsync(ScenarioContext context)
    {
        var (workspace, agentId, workflowId) = await InitialisedAsync(context).ConfigureAwait(false);
        await WorkflowSteps.DeployAsync(context, workspace, agentId).ConfigureAwait(false);

        return (workspace, agentId, workflowId);
    }

    private static void EditDescription(string metadataPath, string marker) =>
        File.WriteAllLines(
            metadataPath,
            File.ReadAllLines(metadataPath).Select(line => line.StartsWith("description:", StringComparison.Ordinal) ? $"description: {marker}" : line));

    private static string Name(ScenarioContext context) => $"ahe2e-{context.ScenarioId}-{context.RunSuffix}";
}
