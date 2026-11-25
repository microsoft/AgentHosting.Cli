using System.Globalization;
using System.Text;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal sealed class MsalDiagnosticWriter(
    TextWriter writer, bool color = false, Action<Exception>? recordWriteError = null)
{
    private readonly TextWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly Lock _gate = new();
    private Exception? _writeError;
    private string? _context;

    public Exception? DiagnosticWriteError => Volatile.Read(ref _writeError);

    public void Write(LogLevel level, string message, bool containsPii)
    {
        lock (_gate)
        {
            var output = new StringBuilder();
            if (Parse(message) is { } sdk)
            {
                if (_context != sdk.Context)
                {
                    AppendMetadata(output, sdk.Timestamp);
                    output.Append(' ').AppendLine(sdk.Context);
                    _context = sdk.Context;
                }

                AppendMetadata(output, sdk.Timestamp, sdk.CorrelationId);
                var body = sdk.Message.AsSpan().Trim();
                if (!body.IsEmpty)
                {
                    output.Append(' ');
                }

                output.Append(body).AppendLine();
            }
            else
            {
                var body = message.AsSpan().Trim();
                if (!body.IsEmpty)
                {
                    output.Append(body).AppendLine();
                }
            }

            Flush(output);
        }
    }

    private void AppendMetadata(StringBuilder output, string timestamp, string? correlationId = null)
    {
        if (color)
        {
            output.Append("\u001b[90m");
        }

        output.Append(timestamp);
        if (correlationId is not null)
        {
            output.Append(" - ").Append(correlationId);
        }
        if (color)
        {
            output.Append("\u001b[0m");
        }

        output.Append(" [MSAL]");
    }

    private void Flush(StringBuilder output)
    {
        try
        {
            _writer.Write(output.ToString());
            _writer.Flush();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Volatile.Write(ref _writeError, error);
            recordWriteError?.Invoke(error);
        }
    }

    private static SdkMessage? Parse(string message)
    {
        // MSAL 4.92.0 LoggerHelper.FormatLogMessage emits:
        // {PII enabled} MSAL {version} {SKU} {runtime} {OS} [{timestamp}{correlation}]{client information} {message}
        // Keep everything before valid metadata as context, including the leading PII boolean.
        // Show "{timestamp} [MSAL] {context}" once per unchanged context to reduce clutter,
        // then show "{timestamp} - {correlationId} [MSAL] {message}", omitting the separator and ID when absent.
        // Format SDK time like HTTP (UTC with milliseconds; MSAL's whole seconds become .000).
        // Keep ID spelling and message content apart from outer whitespace.
        // Only valid metadata anchors the split. Unknown envelopes keep their text without a source tag.
        for (var start = message.IndexOf('['); start >= 0; start = message.IndexOf('[', start + 1))
        {
            var remaining = message.AsSpan(start + 1);
            var metadataLength = remaining.Length >= 21 && remaining[20] == ']'
                ? 20
                : remaining.Length >= 60 && remaining[59] == ']' && remaining.Slice(20, 3).SequenceEqual(" - ") &&
                    Guid.TryParseExact(remaining.Slice(23, 36), "D", out _)
                    ? 59 : 0;
            if (metadataLength == 0 ||
                !DateTimeOffset.TryParseExact(remaining[..20], "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                continue;
            }

            var context = message.AsSpan(0, start).Trim();
            var end = start + metadataLength + 2;
            return new(context.ToString(),
                timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                metadataLength == 59 ? remaining.Slice(23, 36).ToString() : null, message[end..]);
        }

        return null;
    }

    private readonly record struct SdkMessage(
        string Context, string Timestamp, string? CorrelationId, string Message);
}
