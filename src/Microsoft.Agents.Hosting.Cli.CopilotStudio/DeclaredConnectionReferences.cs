using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Reads the connection references a workspace declares on disk, in either layout: the classic
/// <c>connectionreferences.mcs.yml</c> or the CLI's one-file-per-reference
/// <c>infrastructure/connections</c> folder.
/// </summary>
internal static class DeclaredConnectionReferences
{
    private const string DeclarationFileName = "connectionreferences.mcs.yml";
    private const string ConnectionsFolderName = "connections";
    private const string InfrastructureFolderName = "infrastructure";
    private const string ConnectionFilePattern = "*.sync.yaml";

    /// <summary>Every connection reference the workspace declares, deduplicated by logical name.</summary>
    /// <param name="workspacePath">The workspace directory.</param>
    /// <returns>The declared references, classic declarations first.</returns>
    /// <exception cref="CopilotStudioException">One name is declared for two different connectors.</exception>
    public static ImmutableArray<ConnectionReference> Declared(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var references = ImmutableArray.CreateBuilder<ConnectionReference>();
        var declaredBy = new Dictionary<string, (int Index, string Path, string ConnectorId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var declarationPath in DeclarationPaths(workspacePath))
        {
            foreach (var reference in Parse(declarationPath))
            {
                var logicalName = reference.ConnectionReferenceLogicalName.Value;
                var connectorId = ConnectionConsentLink.NormalizeOptionalResourceId(reference.ConnectorId.Value);

                if (!declaredBy.TryGetValue(logicalName, out var first))
                {
                    declaredBy.Add(logicalName, (references.Count, declarationPath, connectorId));
                    references.Add(reference);
                }
                else if (first.ConnectorId.Length == 0)
                {
                    declaredBy[logicalName] = (first.Index, declarationPath, connectorId);
                    references[first.Index] = reference;
                }
                else if (connectorId.Length > 0 && !first.ConnectorId.Equals(connectorId, StringComparison.OrdinalIgnoreCase))
                {
                    throw CopilotStudioException.ConnectionReferencesConflict(
                        logicalName, first.Path, first.ConnectorId, declarationPath, connectorId);
                }
            }
        }

        return references.ToImmutable();
    }

    /// <summary>
    /// Creates the references the workspace declares on disk in the target, and reports them back as
    /// the target now records them.
    /// </summary>
    /// <remarks>
    /// The declarations on disk are the desired state, so a reference the workspace no longer declares
    /// is never recreated and an edited connector is never overwritten by the stale value a cloud
    /// cache still carries. The reported references carry the target's own row ids, which is what a
    /// baseline may record: a row that was not confirmed is never reported as though it existed.
    /// </remarks>
    /// <param name="workspacePath">The workspace directory.</param>
    /// <param name="synchronizer">Creates the references the target does not hold.</param>
    /// <param name="dataverseClient">Creates and reads the rows.</param>
    /// <param name="pushedConnectorIds">Row ids of custom connectors pushed in this run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The references the target confirmed, in declaration order.</returns>
    public static async Task<ImmutableArray<ConnectionReference>> ProvisionAsync(
        string workspacePath,
        IWorkspaceSynchronizer synchronizer,
        ISyncDataverseClient dataverseClient,
        IReadOnlyDictionary<string, Guid> pushedConnectorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(synchronizer);
        ArgumentNullException.ThrowIfNull(dataverseClient);
        cancellationToken.ThrowIfCancellationRequested();

        var declared = Declared(workspacePath);

        if (declared.IsDefaultOrEmpty)
        {
            return [];
        }

        await synchronizer.ProvisionConnectionReferencesAsync(
            BotDefinition.Empty.WithConnectionReferences(declared),
            dataverseClient,
            cancellationToken,
            pushedConnectorIds).ConfigureAwait(false);

        return await ConfirmAsync(declared, dataverseClient, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads back what the target holds for each declared reference, refusing any the target does not
    /// serve as the workspace asked.
    /// </summary>
    /// <remarks>
    /// The library reports a reference it could not create or re-point through its progress channel
    /// and completes anyway, so a successful call is not evidence the target changed. Reading the rows
    /// back is: a missing row, or one still pointing at the connector the declaration replaced, means
    /// the request did not take effect and nothing may be recorded as though it had.
    /// </remarks>
    private static async Task<ImmutableArray<ConnectionReference>> ConfirmAsync(
        ImmutableArray<ConnectionReference> declared,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken)
    {
        var names = declared.Select(reference => reference.ConnectionReferenceLogicalName.Value).ToArray();
        var rows = await dataverseClient.GetConnectionReferencesByLogicalNamesAsync(names, cancellationToken).ConfigureAwait(false);
        var byName = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ConnectionReferenceLogicalName))
            .ToDictionary(row => row.ConnectionReferenceLogicalName, StringComparer.OrdinalIgnoreCase);

        var unconfirmed = declared
            .Where(reference => !IsServedAsDeclared(reference, byName))
            .Select(reference => reference.ConnectionReferenceLogicalName.Value)
            .ToArray();

        if (unconfirmed.Length > 0)
        {
            throw CopilotStudioException.ServiceError(
                "provision connection references",
                $"The target did not confirm these connection references as the workspace declares them: {string.Join(", ", unconfirmed)}. Nothing was recorded as deployed.");
        }

        return [.. declared.Select(reference => Confirmed(byName[reference.ConnectionReferenceLogicalName.Value]))];
    }

