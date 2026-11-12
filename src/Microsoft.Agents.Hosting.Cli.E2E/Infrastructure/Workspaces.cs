using System.Text.Json;

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

    /// <summary>Whether the workspace may only be read or copied, never deployed as it is.</summary>
    public bool DeployForbidden { get; init; }

    /// <summary>The local record <c>agent init</c> registered for the workspace, once it has.</summary>
    public string? RecordId { get; init; }

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

    private static readonly string[] LocalState = [".config", ".mcs"];
    private static readonly string[] CorpusExclusions = [.. LocalState, ".git"];

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
    public Workspace Workflow(string scenarioId, string? label = null, string? parentDirectory = null)
    {
        var root = parentDirectory ?? Path.Combine(_workspaceRoot, Name(scenarioId, label));
        var destination = Path.Combine(root, WorkflowFixture);
        var displayName = $"ahe2e-{scenarioId}-{_runSuffix}";
        var workflowId = Guid.NewGuid();

        FixtureCopies.CopyWorkflow(Source(scenarioId, _config.Mcs.Workspaces.Workflow, WorkflowFixture), destination, displayName, workflowId, keepLocalState: true);

        return new Workspace
        {
            ScenarioId = scenarioId,
            Root = root,
            ProjectDirectory = destination,
            AgentName = displayName,
            SchemaName = string.Empty,
            WorkflowId = workflowId.ToString(),
        };
    }

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
        FixtureCopies.CopyTree(source.ProjectDirectory, destination, keepLocalState: false);

        return source with { ScenarioId = scenarioId, Root = root, ProjectDirectory = destination };
    }

    /// <summary>Copies a corpus agent without its cloud binding or records, as a copy source that is never deployed itself.</summary>
    public Workspace CorpusCopy(string scenarioId, string agentFolder, string? label = null)
    {
        var source = Path.Combine(_config.Mcs.Copy.CorpusRoot ?? throw new E2EException($"{scenarioId} needs mcs.copy.corpusRoot, which the capability check should have caught."), agentFolder);

        if (!Directory.Exists(source))
        {
            throw new ScenarioBlockedException($"the corpus holds no agent named {agentFolder}", "corpus agent missing");
        }

        var root = Path.Combine(_workspaceRoot, Name(scenarioId, label));
        var destination = Path.Combine(root, agentFolder);
        FileTree.CopyDirectory(source, destination, CorpusExclusions);

        if (FileTree.FilesMentioning(destination, ForbiddenMarkers(source), []) is [var first, ..])
        {
            throw new E2EException($"Safety stop: {first} names a forbidden environment even without its cloud binding, so {agentFolder} is not copied.");
        }

        return new Workspace { ScenarioId = scenarioId, Root = root, ProjectDirectory = destination, AgentName = agentFolder, SchemaName = string.Empty, DeployForbidden = true };
    }

    private IReadOnlyList<string> ForbiddenMarkers(string corpusAgent) => [.. _config.Mcs.ForbiddenEnvironmentIds, .. ForbiddenHosts(Path.Combine(corpusAgent, ".mcs", "conn.json"))];

    private IEnumerable<string> ForbiddenHosts(string connectionPath)
    {
        if (!File.Exists(connectionPath))
        {
            yield break;
        }

        using var connection = JsonDocument.Parse(File.ReadAllText(connectionPath));

        if (connection.RootElement.TryGetProperty("EnvironmentId", out var environment) && _config.Mcs.ForbiddenEnvironmentIds.Contains(environment.GetString(), StringComparer.OrdinalIgnoreCase) && connection.RootElement.TryGetProperty("DataverseEndpoint", out var endpoint) && Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var organization))
        {
            yield return organization.Host;
        }
    }

    private Workspace Copy(string scenarioId, string? label, string? configured, string fixtureName)
    {
        var root = Path.Combine(_workspaceRoot, Name(scenarioId, label));
        var destination = Path.Combine(root, fixtureName);
        FixtureCopies.CopyTree(Source(scenarioId, configured, fixtureName), destination, keepLocalState: true);

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
        if (FileTree.IsBinary(file))
        {
            return;
        }

        var original = File.ReadAllText(file);

        if (original.Contains(fixtureName, StringComparison.Ordinal))
        {
            File.WriteAllText(file, original.Replace(fixtureName, uniqueName, StringComparison.Ordinal));
        }
    }

    private static string Name(string scenarioId, string? label) => label is { Length: > 0 } ? $"{scenarioId}-{label}" : scenarioId;
}
