using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>
/// The Copilot Studio agent lifecycle.
///
/// <para>Four agents are deployed into the dev environment and shared: two scaffolded by
/// <c>agent create</c> and two from the bundled fixtures. A fifth is created in the test environment
/// by promotion. Everything that only needs an agent to exist reuses one, and the scenarios that
/// destroy an agent run last and consume these rather than deploying their own.</para>
/// </summary>
public static class McsAgentScenarios
{
    private static readonly ScenarioBuilder Builder = new("mcs-agent");

    private static readonly Capability[] Auth = [Capability.Auth];
    private static readonly Capability[] Dev = [Capability.Auth, Capability.McsDev];
    private static readonly Capability[] DevCli = [Capability.Auth, Capability.McsDev, Capability.WorkspaceCli];
    private static readonly Capability[] DevClassic = [Capability.Auth, Capability.McsDev, Capability.WorkspaceClassic];
    private static readonly Capability[] Promotion = [Capability.Auth, Capability.McsDev, Capability.McsTest, Capability.WorkspaceCli];
    private static readonly Capability[] WorkspaceCli = [Capability.WorkspaceCli];
    private static readonly Capability[] WorkspaceClassic = [Capability.WorkspaceClassic];

    private const string TestDeploymentName = "e2e-test";
    private const string BasePrompt = "Would you like to end our conversation?";

    /// <summary>Every mcs-agent scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All() =>
    [
        .. EnvironmentTargeting(),
        .. LocalRecords(),
        .. Scaffolding(),
        .. SessionAgentDeploys(),
        .. CloudInventory(),
        .. AuthoringRoundTrip(),
        .. ConflictAndRecovery(),
        .. DeploymentSlots(),
        .. PromotionToTest(),
        .. McsShareScenarios.All(),
        .. Delete(),
    ];

    private static IEnumerable<Scenario> EnvironmentTargeting()
    {
        yield return Builder.Make("MA-01", "env list", Auth, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            context.Assert.ExitZero(await context.Msagent.RunAsync(["env", "list", "--non-interactive"]).ConfigureAwait(false));
        });