    private static bool IsServedAsDeclared(
        ConnectionReference declared,
        Dictionary<string, SyncDataverseClient.ConnectionReferenceInfo> rows) =>
        rows.TryGetValue(declared.ConnectionReferenceLogicalName.Value, out var row)
        && row.ConnectionReferenceId != Guid.Empty
        && ServesConnector(declared.ConnectorId.Value, row.ConnectorId);

    private static bool ServesConnector(string? declared, string? confirmed)
    {
        var requested = ConnectionConsentLink.NormalizeOptionalResourceId(declared);
        var served = ConnectionConsentLink.NormalizeOptionalResourceId(confirmed);

        if (requested.Length == 0 || served.Length == 0)
        {
            return false;
        }

        if (requested.Equals(served, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CustomConnectorInternalId.StablePrefixOf(requested) is { } prefix && served.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectionReference Confirmed(SyncDataverseClient.ConnectionReferenceInfo row) =>
        new ConnectionReference.Builder
        {
            Id = new ConnectionReferenceId(row.ConnectionReferenceId),
            ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName(row.ConnectionReferenceLogicalName),
            ConnectorId = new ConnectorId(row.ConnectorId!),
            ConnectionId = row.ConnectionId ?? string.Empty,
        }.Build();

    /// <summary>
    /// Adds the references a run confirmed against the target to what the workflow upsert reported.
    /// </summary>
    /// <remarks>
    /// The upsert reports only the references a flow names, so a reference declared for anything else
    /// is absent from the baseline the sync writes. The next push reads that baseline, finds the
    /// reference missing, and asks the service to create a row that already exists. Every reference
    /// added here was read back from the target, so recording it describes the target rather than the
    /// workspace.
    /// </remarks>
    /// <param name="reported">What the workflow upsert reported, absent when no upsert ran.</param>
    /// <param name="provisioned">The references this run confirmed against the target.</param>
    /// <returns>The metadata to record, unchanged when the run confirmed nothing.</returns>
    [return: NotNullIfNotNull(nameof(reported))]
    public static CloudFlowMetadata? WithProvisioned(CloudFlowMetadata? reported, ImmutableArray<ConnectionReference> provisioned)
    {
        if (provisioned.IsDefaultOrEmpty)
        {
            return reported;
        }

        return new CloudFlowMetadata
        {
            Workflows = reported?.Workflows ?? [],
            ConnectionReferences = Merge(provisioned, reported?.ConnectionReferences ?? []),
            Succeeded = reported?.Succeeded ?? true,
        };
    }

    /// <summary>
    /// Combines two sets of references into one, keeping the first record of each logical name.
    /// </summary>
    /// <param name="preferred">The references whose record of a shared name wins.</param>
    /// <param name="additional">References contributed only where <paramref name="preferred"/> is silent.</param>
    /// <returns>The union, in order, with one entry per logical name.</returns>
    public static ImmutableArray<ConnectionReference> Merge(
        ImmutableArray<ConnectionReference> preferred, ImmutableArray<ConnectionReference> additional)
    {
        var merged = ImmutableArray.CreateBuilder<ConnectionReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in preferred.IsDefaultOrEmpty ? [] : preferred)
        {
            if (seen.Add(reference.ConnectionReferenceLogicalName.Value))
            {
                merged.Add(reference);
            }
        }

        foreach (var reference in additional.IsDefaultOrEmpty ? [] : additional)
        {
            if (seen.Add(reference.ConnectionReferenceLogicalName.Value))
            {
                merged.Add(reference);
            }
        }

        return merged.ToImmutable();
    }

    private static IEnumerable<string> DeclarationPaths(string workspacePath)
    {
        var classic = Path.Combine(workspacePath, DeclarationFileName);

        if (File.Exists(classic))
        {
            yield return classic;
        }

        var connectionsFolder = Path.Combine(workspacePath, InfrastructureFolderName, ConnectionsFolderName);

        if (!Directory.Exists(connectionsFolder))
        {
            yield break;
        }

        foreach (var path in Directory
            .EnumerateFiles(connectionsFolder, ConnectionFilePattern)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    private static ImmutableArray<ConnectionReference> Parse(string declarationPath)
    {
        var content = ReadDeclaration(declarationPath);

        try
        {
            using var serializationContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: true);
            var parsed = CodeSerializer.Deserialize<ConnectionReferencesSourceFile>(content, sourceUri: null);

            if (parsed is null || parsed.ConnectionReferences.IsDefaultOrEmpty)
            {
                return ImmutableArray<ConnectionReference>.Empty;
            }

            return parsed.ConnectionReferences.Any(reference => !HasLogicalName(reference))
                ? throw CopilotStudioException.ConnectionReferencesUnreadable(declarationPath, "a declared reference has no connection reference logical name")
                : parsed.ConnectionReferences;
        }
        catch (Exception failure) when (failure is not CopilotStudioException and not OperationCanceledException)
        {
            throw CopilotStudioException.ConnectionReferencesUnreadable(declarationPath, failure.Message, failure);
        }
    }

    private static string ReadDeclaration(string declarationPath)
    {
        try
        {
            return File.ReadAllText(declarationPath, Encoding.UTF8);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw CopilotStudioException.ConnectionReferencesUnreadable(declarationPath, failure.Message, failure);
        }
    }

    private static bool HasLogicalName(ConnectionReference reference) => !string.IsNullOrWhiteSpace(reference?.ConnectionReferenceLogicalName.Value);
}
