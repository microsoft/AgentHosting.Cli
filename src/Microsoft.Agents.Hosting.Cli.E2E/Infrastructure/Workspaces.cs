using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>An isolated copy of a fixture, owned by one scenario.</summary>
public sealed record Workspace
{
    public required string ScenarioId { get; init; }

    public required string Root { get; init; }

    /// <summary>The directory holding the agent's source, which is also where <c>ah</c> is invoked.</summary>
    public required string ProjectDirectory { get; init; }

    public required string AgentName { get; init; }

    /// <summary>The schema name the deploy will target, checked for collisions before any create.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The Dataverse workflow the copy addresses, for a standalone workflow workspace.</summary>
    public string? WorkflowId { get; init; }

    /// <summary>The file holding the local records, once <c>agent init</c> has written it.</summary>
    public string ConfigPath => Path.Combine(ProjectDirectory, ".config", "agent.config.json");

    /// <summary>Resolves a path inside the workspace.</summary>
    public string File(params string[] segments) => Path.Combine([ProjectDirectory, .. segments]);
}

/// <summary>Hands each scenario its own copy of a fixture, so no scenario depends on another.</summary>
public sealed class Workspaces
{
    private const string CliFixture = "MCSCliAgent";
    private const string ClassicFixture = "MCSClassicAgent";
    private const string WorkflowFixture = "MCSWorkflow";

    private readonly string _fixturesDirectory;
    private readonly string _workspaceRoot;
    private readonly E2EConfig _config;
    private readonly string _runSuffix;

    public Workspaces(string fixturesDirectory, string workspaceRoot, E2EConfig config, string runSuffix)
    {
        _fixturesDirectory = fixturesDirectory;
        _workspaceRoot = workspaceRoot;
        _config = config;
        _runSuffix = runSuffix;
    }

    /// <summary>Copies the CLI-shaped fixture for one scenario.</summary>
    public Workspace CliAgent(string scenarioId, string? label = null) =>
        Copy(scenarioId, label, _config.Mcs.Workspaces.CliAgent, CliFixture);

    /// <summary>Copies the classic-shaped fixture, the one carrying connection references and knowledge.</summary>
    public Workspace ClassicAgent(string scenarioId, string? label = null) =>
        Copy(scenarioId, label, _config.Mcs.Workspaces.ClassicAgent, ClassicFixture);

    /// <summary>Copies the standalone workflow fixture, giving the copy a workflow identity of its own.</summary>
    public Workspace Workflow(string scenarioId, string? label = null)
    {
        var destination = Path.Combine(_workspaceRoot, Name(scenarioId, label), WorkflowFixture);
        CopyDirectory(Source(scenarioId, _config.Mcs.Workspaces.Workflow, WorkflowFixture), destination);

        var displayName = $"ahe2e-{scenarioId}-{_runSuffix}";
        var workflowId = Guid.NewGuid();
        Reidentify(WorkflowWorkspace.WorkflowsDirectory(destination), displayName, workflowId);

        return new Workspace
        {
            ScenarioId = scenarioId,
            Root = Path.Combine(_workspaceRoot, Name(scenarioId, label)),
            ProjectDirectory = destination,
            AgentName = displayName,
            SchemaName = string.Empty,
            WorkflowId = workflowId.ToString(),
        };
    }

    /// <summary>
    /// Gives the copied workflow a new identifier and name, so two scenarios never address one cloud
    /// flow. A workflow is adopted by its identifier, so sharing one would make the second scenario
    /// overwrite the first's flow — and cleanup would then delete it twice.
    /// </summary>
    private static void Reidentify(string workflowsDirectory, string displayName, Guid workflowId)
    {
        var folderName = WorkflowWorkspace.FolderNameFor(displayName, workflowId);
        var renamed = Path.Combine(workflowsDirectory, folderName);

        Directory.Move(Directory.EnumerateDirectories(workflowsDirectory).Single(), renamed);

        var metadataPath = Path.Combine(renamed, WorkflowWorkspace.MetadataFileName);

        File.WriteAllLines(metadataPath, File.ReadAllLines(metadataPath).Select(line => Reidentified(line, folderName, displayName, workflowId)));
    }

    private static string Reidentified(string line, string folderName, string displayName, Guid workflowId) =>
        line.StartsWith("workflowId:", StringComparison.Ordinal) ? $"workflowId: {workflowId}"
        : line.StartsWith("name:", StringComparison.Ordinal) ? $"name: {displayName}"
        : line.StartsWith("jsonFileName:", StringComparison.Ordinal)
            ? $"jsonFileName: {WorkflowWorkspace.WorkflowsFolderName}/{folderName}/{WorkflowWorkspace.DefinitionFileName}"
            : line;

