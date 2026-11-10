using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>What AMS mode does with the project folder's git working tree before an agent joins the repository.</summary>
public enum AmsRepositoryAction
{
    /// <summary>The folder is already a working tree of the repository.</summary>
    None,

    /// <summary>Clone the existing repository into the empty folder.</summary>
    Clone,

    /// <summary>Link the folder to the existing repository and check out its main branch.</summary>
    Attach,

    /// <summary>Make the folder a working tree, then link it to the repository register creates.</summary>
    InitAndLink,

    /// <summary>Link the working tree to the repository register creates.</summary>
    AddOrigin,
}

/// <summary>The repository an agent joins and what the project folder needs for it.</summary>
/// <param name="RepositoryId">The existing repository, or null for a repository register creates.</param>
/// <param name="Action">What the project folder needs.</param>
/// <param name="WorkingTree">The working tree the folder is, or null.</param>
public sealed record AmsRepositoryChoice(string? RepositoryId, AmsRepositoryAction Action, GitWorkingTree? WorkingTree);

/// <summary>Decides which GRS repository a project folder's agents join, from the folder's working tree, its entries and --repository-id.</summary>
public sealed class AmsRepositoryResolver
{
    private const string Origin = "origin";

    private readonly GitHostTrust _trust;
    private readonly AgentConfigLocator _locator;
    private readonly ICopilotComponentCollectionPort _collections;

    /// <summary>Creates the resolver.</summary>
    /// <param name="trust">Decides which remote hosts are GRS hosts.</param>
    /// <param name="locator">Finds the agent workspaces the project folder holds.</param>
    /// <param name="collections">Finds the collection folders the agent workspaces declare.</param>
    public AmsRepositoryResolver(GitHostTrust trust, AgentConfigLocator locator, ICopilotComponentCollectionPort collections)
    {
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
    }

    /// <summary>Chooses the repository, refusing a folder AMS mode can't use.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="workingTree">The working tree the folder is in, or null.</param>
    /// <param name="repositoryId">The --repository-id value, or null.</param>
    /// <param name="agents">The agents .config holds, whose registered repository a rerun of an interrupted link takes.</param>
    /// <param name="projectEntries">The folders and files that are part of the project, beyond the agent workspaces.</param>
    public AmsRepositoryChoice Choose(string projectDirectory, GitWorkingTree? workingTree, string? repositoryId, IReadOnlyList<ManagedAgentRecord> agents, IReadOnlyCollection<string> projectEntries)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(projectEntries);

        var requested = repositoryId is null ? null : GrsRemote.RequireRepositoryId(repositoryId);
        if (RegisteredRepository(agents) is { } registered)
        {
            requested = requested is null || string.Equals(requested, registered, StringComparison.OrdinalIgnoreCase) ? registered : throw AmsAgentException.RepositoryMismatch(requested, registered, registered: true);
        }

        return workingTree is null ? ChooseForFolder(projectDirectory, requested, agents, projectEntries) : ChooseForWorkingTree(projectDirectory, workingTree, requested);
    }

    internal static string? RegisteredRepository(IReadOnlyList<ManagedAgentRecord> agents) => agents.Select(agent => agent.SourceControlMetadata?.RepositoryId).FirstOrDefault(registered => registered is not null);

    internal string? RepositoryOf(string projectDirectory, GitWorkingTree workingTree)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingTree.Root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)), StringComparison.OrdinalIgnoreCase))
        {
            throw AmsAgentException.ProjectNotRepositoryRoot(projectDirectory, workingTree.Root);
        }

        var remote = workingTree.Remotes.FirstOrDefault(candidate => candidate.Name == Origin) ?? (workingTree.Remotes.Count switch
        {
            0 => null,
            1 => workingTree.Remotes[0],
            _ => throw AmsAgentException.NotAGrsRepository(string.Join(", ", workingTree.Remotes.Select(candidate => $"{candidate.Name} ({candidate.Url})"))),
        });

        return remote is null ? null : GrsRemote.RepositoryId(remote.Url, _trust) ?? throw AmsAgentException.NotAGrsRepository($"{remote.Name} ({remote.Url})");
    }

    private AmsRepositoryChoice ChooseForWorkingTree(string projectDirectory, GitWorkingTree workingTree, string? requested)
    {
        if (RepositoryOf(projectDirectory, workingTree) is not { } repositoryId)
        {
            return requested is null ? new(null, AmsRepositoryAction.AddOrigin, workingTree) : new(requested, AmsRepositoryAction.Attach, workingTree);
        }

        return requested is null || string.Equals(requested, repositoryId, StringComparison.OrdinalIgnoreCase) ? new(repositoryId, AmsRepositoryAction.None, workingTree) : throw AmsAgentException.RepositoryMismatch(requested, repositoryId);
    }

    private AmsRepositoryChoice ChooseForFolder(string projectDirectory, string? requested, IReadOnlyList<ManagedAgentRecord> agents, IReadOnlyCollection<string> projectEntries)
    {
        var entries = Directory.Exists(projectDirectory) ? Directory.EnumerateFileSystemEntries(projectDirectory).Select(Path.GetFileName).Select(name => name!).ToArray() : [];
        if (entries.Length == 0)
        {
            return requested is null ? new(null, AmsRepositoryAction.InitAndLink, null) : new(requested, AmsRepositoryAction.Clone, null);
        }

        var project = ProjectEntries(projectDirectory, agents, projectEntries);
        var foreign = entries.Where(entry => !project.Contains(entry) && !entry.StartsWith(".git", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (foreign.Length > 0)
        {
            throw AmsAgentException.ProjectFolderNotDedicated(projectDirectory, foreign);
        }

        return requested is null ? new(null, AmsRepositoryAction.InitAndLink, null) : new(requested, AmsRepositoryAction.Attach, null);
    }

    private HashSet<string> ProjectEntries(string projectDirectory, IReadOnlyList<ManagedAgentRecord> agents, IReadOnlyCollection<string> projectEntries)
    {
        var workspaces = _locator.WorkspaceLocations(projectDirectory).Where(location => location != AgentConfigLocator.CurrentDirectoryLocation).ToArray();
        var collections = workspaces.SelectMany(location => _collections.Resolve(projectDirectory, Path.Combine(projectDirectory, location)).Found).Select(folder => Path.GetRelativePath(projectDirectory, folder).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
        return new HashSet<string>([AgentConfigLocator.ConfigDirectoryName, .. workspaces, .. collections, .. agents.Select(agent => agent.AgentLocation), .. projectEntries], StringComparer.OrdinalIgnoreCase);
    }
}
