using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwDeployMatrix
{
    private enum Premise
    {
        None,
        Connected,
        Missing,
    }

    private sealed record Cell(SwShape Shape, bool? Activated, bool Publish, bool Running, int Unbound, Premise Premise = Premise.None, bool EditWhileUnbound = false);

    public static IEnumerable<Scenario> All() =>
    [
        Matrix("SW-B01", "no connector, activated: lands activated", new Cell(SwShape.NoConnection, true, false, true, 0), smoke: true),
        Matrix("SW-B02", "no connector, draft: stays a draft", new Cell(SwShape.NoConnection, false, false, false, 0)),
        Matrix("SW-B03", "no connector, draft with --publish: lands activated", new Cell(SwShape.NoConnection, false, true, true, 0)),
        SwScenario.Make("SW-B04", "a scaffolded workflow deploys activated and redeploys in place", SwScenario.Cloud, SafetyTier.Creating, ScaffoldedAsync),
        Matrix("SW-B05", "one connector with a connection, activated: binds and activates", new Cell(SwShape.ClassicAgentFlow, true, false, true, 0, Premise.Connected)),
        Matrix("SW-B06", "three connectors with connections, activated: binds all three", new Cell(SwShape.CliNative, true, false, true, 0, Premise.Connected)),
        Matrix("SW-B07", "three connectors, draft: stays a draft and reports what is unbound", new Cell(SwShape.CliNative, false, false, false, 3, EditWhileUnbound: true)),
        Matrix("SW-B08", "three connectors, draft with --publish: binds and activates", new Cell(SwShape.CliNative, false, true, true, 0, Premise.Connected)),
        Matrix("SW-B09", "missing connection, activated: lands a draft and says why", new Cell(SwShape.Dataverse, true, false, false, 1, Premise.Missing)),
        Matrix("SW-B10", "missing connection with --publish: still deploys, as a draft that says why", new Cell(SwShape.Dataverse, false, true, false, 1, Premise.Missing)),
        Matrix("SW-B11", "complex flow, partial connections, with --publish: still deploys, as a draft naming what is unbound", new Cell(SwShape.Complex, false, true, false, 4, Premise.Missing, EditWhileUnbound: true)),
        Matrix("SW-B12", "complex flow, partial connections, activated: lands a draft naming what is unbound", new Cell(SwShape.Complex, true, false, false, 4, Premise.Missing, EditWhileUnbound: true)),
        SwScenario.Make("SW-B14", "a connector the environment does not serve still deploys, as a draft naming the reference", SwScenario.Cloud, SafetyTier.Creating, UnknownConnectorAsync),
        SwScenario.Make("SW-B15", "draft and activated transitions follow the files in both directions", SwScenario.Cloud, SafetyTier.Creating, TransitionsAsync),
        SwScenario.Make("SW-B16", "a redeploy reactivates a flow someone else deactivated", SwScenario.Cloud, SafetyTier.Creating, ExternalDeactivationAsync),
        SwScenario.Make("SW-B17", "deploy option validation refuses cleanly, and --deployment-id wins over the name", SwScenario.Cloud, SafetyTier.Creating, OptionValidationAsync),
        SwScenario.Make("SW-B18", "a test slot deploys into the second environment only", SwScenario.CrossEnvironment, SafetyTier.Creating, CrossEnvironmentAsync),
        SwScenario.Make("SW-B19", "an inflated definition either deploys whole or is refused with nothing created", SwScenario.Cloud, SafetyTier.Creating, InflatedAsync),
        SwScenario.Make("SW-B21", "a deployed no-op flow actually runs", SwScenario.FlowRun, SafetyTier.Creating, RealRunAsync),
    ];

    private static Scenario Matrix(string id, string title, Cell cell, bool smoke = false) =>
        SwScenario.Make(id, title, SwScenario.Cloud, SafetyTier.Creating, run => MatrixAsync(run, cell), smoke);

    private static async Task MatrixAsync(SwRun run, Cell cell)
    {
        var workspace = SwFixtures.Materialize(run, cell.Shape, activated: cell.Activated);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);

        string[] publish = cell.Publish ? ["--publish"] : [];
        var declared = SwFixtures.References(workspace);
        var references = declared.Keys.ToArray();
        var expectedUnbound = await ExpectedUnboundAsync(run, cell, declared).ConfigureAwait(false);

        var first = await SwSteps.RawDeployAsync(run, workspace, SwSteps.Slot, null, publish).ConfigureAwait(false);
        var deployed = ReadDeploy(first);
        run.ExpectExit(first, 0, "the deploy succeeds");

        var cloud = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, cell.Running, cell.Running ? "the flow is activated in Dataverse" : "the flow is a draft in Dataverse").ConfigureAwait(false);

        if (deployed is not null)
        {
            run.Expect(deployed.Action == "create", "the first deploy creates the flow", "create", deployed.Action);
            run.Expect(string.Equals(deployed.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase), "the deploy targets the workflow on disk", workspace.WorkflowId, deployed.McsWorkflowId ?? "none");
            run.Expect(SwSteps.ReportedActivated(deployed) == cell.Running, "the reported state is the state the flow is in",
                cell.Running ? "published" : "draft", $"{deployed.WorkflowState} (published={deployed.Published})", "P1");

            if (cloud is not null)
            {
                run.Expect(SwSteps.ReportedActivated(deployed) == cloud.IsActivated, "the reported state matches Dataverse", cloud.IsActivated ? "published" : "draft", deployed.WorkflowState ?? "none", "P1");
            }

            var unbound = await SwChecks.UnboundAsync(run, references).ConfigureAwait(false);
            run.Expect(unbound is null || deployed.UnboundConnectionCount == unbound.Count, "the reported unbound count matches the references Dataverse holds without a connection",
                $"{unbound?.Count}", $"{deployed.UnboundConnectionCount}", "P1", "#9");
            run.Expect(unbound is null || unbound.Count == expectedUnbound, "the references left unbound are the ones with no connection in the environment", $"{expectedUnbound}", $"{unbound?.Count}", "P1");

            if (!cell.Running && (cell.Publish || cell.Activated == true))
            {
                run.Expect(deployed.Warnings.Count > 0, "a deploy that could not activate says so", "a warning", "no warning", "P1", "#8");
            }

            var warnings = string.Join(" ", deployed.Warnings);
            var unnamed = (unbound ?? []).Where(reference => !warnings.Contains(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
            run.Expect(unnamed.Length == 0, "the warnings name each reference left unbound", "every unbound reference named", $"not named: {string.Join(", ", unnamed)}; warnings: {warnings}", "P2", "#11");
        }

        if (cloud is null && run.Oracle)
        {
            return;
        }

        await RedeployUnchangedAsync(run, workspace, cell, cloud, publish).ConfigureAwait(false);
        await RedeployEditedAsync(run, workspace, cell, publish).ConfigureAwait(false);
        await ExpectDefinitionParityAsync(run, workspace).ConfigureAwait(false);
    }

    private static async Task RedeployUnchangedAsync(SwRun run, SwWorkspace workspace, Cell cell, DataverseWorkflow? before, string[] publish)
    {
        var again = await SwSteps.RawDeployAsync(run, workspace, SwSteps.Slot, null, publish).ConfigureAwait(false);
        run.ExpectExit(again, 0, "an unchanged redeploy succeeds");

        if (ReadDeploy(again) is { } redeployed)
        {
            run.Expect(redeployed.Action == "update", "an unchanged redeploy updates in place", "update", redeployed.Action, "P1");
        }

        var after = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, cell.Running, "an unchanged redeploy keeps the state").ConfigureAwait(false);
        run.Expect(before is null || after is null || before.CreatedOn == after.CreatedOn, "an unchanged redeploy keeps the same flow rather than replacing it",
            $"{before?.CreatedOn:O}", $"{after?.CreatedOn:O}", "P1");
        await SwChecks.ExpectSingleFlowAsync(run, workspace.DisplayName, "the environment holds one flow with the name").ConfigureAwait(false);
    }

    private static async Task RedeployEditedAsync(SwRun run, SwWorkspace workspace, Cell cell, string[] publish)
    {
        var marker = $"ahe2e edit {run.Context.RunSuffix} {run.Key}";
        SwFixtures.SetDescription(workspace, marker);

        if (cell.EditWhileUnbound)
        {
            await RedeployEditWhileUnboundAsync(run, workspace, marker, publish).ConfigureAwait(false);
            return;
        }

        var edited = await SwSteps.RawDeployAsync(run, workspace, SwSteps.Slot, null, publish).ConfigureAwait(false);
        run.ExpectExit(edited, 0, "an edited redeploy succeeds");

        var after = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, cell.Running, "an edited redeploy keeps the state").ConfigureAwait(false);
        run.Expect(after is null || string.Equals(after.Description, marker, StringComparison.Ordinal), "the edit reached Dataverse", marker, after?.Description ?? "none", "P1");
    }

    private static async Task RedeployEditWhileUnboundAsync(SwRun run, SwWorkspace workspace, string marker, string[] publish)
    {
        var files = SwChecks.FilesSnapshot(workspace.WorkflowRoot);
        var before = await SwChecks.CloudAsync(run, workspace.WorkflowId).ConfigureAwait(false);
        var edited = await SwSteps.RawDeployAsync(run, workspace, SwSteps.Slot, null, publish).ConfigureAwait(false);
        run.Observe("an edited redeploy of a draft whose references are unbound", edited.ExitCode == 0 ? "accepted" : $"exit {edited.ExitCode}: {SwRun.Tail(edited)}");
        SwChecks.ExpectFilesUnchanged(run, files, workspace.WorkflowRoot, "the edit stays on disk whatever the redeploy did", "P0");

        var expected = edited.ExitCode == 0 ? marker : before?.Description;
        var after = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, false, "the flow is still a draft after the edited redeploy").ConfigureAwait(false);
        run.Expect(before is null || after is null || string.Equals(after.Description, expected, StringComparison.Ordinal), "the cloud copy holds the edit only when the redeploy reports success",
            expected ?? "none", after?.Description ?? "none", "P1");
    }

    private static async Task ExpectDefinitionParityAsync(SwRun run, SwWorkspace workspace)
    {
        if (await SwChecks.CloudAsync(run, workspace.WorkflowId, definition: true).ConfigureAwait(false) is not { ClientData: { Length: > 0 } clientData })
        {
            return;
        }

        var difference = SwChecks.StructureDifference(SwChecks.Structure(await File.ReadAllTextAsync(workspace.DefinitionPath, run.Token).ConfigureAwait(false)), SwChecks.Structure(clientData));
        run.Expect(difference is null, "the deployed definition carries every trigger, action and reference on disk", "the same structure", difference ?? string.Empty, "P1");
    }

    private static async Task ScaffoldedAsync(SwRun run)
    {
        var workspace = await SwSteps.ScaffoldAsync(run).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);

        var deployed = await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        run.Expect(deployed.Action == "create", "the first deploy creates the flow", "create", deployed.Action);
        var cloud = await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the scaffolded flow is activated in Dataverse").ConfigureAwait(false);
        run.Expect(SwSteps.ReportedActivated(deployed), "the scaffolded flow is reported published", "published", deployed.WorkflowState ?? "none");

        await RedeployUnchangedAsync(run, workspace, new Cell(SwShape.Scaffold, null, false, true, 0), cloud, []).ConfigureAwait(false);
        await RedeployEditedAsync(run, workspace, new Cell(SwShape.Scaffold, null, false, true, 0), []).ConfigureAwait(false);
        await ExpectDefinitionParityAsync(run, workspace).ConfigureAwait(false);
    }

    private static async Task UnknownConnectorAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.ClassicAgentFlow, activated: true);
        SwFixtures.ReplaceConnector(workspace, "shared_msnweather", "shared_ahe2enotaconnector");

        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);

        var result = await SwSteps.RawDeployAsync(run, workspace).ConfigureAwait(false);
        run.ExpectExit(result, 0, "a deploy naming a connector the environment does not serve still succeeds", "P1", "#8");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, false, "the flow lands a draft, its reference having no connection").ConfigureAwait(false);

        if (ReadDeploy(result) is not { } deployed)
        {
            return;
        }

        var references = SwFixtures.References(workspace).Keys.ToArray();
        var warnings = string.Join(" ", deployed.Warnings);
        var unnamed = references.Where(reference => !warnings.Contains(reference, StringComparison.OrdinalIgnoreCase)).ToArray();

        run.Expect(!SwSteps.ReportedActivated(deployed), "the deploy reports a draft", "draft", $"{deployed.WorkflowState} (published={deployed.Published})", "P1");
        run.Expect(deployed.UnboundConnectionCount == references.Length, "the deploy counts the reference left unbound", $"{references.Length}", $"{deployed.UnboundConnectionCount}", "P1", "#9");
        run.Expect(unnamed.Length == 0, "the warnings name the reference left unbound", "every reference named", $"not named: {string.Join(", ", unnamed)}; warnings: {warnings}", "P2", "#11");
    }

    private static async Task TransitionsAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection, activated: true).ConfigureAwait(false);
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the flow starts activated").ConfigureAwait(false);

        SwFixtures.SetState(workspace, false);
        var drafted = await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        run.Expect(!SwSteps.ReportedActivated(drafted), "a draft redeployed over an activated flow is reported as a draft", "draft", drafted.WorkflowState ?? "none");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, false, "a draft redeployed over an activated flow stops it").ConfigureAwait(false);

        SwFixtures.SetState(workspace, true);
        var activated = await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        run.Expect(SwSteps.ReportedActivated(activated), "activated files redeployed over a draft are reported published", "published", activated.WorkflowState ?? "none");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "activated files redeployed over a draft start it").ConfigureAwait(false);
    }

    private static async Task ExternalDeactivationAsync(SwRun run)
    {
        var (workspace, _) = await SwSteps.DeployedAsync(run, SwShape.NoConnection, activated: true).ConfigureAwait(false);

        var actor = await SwSteps.SecondActorAsync(run, workspace).ConfigureAwait(false);
        SwFixtures.SetState(actor, false);
        await SwSteps.DeployAsync(run, actor, actor.HomeDeploymentName ?? "develop").ConfigureAwait(false);
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, false, "the second operator's draft deploy deactivated the flow").ConfigureAwait(false);

        var redeployed = await SwSteps.DeployAsync(run, workspace).ConfigureAwait(false);
        run.Expect(SwSteps.ReportedActivated(redeployed), "the original workspace's redeploy reports the flow published again", "published", redeployed.WorkflowState ?? "none");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the original workspace's redeploy reactivates the flow").ConfigureAwait(false);
    }

    private static async Task OptionValidationAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection, activated: true);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);

        var slot = await SwSteps.RawSlotAsync(run, workspace, SwSteps.Slot).ConfigureAwait(false);
        run.Assert.ExitZero(slot, "deployment create");
        var slotId = slot.Json<AgentDeploymentCreateResult>().DeploymentId;

        var refusals = new (string[] Arguments, string Why)[]
        {
            (["agent", "deploy", "--agent-id", workspace.Agent, "--json", "--non-interactive"], "no deployment"),
            (["agent", "deploy", "--agent-id", workspace.Agent, "--deployment-name", "nosuchslot", "--json", "--non-interactive"], "an unknown deployment"),
            (["agent", "deploy", "--agent-id", workspace.Agent, "--deployment-name", SwSteps.Slot, "--schema-name", "ahe2e_schema", "--json", "--non-interactive"], "--schema-name"),
            (["agent", "deploy", "--agent-id", workspace.Agent, "--deployment-name", SwSteps.Slot, "--commit-id", "abc123", "--json", "--non-interactive"], "--commit-id"),
            (["agent", "deploy", "--agent-id", workspace.Agent, "--deployment-name", SwSteps.Slot, "--async", "--json", "--non-interactive"], "--async"),
            (["agent", "deploy", "--agent-id", Guid.NewGuid().ToString(), "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], "an unknown record"),
        };

        SwSteps.RecordIntent(run, workspace, SwSteps.Slot, run.Dev);

        foreach (var (arguments, why) in refusals)
        {
            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var result = await SwSteps.RunAsync(run, arguments, workspace.ProjectDirectory).ConfigureAwait(false);

            run.ExpectExit(result, ExitCodes.Usage, $"deploy with {why} is a usage error", "P2");
            SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, $"deploy with {why} wrote nothing");
        }

        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "none of the refused deploys created the flow", severity: "P0").ConfigureAwait(false);

        var byId = await SwSteps.RunAsync(run, ["agent", "deploy", "--agent-id", workspace.Agent, "--deployment-id", slotId, "--deployment-name", "nosuchslot", "--json", "--non-interactive"], workspace.ProjectDirectory, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        run.ExpectExit(byId, 0, "--deployment-id wins over a conflicting --deployment-name, as its help says", "P2");

        var outside = run.Context.Workspaces.EmptyDirectory(run.Id, "outside");
        var fromOutside = await SwSteps.RunAsync(run, ["agent", "deploy", "--project", workspace.ProjectDirectory, "--agent-id", workspace.Agent, "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], outside, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        run.ExpectExit(fromOutside, 0, "--project deploys a workspace from another directory");
        run.Expect(SwChecks.Snapshot(outside).Count == 0, "--project wrote nothing into the current directory", "nothing", SwChecks.Difference(new Dictionary<string, string>(), SwChecks.Snapshot(outside)) ?? string.Empty);

        run.ExpectExit(await SwSteps.RawDeployAsync(run, workspace, SwSteps.Slot, null, "--overwrite").ConfigureAwait(false), 0, "deploy --overwrite on a workflow is accepted", "P3", "#3");
        await SwChecks.ExpectHelpAsync(run, ["agent", "deploy"], "--overwrite", "Overwrite cloud drift, and replace an unverified MCS agent. Only dev refuses drift without it.",
            "the --overwrite help says it overwrites cloud drift, which a dev deploy refuses without it", "#3").ConfigureAwait(false);
    }

    private static async Task CrossEnvironmentAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection, activated: true);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace, SwSteps.TestSlot, "test", run.Test).ConfigureAwait(false);

        var deployed = await SwSteps.DeployAsync(run, workspace, SwSteps.TestSlot, run.Test).ConfigureAwait(false);
        run.Expect(string.Equals(deployed.TargetEnvironmentId, run.Test, StringComparison.OrdinalIgnoreCase), "the test slot deploys into the test environment", run.Test, deployed.TargetEnvironmentId);
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the flow is activated in the test environment", run.Test).ConfigureAwait(false);
        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "deploying to the test slot created nothing in the dev environment", severity: "P0").ConfigureAwait(false);

        var listed = await SwSteps.ListedAsync(run, workspace.ProjectDirectory, run.Test, "--filter", workspace.DisplayName).ConfigureAwait(false);
        run.Expect(listed.Agents.Any(agent => string.Equals(agent.McsWorkflowId, workspace.WorkflowId, StringComparison.OrdinalIgnoreCase)), "the test environment lists the flow", "listed", "absent");

        var weather = SwFixtures.Materialize(run, SwShape.ClassicAgentFlow, "weather", activated: true);
        await SwSteps.InitAsync(run, weather).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, weather, SwSteps.TestSlot, "test", run.Test).ConfigureAwait(false);

        var landed = await SwSteps.RawDeployAsync(run, weather, SwSteps.TestSlot, run.Test).ConfigureAwait(false);
        var result = ReadDeploy(landed);
        await SwChecks.ExpectCloudAsync(run, weather.WorkflowId, false, "a flow whose connector has no connection in the test environment lands a draft there", run.Test).ConfigureAwait(false);
        run.Expect(result is null || (!result.Published && result.UnboundConnectionCount == 1), "the promoted flow is reported as an unbound draft", "draft, 1 unbound",
            result is null ? $"exit {landed.ExitCode}" : $"{result.WorkflowState}, {result.UnboundConnectionCount} unbound", "P1", "#8");
    }

    private static async Task InflatedAsync(SwRun run)
    {
        foreach (var megabytes in new[] { 2, 5 })
        {
            var workspace = SwFixtures.Materialize(run, SwShape.ButtonNoop, $"mb{megabytes}", activated: false);
            SwFixtures.Inflate(workspace, megabytes * 1024 * 1024);

            await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
            await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);

            var result = await SwSteps.RawDeployAsync(run, workspace).ConfigureAwait(false);
            run.Observe($"deploy of a {megabytes} MB definition", $"exit {result.ExitCode} in {result.Duration.TotalSeconds:N0}s", "P3");

            var cloud = await SwChecks.CloudAsync(run, workspace.WorkflowId, definition: true).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                run.Expect(cloud is { ClientData.Length: > 0 } && cloud.ClientData.Length >= megabytes * 1024 * 1024,
                    $"the {megabytes} MB definition reached Dataverse whole", $"at least {megabytes * 1024 * 1024} characters", $"{cloud?.ClientData?.Length ?? 0}", "P1");
            }
            else
            {
                run.Expect(cloud is null, $"the refused {megabytes} MB deploy left no flow behind", "no flow", cloud is null ? string.Empty : $"'{cloud.Name}'", "P1");
                run.Expect(result.ExitCode != 1 || !result.Output.Contains("   at ", StringComparison.Ordinal), $"the refused {megabytes} MB deploy explains itself rather than crashing", "a message", SwRun.Tail(result), "P2");
            }
        }
    }

    private static async Task RealRunAsync(SwRun run)
    {
        var (workspace, deployed) = await SwSteps.DeployedAsync(run, SwShape.ButtonNoop, activated: true).ConfigureAwait(false);
        run.Expect(SwSteps.ReportedActivated(deployed), "the no-op flow is reported published", "published", deployed.WorkflowState ?? "none");
        await SwChecks.ExpectCloudAsync(run, workspace.WorkflowId, true, "the no-op flow is activated in Dataverse").ConfigureAwait(false);

        run.Services.Flows.AllowRunsOf(workspace.WorkflowId);
        var outcome = await run.Services.Flows.RunAsync(run.Dev, workspace.WorkflowId, TimeSpan.FromMinutes(5), run.Token).ConfigureAwait(false);

        if (outcome.TriggerStatus is 0 or 400 or 401 or 403 or 404 or 405)
        {
            throw new ScenarioBlockedException($"the Power Automate API would not start the run: {outcome.TriggerStatus} {outcome.Detail}", "flow-run trigger unavailable");
        }

        run.Expect(outcome.Succeeded, "the deployed flow runs to completion", "Succeeded", $"{outcome.RunStatus} {outcome.Detail}", "P1");
        run.Observe("time from trigger to a finished run", $"{outcome.Elapsed.TotalSeconds:N0}s", "P3");
    }

    private static ManagedAgentDeployResult? ReadDeploy(MsagentResult result)
    {
        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            return result.Json<ManagedAgentDeployResult>();
        }
        catch (E2EException)
        {
            return null;
        }
    }

    private static async Task<int> ExpectedUnboundAsync(SwRun run, Cell cell, IReadOnlyDictionary<string, string> declared)
    {
        if (cell.Premise == Premise.None)
        {
            return cell.Unbound;
        }

        IReadOnlySet<string> usable;

        try
        {
            usable = await run.Services.Connections.UsableConnectorsAsync(run.Dev, run.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Observe("the environment's connections could not be listed, so the fixture's own expectation stands", exception.Message);
            return cell.Unbound;
        }

        var missing = declared.Values.Select(ConnectorName).Where(connector => !usable.Contains(connector)).ToArray();

        return (cell.Premise, missing.Length) switch
        {
            (Premise.Connected, > 0) => throw new ScenarioBlockedException($"{run.Dev} has no connection of the signed-in user for {string.Join(", ", missing.Distinct(StringComparer.OrdinalIgnoreCase))}, which this scenario needs", "connection missing"),
            (Premise.Missing, 0) => throw new ScenarioBlockedException($"{run.Dev} has a connection of the signed-in user for every connector of {cell.Shape}, so nothing would be left unbound; this scenario needs one without", "no missing connection"),
            _ => missing.Length,
        };
    }

    private static string ConnectorName(string connectorId) => connectorId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? connectorId;
}
