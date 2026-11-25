using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Lists, reads, changes and deletes AMS code agents and lists their commits.</summary>
public sealed class AmsAgentService :
    ICommandHandler<AgentAmsListRequest, AgentAmsListResult>,
    ICommandHandler<AgentAmsShowRequest, AgentAmsShowResult>,
    ICommandHandler<AgentAmsUpdateRequest, AgentAmsUpdateResult>,
    ICommandHandler<AgentAmsDeleteRequest, AgentAmsDeleteResult>,
    ICommandHandler<AgentCommitListRequest, AgentCommitListResult>
{
    private readonly AmsAddressResolver _addresses;
    private readonly IAmsAgentReadTransport _agentReads;
    private readonly IAmsAgentTransport _agents;
    private readonly IConfirmationPrompt _confirmations;

    /// <summary>Creates the service over the AMS agent transports and the prompt that confirms an agent delete.</summary>
    public AmsAgentService(AmsAddressResolver addresses, IAmsAgentReadTransport agentReads, IAmsAgentTransport agents, IConfirmationPrompt confirmations)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _agentReads = agentReads ?? throw new ArgumentNullException(nameof(agentReads));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
    }

    /// <summary>Lists the code agents of the home environment.</summary>
    public async Task<AgentAmsListResult> ListAsync(AgentAmsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var home = await _addresses.ResolveHomeAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var agents = await _agentReads.ListAsync(home.DataPlane, cancellationToken).ConfigureAwait(false);
        return new AgentAmsListResult { EnvironmentId = home.Environment.EnvironmentId, Agents = [.. agents.Where(agent => string.Equals(agent.AgentType, "CodeAgent", StringComparison.Ordinal)).Select(ToRecord)] };
    }

    /// <summary>Reads the agent with a summary of its deployments and the bots kept from deleted records.</summary>
    public async Task<AgentAmsShowResult> ShowAsync(AgentAmsShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _addresses.ResolveAgentAsync(request, "agent show", cancellationToken).ConfigureAwait(false);
        var definition = await ReadCodeAgentAsync(agent, cancellationToken).ConfigureAwait(false);
        var deployments = await _addresses.TryListDeploymentsAsync(agent, cancellationToken).ConfigureAwait(false);
        return new AgentAmsShowResult { Agent = ToRecord(definition), DeploymentsAvailable = deployments is not null, Deployments = [.. (deployments?.Deployments ?? []).Select(AmsDeploymentView.ToRecord)], KeptBots = deployments?.KeptBots ?? [] };
    }

    /// <summary>Renames the agent or changes its description, guarded by the entity tag of the agent read.</summary>
    public async Task<AgentAmsUpdateResult> UpdateAsync(AgentAmsUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var newName = string.IsNullOrWhiteSpace(request.NewName) ? null : request.NewName.Trim();
        if (newName is null && request.Description is null)
        {
            throw new UsageException("--new-name or --description is required for `msagent agent update`.");
        }

        var agent = await _addresses.ResolveAgentAsync(request, "agent update", cancellationToken).ConfigureAwait(false);
        var current = await ReadCodeAgentAsync(agent, cancellationToken).ConfigureAwait(false);
        var updated = await _agents.UpdateAsync(new AmsAgentUpdateTransportRequest { ServiceTarget = agent.Home.DataPlane, AgentId = agent.AgentId, ETag = current.ETag, DisplayName = newName, Description = request.Description }, cancellationToken).ConfigureAwait(false);
        AmsAgentResponseValidator.ValidateCodeAgent(updated, agent.Home.Environment, agent.AgentId);
        return new AgentAmsUpdateResult { Status = AgentAmsUpdateResult.UpdatedStatus, Agent = ToRecord(updated) };
    }

    /// <summary>Deletes the agent with every deployment record and bot it owns, after the operator confirms with the agent's name.</summary>
    public async Task<AgentAmsDeleteResult> DeleteAsync(AgentAmsDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _addresses.ResolveAgentAsync(request, "agent delete", cancellationToken).ConfigureAwait(false);
        var current = await ReadCodeAgentAsync(agent, cancellationToken).ConfigureAwait(false);
        var deployments = await _addresses.ListDeploymentsAsync(agent, cancellationToken).ConfigureAwait(false);
        var name = current.DisplayName ?? current.AgentId;
        if (await AmsConfirmation.StoppedStatusAsync(_confirmations, request.NonInteractive, DeletePlan(current, deployments), name, cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return new AgentAmsDeleteResult { Status = stopped, AgentId = current.AgentId, DisplayName = current.DisplayName };
        }

        var deleted = await _agents.CascadeDeleteAsync(new AmsAgentCascadeDeleteTransportRequest { ServiceTarget = agent.Home.ControlPlane, AgentId = agent.AgentId, ETag = current.ETag }, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(deleted.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentDeployException.ResponseMismatch("Agent delete", "agentId");
        }

        return new AgentAmsDeleteResult { Status = AgentAmsDeleteResult.DeletedStatus, AgentId = current.AgentId, DisplayName = current.DisplayName, Deployments = deleted.Deployments, KeptBots = deleted.KeptBots };
    }

    /// <summary>Lists the agent's uploads, newest first, with the deployments that run each.</summary>
    public async Task<AgentCommitListResult> ListCommitsAsync(AgentCommitListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _addresses.ResolveAgentAsync(request, "agent commit list", cancellationToken).ConfigureAwait(false);
        var packages = await _agents.ListPackagesAsync(agent.OnDataPlane, cancellationToken).ConfigureAwait(false);
        var deployments = await _addresses.TryListDeploymentsAsync(agent, cancellationToken).ConfigureAwait(false);
        return new AgentCommitListResult { AgentId = agent.AgentId, DeploymentsAvailable = deployments is not null, Commits = [.. packages.OrderByDescending(package => package.CreatedTime).Select(package => ToCommit(package, deployments?.Deployments ?? []))] };
    }

    Task<AgentAmsListResult> ICommandHandler<AgentAmsListRequest, AgentAmsListResult>.HandleAsync(AgentAmsListRequest request, CancellationToken cancellationToken) => ListAsync(request, cancellationToken);

    Task<AgentAmsShowResult> ICommandHandler<AgentAmsShowRequest, AgentAmsShowResult>.HandleAsync(AgentAmsShowRequest request, CancellationToken cancellationToken) => ShowAsync(request, cancellationToken);

    Task<AgentAmsUpdateResult> ICommandHandler<AgentAmsUpdateRequest, AgentAmsUpdateResult>.HandleAsync(AgentAmsUpdateRequest request, CancellationToken cancellationToken) => UpdateAsync(request, cancellationToken);

    Task<AgentAmsDeleteResult> ICommandHandler<AgentAmsDeleteRequest, AgentAmsDeleteResult>.HandleAsync(AgentAmsDeleteRequest request, CancellationToken cancellationToken) => DeleteAsync(request, cancellationToken);

    Task<AgentCommitListResult> ICommandHandler<AgentCommitListRequest, AgentCommitListResult>.HandleAsync(AgentCommitListRequest request, CancellationToken cancellationToken) => ListCommitsAsync(request, cancellationToken);

    private async Task<AmsAgentReadResult> ReadCodeAgentAsync(AmsAgentAddress agent, CancellationToken cancellationToken)
    {
        var definition = await _agentReads.GetAsync(agent.OnDataPlane, cancellationToken).ConfigureAwait(false);
        AmsAgentResponseValidator.ValidateCodeAgent(definition, agent.Home.Environment, agent.AgentId);
        return definition;
    }

    private static IEnumerable<string> DeletePlan(AmsAgentReadResult agent, AmsDeploymentListResult deployments) =>
    [
        $"About to delete code agent {agent.DisplayName ?? agent.AgentId} ({agent.AgentId}) of environment {agent.EnvironmentId}:",
        .. deployments.Deployments.Select(deployment => $"  deployment {deployment.DeploymentName} ({deployment.DeploymentId}, {deployment.DeploymentType}) in {deployment.TargetEnvironmentId}{(deployment.BotService is null ? string.Empty : $", with bot {deployment.BotService.Name}")}"),
        .. deployments.KeptBots.Select(bot => $"  bot {TextGrid.DescribeOrDash(bot.BotName)} kept from deleted deployment {TextGrid.DescribeOrDash(bot.DeploymentName)} ({bot.DeploymentId})"),
        "  the agent record",
        string.Empty,
        "This deletes every bot of the agent with its channels and OAuth connections. Local files are not touched.",
    ];

    private static AgentCommitRecord ToCommit(AmsAgentPackage package, IReadOnlyList<AmsDeploymentResult> deployments) => new()
    {
        CommitId = package.CommitId,
        PackageRevisionId = package.PackageRevisionId,
        CreatedTime = package.CreatedTime,
        SizeInBytes = package.SizeInBytes,
        Sha256 = package.Sha256,
        Deployments = [.. deployments.Where(deployment => Runs(deployment, package)).Select(deployment => deployment.DeploymentName)],
    };

    private static bool Runs(AmsDeploymentResult deployment, AmsAgentPackage package) => (package.CommitId is not null && string.Equals(deployment.CommitId, package.CommitId, StringComparison.OrdinalIgnoreCase)) || string.Equals(deployment.PackageRevisionId, package.PackageRevisionId, StringComparison.OrdinalIgnoreCase);

    private static AgentAmsRecord ToRecord(AmsAgentReadResult agent) => new() { AgentId = agent.AgentId, DisplayName = agent.DisplayName, Description = agent.Description, AgentType = agent.AgentType, State = agent.State, IsDeleting = agent.IsDeleting, TenantId = agent.TenantId, EnvironmentId = agent.EnvironmentId, Language = agent.Language, LanguageVersion = agent.LanguageVersion, CreatedTime = agent.CreatedTime, ChangedTime = agent.ChangedTime };
}
