using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Reads built on <see cref="ICopilotCatalogPort"/>.</summary>
public static class CopilotCatalogPortExtensions
{
    /// <summary>Reads an agent, or nothing when the service reports that it does not exist.</summary>
    /// <param name="catalog">The catalog to read.</param>
    /// <param name="environment">The environment the agent belongs to.</param>
    /// <param name="agentId">The agent to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The agent, or <see langword="null"/> only for a typed not-found; every other failure is thrown.</returns>
    public static async Task<CopilotAgentSummary?> FindAgentAsync(this ICopilotCatalogPort catalog, CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        try
        {
            return await catalog.GetAgentAsync(environment, agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (CopilotStudioException failure) when (failure.ErrorKind == CopilotErrorKinds.AgentNotFound)
        {
            return null;
        }
    }
}
