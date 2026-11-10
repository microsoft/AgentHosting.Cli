using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed partial class Cleanup
{
    private AmsMcsServices AmsMcs => _amsMcs ?? throw new E2EException("the AMS services aren't available, so cleanup can't reach AMS, GRS or Dataverse");

    private async Task<string?> RemoveAmsAgentAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(entry.Cleanup.ProjectDirectory))
        {
            return $"the project directory {entry.Cleanup.ProjectDirectory} is gone, so the CLI cannot address the agent";
        }

        var project = entry.Cleanup.ProjectDirectory;
        var config = AmsMcsFiles.ReadAgentConfig(project);

        if (config?.ManagedAgents.FirstOrDefault(agent => AmsIds.Same(agent.AgentId, entry.Id)) is not { } agent)
        {
            return null;
        }

        var msagent = entry.Cleanup.AmsOff ? AmsMcs.Off : AmsMcs.On;
        var deployments = config.AgentDeployments.Where(deployment => AmsIds.Same(deployment.AgentId, agent.AgentId)).ToArray();

        foreach (var deployment in deployments.Where(deployment => deployment.DeploymentType is DeploymentStages.Test or DeploymentStages.Prod))
        {
            string[] deployed = deployment.LastDeployedTime is not null && !AgentTypes.IsMcsWorkflow(agent.AgentType) ? ["--delete-deployed-mcs-agent"] : [];
            await msagent.RunAsync(["deployment", "delete", "--project", project, "--deployment-id", deployment.DeploymentId, .. deployed, "--json", "--non-interactive"], project, timeout: AmsMcsServices.CallTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string[] dev = deployments.FirstOrDefault(deployment => deployment.DeploymentType == DeploymentStages.Dev) is { } home ? ["--deployment-id", home.DeploymentId] : [];
        string[] delete = ["agent", "delete", "--project", project, "--agent-id", agent.AgentId, .. dev, "--json", "--non-interactive"];
        var deleted = await msagent.RunAsync(delete, project, timeout: AmsMcsServices.CallTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        var cache = Path.Combine(project, agent.AgentLocation, WorkflowStateCache.FolderName);

        if (deleted.ExitCode != 0 && Directory.Exists(cache))
        {
            LocalDirectories.Delete(cache);
            deleted = await msagent.RunAsync(delete, project, timeout: AmsMcsServices.CallTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return deleted.ExitCode == 0 ? null : $"agent delete exited {deleted.ExitCode}: {Summarize(deleted)}";
    }

    private Task<string?> RemoveAmsFlowAsync(LedgerEntry entry, CancellationToken cancellationToken) => RemoveNamedAsync(entry, "flow", AmsMcs.Dataverse.DeleteFlowsNamedAsync, cancellationToken);

    private async Task<string?> RemoveLeftoverBotAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        foreach (var environmentId in AmsMcs.Config.Environments.Select(environment => environment.Id).OfType<string>())
        {
            foreach (var bot in await AmsMcs.Dataverse.BotsNamedAsync(environmentId, entry.Id, cancellationToken).ConfigureAwait(false))
            {
                await AmsMcs.Dataverse.DeleteBotAsync(environmentId, bot.BotId, entry.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<string?> AgentsInRepositoryAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        var listed = await AmsMcs.ListAgentsAsync(entry.Cleanup.EnvironmentId!, entry.Id, null, cancellationToken).ConfigureAwait(false);

        if (listed.ExitCode != 0)
        {
            throw new E2EException($"agent list --view ams exited {listed.ExitCode}, so whether AMS still lists an agent in the repository is unknown: {Summarize(listed)}");
        }

        return listed.Json<ManagedAgentListResult>().Agents is { Count: > 0 } agents ? $"AMS still lists {string.Join(", ", agents.Select(agent => $"{agent.DisplayName} ({agent.AgentId})"))} in it, so it's kept for them" : null;
    }

    private async Task<string?> RemoveGrsRepositoryAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        var deleted = await OracleHttp.DeleteRepositoryAsync(new Uri(entry.Cleanup.GitUrl ?? throw new E2EException("the ledger entry has no git URL")), await AmsMcs.MsagentTokens.TokenAsync(AmsMcs.MsagentTokens.Audience, cancellationToken).ConfigureAwait(false), _transcript, cancellationToken, AmsMcs.Client).ConfigureAwait(false);
        var body = Redaction.Apply(deleted.Body.Trim().ReplaceLineEndings(" "));

        return deleted.Ok ? null : $"GRS refused the delete: {(int)deleted.Status} {(body.Length <= 200 ? body : body[..200] + "…")}";
    }

    private Task<string?> RemoveAmsConnectorAsync(LedgerEntry entry, CancellationToken cancellationToken) => RemoveNamedAsync(entry, "connector", AmsMcs.Dataverse.DeleteConnectorsNamedAsync, cancellationToken);

    private Task<string?> RemoveAmsConnectionReferenceAsync(LedgerEntry entry, CancellationToken cancellationToken) => RemoveNamedAsync(entry, "connection reference", AmsMcs.Dataverse.DeleteConnectionReferencesNamedAsync, cancellationToken);

    private static async Task<string?> RemoveNamedAsync<T>(LedgerEntry entry, string what, Func<string, string, CancellationToken, Task<IReadOnlyList<T>>> delete, CancellationToken cancellationToken) => await delete(entry.Cleanup.EnvironmentId!, entry.Cleanup.Correlation!, cancellationToken).ConfigureAwait(false) is { Count: > 0 } left ? $"{left.Count} {what}(s) named exactly '{entry.Cleanup.Correlation}' are still in {entry.Cleanup.EnvironmentId}" : null;
}
