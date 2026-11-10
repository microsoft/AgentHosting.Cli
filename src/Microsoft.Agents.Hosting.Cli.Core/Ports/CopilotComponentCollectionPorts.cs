namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The component-collection workspaces an agent workspace declares.</summary>
/// <param name="Found">The collection workspace directories that exist inside the project, in declaration order.</param>
/// <param name="Missing">The declared directories that resolve to no collection inside the project.</param>
public sealed record CopilotComponentCollections(IReadOnlyList<string> Found, IReadOnlyList<string> Missing);

/// <summary>One declared component collection, and what pushing it produced when it was pushed.</summary>
/// <typeparam name="TPushed">What one workspace push produces.</typeparam>
/// <param name="Directory">The collection workspace directory.</param>
/// <param name="Pushed">Whether the collection was pushed rather than held back.</param>
/// <param name="Result">What the push produced, when the collection was pushed.</param>
public sealed record CopilotComponentCollectionPushed<TPushed>(string Directory, bool Pushed, TPushed? Result);

/// <summary>What pushing an agent after the component collections it declares produced.</summary>
/// <typeparam name="TPushed">What one workspace push produces.</typeparam>
public sealed record CopilotComponentCollectionPush<TPushed>
{
    /// <summary>Every collection the agent declares that exists inside the project, in declaration order.</summary>
    public IReadOnlyList<CopilotComponentCollectionPushed<TPushed>> Collections { get; init; } = [];

    /// <summary>The declared directories that resolve to no collection inside the project.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>What the agent's push produced.</summary>
    public required TPushed Agent { get; init; }
}

/// <summary>Resolves the component collections an agent workspace declares, and pushes them before the agent.</summary>
public interface ICopilotComponentCollectionPort
{
    /// <summary>Resolves the collections an agent workspace declares, keeping the ones whose folder lies inside the project.</summary>
    /// <param name="projectDirectory">The project directory every collection has to stay beneath.</param>
    /// <param name="agentWorkspaceDirectory">The directory holding the agent's workspace.</param>
    /// <returns>The collection workspaces that exist, and the declarations that resolve to nothing.</returns>
    CopilotComponentCollections Resolve(string projectDirectory, string agentWorkspaceDirectory);

    /// <summary>Pushes each declared collection the caller lets through, in declaration order, then the agent, stopping at the first push that fails.</summary>
    /// <typeparam name="TPushed">What one workspace push produces.</typeparam>
    /// <param name="projectDirectory">The project directory every collection has to stay beneath.</param>
    /// <param name="agentWorkspaceDirectory">The directory holding the agent's workspace.</param>
    /// <param name="pushWorkspaceAsync">Pushes one workspace directory.</param>
    /// <param name="shouldPushCollectionAsync">Decides whether a collection is pushed.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>What each push produced, and the declarations that resolve to nothing.</returns>
    Task<CopilotComponentCollectionPush<TPushed>> PushAsync<TPushed>(string projectDirectory, string agentWorkspaceDirectory, Func<string, CancellationToken, Task<TPushed>> pushWorkspaceAsync, Func<string, CancellationToken, Task<bool>> shouldPushCollectionAsync, CancellationToken cancellationToken);
}
