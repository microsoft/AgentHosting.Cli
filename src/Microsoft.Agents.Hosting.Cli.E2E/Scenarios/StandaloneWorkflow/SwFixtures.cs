using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public enum SwShape
{
    Complex,

    CliNative,

    ClassicAgentFlow,

    Dataverse,

    NoConnection,

    ButtonNoop,

    Scaffold,

    Clone,
}

public sealed class SwWorkspace
{
    public required SwShape Shape { get; init; }

    public required string Root { get; init; }

    public required string ProjectDirectory { get; init; }

    public required string WorkflowRoot { get; init; }

    public required string WorkflowId { get; set; }

    public required string DisplayName { get; set; }

    public string? AgentId { get; set; }

    public string? EnvironmentId { get; set; }

    public string? HomeDeploymentName { get; set; }

    public string ConfigPath => Path.Combine(ProjectDirectory, ".config", "agent.config.json");

    public string WorkflowsDirectory => Path.Combine(WorkflowRoot, WorkflowWorkspace.WorkflowsFolderName);

    public string DeclarationsDirectory => Path.Combine(WorkflowRoot, "infrastructure", "connections");

    public IReadOnlyList<string> Folders => Directory.Exists(WorkflowsDirectory) ? [.. Directory.GetDirectories(WorkflowsDirectory).Order(StringComparer.Ordinal)] : [];

    public string FolderPath => Folders is [var only]
        ? only
        : throw new E2EException($"{WorkflowsDirectory} holds {Folders.Count} workflow folders, not one.");

    public string DefinitionPath => Path.Combine(FolderPath, WorkflowWorkspace.DefinitionFileName);

    public string MetadataPath => Path.Combine(FolderPath, WorkflowWorkspace.MetadataFileName);

    public string Agent => AgentId ?? throw new E2EException($"{ProjectDirectory} has no record yet.");
}

