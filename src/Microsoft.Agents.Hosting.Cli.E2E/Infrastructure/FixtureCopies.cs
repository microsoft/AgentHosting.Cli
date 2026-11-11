using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static class FixtureCopies
{
    public static void CopyTree(string source, string destination, bool keepLocalState) => CopyTree(source, destination, keepLocalState, relative => relative);

    public static void CopyWorkflow(string source, string destination, string displayName, Guid workflowId, bool keepLocalState)
    {
        var fixtureFolder = Path.Combine(WorkflowWorkspace.WorkflowsFolderName, Path.GetFileName(Directory.EnumerateDirectories(WorkflowWorkspace.WorkflowsDirectory(source)).Single()));
        var workflowFolder = Path.Combine(WorkflowWorkspace.WorkflowsFolderName, WorkflowWorkspace.FolderNameFor(displayName, workflowId));

        CopyTree(source, destination, keepLocalState, relative => IsWithin(relative, fixtureFolder) ? workflowFolder + relative[fixtureFolder.Length..] : relative);
        Reidentify(Path.Combine(destination, workflowFolder), displayName, workflowId);
    }

    public static void Reidentify(string workflowFolder, string displayName, Guid workflowId)
    {
        var metadataPath = Path.Combine(workflowFolder, WorkflowWorkspace.MetadataFileName);

        File.WriteAllLines(metadataPath, File.ReadAllLines(metadataPath).Select(line => Reidentified(line, Path.GetFileName(workflowFolder), displayName, workflowId)));
    }

    public static string Yaml(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_' or '.') && value[0] != ' ' && value[^1] != ' '
            ? value
            : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void CopyTree(string source, string destination, bool keepLocalState, Func<string, string> relocate)
    {
        if (!Directory.Exists(source))
        {
            throw new E2EException($"The fixture {source} is missing.");
        }

        FileTree.CopyTree(source, destination, relative => keepLocalState || !IsLocalState(relative) ? relocate(relative) : null);
    }

    private static string Reidentified(string line, string folderName, string displayName, Guid workflowId) =>
        line.StartsWith("workflowId:", StringComparison.Ordinal) ? $"workflowId: {workflowId}"
        : line.StartsWith("name:", StringComparison.Ordinal) ? $"name: {Yaml(displayName)}"
        : line.StartsWith("jsonFileName:", StringComparison.Ordinal) ? $"jsonFileName: {WorkflowWorkspace.WorkflowsFolderName}/{folderName}/{WorkflowWorkspace.DefinitionFileName}"
        : line;

    private static bool IsWithin(string relative, string folder) =>
        relative.Equals(folder, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalState(string relative) =>
        relative.StartsWith(".config", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".mcs", StringComparison.OrdinalIgnoreCase);
}
