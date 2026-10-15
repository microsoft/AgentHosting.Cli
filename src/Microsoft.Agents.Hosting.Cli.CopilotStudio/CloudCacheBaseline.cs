using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Edits the last-synced agent snapshot the sync library keeps in <c>.mcs/botdefinition.json</c>.</summary>
internal static class CloudCacheBaseline
{
    private static readonly AgentFilePath SnapshotPath = new(".mcs/botdefinition.json");
    private static readonly AgentFilePath LegacySnapshotPath = new(".mcs/botdefinition.yml");

    /// <summary>
    /// Removes one connection reference from the snapshot, leaving every other component's last-synced state as it was.
    /// </summary>
    /// <param name="workspace">The workspace holding the snapshot.</param>
    /// <param name="logicalName">The connection reference that no longer exists in either place.</param>
    /// <returns><see langword="true"/> when the snapshot listed it and was rewritten without it.</returns>
    public static bool RetireConnectionReference(IFileAccessor workspace, string logicalName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        if (Read(workspace) is not { } snapshot)
        {
            return false;
        }

        var surviving = snapshot.ConnectionReferences
            .Where(reference => !logicalName.Equals(reference.ConnectionReferenceLogicalName.Value, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (surviving.Length == snapshot.ConnectionReferences.Length)
        {
            return false;
        }

        Write(workspace, snapshot.WithConnectionReferences(surviving));

        return true;
    }

    private static DefinitionBase? Read(IFileAccessor workspace)
    {
        if (workspace.Exists(SnapshotPath))
        {
            using var snapshot = workspace.OpenRead(SnapshotPath);
            using var context = YamlSerializationContext.UseYamlPassThroughSerializationContext();

            return JsonSerializer.Deserialize<DefinitionBase>(snapshot, ElementSerializer.CreateOptions());
        }

        if (!workspace.Exists(LegacySnapshotPath))
        {
            return null;
        }

        using var legacy = workspace.OpenRead(LegacySnapshotPath);
        using var reader = new StreamReader(legacy, Encoding.UTF8);

        return YamlSerializer.Deserialize<DefinitionBase>(reader);
    }

    private static void Write(IFileAccessor workspace, DefinitionBase snapshot)
    {
        using var destination = workspace.OpenWrite(SnapshotPath);
        using var context = YamlSerializationContext.UseYamlPassThroughSerializationContext();

        JsonSerializer.Serialize(destination, snapshot, ElementSerializer.CreateOptions());
    }
}
