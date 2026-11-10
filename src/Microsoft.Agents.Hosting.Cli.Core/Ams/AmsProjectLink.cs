using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>A project folder's files and folders before AMS mode changed it, so a failure takes back exactly what the command did.</summary>
/// <param name="ProjectDirectory">The folder the snapshot covers: the project folder, or the first folder on its path that didn't exist.</param>
/// <param name="ProjectDirectoryCreated">Whether the command created that folder.</param>
/// <param name="Entries">The paths of the folder's files and folders before the change, relative to it; a .git folder counts as one entry.</param>
/// <param name="GitIgnore">The folder's .gitignore before the change, which an attach rewrites, or null when the folder had none.</param>
public sealed record AmsFolderSnapshot(string ProjectDirectory, bool ProjectDirectoryCreated, IReadOnlySet<string> Entries, byte[]? GitIgnore);

/// <summary>The link of a project folder to the repository register just created, which decides how msagent's commit reaches it.</summary>
/// <param name="Linked">Whether the folder is linked, so msagent commits to it; false when git couldn't attach a folder without history, which msagent then took back.</param>
/// <param name="ReplacesInitialCommit">Whether the folder keeps its own history, which replaces GRS's initial commit through a push leased on that commit.</param>
/// <param name="Warnings">What git couldn't do, with the commands that finish the link.</param>
public sealed record AmsNewRepositoryLink(bool Linked, bool ReplacesInitialCommit, IReadOnlyList<string> Warnings);

/// <summary>Links a project folder to its GRS repository: the question, the clone or the attach of an existing repository, the link to a new one, msagent's credential helper and the .gitignore line.</summary>
public sealed class AmsProjectLink
{
    /// <summary>The remote AMS mode adds.</summary>
    public const string Origin = "origin";

    /// <summary>The branch AMS mode links.</summary>
    public const string Branch = "main";

    /// <summary>The .gitignore line that keeps each person's .config out of the repository.</summary>
    public const string ConfigIgnoreLine = "/.config/agent.config.json";

    /// <summary>The .gitignore line that keeps every folder's .mcs, which holds that person's connection to Copilot Studio, out of the repository.</summary>
    public const string McsIgnoreLine = ".mcs/";

    private const string GitIgnoreFileName = ".gitignore";

    private const string GitDirectoryName = ".git";

    private readonly IGitPort _git;
    private readonly IYesNoPrompt _prompt;
    private readonly ISyncStatePathRule _syncState;

