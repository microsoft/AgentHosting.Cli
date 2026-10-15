namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Raised when a connector cannot be connected by this flow, so a caller with another way to create the connection can still try it.</summary>
public sealed class SilentConnectionUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why this flow does not apply to the connector.</param>
    public SilentConnectionUnavailableException(string message) : base(message)
    {
    }
}
