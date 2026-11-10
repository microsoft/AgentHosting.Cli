using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>What <c>msagent agent create</c> is about to write.</summary>
/// <param name="ProjectDirectory">The project folder.</param>
/// <param name="ProjectDirectoryCreated">Whether this command created the project folder.</param>
/// <param name="AgentLocation">The agent's folder, relative to the project folder.</param>
/// <param name="DisplayName">The agent's name.</param>
/// <param name="AgentType">MCSAgent or MCSWorkflow.</param>
/// <param name="HomeEnvironmentId">The agent's home environment.</param>
/// <param name="RepositoryId">The --repository-id value, or null.</param>
/// <param name="NonInteractive">Whether the command may not ask questions.</param>
/// <param name="ClaimedBotId">The Copilot Studio agent a clone from the home environment checks out, or null.</param>
/// <param name="ClaimedWorkflowId">The workflow a clone from the home environment checks out, or null.</param>
public sealed record AgentCreationPlan(string ProjectDirectory, bool ProjectDirectoryCreated, string AgentLocation, string DisplayName, string AgentType, string HomeEnvironmentId, string? RepositoryId, bool NonInteractive, string? ClaimedBotId, string? ClaimedWorkflowId)
{
    /// <summary>The name of the Copilot Studio agent or flow a clone reads, or null for a scaffold.</summary>
    public string? SourceName { get; init; }

    /// <summary>The --agent-name value of a clone, or null when the clone takes the source's name.</summary>
    public string? RequestedName { get; init; }
}

/// <summary>The AMS agent a clone joined instead of registering a new one, or a join the operator declined.</summary>
/// <param name="Agent">The holder's restored record.</param>
/// <param name="Deployment">The holder's restored Dev deployment record.</param>
/// <param name="Ams">The AMS registration to report.</param>
/// <param name="WorkspaceConnected">Whether the join connected the holder's folder to Copilot Studio.</param>
public sealed record AgentCreationJoin(ManagedAgentRecord? Agent, AgentDeploymentRecord? Deployment, AmsAgentRegistration? Ams, bool? WorkspaceConnected)
{
    /// <summary>The join the operator declined, which leaves the folder as it was.</summary>
    public static AgentCreationJoin Declined { get; } = new(null, null, null, null);
}

/// <summary>The go-ahead <see cref="IAgentCreationHook.PrepareAsync"/> gives, which <see cref="IAgentCreationHook.CompleteAsync"/> takes back.</summary>
/// <param name="Plan">The plan that was prepared.</param>
/// <param name="State">What the hook needs to complete the creation, or null.</param>
public sealed record AgentCreationTicket(AgentCreationPlan Plan, object? State);

/// <summary>Runs work of <c>msagent agent create</c> that belongs between its checks and its writes, and after its writes.</summary>
public interface IAgentCreationHook
{
    /// <summary>Joins the AMS agent that already holds the cloned agent or flow, if one does.</summary>
    /// <param name="plan">What the command is about to write.</param>
    /// <param name="cancellationToken">Cancels the join.</param>
    Task<AgentCreationJoin?> JoinAsync(AgentCreationPlan plan, CancellationToken cancellationToken);

    /// <summary>Checks and prepares the creation before the command writes anything, or returns null when the operator declined it.</summary>
    /// <param name="plan">What the command is about to write.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    Task<AgentCreationTicket?> PrepareAsync(AgentCreationPlan plan, CancellationToken cancellationToken);

    /// <summary>Completes the creation once the command wrote the agent's records.</summary>
    /// <param name="ticket">The ticket the preparation gave.</param>
    /// <param name="stored">The agent record the command wrote.</param>
    /// <param name="homeDeployment">The home deployment the command wrote, or null.</param>
    /// <param name="clonedFolders">The folders the command cloned, or none for a scaffold.</param>
    /// <param name="cancellationToken">Cancels the completion.</param>
    /// <returns>The AMS registration to report, or null.</returns>
    Task<AmsAgentRegistration?> CompleteAsync(AgentCreationTicket ticket, ManagedAgentRecord stored, AgentDeploymentRecord? homeDeployment, IReadOnlyList<string> clonedFolders, CancellationToken cancellationToken);
}

/// <summary>The creation hook that adds nothing, used when AMS mode isn't available.</summary>
public sealed class NoAgentCreationHook : IAgentCreationHook
{
    private NoAgentCreationHook()
    {
    }

    /// <summary>The shared instance.</summary>
    public static NoAgentCreationHook Instance { get; } = new();

    /// <inheritdoc />
    public Task<AgentCreationJoin?> JoinAsync(AgentCreationPlan plan, CancellationToken cancellationToken) => Task.FromResult<AgentCreationJoin?>(null);

    /// <inheritdoc />
    public Task<AgentCreationTicket?> PrepareAsync(AgentCreationPlan plan, CancellationToken cancellationToken) => Task.FromResult<AgentCreationTicket?>(new AgentCreationTicket(plan, null));

    /// <inheritdoc />
    public Task<AmsAgentRegistration?> CompleteAsync(AgentCreationTicket ticket, ManagedAgentRecord stored, AgentDeploymentRecord? homeDeployment, IReadOnlyList<string> clonedFolders, CancellationToken cancellationToken) => Task.FromResult<AmsAgentRegistration?>(null);
}
