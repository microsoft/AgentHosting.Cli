using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>Steps every MCS scenario shares, so a deploy is described once rather than fifty times.</summary>
public static class McsSteps
{
    /// <summary>Refuses to run when the workspace's schema name is already in the target environment.</summary>
    public static async Task GuardSchemaNameAsync(ScenarioContext context, Workspace workspace, string environmentId)
    {
        if (await context.Cloud.SchemaNameExistsAsync(environmentId, workspace.SchemaName).ConfigureAwait(false))
        {
            throw new ScenarioBlockedException(
                $"an agent with schema name {workspace.SchemaName} already exists in {environmentId}; deploying would adopt it and cleanup would then delete it",
                "schema-name collision");
        }
    }

    /// <summary>Initialises a workspace as a local agent record.</summary>
    public static async Task<MsagentResult> InitAsync(ScenarioContext context, Workspace workspace)
    {
        var result = await context.Msagent.RunAsync(
            ["agent", "init", "--agent-name", workspace.AgentName, "--agent-type", "MCSAgent", "--json", "--non-interactive"],
            workspace.ProjectDirectory).ConfigureAwait(false);

        context.Ledger.RecordLocalWorkspace(workspace.Root, context.ScenarioId);
        return result;
    }

    /// <summary>Creates a deployment slot pointing at an environment.</summary>
    public static Task<MsagentResult> CreateSlotAsync(ScenarioContext context, Workspace workspace, string deploymentName, string environmentId, string deploymentType = "dev") =>
        context.Msagent.RunAsync(
            ["deployment", "create", "--deployment-name", deploymentName, "--deployment-type", deploymentType, "--environment-id", environmentId, "--json", "--non-interactive"],
            workspace.ProjectDirectory);

    /// <summary>Deploys, records the created agent, and returns the parsed result.</summary>
    public static async Task<ManagedAgentDeployResult> DeployAsync(ScenarioContext context, Workspace workspace, string deploymentName, params string[] extraArguments)
    {
        RecordIntent(context, workspace, deploymentName);
        var result = await RawDeployAsync(context, workspace, deploymentName, extraArguments).ConfigureAwait(false);
        context.Assert.ExitZero(result, "agent deploy");

        var deployed = result.Json<ManagedAgentDeployResult>();
        RecordDeployed(context, workspace, deploymentName, deployed);
        return deployed;
    }

    /// <summary>Names the agent a deploy is about to create, so a crash mid-deploy still leaves a cleanup trail.</summary>
    public static void RecordIntent(ScenarioContext context, Workspace workspace, string deploymentName)
    {
        if (workspace.SchemaName is { Length: > 0 } schemaName)
        {
            context.Ledger.RecordIntendedMcsAgent(schemaName, context.DevEnvironmentId, workspace.ProjectDirectory, deploymentName, context.ScenarioId);
        }
    }

    /// <summary>Deploys without asserting, for the scenarios whose point is the failure.</summary>
    public static Task<MsagentResult> RawDeployAsync(ScenarioContext context, Workspace workspace, string deploymentName, params string[] extraArguments) =>
        context.Msagent.RunAsync(
            ["agent", "deploy", "--deployment-name", deploymentName, "--json", "--non-interactive", .. extraArguments],
            workspace.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(10));

    /// <summary>Enters a deployed agent in the ledger the instant its service-assigned id exists.</summary>
    public static void RecordDeployed(ScenarioContext context, Workspace workspace, string deploymentName, ManagedAgentDeployResult deployed)
    {
        if (deployed.TargetMcsAgentId is not { Length: > 0 } agentId)
        {
            return;
        }

        if (workspace.SchemaName is { Length: > 0 } schemaName)
        {
            context.Ledger.ResolveMcsAgent(schemaName, agentId);
        }

        context.Ledger.RecordMcsAgent(agentId, deployed.TargetEnvironmentId, deployed.SchemaName, workspace.ProjectDirectory, deploymentName, context.ScenarioId);
    }

