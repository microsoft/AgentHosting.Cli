using Microsoft.Agents.Hosting.Cli.Core.Ams;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>How long Agent Management Service cleanup waits while an agent delete waits for a deploy that may still run.</summary>
public sealed record AmsCleanupTiming(TimeSpan DrainRetryDelay, TimeSpan DrainLimit)
{
    /// <summary>Retries every 30 seconds for 15 minutes, longer than the service holds an interrupted deploy's admission.</summary>
    public static AmsCleanupTiming Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
}

public sealed partial class Cleanup
{
    private static readonly TimeSpan AmsReadTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan AmsDeleteTimeout = TimeSpan.FromMinutes(10);

    private async Task<string?> RemoveAmsAgentAsync(Ledger ledger, LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.Ams is not { HomeEnvironmentId: { Length: > 0 } home } context)
        {
            return "the ledger entry has no Agent Management Service context";
        }

        var msagent = _msagent.With(AmsServices.Overlay(context.ControlPlaneEndpoint, context.DataPlaneEndpoint));
        var status = await msagent.AuthStatusAsync(cancellationToken).ConfigureAwait(false);

        if (AmsServices.SignInMismatch(AmsServices.ReadSignIn(status), context.Cloud, context.TenantId) is { } mismatch)
        {
            return $"{mismatch}, where this agent was created; sign in there and run cleanup again";
        }

        if (!entry.Resolved)
        {
            var (agentId, failure) = await FindAmsAgentAsync(msagent, entry.Id, home, cancellationToken).ConfigureAwait(false);

            if (failure is not null || agentId is null)
            {
                return failure;
            }

            ledger.ResolveAmsAgent(entry.Id, agentId);
        }

        return await DeleteAmsAgentAsync(msagent, entry.Id, home, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string? AgentId, string? Failure)> FindAmsAgentAsync(MsagentCli msagent, string agentName, string home, CancellationToken cancellationToken)
    {
        var shown = await msagent.RunAsync(["agent", "show", "--agent-name", agentName, "--environment-id", home, "--json", "--non-interactive"], timeout: AmsReadTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return AmsOutput.Succeeded(shown) && AmsOutput.Document(shown) is { } root && AmsOutput.Text(root, "agent", "agentId") is { Length: > 0 } agentId ? (agentId, null)
            : AmsOutput.IsRefused(shown, AmsOutput.AgentNotFound) ? (null, null)
            : (null, $"agent show --agent-name {agentName} exited {shown.ExitCode}: {Summarize(shown)}");
    }

    private async Task<string?> DeleteAmsAgentAsync(MsagentCli msagent, string agentId, string home, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _amsTiming.DrainLimit;

        while (true)
        {
            var deleted = await msagent.RunAsync(["agent", "delete", "--agent-id", agentId, "--environment-id", home, "--json", "--non-interactive"], timeout: AmsDeleteTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (AmsOutput.IsRefused(deleted, AmsOutput.DeployInProgress) && DateTimeOffset.UtcNow < deadline)
            {
                _transcript.Note($"agent {agentId} waits for a deploy that may still run; trying again in {_amsTiming.DrainRetryDelay.TotalSeconds:N0}s");
                await Task.Delay(_amsTiming.DrainRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return AmsOutput.Succeeded(deleted) || AmsOutput.IsRefused(deleted, AmsOutput.AgentNotFound)
                ? await AmsAgentStillPresentAsync(msagent, agentId, home, cancellationToken).ConfigureAwait(false)
                : $"agent delete exited {deleted.ExitCode}: {Summarize(deleted)}";
        }
    }

    private static async Task<string?> AmsAgentStillPresentAsync(MsagentCli msagent, string agentId, string home, CancellationToken cancellationToken)
    {
        var shown = await msagent.RunAsync(["agent", "show", "--agent-id", agentId, "--environment-id", home, "--json", "--non-interactive"], timeout: AmsReadTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return AmsOutput.IsRefused(shown, AmsOutput.AgentNotFound) ? null
            : AmsOutput.Succeeded(shown) ? "agent delete finished, but agent show still finds the agent"
            : $"agent delete finished, but agent show could not confirm the agent is gone: exit {shown.ExitCode}: {Summarize(shown)}";
    }

    private async Task<(string? ApplicationId, string? Failure)> FindEntraAppAsync(string displayName, CancellationToken cancellationToken) =>
        await _cloud.AppRegistrationIdsAsync(displayName, cancellationToken).ConfigureAwait(false) switch
        {
            { Failure: { } failure } => (null, failure),
            { ApplicationIds: [] } => (null, null),
            { ApplicationIds: [var applicationId] } => (applicationId, null),
            { ApplicationIds: var applicationIds } => (null, $"{applicationIds.Count} App Registrations are named {displayName}, so cleanup cannot tell which one this run created"),
        };

    private static string AmsAgentManualCommand(LedgerEntry entry)
    {
        var context = entry.Cleanup.Ams ?? new AmsCleanupContext();
        var selector = entry.Resolved ? $"--agent-id {entry.Id}" : $"--agent-name {entry.Id}";

        return $"with {MsagentFeatures.Variable}={MsagentFeatures.Ams}, {AmsEndpointProfile.ControlPlaneEndpointVariable}={context.ControlPlaneEndpoint} and {AmsEndpointProfile.DataPlaneEndpointVariable}={context.DataPlaneEndpoint} set, signed in to the {context.Cloud} cloud and tenant {context.TenantId}: msagent agent delete {selector} --environment-id {context.HomeEnvironmentId} --non-interactive";
    }
}