    /// <summary>Creates an empty directory, for scenarios asserting behaviour where no workspace exists.</summary>
    public string EmptyDirectory(string scenarioId, string? label = null)
    {
        var directory = Path.Combine(_workspaceRoot, Name(scenarioId, label));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Copies an existing workspace, keeping its schema name so both copies address the same cloud
    /// agent. That is what the conflict and stale-pull scenarios need: two local views of one agent.
    /// </summary>
    public Workspace CopyOf(Workspace source, string scenarioId)
    {
        var root = Path.Combine(_workspaceRoot, $"{scenarioId}-copy");
        var destination = Path.Combine(root, Path.GetFileName(source.ProjectDirectory));
        CopyDirectory(source.ProjectDirectory, destination);
        RemoveLocalState(destination);

        return source with { ScenarioId = scenarioId, Root = root, ProjectDirectory = destination };
    }

    /// <summary>
    /// Drops the copied workspace's own records and sync cache, so it registers and deploys as a
    /// separate local project rather than inheriting the source's identity.
    /// </summary>
    private static void RemoveLocalState(string directory)
    {
        foreach (var state in new[] { ".config", ".mcs" })
        {
            var path = Path.Combine(directory, state);

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private Workspace Copy(string scenarioId, string? label, string? configured, string fixtureName)
    {
        var root = Path.Combine(_workspaceRoot, Name(scenarioId, label));
        var destination = Path.Combine(root, fixtureName);
        CopyDirectory(Source(scenarioId, configured, fixtureName), destination);

        var uniqueName = UniqueAgentName(fixtureName, scenarioId);
        Rebrand(destination, fixtureName, uniqueName);

        return new Workspace
        {
            ScenarioId = scenarioId,
            Root = root,
            ProjectDirectory = destination,
            AgentName = uniqueName,
            SchemaName = $"ahe2e_{uniqueName}",
        };
    }

    private string Source(string scenarioId, string? configured, string fixtureName)
    {
        var source = string.IsNullOrWhiteSpace(configured) ? Path.Combine(_fixturesDirectory, fixtureName) : configured;

        return Directory.Exists(source)
            ? source
            : throw new E2EException($"{scenarioId} needs the {fixtureName} workspace, which is not at {source}.");
    }

    /// <summary>
    /// Builds a name the same length as the fixture's, so rewriting it cannot lengthen a connection
    /// reference logical name past a service limit. The run suffix is kept even when the name has to
    /// be truncated, because losing it would let two runs collide on one schema name.
    /// </summary>
    private string UniqueAgentName(string fixtureName, string scenarioId)
    {
        var key = scenarioId.Replace("-", string.Empty, StringComparison.Ordinal);
        var discriminator = key + _runSuffix;

        return discriminator.Length >= fixtureName.Length
            ? discriminator[^fixtureName.Length..]
            : fixtureName[..(fixtureName.Length - discriminator.Length)] + discriminator;
    }

    /// <summary>
    /// Rewrites the fixture's schema name everywhere it appears so two scenarios never target the
    /// same cloud agent. A deploy adopts an agent by schema name, so sharing one would make the
    /// second scenario adopt the first's agent — and cleanup would then delete it twice.
    /// </summary>
    private static void Rebrand(string directory, string fixtureName, string uniqueName)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            RebrandContent(file, fixtureName, uniqueName);
        }

        foreach (var file in Directory.GetFiles(directory, $"*{fixtureName}*", SearchOption.AllDirectories))
        {
            var renamed = Path.Combine(
                Path.GetDirectoryName(file)!,
                Path.GetFileName(file).Replace(fixtureName, uniqueName, StringComparison.Ordinal));

            File.Move(file, renamed);
        }
    }

    private static void RebrandContent(string file, string fixtureName, string uniqueName)
    {
        if (IsBinary(file))
        {
            return;
        }

        var original = File.ReadAllText(file);

        if (original.Contains(fixtureName, StringComparison.Ordinal))
        {
            File.WriteAllText(file, original.Replace(fixtureName, uniqueName, StringComparison.Ordinal));
        }
    }

    private static bool IsBinary(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".zip" or ".dll" or ".exe";

    private static string Name(string scenarioId, string? label) => label is { Length: > 0 } ? $"{scenarioId}-{label}" : scenarioId;

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
        }
    }
}