    /// <summary>Initialises, creates a slot, and deploys — the preamble most cloud scenarios need.</summary>
    public static async Task<(Workspace Workspace, ManagedAgentDeployResult Deployed)> DeployedCliAgentAsync(ScenarioContext context, string deploymentName = "e2e-dev", string? label = null)
    {
        var workspace = context.Workspaces.CliAgent(context.ScenarioId, label);
        await GuardSchemaNameAsync(context, workspace, context.DevEnvironmentId).ConfigureAwait(false);
        await InitAsync(context, workspace).ConfigureAwait(false);
        await CreateSlotAsync(context, workspace, deploymentName, context.DevEnvironmentId).ConfigureAwait(false);
        var deployed = await DeployAsync(context, workspace, deploymentName).ConfigureAwait(false);
        return (workspace, deployed);
    }

    /// <summary>The classic-shape equivalent, used by the knowledge, prompt, and conflict scenarios.</summary>
    public static async Task<(Workspace Workspace, ManagedAgentDeployResult Deployed)> DeployedClassicAgentAsync(ScenarioContext context, string deploymentName = "e2e-dev", string? label = null)
    {
        var workspace = context.Workspaces.ClassicAgent(context.ScenarioId, label);
        await GuardSchemaNameAsync(context, workspace, context.DevEnvironmentId).ConfigureAwait(false);
        await InitAsync(context, workspace).ConfigureAwait(false);
        await CreateSlotAsync(context, workspace, deploymentName, context.DevEnvironmentId).ConfigureAwait(false);
        var deployed = await DeployAsync(context, workspace, deploymentName).ConfigureAwait(false);
        return (workspace, deployed);
    }

