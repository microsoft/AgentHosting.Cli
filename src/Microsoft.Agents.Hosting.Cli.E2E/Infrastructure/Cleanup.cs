using System.Collections.Concurrent;
using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.CopilotStudio.Sync.Copy;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>What the copy-workflow sweep does in one environment.</summary>
/// <param name="Gone">The recorded copy workflows the listing proves gone.</param>
/// <param name="Remaining">The recorded copy workflows still listed, or all of them when the listing is unknown.</param>
/// <param name="Unproven">The listed workflows named after this run's copies that no ledger entry records, which the sweep leaves in place.</param>
public sealed record CopyWorkflowSweep(IReadOnlyList<LedgerEntry> Gone, IReadOnlyList<LedgerEntry> Remaining, IReadOnlyList<(string WorkflowId, string DisplayName)> Unproven);

/// <summary>Removes everything the ledger records, and only that.</summary>
public sealed partial class Cleanup
{
    /// <summary>How long a stopped run is given to reach a state the service will delete.</summary>
    private static readonly TimeSpan RunTerminalWait = TimeSpan.FromMinutes(3);

    /// <summary>How long to wait between attempts to delete a run that is still stopping.</summary>
    private static readonly TimeSpan RunTerminalPollInterval = TimeSpan.FromSeconds(10);

    private const int WorkflowDeleteParallelism = 4;
    private const string CopyNamePrefix = "ahe2e ";

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

    public Cleanup(MsagentCli msagent, AzureCli azure, CloudProbe cloud, Transcript transcript, StandaloneServices? standalone = null)
    {
        _msagent = msagent;
        _azure = azure;
        _cloud = cloud;
        _transcript = transcript;
        _standalone = standalone;
    }

    /// <summary>Walks the ledger newest-first within each cleanup phase, removing or restoring each outstanding entry, then removes the recorded copy workflows still listed and notes the unrecorded ones named after this run's copies.</summary>
    public async Task<IReadOnlyList<LedgerEntry>> RunAsync(Ledger ledger, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ledger.Outstanding.Where(candidate => candidate.Kind != ResourceKind.McsCopyWorkflow).OrderBy(CleanupPhase).ThenByDescending(candidate => candidate.Sequence))
        {
            await RemoveAsync(ledger, entry, cancellationToken).ConfigureAwait(false);
        }

        await SweepCopyWorkflowsAsync(ledger, cancellationToken).ConfigureAwait(false);