    /// <summary>Creates the link.</summary>
    /// <param name="git">Runs git.</param>
    /// <param name="prompt">Asks the operator before the folder is linked.</param>
    /// <param name="syncState">Recognizes the .mcs cache among the files git lists.</param>
    public AmsProjectLink(IGitPort git, IYesNoPrompt prompt, ISyncStatePathRule syncState)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _syncState = syncState ?? throw new ArgumentNullException(nameof(syncState));
    }

    /// <summary>Refuses, before anything changes, a .mcs cache that a commit msagent would push adds or changes, or that git tracks in the folders the command commits or restores.</summary>
    /// <param name="choice">The repository choice, which decides the commits msagent would push.</param>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="folders">The folders the command commits or restores, relative to the project folder.</param>
    /// <param name="joined">Whether the command has just cloned or linked the repository that brought the folders it restores.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    public async Task RequireNoSyncStateAsync(AmsRepositoryChoice choice, string projectDirectory, IReadOnlyList<string> folders, bool joined, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(folders);

        if (choice.WorkingTree is null && !joined)
        {
            return;
        }

        var revisions = PushedRevisions(choice);
        var pushed = revisions.Length == 0 ? [] : Holders(await _git.ListCommittedPathsAsync(projectDirectory, revisions, SyncStatePathspecs.Included, cancellationToken).ConfigureAwait(false), projectDirectory);
        if (pushed.Length > 0)
        {
            throw AmsAgentException.McsFolderInCommits(pushed);
        }

        var tracked = folders.Count == 0 ? [] : Holders(await _git.ListTrackedAsync(projectDirectory, folders, cancellationToken).ConfigureAwait(false), projectDirectory);
        if (tracked.Length > 0)
        {
            throw joined ? AmsAgentException.McsFolderInRepository(tracked) : AmsAgentException.McsFolderTracked(tracked);
        }
    }

    /// <summary>Asks the operator before the project folder is linked to a repository, unless the command may not ask or the folder is already linked.</summary>
    /// <param name="choice">The repository choice.</param>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="subject">The agents the command adds, named in the question, such as agent 'Helpdesk', or null for a clone that restores the repository's agents.</param>
    /// <param name="repository">The existing repository, or null for a new one.</param>
    /// <param name="environment">The home environment, named for a new repository.</param>
    /// <param name="nonInteractive">Whether the command may not ask.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>Whether the command may go on.</returns>
    public Task<bool> ConfirmAsync(AmsRepositoryChoice choice, string projectDirectory, string? subject, AmsMcsRepository? repository, string environment, bool nonInteractive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var repositoryName = repository is null ? null : $"'{repository.Name ?? repository.RepositoryId}' ({repository.RepositoryId})";
        return choice.Action == AmsRepositoryAction.None || nonInteractive
            ? Task.FromResult(true)
            : _prompt.AskAsync(choice.Action switch
            {
                AmsRepositoryAction.Clone => $"Clone repository {repositoryName} into {projectDirectory} and {(subject is null ? "restore its agents" : $"add {subject} to it")}? [Y/n]",
                AmsRepositoryAction.Attach => $"Link {projectDirectory} to repository {repositoryName} and add {subject} to it? [Y/n]",
                _ => $"Create a GRS repository in environment '{environment}' for {projectDirectory} and push {subject} to it? [Y/n]",
            }, cancellationToken);
    }

    /// <summary>Records the project folder's files and folders, and its .gitignore, before AMS mode changes it.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="projectDirectoryCreated">Whether the command created the project folder.</param>
    public static AmsFolderSnapshot Snapshot(string projectDirectory, bool projectDirectoryCreated)
    {
        var gitIgnore = Path.Combine(projectDirectory, GitIgnoreFileName);
        return new(projectDirectory, projectDirectoryCreated, Entries(projectDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase), File.Exists(gitIgnore) ? File.ReadAllBytes(gitIgnore) : null);
    }

    /// <summary>Removes the files and folders the command added since the snapshot, puts the .gitignore back, and removes the project folder when the command created it and nothing is left in it.</summary>
    /// <param name="snapshot">The folder before the change.</param>
    public static void Undo(AmsFolderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Directory.Exists(snapshot.ProjectDirectory))
        {
            return;
        }

        foreach (var entry in Entries(snapshot.ProjectDirectory).Where(entry => !snapshot.Entries.Contains(entry)).ToArray())
        {
            AmsFileTree.Delete(Path.Combine(snapshot.ProjectDirectory, entry));
        }

        var gitIgnore = Path.Combine(snapshot.ProjectDirectory, GitIgnoreFileName);
        if (snapshot.GitIgnore is { } content && !(File.Exists(gitIgnore) && File.ReadAllBytes(gitIgnore).AsSpan().SequenceEqual(content)))
        {
            File.WriteAllBytes(gitIgnore, content);
        }

        if (snapshot.ProjectDirectoryCreated && !Directory.EnumerateFileSystemEntries(snapshot.ProjectDirectory).Any())
        {
            Directory.Delete(snapshot.ProjectDirectory);
        }
    }

    /// <summary>Makes the project folder a working tree of an existing repository: a clone into the empty or missing folder, which it creates first, or an attach that checks out the repository's main branch over the project's files.</summary>
    /// <param name="choice">The repository choice, whose action is Clone, Attach or None.</param>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="repository">The existing repository.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The warnings the link left, such as a credential helper git couldn't save.</returns>
    public async Task<IReadOnlyList<string>> JoinAsync(AmsRepositoryChoice choice, string projectDirectory, AmsMcsRepository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(repository);

        switch (choice.Action)
        {
            case AmsRepositoryAction.Clone:
                Directory.CreateDirectory(projectDirectory);
                await _git.CloneAsync(repository.GitUrl, projectDirectory, cancellationToken).ConfigureAwait(false);
                return await SaveHelperAsync(projectDirectory, repository.GitUrl, cancellationToken).ConfigureAwait(false);
            case AmsRepositoryAction.Attach:
                return await AttachAsync(choice.WorkingTree, projectDirectory, repository.GitUrl, repository.Name ?? repository.RepositoryId, cancellationToken).ConfigureAwait(false);
            default:
                return [];
        }
    }

    /// <summary>Links the project folder to the repository register created. GRS starts every repository with an initial commit, so a folder without history is attached in place like an existing repository, and msagent's commit lands on that commit; a working tree with commits only gets the repository as origin, and its history replaces that commit. A failed link is a warning, since the agent and its records exist: it says what git reported and the commands that finish the link, and an attach that fails is taken back first.</summary>
    /// <param name="choice">The repository choice, whose action is InitAndLink or AddOrigin.</param>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="repositoryId">The new repository, named when its main would overwrite local files.</param>
    /// <param name="gitUrl">The new repository's url.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The link.</returns>
    public async Task<AmsNewRepositoryLink> LinkNewAsync(AmsRepositoryChoice choice, string projectDirectory, string repositoryId, string gitUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);

        if (choice.WorkingTree is { HasCommits: true })
        {
            var warnings = await SaveHelperAsync(projectDirectory, gitUrl, cancellationToken).ConfigureAwait(false);
            try
            {
                await _git.AddRemoteAsync(projectDirectory, Origin, gitUrl, cancellationToken).ConfigureAwait(false);
                return new(true, true, warnings);
            }
            catch (AmsAgentException failure)
            {
                return new(true, true, [.. warnings, Unlinked(projectDirectory, failure, $"`git remote add {Origin} {gitUrl}`")]);
            }
        }

        try
        {
            return new(true, false, await AttachAsync(choice.WorkingTree, projectDirectory, gitUrl, repositoryId, cancellationToken).ConfigureAwait(false));
        }
        catch (AmsAgentException failure)
        {
            return new(false, false, [Unlinked(projectDirectory, failure, choice.WorkingTree is null ? $"`git init -b {Branch}`, `git remote add {Origin} {gitUrl}`, `msagent auth setup-git`, `git fetch {Origin} {Branch}` and `git checkout -b {Branch} --track {Origin}/{Branch}`" : $"`git remote add {Origin} {gitUrl}`, `git fetch {Origin} {Branch}` and `git checkout -b {Branch} --track {Origin}/{Branch}`")]);
        }
    }

    /// <summary>Adds the lines that keep .config and .mcs out of the repository to the project's .gitignore, each one it lacks, keeping the file's line ending.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    public static void EnsureGitIgnore(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, GitIgnoreFileName);
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var missing = new[] { ConfigIgnoreLine, McsIgnoreLine }.Except(text.Split('\n').Select(line => line.Trim()), StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(path, $"{text}{(text.Length == 0 || text.EndsWith('\n') ? string.Empty : newLine)}{string.Concat(missing.Select(line => line + newLine))}");
    }

    internal static string Kind(bool isWorkflow) => isWorkflow ? "workflow" : "agent";

    internal static string Subject(string agentType, string displayName) => $"{Kind(agentType == AgentTypes.McsWorkflow)} '{displayName}'";

    private static string[] PushedRevisions(AmsRepositoryChoice choice) => choice.WorkingTree is not { HasCommits: true } ? [] : choice.Action switch
    {
        AmsRepositoryAction.AddOrigin => ["HEAD"],
        AmsRepositoryAction.None => ["HEAD", "--not", $"--remotes={Origin}"],
        _ => [],
    };

    private string[] Holders(IEnumerable<string> paths, string projectDirectory) => [.. paths.Where(_syncState.IsSyncStatePath).Select(path => path.Split('/')[0]).Select(folder => _syncState.IsSyncStatePath(folder) ? projectDirectory : folder).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    private async Task<IReadOnlyList<string>> AttachAsync(GitWorkingTree? workingTree, string projectDirectory, string gitUrl, string repositoryName, CancellationToken cancellationToken)
    {
        var madeGit = workingTree is null;
        if (madeGit)
        {
            await _git.InitAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
        }

        var addedOrigin = false;
        try
        {
            await _git.AddRemoteAsync(projectDirectory, Origin, gitUrl, cancellationToken).ConfigureAwait(false);
            addedOrigin = true;
            var warnings = await SaveHelperAsync(projectDirectory, gitUrl, cancellationToken).ConfigureAwait(false);
            if (!await _git.FetchBranchAsync(projectDirectory, Origin, Branch, cancellationToken).ConfigureAwait(false))
            {
                return warnings;
            }

            if (workingTree is { HasCommits: true })
            {
                throw AmsAgentException.RepositoryWouldOverwrite(repositoryName, $"this folder's history: it and the repository's {Branch} are unrelated");
            }

            var clashes = (await _git.ListTreeAsync(projectDirectory, $"{Origin}/{Branch}", cancellationToken).ConfigureAwait(false)).Where(path => !string.Equals(path, GitIgnoreFileName, StringComparison.OrdinalIgnoreCase) && Path.Exists(Path.Combine(projectDirectory, path))).ToArray();
            if (clashes.Length > 0)
            {
                throw AmsAgentException.RepositoryWouldOverwrite(repositoryName, $"{string.Join(", ", clashes.Take(5))}{(clashes.Length > 5 ? $" and {clashes.Length - 5} more" : string.Empty)}");
            }

            var gitIgnore = Path.Combine(projectDirectory, GitIgnoreFileName);
            var localGitIgnore = File.Exists(gitIgnore) ? File.ReadAllBytes(gitIgnore) : null;
            var localLines = File.Exists(gitIgnore) ? File.ReadAllLines(gitIgnore) : [];
            File.Delete(gitIgnore);
            try
            {
                await _git.CheckoutTrackingBranchAsync(projectDirectory, Branch, Origin, cancellationToken).ConfigureAwait(false);
            }
            catch when (localGitIgnore is not null)
            {
                File.WriteAllBytes(gitIgnore, localGitIgnore);
                throw;
            }

            MergeGitIgnore(gitIgnore, localLines);
            return warnings;
        }
        catch
        {
            await TakeBackAsync(projectDirectory, madeGit, addedOrigin).ConfigureAwait(false);
            throw;
        }
    }

    private async Task TakeBackAsync(string projectDirectory, bool madeGit, bool addedOrigin)
    {
        if (madeGit)
        {
            AmsFileTree.Delete(Path.Combine(projectDirectory, GitDirectoryName));
            return;
        }

        if (addedOrigin)
        {
            try
            {
                await _git.RemoveRemoteAsync(projectDirectory, Origin, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AmsAgentException)
            {
            }
        }
    }

    private static string Unlinked(string projectDirectory, AmsAgentException failure, string commands) =>
        $"git couldn't link {projectDirectory} to the new repository: {failure.Message} {(failure.ErrorCode == ErrorCodes.AmsAgent.RepositoryWouldOverwrite ? "Move those files out of the folder, then run" : "Run")} {commands} there.";

    private static void MergeGitIgnore(string path, IReadOnlyList<string> localLines)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var present = text.Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var missing = localLines.Where(line => line.Length > 0 && !present.Contains(line)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(path, $"{text}{(text.Length == 0 || text.EndsWith('\n') ? string.Empty : newLine)}{string.Join(newLine, missing)}{newLine}");
    }

    private async Task<IReadOnlyList<string>> SaveHelperAsync(string projectDirectory, string gitUrl, CancellationToken cancellationToken)
    {
        try
        {
            await _git.SetRepositoryHelperAsync(projectDirectory, gitUrl, cancellationToken).ConfigureAwait(false);
            return [];
        }
        catch (AmsAgentException failure)
        {
            return [$"git couldn't save msagent's credential helper in {projectDirectory}: {failure.Message} Run `msagent auth setup-git` so git signs in to GRS."];
        }
    }

    private static IEnumerable<string> Entries(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            yield break;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(projectDirectory))
        {
            yield return Path.GetFileName(entry);
            if (Directory.Exists(entry) && !string.Equals(Path.GetFileName(entry), GitDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var nested in Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.AllDirectories))
                {
                    yield return Path.GetRelativePath(projectDirectory, nested);
                }
            }
        }
    }
}
