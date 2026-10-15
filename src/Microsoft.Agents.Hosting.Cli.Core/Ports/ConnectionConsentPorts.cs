using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Opens a URL in the operator's browser.</summary>
public interface IBrowserLauncher
{
    /// <summary>Opens <paramref name="url"/>.</summary>
    /// <param name="url">The URL to open.</param>
    void Launch(Uri url);
}

/// <summary>Reports that the operator has asked to abandon a wait that cannot otherwise be ended.</summary>
public interface IOperatorInterrupt
{
    /// <summary>Completes once the operator has asked to stop waiting.</summary>
    /// <param name="cancellationToken">Abandons the listen when the wait ends for another reason.</param>
    /// <returns>A task that completes when the operator interrupts, and otherwise runs until cancelled.</returns>
    Task WaitForInterruptAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates a Copilot Studio connection by walking the operator through the browser consent
/// experience.
/// </summary>
public interface IConnectionConsentFlow
{
    /// <summary>Opens the consent experience for one connector and waits for the result.</summary>
    /// <param name="environment">The environment the connection is created in.</param>
    /// <param name="connectorId">The connector to create a connection for.</param>
    /// <param name="openBrowser">Whether to launch a browser, or only print the link to open by hand.</param>
    /// <param name="cancellationToken">Cancels the flow.</param>
    /// <returns>What the consent experience reported.</returns>
    Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken);
}
