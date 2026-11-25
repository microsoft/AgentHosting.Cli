using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The home environment of an AMS command with the data-plane and control-plane endpoints that reach it.</summary>
internal sealed record AmsHome(PowerPlatformEnvironment Environment, AmsServiceTarget DataPlane, AmsServiceTarget ControlPlane);

/// <summary>An AMS agent resolved in its home environment.</summary>
internal sealed record AmsAgentAddress(AmsHome Home, string AgentId)
{
    /// <summary>Gets the agent on the data plane of its home environment.</summary>
    public AmsAgentReadTransportRequest OnDataPlane => new() { ServiceTarget = Home.DataPlane, AgentId = AgentId };

    /// <summary>Gets the agent on the control plane, addressed through its home environment.</summary>
    public AmsAgentReadTransportRequest OnControlPlane => new() { ServiceTarget = Home.ControlPlane, AgentId = AgentId };
}

/// <summary>An AMS deployment resolved under its agent, with its list entry when it was resolved by name.</summary>
internal sealed record AmsDeploymentAddress(AmsAgentAddress Agent, AmsDeploymentReadTransportRequest Reference, AmsDeploymentResult? Listed);

/// <summary>Resolves the agent and deployment an AMS command names, by id without a call or by name through one paged list.</summary>
public sealed class AmsAddressResolver
{
    private readonly AmsTargets _targets;
    private readonly EnvironmentSelectionService _environments;
    private readonly IAmsAgentReadTransport _agents;
    private readonly IAmsDeploymentTransport _deployments;

