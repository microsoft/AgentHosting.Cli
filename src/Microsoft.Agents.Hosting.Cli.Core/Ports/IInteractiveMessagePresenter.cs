namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Surfaces an interactive sign-in instruction to the operator — currently the device-code flow's
/// verification message. Keeps <see cref="Microsoft.Agents.Hosting.Cli.Core.Operations.AgentHostingOperations"/>
/// free of console coupling: the facade forwards MSAL's message through this port and the host writes
/// it to stderr. PAC writes the same message through <c>IOutputWindow</c>. This port never reads input
/// and never blocks on a prompt.
/// </summary>
public interface IInteractiveMessagePresenter
{
    /// <summary>Presents <paramref name="message"/> to the operator (for example on stderr).</summary>
    Task PresentAsync(string message, CancellationToken cancellationToken);
}
