using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwRoundTrip
{
    public static IEnumerable<Scenario> All() =>
    [
        SwScenario.Make("SW-D01", "a clone of each deployed shape matches what was deployed", SwScenario.Cloud, SafetyTier.Creating, CloneParityAsync),
        SwScenario.Make("SW-D02", "clone refusals write no folder and no record", SwScenario.Cloud, SafetyTier.Creating, CloneRefusalsAsync),
        SwScenario.Make("SW-D03", "a flow made outside the CLI clones, and a re-identified copy of it deploys", SwScenario.Cloud, SafetyTier.Creating, ForeignFlowAsync),
        SwScenario.Make("SW-D05", "a second operator's edit arrives with a pull", SwScenario.Cloud, SafetyTier.Creating, RemoteEditAsync),
        SwScenario.Make("SW-D06", "a pull refuses to discard a local edit, and --overwrite replaces it", SwScenario.Cloud, SafetyTier.Creating, LocalEditAsync),
        SwScenario.Make("SW-D07", "a dev deploy refuses to replace a second operator's edit it has not pulled, and --overwrite replaces it", SwScenario.Cloud, SafetyTier.Creating, LostUpdateAsync),
        SwScenario.Make("SW-D08", "a pull after someone deleted the flow fails clearly and keeps local files", SwScenario.Cloud, SafetyTier.Creating, PullAfterDeleteAsync),
        SwScenario.Make("SW-D09", "a pull restores a deleted workflow folder", SwScenario.Cloud, SafetyTier.Creating, PullRestoresFolderAsync),
        SwScenario.Make("SW-D10", "a remote rename arrives as one folder, and a redeploy keeps it", SwScenario.Cloud, SafetyTier.Creating, RemoteRenameAsync),
        SwScenario.Make("SW-D12", "corrupt local JSON is never deployed, and pull --overwrite repairs it", SwScenario.Cloud, SafetyTier.Creating, CorruptDefinitionAsync),
    ];

    private static async Task CloneParityAsync(SwRun run)
    {
        foreach (var shape in new[] { SwShape.NoConnection, SwShape.ClassicAgentFlow, SwShape.CliNative })
        {
            var (workspace, _) = await SwSteps.DeployedAsync(run, shape, label: shape.ToString().ToLowerInvariant()).ConfigureAwait(false);
            var cloud = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, null, $"the {shape} flow exists").ConfigureAwait(false);
            var clone = await SwSteps.CloneAsync(run, workspace.WorkflowId, label: $"{shape.ToString().ToLowerInvariant()}-clone").ConfigureAwait(false);

            run.Expect(string.Equals(clone.WorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase), $"the {shape} clone addresses the cloned flow", workspace.WorkflowId, clone.WorkflowId);
            run.Expect(string.Equals(clone.DisplayName, workspace.DisplayName, StringComparison.Ordinal), $"the {shape} clone keeps the flow's name", workspace.DisplayName, clone.DisplayName);
            run.Expect(clone.HomeDeploymentName is { Length: > 0 }, $"the {shape} clone registers a home deployment", "a deployment", "none");
            run.Expect(clone.HomeDeploymentName is not { } home || (home.Length is >= 4 and <= 42 && home.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')),
                $"the {shape} clone's home deployment name follows the documented rule, 4-42 characters from -, a-z, A-Z, 0-9 and _", "a valid name", $"'{clone.HomeDeploymentName}'", "P2", "#27");

            var localStructure = SwChecks.Structure(await File.ReadAllTextAsync(workspace.DefinitionPath, run.Token).ConfigureAwait(false));
            var cloneStructure = SwChecks.Structure(await File.ReadAllTextAsync(clone.DefinitionPath, run.Token).ConfigureAwait(false));
            var difference = SwChecks.StructureDifference(localStructure, cloneStructure);
            run.Expect(difference is null, $"the {shape} clone carries every trigger, action and reference that was deployed", "the same structure", difference ?? string.Empty);

            var localReferences = SwFixtures.References(workspace).Keys.Order(StringComparer.Ordinal).ToArray();
            var cloneReferences = SwFixtures.References(clone).Keys.Order(StringComparer.Ordinal).ToArray();
            run.Expect(localReferences.SequenceEqual(cloneReferences, StringComparer.OrdinalIgnoreCase), $"the {shape} clone declares the same connection references",
                string.Join(", ", localReferences), string.Join(", ", cloneReferences));

            if (cloud is not null)
            {
                run.Expect(SwFixtures.ActivatedLocally(clone) == cloud.IsActivated, $"the {shape} clone's metadata records the state Dataverse holds",
                    cloud.IsActivated ? "stateCode 1" : "stateCode 0", $"{SwFixtures.MetadataValue(clone.MetadataPath, "stateCode")}");
            }

            SwChecks.ExpectConfigIntact(run, clone.ProjectDirectory, $"the {shape} clone's configuration is well formed");
        }
    }

    private static async Task CloneRefusalsAsync(SwRun run)
    {
        await ExpectCloneRefusedAsync(run, Guid.NewGuid().ToString(), run.Dev, "a workflow id that does not exist").ConfigureAwait(false);
        await ExpectCloneRefusedAsync(run, "not-a-guid", run.Dev, "a malformed workflow id", ExitCodes.Usage).ConfigureAwait(false);
        await ExpectCloneRefusedAsync(run, Guid.Empty.ToString(), run.Dev, "the empty workflow id").ConfigureAwait(false);

        if (await run.Services.Dataverse.ManagedFlowAsync(run.Dev, run.Token).ConfigureAwait(false) is { } managed)
        {
            await ExpectCloneRefusedAsync(run, managed.WorkflowId, run.Dev, "a managed flow", errorKind: "workflow-managed").ConfigureAwait(false);
        }
        else
        {
            run.Observe("cloning a managed flow is refused", "the environment holds no managed cloud flow to try");
        }

        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);

        var directory = run.Context.Workspaces.EmptyDirectory(run.Id, "twice");
        run.Assert.ExitZero(await SwSteps.RawCloneAsync(run, directory, workspace.WorkflowId, run.Dev).ConfigureAwait(false), "the first clone into the directory");

        var before = SwChecks.Snapshot(directory);
        var twice = await SwSteps.RawCloneAsync(run, directory, workspace.WorkflowId, run.Dev).ConfigureAwait(false);
        run.ExpectRefused(twice, "cloning the same flow into a directory that already holds it is refused", "P1", "#17");
        SwChecks.ExpectUnchanged(run, before, directory, "the refused second clone changed nothing", "P0");

        if (run.Context.Config.Mcs.TestEnvironmentId is { Length: > 0 } test)
        {
            await ExpectCloneRefusedAsync(run, workspace.WorkflowId, test, "a flow named in an environment that does not hold it").ConfigureAwait(false);
        }
    }

    private static async Task ExpectCloneRefusedAsync(SwRun run, string workflowId, string environmentId, string what, int? exitCode = null, string? errorKind = null)
    {
        var directory = run.Context.Workspaces.EmptyDirectory(run.Id, $"refuse{run.NextIndex()}");
        var result = await SwSteps.RawCloneAsync(run, directory, workflowId, environmentId).ConfigureAwait(false);

        if (errorKind is not null)
        {
            run.ExpectFailure(result, exitCode ?? ExitCodes.Usage, errorKind, $"cloning {what} is refused as {errorKind}");
        }
        else if (exitCode is { } code)
        {
            run.ExpectExit(result, code, $"cloning {what} is refused as exit {code}", "P2");
        }
        else
        {
            run.ExpectRefused(result, $"cloning {what} is refused");
        }

        var left = SwChecks.Snapshot(directory);
        run.Expect(left.Count == 0, $"cloning {what} leaves no folder or record behind", "an empty directory", string.Join(", ", left.Keys.Take(6)), "P1", "#17");
    }

    private static async Task ForeignFlowAsync(SwRun run)
    {
        var listed = await SwSteps.ListedAsync(run, null, null, "--limit", "100").ConfigureAwait(false);
        var foreign = listed.Agents.FirstOrDefault(agent =>
            agent.McsWorkflowId is { Length: > 0 }
            && !(agent.DisplayName ?? string.Empty).StartsWith("ahe2e", StringComparison.OrdinalIgnoreCase));

        if (foreign is null)
        {
            throw new ScenarioBlockedException("the environment holds no flow the suite did not make", "no foreign flow");
        }

        run.Note($"cloning the foreign flow '{foreign.DisplayName}' ({foreign.McsWorkflowId}) read-only; nothing is ever deployed or deleted through this clone");
        var clone = await SwSteps.CloneAsync(run, foreign.McsWorkflowId!, label: "foreign").ConfigureAwait(false);
        run.Expect(clone.Folders.Count == 1, "the foreign flow clones into one folder", "1", $"{clone.Folders.Count}");

        var copyRoot = run.Context.Workspaces.EmptyDirectory(run.Id, "copy");
        var copy = Reidentified(run, clone.WorkflowRoot, Path.Combine(copyRoot, "copy"));
        await SwSteps.InitAsync(run, copy).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, copy).ConfigureAwait(false);

        var deployed = await SwSteps.RawDeployAsync(run, copy).ConfigureAwait(false);
        run.ExpectExit(deployed, 0, "a re-identified copy of a cloned portal flow deploys");
        await SwChecks.ExpectCloudAsync(run, copy.WorkflowId, null, "the copy exists as a new flow").ConfigureAwait(false);

        var original = await SwChecks.CloudAsync(run, foreign.McsWorkflowId!).ConfigureAwait(false);
        run.Expect(original is null || string.Equals(original.Name, foreign.DisplayName, StringComparison.Ordinal), "the original flow was not touched", foreign.DisplayName ?? string.Empty, original?.Name ?? "none", "P0");
    }

    private static SwWorkspace Reidentified(SwRun run, string source, string project)
    {
        var name = SwFixtures.NameFor(run, "copy");
        var id = Guid.NewGuid();

        FixtureCopies.CopyWorkflow(source, project, name, id, keepLocalState: false);

        var workspace = new SwWorkspace
        {
            Shape = SwShape.Clone,
            Root = Path.GetDirectoryName(project)!,
            ProjectDirectory = project,
            WorkflowRoot = project,
            WorkflowId = id.ToString(),
            DisplayName = name,
        };

        IsolateCopiedReferences(run, workspace);
        return workspace;
    }

    private static void IsolateCopiedReferences(SwRun run, SwWorkspace workspace)
    {
        var references = SwFixtures.References(workspace);
        var ordinal = 0;

        foreach (var (logicalName, connectorId) in references)
        {
            ordinal++;
            var isolated = $"{StandaloneServices.ReferencePrefix}{run.Context.RunSuffix}_{run.Key}_c{ordinal}".ToLowerInvariant();

            foreach (var file in Directory.GetFiles(workspace.WorkflowRoot, "*", SearchOption.AllDirectories).Where(file => Path.GetExtension(file) is ".json" or ".yml" or ".yaml"))
            {
                var content = File.ReadAllText(file);

                if (content.Contains(logicalName, StringComparison.Ordinal))
                {
                    File.WriteAllText(file, content.Replace(logicalName, isolated, StringComparison.Ordinal));
                }
            }

            foreach (var file in Directory.GetFiles(workspace.DeclarationsDirectory).Where(file => Path.GetFileName(file).StartsWith(logicalName + ".", StringComparison.Ordinal)))
            {
                File.Move(file, Path.Combine(workspace.DeclarationsDirectory, isolated + Path.GetFileName(file)[logicalName.Length..]));
            }

            run.Note($"isolated reference {logicalName} ({connectorId}) as {isolated}");
        }
    }

    private static async Task RemoteEditAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var actor = await SwSteps.SecondActorAsync(run, workspace).ConfigureAwait(false);

        run.Observe("a clone attaches to the cloned flow", $"by design, as an agent clone does: the clone's record addresses {actor.WorkflowId}, the original, so every clone deploys to the one shared flow, and a dev deploy refuses to replace an edit it has not pulled (SW-D07)", "info", "#1");

        var marker = $"remote edit {run.Context.RunSuffix}";
        SwFixtures.SetDescription(actor, marker);
        await SwSteps.DeployAsync(run, actor, actor.HomeDeploymentName ?? "develop").ConfigureAwait(false);

        var landed = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false);
        run.Expect(landed is null || string.Equals(landed.Description, marker, StringComparison.Ordinal), "the second operator's edit reached Dataverse", marker, landed?.Description ?? "none");

        var pulled = await SwSteps.PullAsync(run, workspace).ConfigureAwait(false);
        run.ExpectExit(pulled, 0, "pull brings the remote edit");

        if (pulled.ExitCode == 0)
        {
            run.Expect(pulled.Json<ManagedAgentPullResult>().ChangeCount >= 1, "the pull reports the remote edit as a change", "at least 1", $"{pulled.Json<ManagedAgentPullResult>().ChangeCount}");
        }

        run.Expect(string.Equals(SwFixtures.MetadataValue(workspace.MetadataPath, "description"), marker, StringComparison.Ordinal), "the remote edit is on disk after the pull", marker,
            SwFixtures.MetadataValue(workspace.MetadataPath, "description") ?? "none");
    }

    private static async Task LocalEditAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.ClassicAgentFlow).ConfigureAwait(false);
        var original = SwFixtures.MetadataValue(workspace.MetadataPath, "description");

        SwFixtures.SetDescription(workspace, "local edit never deployed");
        var before = SwChecks.Snapshot(workspace.WorkflowRoot);

        var refused = await SwSteps.PullAsync(run, workspace).ConfigureAwait(false);
        run.ExpectExit(refused, ExitCodes.Usage, "an ordinary pull refuses to discard a local edit", "P0");
        SwChecks.ExpectUnchanged(run, before, workspace.WorkflowRoot, "the refused pull left the local edit alone", "P0");

        var overwritten = await SwSteps.PullAsync(run, workspace, SwSteps.Slot, "--overwrite").ConfigureAwait(false);
        run.ExpectExit(overwritten, 0, "pull --overwrite replaces the local edit");
        run.Expect(string.Equals(SwFixtures.MetadataValue(workspace.MetadataPath, "description"), original, StringComparison.Ordinal), "pull --overwrite restored the deployed description",
            original ?? "none", SwFixtures.MetadataValue(workspace.MetadataPath, "description") ?? "none");
        run.Expect(SwFixtures.References(workspace).Count == 1, "pull --overwrite kept the connection declaration", "1 declaration", $"{SwFixtures.References(workspace).Count}", "P0");
    }

    private static async Task LostUpdateAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var actor = await SwSteps.SecondActorAsync(run, workspace).ConfigureAwait(false);

        var remote = $"remote edit {run.Context.RunSuffix}";
        SwFixtures.SetDescription(actor, remote);
        await SwSteps.DeployAsync(run, actor, actor.HomeDeploymentName ?? "develop").ConfigureAwait(false);

        var landed = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false);
        run.Assert.True(landed is not null && string.Equals(landed.Description, remote, StringComparison.Ordinal), "the second operator's edit reached Dataverse");

        const string marker = "ahe2e_local_edit";
        var local = $"local edit {run.Context.RunSuffix}";
        SwFixtures.SetDescription(workspace, local);
        SwFixtures.AddMarkerAction(workspace, marker);
        var before = SwChecks.Snapshot(workspace.ProjectDirectory);

        var refused = await SwSteps.RawDeployAsync(run, workspace).ConfigureAwait(false);
        run.ExpectFailure(refused, ExitCodes.Usage, "workflow-cloud-changes-would-be-lost", "a dev deploy refuses to replace a second operator's edit it has not pulled", "P0", "#3");
        SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, "the refused deploy left the local edit and the records alone", "P0");

        var kept = await SwChecks.CloudAsync(run, workspace.WorkflowId, definition: true).ConfigureAwait(false);
        run.Expect(kept is not null && string.Equals(kept.Description, remote, StringComparison.Ordinal), "the refused deploy left the second operator's edit in Dataverse", remote, kept?.Description ?? "none", "P0");
        run.Expect(kept?.ClientData is not { } keptData || !SwChecks.Structure(keptData).Any(entry => entry.StartsWith($"action:Compose_{marker}:", StringComparison.Ordinal)),
            "the refused deploy wrote none of the local definition edit", $"no action Compose_{marker} in the cloud definition", "present", "P0");

        var overwritten = await SwSteps.RawDeployAsync(run, workspace, SwSteps.Slot, null, "--overwrite").ConfigureAwait(false);
        run.ExpectExit(overwritten, 0, "deploy --overwrite replaces the second operator's edit");

        var cloud = await SwChecks.CloudAsync(run, workspace.WorkflowId, definition: true).ConfigureAwait(false);
        run.Expect(cloud is not null && string.Equals(cloud.Description, local, StringComparison.Ordinal), "after --overwrite the cloud description is the local one, not the second operator's", local, cloud?.Description ?? "none", "P1");
        run.Expect(cloud?.ClientData is { } clientData && SwChecks.Structure(clientData).Any(entry => entry.StartsWith($"action:Compose_{marker}:", StringComparison.Ordinal)),
            "the local definition edit reached Dataverse with it", $"action Compose_{marker} in the cloud definition", "absent", "P1");
        await SwChecks.ExpectSingleFlowAsync(run, workspace.DisplayName, "the environment still holds one flow with the name").ConfigureAwait(false);
    }

    private static async Task PullAfterDeleteAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var actor = await SwSteps.SecondActorAsync(run, workspace).ConfigureAwait(false);

        run.Assert.ExitZero(await SwSteps.DeleteAsync(run, actor, actor.DisplayName).ConfigureAwait(false), "the second operator deletes the flow");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the flow is gone").ConfigureAwait(false);

        var before = SwChecks.Snapshot(workspace.WorkflowRoot);

        var pulled = await SwSteps.PullAsync(run, workspace).ConfigureAwait(false);
        run.ExpectRefused(pulled, "a pull of a flow that no longer exists fails");
        run.Expect(pulled.Output.Contains("not found", StringComparison.OrdinalIgnoreCase) || pulled.Output.Contains("no longer", StringComparison.OrdinalIgnoreCase)
            || pulled.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || pulled.Output.Contains("deleted", StringComparison.OrdinalIgnoreCase)
            || pulled.Output.Contains("no workflow with id", StringComparison.OrdinalIgnoreCase),
            "the failure says the flow is gone", "a message naming the missing flow", SwRun.Tail(pulled), "P2");
        SwChecks.ExpectUnchanged(run, before, workspace.WorkflowRoot, "local files survive a pull of a deleted flow", "P0");

        var overwritten = await SwSteps.PullAsync(run, workspace, SwSteps.Slot, "--overwrite").ConfigureAwait(false);
        run.ExpectRefused(overwritten, "pull --overwrite of a deleted flow fails too");
        SwChecks.ExpectUnchanged(run, before, workspace.WorkflowRoot, "local files survive pull --overwrite of a deleted flow", "P0");
    }

    private static async Task PullRestoresFolderAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.ClassicAgentFlow).ConfigureAwait(false);
        var structure = SwChecks.Structure(await File.ReadAllTextAsync(workspace.DefinitionPath, run.Token).ConfigureAwait(false));

        Directory.Delete(workspace.FolderPath, recursive: true);

        var pulled = await SwSteps.PullAsync(run, workspace).ConfigureAwait(false);
        run.ExpectExit(pulled, 0, "a pull with the workflow folder gone succeeds");
        run.Expect(workspace.Folders.Count == 1, "the pull wrote the folder back", "1 folder", $"{workspace.Folders.Count}");

        if (workspace.Folders.Count == 1)
        {
            run.Expect(string.Equals(SwFixtures.MetadataValue(workspace.MetadataPath, "workflowId"), workspace.WorkflowId, StringComparison.OrdinalIgnoreCase), "the restored folder carries the same workflow id",
                workspace.WorkflowId, SwFixtures.MetadataValue(workspace.MetadataPath, "workflowId") ?? "none");
            run.Expect(SwChecks.StructureDifference(structure, SwChecks.Structure(await File.ReadAllTextAsync(workspace.DefinitionPath, run.Token).ConfigureAwait(false))) is null,
                "the restored definition has the deployed structure", "the same structure", "different");
        }
    }

    private static async Task RemoteRenameAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var actor = await SwSteps.SecondActorAsync(run, workspace).ConfigureAwait(false);
        var renamed = workspace.DisplayName + "-renamed";
        var previousFolder = workspace.FolderPath;

        static bool SamePath(string? reported, string expected) => reported is not null && string.Equals(Path.GetFullPath(reported), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

        SwFixtures.SetMetadataName(actor, renamed);
        await SwSteps.DeployAsync(run, actor, actor.HomeDeploymentName ?? "develop").ConfigureAwait(false);

        var landed = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false);
        run.Expect(landed is null || string.Equals(landed.Name, renamed, StringComparison.Ordinal), "the remote rename reached Dataverse", renamed, landed?.Name ?? "none");

        var pulled = await SwSteps.PullAsync(run, workspace).ConfigureAwait(false);
        run.ExpectExit(pulled, 0, "a pull after a remote rename succeeds");
        run.Expect(workspace.Folders.Count == 1, "the rename arrives as one folder, not a second copy", "1 folder", string.Join(", ", workspace.Folders.Select(Path.GetFileName)), "P1");

        if (workspace.Folders is [var folder])
        {
            run.Expect(string.Equals(SwFixtures.MetadataValue(workspace.MetadataPath, "name"), renamed, StringComparison.Ordinal), "the local metadata carries the new name", renamed,
                SwFixtures.MetadataValue(workspace.MetadataPath, "name") ?? "none");

            if (pulled.ExitCode == 0)
            {
                var result = pulled.Json<ManagedAgentPullResult>();
                run.Expect(SamePath(result.PreviousFolder, previousFolder) && SamePath(result.WorkflowFolder, folder), "the pull reports the folder the rename moved the workflow out of and the one it moved it into",
                    $"{previousFolder} -> {folder}", $"{result.PreviousFolder ?? "none"} -> {result.WorkflowFolder ?? "none"}", "P2");
            }
        }

        var records = SwChecks.Records(workspace.ProjectDirectory);
        run.Observe("the record's name after a remote rename and pull", $"record '{(records.Count > 0 ? records[0].DisplayName : null)}', cloud '{renamed}'", "P2");
        SwChecks.ExpectConfigIntact(run, workspace.ProjectDirectory, "the configuration is well formed after the rename");

        var redeployed = await SwSteps.RawDeployAsync(run, workspace).ConfigureAwait(false);
        run.ExpectExit(redeployed, 0, "the record still deploys after the rename");

        var after = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false);
        run.Expect(after is null || string.Equals(after.Name, renamed, StringComparison.Ordinal), "the redeploy keeps the new name rather than reverting it", renamed, after?.Name ?? "none", "P1");
    }

    private static async Task CorruptDefinitionAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection, activated: true).ConfigureAwait(false);
        var deployedStructure = SwChecks.Structure(await File.ReadAllTextAsync(workspace.DefinitionPath, run.Token).ConfigureAwait(false));

        await File.WriteAllTextAsync(workspace.DefinitionPath, "{ \"properties\": { \"definition\": ", run.Token).ConfigureAwait(false);

        var deploy = await SwSteps.RawDeployAsync(run, workspace).ConfigureAwait(false);
        run.ExpectRefused(deploy, "a deploy of corrupt JSON is refused");
        run.Expect(deploy.ExitCode != 1 || !deploy.Output.Contains("   at ", StringComparison.Ordinal), "the refusal explains itself rather than crashing", "a message", SwRun.Tail(deploy), "P2");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the deployed flow is still activated after the refused deploy", severity: "P0").ConfigureAwait(false);

        if (await SwChecks.CloudAsync(run, workspace.WorkflowId, definition: true).ConfigureAwait(false) is { ClientData: { Length: > 0 } clientData })
        {
            run.Expect(SwChecks.StructureDifference(deployedStructure, SwChecks.Structure(clientData)) is null, "the deployed definition is intact after the refused deploy", "unchanged", "changed", "P0");
        }

        var pulled = await SwSteps.PullAsync(run, workspace, SwSteps.Slot, "--overwrite").ConfigureAwait(false);
        run.ExpectExit(pulled, 0, "pull --overwrite repairs the corrupt definition");

        var repaired = await File.ReadAllTextAsync(workspace.DefinitionPath, run.Token).ConfigureAwait(false);
        var parses = true;

        try
        {
            run.Expect(SwChecks.StructureDifference(deployedStructure, SwChecks.Structure(repaired)) is null, "the repaired definition has the deployed structure", "the same structure", "different");
        }
        catch (System.Text.Json.JsonException)
        {
            parses = false;
        }

        run.Expect(parses, "the repaired definition is valid JSON", "valid JSON", "still corrupt", "P1");
    }
}
