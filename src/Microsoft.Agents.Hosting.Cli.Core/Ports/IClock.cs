namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Deterministic time source for metadata timestamps and fixtures. Injecting it keeps report and
/// connection-metadata output reproducible under test.
/// </summary>
public interface IClock
{
    /// <summary>The current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}