    /// <summary>Clones a deployed agent into a fresh directory, which is how content is proved to have landed.</summary>
    public static async Task<string> CloneAsync(ScenarioContext context, ManagedAgentDeployResult deployed, string label)
    {
        var directory = context.Workspaces.EmptyDirectory(context.ScenarioId, label);

        var result = await context.Msagent.RunAsync(
            ["agent", "create", "--agent-type", "MCSAgent", "--mcs-clone-agent-id", deployed.TargetMcsAgentId!, "--mcs-clone-environment-id", deployed.TargetEnvironmentId, "--json", "--non-interactive"],
            directory,
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

        context.Assert.ExitZero(result, $"clone back ({label})");
        return directory;
    }

    /// <summary>Finds a file under a cloned directory, whose layout the service decides.</summary>
    public static string? FindFile(string root, string fileName) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>Scaffolds a new MCS agent the way a user does, rather than cloning an existing one.</summary>
    public static Task<MsagentResult> ScaffoldAsync(ScenarioContext context, string directory, string name, string shape, params string[] extraArguments) =>
        context.Msagent.RunAsync(
            ["agent", "create", "--agent-type", "MCSAgent", "--mcs-agent-type", shape,
             "--agent-name", name,
             "--initial-prompt", $"Help answer questions for {name}.",
             "--description", $"{name} created by the e2e suite.",
             "--environment-id", context.DevEnvironmentId,
             "--json", "--non-interactive", .. extraArguments],
            directory,
            timeout: TimeSpan.FromMinutes(5));

    /// <summary>
    /// Returns a long-lived agent, deploying it on first use. Scenarios that only need an agent to
    /// exist share these rather than paying for a deploy each.
    /// </summary>
    public static Task<SessionAgent> SessionAsync(ScenarioContext context, SessionAgentKind kind) =>
        context.Session.AgentAsync(kind, () => DeploySessionAgentAsync(context, kind));

    private static async Task<SessionAgent> DeploySessionAgentAsync(ScenarioContext context, SessionAgentKind kind)
    {
        var workspace = kind switch
        {
            SessionAgentKind.ScaffoldedCli => await ScaffoldedWorkspaceAsync(context, kind, "CLI").ConfigureAwait(false),
            SessionAgentKind.ScaffoldedClassic => await ScaffoldedWorkspaceAsync(context, kind, "Classic").ConfigureAwait(false),
            SessionAgentKind.FixtureCli => context.Workspaces.CliAgent(SessionLabel(kind)),
            _ => context.Workspaces.ClassicAgent(SessionLabel(kind)),
        };

        await GuardSchemaNameAsync(context, workspace, context.DevEnvironmentId).ConfigureAwait(false);

        if (kind is SessionAgentKind.FixtureCli or SessionAgentKind.FixtureClassic)
        {
            await InitAsync(context, workspace).ConfigureAwait(false);
        }

        await CreateSlotAsync(context, workspace, SessionDeploymentName, context.DevEnvironmentId).ConfigureAwait(false);
        var deployed = await DeployAsync(context, workspace, SessionDeploymentName).ConfigureAwait(false);

        return new SessionAgent(kind, workspace, deployed, SessionDeploymentName);
    }

    /// <summary>
    /// Scaffolds a session agent into a directory unique to the attempt, so a retry after a failed
    /// deploy does not collide with the local record the previous attempt already registered.
    /// </summary>
    private static async Task<Workspace> ScaffoldedWorkspaceAsync(ScenarioContext context, SessionAgentKind kind, string shape)
    {
        var label = $"{SessionLabel(kind)}-{Guid.NewGuid().ToString()[..4]}";
        var root = context.Workspaces.EmptyDirectory(label);
        var name = $"ahe2e {shape} {label}";

        var result = await ScaffoldAsync(context, root, name, shape).ConfigureAwait(false);
        context.Assert.ExitZero(result, $"scaffold a {shape} agent");
        context.Ledger.RecordLocalWorkspace(root, context.ScenarioId);

        var created = result.Json<ManagedAgentCreateResult>();

        return new Workspace
        {
            ScenarioId = label,
            Root = root,
            ProjectDirectory = created.ProjectDirectory is { Length: > 0 } project && Directory.Exists(project) ? project : root,
            AgentName = created.DisplayName,
            SchemaName = created.SchemaName ?? string.Empty,
        };
    }

    /// <summary>The deployment slot every session agent uses.</summary>
    public const string SessionDeploymentName = "e2e-dev";

    private static string SessionLabel(SessionAgentKind kind) => kind switch
    {
        SessionAgentKind.ScaffoldedCli => "sCli",
        SessionAgentKind.ScaffoldedClassic => "sCls",
        SessionAgentKind.FixtureCli => "fCli",
        _ => "fCls",
    };

    /// <summary>Selects an environment, which the CLI takes as a positional argument rather than an option.</summary>
    public static Task<MsagentResult> SelectEnvironmentAsync(ScenarioContext context, string environmentId, params string[] extraArguments) =>
        context.Msagent.RunAsync(["env", "select", environmentId, "--non-interactive", .. extraArguments]);

    /// <summary>Deletes an agent by name, piping the typed confirmation token.</summary>
    public static Task<MsagentResult> DeleteAgentAsync(ScenarioContext context, Workspace workspace, string deploymentName, string? confirmation = null) =>
        context.Msagent.RunAsync(
            ["agent", "delete", "--agent-name", workspace.AgentName, "--deployment-name", deploymentName, "--json"],
            workspace.ProjectDirectory,
            stdin: confirmation ?? workspace.AgentName,
            timeout: TimeSpan.FromMinutes(5));

    /// <summary>Deletes a deployment slot, optionally taking the cloud agent with it.</summary>
    public static Task<MsagentResult> DeleteDeploymentAsync(ScenarioContext context, Workspace workspace, string deploymentName, bool deleteCloudAgent, string? confirmation = null) =>
        context.Msagent.RunAsync(
            deleteCloudAgent
                ? ["deployment", "delete", "--deployment-name", deploymentName, "--delete-deployed-mcs-agent", "--json"]
                : ["deployment", "delete", "--deployment-name", deploymentName, "--json"],
            workspace.ProjectDirectory,
            stdin: confirmation ?? deploymentName,
            timeout: TimeSpan.FromMinutes(5));
}