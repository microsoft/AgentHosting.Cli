namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Reads what the operator pipes into the process, such as a secret that must stay off the command line.</summary>
public interface IStandardInput
{
    /// <summary>Gets whether standard input comes from a pipe or a file rather than a terminal.</summary>
    bool IsRedirected { get; }

    /// <summary>Reads all of standard input as text, refusing more than <paramref name="maximumBytes"/> bytes without showing them.</summary>
    Task<string> ReadToEndAsync(int maximumBytes, CancellationToken cancellationToken);
}
