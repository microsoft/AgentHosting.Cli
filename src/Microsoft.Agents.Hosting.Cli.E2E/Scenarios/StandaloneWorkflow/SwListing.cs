using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwListing
{
    public static IEnumerable<Scenario> All() =>
    [
        SwScenario.Make("SW-E01", "listing reports each flow's state and joins the local record", SwScenario.Cloud, SafetyTier.Creating, StatesAndJoinAsync),
        SwScenario.Make("SW-E02", "--filter matches names safely, whatever characters it is given", SwScenario.Cloud, SafetyTier.Creating, FilterAsync),
        SwScenario.Make("SW-E03", "--limit is validated, truncated is reported truthfully, and every unmanaged flow is listed, including the ones agents use, never a managed flow", SwScenario.Cloud, SafetyTier.ReadOnly, LimitAsync),
        SwScenario.Make("SW-E04", "--tenant-wide lists across environments and reports what it could not read", SwScenario.Cloud, SafetyTier.ReadOnly, TenantWideAsync),
        SwScenario.Make("SW-E05", "a flow is listed once deployed and not after it is deleted", SwScenario.Cloud, SafetyTier.Creating, LifecycleAsync),
    ];

    private static async Task StatesAndJoinAsync(SwRun run)
    {
        var (active, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection, activated: true, label: "act").ConfigureAwait(false);
        var (draft, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection, activated: false, label: "dft").ConfigureAwait(false);
        var prefix = SwFixtures.NameFor(run);

        var inside = await SwSteps.ListedAsync(run, active.ProjectDirectory, null, "--filter", prefix).ConfigureAwait(false);
        var activeRow = inside.Agents.FirstOrDefault(agent => string.Equals(agent.McsWorkflowId, active.WorkflowId, StringComparison.OrdinalIgnoreCase));
        var draftRow = inside.Agents.FirstOrDefault(agent => string.Equals(agent.McsWorkflowId, draft.WorkflowId, StringComparison.OrdinalIgnoreCase));

        run.Expect(activeRow is not null && draftRow is not null, "both flows are listed", "2 rows", $"{inside.Agents.Count} rows");
        run.Expect(activeRow?.WorkflowState == "published", "the activated flow lists as published", "published", activeRow?.WorkflowState ?? "absent");
        run.Expect(draftRow?.WorkflowState == "draft", "the draft flow lists as draft", "draft", draftRow?.WorkflowState ?? "absent");
        run.Expect(string.Equals(activeRow?.AgentId, active.AgentId, StringComparison.OrdinalIgnoreCase), "listed from its project, a flow carries its local record", active.AgentId ?? string.Empty, activeRow?.AgentId ?? "none");
        run.Expect(activeRow?.DeploymentSlotCount == 1, "listed from its project, a flow counts its deployment slots", "1", $"{activeRow?.DeploymentSlotCount}");
        run.Expect(draftRow is null || draftRow.AgentId is null, "a flow whose record is in another project carries no record id", "none", draftRow?.AgentId ?? "none");

        var outside = await SwSteps.ListedAsync(run, run.Context.Workspaces.EmptyDirectory(run.Id, "outside"), null, "--filter", prefix).ConfigureAwait(false);
        run.Expect(outside.Agents.All(agent => agent.AgentId is null), "listed from outside any project, no row claims a record", "no record ids", string.Join(", ", outside.Agents.Select(agent => agent.AgentId)));

        foreach (var (workspace, row) in new[] { (active, activeRow), (draft, draftRow) })
        {
            if (row is not null && await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false) is { } cloud)
            {
                run.Expect((row.WorkflowState == "published") == cloud.IsActivated, $"the listed state of '{workspace.DisplayName}' matches Dataverse", cloud.IsActivated ? "published" : "draft", row.WorkflowState ?? "none");
            }
        }
    }

    private static async Task FilterAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection, name: $"{SwFixtures.NameFor(run)} 50%_off [x]");
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);

        foreach (var (filter, mustMatch) in new[]
        {
            (workspace.DisplayName, true),
            (workspace.DisplayName.ToLowerInvariant(), true),
            ($"{run.Key}-{run.Context.RunSuffix}", true),
            ("%", true),
            ("_", true),
            ("[", true),
            ($"{run.Key}%{run.Context.RunSuffix}", false),
            ($"{run.Key}_{run.Context.RunSuffix}", false),
            ($"[{run.Key[0]}]{run.Key[1..]}", false),
            ("'", false),
            ("*", false),
            ("a' or 1 eq 1 or name eq 'x", false),
            ("') or contains(name,'", false),
            ("Ünïcødé", false),
            ($"zz-no-such-flow-{Guid.NewGuid():N}", false),
        })
        {
            var result = await SwSteps.ListAsync(run, workspace.ProjectDirectory, null, "--filter", filter, "--limit", "1000").ConfigureAwait(false);
            run.ExpectExit(result, 0, $"--filter \"{filter}\" is accepted", "P1");

            if (result.ExitCode != 0)
            {
                continue;
            }

            var rows = result.Json<ManagedAgentListResult>().Agents;
            run.Expect(rows.All(agent => (agent.DisplayName ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase)),
                $"every row --filter \"{filter}\" returns contains the text", "only matching rows", string.Join(", ", rows.Select(agent => agent.DisplayName).Take(5)), "P1");

            if (mustMatch)
            {
                run.Expect(rows.Any(agent => string.Equals(agent.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase)), $"--filter \"{filter}\" finds the flow", "the flow listed", "absent");
            }
        }
    }

    private static async Task LimitAsync(SwRun run)
    {
        var unmanaged = await run.Services.Dataverse.UnmanagedFlowsAsync(run.Dev, run.Token).ConfigureAwait(false);
        run.Note($"Dataverse holds {unmanaged.Count} unmanaged cloud flows in the dev environment");

        var one = await SwSteps.ListAsync(run, null, null, "--limit", "1").ConfigureAwait(false);
        run.ExpectExit(one, 0, "--limit 1 lists");

        if (one.ExitCode == 0)
        {
            var listed = one.Json<ManagedAgentListResult>();
            run.Expect(listed.Agents.Count <= 1, "--limit 1 returns at most one row", "at most 1", $"{listed.Agents.Count}");
            run.Expect(!(unmanaged.Count > 1) || listed.Truncated, "--limit 1 says it was truncated when more flows exist", "truncated", "not truncated", "P1");
        }

        run.ExpectExit(await SwSteps.ListAsync(run, null, null, "--limit", "0").ConfigureAwait(false), ExitCodes.Usage, "--limit 0 is a usage error", "P2");
        run.ExpectExit(await SwSteps.ListAsync(run, null, null, "--limit", "-1").ConfigureAwait(false), ExitCodes.Usage, "--limit -1 is a usage error", "P2");

        var full = await SwSteps.ListAsync(run, null, null, "--limit", "1000").ConfigureAwait(false);
        run.ExpectExit(full, 0, "the listing with --limit 1000 lists");

        if (full.ExitCode == 0)
        {
            var listed = full.Json<ManagedAgentListResult>();
            var unmanagedIds = unmanaged.Select(flow => flow.WorkflowId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var listedIds = listed.Agents.Select(flow => flow.McsWorkflowId ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notUnmanaged = listed.Agents.Where(flow => !unmanagedIds.Contains(flow.McsWorkflowId ?? string.Empty)).Select(flow => flow.DisplayName).ToArray();
            var leftOut = unmanaged.Where(flow => !listedIds.Contains(flow.WorkflowId)).Select(flow => flow.Name ?? flow.WorkflowId).ToArray();
            run.Expect(!listed.Truncated, "the listing with --limit 1000 is not truncated in an environment holding fewer flows", "not truncated", "truncated");
            run.Expect(notUnmanaged.Length + leftOut.Length == 0, "the listing holds exactly the unmanaged flows Dataverse holds, including the ones agents use, never a managed flow", $"{unmanaged.Count} unmanaged flows",
                $"listed but not unmanaged: {string.Join(", ", notUnmanaged.Take(8))}; unmanaged but not listed: {string.Join(", ", leftOut.Take(8))}", "P2", "#33");
        }

        var standard = await SwSteps.ListAsync(run).ConfigureAwait(false);

        if (standard.ExitCode == 0)
        {
            var listed = standard.Json<ManagedAgentListResult>();
            run.Expect(listed.Agents.Count <= 20, "the default listing returns at most 20 rows", "at most 20", $"{listed.Agents.Count}");
            run.Expect(!(unmanaged.Count > 20) || listed.Truncated, "the default listing says it was truncated when more than 20 flows exist", "truncated", "not truncated", "P1");
        }
    }

    private static async Task TenantWideAsync(SwRun run)
    {
        var result = await SwSteps.RunAsync(run, ["agent", "list", "--agent-type", "MCSWorkflow", "--tenant-wide", "--limit", "200", "--json", "--non-interactive"], null, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        run.ExpectExit(result, 0, "--tenant-wide lists");
        run.Observe("--tenant-wide duration", $"{result.Duration.TotalSeconds:N0}s", "P3", "#39");

        if (result.ExitCode == 0)
        {
            var listed = result.Json<ManagedAgentListResult>();
            run.Expect(listed.Scope == "tenant", "--tenant-wide reports tenant scope", "tenant", listed.Scope);
            run.Expect(listed.Agents.Any(agent => string.Equals(agent.EnvironmentId, run.Dev, StringComparison.OrdinalIgnoreCase)) || listed.Truncated,
                "the dev environment's flows are among the tenant-wide rows", "rows from the dev environment", "none");

            if (listed.Diagnostics.Count > 0)
            {
                run.Observe("--tenant-wide exits 0 while environments could not be read", string.Join("; ", listed.Diagnostics.Select(diagnostic => $"{diagnostic.EnvironmentId}: {diagnostic.Message}").Take(4)), "P2", "#20");
            }
        }

        var conflicting = await SwSteps.RunAsync(run, ["agent", "list", "--agent-type", "MCSWorkflow", "--tenant-wide", "--environment-id", run.Dev, "--json", "--non-interactive"], null, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        run.ExpectExit(conflicting, ExitCodes.Usage, "--tenant-wide with --environment-id is refused as contradictory", "P2");
    }

    private static async Task LifecycleAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);

        bool Listed(ManagedAgentListResult result) => result.Agents.Any(agent => string.Equals(agent.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase));

        run.Expect(!Listed(await SwSteps.ListedAsync(run, workspace.ProjectDirectory, null, "--filter", workspace.DisplayName).ConfigureAwait(false)), "a registered but never-deployed flow is not listed", "absent", "listed");

        await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        run.Expect(Listed(await SwSteps.ListedAsync(run, workspace.ProjectDirectory, null, "--filter", workspace.DisplayName).ConfigureAwait(false)), "a deployed flow is listed", "listed", "absent");

        run.Assert.ExitZero(await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false), "agent delete");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the deleted flow is gone from Dataverse").ConfigureAwait(false);
        run.Expect(!Listed(await SwSteps.ListedAsync(run, workspace.ProjectDirectory, null, "--filter", workspace.DisplayName).ConfigureAwait(false)), "a deleted flow is no longer listed", "absent", "listed");
    }
}

