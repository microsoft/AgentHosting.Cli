using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

/// <summary>
/// Records the connection references a run provisions, so the order and the arguments can be
/// asserted without a Dataverse.
/// </summary>
internal class RecordingConnectionReferenceClient : SyncDataverseClient, ISyncDataverseClient
{
    private readonly Lock _gate = new();

    public RecordingConnectionReferenceClient() : base(new StubDataverseHttpClientAccessor(), "AgentHost.Cli.Tests")
    {
    }

    public List<string> EnsuredLogicalNames { get; } = [];

    public List<string> EnsuredConnectorIds { get; } = [];

    public List<Guid?> EnsuredCustomConnectorRowIds { get; } = [];

    public Dictionary<string, ConnectionReferenceInfo> References { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ProvisioningThrows { get; init; }

    public Exception? QueryThrows { get; init; }

    public bool OmitProvisionedReferences { get; init; }

    public override Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProvisioningThrows is not null)
        {
            return Task.FromException(ProvisioningThrows);
        }

        lock (_gate)
        {
            EnsuredLogicalNames.Add(connectionReferenceLogicalName);
            EnsuredConnectorIds.Add(connectorId);
            EnsuredCustomConnectorRowIds.Add(customConnectorRowId);
            References.TryAdd(connectionReferenceLogicalName, new ConnectionReferenceInfo
            {
                ConnectionReferenceId = Guid.NewGuid(),
                ConnectionReferenceLogicalName = connectionReferenceLogicalName,
                ConnectorId = connectorId,
            });
        }

        return Task.CompletedTask;
    }

    public override Task<ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueryThrows is not null)
        {
            return Task.FromException<ConnectionReferenceInfo[]>(QueryThrows);
        }

        lock (_gate)
        {
            return Task.FromResult<ConnectionReferenceInfo[]>(OmitProvisionedReferences
                ? []
                : [.. logicalNames.Where(References.ContainsKey).Select(name => References[name])]);
        }
    }
}
