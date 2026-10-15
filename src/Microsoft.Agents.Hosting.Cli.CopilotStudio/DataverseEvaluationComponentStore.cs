using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Deletes evaluation components through the Dataverse Web API, which owns the underlying rows.</summary>
public sealed class DataverseEvaluationComponentStore : IEvaluationComponentStore
{
    private readonly DataverseWebApiClient _dataverse;

    /// <summary>Creates the store.</summary>
    /// <param name="dataverse">The Dataverse Web API request plumbing.</param>
    public DataverseEvaluationComponentStore(DataverseWebApiClient dataverse) => _dataverse = dataverse ?? throw new ArgumentNullException(nameof(dataverse));

    /// <inheritdoc />
    public async Task DeleteAsync(CopilotEnvironmentContext environment, string componentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);

        (await _dataverse.SendAsync(environment, HttpMethod.Delete, $"api/data/v9.2/botcomponents({Uri.EscapeDataString(componentId)})", cancellationToken).ConfigureAwait(false))?.Dispose();
    }
}
