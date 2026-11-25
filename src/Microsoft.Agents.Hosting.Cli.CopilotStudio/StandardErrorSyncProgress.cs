using System.Globalization;
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
    private readonly TimeProvider _clock;
    private readonly bool _color;

    /// <summary>Creates a reporter writing to <paramref name="standardError"/>.</summary>
    /// <param name="standardError">The stream progress is written to.</param>
    /// <param name="verbose">Whether routine service progress and diagnostics are written.</param>
    /// <param name="timeProvider">The clock used for debug receipt timestamps.</param>
    /// <param name="color">Whether debug timestamps use ANSI gray.</param>
    public StandardErrorSyncProgress(TextWriter standardError, bool verbose = true, TimeProvider? timeProvider = null, bool color = false)
    {
        _standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
        _verbose = verbose;
        _clock = timeProvider ?? TimeProvider.System;
        _color = color;
    }

    /// <summary>Reports <paramref name="message"/> to standard error.</summary>
    /// <param name="message">The progress message.</param>
    public void Report(string message)
    {
        if (!string.IsNullOrWhiteSpace(message) && (_verbose || IsFailure(message)))
        {
            if (_verbose)
            {
                var timestamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                if (_color)
                {
                    timestamp = $"\u001b[90m{timestamp}\u001b[0m";
                }

                message = $"{timestamp} [SYNC] {message}";
            }

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
