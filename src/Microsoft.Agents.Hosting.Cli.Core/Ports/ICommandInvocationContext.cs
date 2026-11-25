namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Provides optional correlation for the currently executing command without owning telemetry policy.</summary>
public interface ICommandInvocationContext
{
    /// <summary>The invocation's session identifier, or null when correlation is suppressed or no command is active.</summary>
    Guid? SessionId { get; }
}
