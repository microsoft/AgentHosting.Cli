using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwUnsupported
{
    public static IEnumerable<Scenario> All() =>
    [
        SwScenario.Make("SW-X01", "agent-only and code-agent-only commands refuse a workflow record and change nothing", SwScenario.Local, SafetyTier.Reversible, CommandsAsync),
        SwScenario.Make("SW-X02", "agent-only create options refuse a workflow and write nothing", SwScenario.Local, SafetyTier.ReadOnly, CreateOptionsAsync),
        SwScenario.Make("SW-X03", "a workflow clone id without --agent-type gets accurate guidance", SwScenario.Local, SafetyTier.ReadOnly, CloneWithoutTypeAsync),
    ];

    private static async Task CommandsAsync(SwRun run)
    {
        var workspace = SwFixtures.Materialize(run, SwShape.NoConnection);
        await SwSteps.InitAsync(run, workspace).ConfigureAwait(false);
        await SwSteps.SlotAsync(run, workspace).ConfigureAwait(false);

        var agent = workspace.Agent;
        SwSteps.RecordIntent(run, workspace, SwSteps.Slot, run.Dev);

        var commands = new (string[] Arguments, string What)[]
        {
            (["agent", "share", "--agent-id", agent, "--user", Guid.NewGuid().ToString(), "--role", "use", "--json", "--non-interactive"], "agent share"),
            (["agent", "share", "show", "--agent-id", agent, "--json", "--non-interactive"], "agent share show"),
            (["agent", "unshare", "--agent-id", agent, "--user", Guid.NewGuid().ToString(), "--json", "--non-interactive"], "agent unshare"),
            (["agent", "eval", "list", "--agent-id", agent, "--json", "--non-interactive"], "agent eval list"),
            (["agent", "eval", "testset", "list", "--agent-id", agent, "--json", "--non-interactive"], "agent eval testset list"),
            (["deployment", "channel", "list", "--agent-id", agent, "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], "deployment channel list"),
            (["deployment", "oauth", "list", "--agent-id", agent, "--deployment-name", SwSteps.Slot, "--json", "--non-interactive"], "deployment oauth list"),
            (["agent", "deploy", "--agent-id", agent, "--deployment-name", SwSteps.Slot, "--schema-name", "ahe2e_x", "--json", "--non-interactive"], "deploy --schema-name"),
        };

        foreach (var (arguments, what) in commands)
        {
            var before = SwChecks.Snapshot(workspace.ProjectDirectory);
            var result = await SwSteps.RunAsync(run, arguments, workspace.ProjectDirectory, timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);

            run.ExpectExit(result, ExitCodes.Usage, $"{what} on a workflow record is a usage error", "P2");
            run.Expect(result.Output.Contains("workflow", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("MCSWorkflow", StringComparison.OrdinalIgnoreCase)
                || result.Output.Contains("not supported", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("only", StringComparison.OrdinalIgnoreCase),
                $"{what} says why a workflow cannot use it", "a message naming the workflow or the supported type", SwRun.Tail(result), "P2");
            SwChecks.ExpectUnchanged(run, before, workspace.ProjectDirectory, $"{what} changed nothing locally");
        }

        await SwChecks.ExpectCloudAbsentAsync(run, workspace.WorkflowId, "none of the refused commands created the flow", severity: "P0").ConfigureAwait(false);
    }

    private static async Task CreateOptionsAsync(SwRun run)
    {
        var options = new (string[] Arguments, string What)[]
        {
            (["--mcs-agent-type", "CLI"], "--mcs-agent-type"),
            (["--initial-prompt", "be helpful"], "--initial-prompt"),
            (["--init-template", "default"], "--init-template"),
            (["--schema-name", "ahe2e_schema"], "--schema-name"),
            (["--mcs-clone-agent-id", Guid.NewGuid().ToString()], "--mcs-clone-agent-id"),
            (["--mcs-clone-agent-schemaname", "ahe2e_schema"], "--mcs-clone-agent-schemaname"),
        };

        foreach (var (arguments, what) in options)
        {
            var directory = run.Context.Workspaces.EmptyDirectory(run.Id, what.TrimStart('-'));
            var result = await SwSteps.RawScaffoldAsync(run, directory, SwFixtures.NameFor(run, what.TrimStart('-')), arguments).ConfigureAwait(false);

            run.ExpectExit(result, ExitCodes.Usage, $"agent create --agent-type MCSWorkflow {what} is a usage error", "P2");
            run.Expect(SwChecks.Snapshot(directory).Count == 0, $"the refused {what} wrote nothing", "an empty directory", string.Join(", ", SwChecks.Snapshot(directory).Keys.Take(5)), "P1");
        }
    }

    private static async Task CloneWithoutTypeAsync(SwRun run)
    {
        var directory = run.Context.Workspaces.EmptyDirectory(run.Id, "untyped");
        var result = await SwSteps.RunAsync(run, ["agent", "create", "--mcs-clone-workflow-id", Guid.NewGuid().ToString(), "--mcs-clone-environment-id", run.Dev, "--json", "--non-interactive"], directory).ConfigureAwait(false);

        run.ExpectExit(result, ExitCodes.Usage, "a workflow clone id without --agent-type is a usage error", "P2");
        run.Expect(!result.Output.Contains("--agent-type MCSAgent", StringComparison.OrdinalIgnoreCase), "the guidance does not send a workflow user to --agent-type MCSAgent", "--agent-type MCSWorkflow suggested", SwRun.Tail(result), "P2", "#34");
        run.Expect(SwChecks.Snapshot(directory).Count == 0, "the refused clone wrote nothing", "an empty directory", string.Join(", ", SwChecks.Snapshot(directory).Keys.Take(5)), "P1");
    }
}
