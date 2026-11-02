using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Translates the sync library's change records into the transport-neutral shape the command layer
/// and the JSON output contract use.
/// </summary>
public sealed class CopilotChangeMapper
{
    private static readonly string[] AssetSchemaNamePrefixes = [CustomConnectorLayout.SchemaNamePrefix, AiPromptLayout.SchemaNamePrefix, WorkflowLayout.SchemaNamePrefix];

    /// <summary>Maps a set of sync library changes, reporting each custom connector, AI prompt and workflow once however many of its files changed.</summary>
    /// <param name="changes">The changes to map.</param>
    /// <returns>The mapped changes, ordered by schema name.</returns>
    // Deliberately an instance member: registered as a singleton and injected, so the mapping rule stays
    // a replaceable seam rather than a hard-coded static call.
#pragma warning disable CA1822
    public IReadOnlyList<CopilotChange> Map(ImmutableArray<Change> changes) => [.. OnePerAsset(changes).Select(Map).OrderBy(change => change.SchemaName, StringComparer.Ordinal)];
#pragma warning restore CA1822

    private static IEnumerable<Change> OnePerAsset(ImmutableArray<Change> changes) => changes.Where(change => !IsAssetFile(change)).Concat(changes.Where(IsAssetFile).GroupBy(change => SyncedAssetSchemaName.GetAssetSchemaName(change.SchemaName), StringComparer.OrdinalIgnoreCase).Select(ToAssetChange));

    private static bool IsAssetFile(Change change) => change.SchemaName is { } schemaName && AssetSchemaNamePrefixes.Any(prefix => schemaName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static Change ToAssetChange(IGrouping<string, Change> files) => files.FirstOrDefault(file => string.Equals(file.SchemaName, files.Key, StringComparison.OrdinalIgnoreCase)) ?? (files.First() with { SchemaName = files.Key, ChangeType = ChangeType.Update });

    private static CopilotChange Map(Change change) => new()
    {
        Kind = MapKind(change),
        ComponentType = change.ChangeKind ?? "component",
        SchemaName = change.SchemaName ?? string.Empty,
    };

    private static string MapKind(Change change)
    {
        var kind = change.ChangeType.ToString();

        return kind switch
        {
            "Add" or "Added" or "Create" or "Created" => CopilotChangeKinds.Created,
            "Delete" or "Deleted" or "Remove" or "Removed" => CopilotChangeKinds.Deleted,
            _ => CopilotChangeKinds.Updated,
        };
    }
}
