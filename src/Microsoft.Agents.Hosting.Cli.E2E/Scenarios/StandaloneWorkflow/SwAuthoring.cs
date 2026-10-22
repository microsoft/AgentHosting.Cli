using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwAuthoring
{
    private sealed record NameCase(string Label, string Name, bool MustSucceed = false, bool MustRefuse = false, string? ErrorKind = null, string? ProjectFolder = null);

    public static IEnumerable<Scenario> All() =>
    [
        SwScenario.Make("SW-A01", "scaffold writes one folder and one record, and nothing in the cloud", SwScenario.Cloud, SafetyTier.ReadOnly, ScaffoldLayoutAsync, smoke: true),
        SwScenario.Make("SW-A02", "scaffold honours --project given absolute, relative, or not yet created", SwScenario.Local, SafetyTier.ReadOnly, ScaffoldProjectAsync),
        SwScenario.Make("SW-A03", "awkward names are scaffolded into usable folders inside the project or refused before any write", SwScenario.Local, SafetyTier.ReadOnly, NameEdgeCasesAsync),
        SwScenario.Make("SW-A04", "a duplicate or case-variant name is refused and writes nothing", SwScenario.Local, SafetyTier.ReadOnly, DuplicateNamesAsync),
        SwScenario.Make("SW-A06", "init refusals write nothing", SwScenario.Local, SafetyTier.Reversible, InitRefusalsAsync),
        SwScenario.Make("SW-A07", "a record named differently from its workflow deploys and deletes consistently", SwScenario.Cloud, SafetyTier.Creating, InitNameMismatchAsync),
        SwScenario.Make("SW-A08", "a multi-record project never acts on an arbitrary record", SwScenario.Cloud, SafetyTier.Creating, MultiRecordProjectAsync),
        SwScenario.Make("SW-A09", "deployment slot names and stages are validated, and refusals write nothing", SwScenario.Local, SafetyTier.Reversible, SlotRulesAsync),
        SwScenario.Make("SW-A10", "init refuses a workflow folder that belongs to an agent project", SwScenario.Local, SafetyTier.Reversible, AgentOwnedFolderAsync),
    ];

    private static async Task ScaffoldLayoutAsync(SwRun run)
    {
        var workspace = await SwSteps.ScaffoldAsync(run).ConfigureAwait(false);

        run.Expect(workspace.Folders.Count == 1, "the scaffold writes exactly one workflow folder", "1", $"{workspace.Folders.Count}");
        run.Expect(File.Exists(workspace.DefinitionPath) && File.Exists(workspace.MetadataPath), "the folder holds workflow.json and metadata.yml", "both files", "missing");
        run.Expect(string.Equals(SwFixtures.MetadataValue(workspace.MetadataPath, "workflowId"), workspace.WorkflowId, StringComparison.OrdinalIgnoreCase),
            "metadata.yml carries the workflow id the command reported", workspace.WorkflowId, SwFixtures.MetadataValue(workspace.MetadataPath, "workflowId") ?? "none");
        run.Expect(string.Equals(SwFixtures.MetadataValue(workspace.MetadataPath, "name"), workspace.DisplayName, StringComparison.Ordinal),
            "metadata.yml carries the name given", workspace.DisplayName, SwFixtures.MetadataValue(workspace.MetadataPath, "name") ?? "none");
        run.Expect(!Directory.Exists(Path.Combine(workspace.ProjectDirectory, ".mcs")) && !Directory.Exists(Path.Combine(workspace.WorkflowRoot, ".mcs")),
            "a scaffold writes no .mcs cache", "no .mcs", "a .mcs directory");

        var records = SwChecks.Records(workspace.ProjectDirectory);
        run.Expect(records.Count == 1 && string.Equals(records[0].WorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase),
            "one record addresses the scaffolded workflow", "1 record", $"{records.Count} record(s)");
        run.Expect(SwChecks.Deployments(workspace.ProjectDirectory).Count == 0, "a scaffold registers no deployment slot", "0 slots", $"{SwChecks.Deployments(workspace.ProjectDirectory).Count}");
        SwChecks.ExpectConfigIntact(run, workspace.ProjectDirectory, "the configuration is well formed after a scaffold");

        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "a scaffold creates nothing in Dataverse", severity: "P0").ConfigureAwait(false);

        var shown = await SwSteps.ShowAsync(run, workspace.ProjectDirectory).ConfigureAwait(false);
        run.ExpectExit(shown, 0, "agent show reads the new record");

        var listed = await SwSteps.ListedAsync(run, workspace.ProjectDirectory).ConfigureAwait(false);
        run.Expect(!listed.Agents.Any(agent => string.Equals(agent.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase)),
            "a never-deployed workflow is not listed", "absent", "listed");
    }

    private static async Task ScaffoldProjectAsync(SwRun run)
    {
        var root = run.Context.Workspaces.EmptyDirectory(run.Id, "proj");
        var cases = new (string Label, string Argument, string Expected)[]
        {
            ("absolute", Path.Combine(root, "absolute-target"), Path.Combine(root, "absolute-target")),
            ("relative", "relative-target", Path.Combine(root, "relative-target")),
            ("nested", Path.Combine(root, "x", "y", "nested-target"), Path.Combine(root, "x", "y", "nested-target")),
        };

        foreach (var (label, argument, expected) in cases)
        {
            var before = SwChecks.Snapshot(root);
            var result = await SwSteps.RawScaffoldAsync(run, root, SwFixtures.NameFor(run, label), "--project", argument).ConfigureAwait(false);

            run.ExpectExit(result, 0, $"--project given a {label} path scaffolds");
            run.Expect(File.Exists(Path.Combine(expected, ".config", "agent.config.json")), $"the {label} --project directory holds the record", expected, "no agent.config.json there");
            SwChecks.ExpectNothingOutside(run, before, root, expected, $"a {label} --project writes nothing outside it");
        }
    }

    private static async Task NameEdgeCasesAsync(SwRun run)
    {
        var root = run.Context.Workspaces.EmptyDirectory(run.Id, "names");
        var suffix = run.Context.RunSuffix;
        NameCase[] cases =
        [
            new("unicode", $"ahe2e-SW-A03-{suffix} Ünïcødé ✓", MustSucceed: true),
            new("emoji", "🚀🚀"),
            new("slash", $"ahe2e-SW-A03-{suffix} a/b"),
            new("backslash", $"ahe2e-SW-A03-{suffix} a\\b"),
            new("dotdot", "..\\..\\escaped"),
            new("dotdot-forward", "../../escaped"),
            new("reserved", "CON", MustSucceed: true, ProjectFolder: "CON_"),
            new("trailing-dot", $"ahe2e-SW-A03-{suffix} trailing.", MustSucceed: true, ProjectFolder: $"ahe2e-SW-A03-{suffix} trailing"),
            new("trailing-space", $"ahe2e-SW-A03-{suffix} trailing ", MustSucceed: true, ProjectFolder: $"ahe2e-SW-A03-{suffix} trailing"),
            new("len100", "ahe2e-" + new string('a', 94), MustSucceed: true),
            new("len101", "ahe2e-" + new string('a', 95), MustRefuse: true, ErrorKind: "workflow-name-invalid"),
            new("len255", "ahe2e-" + new string('a', 249), MustRefuse: true, ErrorKind: "workflow-name-invalid"),
            new("control", $"ahe2e-SW-A03-{suffix} tab\there", MustRefuse: true, ErrorKind: "workflow-name-invalid"),
            new("empty", string.Empty, MustRefuse: true),
            new("blank", "   ", MustRefuse: true),
        ];

        foreach (var (label, name, mustSucceed, mustRefuse, errorKind, projectFolder) in cases)
        {
            var directory = Path.Combine(root, label);
            Directory.CreateDirectory(directory);

            var before = SwChecks.Snapshot(root);
            var result = await SwSteps.RawScaffoldAsync(run, directory, name).ConfigureAwait(false);

            run.Expect(result.ExitCode is 0 or ExitCodes.Usage, $"'{label}' is accepted or refused as a usage error, never a crash", "exit 0 or 2", $"exit {result.ExitCode}: {SwRun.Tail(result)}", "P1", "#24", result);
            SwChecks.ExpectNothingOutside(run, before, root, directory, $"'{label}' writes nothing outside the project");

            if (mustSucceed)
            {
                run.ExpectExit(result, 0, $"'{label}' is a valid name", "P2", "#24");
            }

            if (mustRefuse)
            {
                if (errorKind is null)
                {
                    run.ExpectExit(result, ExitCodes.Usage, $"'{label}' is refused before anything is written", "P2", "#24");
                }
                else
                {
                    run.ExpectFailure(result, ExitCodes.Usage, errorKind, $"'{label}' is refused as {errorKind} before anything is written", "P2", "#24");
                }

                SwChecks.ExpectUnchanged(run, before, root, $"the refused '{label}' wrote nothing", "P2", "#24");
            }

            if (result.ExitCode == 0)
            {
                SwChecks.ExpectConfigIntact(run, directory, $"'{label}' leaves a well-formed configuration");
                var created = result.Json<ManagedAgentCreateResult>();
                run.Expect(created.WorkflowFolder is { Length: > 0 } folder && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase),
                    $"'{label}' puts its workflow folder inside the project", directory, created.WorkflowFolder ?? "none", "P0", "#24");

                if (projectFolder is not null)
                {
                    var written = created.WorkflowFolder is { Length: > 0 } workflowFolder ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(workflowFolder))) : null;
                    var recorded = SwChecks.Records(directory).Select(record => record.Location).FirstOrDefault();
                    run.Expect(written == projectFolder && recorded == projectFolder, $"'{label}' gets the usable project folder '{projectFolder}', and its record points at it", projectFolder,
                        $"folder '{written ?? "none"}', record location '{recorded ?? "none"}'", "P2", "#24");
                }
            }
        }
    }

    private static async Task DuplicateNamesAsync(SwRun run)
    {
        var root = run.Context.Workspaces.EmptyDirectory(run.Id, "dup");
        var name = SwFixtures.NameFor(run);

        run.Assert.ExitZero(await SwSteps.RawScaffoldAsync(run, root, name).ConfigureAwait(false), "the first scaffold");

        var before = SwChecks.Snapshot(root);
        var exact = await SwSteps.RawScaffoldAsync(run, root, name).ConfigureAwait(false);
        run.ExpectExit(exact, ExitCodes.Usage, "a second workflow with the same name is refused", "P2", "#26");
        SwChecks.ExpectUnchanged(run, before, root, "the refused duplicate wrote nothing", "P1", "#26");

        before = SwChecks.Snapshot(root);
        var variant = await SwSteps.RawScaffoldAsync(run, root, name.ToUpperInvariant()).ConfigureAwait(false);
        run.ExpectExit(variant, ExitCodes.Usage, "a case-variant of an existing name is refused", "P2", "#26");
        SwChecks.ExpectUnchanged(run, before, root, "the refused case-variant wrote nothing", "P1", "#26");
        SwChecks.ExpectConfigIntact(run, root, "the configuration is well formed after the refusals");
    }

    private static async Task InitRefusalsAsync(SwRun run)
    {
        var empty = run.Context.Workspaces.EmptyDirectory(run.Id, "empty");
        await ExpectInitRefusedAsync(run, empty, SwFixtures.NameFor(run, "empty"), "a directory with no workflow").ConfigureAwait(false);

        var twoFolders = SwFixtures.Materialize(run, SwShape.NoConnection, "two");
        DuplicateFolder(twoFolders);
        await ExpectInitRefusedAsync(run, twoFolders.ProjectDirectory, twoFolders.DisplayName, "a directory holding two workflows").ConfigureAwait(false);

        var registered = SwFixtures.Materialize(run, SwShape.NoConnection, "again");
        await SwSteps.InitAsync(run, registered).ConfigureAwait(false);
        await ExpectInitRefusedAsync(run, registered.ProjectDirectory, registered.DisplayName, "a workflow that is already registered").ConfigureAwait(false);
        await ExpectInitRefusedAsync(run, registered.ProjectDirectory, registered.DisplayName + "-other", "an already registered workflow under another name").ConfigureAwait(false);

        var nameless = SwFixtures.Materialize(run, SwShape.NoConnection, "nameless");
        await ExpectInitRefusedAsync(run, nameless.ProjectDirectory, null, "init without --agent-name").ConfigureAwait(false);

        var source = run.Context.Workspaces.EmptyDirectory(run.Id, "source");
        await ExpectInitRefusedAsync(run, source, SwFixtures.NameFor(run, "missing"), "--mcs-workflow-source naming a missing directory", "--mcs-workflow-source", Path.Combine(source, "missing")).ConfigureAwait(false);

        var file = Path.Combine(source, "not-a-directory.txt");
        await File.WriteAllTextAsync(file, "x", run.Token).ConfigureAwait(false);
        await ExpectInitRefusedAsync(run, source, SwFixtures.NameFor(run, "file"), "--mcs-workflow-source naming a file", "--mcs-workflow-source", file).ConfigureAwait(false);

        var halfWritten = SwFixtures.Materialize(run, SwShape.NoConnection, "half");
        File.Delete(halfWritten.DefinitionPath);
        await ExpectInitRefusedAsync(run, halfWritten.ProjectDirectory, halfWritten.DisplayName, "a workflow folder with no workflow.json").ConfigureAwait(false);

        var mismatched = SwFixtures.Materialize(run, SwShape.NoConnection, "idmismatch");
        SwFixtures.SetMetadataWorkflowId(mismatched, Guid.NewGuid().ToString());
        await ExpectInitRefusedAsync(run, mismatched.ProjectDirectory, mismatched.DisplayName, "a folder whose metadata id differs from its folder id").ConfigureAwait(false);
    }

    private static async Task ExpectInitRefusedAsync(SwRun run, string directory, string? name, string what, params string[] extra)
    {
        var before = SwChecks.Snapshot(directory);
        var result = await SwSteps.RawInitAsync(run, directory, name, extra).ConfigureAwait(false);

        run.ExpectExit(result, ExitCodes.Usage, $"init refuses {what} as a usage error", "P2");
        SwChecks.ExpectUnchanged(run, before, directory, $"init of {what} writes nothing");
    }

    private static async Task InitNameMismatchAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection, activated: true);
        var recordName = workspace.DisplayName + "-record";
        var metadataName = SwFixtures.MetadataValue(workspace.MetadataPath, "name");

        var initialised = await SwSteps.InitAsync(run, workspace, recordName).ConfigureAwait(false);
        ExpectNames(run, "init", initialised.WorkflowName, initialised.DisplayName, metadataName, recordName);

        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);
        var deployed = await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        ExpectNames(run, "deploy", deployed.WorkflowName, deployed.DisplayName, metadataName, recordName);

        var cloud = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the flow lands activated").ConfigureAwait(false);
        run.Expect(cloud is null || string.Equals(cloud.Name, deployed.WorkflowName, StringComparison.Ordinal), "the deploy's workflowName is the flow's name in Dataverse", cloud?.Name ?? "none", deployed.WorkflowName ?? "none", "P2");

        var row = (await SwSteps.ListedAsync(run, workspace.ProjectDirectory, null, "--filter", workspace.DisplayName).ConfigureAwait(false)).Agents
            .FirstOrDefault(agent => string.Equals(agent.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase));
        run.Expect(string.Equals(row?.DisplayName, deployed.WorkflowName, StringComparison.Ordinal), "agent list shows the flow under the workflowName the deploy reported", deployed.WorkflowName ?? "none", row?.DisplayName ?? "absent", "P2");

        var deleted = await SwSteps.DeleteAsync(run, workspace, recordName).ConfigureAwait(false);
        run.ExpectExit(deleted, 0, "agent delete accepts the record's own name as confirmation");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "typing the record name the delete asks for removes the flow").ConfigureAwait(false);
    }

    private static void ExpectNames(SwRun run, string verb, string? workflowName, string displayName, string? flowName, string recordName)
    {
        run.Expect(string.Equals(workflowName, flowName, StringComparison.Ordinal), $"{verb} reports the flow's own name as workflowName", flowName ?? "none", workflowName ?? "none", "P2");
        run.Expect(string.Equals(displayName, recordName, StringComparison.Ordinal), $"{verb} keeps the record name as displayName", recordName, displayName, "P2");
    }

    private static async Task MultiRecordProjectAsync(SwRun run)
    {
        var root = run.Context.Workspaces.EmptyDirectory(run.Id, "parent");
        var first = await ScaffoldIntoAsync(run, root, "one").ConfigureAwait(false);
        var second = await ScaffoldIntoAsync(run, root, "two").ConfigureAwait(false);

        await SwSteps.SlotAsync(run, first).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, second).ConfigureAwait(false);

        var before = SwChecks.Snapshot(root);

        SwSteps.RecordIntent(run, first, SwSteps.Slot, run.Dev);
        SwSteps.RecordIntent(run, second, SwSteps.Slot, run.Dev);

        var deploy = await SwSteps.RunAsync(run, ["agent", "deploy", "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], root, timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        run.ExpectExit(deploy, ExitCodes.Usage, "deploy without --agent-id in a two-record project is refused as ambiguous", "P1");

        var pull = await SwSteps.RunAsync(run, ["agent", "pull", "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], root).ConfigureAwait(false);
        run.ExpectExit(pull, ExitCodes.Usage, "pull without --agent-id in a two-record project is refused as ambiguous", "P1");

        var delete = await SwSteps.RunAsync(run, ["agent", "delete", "--deployment-name", SwSteps.Slot, "--json"], root, stdin: first.DisplayName).ConfigureAwait(false);
        run.ExpectExit(delete, ExitCodes.Usage, "delete without --agent-id in a two-record project is refused as ambiguous", "P0");

        var show = await SwSteps.RunAsync(run, ["deployment", "show", "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], root).ConfigureAwait(false);
        run.ExpectExit(show, ExitCodes.Usage, "deployment show by a name both records use is refused as ambiguous", "P2");

        SwChecks.ExpectUnchanged(run, before, root, "the ambiguous commands changed nothing");
        await SwChecks.ExpectCloudAbsentAsync(run, first.WorkflowId, "the ambiguous deploy created nothing for the first record").ConfigureAwait(false);
        await SwChecks.ExpectCloudAbsentAsync(run, second.WorkflowId, "the ambiguous deploy created nothing for the second record").ConfigureAwait(false);

        var deployed = await SwSteps.DeployAsync(run, first).ConfigureAwait(false);
        run.Expect(string.Equals(deployed.McsWorkflowId, first.WorkflowId, StringComparison.OrdinalIgnoreCase), "--agent-id deploys exactly the record it names", first.WorkflowId, deployed.McsWorkflowId ?? "none");
        await SwChecks.ExpectCloudAsync(run, first.WorkflowId, null, "the named record's flow exists").ConfigureAwait(false);
        await SwChecks.ExpectCloudAbsentAsync(run, second.WorkflowId, "the other record's flow was not created", severity: "P0").ConfigureAwait(false);
        SwChecks.ExpectConfigIntact(run, root, "the shared configuration is well formed");
    }

    private static async Task SlotRulesAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);

        var names = new (string Name, bool Valid, string Why, string Severity)[]
        {
            ("abcd", true, "a four-character name, the documented minimum", "P1"),
            (new string('a', 42), true, "a 42-character name, the documented maximum", "P1"),
            ("A-b_9", true, "a name using every documented character class", "P1"),
            ("abc", false, "a three-character name, below the documented minimum", "P2"),
            (new string('b', 43), false, "a 43-character name, above the documented 42", "P2"),
            ("has space", false, "a name with a space", "P2"),
            ("a/bcd", false, "a name with a slash", "P2"),
            ("abc.d", false, "a name with a dot", "P2"),
        };

        foreach (var (name, valid, why, severity) in names)
        {
            run.Assert.True(SwChecks.Deployments(workspace.ProjectDirectory).Count == 0, $"the record has no slot before {why} is tried");

            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var result = await SwSteps.RawSlotAsync(run, workspace, name).ConfigureAwait(false);
            ExpectNameVerdict(run, result, valid, "deployment create", why, severity, before, workspace.ProjectDirectory);

            if (result.ExitCode == 0)
            {
                await RemoveSlotAsync(run, workspace, name).ConfigureAwait(false);
            }
        }

        await SwSteps.SlotAsync(run, workspace, "abcd").ConfigureAwait(false);

        var refused = new (string Name, string Type, string? Environment, string Why)[]
        {
            ("abcde", "dev", null, "a second slot for the environment the record already has one for"),
            ("tst1", "test", null, "a test slot with no environment"),
            ("tst2", "test", Guid.NewGuid().ToString(), "a test slot naming an environment that does not exist"),
            ("tst3", "staging", null, "an unknown stage"),
        };

        foreach (var (name, type, environment, why) in refused)
        {
            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var result = await SwSteps.RawSlotAsync(run, workspace, name, type, environment).ConfigureAwait(false);

            run.ExpectRefused(result, $"deployment create refuses {why}", "P2");

            if (result.ExitCode != 0)
            {
                SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, $"the refused slot ({why}) wrote nothing");
            }
        }

        foreach (var (newName, valid, why) in new[] { ("wxyz", true, "renaming to a four-character name"), ("abc", false, "renaming to a three-character name"), ("has space", false, "renaming to a name with a space") })
        {
            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var renamed = await RenameSlotAsync(run, workspace, "abcd", newName).ConfigureAwait(false);
            ExpectNameVerdict(run, renamed, valid, "deployment update", why, "P2", before, workspace.ProjectDirectory);

            if (renamed.ExitCode == 0)
            {
                run.Assert.ExitZero(await RenameSlotAsync(run, workspace, newName, "abcd").ConfigureAwait(false), "renaming the slot back");
            }
        }

        run.ExpectExit(await RenameSlotAsync(run, workspace, "abcd", "abcde").ConfigureAwait(false), 0, "deployment update --new-name renames a slot");

        var shownNew = await SwSteps.RunAsync(run, ["deployment", "show", "--agent-id", workspace.Agent, "--deployment-name", "abcde", "--json", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);
        var shownOld = await SwSteps.RunAsync(run, ["deployment", "show", "--agent-id", workspace.Agent, "--deployment-name", "abcd", "--json", "--non-interactive"], workspace.ProjectDirectory).ConfigureAwait(false);
        run.ExpectExit(shownNew, 0, "the renamed slot resolves by its new name");
        run.ExpectRefused(shownOld, "the renamed slot no longer resolves by its old name");

        var expected = new List<string> { "abcde" };

        if (string.IsNullOrEmpty(run.Test))
        {
            run.Observe("slot names across environments", "not checked: no test environment is configured");
        }
        else
        {
            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var duplicate = await SwSteps.RawSlotAsync(run, workspace, "abcde", "test", run.Test).ConfigureAwait(false);
            run.ExpectRefused(duplicate, "a test slot reusing the dev slot's name is refused, the name being 'unique within the agent'", "P1");

            if (duplicate.ExitCode == 0)
            {
                var id = JsonDocument.Parse(duplicate.StdOut).RootElement.GetProperty("deploymentId").GetString() ?? string.Empty;
                var removed = await SwSteps.RunAsync(run, ["deployment", "delete", "--deployment-id", id, "--json"], workspace.ProjectDirectory, stdin: "abcde").ConfigureAwait(false);
                run.Assert.ExitZero(removed, "deployment delete of the duplicate slot");
            }
            else
            {
                SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, "the refused duplicate name wrote nothing");
            }

            var second = await SwSteps.RawSlotAsync(run, workspace, "tstx", "test", run.Test).ConfigureAwait(false);
            run.ExpectExit(second, 0, "a test slot for the second environment is accepted");

            if (second.ExitCode == 0)
            {
                expected.Add("tstx");
                before = SwChecks.Snapshot(workspace.ProjectDirectory);
                var clash = await RenameSlotAsync(run, workspace, "abcde", "tstx").ConfigureAwait(false);
                run.ExpectRefused(clash, "renaming a slot onto another slot's name is refused", "P1");
                SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, "the refused clashing rename wrote nothing");
            }
        }

        var slots = SwChecks.Deployments(workspace.ProjectDirectory).Select(slot => slot.DeploymentName ?? string.Empty).Order(StringComparer.Ordinal).ToArray();
        var wanted = expected.Order(StringComparer.Ordinal).ToArray();
        run.Expect(slots.SequenceEqual(wanted, StringComparer.Ordinal), "the configuration holds exactly the slots that were accepted", string.Join(", ", wanted), string.Join(", ", slots));
        SwChecks.ExpectConfigIntact(run, workspace.ProjectDirectory, "the configuration is well formed after the slot rules");
    }

    private static void ExpectNameVerdict(SwRun run, MsagentResult result, bool valid, string verb, string why, string severity, IReadOnlyDictionary<string, string> before, string projectDirectory)
    {
        if (valid)
        {
            run.ExpectExit(result, 0, $"{verb} accepts {why}", severity, "#27");
            return;
        }

        run.ExpectExit(result, ExitCodes.Usage, $"{verb} refuses {why}", severity, "#27");

        if (result.ExitCode != 0)
        {
            SwChecks.ExpectUnchanged(run, before, projectDirectory, $"the refused {verb} ({why}) wrote nothing");
        }
    }

    private static async Task RemoveSlotAsync(SwRun run, SwWorkspace workspace, string name) =>
        run.Assert.ExitZero(await SwSteps.DeleteSlotAsync(run, workspace, name).ConfigureAwait(false), $"deployment delete {name}");

    private static Task<MsagentResult> RenameSlotAsync(SwRun run, SwWorkspace workspace, string name, string newName) =>
        SwSteps.RunAsync(run, ["deployment", "update", "--agent-id", workspace.Agent, "--deployment-name", name, "--new-name", newName, "--json", "--non-interactive"], workspace.ProjectDirectory);

    private static async Task AgentOwnedFolderAsync(SwRun run)
    {
        var project = SwFixtures.MaterializeAgentProject(run, "agent");
        var before = SwChecks.Snapshot(project);

        var inside = await SwSteps.RawInitAsync(run, project, SwFixtures.NameFor(run, "inside")).ConfigureAwait(false);
        run.ExpectRefused(inside, "init as a standalone workflow inside an agent project is refused", "P1", "#5");
        SwChecks.ExpectUnchanged(run, before, project, "the refused init inside an agent project wrote nothing", "P1", "#5");

        var outside = run.Context.Workspaces.EmptyDirectory(run.Id, "outside");
        var sourced = await SwSteps.RawInitAsync(run, outside, SwFixtures.NameFor(run, "sourced"), "--mcs-workflow-source", project).ConfigureAwait(false);
        run.ExpectRefused(sourced, "init pointing --mcs-workflow-source at an agent project is refused", "P1", "#5");
        SwChecks.ExpectUnchanged(run, before, project, "the refused sourced init wrote nothing into the agent project", "P1", "#5");
    }

    internal static async Task<SwWorkspace> ScaffoldIntoAsync(SwRun run, string root, string label)
    {
        var result = await SwSteps.RawScaffoldAsync(run, root, SwFixtures.NameFor(run, label)).ConfigureAwait(false);
        run.Assert.ExitZero(result, $"scaffold '{label}' into the shared project");

        var created = result.Json<ManagedAgentCreateResult>();

        return new SwWorkspace
        {
            Shape = SwShape.Scaffold,
            Root = root,
            ProjectDirectory = root,
            WorkflowRoot = Path.GetDirectoryName(Path.GetDirectoryName(created.WorkflowFolder!)!)!,
            WorkflowId = created.McsWorkflowId!,
            DisplayName = created.DisplayName,
            AgentId = created.AgentId,
            EnvironmentId = created.EnvironmentId,
        };
    }

    internal static void DuplicateFolder(SwWorkspace workspace)
    {
        var copyId = Guid.NewGuid();
        var copyName = workspace.DisplayName + "-copy";
        var target = Path.Combine(workspace.WorkflowsDirectory, WorkflowWorkspace.FolderNameFor(copyName, copyId));

        FixtureCopies.CopyTree(workspace.FolderPath, target, keepLocalState: false);
        FixtureCopies.Reidentify(target, copyName, copyId);
    }
}
