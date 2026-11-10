using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Removes everything the ledger records, and only that.</summary>
public sealed partial class Cleanup
{
    /// <summary>How long a stopped run is given to reach a state the service will delete.</summary>
    private static readonly TimeSpan RunTerminalWait = TimeSpan.FromMinutes(3);

    /// <summary>How long to wait between attempts to delete a run that is still stopping.</summary>
    private static readonly TimeSpan RunTerminalPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>How many runs are searched when an intent was never given a run id.</summary>
    private const int CorrelationSearchLimit = 200;

    /// <summary>How far on either side of the intent a correlated run may claim to have started.</summary>
    private static readonly TimeSpan CorrelationClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>How long a run the service accepted is given to appear in its listing.</summary>
    private static readonly TimeSpan CorrelationWait = TimeSpan.FromMinutes(1);

    private readonly MsagentCli _msagent;
    private readonly AzureCli _azure;
    private readonly CloudProbe _cloud;
    private readonly Transcript _transcript;
    private readonly StandaloneServices? _standalone;
    private readonly AmsMcsServices? _amsMcs;

    public Cleanup(MsagentCli msagent, AzureCli azure, CloudProbe cloud, Transcript transcript, StandaloneServices? standalone = null, AmsMcsServices? amsMcs = null)
    {
        _msagent = msagent;
        _azure = azure;
        _cloud = cloud;
        _transcript = transcript;
        _standalone = standalone;
        _amsMcs = amsMcs;
    }

    /// <summary>Walks the ledger newest-first within each cleanup phase, removing or restoring each outstanding entry.</summary>
    public async Task<IReadOnlyList<LedgerEntry>> RunAsync(Ledger ledger, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ledger.Outstanding.OrderBy(CleanupPhase).ThenByDescending(candidate => candidate.Sequence))
        {
            await RemoveAsync(ledger, entry, cancellationToken).ConfigureAwait(false);
        }