public static class SwFixtures
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FolderOf(SwShape shape) => shape switch
    {
        SwShape.Complex => "SWComplex",
        SwShape.CliNative => "SWCliNative",
        SwShape.ClassicAgentFlow => "SWClassicAgentFlow",
        SwShape.Dataverse => "SWDataverse",
        SwShape.ButtonNoop => "SWButtonNoop",
        SwShape.NoConnection => "MCSWorkflow",
        _ => throw new E2EException($"{shape} is written by the CLI, not materialized from a fixture."),
    };

    public static string NameFor(SwRun run, string? label = null) =>
        label is { Length: > 0 }
            ? $"{StandaloneServices.FlowNamePrefix}-{run.Key}-{run.Context.RunSuffix}-{label}"
            : $"{StandaloneServices.FlowNamePrefix}-{run.Key}-{run.Context.RunSuffix}";

    public static SwWorkspace Materialize(SwRun run, SwShape shape, string? label = null, bool? activated = null, string? name = null)
    {
        var index = run.NextIndex();
        var tag = label ?? $"w{index}";
        var root = run.Context.Workspaces.EmptyDirectory(run.Id, tag);
        var project = Path.Combine(root, FolderOf(shape));
        var displayName = name ?? NameFor(run, index == 1 && label is null ? null : tag);
        var workflowId = Guid.NewGuid();

        FixtureCopies.CopyWorkflow(Source(run, shape), project, displayName, workflowId, keepLocalState: false);
        IsolateReferences(run, project, index);

        var workspace = new SwWorkspace
        {
            Shape = shape,
            Root = root,
            ProjectDirectory = project,
            WorkflowRoot = project,
            WorkflowId = workflowId.ToString(),
            DisplayName = displayName,
        };

        if (activated is { } state)
        {
            SetState(workspace, state);
        }

        run.Note($"materialized {shape} as '{displayName}' ({workflowId}) in {project}");
        return workspace;
    }

    public static string MaterializeAgentProject(SwRun run, string label)
    {
        var root = run.Context.Workspaces.EmptyDirectory(run.Id, label);
        var project = Path.Combine(root, "MCSClassicAgent");
        FixtureCopies.CopyTree(Path.Combine(Path.GetDirectoryName(run.Services.FixturesDirectory)!, "MCSClassicAgent"), project, keepLocalState: false);
        return project;
    }

    public static bool? ActivatedLocally(SwWorkspace workspace) =>
        MetadataValue(workspace.MetadataPath, "stateCode") switch
        {
            "1" => true,
            "0" => false,
            _ => null,
        };

    public static void SetState(SwWorkspace workspace, bool activated) =>
        RewriteMetadata(workspace.MetadataPath, line =>
            line.StartsWith("stateCode:", StringComparison.Ordinal) ? $"stateCode: {(activated ? 1 : 0)}"
            : line.StartsWith("statusCode:", StringComparison.Ordinal) ? $"statusCode: {(activated ? 2 : 1)}"
            : line);

    public static void SetDescription(SwWorkspace workspace, string description) =>
        RewriteMetadata(workspace.MetadataPath, line => line.StartsWith("description:", StringComparison.Ordinal) ? $"description: {FixtureCopies.Yaml(description)}" : line);

    public static void SetMetadataName(SwWorkspace workspace, string name) =>
        RewriteMetadata(workspace.MetadataPath, line => line.StartsWith("name:", StringComparison.Ordinal) ? $"name: {FixtureCopies.Yaml(name)}" : line);

    public static void SetMetadataWorkflowId(SwWorkspace workspace, string workflowId) =>
        RewriteMetadata(workspace.MetadataPath, line => line.StartsWith("workflowId:", StringComparison.Ordinal) ? $"workflowId: {workflowId}" : line);

    public static string? MetadataValue(string metadataPath, string key) =>
        File.Exists(metadataPath)
            ? File.ReadLines(metadataPath)
                .Where(line => line.StartsWith(key + ":", StringComparison.Ordinal))
                .Select(line => Unquote(line[(key.Length + 1)..].Trim()))
                .FirstOrDefault()
            : null;

    public static IReadOnlyDictionary<string, string> References(SwWorkspace workspace) => ReadDeclarations(workspace.DeclarationsDirectory);

    public static void ReplaceConnector(SwWorkspace workspace, string connectorName, string replacement)
    {
        foreach (var file in Directory.GetFiles(workspace.WorkflowRoot, "*", SearchOption.AllDirectories).Where(IsText))
        {
            var original = File.ReadAllText(file);
            var updated = original.Replace(connectorName, replacement, StringComparison.Ordinal);

            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(file, updated);
            }
        }
    }

    public static void Inflate(SwWorkspace workspace, int characters)
    {
        var document = JsonNode.Parse(File.ReadAllText(workspace.DefinitionPath))
            ?? throw new E2EException($"{workspace.DefinitionPath} is empty.");

        var actions = document["properties"]?["definition"]?["actions"]?.AsObject()
            ?? throw new E2EException($"{workspace.DefinitionPath} has no actions.");

        actions["Compose_ahe2e_padding"] = new JsonObject
        {
            ["runAfter"] = new JsonObject(),
            ["type"] = "Compose",
            ["inputs"] = new string('x', characters),
        };

        File.WriteAllText(workspace.DefinitionPath, document.ToJsonString(Indented));
    }

    public static void AddMarkerAction(SwWorkspace workspace, string marker)
    {
        var document = JsonNode.Parse(File.ReadAllText(workspace.DefinitionPath))
            ?? throw new E2EException($"{workspace.DefinitionPath} is empty.");

        var actions = document["properties"]?["definition"]?["actions"]?.AsObject()
            ?? throw new E2EException($"{workspace.DefinitionPath} has no actions.");

        actions[$"Compose_{marker}"] = new JsonObject
        {
            ["runAfter"] = new JsonObject(),
            ["type"] = "Compose",
            ["inputs"] = marker,
        };

        File.WriteAllText(workspace.DefinitionPath, document.ToJsonString(Indented));
    }

    private static string Source(SwRun run, SwShape shape) =>
        shape == SwShape.NoConnection
            ? Path.Combine(Path.GetDirectoryName(run.Services.FixturesDirectory)!, FolderOf(shape))
            : Path.Combine(run.Services.FixturesDirectory, FolderOf(shape));

    private static void IsolateReferences(SwRun run, string project, int index)
    {
        var declarations = Path.Combine(project, "infrastructure", "connections");
        var original = ReadDeclarations(declarations);

        if (original.Count == 0)
        {
            return;
        }

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordinal = 0;

        foreach (var (logicalName, connectorId) in original.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ordinal++;
            renames[logicalName] = $"{StandaloneServices.ReferencePrefix}{run.Context.RunSuffix}_{run.Key}_{index}{ordinal}_{ShortConnector(connectorId)}".ToLowerInvariant();
        }

        foreach (var file in Directory.GetFiles(project, "*", SearchOption.AllDirectories).Where(IsText))
        {
            var content = File.ReadAllText(file);
            var updated = renames.OrderByDescending(pair => pair.Key.Length).Aggregate(content, (text, pair) => text.Replace(pair.Key, pair.Value, StringComparison.Ordinal));

            if (!string.Equals(content, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(file, updated);
            }
        }

        foreach (var file in Directory.GetFiles(declarations))
        {
            var fileName = Path.GetFileName(file);

            if (renames.FirstOrDefault(pair => fileName.StartsWith(pair.Key + ".", StringComparison.Ordinal)) is { Key: { Length: > 0 } from, Value: var to })
            {
                File.Move(file, Path.Combine(declarations, to + fileName[from.Length..]));
            }
        }
    }

    private static Dictionary<string, string> ReadDeclarations(string declarations)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(declarations))
        {
            return references;
        }

        foreach (var file in Directory.GetFiles(declarations, "*.yaml"))
        {
            string? current = null;

            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim().TrimStart('-').Trim();

                if (line.StartsWith("connectionReferenceLogicalName:", StringComparison.Ordinal))
                {
                    current = Unquote(line["connectionReferenceLogicalName:".Length..].Trim());
                    references[current] = string.Empty;
                }
                else if (line.StartsWith("connectorId:", StringComparison.Ordinal) && current is not null)
                {
                    references[current] = Unquote(line["connectorId:".Length..].Trim());
                }
            }
        }

        return references;
    }

    private static string ShortConnector(string connectorId)
    {
        var name = connectorId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "conn";
        var trimmed = name.StartsWith("shared_", StringComparison.OrdinalIgnoreCase) ? name[7..] : name;
        var letters = string.Concat(trimmed.Where(char.IsAsciiLetterOrDigit));

        return letters.Length <= 12 ? letters : letters[..12];
    }

    private static void RewriteMetadata(string metadataPath, Func<string, string> rewrite) =>
        File.WriteAllLines(metadataPath, File.ReadAllLines(metadataPath).Select(rewrite));

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : value;

    private static bool IsText(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".json" or ".yml" or ".yaml";
}