    /// <summary>Creates the resolver over the AMS agent and deployment lists.</summary>
    public AmsAddressResolver(AmsTargets targets, EnvironmentSelectionService environments, IAmsAgentReadTransport agents, IAmsDeploymentTransport deployments)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
    }

    /// <summary>Reports whether a stored name is the requested AMS name, comparing both after trimming and lowering them in the invariant culture.</summary>
    internal static bool SameName(string? stored, string requested) => stored is not null && string.Equals(stored.Trim().ToLowerInvariant(), requested.Trim().ToLowerInvariant(), StringComparison.Ordinal);

    /// <summary>Returns the canonical form of a non-empty GUID option, or throws a usage error naming the option.</summary>
    internal static string RequireGuid(string value, string option) => Guid.TryParse(value.Trim(), out var parsed) && parsed != Guid.Empty ? parsed.ToString("D") : throw new UsageException($"{option} must be a non-empty GUID.");

    /// <summary>Resolves the home environment, the active one when none is named.</summary>
    internal async Task<AmsHome> ResolveHomeAsync(string? environmentId, CancellationToken cancellationToken)
    {
        var environment = await _environments.ResolveForCommandAsync(environmentId, cancellationToken).ConfigureAwait(false);
        return new AmsHome(environment, _targets.DataPlane(environment), _targets.ControlPlane(environment));
    }

    /// <summary>Resolves a deployment's target environment in the home environment's sign-in and tenant.</summary>
    internal async Task<PowerPlatformEnvironment> ResolveTargetEnvironmentAsync(AmsHome home, string targetEnvironmentId, CancellationToken cancellationToken)
    {
        var target = await _environments.ResolveForProfileAsync(ResourceTokenContext.Require(home.Environment.Context).Profile, targetEnvironmentId, cancellationToken).ConfigureAwait(false);
        return string.Equals(home.Environment.TenantId, target.TenantId, StringComparison.OrdinalIgnoreCase) ? target : throw new UsageException("--target-environment-id must belong to the same tenant as the agent's home environment.");
    }

    /// <summary>Resolves the agent a request names in its home environment.</summary>
    internal async Task<AmsAgentAddress> ResolveAgentAsync(AgentAmsRequest request, string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = Select(request.AgentId, request.AgentName, "--agent-id", "--agent-name", operation);
        return await ResolveAgentAsync(await ResolveHomeAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false), agent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the deployment a request names under its agent.</summary>
    internal async Task<AmsDeploymentAddress> ResolveDeploymentAsync(AgentDeploymentAmsRequest request, string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agentSelector = Select(request.AgentId, request.AgentName, "--agent-id", "--agent-name", operation);
        var deploymentSelector = Select(request.DeploymentId, request.DeploymentName, "--deployment-id", "--deployment-name", operation);
        var agent = await ResolveAgentAsync(await ResolveHomeAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false), agentSelector, cancellationToken).ConfigureAwait(false);
        if (deploymentSelector.Id is { } deploymentId)
        {
            return new AmsDeploymentAddress(agent, Reference(agent, deploymentId), Listed: null);
        }

        var name = deploymentSelector.Name!;
        var listed = Single((await ListDeploymentsAsync(agent, cancellationToken).ConfigureAwait(false)).Deployments, deployment => deployment.DeploymentName, name, matches => ManagedAgentException.AmsDeploymentAmbiguous(name, matches.Select(deployment => deployment.DeploymentId)), () => ManagedAgentException.AmsDeploymentNotFound(name, agent.AgentId));
        return new AmsDeploymentAddress(agent, Reference(agent, listed.DeploymentId), listed);
    }

    /// <summary>Resolves a deployment whose bot a channel or OAuth command manages, refusing one that its list entry shows was never deployed.</summary>
    internal async Task<AmsDeploymentReadTransportRequest> ResolveBotDeploymentAsync(AgentDeploymentAmsRequest request, string operation, CancellationToken cancellationToken)
    {
        var address = await ResolveDeploymentAsync(request, operation, cancellationToken).ConfigureAwait(false);
        return address.Listed is { State: AmsDeploymentResult.NotDeployedState } listed ? throw AgentDeployException.ServiceRejected($"Deployment {listed.DeploymentName} ({listed.DeploymentId}) is not deployed, so it has no bot whose channels or OAuth connections can be managed.", ExitCodes.Failure, "deployment-not-deployed", "Deploy it first with `msagent agent deploy`.") : address.Reference;
    }

    /// <summary>Returns the deployment an address names: its list entry when it was resolved by name, else one read.</summary>
    internal async Task<AmsDeploymentResult> ReadDeploymentAsync(AmsDeploymentAddress address, CancellationToken cancellationToken) => AmsDeploymentView.RequireAddressed(address.Reference, address.Listed ?? await _deployments.ReadAsync(address.Reference, cancellationToken).ConfigureAwait(false), "Deployment read");

    /// <summary>Lists the deployments of an agent with the bots it keeps, refusing an entry of another agent.</summary>
    internal async Task<AmsDeploymentListResult> ListDeploymentsAsync(AmsAgentAddress agent, CancellationToken cancellationToken)
    {
        var list = await _deployments.ListAsync(agent.OnControlPlane, cancellationToken).ConfigureAwait(false);
        foreach (var deployment in list.Deployments)
        {
            AmsDeploymentView.RequireAddressed(Reference(agent, deployment.DeploymentId), deployment, "Deployment list");
        }

        return list;
    }

    /// <summary>Lists the deployments of an agent, or returns null when AMS does not list deployments in the environment yet.</summary>
    internal async Task<AmsDeploymentListResult?> TryListDeploymentsAsync(AmsAgentAddress agent, CancellationToken cancellationToken)
    {
        try
        {
            return await ListDeploymentsAsync(agent, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentDeployException exception) when (exception.ErrorKind == AmsRefusals.LifecycleUnavailableKind)
        {
            return null;
        }
    }

    private async Task<AmsAgentAddress> ResolveAgentAsync(AmsHome home, (string? Id, string? Name) selector, CancellationToken cancellationToken)
    {
        if (selector.Id is { } agentId)
        {
            return new AmsAgentAddress(home, agentId);
        }

        var name = selector.Name!;
        var codeAgents = (await _agents.ListAsync(home.DataPlane, cancellationToken).ConfigureAwait(false)).Where(agent => string.Equals(agent.AgentType, "CodeAgent", StringComparison.Ordinal));
        return new AmsAgentAddress(home, Single(codeAgents, agent => agent.DisplayName, name, matches => ManagedAgentException.AmsAgentAmbiguous(name, matches.Select(agent => agent.AgentId)), () => ManagedAgentException.AmsAgentNotFound(name, home.Environment.EnvironmentId)).AgentId);
    }

    private static AmsDeploymentReadTransportRequest Reference(AmsAgentAddress agent, string deploymentId) => new() { ServiceTarget = agent.Home.ControlPlane, AgentId = agent.AgentId, DeploymentId = deploymentId };

    private static T Single<T>(IEnumerable<T> items, Func<T, string?> name, string requested, Func<IReadOnlyList<T>, Exception> ambiguous, Func<Exception> notFound)
    {
        var matches = items.Where(item => SameName(name(item), requested)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw notFound(),
            _ => throw ambiguous(matches),
        };
    }

    private static (string? Id, string? Name) Select(string? id, string? name, string idOption, string nameOption, string operation) =>
        !string.IsNullOrWhiteSpace(id) ? (RequireGuid(id, idOption), null)
        : !string.IsNullOrWhiteSpace(name) ? (null, name.Trim())
        : throw new UsageException($"{idOption} or {nameOption} is required for `msagent {operation}`.");
}
