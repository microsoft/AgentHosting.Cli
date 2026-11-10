using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Deletes AMS's records of an agent with the agent's ETag as If-Match, reading the agent for it when the caller doesn't hold it.</summary>
public sealed class AmsAgentDeletion
{
    private const int NotFound = 404;

    private const int PreconditionFailed = 412;

    private readonly IAmsMcsAgentReadTransport _agents;
    private readonly IAmsMcsAgentDeleteTransport _deletes;
    private readonly AmsTargets _targets;
    private readonly AmsCloudCacheShelf _shelf;
    private readonly AgentConfigLocator _locator;
    private readonly ICopilotComponentCollectionPort _collections;

    /// <summary>Creates the deletion.</summary>
    /// <param name="agents">Reads the agent's ETag.</param>
    /// <param name="deletes">Deletes the agent's records.</param>
    /// <param name="targets">Resolves the endpoints of the agent's home environment.</param>
    /// <param name="shelf">Forgets the copies of the cloud caches of the agent's folders.</param>
    /// <param name="locator">Resolves the agent's folder.</param>
    /// <param name="collections">Resolves the component collections the agent declares.</param>
    public AmsAgentDeletion(IAmsMcsAgentReadTransport agents, IAmsMcsAgentDeleteTransport deletes, AmsTargets targets, AmsCloudCacheShelf shelf, AgentConfigLocator locator, ICopilotComponentCollectionPort collections)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deletes = deletes ?? throw new ArgumentNullException(nameof(deletes));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
    }

    /// <summary>Deletes the agent's AMS records; an agent AMS doesn't hold counts as deleted.</summary>
    /// <param name="home">The agent's home environment.</param>
    /// <param name="agent">The agent's .config record.</param>
    /// <param name="eTag">The agent's ETag, or null to read it.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task DeleteAsync(PowerPlatformEnvironment home, ManagedAgentRecord agent, string? eTag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var target = _targets.DataPlane(home);
        try
        {
            await _deletes.DeleteAgentAsync(target, agent.AgentId, eTag ?? await ReadETagAsync(target, agent.AgentId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (TargetRequestException failure) when (failure.Status == NotFound)
        {
        }
        catch (TargetRequestException failure) when (failure.Status == PreconditionFailed)
        {
            throw AmsAgentException.AgentRecordChanged(agent.DisplayName, agent.AgentId);
        }
    }

    /// <summary>Deletes the agent's AMS records, then forgets the copies of the cloud caches of the agent's folders, which never fails the delete.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="home">The agent's home environment.</param>
    /// <param name="agent">The agent's .config record.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task DeleteAndForgetAsync(string projectDirectory, PowerPlatformEnvironment home, ManagedAgentRecord agent, CancellationToken cancellationToken)
    {
        await DeleteAsync(home, agent, null, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var folder in FoldersOf(projectDirectory, agent))
            {
                await _shelf.ForgetAsync(folder, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
        }
    }

    private IReadOnlyList<string> FoldersOf(string projectDirectory, ManagedAgentRecord agent) =>
        _locator.ContainedLocation(projectDirectory, agent.AgentLocation) is not { } workspace ? []
        : agent.AgentType == AgentTypes.McsWorkflow ? [workspace]
        : [workspace, .. _collections.Resolve(projectDirectory, workspace).Found];

    private async Task<string> ReadETagAsync(AmsServiceTarget target, string agentId, CancellationToken cancellationToken) =>
        (await _agents.GetAgentAsync(target, agentId, cancellationToken).ConfigureAwait(false)).ETag ?? throw new FormatException("AMS's agent record carries no ETag.");
}