public static class SwDelete
{
    public static IEnumerable<Scenario> All() =>
    [
        SwScenario.Make("SW-F01", "delete removes an activated flow and its record, and keeps local files", SwScenario.Cloud, SafetyTier.Creating, run => DeleteStateAsync(run, true)),
        SwScenario.Make("SW-F02", "delete removes a draft flow and its record, and keeps local files", SwScenario.Cloud, SafetyTier.Creating, run => DeleteStateAsync(run, false)),
        SwScenario.Make("SW-F03", "a wrong, empty or missing confirmation deletes nothing", SwScenario.Cloud, SafetyTier.Creating, ConfirmationVariantsAsync),
        SwScenario.Make("SW-F06", "delete of a flow someone else already deleted removes the record and says so", SwScenario.Cloud, SafetyTier.Creating, AlreadyGoneAsync),
        SwScenario.Make("SW-F07", "deleting the test copy never leaves the dev copy silently untracked", SwScenario.CrossEnvironment, SafetyTier.Creating, DeleteOneEnvironmentAsync),
        SwScenario.Make("SW-F08", "deployment delete keeps the flow, and a later delete still finds it", SwScenario.Cloud, SafetyTier.Creating, SlotDeletedFirstAsync),
        SwScenario.Make("SW-F09", "deployment delete --delete-deployed-mcs-agent is refused for a workflow", SwScenario.Cloud, SafetyTier.Creating, AgentOnlyDeleteOptionAsync),
        SwScenario.Make("SW-F10", "delete works for a record whose local folder is gone", SwScenario.Cloud, SafetyTier.Creating, FolderGoneAsync),
        SwScenario.Make("SW-F11", "after a delete the record cannot resurrect the flow, and a re-init deploys it afresh", SwScenario.Cloud, SafetyTier.Creating, DeleteThenRedeployAsync),
        SwScenario.Make("SW-F12", "--non-interactive deletes without the typed confirmation, as designed", SwScenario.Cloud, SafetyTier.Creating, NonInteractiveDeleteAsync),
    ];

