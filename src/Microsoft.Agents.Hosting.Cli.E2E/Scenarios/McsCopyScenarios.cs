using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Copy;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

public static partial class McsCopyScenarios
{
    private const string RefusedName = "ahe2e refused copy";
    private const string PlaceholderCloudId = "00000000-0000-0000-0000-000000000001";
    private const string DocsFolder = "Microsoft.Agents.Hosting.Cli.Docs";
    private const string AgentA1Folder = "Agent A1 This is long name as possible 123";
    private const string LongNameFiller = "Weather Assistant For The Whole Pacific Northwest With Extended Forecasts And Severe Alerts";
    private const int ConnectorDisplayNameLimit = 30;
    private const string AgentNamePlaceholder = "{agent-name}";

    private static readonly ScenarioBuilder Builder = new("mcs-copy");
    private static readonly Capability[] Signed = [Capability.Auth];
    private static readonly Capability[] Cli = [Capability.Auth, Capability.McsDev, Capability.WorkspaceCli];
    private static readonly Capability[] CliAndWorkflow = [Capability.Auth, Capability.McsDev, Capability.WorkspaceCli, Capability.WorkspaceWorkflow];
    private static readonly Capability[] Classic = [Capability.Auth, Capability.McsDev, Capability.WorkspaceClassic];
    private static readonly Capability[] ClassicAndWorkflow = [Capability.Auth, Capability.McsDev, Capability.WorkspaceClassic, Capability.WorkspaceWorkflow];
    private static readonly Capability[] Both = [Capability.Auth, Capability.McsDev, Capability.WorkspaceCli, Capability.WorkspaceClassic];
    private static readonly Capability[] BothAndWorkflow = [Capability.Auth, Capability.McsDev, Capability.WorkspaceCli, Capability.WorkspaceClassic, Capability.WorkspaceWorkflow];
    private static readonly Capability[] FromCorpus = [Capability.Auth, Capability.McsDev, Capability.CopyCorpus];
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(45);
    private static readonly string[] GlobalOptions = ["--help", "--json", "--debug", "--no-color", "--non-interactive"];

    private static readonly HelpTopic[] HelpTopics =
    [
        new(["agent", "copy"], "Command_Agent.md", "## `msagent agent copy`"),
        new(["agent", "deploy"], "Command_Agent.md", "## `msagent agent deploy`"),
        new(["agent", "delete"], "Command_Agent.md", "## `msagent agent delete`"),
    ];

    private static readonly ArgumentRefusal[] ArgumentRefusals =
    [
        new("MC-01", "no --agent-name", [], "requires --agent-name"),
        new("MC-01", "a blank --agent-name", ["--agent-name", "   "], "requires --agent-name"),
        new("MC-02", "--dependencies shared", ["--dependencies", "shared", "--agent-name", RefusedName], "Unrecognized command or argument '--dependencies'"),
        new("MC-03", "the removed --new-name", ["--new-name", RefusedName, "--agent-name", RefusedName], "Unrecognized command or argument '--new-name'"),
        new("MC-03", "the removed --agent-id", ["--agent-id", "00000000-0000-0000-0000-000000000001", "--agent-name", RefusedName], "Unrecognized command or argument '--agent-id'"),
        new("MC-04", "--share-resources", ["--share-resources", "--agent-name", RefusedName], "Unrecognized command or argument '--share-resources'"),
        new("MC-06", "the removed --from-project", ["--from-project", ".", "--agent-name", RefusedName], "Unrecognized command or argument '--from-project'"),
        new("MC-07", $"a {AgentNameValidator.MaximumLength + 1}-character --agent-name", ["--mcs-clone-agent-id", PlaceholderCloudId, "--agent-name", new string('n', AgentNameValidator.MaximumLength + 1)], $"is {AgentNameValidator.MaximumLength + 1} characters long; Copilot Studio allows at most {AgentNameValidator.MaximumLength}"),
        new("MC-08", "a --schema-name with a space", ["--agent-name", RefusedName, "--schema-name", "bad schema"], "unsupported characters"),
        new("MC-08", $"a {SchemaNameValidator.MaximumLength + 1}-character --schema-name", ["--agent-name", RefusedName, "--schema-name", new string('s', SchemaNameValidator.MaximumLength + 1)], $"longer than {SchemaNameValidator.MaximumLength} characters"),
        new("MC-08", "a --schema-name for a workflow copy", ["--mcs-clone-workflow-id", PlaceholderCloudId, "--agent-name", RefusedName, "--schema-name", "ahe2e_refused"], "a workflow has none"),
        new("MC-10", "both --mcs-clone-agent-id and --mcs-clone-workflow-id", ["--mcs-clone-agent-id", PlaceholderCloudId, "--mcs-clone-workflow-id", PlaceholderCloudId, "--agent-name", RefusedName], "Pass one cloud source"),
    ];

    private static readonly CorpusFolder[] CorpusFolders =
    [
        new("MC-60", "MC-67a", "NB1", "the simplest CLI agent", ChecksPushedComponentCount: true),
        new("MC-61", "MC-67b", "N2 A9", "the smallest CLI agent, whose workflow references another agent", ChecksPushedComponentCount: true),
        new("MC-62", "MC-67c", "N2 A8", "a CLI agent with workflow hooks and knowledge files"),
        new("MC-63", "MC-67d", "NAgent A3", "the heaviest CLI agent, under a Default_ schema prefix"),
        new("MC-64", "MC-67e", AgentA1Folder, "a classic agent with child agents, prompts, and a custom connector"),
        new("MC-65", "MC-67f", "Agent D1", "a classic agent with prompts, a custom connector, and knowledge files"),
    ];

    private static readonly string[][] SweepCommands =
    [
        ["agent", "list"],
        ["agent", "show", "--agent-name", AgentNamePlaceholder],
        ["agent", "share", "show", "--agent-name", AgentNamePlaceholder],
        ["agent", "eval", "list"],
        ["deployment", "list", "--agent-name", AgentNamePlaceholder],
        ["deployment", "show", "--agent-name", AgentNamePlaceholder, "--deployment-name", CopySteps.DeploymentName],
        ["deployment", "connection", "list", "--agent-name", AgentNamePlaceholder, "--deployment-name", CopySteps.DeploymentName],
        ["deployment", "oauth", "list", "--agent-name", AgentNamePlaceholder, "--deployment-name", CopySteps.DeploymentName],
    ];

    private static readonly string[] NeverDeployedSweepCommand = ["agent", "pull", "--agent-name", AgentNamePlaceholder];

    public static IEnumerable<Scenario> All() => [.. Surface(), .. Preparation(), .. Lifecycle(), .. EdgeCases(), .. Corpus(), .. Deletion(), .. MessyOrders(), .. Concurrency()];

    private static IEnumerable<Scenario> Surface()
    {
        yield return Builder.Make("MC-01", "agent copy without an --agent-name is a usage error", Signed, SafetyTier.ReadOnly, smoke: true, run: context => RefuseAsync(context, json: true));

        yield return Builder.Make("MC-02", "the removed --dependencies option is refused", Signed, SafetyTier.ReadOnly, run: context => RefuseAsync(context, json: true));

        yield return Builder.Make("MC-03", "the removed --new-name and --agent-id options are refused", Signed, SafetyTier.ReadOnly, run: context => RefuseAsync(context, json: true));

        yield return Builder.Make("MC-04", "the removed --share-resources switch is refused, so no copy shares its source's resources", Signed, SafetyTier.ReadOnly, run: context => RefuseAsync(context, json: true));

        yield return Builder.Make("MC-05", "agent copy asks nothing", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", CopySteps.NewName(context)], stdin: string.Empty, interactive: true).ConfigureAwait(false);

            context.Assert.ExitZero(result, "a copy runs without asking anything");
            context.Assert.True(IsJson(result.StdOut), "stdout stays parseable JSON");
            context.Assert.Equal(CopilotCopySourceKinds.Local, CopySteps.RequireHome(context, result.Json<ManagedAgentCopyResult>()).Source, "without --mcs-clone-agent-id the local record is the source");
        });

        yield return Builder.Make("MC-06", "the removed --from-project option is refused", Signed, SafetyTier.ReadOnly, run: context => RefuseAsync(context, json: true));