        yield return Builder.Make("MA-02", "env list --json", Auth, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(["env", "list", "--json", "--non-interactive"]).ConfigureAwait(false);
            context.Assert.ExitZero(result);

            var listed = result.Json<EnvironmentListResult>();
            context.Assert.True(listed.Environments.Count > 0, "at least one environment is listed");
            context.Assert.True(listed.Environments.All(environment => !string.IsNullOrWhiteSpace(environment.EnvironmentId)), "every row carries an id");
        });

        yield return Builder.Make("MA-03", "env select a valid environment", Dev, SafetyTier.Reversible, run: async context =>
        {
            var previous = await ReadSelectedEnvironmentAsync(context).ConfigureAwait(false);
            context.Ledger.RecordConfigEdit("env-select", previous, context.ScenarioId);

            context.Assert.ExitZero(await McsSteps.SelectEnvironmentAsync(context, context.DevEnvironmentId, "--json").ConfigureAwait(false));
            await RestoreEnvironmentAsync(context, previous).ConfigureAwait(false);
        });

        yield return Builder.Make("MA-04", "env select a malformed id", Auth, SafetyTier.ReadOnly, run: async context =>
        {
            context.Assert.ExitCode(
                await McsSteps.SelectEnvironmentAsync(context, "not-a-guid").ConfigureAwait(false),
                2,
                "a malformed id is a usage error");
        });

        yield return Builder.Make("MA-05", "env select persists for later commands", Dev, SafetyTier.Reversible, run: async context =>
        {
            var previous = await ReadSelectedEnvironmentAsync(context).ConfigureAwait(false);
            context.Ledger.RecordConfigEdit("env-select", previous, context.ScenarioId);

            context.Assert.ExitZero(
                await McsSteps.SelectEnvironmentAsync(context, context.DevEnvironmentId).ConfigureAwait(false),
                "the selection is made");

            context.Assert.ExitZero(
                await context.Msagent.RunAsync(["agent", "list", "--json", "--non-interactive"]).ConfigureAwait(false),
                "a later command targets the selection with no --environment-id");

            await RestoreEnvironmentAsync(context, previous).ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> LocalRecords()
    {
        yield return Builder.Make("MA-06", "agent init on a CLI workspace", WorkspaceCli, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            var result = await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.FileExists(workspace.ConfigPath, "agent.config.json is written");
            context.Assert.FileContains(workspace.ConfigPath, "schemaVersion", "the config carries the schema marker");
            context.Assert.Equal("MCSAgent", result.Json<ManagedAgentInitResult>().AgentType, "the record is an MCSAgent");
        });

        yield return Builder.Make("MA-07", "agent init on a classic workspace", WorkspaceClassic, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.ClassicAgent(context.ScenarioId);
            context.Assert.ExitZero(await McsSteps.InitAsync(context, workspace).ConfigureAwait(false));
            context.Assert.FileExists(workspace.ConfigPath, "agent.config.json is written");
        });

        yield return Builder.Make("MA-08", "agent init where no workspace exists", [], SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            context.Assert.NonZero(
                await context.Msagent.RunAsync(["agent", "init", "--agent-name", "nothing-here", "--agent-type", "MCSAgent", "--non-interactive"], directory).ConfigureAwait(false),
                "an empty directory cannot be initialised as an MCS agent");
        });

        yield return Builder.Make("MA-09", "agent show with no selector lists every record", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(["agent", "show", "--json", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);
            context.Assert.ExitZero(result);
            context.Assert.Contains(result, workspace.AgentName, "the initialised agent is listed");
        });

        yield return Builder.Make("MA-10", "agent show by id and by name agree", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            var initialized = (await McsSteps.InitAsync(context, workspace).ConfigureAwait(false)).Json<ManagedAgentInitResult>();

            var byId = await context.Msagent.RunAsync(["agent", "show", "--agent-id", initialized.AgentId, "--json", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);
            var byName = await context.Msagent.RunAsync(["agent", "show", "--agent-name", workspace.AgentName, "--json", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitZero(byId, "--agent-id resolves");
            context.Assert.ExitZero(byName, "--agent-name resolves");
            context.Assert.Equal(byId.StdOut.Trim(), byName.StdOut.Trim(), "both selectors return the same record");
        });

        yield return Builder.Make("MA-11", "agent show names an absent agent", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(["agent", "show", "--agent-name", "no-such-agent", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);
            context.Assert.NonZero(result);
            context.Assert.Contains(result, "no-such-agent", "the message names what was sought");
        });
    }

    private static IEnumerable<Scenario> Scaffolding()
    {
        yield return Builder.Make("MA-12", "agent create scaffolds a CLI-shaped agent", Dev, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);
            var result = await McsSteps.ScaffoldAsync(context, directory, $"ahe2e scaffold cli {context.RunSuffix}", "CLI").ConfigureAwait(false);

            context.Assert.ExitZero(result);

            var created = result.Json<ManagedAgentCreateResult>();
            context.Assert.Equal("MCSAgent", created.AgentType, "the record is an MCSAgent");
            context.Assert.True(!string.IsNullOrWhiteSpace(created.SchemaName), "a schema name is derived from the display name");
            context.Assert.FileExists(created.ConfigPath, "the local record is written");
            context.Assert.True(Directory.EnumerateFileSystemEntries(created.ProjectDirectory).Any(), "the project is scaffolded on disk");
        });

        yield return Builder.Make("MA-13", "agent create scaffolds a Classic-shaped agent", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);
            var result = await McsSteps.ScaffoldAsync(context, directory, $"ahe2e scaffold classic {context.RunSuffix}", "Classic").ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.True(
                Directory.EnumerateFileSystemEntries(result.Json<ManagedAgentCreateResult>().ProjectDirectory).Any(),
                "the project is scaffolded on disk");
        });

        yield return Builder.Make("MA-14", "agent create honours an explicit --schema-name", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);
            var schemaName = $"ahe2e_explicit{context.RunSuffix}";

            var result = await McsSteps.ScaffoldAsync(context, directory, $"ahe2e explicit {context.RunSuffix}", "CLI", "--schema-name", schemaName).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.Equal(schemaName, result.Json<ManagedAgentCreateResult>().SchemaName, "the supplied schema name is used verbatim");
        });

        yield return Builder.Make("MA-15", "agent create rejects --init-template on a CLI agent", [], SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSAgent", "--mcs-agent-type", "CLI", "--init-template", "default",
                 "--agent-name", "ahe2e template", "--initial-prompt", "Help.", "--description", "d", "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "--init-template is Classic-only");
            context.Assert.Contains(result, "--init-template", "the message names the offending option");
        });

        yield return Builder.Make("MA-16", "agent create requires --initial-prompt", [], SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSAgent", "--agent-name", "ahe2e no prompt", "--description", "d", "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a new MCS agent needs instructions");
            context.Assert.Contains(result, "--initial-prompt", "the message names --initial-prompt");
        });

        yield return Builder.Make("MA-17", "agent create requires --description", [], SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSAgent", "--agent-name", "ahe2e no description", "--initial-prompt", "Help.", "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a new MCS agent needs a description");
            context.Assert.Contains(result, "--description", "the message names --description");
        });

        yield return Builder.Make("MA-18", "agent create rejects an unknown --mcs-agent-type", [], SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSAgent", "--mcs-agent-type", "Hybrid",
                 "--agent-name", "ahe2e bad shape", "--initial-prompt", "Help.", "--description", "d", "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "only CLI and Classic are scaffold shapes");
        });
    }

    private static IEnumerable<Scenario> SessionAgentDeploys()
    {
        yield return Builder.Make("MA-19", "deploy a scaffolded CLI agent", Dev, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            context.Assert.Equal("reattach", session.Deployed.Action, "a first deploy reattaches");
            context.Assert.True(session.Deployed.TargetAgentCreated, "the cloud agent is created");
            context.Assert.True(await context.Cloud.AgentExistsAsync(session.EnvironmentId, session.CloudAgentId).ConfigureAwait(false), "the agent is listed in the cloud");
        });

        yield return Builder.Make("MA-21", "deploy the CLI fixture", DevCli, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);

            context.Assert.Equal("reattach", session.Deployed.Action, "a first deploy reattaches");
            context.Assert.True(session.Deployed.PushedComponentCount > 0, "components are pushed");
        });

        yield return Builder.Make("MA-22", "deploy the classic fixture, which declares connection references", DevClassic, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            context.Assert.True(session.Deployed.TargetAgentCreated, "the cloud agent is created");
            context.Assert.True(session.Deployed.PushedComponentCount > 0, "declared connection references do not block the push");
        });
    }

    private static IEnumerable<Scenario> CloudInventory()
    {
        yield return Builder.Make("MA-23", "agent list --environment-id", Dev, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            context.Assert.ExitZero(await context.Msagent.RunAsync(["agent", "list", "--environment-id", context.DevEnvironmentId, "--non-interactive"]).ConfigureAwait(false));
        });

        yield return Builder.Make("MA-24", "agent list --limit 5 --json", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(["agent", "list", "--environment-id", context.DevEnvironmentId, "--limit", "5", "--json", "--non-interactive"]).ConfigureAwait(false);
            context.Assert.ExitZero(result);

            var listed = result.Json<ManagedAgentListResult>();
            context.Assert.True(listed.Agents.Count <= 5, "at most five rows come back");
            context.Assert.True(listed.Agents.All(agent => !string.IsNullOrWhiteSpace(agent.SchemaName)), "every row carries a schema name");
        });

        yield return Builder.Make("MA-25", "agent list tenant-wide", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            context.Assert.ExitZero(await context.Msagent.RunAsync(["agent", "list", "--tenant-wide", "--non-interactive"], timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false));
        });

        yield return Builder.Make("MA-26", "agent create --mcs-clone-agent-id clones without mutating", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var before = await CountAgentsAsync(context).ConfigureAwait(false);
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSAgent", "--mcs-clone-agent-id", session.CloudAgentId, "--mcs-clone-environment-id", session.EnvironmentId, "--json", "--non-interactive"],
                directory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.True(Directory.EnumerateFileSystemEntries(directory).Any(), "components land on disk");
            context.Assert.Equal(before.ToString(), (await CountAgentsAsync(context).ConfigureAwait(false)).ToString(), "cloning creates nothing in the cloud");
        });

        yield return Builder.Make("MA-27", "agent create rejects an absent clone source", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            context.Assert.NonZero(await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSAgent", "--mcs-clone-agent-id", Guid.NewGuid().ToString(), "--mcs-clone-environment-id", context.DevEnvironmentId, "--non-interactive"],
                directory).ConfigureAwait(false));
        });

        yield return Builder.Make("MA-28", "agent pull on an up-to-date workspace", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            context.Assert.ExitZero(await context.Msagent.RunAsync(
                ["agent", "pull", "--agent-name", session.Workspace.AgentName, "--json", "--non-interactive"],
                session.Workspace.ProjectDirectory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false));
        });

        yield return Builder.Make("MA-29", "agent deploy re-run reports a push", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var again = await McsSteps.DeployAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            context.Assert.Equal("push", again.Action, "a repeat deploy pushes rather than reattaches");
            context.Assert.False(again.TargetAgentCreated, "no second cloud agent is created");
        });

        yield return Builder.Make("MA-30", "agent deploy names a missing deployment slot", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            var result = await McsSteps.RawDeployAsync(context, workspace, "no-such-slot").ConfigureAwait(false);
            context.Assert.ExitCode(result, 2, "a missing slot is a usage error");
            context.Assert.Contains(result, "no-such-slot", "the message names the missing slot");
        });
    }

    private static IEnumerable<Scenario> AuthoringRoundTrip()
    {
        yield return Builder.Make("MA-31", "multi-edit round-trip, CLI shape", DevCli, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var workspace = session.Workspace;
            var marker = $"ahe2e {context.RunSuffix} {context.ScenarioId}";

            File.AppendAllText(workspace.File("behaviors", "get-us-temperature", "text.md"), $"{Environment.NewLine}{marker} text{Environment.NewLine}");
            File.AppendAllText(workspace.File("behaviors", "get-us-temperature", "SKILL.md"), $"{Environment.NewLine}{marker} skill{Environment.NewLine}");

            var deployed = await McsSteps.DeployAsync(context, workspace, session.DeploymentName).ConfigureAwait(false);
            context.Assert.Equal("push", deployed.Action, "the edit is pushed");
            context.Assert.True(deployed.PushedComponentCount + deployed.UploadedFileCount + deployed.AuxiliaryChangeCount > 0, "something is genuinely sent");

            var clone = await McsSteps.CloneAsync(context, deployed, "clone").ConfigureAwait(false);
            AssertClonedFileContains(context, clone, "text.md", $"{marker} text", "text.md round-trips");
            AssertClonedFileContains(context, clone, "SKILL.md", $"{marker} skill", "SKILL.md round-trips");
        });

        yield return Builder.Make("MA-32", "multi-edit round-trip, classic shape", DevClassic, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);
            var workspace = session.Workspace;
            var marker = $"ahe2e {context.RunSuffix} {context.ScenarioId}";

            var knowledgeDirectory = workspace.File("knowledge", "files");
            File.WriteAllText(Path.Combine(knowledgeDirectory, "File 9.txt"), $"{marker} a new knowledge file");

            var existing = Directory.EnumerateFiles(knowledgeDirectory, "File 3.txt").FirstOrDefault();

            if (existing is not null)
            {
                File.WriteAllText(existing, $"{marker} modified knowledge contents");
            }

            var promptFile = Directory.EnumerateFiles(workspace.File("prompts"), "prompt.json", SearchOption.AllDirectories).FirstOrDefault();

            if (promptFile is not null)
            {
                UpdatePromptInstruction(promptFile, $"Version msagent {context.RunSuffix}: summarize the document.");
            }

            var deployed = await McsSteps.DeployAsync(context, workspace, session.DeploymentName).ConfigureAwait(false);
            context.Assert.True(deployed.UploadedFileCount > 0, "knowledge files are uploaded");
            context.Assert.FileExists(Path.Combine(knowledgeDirectory, "File9.txt.mcs.yml"), "the CLI generates the companion metadata");

            var clone = await McsSteps.CloneAsync(context, deployed, "clone").ConfigureAwait(false);
            AssertClonedFileContains(context, clone, "File 9.txt", marker, "the new knowledge file round-trips");

            if (existing is not null)
            {
                AssertClonedFileContains(context, clone, "File 3.txt", "modified knowledge contents", "the modified knowledge file round-trips");
            }

            if (promptFile is not null)
            {
                AssertClonedFileContains(context, clone, "prompt.json", $"Version msagent {context.RunSuffix}:", "the prompt instruction round-trips");
            }
        });

        yield return Builder.Make("MA-33", "agent pull brings a remote change into a stale workspace", DevCli, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var stale = await SecondCopyAsync(context, session).ConfigureAwait(false);

            var marker = $"ahe2e {context.RunSuffix} remote";
            File.AppendAllText(session.Workspace.File("behaviors", "get-us-temperature", "text.md"), $"{Environment.NewLine}{marker}{Environment.NewLine}");
            await McsSteps.DeployAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            var pull = await context.Msagent.RunAsync(
                ["agent", "pull", "--agent-name", stale.AgentName, "--json", "--non-interactive"],
                stale.ProjectDirectory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(pull, "pull succeeds");
            context.Assert.FileContains(stale.File("behaviors", "get-us-temperature", "text.md"), marker, "the stale copy now carries the remote edit");
        });
    }

    private static IEnumerable<Scenario> ConflictAndRecovery()
    {
        yield return Builder.Make("MA-34", "handling conflict in mcs agent", DevClassic, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var (_, copyB) = await StageConflictAsync(context).ConfigureAwait(false);

            var result = await McsSteps.RawDeployAsync(context, copyB, McsSteps.SessionDeploymentName).ConfigureAwait(false);
            context.Assert.NonZero(result, "the conflicting deploy is refused");
            context.Assert.Contains(result, "pull", "the remediation names a pull");
        });

        yield return Builder.Make("MA-35", "pull, resolve, deploy — the documented recovery", DevClassic, SafetyTier.Creating, run: async context =>
        {
            var (_, copyB) = await StageConflictAsync(context).ConfigureAwait(false);
            await McsSteps.RawDeployAsync(context, copyB, McsSteps.SessionDeploymentName).ConfigureAwait(false);

            var pull = await context.Msagent.RunAsync(
                ["agent", "pull", "--agent-name", copyB.AgentName, "--json", "--non-interactive"],
                copyB.ProjectDirectory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(pull, "pull resolves the workspace");
            SetGoodbyePrompt(copyB, ResolvedPrompt(context));

            var deployed = await McsSteps.DeployAsync(context, copyB, McsSteps.SessionDeploymentName).ConfigureAwait(false);
            context.Assert.Equal("push", deployed.Action, "the resolved workspace deploys");
        });

        yield return Builder.Make("MA-36", "--overwrite replaces cloud content", DevClassic, SafetyTier.Creating, run: async context =>
        {
            var (_, copyB) = await StageConflictAsync(context).ConfigureAwait(false);

            var result = await McsSteps.RawDeployAsync(context, copyB, McsSteps.SessionDeploymentName, "--overwrite").ConfigureAwait(false);
            context.Assert.ExitZero(result, "--overwrite deploys without a pull");

            var deployed = result.Json<ManagedAgentDeployResult>();
            McsSteps.RecordDeployed(context, copyB, McsSteps.SessionDeploymentName, deployed);

            var clone = await McsSteps.CloneAsync(context, deployed, "clone").ConfigureAwait(false);
            AssertClonedFileContains(context, clone, "Goodbye.mcs.yml", LocalPrompt(context), "the local version wins");
        });
    }

    private static IEnumerable<Scenario> DeploymentSlots()
    {
        yield return Builder.Make("MA-37", "deployment create persists a slot", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            var result = await McsSteps.CreateSlotAsync(context, workspace, "e2e", context.DevEnvironmentId).ConfigureAwait(false);
            context.Assert.ExitZero(result);
            context.Assert.Equal("e2e", result.Json<AgentDeploymentCreateResult>().DeploymentName, "the slot is named");
            context.Assert.FileContains(workspace.ConfigPath, "e2e", "the slot persists locally");
        });

        yield return Builder.Make("MA-38", "deployment create rejects a duplicate name", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);
            await McsSteps.CreateSlotAsync(context, workspace, "e2e", context.DevEnvironmentId).ConfigureAwait(false);

            context.Assert.NonZero(
                await McsSteps.CreateSlotAsync(context, workspace, "e2e", context.DevEnvironmentId).ConfigureAwait(false),
                "a duplicate slot name is refused");
        });

        yield return Builder.Make("MA-39", "deployment create refuses two slots in one environment", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);
            await McsSteps.CreateSlotAsync(context, workspace, "first", context.DevEnvironmentId).ConfigureAwait(false);

            context.Assert.NonZero(
                await McsSteps.CreateSlotAsync(context, workspace, "second", context.DevEnvironmentId).ConfigureAwait(false),
                "one deployment per environment");
        });

        yield return Builder.Make("MA-40", "deployment create rejects a malformed environment id", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            context.Assert.ExitCode(
                await McsSteps.CreateSlotAsync(context, workspace, "e2e", "not-a-guid").ConfigureAwait(false),
                2,
                "a malformed environment id is a usage error");
        });

        yield return Builder.Make("MA-41", "deployment list --agent-id", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var agentId = await LocalAgentIdAsync(context, session).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(["deployment", "list", "--agent-id", agentId, "--json", "--non-interactive"], session.Workspace.ProjectDirectory).ConfigureAwait(false);
            context.Assert.ExitZero(result);
            context.Assert.Contains(result, session.DeploymentName, "the slot is listed");
        });

        yield return Builder.Make("MA-42", "deployment list --agent-name", Dev, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var agentId = await LocalAgentIdAsync(context, session).ConfigureAwait(false);

            var byId = await context.Msagent.RunAsync(["deployment", "list", "--agent-id", agentId, "--json", "--non-interactive"], session.Workspace.ProjectDirectory).ConfigureAwait(false);
            var byName = await context.Msagent.RunAsync(["deployment", "list", "--agent-name", session.Workspace.AgentName, "--json", "--non-interactive"], session.Workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitZero(byName, "--agent-name is accepted");
            context.Assert.Equal(byId.StdOut.Trim(), byName.StdOut.Trim(), "both selectors return the same list");
        });

        yield return Builder.Make("MA-43", "deployment list with neither selector lists every agent", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);
            await McsSteps.CreateSlotAsync(context, workspace, "e2e", context.DevEnvironmentId).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(["deployment", "list", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitZero(result, "an omitted selector lists every agent rather than failing");
            context.Assert.Contains(result, "e2e", "the slot is listed");
            context.Assert.Contains(result, "AGENT ID", "the unscoped listing names the owning agent");
        });

        yield return Builder.Make("MA-44", "deployment show by name and by id agree", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            var byName = await context.Msagent.RunAsync(["deployment", "show", "--deployment-name", session.DeploymentName, "--json", "--non-interactive"], session.Workspace.ProjectDirectory).ConfigureAwait(false);
            context.Assert.ExitZero(byName, "--deployment-name resolves");

            var shown = byName.Json<AgentDeploymentShowResult>();
            var byId = await context.Msagent.RunAsync(["deployment", "show", "--deployment-id", shown.DeploymentId, "--json", "--non-interactive"], session.Workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitZero(byId, "--deployment-id resolves");
            context.Assert.Equal(byName.StdOut.Trim(), byId.StdOut.Trim(), "both selectors return the same record");
        });

        yield return Builder.Make("MA-45", "deployment show --agent-name", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            context.Assert.ExitZero(await context.Msagent.RunAsync(
                ["deployment", "show", "--deployment-name", session.DeploymentName, "--agent-name", session.Workspace.AgentName, "--json", "--non-interactive"],
                session.Workspace.ProjectDirectory).ConfigureAwait(false),
                "the --agent-name binding is present");
        });

        yield return Builder.Make("MA-46", "deployment connection list reports readiness without changing anything", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var result = await ShowConnectionAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.Equal("connections-previewed", result.Json<AgentDeploymentConnectionResult>().Status, "the read path reports its own status");
        });

        yield return Builder.Make("MA-47", "deployment connection list never opens a browser", DevClassic, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            context.Assert.ExitZero(
                await ShowConnectionAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false),
                "the read path completes without waiting on consent");
        });

        yield return Builder.Make("MA-62", "show connection is repeatable and changes nothing", DevClassic, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            var first = await ShowConnectionAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);
            var second = await ShowConnectionAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            context.Assert.ExitZero(first);
            context.Assert.ExitZero(second);
            context.Assert.True(
                first.Json<AgentDeploymentConnectionResult>().Connections.Count == second.Json<AgentDeploymentConnectionResult>().Connections.Count,
                "a read-only command reports the same references twice running");
        });

        yield return Builder.Make("MA-63", "show connection rejects an unknown connection reference locally", DevClassic, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            context.Assert.NonZero(
                await context.Msagent.RunAsync(
                    ["deployment", "connection", "update", "--deployment-name", session.DeploymentName, "--connection-reference", "cr_does_not_exist", "--non-interactive"],
                    session.Workspace.ProjectDirectory).ConfigureAwait(false),
                "a reference the workspace does not declare is rejected");
        });

        yield return Builder.Make("MA-64", "update connection never activates a workflow whose references are unbound", DevClassic, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            var updated = await UpdateConnectionAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            context.Assert.ExitZero(updated, "update connection reports readiness");

            var result = updated.Json<AgentDeploymentConnectionResult>();
            var unbound = result.Connections
                .Where(connection => connection.State is not ("bound" or "already-bound" or "created"))
                .Select(connection => connection.ConnectionReference)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var blocked = result.Workflows
                .Where(workflow => workflow.ConnectionReferences.Any(unbound.Contains))
                .ToArray();

            if (blocked.Length == 0)
            {
                throw new ScenarioBlockedException(
                    "every workflow's connection references are already bound in this environment, so the guard has nothing to exercise",
                    "no workflow with an unbound connection reference");
            }

            context.Assert.True(
                blocked.All(workflow => !result.ActivatedWorkflows.Contains(workflow.DisplayName, StringComparer.OrdinalIgnoreCase)),
                "a workflow whose references are unbound is not reported as activated");

            context.Assert.True(
                !result.RefusedWorkflows.Any(refusal => refusal.Contains("0x80060467", StringComparison.Ordinal)),
                "the service was never asked to start a workflow whose connection references are unbound");
        });

        yield return Builder.Make("MA-49", "update connection when the agent is absent from the cloud", DevCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);
            await McsSteps.CreateSlotAsync(context, workspace, "e2e", context.DevEnvironmentId).ConfigureAwait(false);

            context.Assert.NonZero(
                await UpdateConnectionAsync(context, workspace, "e2e").ConfigureAwait(false),
                "an absent cloud agent is reported");
        });

        yield return Builder.Make("MA-50", "update connection with neither deployment selector", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            context.Assert.ExitCode(
                await context.Msagent.RunAsync(["deployment", "connection", "update", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false),
                2,
                "a missing selector is a usage error");
        });

        yield return Builder.Make("MA-51", "update connection rejects parent-only options", WorkspaceCli, SafetyTier.ReadOnly, run: async context =>
        {
            var workspace = context.Workspaces.CliAgent(context.ScenarioId);
            await McsSteps.InitAsync(context, workspace).ConfigureAwait(false);

            var trailing = await context.Msagent.RunAsync(["deployment", "connection", "update", "--deployment-name", "e2e", "--sku", "F0", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);
            var leading = await context.Msagent.RunAsync(["deployment", "update", "--sku", "F0", "connection", "--deployment-name", "e2e", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.NonZero(trailing, "--sku after the subcommand is rejected");
            context.Assert.NonZero(leading, "--sku before the subcommand is rejected");
        });
    }

    private static IEnumerable<Scenario> PromotionToTest()
    {
        yield return Builder.Make("MA-52", "promote dev to test", Promotion, SafetyTier.Creating, run: async context =>
        {
            var promoted = await PromoteAsync(context).ConfigureAwait(false);

            context.Assert.Equal("retarget", promoted.Action, "promotion is a retarget");
            context.Assert.Equal(context.TestEnvironmentId, promoted.TargetEnvironmentId, "the deploy targets the test environment");
            context.Assert.True(await context.Cloud.AgentExistsAsync(promoted.TargetEnvironmentId, promoted.TargetMcsAgentId!).ConfigureAwait(false), "the test agent exists");
        });

        yield return Builder.Make("MA-53", "the dev agent survives promotion", Promotion, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, PromotionSource).ConfigureAwait(false);
            await PromoteAsync(context).ConfigureAwait(false);

            context.Assert.True(
                await context.Cloud.AgentExistsAsync(session.EnvironmentId, session.CloudAgentId).ConfigureAwait(false),
                "the dev agent is left intact");
        });

        yield return Builder.Make("MA-54", "promote back to dev", Promotion, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, PromotionSource).ConfigureAwait(false);
            var promoted = await PromoteAsync(context).ConfigureAwait(false);
            var back = await McsSteps.DeployAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            context.Assert.Equal("retarget", back.Action, "promoting back is also a retarget");
            context.Assert.True(
                await context.Cloud.AgentExistsAsync(promoted.TargetEnvironmentId, promoted.TargetMcsAgentId!).ConfigureAwait(false),
                "the test agent is left intact");
        });

        yield return Builder.Make("MA-55", "content promoted is the content deployed", Promotion, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, PromotionSource).ConfigureAwait(false);
            var marker = $"ahe2e {context.RunSuffix} promoted";

            File.AppendAllText(session.Workspace.File("behaviors", "get-us-temperature", "text.md"), $"{Environment.NewLine}{marker}{Environment.NewLine}");
            await McsSteps.DeployAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            var promoted = await PromoteAsync(context).ConfigureAwait(false);
            var clone = await McsSteps.CloneAsync(context, promoted, "clone").ConfigureAwait(false);

            AssertClonedFileContains(context, clone, "text.md", marker, "the clone from test carries the edit");
            await McsSteps.DeployAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Promotion uses the fixture agent rather than a scaffolded one: a scaffold produces only
    /// <c>settings.mcs.yml</c> and <c>agent.sync.yaml</c>, neither of which carries free text, so there
    /// would be nothing to edit to prove that content — not merely a binding — moved between
    /// environments.
    /// </summary>
    private const SessionAgentKind PromotionSource = SessionAgentKind.FixtureCli;

    private static IEnumerable<Scenario> Delete()
    {
        yield return Builder.Make("MA-56", "agent delete with a mismatched confirmation deletes nothing", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            var delete = await McsSteps.DeleteAgentAsync(context, session.Workspace, session.DeploymentName, confirmation: "definitely-not-the-name").ConfigureAwait(false);

            context.Assert.ExitZero(delete, "declining is not an error");
            context.Assert.Contains(delete, "name-mismatch", "the result says why nothing happened");
            context.Assert.True(await context.Cloud.AgentExistsAsync(session.EnvironmentId, session.CloudAgentId).ConfigureAwait(false), "nothing is deleted");
        });

        yield return Builder.Make("MA-57", "deployment delete with a mismatched token deletes nothing", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            var delete = await McsSteps.DeleteDeploymentAsync(context, session.Workspace, session.DeploymentName, deleteCloudAgent: true, confirmation: "wrong-token").ConfigureAwait(false);

            context.Assert.ExitZero(delete, "declining is not an error");
            context.Assert.Contains(delete, "name-mismatch", "the result says why nothing happened");
            context.Assert.True(await context.Cloud.AgentExistsAsync(session.EnvironmentId, session.CloudAgentId).ConfigureAwait(false), "nothing is deleted");
        });

        yield return Builder.Make("MA-58", "deployment delete removes the record and spares the agent", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            context.Assert.ExitZero(
                await McsSteps.DeleteDeploymentAsync(context, session.Workspace, session.DeploymentName, deleteCloudAgent: false).ConfigureAwait(false),
                "the record is removed");

            context.Assert.True(await context.Cloud.AgentExistsAsync(session.EnvironmentId, session.CloudAgentId).ConfigureAwait(false), "the cloud agent survives");
            await McsSteps.CreateSlotAsync(context, session.Workspace, session.DeploymentName, context.DevEnvironmentId).ConfigureAwait(false);
        });

        yield return Builder.Make("MA-59", "agent delete by name, confirmed", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedClassic).ConfigureAwait(false);

            context.Assert.ExitZero(await McsSteps.DeleteAgentAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false));
            await AssertGoneAsync(context, session).ConfigureAwait(false);
        });

        yield return Builder.Make("MA-60", "deployment delete --delete-deployed-mcs-agent removes both", DevCli, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);

            context.Assert.ExitZero(await McsSteps.DeleteDeploymentAsync(context, session.Workspace, session.DeploymentName, deleteCloudAgent: true).ConfigureAwait(false));
            await AssertGoneAsync(context, session).ConfigureAwait(false);
        });

        yield return Builder.Make("MA-61", "delete immediately after a push", DevClassic, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            var pull = await context.Msagent.RunAsync(
                ["agent", "pull", "--agent-name", session.Workspace.AgentName, "--json", "--non-interactive"],
                session.Workspace.ProjectDirectory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(pull, "the shared agent is synced before the push");
            await McsSteps.DeployAsync(context, session.Workspace, session.DeploymentName).ConfigureAwait(false);

            context.Assert.ExitZero(
                await McsSteps.DeleteDeploymentAsync(context, session.Workspace, session.DeploymentName, deleteCloudAgent: true).ConfigureAwait(false),
                "delete succeeds despite the dependency window a push opens");

            await AssertGoneAsync(context, session).ConfigureAwait(false);
        });
    }

    private static async Task AssertGoneAsync(ScenarioContext context, SessionAgent session)
    {
        await context.Assert.EventuallyAsync(
            () => context.Cloud.AgentGoneAsync(session.EnvironmentId, session.CloudAgentId),
            TimeSpan.FromMinutes(3),
            "the cloud agent disappears").ConfigureAwait(false);

        context.Ledger.MarkDeleted(session.CloudAgentId);
        context.Session.ConsumeAgent(session.Kind);
    }

    private static async Task<ManagedAgentDeployResult> PromoteAsync(ScenarioContext context)
    {
        var session = await McsSteps.SessionAsync(context, PromotionSource).ConfigureAwait(false);

        var existing = await context.Msagent.RunAsync(
            ["deployment", "show", "--deployment-name", TestDeploymentName, "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        if (existing.ExitCode != 0)
        {
            await McsSteps.GuardSchemaNameAsync(context, session.Workspace, context.TestEnvironmentId).ConfigureAwait(false);
            await McsSteps.CreateSlotAsync(context, session.Workspace, TestDeploymentName, context.TestEnvironmentId, "test").ConfigureAwait(false);
        }

        return await McsSteps.DeployAsync(context, session.Workspace, TestDeploymentName).ConfigureAwait(false);
    }
    
    private static async Task<Workspace> SecondCopyAsync(ScenarioContext context, SessionAgent session)
    {
        var copy = context.Workspaces.CopyOf(session.Workspace, context.ScenarioId);
        await McsSteps.InitAsync(context, copy).ConfigureAwait(false);
        await McsSteps.CreateSlotAsync(context, copy, McsSteps.SessionDeploymentName, context.DevEnvironmentId).ConfigureAwait(false);
        await McsSteps.DeployAsync(context, copy, McsSteps.SessionDeploymentName, "--overwrite").ConfigureAwait(false);

        return copy;
    }

    /// <summary>
    /// Both copies deploy the current agent first. Without that, copy B's deploy is its own first
    /// deploy — it adopts the cloud agent and overwrites it, so no conflict is ever detected.
    ///
    /// <para>Copy A is pulled before staging because the classic session agent is shared: earlier
    /// scenarios have already pushed to it, so the pristine fixture text is not what the cloud holds
    /// and a push from a stale baseline is refused as a remote change.</para>
    /// </summary>
    private static async Task<(Workspace CopyA, Workspace CopyB)> StageConflictAsync(ScenarioContext context)
    {
        var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);
        var copyA = session.Workspace;

        var pull = await context.Msagent.RunAsync(
            ["agent", "pull", "--agent-name", copyA.AgentName, "--json", "--non-interactive"],
            copyA.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

        context.Assert.ExitZero(pull, "the shared agent is synced before staging");

        var copyB = await SecondCopyAsync(context, session).ConfigureAwait(false);

        SetGoodbyePrompt(copyA, CloudPrompt(context));
        await McsSteps.DeployAsync(context, copyA, session.DeploymentName).ConfigureAwait(false);

        SetGoodbyePrompt(copyB, LocalPrompt(context));

        return (copyA, copyB);
    }

    /// <summary>Rewrites the Goodbye topic's question prompt, whatever it currently says.</summary>
    private static void SetGoodbyePrompt(Workspace workspace, string prompt)
    {
        var goodbye = workspace.File("topics", "Goodbye.mcs.yml");

        if (!File.Exists(goodbye))
        {
            return;
        }

        var lines = File.ReadAllLines(goodbye);
        var index = Array.FindIndex(lines, line => line.TrimStart().StartsWith("prompt:", StringComparison.Ordinal));

        if (index < 0)
        {
            throw new E2EException($"{goodbye} has no prompt line to edit, so the conflict cannot be staged.");
        }

        var indent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
        lines[index] = $"{indent}prompt: {prompt}";
        File.WriteAllLines(goodbye, lines);
    }

    private static Task<MsagentResult> UpdateConnectionAsync(ScenarioContext context, Workspace workspace, string deploymentName) =>
        context.Msagent.RunAsync(
            ["deployment", "connection", "update", "--deployment-name", deploymentName, "--json", "--non-interactive"],
            workspace.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(5));

    private static Task<MsagentResult> ShowConnectionAsync(ScenarioContext context, Workspace workspace, string deploymentName) =>
        context.Msagent.RunAsync(
            ["deployment", "connection", "list", "--deployment-name", deploymentName, "--json", "--non-interactive"],
            workspace.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(5));

    private static async Task<string> LocalAgentIdAsync(ScenarioContext context, SessionAgent session)
    {
        var result = await context.Msagent.RunAsync(["agent", "show", "--json", "--non-interactive"], session.Workspace.ProjectDirectory).ConfigureAwait(false);
        context.Assert.ExitZero(result, "agent show");

        var agents = result.Json<ManagedAgentShowResult>().Agents;

        return (agents.Count > 0 ? agents[0].AgentId : null)
            ?? throw new E2EException($"{context.ScenarioId} found no local agent record to address.");
    }

    private static async Task<int> CountAgentsAsync(ScenarioContext context)
    {
        var result = await context.Msagent.RunAsync(
            ["agent", "list", "--environment-id", context.DevEnvironmentId, "--json", "--non-interactive"],
            timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent list");
        return result.Json<ManagedAgentListResult>().Agents.Count;
    }

    private static void AssertClonedFileContains(ScenarioContext context, string cloneDirectory, string fileName, string fragment, string check)
    {
        var path = McsSteps.FindFile(cloneDirectory, fileName)
            ?? throw new AssertionException(check, $"{fileName} under the clone", "it is not there", null, null);

        context.Assert.FileContains(path, fragment, check);
    }

    private static void UpdatePromptInstruction(string promptFile, string instruction)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(promptFile))
            ?? throw new E2EException($"{promptFile} is not valid JSON.");

        document["instruction"] = instruction;
        File.WriteAllText(promptFile, document.ToJsonString(ConfigJson.Options));
    }

    private static async Task<string?> ReadSelectedEnvironmentAsync(ScenarioContext context)
    {
        var result = await context.Msagent.RunAsync(["env", "list", "--json", "--non-interactive"]).ConfigureAwait(false);

        return result.ExitCode != 0
            ? null
            : result.Json<EnvironmentListResult>().Environments.FirstOrDefault(environment => environment.IsActive)?.EnvironmentId;
    }

    private static async Task RestoreEnvironmentAsync(ScenarioContext context, string? previous)
    {
        if (previous is { Length: > 0 })
        {
            await McsSteps.SelectEnvironmentAsync(context, previous).ConfigureAwait(false);
            context.Ledger.MarkRestored("env-select");
        }
    }

    private static string CloudPrompt(ScenarioContext context) => $"ahmcs cloud {context.RunSuffix} - {BasePrompt}";

    private static string LocalPrompt(ScenarioContext context) => $"ahmcs local {context.RunSuffix} - {BasePrompt}";

    private static string ResolvedPrompt(ScenarioContext context) => $"ahmcs resolved {context.RunSuffix} - {BasePrompt}";
}