    private static async Task DeleteStateAsync(SwRun run, bool activated)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection, activated: activated).ConfigureAwait(false);
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, activated, "the flow is deployed in the expected state").ConfigureAwait(false);

        var files = SwChecks.FilesSnapshot(workspace.WorkflowRoot);
        var deleted = await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false);

        if (deleted.ExitCode != 0)
        {
            var flowKept = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false) is not null;
            var recordKept = SwChecks.Records(workspace.ProjectDirectory).Any(record => string.Equals(record.AgentId, workspace.AgentId, StringComparison.OrdinalIgnoreCase));
            var slotKept = SwChecks.Deployments(workspace.ProjectDirectory).Count == 1;
            run.Expect(flowKept && recordKept && slotKept, "a failed delete leaves the flow, record and slot all in place", "nothing half-deleted",
                $"flow {(flowKept ? "kept" : "gone")}, record {(recordKept ? "kept" : "gone")}, slot {(slotKept ? "kept" : "gone")}", "P0");
            run.Observe("agent delete failed after its retries and was run again", SwRun.Tail(deleted), "P2");

            await Task.Delay(TimeSpan.FromSeconds(10), run.Token).ConfigureAwait(false);
            deleted = await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false);
        }

        run.ExpectExit(deleted, 0, "agent delete with the typed name succeeds");

        if (deleted.ExitCode == 0)
        {
            var result = deleted.Json<ManagedAgentDeleteResult>();
            run.Expect(result.Status == "agent-deleted", "the delete reports agent-deleted", "agent-deleted", result.Status);
            run.Expect(string.Equals(result.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase), "the delete names the flow it removed", workspace.WorkflowId, result.McsWorkflowId ?? "none");
        }

        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the flow is gone from Dataverse", severity: "P0").ConfigureAwait(false);
        run.Expect(!SwChecks.Records(workspace.ProjectDirectory).Any(record => string.Equals(record.AgentId, workspace.AgentId, StringComparison.OrdinalIgnoreCase)), "the record is removed", "no record", "still there");
        run.Expect(SwChecks.Deployments(workspace.ProjectDirectory).Count == 0, "the record's slots are removed", "no slots", $"{SwChecks.Deployments(workspace.ProjectDirectory).Count}");
        SwChecks.ExpectFilesUnchanged(run, files, workspace.WorkflowRoot, "local workflow files are untouched, as the help promises", "P0");
        SwChecks.ExpectConfigIntact(run, workspace.ProjectDirectory, "the configuration is well formed after the delete");
    }

    private static async Task ConfirmationVariantsAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);

        foreach (var (typed, what) in new (string?, string)[]
        {
            ("not-the-name", "a wrong name"),
            (string.Empty, "an empty line"),
            (null, "a closed input with nothing typed"),
            (workspace.DisplayName.ToUpperInvariant(), "the name in another case"),
        })
        {
            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var result = await SwSteps.DeleteAsync(run, workspace, typed).ConfigureAwait(false);

            run.Expect(result.Duration < TimeSpan.FromMinutes(2), $"delete with {what} returns instead of waiting", "under 2 minutes", $"{result.Duration.TotalSeconds:N0}s", "P1");
            await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, null, $"delete with {what} leaves the flow", severity: "P0").ConfigureAwait(false);
            SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, $"delete with {what} leaves the record and files", "P0");
        }

        var padded = await SwSteps.DeleteAsync(run, workspace, $"  {workspace.DisplayName} ").ConfigureAwait(false);
        var gone = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false) is null;
        run.Observe("delete confirmed with the name surrounded by spaces", gone ? "accepted: the prompt trims what is typed" : $"refused: exit {padded.ExitCode}", "info");
    }

    private static async Task AlreadyGoneAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var actor = await SwSteps.SecondActorAsync(run, workspace).ConfigureAwait(false);

        run.Assert.ExitZero(await SwSteps.DeleteAsync(run, actor, actor.DisplayName).ConfigureAwait(false), "the second operator deletes the flow");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the flow is gone").ConfigureAwait(false);

        var deleted = await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false);
        run.ExpectExit(deleted, 0, "deleting a flow that is already gone succeeds");

        if (deleted.ExitCode == 0)
        {
            var result = deleted.Json<ManagedAgentDeleteResult>();
            run.Expect(result.McsAgentAlreadyAbsent, "the delete says the flow was already gone", "alreadyAbsent true", "false");
        }

        run.Expect(SwChecks.Records(workspace.ProjectDirectory).Count == 0, "the stale record is removed", "no record", $"{SwChecks.Records(workspace.ProjectDirectory).Count}");
    }

    private static async Task DeleteOneEnvironmentAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection, activated: true);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace, SwSteps.TestSlot, "test", run.Test).ConfigureAwait(false);

        await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.DeployAsync(run, workspace, SwSteps.TestSlot, run.Test).ConfigureAwait(false);

        var deleted = await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName, SwSteps.TestSlot).ConfigureAwait(false);
        run.ExpectExit(deleted, 0, "delete through the test slot succeeds");

        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the test copy is gone", run.Test).ConfigureAwait(false);
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, null, "the dev copy is still there", severity: "P0").ConfigureAwait(false);

        var tracked = SwChecks.Records(workspace.ProjectDirectory).Any(record => string.Equals(record.AgentId, workspace.AgentId, StringComparison.OrdinalIgnoreCase))
            && SwChecks.Deployments(workspace.ProjectDirectory).Any(slot => slot.DeploymentName == SwSteps.Slot);
        var reported = deleted.ExitCode == 0 && deleted.Json<ManagedAgentDeleteResult>().OrphanedTargets.Count > 0;

        run.Expect(tracked || reported, "deleting through the test slot never leaves the dev copy silently untracked", "dev copy kept tracked, or reported as orphaned",
            "removed without a trace", "P1", "#16");
    }

    private static async Task SlotDeletedFirstAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);

        run.ExpectExit(await SwSteps.DeleteSlotAsync(run, workspace, SwSteps.Slot).ConfigureAwait(false), 0, "deployment delete succeeds");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, null, "deployment delete leaves the flow", severity: "P0").ConfigureAwait(false);

        var deleted = await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false);
        run.ExpectExit(deleted, 0, "agent delete after the slot is gone succeeds");

        var gone = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false) is null;
        var reported = deleted.ExitCode == 0 && deleted.Json<ManagedAgentDeleteResult>().OrphanedTargets.Count > 0;
        run.Expect(gone || reported, "agent delete removes the flow or reports it left behind", "deleted or reported", "left behind silently", "P1", "#16");
    }

    private static async Task AgentOnlyDeleteOptionAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var before = SwChecks.Snapshot(workspace.ProjectDirectory);

        var result = await SwSteps.DeleteSlotAsync(run, workspace, SwSteps.Slot, "--delete-deployed-mcs-agent").ConfigureAwait(false);
        run.ExpectExit(result, ExitCodes.Usage, "--delete-deployed-mcs-agent is a usage error for a workflow", "P2");
        SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, "the refused slot delete changed nothing", "P1");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, null, "the refused slot delete left the flow", severity: "P0").ConfigureAwait(false);
    }

    private static async Task FolderGoneAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        Directory.Delete(workspace.WorkflowsDirectory, recursive: true);

        var deleted = await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false);
        run.ExpectExit(deleted, 0, "agent delete works from the record when the local folder is gone");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the flow is deleted though its local folder was gone").ConfigureAwait(false);
        run.Expect(deleted.ExitCode == 0 || !deleted.Output.Contains("agent delete", StringComparison.OrdinalIgnoreCase),
            "a local-folder problem does not recommend a cloud delete", "no destructive hint", SwRun.Tail(deleted), "P0", "#4");
    }

    private static async Task DeleteThenRedeployAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);

        run.Assert.ExitZero(await SwSteps.DeleteAsync(run, workspace, workspace.DisplayName).ConfigureAwait(false), "agent delete");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the flow is gone").ConfigureAwait(false);

        var resurrect = await SwSteps.RawDeployAsync(run, workspace).ConfigureAwait(false);
        run.ExpectRefused(resurrect, "a deploy through the deleted record is refused", "P1", "#7");
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the deleted record did not resurrect the flow", severity: "P0", reference: "#7").ConfigureAwait(false);

        var recordsBefore = SwChecks.Records(workspace.ProjectDirectory).Count;
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        run.Expect(SwChecks.Records(workspace.ProjectDirectory).Count == recordsBefore + 1, "re-init registers the workflow again", $"{recordsBefore + 1}", $"{SwChecks.Records(workspace.ProjectDirectory).Count}");

        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);
        var redeployed = await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        run.Expect(redeployed.Action == "create", "the re-initialised workflow deploys as a new flow", "create", redeployed.Action);
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, null, "the flow exists again under the same id").ConfigureAwait(false);
    }

    private static async Task NonInteractiveDeleteAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection).ConfigureAwait(false);
        var files = SwChecks.FilesSnapshot(workspace.WorkflowRoot);

        var result = await SwSteps.DeleteAsync(run, workspace, null, null, "--non-interactive").ConfigureAwait(false);
        run.ExpectExit(result, 0, "--non-interactive deletes without the typed confirmation, as designed");
        run.Expect(result.Duration < TimeSpan.FromMinutes(2), "--non-interactive never waits for input", "under 2 minutes", $"{result.Duration.TotalSeconds:N0}s");

        if (result.ExitCode == 0)
        {
            var status = result.Json<ManagedAgentDeleteResult>().Status;
            run.Expect(status == "agent-deleted", "the unattended delete reports agent-deleted", "agent-deleted", status);
        }

        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "the flow is gone after the unattended delete").ConfigureAwait(false);
        run.Expect(!SwChecks.Records(workspace.ProjectDirectory).Any(record => string.Equals(record.AgentId, workspace.AgentId, StringComparison.OrdinalIgnoreCase)),
            "the unattended delete removes the record", "no record", "still there");
        SwChecks.ExpectFilesUnchanged(run, files, workspace.WorkflowRoot, "local workflow files are untouched by the unattended delete", "P0");
    }
}
