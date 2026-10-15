using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Writes sync progress to standard error.
/// </summary>
public sealed class StandardErrorSyncProgress : ISyncProgress
{
    private const string FailurePrefix = "Failed to ";

    private readonly TextWriter _standardError;
    private readonly bool _verbose;

    /// <summary>Creates a reporter writing to <paramref name="standardError"/>.</summary>
    /// <param name="standardError">The stream progress is written to.</param>
    /// <param name="verbose">Whether routine service progress and diagnostics are written.</param>
    public StandardErrorSyncProgress(TextWriter standardError, bool verbose = true)
    {
        _standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
        _verbose = verbose;
    }

    /// <summary>Reports <paramref name="message"/> to standard error.</summary>
    /// <param name="message">The progress message.</param>
    public void Report(string message)
    {
        if (!string.IsNullOrWhiteSpace(message) && (_verbose || IsFailure(message)))
        {
            _standardError.WriteLine(message);
        }
    }

    /// <summary>
    /// Whether a message reports an operation the library gave up on.
    /// </summary>
    /// <param name="message">The reported message.</param>
    /// <returns><see langword="true"/> when the message has to be shown whatever the verbosity.</returns>
    private static bool IsFailure(string message) =>
        message.TrimStart().StartsWith(FailurePrefix, StringComparison.OrdinalIgnoreCase);
}