        return ledger.Entries.Where(entry => entry.State == ResourceState.Orphaned).ToArray();
    }

    private static int CleanupPhase(LedgerEntry entry) => entry.Kind switch
    {
        ResourceKind.StandaloneConnectionReference => 1,
        ResourceKind.AmsFlow or ResourceKind.LeftoverBot or ResourceKind.GrsRepository => 1,
        ResourceKind.AmsConnectionReference => 2,
        ResourceKind.AmsConnector => 3,
        ResourceKind.LocalWorkspace => 2,
        _ => 0,
    };

    private async Task RemoveAsync(Ledger ledger, LedgerEntry entry, CancellationToken cancellationToken)
    {
        _transcript.Note($"cleanup {entry.Kind} {entry.Id} (created by {entry.CreatedBy})");

        try
        {
            if (entry.Kind == ResourceKind.GrsRepository && await AgentsInRepositoryAsync(entry, cancellationToken).ConfigureAwait(false) is { } agents)
            {
                ledger.MarkRetained(entry.Id, agents);
                return;
            }

            var failure = entry.Kind switch
            {
                ResourceKind.McsAgent => await RemoveMcsAgentAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.McsWorkflow => await RemoveMcsWorkflowAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.AzureBot => await RemoveAzureBotAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.EntraApp => await RemoveEntraAppAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.LocalWorkspace => RemoveLocalWorkspace(entry, ledger),
                ResourceKind.ConfigEdit => await RestoreConfigEditAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.EvaluationTestSet => await RemoveEvaluationTestSetAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.EvaluationRun => await RemoveEvaluationRunAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.StandaloneWorkflow => await RemoveStandaloneWorkflowAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.StandaloneConnectionReference => await RemoveStandaloneConnectionReferenceAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.AmsAgent => await RemoveAmsAgentAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.AmsFlow => await RemoveAmsFlowAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.LeftoverBot => await RemoveLeftoverBotAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.GrsRepository => await RemoveGrsRepositoryAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.AmsConnector => await RemoveAmsConnectorAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.AmsConnectionReference => await RemoveAmsConnectionReferenceAsync(entry, cancellationToken).ConfigureAwait(false),
                _ => $"no cleanup is defined for {entry.Kind}",
            };

            if (failure is null)
            {
                MarkRemoved(ledger, entry);
            }
            else
            {
                ledger.MarkOrphaned(entry.Id, failure);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ledger.MarkOrphaned(entry.Id, exception.Message);
        }
    }

    private static void MarkRemoved(Ledger ledger, LedgerEntry entry)
    {
        if (entry.Kind == ResourceKind.ConfigEdit)
        {
            ledger.MarkRestored(entry.Id);
        }
        else
        {
            ledger.MarkDeleted(entry.Id);
        }
    }

    private async Task<string?> RemoveMcsAgentAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.ProjectDirectory is not { Length: > 0 } project || entry.Cleanup.DeploymentName is not { Length: > 0 } deployment)
        {
            return "the ledger entry has no project directory or deployment name";
        }

        if (await NothingWasCreatedAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!Directory.Exists(project))
        {
            return $"the project directory {project} is gone, so the CLI cannot address the agent";
        }

        var result = await _msagent.RunAsync(
            ["deployment", "delete", "--deployment-name", deployment, "--delete-deployed-mcs-agent"],
            project,
            stdin: deployment,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return $"deployment delete exited {result.ExitCode}: {Summarize(result)}";
        }

        return await StillPresentAsync(entry, cancellationToken).ConfigureAwait(false)
            ? "the command succeeded but the agent is still listed"
            : null;
    }

    /// <summary>Deletes a standalone workflow through the local record that addresses it.</summary>
    private async Task<string?> RemoveMcsWorkflowAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.ProjectDirectory is not { Length: > 0 } project || entry.Cleanup.AgentId is not { Length: > 0 } agentId)
        {
            return "the ledger entry has no project directory or agent id";
        }

        if (!await WorkflowStillPresentAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!Directory.Exists(project))
        {
            return $"the project directory {project} is gone, so the CLI cannot address the workflow";
        }

        string? failure = null;
        string[] selector = entry.Cleanup.DeploymentName is { Length: > 0 } deployment
            ? ["agent", "delete", "--agent-id", agentId, "--deployment-name", deployment]
            : ["agent", "delete", "--agent-id", agentId];

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            var result = await _msagent.RunAsync(
                selector,
                project,
                stdin: entry.DisplayName ?? agentId,
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);

            if (!await WorkflowStillPresentAsync(entry, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            failure = result.ExitCode == 0
                ? "the command succeeded but the workflow is still listed"
                : $"agent delete exited {result.ExitCode}: {Summarize(result)}";
        }

        return failure;
    }

    /// <summary>
    /// How many times a workflow delete is attempted, because Dataverse drops a response often
    /// enough that one transient failure would otherwise be reported as a leaked flow.
    /// </summary>
    private const int DeleteAttempts = 2;

    /// <summary>Whether the flow is still there, treating an unknown environment as still present.</summary>
    private async Task<bool> WorkflowStillPresentAsync(LedgerEntry entry, CancellationToken cancellationToken) =>
        entry.Cleanup.EnvironmentId is not { Length: > 0 } environment
        || await _cloud.WorkflowExistsAsync(environment, entry.Id, cancellationToken).ConfigureAwait(false) != false;

    /// <summary>
    /// Whether an entry recorded before a deploy never became an agent.
    ///
    /// The intent is written before the creating call so a crash cannot hide a cloud agent. When the
    /// deploy instead failed outright there is nothing to remove, and running the delete would report a
    /// false orphan.
    /// </summary>
    private async Task<bool> NothingWasCreatedAsync(LedgerEntry entry, CancellationToken cancellationToken) =>
        IsUnresolved(entry)
        && entry.Cleanup.EnvironmentId is { Length: > 0 } environment
        && !await _cloud.SchemaNameExistsAsync(environment, entry.Id, cancellationToken).ConfigureAwait(false);

    private async Task<bool> StillPresentAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.EnvironmentId is not { Length: > 0 } environment)
        {
            return false;
        }

        return IsUnresolved(entry)
            ? await _cloud.SchemaNameExistsAsync(environment, entry.Id, cancellationToken).ConfigureAwait(false)
            : !await _cloud.AgentGoneAsync(environment, entry.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the entry is still keyed by the schema name a deploy never reported an id for.</summary>
    private static bool IsUnresolved(LedgerEntry entry) =>
        string.Equals(entry.Id, entry.Cleanup.SchemaName, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> RemoveAzureBotAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.ResourceGroup is not { Length: > 0 } resourceGroup)
        {
            return "the ledger entry has no resource group";
        }

        var subscriptionId = entry.Cleanup.SubscriptionId;
        string[] arguments = subscriptionId is { Length: > 0 }
            ? ["bot", "delete", "--name", entry.Id, "--resource-group", resourceGroup, "--subscription", subscriptionId]
            : ["bot", "delete", "--name", entry.Id, "--resource-group", resourceGroup];

        var result = await _azure.RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 || !await _cloud.BotExistsAsync(entry.Id, resourceGroup, subscriptionId, cancellationToken).ConfigureAwait(false)
            ? null
            : $"az bot delete exited {result.ExitCode}: {Summarize(result)}";
    }

    private async Task<string?> RemoveEntraAppAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        var result = await _azure.RunAsync(["ad", "app", "delete", "--id", entry.Id], cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 || IsAlreadyGone(result)
            ? null
            : $"az ad app delete exited {result.ExitCode}: {Summarize(result)}";
    }

    private static bool IsAlreadyGone(MsagentResult result) =>
        result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> RemoveEvaluationTestSetAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.EnvironmentId is not { Length: > 0 } environment || entry.Cleanup.McsAgentId is not { Length: > 0 } agentId)
        {
            return "the ledger entry has no environment id or agent id";
        }

        var result = await _msagent.RunAsync(
            ["agent", "eval", "testset", "delete", "--testset", entry.Id, "--agent-id", agentId, "--environment-id", environment, "--force", "--non-interactive"],
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 || IsAlreadyGone(result) || NoTestSetMatches(result)
            ? null
            : $"agent eval testset delete exited {result.ExitCode}: {Summarize(result)}";
    }

    /// <summary>Whether the delete was refused because the test set was never created, or is already gone.</summary>
    private static bool NoTestSetMatches(MsagentResult result) =>
        result.Output.Contains("No evaluation test set matches", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops an evaluation run, then removes it.
    /// </summary>
    private async Task<string?> RemoveEvaluationRunAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.EnvironmentId is not { Length: > 0 } environment || entry.Cleanup.McsAgentId is not { Length: > 0 } agentId)
        {
            return "the ledger entry has no environment id or agent id";
        }

        if (!entry.Resolved)
        {
            return await RemoveCorrelatedRunsAsync(entry, environment, agentId, cancellationToken).ConfigureAwait(false);
        }

        return await RemoveRunByIdAsync(entry.Id, environment, agentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RemoveRunByIdAsync(string runId, string environment, string agentId, CancellationToken cancellationToken)
    {
        async Task<MsagentResult> EvalAsync(string verb) => await _msagent.RunAsync(
            ["agent", "eval", verb, "--run-id", runId, "--agent-id", agentId, "--environment-id", environment, "--non-interactive"],
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var cancelled = await EvalAsync("cancel").ConfigureAwait(false);

        if (cancelled.ExitCode != 0 && !NothingToStop(cancelled))
        {
            return $"agent eval cancel exited {cancelled.ExitCode}: {Summarize(cancelled)}";
        }

        var deleted = await DeleteOnceTerminalAsync(EvalAsync, cancellationToken).ConfigureAwait(false);

        return deleted.ExitCode == 0 || NoRunMatches(deleted) || IsAlreadyGone(deleted)
            ? null
            : $"agent eval delete exited {deleted.ExitCode}: {Summarize(deleted)}";
    }

    /// <summary>Removes the runs carrying the name an unresolved intent was started under.</summary>
    private async Task<string?> RemoveCorrelatedRunsAsync(LedgerEntry entry, string environment, string agentId, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.Correlation is not { Length: > 0 } runName)
        {
            return $"the run was never named, and the entry records no name to find it by (intent {entry.Id})";
        }

        var matches = await CorrelatedRunsAsync(entry, environment, agentId, runName, cancellationToken).ConfigureAwait(false);

        if (matches.Failure is { } listingFailure)
        {
            return listingFailure;
        }

        var failures = new List<string>();

        foreach (var run in matches.Runs)
        {
            if (await RemoveRunByIdAsync(run.RunId, environment, agentId, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                failures.Add(failure);
            }
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private async Task<(IReadOnlyList<EvaluationRunRow> Runs, string? Failure)> CorrelatedRunsAsync(LedgerEntry entry, string environment, string agentId, string runName, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CorrelationWait;

        while (true)
        {
            var listed = await _msagent.RunAsync(
                ["agent", "eval", "list", "--agent-id", agentId, "--environment-id", environment, "--limit", $"{CorrelationSearchLimit}", "--json", "--non-interactive"],
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (listed.ExitCode != 0)
            {
                return ([], $"agent eval list exited {listed.ExitCode} while looking for runs named '{runName}': {Summarize(listed)}");
            }

            var result = listed.Json<EvaluationListResult>();

            var matches = result.Runs
                .Where(run => run.RunId.Length > 0
                    && string.Equals(run.Name, runName, StringComparison.OrdinalIgnoreCase)
                    && StartedWithTheIntent(run, entry))
                .ToArray();

            if (matches.Length > 0)
            {
                return (matches, null);
            }

            if (result.Truncated)
            {
                return ([], $"no run named '{runName}' was among the newest {CorrelationSearchLimit}, which stopped at the limit, so a charged run may still be running");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return ([], null);
            }

            await Task.Delay(RunTerminalPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a run could be the one this intent started.
    /// </summary>
    /// <param name="run">The run the listing reported.</param>
    /// <param name="entry">The ledger entry recording the intent to start one.</param>
    public static bool StartedWithTheIntent(EvaluationRunRow run, LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(entry);

        return run.StartTime is not { } startedAt
            || (startedAt >= entry.CreatedAt - CorrelationClockSkew && startedAt <= entry.CreatedAt + CorrelationClockSkew);
    }

    /// <summary>
    /// Deletes a stopped run, waiting for the service to finish with it first.
    /// </summary>
    private static async Task<MsagentResult> DeleteOnceTerminalAsync(Func<string, Task<MsagentResult>> evalAsync, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + RunTerminalWait;

        while (true)
        {
            var deleted = await evalAsync("delete").ConfigureAwait(false);

            if (deleted.ExitCode == 0 || !StillRunning(deleted) || DateTimeOffset.UtcNow >= deadline)
            {
                return deleted;
            }

            await Task.Delay(RunTerminalPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool StillRunning(MsagentResult result) =>
        result.Output.Contains("cannot be deleted while it is still running", StringComparison.OrdinalIgnoreCase);

    private static bool NothingToStop(MsagentResult result) =>
        NoRunMatches(result) || result.Output.Contains("is already", StringComparison.OrdinalIgnoreCase);

    private static bool NoRunMatches(MsagentResult result) =>
        result.Output.Contains("No evaluation run matches", StringComparison.OrdinalIgnoreCase);

    private static string? RemoveLocalWorkspace(LedgerEntry entry, Ledger ledger)
    {
        if (ledger.Entries.Any(candidate =>
                candidate.Kind is ResourceKind.McsAgent or ResourceKind.McsWorkflow or ResourceKind.StandaloneWorkflow or ResourceKind.AmsAgent
                && candidate.State == ResourceState.Orphaned
                && IsUnder(candidate.Cleanup.ProjectDirectory, entry.Id)))
        {
            return "kept, because an agent deployed from it could not be deleted and the CLI addresses that agent through this directory";
        }

        if (Directory.Exists(entry.Id))
        {
            LocalDirectories.Delete(entry.Id);
        }

        return null;
    }

    public static bool PointsInto(LedgerEntry entry, string directory) => IsUnder(entry.Cleanup.ProjectDirectory, directory) || (entry.Kind == ResourceKind.LocalWorkspace && IsUnder(entry.Id, directory));

    /// <summary>
    /// Whether an orphan's project is this workspace or sits inside it. The project a workflow is
    /// addressed through is the scaffolded directory itself as often as a child of it, so matching
    /// only the first child would delete the configuration the recorded recovery command needs.
    /// </summary>
    private static bool IsUnder(string? projectDirectory, string workspaceRoot)
    {
        if (projectDirectory is not { Length: > 0 })
        {
            return false;
        }

        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));

        return string.Equals(project, root, StringComparison.OrdinalIgnoreCase)
            || project.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> RestoreConfigEditAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.PriorValue is not { Length: > 0 } prior)
        {
            return null;
        }

        var result = await _msagent
            .RunAsync(["env", "select", prior, "--non-interactive"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0 ? null : $"could not restore the prior environment selection: {Summarize(result)}";
    }

    /// <summary>The command an operator runs by hand when cleanup left something behind.</summary>
    public static string ManualCommand(LedgerEntry entry) => entry.Kind switch
    {
        ResourceKind.AzureBot => $"az bot delete --name {entry.Id} --resource-group {entry.Cleanup.ResourceGroup} --subscription {entry.Cleanup.SubscriptionId} --yes",
        ResourceKind.EntraApp => $"az ad app delete --id {entry.Id}",
        ResourceKind.McsAgent => $"cd \"{entry.Cleanup.ProjectDirectory}\" && msagent deployment delete --deployment-name {entry.Cleanup.DeploymentName} --delete-deployed-mcs-agent",
        ResourceKind.McsWorkflow => $"cd \"{entry.Cleanup.ProjectDirectory}\" && msagent agent delete --agent-id {entry.Cleanup.AgentId}",
        ResourceKind.StandaloneWorkflow => $"msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id {entry.Cleanup.WorkflowId} --mcs-clone-environment-id {entry.Cleanup.EnvironmentId} --environment-id {entry.Cleanup.EnvironmentId} --project <empty directory>, then msagent agent delete in that directory",
        ResourceKind.StandaloneConnectionReference => $"declare {entry.Cleanup.Correlation} ({entry.Cleanup.ConnectorId}) in a workflow workspace with a deployment targeting {entry.Cleanup.EnvironmentId}, then msagent deployment connection remove --connection-reference {entry.Cleanup.Correlation} --force",
        ResourceKind.LocalWorkspace => $"remove the directory {entry.Id}",
        ResourceKind.ConfigEdit => $"msagent env select {entry.Cleanup.PriorValue}",
        ResourceKind.EvaluationTestSet => $"msagent agent eval testset delete --testset {entry.Id} --agent-id {entry.Cleanup.McsAgentId} --environment-id {entry.Cleanup.EnvironmentId} --force",
        ResourceKind.EvaluationRun => entry.Resolved
            ? $"msagent agent eval cancel --run-id {entry.Id} --agent-id {entry.Cleanup.McsAgentId} --environment-id {entry.Cleanup.EnvironmentId}, then the same with delete once it stops"
            : $"msagent agent eval list --agent-id {entry.Cleanup.McsAgentId} --environment-id {entry.Cleanup.EnvironmentId}, then cancel and delete any run named '{entry.Cleanup.Correlation}' (the run was never named, so {entry.Id} will not match)",
        ResourceKind.AmsAgent => $"{(entry.Cleanup.AmsOff ? string.Empty : $"with {GitCredentialHelper.FeatureVariable}={GitCredentialHelper.Feature} and the amsMcs variables set, ")}msagent deployment delete --project \"{entry.Cleanup.ProjectDirectory}\" --deployment-id <each test and prod deployment> [--delete-deployed-mcs-agent], then msagent agent delete --project \"{entry.Cleanup.ProjectDirectory}\" --agent-id {entry.Id} --deployment-id <its dev deployment>",
        ResourceKind.AmsFlow => $"turn off, then delete, the flow named exactly '{entry.Cleanup.Correlation}' in environment {entry.Cleanup.EnvironmentId}",
        ResourceKind.LeftoverBot => $"delete the bot whose schema name is {entry.Cleanup.SchemaName}, with its bot components, from the Dev, Test and Prod organizations",
        ResourceKind.GrsRepository => $"Invoke-WebRequest -Method Delete -Uri '{entry.Cleanup.GitUrl}' -Headers @{{ Authorization = 'Bearer <token>' }}, with <token> the password= line of msagent auth git-credential get, once agent list --view ams --repository-id {entry.Id} lists no agent",
        ResourceKind.AmsConnector => $"delete the custom connector named exactly '{entry.Cleanup.Correlation}' from environment {entry.Cleanup.EnvironmentId}, after its connection references",
        ResourceKind.AmsConnectionReference => $"delete the connection reference named exactly '{entry.Cleanup.Correlation}' from environment {entry.Cleanup.EnvironmentId}",
        _ => $"remove {entry.Kind} {entry.Id} by hand",
    };

    private static string Summarize(MsagentResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var trimmed = Redaction.Apply(output.Trim().ReplaceLineEndings(" "));

        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }
}
