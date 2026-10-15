using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Translates the sync library's change records into the transport-neutral shape the command layer
/// and the JSON output contract use.
/// </summary>
public sealed class CopilotChangeMapper
{
    /// <summary>Maps a set of sync library changes.</summary>
    /// <param name="changes">The changes to map.</param>
    /// <returns>The mapped changes, ordered by schema name.</returns>
    // Deliberately an instance member: registered as a singleton and injected, so the mapping rule stays
    // a replaceable seam rather than a hard-coded static call.
#pragma warning disable CA1822
    public IReadOnlyList<CopilotChange> Map(ImmutableArray<Change> changes) => [.. changes.Select(Map).OrderBy(change => change.SchemaName, StringComparer.Ordinal)];
#pragma warning restore CA1822

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