        yield return Builder.Make("MC-07", $"--agent-name is accepted at {AgentNameValidator.MaximumLength} characters for an agent and {WorkflowNaming.MaximumLength} for a workflow, and refused past it", CliAndWorkflow, SafetyTier.ReadOnly, run: async context =>
        {
            await RefuseAsync(context, json: true).ConfigureAwait(false);

            var name = CopySteps.NewName(context, "longest").PadRight(AgentNameValidator.MaximumLength, 'x');
            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false)), "longest", name).ConfigureAwait(false);

            context.Assert.Equal(name, copy.DisplayName, "the whole name is kept");
            context.Assert.True(Directory.Exists(copy.WorkspaceDirectory), "the workspace exists where the result says");
            CopySteps.AssertRegistered(context, copy, deployed: null);

            var workflowSource = await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false);
            var overlongName = CopySteps.NewName(context, "workflow-overlong").PadRight(WorkflowNaming.MaximumLength + 1, 'x');

            RefusedAs(context, await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(workflowSource), "--agent-name", overlongName]).ConfigureAwait(false), "workflow-name-invalid", ErrorCodes.ManagedAgent.WorkflowNameInvalid, $"can hold at most {WorkflowNaming.MaximumLength} characters", $"the {WorkflowNaming.MaximumLength + 1}-character workflow copy {overlongName}");
            context.Assert.False(Directory.Exists(CopySteps.BesideSource(workflowSource, overlongName)), $"nothing was written for the workflow copy {overlongName}");

            var workflowName = CopySteps.NewName(context, "workflow-longest").PadRight(WorkflowNaming.MaximumLength, 'x');
            var workflowCopy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(workflowSource), "workflow-longest", workflowName).ConfigureAwait(false);

            context.Assert.Equal(workflowName, workflowCopy.DisplayName, $"the whole name of the workflow copy {workflowName} is kept");
            context.Assert.Equal(workflowName, workflowCopy.WorkflowName, $"the workflow copy {workflowName} names its flow so");
            context.Assert.True(Directory.Exists(workflowCopy.WorkspaceDirectory), $"the workspace of the workflow copy {workflowName} exists where the result says");
            CopySteps.AssertRegistered(context, workflowCopy, deployed: null);
        });

        yield return Builder.Make("MC-08", "an invalid, overlong, or source-owned --schema-name is refused, and so is any --schema-name for a workflow copy", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            await RefuseAsync(context, json: true).ConfigureAwait(false);

            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", CopySteps.NewName(context), "--schema-name", source.SchemaName.ToUpperInvariant()]).ConfigureAwait(false);

            context.Assert.Refused(result, CopySteps.Usage, "is the source agent's own schema name", "the source's schema name in another case");
        });

        yield return Builder.Make("MC-09", "--agent-name equal to the source agent's or workflow's name in another case is refused", CliAndWorkflow, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", CopySteps.DisplayNameOf(source.ProjectDirectory).ToLowerInvariant()]).ConfigureAwait(false);

            context.Assert.Refused(result, CopySteps.Usage, "is the source agent's own name", "the source's name in lower case");

            var workflowSource = await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false);
            var workflowName = workflowSource.AgentName.ToLowerInvariant();

            RefusedAs(context, await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(workflowSource), "--agent-name", workflowName]).ConfigureAwait(false), "copy-not-prepared", ErrorCodes.ManagedAgent.CopyNotPrepared, "is the source workflow's own name", $"the workflow copy {workflowName}, its source's name in lower case");
            context.Assert.NoDifferences(CopySteps.LeftBesideTheProjectLock(CopySteps.BesideSource(workflowSource, workflowName)), $"nothing but the project's copy lock is left for the workflow copy {workflowName}");
        });

        yield return Builder.Make("MC-10", "--mcs-clone-agent-id or --mcs-clone-workflow-id given a local record id or an unknown id is refused, and so are both together", CliAndWorkflow, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var project = context.Workspaces.EmptyDirectory(context.ScenarioId, "copy");

            string[] Arguments(string agentId) => [.. CopySteps.CloudSource(agentId, context.DevEnvironmentId), "--agent-name", CopySteps.NewName(context), "--project", project];

            context.Assert.Refused(await CopySteps.RawCopyAsync(context, Arguments(source.RecordId!), source.ProjectDirectory).ConfigureAwait(false), CopySteps.Usage, "is the managed-agent record", "a record id passed as a cloud agent id");
            context.Assert.FailedCleanly(await CopySteps.RawCopyAsync(context, Arguments(Guid.NewGuid().ToString())).ConfigureAwait(false), "a cloud agent id nobody owns");
            context.Assert.False(Directory.EnumerateFileSystemEntries(project).Any(), "nothing was written");

            var workflowSource = await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false);
            var workflowProject = context.Workspaces.EmptyDirectory(context.ScenarioId, "workflow-copy");
            var workflowName = CopySteps.NewName(context, "workflow");
            var unknownWorkflowId = Guid.NewGuid().ToString();

            string[] WorkflowArguments(string workflowId) => [.. CopySteps.CloudWorkflowSource(workflowId, context.DevEnvironmentId), "--agent-name", workflowName, "--project", workflowProject];

            var recordId = await CopySteps.RawCopyAsync(context, WorkflowArguments(workflowSource.RecordId!), workflowSource.ProjectDirectory).ConfigureAwait(false);
            context.Assert.Refused(recordId, CopySteps.Usage, "is the managed-agent record", $"the workflow copy {workflowName} of a record id passed as a cloud workflow id");
            context.Assert.Contains(recordId, workflowSource.AgentName, $"the refusal of the workflow copy {workflowName} names the record");
            context.Assert.Contains(recordId, workflowSource.WorkflowId!, $"the refusal of the workflow copy {workflowName} names the record's cloud workflow id");

            RefusedAs(context, await CopySteps.RawCopyAsync(context, WorkflowArguments(unknownWorkflowId)).ConfigureAwait(false), "workflow-source-not-found", ErrorCodes.ManagedAgent.WorkflowSourceNotFound, unknownWorkflowId, $"the workflow copy {workflowName} of a cloud workflow id nobody owns");
            context.Assert.NoDifferences(CopySteps.LeftBesideTheProjectLock(workflowProject), $"nothing but the project's copy lock is left for the workflow copy {workflowName}");

            await RefuseAsync(context, json: true).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-11", "a local copy takes the agent or workflow the current directory or --project is in, and nothing else", CliAndWorkflow, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var outside = context.Workspaces.EmptyDirectory(context.ScenarioId, "outside");
            var inside = await CopySteps.RawCopyAsync(context, ["--agent-name", CopySteps.NewName(context, "inside")], source.File("behaviors")).ConfigureAwait(false);

            context.Assert.ExitZero(inside, "a copy run from a folder inside the agent's folder");
            context.Assert.Equal(source.SchemaName, CopySteps.RequireHome(context, inside.Json<ManagedAgentCopyResult>()).SourceSchemaName, "the agent the current directory is in is the source");
            context.Assert.Refused(await CopySteps.RawCopyAsync(context, ["--project", source.File("missing"), "--agent-name", CopySteps.NewName(context, "missing")]).ConfigureAwait(false), CopySteps.Usage, "No agent configuration was found", "a --project that does not exist, even inside the agent's folder");
            context.Assert.Refused(await CopySteps.RawCopyAsync(context, ["--agent-name", CopySteps.NewName(context, "outside")], outside).ConfigureAwait(false), CopySteps.Usage, "No agent configuration was found", "a copy run outside any project");
            context.Assert.False(Directory.EnumerateFileSystemEntries(outside).Any(), "nothing was written outside any project");

            var agentCopy = inside.Json<ManagedAgentCopyResult>();
            var nested = await CopySteps.WorkflowSourceAsync(context, parentDirectory: agentCopy.ProjectDirectory).ConfigureAwait(false);
            var byProject = await CopySteps.CopyAsync(context, CopySteps.LocalSource(nested), "workflow-by-project").ConfigureAwait(false);
            context.Assert.Equal(nested.WorkflowId, byProject.SourceMcsWorkflowId, $"--project naming the folder of the workflow {nested.AgentName}, registered beside an agent, makes the workflow copy {byProject.DisplayName} of it");

            var insideWorkflowName = CopySteps.NewName(context, "workflow-inside");
            var insideWorkflow = await CopySteps.RawCopyAsync(context, ["--agent-name", insideWorkflowName], Path.Combine(nested.ProjectDirectory, WorkflowWorkspace.WorkflowsFolderName)).ConfigureAwait(false);
            context.Assert.ExitZero(insideWorkflow, $"the workflow copy {insideWorkflowName} run from a folder inside the workflow's folder");
            context.Assert.Equal(nested.WorkflowId, CopySteps.RequireHome(context, insideWorkflow.Json<ManagedAgentCopyResult>()).SourceMcsWorkflowId, $"the workflow the current directory is in is the source of the workflow copy {insideWorkflowName}");

            context.Assert.Equal(agentCopy.SchemaName, (await CopySteps.CopyAsync(context, CopySteps.LocalSource(agentCopy.ProjectDirectory), "agent-beside-workflow").ConfigureAwait(false)).SourceSchemaName, $"the project root still copies the agent, not the workflow {nested.AgentName} it also registers");

            var standalone = await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false);
            var alone = await CopySteps.CopyAsync(context, CopySteps.LocalSource(standalone), "workflow-alone").ConfigureAwait(false);
            context.Assert.Equal(standalone.WorkflowId, alone.SourceMcsWorkflowId, $"a project holding only the workflow {standalone.AgentName} copies it as the workflow copy {alone.DisplayName}");

            var fromWorkflowProject = await CopySteps.CopyAsync(context, CopySteps.LocalSource(alone.ProjectDirectory), "workflow-from-root").ConfigureAwait(false);
            context.Assert.Equal(alone.McsWorkflowId, fromWorkflowProject.SourceMcsWorkflowId, $"the root of a project holding only the workflow copy {alone.DisplayName}, in a folder, copies it as the workflow copy {fromWorkflowProject.DisplayName}");
        });

        yield return Builder.Make("MC-12", "a local agent or workflow source names its own environment when another is asked for", CliAndWorkflow, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);

            string[] Arguments(string label, params string[] extra) => [.. CopySteps.LocalSource(source), "--agent-name", CopySteps.NewName(context, label), .. extra];

            var elsewhere = await CopySteps.RawCopyAsync(context, Arguments("elsewhere", "--environment-id", Guid.NewGuid().ToString())).ConfigureAwait(false);
            context.Assert.Refused(elsewhere, CopySteps.Usage, "is not the environment the local source belongs to", "another environment");
            context.Assert.Contains(elsewhere, context.DevEnvironmentId, "the message names the source's environment");

            CopySteps.RewriteRegistry(source.ConfigPath, registry => registry with { ManagedAgents = [.. registry.ManagedAgents.Select(agent => agent with { EnvironmentId = string.Empty })] });

            (string Label, string[] Extra, bool Copies)[] variants = [("named", ["--environment-id", context.DevEnvironmentId], true), ("unnamed", [], false)];

            await EachAsync(context, "a record with no environment copies in the named one, and without one is refused clearly", variants, variant => variant.Label, async variant =>
            {
                var result = await CopySteps.RawCopyAsync(context, Arguments($"blank-{variant.Label}", variant.Extra)).ConfigureAwait(false);

                if (variant.Copies)
                {
                    context.Assert.ExitZero(result, "the copy in the named environment");
                    CopySteps.RequireHome(context, result.Json<ManagedAgentCopyResult>());
                    return;
                }

                context.Assert.Refused(result, CopySteps.Usage, "--environment-id", "the refusal");
                context.Assert.DoesNotContain(result, "()", "the message names a real environment");
                context.Assert.DoesNotContain(result, "agent create", "the message names the command that was run");
            }).ConfigureAwait(false);

            var workflowName = CopySteps.NewName(context, "workflow-elsewhere");
            var workflowElsewhere = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false)), "--agent-name", workflowName, "--environment-id", Guid.NewGuid().ToString()]).ConfigureAwait(false);

            context.Assert.Refused(workflowElsewhere, CopySteps.Usage, "is not the environment the local source belongs to", $"the workflow copy {workflowName} in another environment");
            context.Assert.Contains(workflowElsewhere, context.DevEnvironmentId, $"the refusal of the workflow copy {workflowName} names its source's environment");
            context.Assert.Contains(workflowElsewhere, "--mcs-clone-workflow-id", $"the refusal of the workflow copy {workflowName} names --mcs-clone-workflow-id");
        });

        yield return Builder.Make("MC-14", "agent delete --all-resources of a copy that never deployed finds none of its resources, removes only its records, and sends nothing, and of a workflow copy is refused and keeps its record and files", CliAndWorkflow, SafetyTier.ReadOnly, run: async context =>
        {
            var copy = await CopySteps.NeverDeployedCopyAsync(context).ConfigureAwait(false);
            var workspace = TreeSnapshot.Take(copy.WorkspaceDirectory);
            var deleted = await CopySteps.DeleteAsync(context, copy, allResources: true).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "agent delete --all-resources");

            var result = deleted.Json<ManagedAgentDeleteResult>();
            context.Assert.True(result.McsAgentAlreadyAbsent && result.Resources.Count == 0, "the delete finds no agent and none of the copy's resources in the environment");
            context.Assert.False(CopySteps.Registry(copy.ConfigPath).ManagedAgents.Any(agent => agent.AgentId == copy.AgentId), "the copy's record is gone");
            context.Assert.NoDifferences(TreeSnapshot.Take(copy.WorkspaceDirectory).DifferencesFrom(workspace), "the delete left the copy's workspace as it was");
            await CopySteps.AssertNothingSentAsync(context, copy, "the delete sent nothing").ConfigureAwait(false);

            var workflowCopy = await CopySteps.RegisteredAsync(context, CopySteps.LocalSource(await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false)), "workflow").ConfigureAwait(false);
            var workflowProject = TreeSnapshot.Take(workflowCopy.ProjectDirectory);

            context.Assert.Refused(await CopySteps.DeleteAsync(context, workflowCopy, allResources: true).ConfigureAwait(false), CopySteps.Usage, "a standalone workflow has none of its own", $"agent delete --all-resources of the workflow copy {workflowCopy.DisplayName}");
            context.Assert.NoDifferences(TreeSnapshot.Take(workflowCopy.ProjectDirectory).DifferencesFrom(workflowProject), $"the refused delete left the record and files of the workflow copy {workflowCopy.DisplayName} as they were");
            CopySteps.AssertRegistered(context, workflowCopy, deployed: null);
            await CopySteps.AssertNothingSentAsync(context, workflowCopy, $"the refused delete of the workflow copy {workflowCopy.DisplayName} sent nothing").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-15", "every argument refusal is a clean usage error, as JSON and as text", Signed, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            await RefuseAsync(context, json: true, ArgumentRefusals).ConfigureAwait(false);
            await RefuseAsync(context, json: false, ArgumentRefusals).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-16", "--help of copy, deploy, and delete lists exactly the documented options", Signed, SafetyTier.ReadOnly, run: async context =>
        {
            var docs = FileTree.FindAncestor(AppContext.BaseDirectory, directory => Directory.Exists(Path.Combine(directory, DocsFolder))) is { } root ? Path.Combine(root, DocsFolder) : throw new ScenarioBlockedException($"no {DocsFolder} folder is above {AppContext.BaseDirectory}", "command docs missing");

            await EachAsync(context, "every command's --help matches its docs", HelpTopics, topic => string.Join(' ', topic.Command), async topic =>
            {
                var help = await context.Msagent.RunAsync([.. topic.Command, "--help"], cancellationToken: context.CancellationToken).ConfigureAwait(false);
                context.Assert.ExitZero(help, $"{string.Join(' ', topic.Command)} --help");

                var listed = Options(HelpOption(), help.StdOut[Math.Max(0, help.StdOut.IndexOf("Options:", StringComparison.Ordinal))..]);
                var documented = Options(DocumentedOption(), Section(File.ReadAllText(Path.Combine(docs, topic.Document)), topic.Heading));

                context.Assert.NoDifferences([.. listed.Except(documented).Select(option => $"{option} is in --help but not in {topic.Document}"), .. documented.Except(listed).Select(option => $"{option} is in {topic.Document} but not in --help")], $"{string.Join(' ', topic.Command)} options and shortcuts match {topic.Document}");
            }).ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> Preparation()
    {
        yield return Builder.Make("MC-20", "a local copy of an agent or a workflow registers locally as one of its own and leaves the cloud and the source alone", CliAndWorkflow, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var before = TreeSnapshot.Take(source.ProjectDirectory);
            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(source)).ConfigureAwait(false);

            context.Assert.Equal("agent-copied", copy.Status, "the result status");
            context.Assert.Equal(CopilotCopySourceKinds.Local, copy.Source, "the source kind");
            context.Assert.Equal(source.SchemaName, copy.SourceSchemaName, "the source's schema name");
            context.Assert.True(copy.Identities.Any(identity => identity.Kind == CopyIdentityKinds.Schema && string.Equals(identity.Source, source.SchemaName, StringComparison.OrdinalIgnoreCase) && string.Equals(identity.Destination, copy.SchemaName, StringComparison.OrdinalIgnoreCase)), "the result maps the source's schema name to the copy's");
            CopySteps.AssertRegistered(context, copy, deployed: null);
            context.Assert.False(Directory.EnumerateFiles(copy.ProjectDirectory, "copy.sync.yaml", SearchOption.AllDirectories).Any(), "the copy writes no copy.sync.yaml");
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(copy.ProjectDirectory), ".config\\copy holds only .gitignore and copy.lock");
            context.Assert.NoDifferences(CopySteps.SourceSchemaMentions(copy), "no file of the copy names the source's schema");
            context.Assert.False(Directory.Exists(Path.Combine(copy.WorkspaceDirectory, ".mcs")), "the copy carries no cloud binding");
            context.Assert.NoDifferences(TreeSnapshot.Take(source.ProjectDirectory).DifferencesFrom(before), "the source project is byte-identical");
            await CopySteps.AssertNothingSentAsync(context, copy, "nothing was sent").ConfigureAwait(false);
            context.Assert.Equal(AgentTypes.McsAgent, copy.AgentType, "the result names an agent copy");
            context.Assert.True(copy.McsWorkflowId is null && copy.SourceMcsWorkflowId is null && copy.WorkflowName is null && copy.WorkflowFolder is null && copy.WorkflowState is null, "the result of an agent copy carries no workflow fields");

            var workflowSource = await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false);
            var workflowBefore = TreeSnapshot.Take(workflowSource.ProjectDirectory);
            var workflowCopy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(workflowSource), "workflow").ConfigureAwait(false);
            var workflowFolder = WorkflowSteps.Folder(workflowCopy.WorkspaceDirectory);

            context.Assert.Equal("agent-copied", workflowCopy.Status, $"the result status of the workflow copy {workflowCopy.DisplayName}");
            context.Assert.Equal(CopilotCopySourceKinds.Local, workflowCopy.Source, $"the source kind of the workflow copy {workflowCopy.DisplayName}");
            context.Assert.Equal(AgentTypes.McsWorkflow, workflowCopy.AgentType, $"the result names the workflow copy {workflowCopy.DisplayName} a workflow");
            CopySteps.AssertRegistered(context, workflowCopy, deployed: null);
            context.Assert.Equal(workflowSource.WorkflowId, workflowCopy.SourceMcsWorkflowId, $"the workflow copy {workflowCopy.DisplayName} names its source's flow id");
            context.Assert.False(string.Equals(workflowSource.WorkflowId, workflowCopy.McsWorkflowId, StringComparison.OrdinalIgnoreCase), $"the workflow copy {workflowCopy.DisplayName} gets a flow id of its own");
            context.Assert.True(workflowCopy.Identities.Any(identity => identity.Kind == CopyIdentityKinds.Workflow && string.Equals(identity.Source, workflowSource.WorkflowId, StringComparison.OrdinalIgnoreCase) && string.Equals(identity.Destination, workflowCopy.McsWorkflowId, StringComparison.OrdinalIgnoreCase)), $"the result maps the source's flow id to the workflow copy {workflowCopy.DisplayName}'s");
            context.Assert.Equal(workflowCopy.DisplayName, workflowCopy.WorkflowName, $"the workflow copy {workflowCopy.DisplayName} names its flow so");
            context.Assert.Equal(workflowFolder.FolderPath, workflowCopy.WorkflowFolder, $"the result names the folder of the workflow copy {workflowCopy.DisplayName}");
            context.Assert.Equal(workflowCopy.McsWorkflowId, workflowFolder.WorkflowId.ToString(), $"the folder of the workflow copy {workflowCopy.DisplayName} holds its own flow");
            context.Assert.Equal(WorkflowStateProjection.NotDeployed, workflowCopy.WorkflowState, $"the result reports the workflow copy {workflowCopy.DisplayName} not deployed");
            context.Assert.Equal("0", CopySteps.WorkflowMetadataOf(workflowCopy.WorkspaceDirectory, "stateCode"), $"the workflow copy {workflowCopy.DisplayName} is a draft");
            context.Assert.Equal("1", CopySteps.WorkflowMetadataOf(workflowSource.ProjectDirectory, "stateCode"), $"the source of the workflow copy {workflowCopy.DisplayName} is activated");
            context.Assert.True(workflowCopy.SchemaName is null && workflowCopy.SourceSchemaName is null, $"the result of the workflow copy {workflowCopy.DisplayName} names no schema");
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(workflowCopy.ProjectDirectory), $".config\\copy holds only .gitignore and copy.lock for the workflow copy {workflowCopy.DisplayName}");
            context.Assert.NoDifferences(CopySteps.SourceIdentityMentions(workflowCopy.WorkspaceDirectory, workflowCopy), $"no file of the workflow copy {workflowCopy.DisplayName} names its source's identities");
            context.Assert.False(Directory.Exists(Path.Combine(workflowCopy.WorkspaceDirectory, ".mcs")), $"the workflow copy {workflowCopy.DisplayName} carries no cloud binding");
            context.Assert.NoDifferences(TreeSnapshot.Take(workflowSource.ProjectDirectory).DifferencesFrom(workflowBefore), $"the source of the workflow copy {workflowCopy.DisplayName} is byte-identical");
            await CopySteps.AssertNothingSentAsync(context, workflowCopy, $"the workflow copy {workflowCopy.DisplayName} sent nothing").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-22", "a cloud copy of an agent or a workflow stages a clone, keeps no copy of the source, and binds to nothing", Classic, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);
            var copy = await CopySteps.CopyAsync(context, CopySteps.CloudSource(seed)).ConfigureAwait(false);

            context.Assert.Equal(CopilotCopySourceKinds.Cloud, copy.Source, "the source kind");
            context.Assert.Equal(seed.CloudAgentId, copy.SourceMcsAgentId, "the provenance names the source's cloud agent");
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(copy.ProjectDirectory), "no copy of the source is kept under .config\\copy");
            context.Assert.False(Directory.Exists(Path.Combine(copy.WorkspaceDirectory, ".mcs")), "the copy carries no cloud binding");
            context.Assert.True(CopySteps.OwnedIdentities(copy, CopyIdentityKinds.AIModel).Count > 0, "the prompts are copied");
            context.Assert.True(CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Connector).Count > 0, "the custom connector is copied");
            context.Assert.NoDifferences(CopySteps.SourceSchemaMentions(copy), "no file of the copy names the source's schema");
            await CopySteps.AssertNothingSentAsync(context, copy, "nothing was sent").ConfigureAwait(false);

            await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false);

            var flow = WorkflowSteps.Folder(seed.ProjectDirectory);
            var workflowCopy = await CopySteps.CopyAsync(context, CopySteps.CloudWorkflowSource(flow.WorkflowId.ToString(), seed.EnvironmentId), "workflow").ConfigureAwait(false);

            context.Assert.Equal(CopilotCopySourceKinds.Cloud, workflowCopy.Source, $"the source kind of the workflow copy {workflowCopy.DisplayName}");
            context.Assert.Equal(flow.WorkflowId.ToString(), workflowCopy.SourceMcsWorkflowId, $"the provenance of the workflow copy {workflowCopy.DisplayName} names the classic seed's flow");
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(workflowCopy.ProjectDirectory), $"no copy of the source flow is kept under .config\\copy for the workflow copy {workflowCopy.DisplayName}");
            context.Assert.False(Directory.Exists(Path.Combine(workflowCopy.WorkspaceDirectory, ".mcs")), $"the workflow copy {workflowCopy.DisplayName} carries no cloud binding");
            context.Assert.True(CopySteps.OwnedIdentities(workflowCopy, CopyIdentityKinds.ConnectionReference).Count == 1, $"the workflow copy {workflowCopy.DisplayName} renames the flow's connection reference");
            context.Assert.NoDifferences(CopySteps.SourceIdentityMentions(workflowCopy.WorkspaceDirectory, workflowCopy), $"no file of the workflow copy {workflowCopy.DisplayName} names the flow's identities");
            await CopySteps.AssertNothingSentAsync(context, workflowCopy, $"the workflow copy {workflowCopy.DisplayName} sent nothing").ConfigureAwait(false);
            await CopySteps.AssertSourceIntactAsync(context, seed).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-23", "a local copy lands beside its source under a safe folder name", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            string[] names = [$"ahe2e {context.ScenarioId} A/B:C*?<>| {context.RunSuffix}", $"ahe2e {context.ScenarioId} trailing dot {context.RunSuffix}.", $"  ahe2e {context.ScenarioId} padded {context.RunSuffix}  ", "CON", "aux.txt"];

            await EachAsync(context, "every awkward name copies beside the source or is refused cleanly", names, name => JsonSerializer.Serialize(name), async name =>
            {
                var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", name]).ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    context.Assert.FailedCleanly(result, "the refusal");
                    context.Assert.Contains(result, "--agent-name", "the refusal suggests another --agent-name");
                    return;
                }

                var copy = CopySteps.RequireHome(context, result.Json<ManagedAgentCopyResult>());
                context.Assert.Equal(name.Trim(), copy.DisplayName, "the name is kept as typed, trimmed");
                context.Assert.Equal(CopySteps.BesideSource(source, name), copy.ProjectDirectory, "the project is a sibling of the source's");
                CopySteps.AssertRegistered(context, copy, deployed: null);
            }).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-24", "a --project that is a file, or a name whose place beside the source is registered, occupied, or the source's own, is refused cleanly", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var before = TreeSnapshot.Take(source.ProjectDirectory);
            var file = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId, "file"), "project.txt");
            var occupiedName = CopySteps.NewName(context, "occupied");
            var occupied = Path.Combine(CopySteps.BesideSource(source, occupiedName), WorkspaceFolderNames.ForProject(occupiedName));
            var registered = await CopySteps.CopyAsync(context, CopySteps.LocalSource(source), "registered").ConfigureAwait(false);

            File.WriteAllText(file, "not a project");
            Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "keep.txt"), "occupied");

            (string Label, string[] Arguments, string Fragment)[] refusals =
            [
                ("a file", ["--project", file, "--agent-name", CopySteps.NewName(context)], "No agent configuration was found"),
                ("a name the project beside the source registers", [.. CopySteps.LocalSource(source), "--agent-name", registered.DisplayName], "is already registered"),
                ("a name whose folder beside the source holds content", [.. CopySteps.LocalSource(source), "--agent-name", occupiedName], "already holds content"),
                ("a name whose folder is the source's own", [.. CopySteps.LocalSource(source), "--agent-name", Path.GetFileName(source.ProjectDirectory)], "would overlap"),
            ];

            await EachAsync(context, "every unusable --project or name is refused", refusals, refusal => refusal.Label, async refusal => context.Assert.Refused(await CopySteps.RawCopyAsync(context, refusal.Arguments).ConfigureAwait(false), CopySteps.Usage, refusal.Fragment, refusal.Label)).ConfigureAwait(false);

            context.Assert.Equal("not a project", File.ReadAllText(file), "the file is untouched");
            context.Assert.Equal("occupied", File.ReadAllText(Path.Combine(occupied, "keep.txt")), "the occupied folder is untouched");
            context.Assert.NoDifferences(TreeSnapshot.Take(source.ProjectDirectory).DifferencesFrom(before), "the source project is byte-identical");
        });

        yield return Builder.Make("MC-25", "two copies registered in one project are each selected by name, and a copy from that project's root asks for the agent's folder", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var first = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false)), "first").ConfigureAwait(false);
            var second = await CopySteps.CopyAsync(context, CopySteps.LocalSource(first), "second").ConfigureAwait(false);
            var project = first.ProjectDirectory;

            context.Assert.Equal(project, second.ProjectDirectory, "a copy of a copy is registered in its project");
            context.Assert.True(CopySteps.Registry(first.ConfigPath).ManagedAgents.Count == 2, "the project registers both copies");

            foreach (var copy in new[] { first, second })
            {
                var shown = await CopySteps.ShowAgentAsync(context, copy).ConfigureAwait(false);
                context.Assert.ExitZero(shown, $"agent show {copy.DisplayName}");

                var result = shown.Json<ManagedAgentShowResult>();
                context.Assert.True(result.Agents.Count == 1 && result.Agents[0].AgentId == copy.AgentId, $"--agent-name selects {copy.DisplayName} alone");
            }

            var ambiguous = await CopySteps.RawCopyAsync(context, ["--agent-name", CopySteps.NewName(context, "third")], project).ConfigureAwait(false);
            context.Assert.Refused(ambiguous, CopySteps.Usage, "holds several agents", "a copy from the root of a project holding several agents");
            context.Assert.Contains(ambiguous, "--project", "the refusal asks for the agent's folder with --project");
            context.Assert.True(CopySteps.Registry(first.ConfigPath).ManagedAgents.Count == 2, "the refused copy registered nothing");

            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, second).ConfigureAwait(false), "deployment create for the second copy");
            context.Assert.True(CopySteps.Registry(first.ConfigPath).AgentDeployments is [{ } deployment] && deployment.AgentId == second.AgentId, "the deployment belongs to the second copy only");
            context.Assert.FailedCleanly(await context.Msagent.RunAsync(["deployment", "create", "--deployment-name", "other", "--deployment-type", "dev", "--environment-id", context.DevEnvironmentId, "--json", "--non-interactive"], project).ConfigureAwait(false), "deployment create without --agent-name in a two-agent project");
        });

        yield return Builder.Make("MC-26", "a source holding a junction is refused and leaves nothing behind", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var name = CopySteps.NewName(context);
            CreateJunction(source.File("linked"), source.File("behaviors"));

            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", name]).ConfigureAwait(false);
            Directory.Delete(source.File("linked"));

            context.Assert.Refused(result, CopySteps.Usage, "holds a link", "a source holding a junction");
            context.Assert.NoDifferences(CopySteps.LeftBesideTheProjectLock(CopySteps.BesideSource(source, name)), "nothing but the project's copy lock is left in the copy's project");
        });

        yield return Builder.Make("MC-27", "a schema name too long for the copy's components stops preparation and writes nothing", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var name = CopySteps.NewName(context);
            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", name, "--schema-name", $"ahe2e_{context.RunSuffix}".PadRight(99, 'x')]).ConfigureAwait(false);

            context.Assert.Refused(result, CopySteps.Usage, "could not be prepared", "a 99-character schema name");
            context.Assert.NoDifferences(CopySteps.LeftBesideTheProjectLock(CopySteps.BesideSource(source, name)), "no workspace, staging, or record is left");
        });

        yield return Builder.Make("MC-28", "a copy of a never-deployed copy and of a deployed copy, agent or workflow, gets fresh identities and leaves its source alone", CliAndWorkflow, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            await AssertCopiesOfCopiesAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false)), () => CopySteps.DeployedCopyAsync(context)).ConfigureAwait(false);
            await AssertCopiesOfCopiesAsync(context, CopySteps.LocalSource(await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false)), () => CopySteps.DeployedWorkflowCopyAsync(context), "workflow-").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-29", "a --schema-name another agent in the environment holds is refused by agent copy, which registers nothing", Both, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var source = await CopySteps.FixtureSourceAsync(context, classic: true).ConfigureAwait(false);
            var name = CopySteps.NewName(context);
            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", name, "--schema-name", seed.SchemaName]).ConfigureAwait(false);

            context.Assert.Refused(result, CopySteps.Usage, "could not be prepared", "a schema name another agent holds");
            context.Assert.Contains(result, "already exists in the environment", "the refusal says another agent holds the schema name");
            context.Assert.Contains(result, seed.SchemaName, "the message names the schema name");
            context.Assert.NoDifferences(CopySteps.LeftBesideTheProjectLock(CopySteps.BesideSource(source, name)), "nothing was registered");

            var (withId, withSchemaName) = await context.Cloud.CountAgentsAsync(seed.EnvironmentId, seed.CloudAgentId, seed.SchemaName, context.CancellationToken).ConfigureAwait(false);
            context.Assert.True(withId == 1 && withSchemaName == 1, "the schema name still belongs to the original agent alone");
        });

        yield return Builder.Make("MC-30", "an unsaved local edit is carried into the copy and stays in the source", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            var marker = $"MC-30 unsaved edit {context.RunSuffix}";
            File.AppendAllText(source.File("behaviors", "skill-1", "SKILL.md"), $"{Environment.NewLine}{marker}{Environment.NewLine}");
            var before = TreeSnapshot.Take(source.ProjectDirectory);

            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(source)).ConfigureAwait(false);

            context.Assert.True(FileTree.FilesMentioning(copy.WorkspaceDirectory, [marker], CopySteps.CloneState).Count == 1, "the unsaved edit is in the copy");
            context.Assert.NoDifferences(TreeSnapshot.Take(source.ProjectDirectory).DifferencesFrom(before), "the source, edit included, is byte-identical");
        });

        yield return Builder.Make("MC-31", "a classic copy owns every identity and names no source schema", Classic, SafetyTier.ReadOnly, run: async context =>
        {
            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context, classic: true).ConfigureAwait(false))).ConfigureAwait(false);
            var kinds = copy.Identities.Select(identity => identity.Kind).ToHashSet(StringComparer.Ordinal);
            string[] expected = [CopyIdentityKinds.Workflow, CopyIdentityKinds.AIModel, CopyIdentityKinds.Connector, CopyIdentityKinds.ConnectionReference];

            context.Assert.NoDifferences([.. expected.Where(kind => !kinds.Contains(kind)).Select(kind => $"no {kind} identity is owned")], "the copy owns its workflow, prompts, connector, and references");
            context.Assert.NoDifferences(CopySteps.SourceSchemaMentions(copy), "no file of the copy names the source's schema");
            context.Assert.True(Directory.Exists(Path.Combine(copy.WorkspaceDirectory, "agents")), "the child agent came along");
        });

        yield return Builder.Make("MC-32", "a copy killed mid-clone can be run again as it was", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var project = context.Workspaces.EmptyDirectory(context.ScenarioId, "copy");
            var configPath = Path.Combine(project, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName);
            string[] arguments = [.. CopySteps.CloudSource(seed), "--agent-name", CopySteps.NewName(context), "--project", project];

            var killed = await CopySteps.RawCopyAsync(context, arguments, interruption: new ProcessInterruption(null, TimeSpan.FromSeconds(4))).ConfigureAwait(false);
            context.Assert.True(killed.Interrupted, "the copy was killed before it finished");
            context.Assert.True(!File.Exists(configPath) || IsJson(File.ReadAllText(configPath)), "the registry is absent or valid after the kill");

            var rerun = await CopySteps.RawCopyAsync(context, arguments).ConfigureAwait(false);
            context.Assert.ExitZero(rerun, "the same copy runs again after the kill");

            var copy = CopySteps.RequireHome(context, rerun.Json<ManagedAgentCopyResult>());
            CopySteps.AssertRegistered(context, copy, deployed: null);
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(project), ".config\\copy holds only .gitignore and copy.lock, and nothing of the killed attempt");
        });

        yield return Builder.Make("MC-33", "--agent-name of an existing cloud agent or workflow in another case is refused and writes nothing", BothAndWorkflow, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var cli = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var source = await CopySteps.FixtureSourceAsync(context, classic: true).ConfigureAwait(false);
            var name = cli.DisplayName.ToUpperInvariant();
            var result = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", name]).ConfigureAwait(false);

            context.Assert.Refused(result, CopySteps.Usage, "already has an agent named", "the name of an agent already in the environment, in upper case");
            context.Assert.False(Directory.Exists(CopySteps.BesideSource(source, name)), "nothing was written beside the source");

            var workflowSource = await CopySteps.WorkflowSourceAsync(context).ConfigureAwait(false);
            var workflowName = CopySteps.WorkflowMetadataOf(cli.ProjectDirectory, "name").ToUpperInvariant();

            RefusedAs(context, await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(workflowSource), "--agent-name", workflowName]).ConfigureAwait(false), "copy-not-prepared", ErrorCodes.ManagedAgent.CopyNotPrepared, "already has a workflow named", $"the workflow copy {workflowName}, the name of the CLI seed's flow in upper case");
            context.Assert.NoDifferences(CopySteps.LeftBesideTheProjectLock(CopySteps.BesideSource(workflowSource, workflowName)), $"nothing but the project's copy lock is left for the workflow copy {workflowName}");
        });
    }

    private static IEnumerable<Scenario> Lifecycle()
    {
        yield return Builder.Make("MC-35", "cloud copy of the CLI agent and of a workflow: copy, deployment create, first deploy", CliAndWorkflow, SafetyTier.Creating, smoke: true, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var (copy, _, clone) = await FirstDeployAsync(context, seed, CopySteps.CloudSource(seed)).ConfigureAwait(false);

            context.Assert.True(copy.DraftWorkflows.Count > 0, "the copied workflows await activation as drafts");
            await CopySteps.AssertWorkflowsExistAsync(context, copy.EnvironmentId, CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Workflow), "the copy").ConfigureAwait(false);
            context.Assert.Equal(Shape((await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false)).CloneDirectory), Shape(clone), "the copy has its source's components");

            var workflowSeed = await CopySteps.WorkflowSeedAsync(context).ConfigureAwait(false);
            await FirstDeployAsync(context, workflowSeed, CopySteps.CloudSource(workflowSeed), "workflow").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-37", "local copy of the classic agent copies prompts, connector, child agent, and knowledge", Classic, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);
            var source = await CopySteps.ClonedSourceAsync(context, seed).ConfigureAwait(false);
            var before = CopySteps.SnapshotLocalSource(source);
            var (copy, _, clone) = await FirstDeployAsync(context, seed, CopySteps.LocalSource(source)).ConfigureAwait(false);

            context.Assert.True(CopySteps.OwnedIdentities(copy, CopyIdentityKinds.AIModel).Count > 0, "the prompts are copied under new model ids");
            context.Assert.True(CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Connector).Count > 0, "the custom connector is copied");
            context.Assert.Equal(Shape((await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false)).CloneDirectory), Shape(clone), "the copy has its source's components, child agent and knowledge included");
            CopySteps.AssertLocalSourceIntact(context, source, before);
        });

        yield return Builder.Make("MC-39", "a deployed agent or workflow copy redeploys unchanged into its own agent or flow", CliAndWorkflow, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var (copy, deployed) = await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false);
            var again = await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);

            context.Transcript.Note($"redeploy warnings: {string.Join(" | ", again.Warnings)}");
            await CopySteps.AssertRedeployedAsync(context, copy, deployed, again).ConfigureAwait(false);

            var (workflowCopy, workflowDeployed) = await CopySteps.DeployedWorkflowCopyAsync(context).ConfigureAwait(false);
            await CopySteps.AssertRedeployedAsync(context, workflowCopy, workflowDeployed, await CopySteps.DeployAsync(context, workflowCopy).ConfigureAwait(false)).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-40", "an edit to an owned skill reaches only the copy", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false);

            var (copy, deployed) = await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false);
            var marker = $"MC-40 copy-only edit {context.RunSuffix}";
            File.AppendAllText(Directory.EnumerateFiles(copy.WorkspaceDirectory, "SKILL.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First(), $"{Environment.NewLine}{marker}{Environment.NewLine}");

            await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);

            context.Assert.True(FileTree.FilesMentioning(await CopySteps.CloneAsync(context, CopySteps.CloudAgentId(deployed), copy.EnvironmentId, "edited").ConfigureAwait(false), [marker], CopySteps.CloneState).Count > 0, "the edit reached the copy");
            await CopySteps.AssertSourceIntactAsync(context, seed).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-42", "--publish on the first deploy publishes the agent or workflow copy and leaves the source alone", CliAndWorkflow, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            await PublishedFirstDeployAsync(context, seed, CopySteps.CloudSource(seed)).ConfigureAwait(false);

            var workflowSeed = await CopySteps.WorkflowSeedAsync(context).ConfigureAwait(false);
            var (workflowCopy, workflowDeployed) = await PublishedFirstDeployAsync(context, workflowSeed, CopySteps.CloudSource(workflowSeed), "workflow").ConfigureAwait(false);

            context.Assert.True(workflowDeployed.UnboundConnectionCount == 0, $"the published workflow copy {workflowCopy.DisplayName} leaves no connection reference unbound");
        });

        yield return Builder.Make("MC-43", "agent pull on a deployed agent or workflow copy reads its own agent or flow and keeps the copy's records", CliAndWorkflow, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var (copy, deployed) = await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false);
            var registryBefore = File.ReadAllText(copy.ConfigPath);

            var pulled = await CopySteps.PullAsync(context, copy).ConfigureAwait(false);

            context.Assert.ExitZero(pulled, "agent pull on a deployed copy");
            context.Assert.Equal(CopySteps.CloudAgentId(deployed), BoundAgentId(copy.WorkspaceDirectory), "the workspace is bound to the copy's own agent");
            context.Assert.Equal(registryBefore, File.ReadAllText(copy.ConfigPath), "the copy's records are byte-identical");

            var (workflowCopy, _) = await CopySteps.DeployedWorkflowCopyAsync(context).ConfigureAwait(false);
            var workflowRegistryBefore = File.ReadAllText(workflowCopy.ConfigPath);

            var workflowPulled = await CopySteps.PullAsync(context, workflowCopy).ConfigureAwait(false);

            context.Assert.ExitZero(workflowPulled, $"agent pull on the deployed workflow copy {workflowCopy.DisplayName}");

            var workflowPull = workflowPulled.Json<ManagedAgentPullResult>();
            context.Assert.Equal(workflowCopy.AgentId, workflowPull.AgentId, $"the pull reads the record of the workflow copy {workflowCopy.DisplayName}");
            context.Assert.Equal(workflowCopy.DisplayName, workflowPull.WorkflowName, $"the pull reads the flow of the workflow copy {workflowCopy.DisplayName}, not its source's");
            context.Assert.Equal(workflowCopy.McsWorkflowId, WorkflowSteps.Folder(workflowCopy.WorkspaceDirectory).WorkflowId.ToString(), $"the workspace of the workflow copy {workflowCopy.DisplayName} holds its own flow");
            context.Assert.Equal(workflowRegistryBefore, File.ReadAllText(workflowCopy.ConfigPath), $"the records of the workflow copy {workflowCopy.DisplayName} are byte-identical");
        });

        yield return Builder.Make("MC-44", "a deployed copy clones back to the files it deployed", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var (copy, deployed) = await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false);
            var clone = await CopySteps.CloneAsync(context, CopySteps.CloudAgentId(deployed), copy.EnvironmentId, "round-trip").ConfigureAwait(false);
            var cloned = RelativeFiles(Path.GetDirectoryName(McsSteps.FindFile(clone, "settings.mcs.yml") ?? throw new E2EException($"the clone in {clone} holds no settings.mcs.yml"))!);
            var deployedFiles = RelativeFiles(copy.WorkspaceDirectory);

            context.Assert.NoDifferences([.. cloned.Except(deployedFiles, StringComparer.OrdinalIgnoreCase).Select(path => $"only the clone has {path}"), .. deployedFiles.Except(cloned, StringComparer.OrdinalIgnoreCase).Select(path => $"only the copy has {path}")], "the clone holds the copy's files");
        });

        yield return Builder.Make("MC-45", "deployment delete of a deployed copy removes its agent, keeps the copy registered with no cloud agent, and reports which of its workflows outlive the delete", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var (copy, deployed) = await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false);
            CopySteps.ConsumeDeployedCopy(context);

            context.Assert.ExitZero(await McsSteps.DeleteDeploymentAsync(context, CopySteps.WorkspaceOf(context, copy), CopySteps.DeploymentName, deleteCloudAgent: true).ConfigureAwait(false), "deployment delete --delete-deployed-mcs-agent");

            await CopySteps.AssertAgentGoneAsync(context, copy, CopySteps.CloudAgentId(deployed), "the copy's agent is gone").ConfigureAwait(false);
            context.Assert.False(CopySteps.Registry(copy.ConfigPath).AgentDeployments.Any(deployment => deployment.AgentId == copy.AgentId), "the deployment record is gone");
            CopySteps.AssertRegistered(context, copy, deployed: null);

            var workflows = CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Workflow);
            var remaining = 0;

            foreach (var workflow in workflows)
            {
                remaining += await context.Cloud.WorkflowExistsAsync(copy.EnvironmentId, workflow, context.CancellationToken).ConfigureAwait(false) == false ? 0 : 1;
            }

            context.Transcript.Note($"documented gap: {remaining} of {workflows.Count} copied workflow(s) outlive the delete");
        });

        yield return Builder.Make("MC-47", "copy and first deploy durations stay within budget of a plain clone and deploy", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var plain = context.Workspaces.CliAgent(context.ScenarioId, "plain");
            await McsSteps.GuardSchemaNameAsync(context, plain, context.DevEnvironmentId).ConfigureAwait(false);
            context.Assert.ExitZero(await McsSteps.InitAsync(context, plain).ConfigureAwait(false), "agent init (plain)");
            context.Assert.ExitZero(await McsSteps.CreateSlotAsync(context, plain, CopySteps.DeploymentName, context.DevEnvironmentId).ConfigureAwait(false), "deployment create (plain)");

            var clone = await CopySteps.TimeAsync(() => CopySteps.CloneAsync(context, seed.CloudAgentId, seed.EnvironmentId, "timed")).ConfigureAwait(false);
            var copy = await CopySteps.TimeAsync(() => CopySteps.RegisteredAsync(context, CopySteps.CloudSource(seed))).ConfigureAwait(false);
            var plainDeploy = await CopySteps.TimeAsync(() => McsSteps.DeployAsync(context, plain, CopySteps.DeploymentName)).ConfigureAwait(false);
            var firstDeploy = await CopySteps.TimeAsync(() => CopySteps.DeployAsync(context, copy.Value)).ConfigureAwait(false);
            await CopySteps.AssertFirstDeployAsync(context, copy.Value, firstDeploy.Value).ConfigureAwait(false);

            context.Transcript.Note($"timing: clone {clone.Seconds:N0}s, copy and deployment create {copy.Seconds:N0}s, plain deploy {plainDeploy.Seconds:N0}s, first copy deploy {firstDeploy.Seconds:N0}s");
            context.Assert.True(copy.Seconds <= (2 * clone.Seconds) + 15, $"the copy takes at most twice a plain clone ({copy.Seconds:N0}s against {clone.Seconds:N0}s)");
            context.Assert.True(firstDeploy.Seconds <= (1.5 * plainDeploy.Seconds) + 30, $"the first copy deploy takes at most 1.5 times a plain deploy ({firstDeploy.Seconds:N0}s against {plainDeploy.Seconds:N0}s)");
        });
    }

    private static IEnumerable<Scenario> EdgeCases()
    {
        yield return Builder.Make("MC-55b", "a deployed copy whose .mcs folder was deleted deploys again into its own agent", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var copy = await CopySteps.NeverDeployedCopyAsync(context, "rebound").ConfigureAwait(false);
            var first = await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);
            Directory.Delete(Path.Combine(copy.WorkspaceDirectory, ".mcs"), recursive: true);

            var again = await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);

            await CopySteps.AssertRedeployedAsync(context, copy, first, again).ConfigureAwait(false);
            context.Assert.Equal(CopySteps.CloudAgentId(first), BoundAgentId(copy.WorkspaceDirectory), "the workspace is bound to the copy's own agent again");
        });

        yield return Builder.Make("MC-56", "a copy of a copy whose tool was pointed back at the source's workflow is refused by agent copy, which writes nothing", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false))).ConfigureAwait(false);
            var workflow = copy.Identities.First(identity => identity.Kind == CopyIdentityKinds.Workflow);
            var tools = FileTree.FilesMentioning(Path.Combine(copy.WorkspaceDirectory, "capabilities", "tools"), [workflow.Destination], []);

            context.Assert.True(tools.Count > 0, "a tool calls the copied workflow");

            foreach (var tool in tools)
            {
                File.WriteAllText(tool, File.ReadAllText(tool).Replace(workflow.Destination, workflow.Source, StringComparison.OrdinalIgnoreCase));
            }

            var before = TreeSnapshot.Take(copy.ProjectDirectory);
            var refused = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(copy), "--agent-name", CopySteps.NewName(context, "of-pointed-back")]).ConfigureAwait(false);

            context.Assert.Refused(refused, CopySteps.Usage, "could not be prepared", "a copy of a copy calling the source's workflow");
            context.Assert.Contains(refused, workflow.Source, "the refusal names the source's workflow");
            context.Assert.NoDifferences(TreeSnapshot.Take(copy.ProjectDirectory).DifferencesFrom(before), "the refused copy registered and wrote nothing");
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(copy.ProjectDirectory), "the refused copy left no state under .config\\copy");
        });

        yield return Builder.Make("MC-57", "a deploy with no deployment record or a misspelled one fails cleanly and sends nothing", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false))).ConfigureAwait(false);

            context.Assert.FailedCleanly(await CopySteps.RawDeployAsync(context, copy).ConfigureAwait(false), "a deploy before deployment create");
            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, copy).ConfigureAwait(false), "deployment create");

            var misspelled = await CopySteps.DeployCommandAsync(context, copy, "devv", []).ConfigureAwait(false);
            context.Assert.FailedCleanly(misspelled, "a misspelled --deployment-name");
            context.Assert.Contains(misspelled, "devv", "the message names the deployment it could not find");
            await CopySteps.AssertNothingSentAsync(context, copy, "nothing was sent").ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> Corpus()
    {
        foreach (var folder in CorpusFolders)
        {
            yield return Builder.Make(folder.BringUpId, $"bring up {folder.Name}, {folder.Description}, as a copy of a project never deployed here", FromCorpus, SafetyTier.Creating, timeout: Long, run: async context =>
            {
                var corpus = await CopySteps.CorpusSeedAsync(context, folder.Name).ConfigureAwait(false);

                context.Transcript.Note($"timing: {folder.Name} bring-up with its checks {corpus.Seconds:N0}s");
                context.Assert.Equal(Shape(corpus.Copy.WorkspaceDirectory), Shape(corpus.CloneDirectory), $"the deployed {folder.Name} holds every component its copy holds");
                context.Assert.NoDifferences([.. CopySteps.OwnedIdentities(corpus.Copy, CopyIdentityKinds.ConnectorInternalId).Except(ConnectorProperties(corpus.CloneDirectory, "connectorinternalid"), StringComparer.OrdinalIgnoreCase).Select(internalId => $"no connector of the deployed {folder.Name} has the internal id {internalId}")], $"the deployed {folder.Name} holds its copied connectors under the internal ids the copy predicted for this environment");

                if (folder.ChecksPushedComponentCount)
                {
                    AssertPushedEveryDeployedComponent(context, corpus.Deployed, corpus.CloneDirectory, folder.Name);
                }
            });
        }

        foreach (var folder in CorpusFolders)
        {
            yield return Builder.Make(folder.CopiesId, $"cloud and local copies of the {folder.Name} seed deploy isolated and leave the seed unchanged", FromCorpus, SafetyTier.Creating, timeout: Long, run: async context =>
            {
                var seed = (await CopySteps.CorpusSeedAsync(context, folder.Name).ConfigureAwait(false)).Seed;

                await FirstDeployAsync(context, seed, CopySteps.CloudSource(seed), "cloud").ConfigureAwait(false);
                await FirstDeployAsync(context, seed, CopySteps.LocalSource(await CopySteps.ClonedSourceAsync(context, seed).ConfigureAwait(false)), "local").ConfigureAwait(false);
            });
        }

        yield return Builder.Make("MC-68", $"a {AgentNameValidator.MaximumLength}-character --agent-name, the service's agent name limit, on a copy of the classic agent keeps the copied connector's display name within 30 sanitized characters", Classic, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);
            var (copy, _, clone) = await FirstDeployAsync(context, seed, CopySteps.CloudSource(seed), "long", $"{CopySteps.NewName(context, "long")} {LongNameFiller}"[..AgentNameValidator.MaximumLength].TrimEnd()).ConfigureAwait(false);
            string[] directories = [copy.WorkspaceDirectory, clone];

            await EachAsync(context, "every copied connector's display name fits the connector limits", directories, directory => directory, directory =>
            {
                var names = ConnectorProperties(directory, "displayname");

                context.Assert.True(names.Count > 0, $"{directory} holds a connector");
                context.Assert.NoDifferences([.. names.Where(name => name.Length > ConnectorDisplayNameLimit || !ConnectorDisplayName().IsMatch(name)).Select(name => $"'{name}' has {name.Length} characters")], $"the connector display names in {directory} are at most {ConnectorDisplayNameLimit} letters, digits, spaces, hyphens, or underscores");

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-69", $"an --agent-name past the service's {AgentNameValidator.MaximumLength}-character agent name limit is refused before anything is written", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var copied = await CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false)), "--agent-name", CopySteps.NewName(context, "over").PadRight(AgentNameValidator.MaximumLength + 1, 'x')]).ConfigureAwait(false);

            if (copied.ExitCode != 0)
            {
                context.Assert.Refused(copied, CopySteps.Usage, $"{AgentNameValidator.MaximumLength}", "the copy of the over-long name");
                return;
            }

            var copy = CopySteps.RequireHome(context, copied.Json<ManagedAgentCopyResult>());
            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, copy).ConfigureAwait(false), "deployment create");
            context.Assert.Refused(await CopySteps.RawDeployAsync(context, copy).ConfigureAwait(false), CopySteps.Usage, $"{AgentNameValidator.MaximumLength}", "the first deploy of the over-long name");
            await CopySteps.AssertNothingSentAsync(context, copy, "the refused first deploy sent nothing").ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> Deletion()
    {
        yield return Builder.Make("MC-82", "agent delete --all-resources of a deployed copy removes its agent with the flows, prompts, and connectors its workspace names, and leaves its files and its source alone, and agent delete of a deployed workflow copy removes only its flow and record", ClassicAndWorkflow, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);
            await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false);

            var (copy, deployed) = await CopySteps.DeployedAsync(context, CopySteps.CloudSource(seed)).ConfigureAwait(false);
            var owned = AgentWorkspaceResources.List(copy.WorkspaceDirectory);
            var workspace = TreeSnapshot.Take(copy.WorkspaceDirectory);
            var deleted = await CopySteps.DeleteAsync(context, copy, allResources: true).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "agent delete --all-resources");
            context.Ledger.MarkDeleted(CopySteps.CloudAgentId(deployed));

            var result = deleted.Json<ManagedAgentDeleteResult>();
            context.Transcript.Note($"deleted resources: {string.Join(", ", result.Resources.Select(resource => $"{resource.Kind} {resource.Identity}"))}");
            context.Assert.NoDifferences([.. result.Resources.Where(resource => !owned.Any(row => row.Kind == resource.Kind && string.Equals(row.Identity, resource.Identity, StringComparison.OrdinalIgnoreCase))).Select(resource => $"{resource.Kind} {resource.Identity} is not named by the copy's workspace")], "the delete removes only resources the copy's workspace names");
            context.Assert.NoDifferences([.. owned.Where(row => row.Kind is CopyIdentityKinds.Workflow or CopyIdentityKinds.AIModel or CopyIdentityKinds.Connector && !result.Resources.Any(resource => resource.Kind == row.Kind && string.Equals(resource.Identity, row.Identity, StringComparison.OrdinalIgnoreCase))).Select(row => $"{row.Kind} {row.Identity} is not deleted")], "the delete removes every flow, prompt, and custom connector of the copy");
            await AssertCopyRemovedAsync(context, copy, deployed).ConfigureAwait(false);
            await CopySteps.AssertWorkflowsExistAsync(context, copy.EnvironmentId, copy.Identities.Where(identity => identity.Kind == CopyIdentityKinds.Workflow).Select(identity => identity.Source), "the source").ConfigureAwait(false);
            context.Assert.False(CopySteps.Registry(copy.ConfigPath).ManagedAgents.Any(agent => agent.AgentId == copy.AgentId), "the copy's record is gone");
            context.Assert.NoDifferences(TreeSnapshot.Take(copy.WorkspaceDirectory).DifferencesFrom(workspace), "the delete left the workspace's files, its cloud cache included, as they were");
            context.Assert.FailedCleanly(await CopySteps.DeleteAsync(context, copy, allResources: true).ConfigureAwait(false), "a second delete of the deleted copy");
            await CopySteps.AssertSourceIntactAsync(context, seed).ConfigureAwait(false);

            var workflowSeed = await CopySteps.WorkflowSeedAsync(context).ConfigureAwait(false);
            var (workflowCopy, workflowDeployed) = await CopySteps.DeployedAsync(context, CopySteps.CloudSource(workflowSeed), "workflow").ConfigureAwait(false);
            var workflowId = CopySteps.CloudWorkflowId(workflowDeployed);
            var workflowFiles = TreeSnapshot.Take(workflowCopy.WorkspaceDirectory);
            var workflowDeleted = await CopySteps.DeleteAsync(context, workflowCopy, allResources: false).ConfigureAwait(false);

            context.Assert.ExitZero(workflowDeleted, $"agent delete of the deployed workflow copy {workflowCopy.DisplayName}");
            context.Ledger.MarkDeleted(workflowId);

            var workflowResult = workflowDeleted.Json<ManagedAgentDeleteResult>();
            context.Assert.True(string.Equals(workflowResult.McsWorkflowId, workflowId, StringComparison.OrdinalIgnoreCase) && !workflowResult.McsAgentAlreadyAbsent && workflowResult.Resources.Count == 0, $"the delete of the workflow copy {workflowCopy.DisplayName} removes its flow and nothing else");
            await CopySteps.AssertWorkflowGoneAsync(context, workflowCopy.EnvironmentId, workflowId, $"the flow of the workflow copy {workflowCopy.DisplayName} is gone").ConfigureAwait(false);
            context.Assert.False(CopySteps.Registry(workflowCopy.ConfigPath).ManagedAgents.Any(agent => agent.AgentId == workflowCopy.AgentId), $"the record of the workflow copy {workflowCopy.DisplayName} is gone");
            context.Assert.NoDifferences(TreeSnapshot.Take(workflowCopy.WorkspaceDirectory).DifferencesFrom(workflowFiles), $"the delete left the files of the workflow copy {workflowCopy.DisplayName} as they were");
            context.Assert.FailedCleanly(await CopySteps.DeleteAsync(context, workflowCopy, allResources: false).ConfigureAwait(false), $"a second delete of the deleted workflow copy {workflowCopy.DisplayName}");
            await CopySteps.AssertWorkflowsExistAsync(context, workflowSeed.EnvironmentId, [workflowSeed.WorkflowId], $"the source of the workflow copy {workflowCopy.DisplayName}").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-83", "agent delete --all-resources of a deployed copy with a wrong confirmation is reported as a mismatch and deletes nothing", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var (copy, deployed) = await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false);
            var registry = File.ReadAllText(copy.ConfigPath);
            var mismatch = await CopySteps.DeleteAsync(context, copy, allResources: true, confirmation: "not the agent").ConfigureAwait(false);

            context.Assert.Contains(mismatch, "name-mismatch", "a wrong confirmation is reported as a mismatch");
            context.Assert.NoStackTrace(mismatch, "a wrong confirmation");
            context.Assert.Equal(registry, File.ReadAllText(copy.ConfigPath), "the mismatch left the records as they were");
            await CopySteps.AssertOneCloudAgentAsync(context, copy, deployed, "the mismatch deleted no agent").ConfigureAwait(false);
            await CopySteps.AssertWorkflowsExistAsync(context, copy.EnvironmentId, CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Workflow), "the copy").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-84", "agent delete --all-resources of a deployed copy turns off and deletes a copied workflow that was turned on", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var (copy, deployed) = await CopySteps.DeployedAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false))).ConfigureAwait(false);
            var workflow = CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Workflow)[0];
            var turnedOn = await WorkflowSteps.DeployCloneAsync(context, await WorkflowSteps.CloneAsync(context, workflow, "turned-on").ConfigureAwait(false)).ConfigureAwait(false);

            if (turnedOn.WorkflowState != WorkflowStateProjection.Published)
            {
                throw new ScenarioBlockedException($"the copied workflow {workflow} stayed {turnedOn.WorkflowState} when deployed on its own", "copied workflow cannot be turned on");
            }

            context.Assert.ExitZero(await CopySteps.DeleteAsync(context, copy, allResources: true).ConfigureAwait(false), "agent delete --all-resources with a turned-on copied workflow");
            context.Ledger.MarkDeleted(CopySteps.CloudAgentId(deployed));
            await AssertCopyRemovedAsync(context, copy, deployed).ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> MessyOrders()
    {
        yield return Builder.Make("MC-90", "deploy before deployment create, then create, deploy, and show, each answered as expected", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var copy = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false))).ConfigureAwait(false);

            context.Assert.FailedCleanly(await CopySteps.RawDeployAsync(context, copy).ConfigureAwait(false), "a deploy before deployment create");
            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, copy).ConfigureAwait(false), "deployment create");

            var deployed = await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);
            await CopySteps.AssertFirstDeployAsync(context, copy, deployed).ConfigureAwait(false);

            var shown = await CopySteps.ShowAgentAsync(context, copy).ConfigureAwait(false);
            context.Assert.ExitZero(shown, $"agent show {copy.DisplayName}");
            context.Assert.Equal(CopySteps.CloudAgentId(deployed), shown.Json<ManagedAgentShowResult>().Agents.SingleOrDefault()?.McsAgentId, "agent show names the copy's own cloud agent");

            var text = await CopySteps.ShowAgentAsync(context, copy, json: false).ConfigureAwait(false);
            context.Assert.ExitZero(text, $"agent show {copy.DisplayName} as text");
            context.Assert.Contains(text, CopySteps.CloudAgentId(deployed), "agent show prints the copy's own cloud agent");

            var deployment = await CopySteps.ShowDeploymentAsync(context, copy).ConfigureAwait(false);
            context.Assert.ExitZero(deployment, $"deployment show {copy.DisplayName}");
            context.Assert.True(deployment.Json<AgentDeploymentShowResult>() is { LastDeployedOn: not null } shownDeployment && shownDeployment.AgentId == copy.AgentId, "deployment show reports the copy's deploy");
        });

        yield return Builder.Make("MC-91", "every step run twice: copy, deployment create, deploy, and delete", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var source = await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false);
            string[] arguments = [.. CopySteps.LocalSource(source), "--agent-name", CopySteps.NewName(context, "twice")];
            var first = await CopySteps.RawCopyAsync(context, arguments).ConfigureAwait(false);

            context.Assert.ExitZero(first, "the first copy");

            var copy = CopySteps.RequireHome(context, first.Json<ManagedAgentCopyResult>());
            var copied = TreeSnapshot.Take(copy.ProjectDirectory);

            context.Assert.FailedCleanly(await CopySteps.RawCopyAsync(context, arguments).ConfigureAwait(false), "the same copy again");
            context.Assert.NoDifferences(TreeSnapshot.Take(copy.ProjectDirectory).DifferencesFrom(copied), "the second copy left the first one's project as it was");
            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, copy).ConfigureAwait(false), "deployment create");
            context.Assert.FailedCleanly(await CopySteps.CreateDeploymentAsync(context, copy).ConfigureAwait(false), "deployment create again");

            var deployed = await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);
            await CopySteps.AssertRedeployedAsync(context, copy, deployed, await CopySteps.DeployAsync(context, copy).ConfigureAwait(false)).ConfigureAwait(false);

            context.Assert.ExitZero(await CopySteps.DeleteAsync(context, copy, allResources: true).ConfigureAwait(false), "the first delete of the deployed copy");
            context.Ledger.MarkDeleted(CopySteps.CloudAgentId(deployed));
            context.Assert.FailedCleanly(await CopySteps.DeleteAsync(context, copy, allResources: true).ConfigureAwait(false), "the second delete of the deleted copy");
            await CopySteps.AssertAgentGoneAsync(context, copy, CopySteps.CloudAgentId(deployed), "the copy's agent is gone").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-92", "a deployment deleted before the first deploy can be created again, and the copy then deploys as a new agent of its own", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var copy = await CopySteps.NeverDeployedCopyAsync(context).ConfigureAwait(false);

            context.Assert.ExitZero(await McsSteps.DeleteDeploymentAsync(context, CopySteps.WorkspaceOf(context, copy), CopySteps.DeploymentName, deleteCloudAgent: false).ConfigureAwait(false), "deployment delete before the first deploy");
            context.Assert.FailedCleanly(await CopySteps.RawDeployAsync(context, copy).ConfigureAwait(false), "a deploy with the deployment deleted");
            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, copy).ConfigureAwait(false), "deployment create again");
            await CopySteps.AssertFirstDeployAsync(context, copy, await CopySteps.DeployAsync(context, copy).ConfigureAwait(false)).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-93", "a deployed copy deleted with agent delete and registered again with agent init deploys as a new agent or says why, and never adopts its deleted agent", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            var (copy, first) = await CopySteps.DeployedAsync(context, CopySteps.CloudSource(seed), "reborn").ConfigureAwait(false);

            context.Assert.ExitZero(await context.Msagent.RunAsync(["agent", "delete", "--agent-name", copy.DisplayName], copy.ProjectDirectory, copy.DisplayName, TimeSpan.FromMinutes(10), cancellationToken: context.CancellationToken).ConfigureAwait(false), "agent delete of the deployed copy");
            await CopySteps.AssertAgentGoneAsync(context, copy, CopySteps.CloudAgentId(first), "the copy's agent is gone").ConfigureAwait(false);

            var initialised = await McsSteps.InitAsync(context, CopySteps.WorkspaceOf(context, copy)).ConfigureAwait(false);
            context.Transcript.Note($"agent init on the deleted copy's project exited {initialised.ExitCode}");

            if (initialised.ExitCode != 0)
            {
                context.Assert.FailedCleanly(initialised, "agent init on the deleted copy's project");
                return;
            }

            var reborn = copy with { AgentId = initialised.Json<ManagedAgentInitResult>().AgentId };
            context.Assert.ExitZero(await CopySteps.CreateDeploymentAsync(context, reborn).ConfigureAwait(false), "deployment create for the registered project");
            context.Ledger.MarkDeleted(CopySteps.CloudAgentId(first));

            var deployed = await CopySteps.RawDeployAsync(context, reborn).ConfigureAwait(false);
            context.Transcript.Note($"the deploy of the registered project exited {deployed.ExitCode}");

            if (deployed.ExitCode != 0)
            {
                context.Assert.FailedCleanly(deployed, "the deploy of the registered project");
                return;
            }

            var again = deployed.Json<ManagedAgentDeployResult>();
            context.Assert.True(again.TargetAgentCreated, "the deploy of the registered project creates an agent");
            context.Assert.False(string.Equals(CopySteps.CloudAgentId(again), CopySteps.CloudAgentId(first), StringComparison.OrdinalIgnoreCase), "the deploy never adopts the deleted agent");
            await CopySteps.AssertOneCloudAgentAsync(context, reborn, again, "exactly one agent carries the new id and the copy's schema name").ConfigureAwait(false);
        });

        yield return Builder.Make("MC-94", "agent pull refuses a never-deployed copy without touching it, then pulls the deployed copy around a local edit the next deploy sends", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var copy = await CopySteps.NeverDeployedCopyAsync(context).ConfigureAwait(false);
            var before = TreeSnapshot.Take(copy.ProjectDirectory);

            context.Assert.FailedCleanly(await CopySteps.PullAsync(context, copy).ConfigureAwait(false), "agent pull on a never-deployed copy");
            context.Assert.NoDifferences(TreeSnapshot.Take(copy.ProjectDirectory).DifferencesFrom(before), "the refused pull changed nothing");

            var deployed = await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);
            context.Assert.ExitZero(await CopySteps.PullAsync(context, copy).ConfigureAwait(false), "agent pull on the deployed copy");

            var marker = $"MC-94 local edit {context.RunSuffix}";
            var skill = Directory.EnumerateFiles(copy.WorkspaceDirectory, "SKILL.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
            File.AppendAllText(skill, $"{Environment.NewLine}{marker}{Environment.NewLine}");

            context.Assert.ExitZero(await CopySteps.PullAsync(context, copy).ConfigureAwait(false), "agent pull over a local edit");
            context.Assert.FileContains(skill, marker, "the pull kept the local edit");

            await CopySteps.DeployAsync(context, copy).ConfigureAwait(false);
            context.Assert.True(FileTree.FilesMentioning(await CopySteps.CloneAsync(context, CopySteps.CloudAgentId(deployed), copy.EnvironmentId, "edited").ConfigureAwait(false), [marker], CopySteps.CloneState).Count > 0, "the deploy sent the local edit");
        });

        yield return Builder.Make("MC-95", "every other command run against a never-deployed copy answers cleanly and changes nothing", Cli, SafetyTier.ReadOnly, run: async context => await SweepAsync(context, await CopySteps.NeverDeployedCopyAsync(context).ConfigureAwait(false), [NeverDeployedSweepCommand], []).ConfigureAwait(false));

        yield return Builder.Make("MC-96", "every other command run against a deployed copy answers cleanly and changes nothing", Cli, SafetyTier.Creating, timeout: Long, run: async context => await SweepAsync(context, (await CopySteps.DeployedCopyAsync(context).ConfigureAwait(false)).Copy, [], [".mcs"]).ConfigureAwait(false));

        yield return Builder.Make("MC-97", "a copy, a copy of it, and a copy of that all deploy, and deleting the middle one leaves the other two unchanged", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var first = await ChainedAsync(context, CopySteps.CloudSource(await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false)), "first").ConfigureAwait(false);
            var middle = await ChainedAsync(context, CopySteps.CloudSource(first.Seed), "middle").ConfigureAwait(false);
            var last = await ChainedAsync(context, CopySteps.CloudSource(middle.Seed), "last").ConfigureAwait(false);

            await CopySteps.BaselineAsync(context, first.Seed).ConfigureAwait(false);
            await CopySteps.BaselineAsync(context, last.Seed).ConfigureAwait(false);

            context.Assert.ExitZero(await McsSteps.DeleteDeploymentAsync(context, CopySteps.WorkspaceOf(context, middle.Copy), CopySteps.DeploymentName, deleteCloudAgent: true).ConfigureAwait(false), "deployment delete of the middle copy");
            await CopySteps.AssertAgentGoneAsync(context, middle.Copy, middle.Seed.CloudAgentId, "the middle copy's agent is gone").ConfigureAwait(false);
            await CopySteps.AssertSourceIntactAsync(context, first.Seed).ConfigureAwait(false);
            await CopySteps.AssertSourceIntactAsync(context, last.Seed).ConfigureAwait(false);
        });

        yield return Builder.Make("MC-98", "a never-deployed copy whose project folder was moved deploys from the new path, or says clearly why not and sends nothing", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var copy = await CopySteps.NeverDeployedCopyAsync(context, "before-move").ConfigureAwait(false);
            var moved = CopySteps.Relocated(copy, Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId, "after-move"), "project"));

            Directory.Move(copy.ProjectDirectory, moved.ProjectDirectory);

            var deployed = await CopySteps.RawDeployAsync(context, moved).ConfigureAwait(false);
            context.Transcript.Note($"the deploy from the moved project exited {deployed.ExitCode}");

            if (deployed.ExitCode == 0)
            {
                await CopySteps.AssertFirstDeployAsync(context, moved, deployed.Json<ManagedAgentDeployResult>()).ConfigureAwait(false);
                return;
            }

            context.Assert.FailedCleanly(deployed, "the deploy from the moved project");
            await CopySteps.AssertNothingSentAsync(context, moved, "the refused deploy sent nothing").ConfigureAwait(false);
        });
    }

    private static IEnumerable<Scenario> Concurrency()
    {
        yield return Builder.Make("MC-102", "two concurrent copies into one project: a copy that loses the race is refused as copy-busy and writes nothing, and the registry names every copy that succeeded", Cli, SafetyTier.ReadOnly, run: async context =>
        {
            var source = await CopySteps.CopyAsync(context, CopySteps.LocalSource(await CopySteps.FixtureSourceAsync(context).ConfigureAwait(false)), "contested").ConfigureAwait(false);
            var project = source.ProjectDirectory;
            string[] labels = ["left", "right"];
            var results = await Task.WhenAll(labels.Select(label => CopySteps.RawCopyAsync(context, [.. CopySteps.LocalSource(source), "--agent-name", CopySteps.NewName(context, label)]))).ConfigureAwait(false);

            AssertRaced(context, results, "a concurrent copy");
            context.Assert.True(results.Any(result => result.ExitCode == 0), "at least one concurrent copy succeeds");

            foreach (var copy in results.Where(result => result.ExitCode == 0).Select(result => result.Json<ManagedAgentCopyResult>()))
            {
                CopySteps.AssertRegistered(context, copy, deployed: null);
                context.Assert.True(Directory.Exists(copy.WorkspaceDirectory), $"{copy.DisplayName}'s workspace is written");
            }

            foreach (var (label, refused) in labels.Zip(results).Where(pair => pair.Second.ExitCode != 0))
            {
                context.Assert.Refused(refused, CopySteps.Failure, "copy-busy", $"the {label} copy that lost the race");
                context.Assert.False(Directory.Exists(Path.Combine(project, WorkspaceFolderNames.ForProject(CopySteps.NewName(context, label)))), $"the {label} copy that lost the race wrote no workspace");
            }

            context.Assert.True(CopySteps.Registry(source.ConfigPath).ManagedAgents.Count == 1 + results.Count(result => result.ExitCode == 0), "the registry names the source and only the copies that succeeded");
            context.Assert.NoDifferences(CopySteps.LeftInCopyState(project), ".config\\copy holds only .gitignore and copy.lock");
        });

        yield return Builder.Make("MC-104", "four concurrent cloud copies of one agent get distinct identities and leave the source unchanged", Cli, SafetyTier.Creating, timeout: Long, run: async context =>
        {
            var seed = await CopySteps.FixtureSeedAsync(context, SessionAgentKind.FixtureCli).ConfigureAwait(false);
            await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false);

            var projects = Enumerable.Range(1, 4).Select(index => (Label: $"parallel{index}", Project: context.Workspaces.EmptyDirectory(context.ScenarioId, $"parallel{index}"))).ToArray();
            var results = await Task.WhenAll(projects.Select(target => CopySteps.RawCopyAsync(context, [.. CopySteps.CloudSource(seed), "--agent-name", CopySteps.NewName(context, target.Label), "--project", target.Project]))).ConfigureAwait(false);

            context.Assert.True(results.All(result => result.ExitCode == 0), $"every concurrent copy succeeds (exits {string.Join(", ", results.Select(result => result.ExitCode))})");

            var copies = results.Select(result => result.Json<ManagedAgentCopyResult>()).ToArray();
            context.Assert.NoDifferences([.. copies.SelectMany(copy => copy.Identities.Select(identity => identity.Destination).Append(copy.SchemaName).Distinct(StringComparer.OrdinalIgnoreCase)).GroupBy(identity => identity, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => $"{group.Key} is given to {group.Count()} copies")], "no two concurrent copies get the same identity");
            await CopySteps.AssertSourceIntactAsync(context, seed).ConfigureAwait(false);
        });
    }

    private static void AssertRaced(ScenarioContext context, IEnumerable<MsagentResult> results, string check)
    {
        foreach (var result in results)
        {
            context.Transcript.Note($"{check}: `{string.Join(' ', result.Arguments.Take(3))}` exited {result.ExitCode}");
            context.Assert.True(result.ExitCode is 0 or CopySteps.Failure or CopySteps.Usage, $"{check} exits 0, 1, or 2, not {result.ExitCode}");
            context.Assert.NoStackTrace(result, check);
        }
    }

    private static async Task AssertCopyRemovedAsync(ScenarioContext context, ManagedAgentCopyResult copy, ManagedAgentDeployResult deployed)
    {
        await CopySteps.AssertAgentGoneAsync(context, copy, CopySteps.CloudAgentId(deployed), "the copy's agent is gone").ConfigureAwait(false);

        foreach (var workflow in CopySteps.OwnedIdentities(copy, CopyIdentityKinds.Workflow))
        {
            await CopySteps.AssertWorkflowGoneAsync(context, copy.EnvironmentId, workflow, $"the copy's workflow {workflow} is gone").ConfigureAwait(false);
        }
    }

    private static async Task SweepAsync(ScenarioContext context, ManagedAgentCopyResult copy, string[][] extraCommands, string[] excludedFolders)
    {
        var before = TreeSnapshot.Take(copy.ProjectDirectory, excludedFolders);
        string[][] commands = [.. SweepCommands.Concat(extraCommands).Select(command => command.Select(argument => argument == AgentNamePlaceholder ? copy.DisplayName : argument).ToArray())];

        await EachAsync(context, "every command answers with exit 0, 1, or 2 and no stack trace", commands, command => string.Join(' ', command), async command =>
        {
            var result = await context.Msagent.RunAsync([.. command, "--json", "--non-interactive"], copy.ProjectDirectory, timeout: TimeSpan.FromMinutes(5), cancellationToken: context.CancellationToken).ConfigureAwait(false);

            context.Assert.True(result.ExitCode is 0 or CopySteps.Failure or CopySteps.Usage, $"`{string.Join(' ', command)}` exits 0, 1, or 2, not {result.ExitCode}");

            if (result.ExitCode == 0)
            {
                context.Assert.NoStackTrace(result, string.Join(' ', command));
            }
            else
            {
                context.Assert.FailedCleanly(result, string.Join(' ', command));
            }
        }).ConfigureAwait(false);

        context.Assert.NoDifferences(TreeSnapshot.Take(copy.ProjectDirectory, excludedFolders).DifferencesFrom(before), "the commands left the copy's project as it was");
    }

    private static async Task<(ManagedAgentCopyResult Copy, CopySeed Seed)> ChainedAsync(ScenarioContext context, IReadOnlyList<string> source, string label)
    {
        var (copy, deployed) = await CopySteps.DeployedAsync(context, source, label).ConfigureAwait(false);

        return (copy, CopySteps.SeedOf(label, copy, deployed));
    }

    private static IReadOnlyList<string> ConnectorProperties(string directory, string property) => [.. Directory.EnumerateFiles(directory, "metadata.yml", SearchOption.AllDirectories).Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))), "connectors", StringComparison.OrdinalIgnoreCase)).Select(path => ConnectorProperty(path, property))];

    private static string ConnectorProperty(string metadataPath, string property)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));

        return metadata.RootElement.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static void AssertPushedEveryDeployedComponent(ScenarioContext context, ManagedAgentDeployResult deployed, string clone, string agent)
    {
        using var definition = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(clone, "botdefinition.json", SearchOption.AllDirectories).Single()));
        var deployedComponents = definition.RootElement.TryGetProperty("components", out var components) ? components.GetArrayLength() : 0;

        context.Assert.True(deployed.PushedComponentCount == deployedComponents, $"pushedComponentCount counts every component the deployed {agent} holds ({deployed.PushedComponentCount} pushed, {deployedComponents} deployed)");
    }

    private static async Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed, string Clone)> FirstDeployAsync(ScenarioContext context, CloudSeed seed, IReadOnlyList<string> source, string label = "copy", string? newName = null)
    {
        await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false);

        var deployed = await CopySteps.VerifiedFirstDeployAsync(context, source, label, newName).ConfigureAwait(false);
        await CopySteps.AssertSourceIntactAsync(context, seed).ConfigureAwait(false);

        return deployed;
    }

    private static async Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed)> PublishedFirstDeployAsync(ScenarioContext context, CloudSeed seed, IReadOnlyList<string> source, string label = "copy")
    {
        await CopySteps.BaselineAsync(context, seed).ConfigureAwait(false);

        var copy = await CopySteps.RegisteredAsync(context, source, label).ConfigureAwait(false);
        var deployed = await CopySteps.DeployAsync(context, copy, "--publish").ConfigureAwait(false);

        context.Assert.True(deployed.Published && deployed.PublishedOn is not null, CopySteps.Labelled(copy, "the copy was published"));
        await CopySteps.AssertFirstDeployAsync(context, copy, deployed).ConfigureAwait(false);
        await CopySteps.AssertSourceIntactAsync(context, seed).ConfigureAwait(false);

        return (copy, deployed);
    }

    private static async Task AssertCopiesOfCopiesAsync(ScenarioContext context, IReadOnlyList<string> source, Func<Task<(ManagedAgentCopyResult Copy, ManagedAgentDeployResult Deployed)>> deployedCopyAsync, string labelPrefix = "")
    {
        var neverDeployed = await CopySteps.CopyAsync(context, source, $"{labelPrefix}first").ConfigureAwait(false);
        var neverDeployedBefore = TreeSnapshot.Take(neverDeployed.WorkspaceDirectory);
        var neverDeployedRecord = JsonSerializer.Serialize(CopySteps.Registry(neverDeployed.ConfigPath).ManagedAgents.Single(agent => agent.AgentId == neverDeployed.AgentId));
        AssertFreshIdentities(context, neverDeployed, await CopySteps.CopyAsync(context, CopySteps.LocalSource(neverDeployed), $"{labelPrefix}of-never-deployed").ConfigureAwait(false));
        context.Assert.NoDifferences(TreeSnapshot.Take(neverDeployed.WorkspaceDirectory).DifferencesFrom(neverDeployedBefore), CopySteps.Labelled(neverDeployed, "the never-deployed copy's workspace is byte-identical"));
        context.Assert.Equal(neverDeployedRecord, JsonSerializer.Serialize(CopySteps.Registry(neverDeployed.ConfigPath).ManagedAgents.Single(agent => agent.AgentId == neverDeployed.AgentId)), CopySteps.Labelled(neverDeployed, "the never-deployed copy's record is unchanged"));

        var (deployedCopy, deployed) = await deployedCopyAsync().ConfigureAwait(false);
        var deployedBefore = TreeSnapshot.Take(deployedCopy.ProjectDirectory);
        AssertFreshIdentities(context, deployedCopy, await CopySteps.CopyAsync(context, CopySteps.CloudSource(deployedCopy, deployed), $"{labelPrefix}of-deployed").ConfigureAwait(false));
        context.Assert.NoDifferences(TreeSnapshot.Take(deployedCopy.ProjectDirectory).DifferencesFrom(deployedBefore), CopySteps.Labelled(deployedCopy, "the deployed copy's project is byte-identical"));
    }

    public static string Shape(string directory) => string.Join(", ", TreeSnapshot.Take(directory, CopySteps.CloneState).Files.Keys.Select(ShapeOf).OfType<string>().Concat(Enumerable.Repeat("connector", ConnectorProperties(directory, "connectorinternalid").Distinct(StringComparer.OrdinalIgnoreCase).Count())).GroupBy(kind => kind, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Key}={group.Count()}"));

    private static string? ShapeOf(string relativePath) => Path.GetFileName(relativePath) switch
    {
        "prompt.json" => "prompt",
        "workflow.json" => "workflow",
        "agent.mcs.yml" => "agent",
        "SKILL.md" => "skill",
        var name when name.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase) => "component",
        _ when relativePath.Split(Path.DirectorySeparatorChar).Contains("knowledge", StringComparer.OrdinalIgnoreCase) => "knowledge",
        _ => null,
    };

    private static IReadOnlyList<string> RelativeFiles(string workspace) => [.. TreeSnapshot.Take(workspace, CopySteps.CloneState).Files.Keys];

    private static string? BoundAgentId(string workspaceDirectory)
    {
        var path = Path.Combine(workspaceDirectory, ".mcs", "conn.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var binding = JsonDocument.Parse(File.ReadAllText(path));
        return binding.RootElement.TryGetProperty("AgentId", out var agentId) ? agentId.GetString() : null;
    }

    private static void AssertFreshIdentities(ScenarioContext context, ManagedAgentCopyResult source, ManagedAgentCopyResult copy)
    {
        var (sourceIdentity, recordedSourceIdentity, ownIdentity, identityName) = CopySteps.IsWorkflow(copy) ? (source.McsWorkflowId, copy.SourceMcsWorkflowId, copy.McsWorkflowId, "flow id") : (source.SchemaName, copy.SourceSchemaName, copy.SchemaName, "schema name");

        context.Assert.Equal(sourceIdentity, recordedSourceIdentity, $"{copy.DisplayName} records {source.DisplayName} as its source");
        context.Assert.False(string.Equals(sourceIdentity, ownIdentity, StringComparison.OrdinalIgnoreCase), $"{copy.DisplayName} gets a {identityName} of its own");
        context.Assert.NoDifferences([.. copy.Identities.Select(identity => identity.Destination).Intersect(source.Identities.Select(identity => identity.Destination), StringComparer.OrdinalIgnoreCase)], $"{copy.DisplayName} reuses no identity of {source.DisplayName}");
    }

    private static void RefusedAs(ScenarioContext context, MsagentResult result, string errorKind, int errorCode, string fragment, string check)
    {
        context.Assert.Refused(result, CopySteps.Usage, fragment, check);
        context.Assert.True(result.JsonElement() is { ValueKind: JsonValueKind.Object } envelope && envelope.TryGetProperty("errorKind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == errorKind && envelope.TryGetProperty("errorCode", out var code) && code.TryGetInt32(out var value) && value == errorCode, $"{check}: refused as {errorKind} ({errorCode})");
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new E2EException("cmd.exe did not start.");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new ScenarioBlockedException($"mklink /J could not link {link}: {error.Trim()}", "junction unavailable");
        }
    }

    private static async Task RefuseAsync(ScenarioContext context, bool json, IEnumerable<ArgumentRefusal>? refusals = null)
    {
        var directory = context.Workspaces.EmptyDirectory(context.ScenarioId, json ? "json" : "text");

        foreach (var refusal in refusals ?? ArgumentRefusals.Where(candidate => candidate.ScenarioId == context.ScenarioId))
        {
            var result = await CopySteps.RawCopyAsync(context, refusal.Arguments, directory, json: json).ConfigureAwait(false);
            context.Assert.Refused(result, CopySteps.Usage, refusal.Fragment, json ? refusal.Check : $"{refusal.Check}, as text");

            if (!json)
            {
                context.Assert.False(result.StdOut.TrimStart().StartsWith('{'), $"{refusal.Check}, as text: stdout holds no JSON");
            }
        }

        context.Assert.False(Directory.EnumerateFileSystemEntries(directory).Any(), "no refusal wrote to the working directory");
    }

    private static async Task EachAsync<T>(ScenarioContext context, string check, IEnumerable<T> cases, Func<T, string> name, Func<T, Task> verify)
    {
        var failures = new List<string>();

        foreach (var item in cases)
        {
            try
            {
                await verify(item).ConfigureAwait(false);
            }
            catch (AssertionException failure)
            {
                failures.Add($"{name(item)}: {failure.Message}");
            }
        }

        context.Assert.NoDifferences(failures, check);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HashSet<string> Options(Regex pattern, string text) => [.. pattern.Matches(text).Where(match => !GlobalOptions.Contains(match.Groups["option"].Value, StringComparer.Ordinal) && !match.Groups["rest"].Value.Contains("**Gated**", StringComparison.Ordinal)).Select(match => $"{match.Groups["option"].Value} {match.Groups["alias"].Value}".TrimEnd())];

    private static string Section(string document, string heading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        var end = start < 0 ? -1 : document.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);

        return start < 0 ? string.Empty : end < 0 ? document[start..] : document[start..end];
    }

    [GeneratedRegex(@"^\s+(?:(?<alias>-[^\s,-][^\s,]*)\s+)?(?<option>--[a-z][a-z0-9-]*)\s", RegexOptions.Multiline)]
    private static partial Regex HelpOption();

    [GeneratedRegex(@"^\|\s*`(?<option>--[a-z][a-z0-9-]*)`\s*\|\s*(?:`(?<alias>-[^`]+)`)?\s*\|(?<rest>.*)$", RegexOptions.Multiline)]
    private static partial Regex DocumentedOption();

    [GeneratedRegex("^[A-Za-z0-9 _-]+$")]
    private static partial Regex ConnectorDisplayName();

    private sealed record HelpTopic(string[] Command, string Document, string Heading);

    private sealed record ArgumentRefusal(string ScenarioId, string Check, string[] Arguments, string Fragment);

    private sealed record CorpusFolder(string BringUpId, string CopiesId, string Name, string Description, bool ChecksPushedComponentCount = false);
}