        return ledger.Entries.Where(entry => entry.State == ResourceState.Orphaned).ToArray();
    }

    /// <summary>Sorts one environment's outstanding copy workflows by what its listing proves, and finds the listed workflows named after this run's copies that no ledger entry records.</summary>
    /// <param name="ledger">The run's ledger.</param>
    /// <param name="environmentId">The environment that was listed.</param>
    /// <param name="listed">The environment's workflows, or null when the listing failed or was truncated.</param>
    /// <returns>The recorded copy workflows proven gone, those cleanup still removes, and the unrecorded ones it leaves in place.</returns>
    public static CopyWorkflowSweep PlanCopyWorkflowSweep(Ledger ledger, string environmentId, IReadOnlyList<(string WorkflowId, string DisplayName)>? listed)
    {
        var recorded = ledger.Outstanding.Where(entry => entry.Kind == ResourceKind.McsCopyWorkflow && string.Equals(entry.Cleanup.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (listed is null)
        {
            return new CopyWorkflowSweep([], recorded, []);
        }

        var listedIds = listed.Select(workflow => workflow.WorkflowId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = recorded.ToLookup(entry => listedIds.Contains(entry.Id));
        var marker = $" {ledger.RunId[^4..]} ";
        var known = ledger.Entries.SelectMany(entry => new[] { entry.Id, entry.Cleanup.WorkflowId }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new CopyWorkflowSweep([.. present[false]], [.. present[true]], [.. listed.Where(workflow => workflow.DisplayName.StartsWith(CopyNamePrefix, StringComparison.Ordinal) && workflow.DisplayName.Contains(marker, StringComparison.Ordinal) && !known.Contains(workflow.WorkflowId))]);
    }

    private static int CleanupPhase(LedgerEntry entry) => entry.Kind switch
    {
        ResourceKind.StandaloneConnectionReference => 1,
        ResourceKind.LocalWorkspace => 2,
        _ => 0,
    };

    private async Task SweepCopyWorkflowsAsync(Ledger ledger, CancellationToken cancellationToken)
    {
        foreach (var environment in ledger.Entries.Where(entry => entry.Kind is ResourceKind.McsCopy or ResourceKind.McsCopyWorkflow).Select(entry => entry.Cleanup.EnvironmentId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var listed = await _cloud.CompleteWorkflowsAsync(environment, cancellationToken).ConfigureAwait(false);
            var sweep = PlanCopyWorkflowSweep(ledger, environment, listed);

            if (listed is null)
            {
                _transcript.Note($"the workflows in {environment} could not be listed in full, so each recorded copy workflow there is removed by id and no unrecorded one can be reported");
            }

            foreach (var entry in sweep.Gone)
            {
                ledger.MarkDeleted(entry.Id);
            }

            await Parallel.ForEachAsync(sweep.Remaining, new ParallelOptions { MaxDegreeOfParallelism = WorkflowDeleteParallelism, CancellationToken = cancellationToken }, async (entry, token) => await RemoveAsync(ledger, entry, token).ConfigureAwait(false)).ConfigureAwait(false);

            foreach (var workflow in sweep.Unproven)
            {
                _transcript.Note($"UNPROVEN: workflow {workflow.WorkflowId} ({workflow.DisplayName}) in {environment} is named after this run's copies, but no ledger entry records it, so cleanup leaves it in place");
            }
        }
    }

    private async Task RemoveAsync(Ledger ledger, LedgerEntry entry, CancellationToken cancellationToken)
    {
        _transcript.Note($"cleanup {entry.Kind} {entry.Id} (created by {entry.CreatedBy})");

        try
        {
            var failure = entry.Kind switch
            {
                ResourceKind.McsAgent => await RemoveMcsAgentAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.McsWorkflow => await RemoveMcsWorkflowAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.McsCopy => await RemoveCopyAsync(ledger, entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.McsCopyWorkflow => entry.Cleanup.EnvironmentId is { Length: > 0 } environment ? await RemoveWorkflowsAsync([entry.Id], environment, cancellationToken).ConfigureAwait(false) : "the ledger entry has no environment",
                ResourceKind.AzureBot => await RemoveAzureBotAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.EntraApp => await RemoveEntraAppAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.LocalWorkspace => RemoveLocalWorkspace(entry, ledger),
                ResourceKind.ConfigEdit => await RestoreConfigEditAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.EvaluationTestSet => await RemoveEvaluationTestSetAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.EvaluationRun => await RemoveEvaluationRunAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.StandaloneWorkflow => await RemoveStandaloneWorkflowAsync(entry, cancellationToken).ConfigureAwait(false),
                ResourceKind.StandaloneConnectionReference => await RemoveStandaloneConnectionReferenceAsync(entry, cancellationToken).ConfigureAwait(false),
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
            return entry.Cleanup.EnvironmentId is not { Length: > 0 } environment || await AgentListedAsync(entry, environment, cancellationToken).ConfigureAwait(false) ? $"the project directory {project} is gone, so the CLI cannot address the agent" : null;
        }

        var result = await _msagent.RunAsync(
            ["deployment", "delete", "--deployment-name", deployment, "--delete-deployed-mcs-agent"],
            project,
            stdin: deployment,
            TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return $"deployment delete exited {result.ExitCode}: {Summarize(result)}";
        }

        return await StillPresentAsync(entry, cancellationToken).ConfigureAwait(false)
            ? "the command succeeded but the agent is still listed"
            : null;
    }

    private async Task<string?> RemoveCopyAsync(Ledger ledger, LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.ProjectDirectory is not { Length: > 0 } project || entry.Cleanup.AgentId is not { Length: > 0 } managedAgentId || entry.Cleanup.EnvironmentId is not { Length: > 0 } environment)
        {
            return "the ledger entry has no project directory, record, or environment";
        }

        if (!Directory.Exists(project))
        {
            return await AgentListedAsync(entry, environment, cancellationToken).ConfigureAwait(false) ? $"the project directory {project} is gone, so the CLI cannot address the copy" : null;
        }

        var deleted = await _msagent.RunAsync(["agent", "delete", "--agent-id", managedAgentId, "--all-resources"], project, entry.DisplayName, TimeSpan.FromMinutes(10), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (deleted.ExitCode == 0)
        {
            return null;
        }

        if (await AgentListedAsync(entry, environment, cancellationToken).ConfigureAwait(false))
        {
            return $"the copy's agent is still listed after agent delete --all-resources exited {deleted.ExitCode}: {Summarize(deleted)}";
        }

        IReadOnlyList<AgentWorkspaceResource> rows = entry.Cleanup.WorkspaceDirectory is { Length: > 0 } workspace && Directory.Exists(workspace) ? AgentWorkspaceResources.List(workspace) : [];

        foreach (var row in rows.Where(row => row.Kind != CopyIdentityKinds.Workflow))
        {
            ledger.RecordRetainedCopyRow(row.Kind, row.Identity, environment, entry.Id, entry.CreatedBy, $"agent delete --all-resources exited {deleted.ExitCode}: {Summarize(deleted)}");
        }

        return await RemoveWorkflowsAsync([.. rows.Where(row => row.Kind == CopyIdentityKinds.Workflow).Select(row => row.Identity)], environment, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AgentListedAsync(LedgerEntry entry, string environment, CancellationToken cancellationToken)
    {
        var (withId, withSchemaName) = await _cloud.CountAgentsAsync(environment, entry.Id, entry.Cleanup.SchemaName ?? entry.Id, cancellationToken).ConfigureAwait(false);

        return withId + withSchemaName > 0;
    }

    private async Task<string?> RemoveWorkflowsAsync(IReadOnlyList<string> workflowIds, string environment, CancellationToken cancellationToken)
    {
        if (workflowIds.Count == 0)
        {
            return null;
        }

        var scratch = Directory.CreateTempSubdirectory("ahe2e-teardown-").FullName;
        var failures = new ConcurrentBag<string>();

        try
        {
            await Parallel.ForEachAsync(workflowIds, new ParallelOptions { MaxDegreeOfParallelism = WorkflowDeleteParallelism, CancellationToken = cancellationToken }, async (workflowId, token) =>
            {
                if (await DeleteWorkflowAsync(_msagent, workflowId, environment, Path.Combine(scratch, workflowId), token).ConfigureAwait(false) is { } failure && !await WorkflowGoneAsync(environment, workflowId, token).ConfigureAwait(false))
                {
                    failures.Add(failure);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            FileTree.TryDeleteDirectory(scratch);
        }

        return failures.IsEmpty ? null : string.Join("; ", failures);
    }

    /// <summary>Deletes a standalone workflow by cloning a local record that addresses it into a directory, then deleting that record's cloud flow; returns why it failed, or null.</summary>
    /// <param name="msagent">Runs the clone and the delete.</param>
    /// <param name="workflowId">The workflow to delete.</param>
    /// <param name="environment">The environment that holds it.</param>
    /// <param name="directory">The directory the clone is written into, which the caller removes.</param>
    /// <param name="cancellationToken">Cancels the commands.</param>
    /// <returns>Why the workflow could not be deleted, or null when it was deleted or was already gone.</returns>
    public static async Task<string?> DeleteWorkflowAsync(MsagentCli msagent, string workflowId, string environment, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        var (cloned, addressed) = await RetriedAsync(() => msagent.RunAsync(["agent", "create", "--agent-type", AgentTypes.McsWorkflow, "--mcs-clone-workflow-id", workflowId, "--mcs-clone-environment-id", environment, "--environment-id", environment, "--json", "--non-interactive"], directory, timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken), SucceededOrGone, cancellationToken).ConfigureAwait(false);

        if (cloned.ExitCode != 0)
        {
            return addressed ? null : $"the clone made to delete workflow {workflowId} exited {cloned.ExitCode}: {Summarize(cloned)}";
        }

        if (CreatedRecord(cloned) is not { AgentId.Length: > 0 } created)
        {
            return $"the clone made to delete workflow {workflowId} reported no readable record: {Summarize(cloned)}";
        }

        var (deleted, settled) = await RetriedAsync(() => msagent.RunAsync(created.DeploymentName is { Length: > 0 } deployment ? ["agent", "delete", "--agent-id", created.AgentId, "--deployment-name", deployment, "--json", "--non-interactive"] : ["agent", "delete", "--agent-id", created.AgentId, "--json", "--non-interactive"], created.ProjectDirectory is { Length: > 0 } project && Directory.Exists(project) ? project : directory, created.DisplayName, TimeSpan.FromMinutes(5), cancellationToken: cancellationToken), SucceededOrGone, cancellationToken).ConfigureAwait(false);

        return settled ? null : $"agent delete through the clone of workflow {workflowId} exited {deleted.ExitCode}: {Summarize(deleted)}";
    }

    private static ManagedAgentCreateResult? CreatedRecord(MsagentResult cloned)
    {
        try
        {
            return cloned.Json<ManagedAgentCreateResult>();
        }
        catch (E2EException)
        {
            return null;
        }
    }

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

        var failure = Directory.Exists(project) ? await DeleteThroughRecordAsync(entry, project, agentId, cancellationToken).ConfigureAwait(false) : $"the project directory {project} is gone, so the CLI cannot address the workflow";

        if (failure is null || entry.Cleanup.EnvironmentId is not { Length: > 0 } environment)
        {
            return failure;
        }

        var fallback = await RemoveWorkflowsAsync([entry.Id], environment, cancellationToken).ConfigureAwait(false);

        return fallback is null ? null : $"{failure}; deleting it through a fresh clone also failed: {fallback}";
    }

    private async Task<string?> DeleteThroughRecordAsync(LedgerEntry entry, string project, string agentId, CancellationToken cancellationToken)
    {
        string[] selector = entry.Cleanup.DeploymentName is { Length: > 0 } deployment
            ? ["agent", "delete", "--agent-id", agentId, "--deployment-name", deployment]
            : ["agent", "delete", "--agent-id", agentId];

        var (result, settled) = await RetriedAsync(() => _msagent.RunAsync(selector, project, stdin: entry.DisplayName ?? agentId, TimeSpan.FromMinutes(5), cancellationToken: cancellationToken), async _ => !await WorkflowStillPresentAsync(entry, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        return settled ? null : result.ExitCode == 0 ? "the command succeeded but the workflow is still listed" : $"agent delete exited {result.ExitCode}: {Summarize(result)}";
    }

    private static Task<bool> SucceededOrGone(MsagentResult result) => Task.FromResult(result.ExitCode == 0 || IsAlreadyGone(result));

    private static Task<(MsagentResult Result, bool Settled)> RetriedAsync(Func<Task<MsagentResult>> run, Func<MsagentResult, Task<bool>> settled, CancellationToken cancellationToken) => MsagentCli.RetriedAsync(run, settled, DeleteAttempts, DeleteRetryDelay, cancellationToken);

    private async Task<bool> WorkflowGoneAsync(string environment, string workflowId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + WorkflowDeleteSettleWait;

        while (await _cloud.WorkflowExistsAsync(environment, workflowId, cancellationToken).ConfigureAwait(false) != false)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(DeleteRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private const int DeleteAttempts = 2;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WorkflowDeleteSettleWait = TimeSpan.FromMinutes(3);

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
        || result.Output.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("workflow-source-not-found", StringComparison.Ordinal);

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
        if (HoldsOrphan(ledger, entry.Id))
        {
            return "kept, because an agent deployed from it could not be deleted and the CLI addresses that agent through this directory";
        }

        if (Directory.Exists(entry.Id))
        {
            Directory.Delete(entry.Id, recursive: true);
        }

        return null;
    }

    /// <summary>Whether a directory holds the project of an agent, workflow, or copy that cleanup could not delete.</summary>
    public static bool HoldsOrphan(Ledger ledger, string directory) => ledger.Entries.Any(candidate => candidate.Kind is ResourceKind.McsAgent or ResourceKind.McsWorkflow or ResourceKind.StandaloneWorkflow or ResourceKind.McsCopy && candidate.State == ResourceState.Orphaned && IsUnder(candidate.Cleanup.ProjectDirectory, directory));

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
        ResourceKind.McsAgent => $"cd {CommandLineArgument.Quote(entry.Cleanup.ProjectDirectory ?? string.Empty)} && msagent deployment delete --deployment-name {entry.Cleanup.DeploymentName} --delete-deployed-mcs-agent",
        ResourceKind.McsWorkflow => $"cd {CommandLineArgument.Quote(entry.Cleanup.ProjectDirectory ?? string.Empty)} && msagent agent delete --agent-id {entry.Cleanup.AgentId}",
        ResourceKind.McsCopy => $"cd {CommandLineArgument.Quote(entry.Cleanup.ProjectDirectory ?? string.Empty)} && msagent agent delete --agent-id {entry.Cleanup.AgentId} --all-resources",
        ResourceKind.McsCopyRow => $"delete {entry.DisplayName} {entry.Id} from environment {entry.Cleanup.EnvironmentId} in the maker portal; no copy record addresses it any more",
        ResourceKind.StandaloneWorkflow or ResourceKind.McsCopyWorkflow => $"msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id {entry.Cleanup.WorkflowId} --mcs-clone-environment-id {entry.Cleanup.EnvironmentId} --environment-id {entry.Cleanup.EnvironmentId} --project <empty directory>, then msagent agent delete in that directory",
        ResourceKind.StandaloneConnectionReference => $"declare {entry.Cleanup.Correlation} ({entry.Cleanup.ConnectorId}) in a workflow workspace with a deployment targeting {entry.Cleanup.EnvironmentId}, then msagent deployment connection remove --connection-reference {entry.Cleanup.Correlation} --force",
        ResourceKind.LocalWorkspace => $"remove the directory {entry.Id}",
        ResourceKind.ConfigEdit => $"msagent env select {entry.Cleanup.PriorValue}",
        ResourceKind.EvaluationTestSet => $"msagent agent eval testset delete --testset {entry.Id} --agent-id {entry.Cleanup.McsAgentId} --environment-id {entry.Cleanup.EnvironmentId} --force",
        ResourceKind.EvaluationRun => entry.Resolved
            ? $"msagent agent eval cancel --run-id {entry.Id} --agent-id {entry.Cleanup.McsAgentId} --environment-id {entry.Cleanup.EnvironmentId}, then the same with delete once it stops"
            : $"msagent agent eval list --agent-id {entry.Cleanup.McsAgentId} --environment-id {entry.Cleanup.EnvironmentId}, then cancel and delete any run named '{entry.Cleanup.Correlation}' (the run was never named, so {entry.Id} will not match)",
        _ => $"remove {entry.Kind} {entry.Id} by hand",
    };

    private static string Summarize(MsagentResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var trimmed = Redaction.Apply(output.Trim().ReplaceLineEndings(" "));

        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }
}
