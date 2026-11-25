using System.Globalization;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>Writes request and body-completion HTTP diagnostic sections to an injected stderr writer.</summary>
public sealed class TextWriterHttpDiagnosticSink(TextWriter writer, bool color = false) : IHttpDiagnosticSink
{
    private readonly TextWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HttpDiagnosticEvent> _responses = [];
    private Guid? _requestSection;

    public void Write(HttpDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        lock (_gate)
        {
            var output = new StringBuilder();
            switch (diagnosticEvent.Stage)
            {
                case HttpDiagnosticStage.Request:
                    AppendHeading(output, diagnosticEvent, "Request:", "34", "> ");
                    AppendHeaders(output, diagnosticEvent.Headers, "> ");
                    _requestSection = diagnosticEvent.RequestId;
                    break;
                case HttpDiagnosticStage.RequestBody:
                    if (_requestSection != diagnosticEvent.RequestId)
                    {
                        AppendHeading(output, diagnosticEvent, "Request (continued)", "34", "> ");
                    }

                    AppendField(output, "Body", diagnosticEvent.Body, "> ");
                    _requestSection = diagnosticEvent.RequestId;
                    break;
                case HttpDiagnosticStage.ResponseHeaders:
                    _responses[diagnosticEvent.RequestId] = diagnosticEvent;
                    return;
                case HttpDiagnosticStage.ResponseBody:
                case HttpDiagnosticStage.Failed:
                    _responses.Remove(diagnosticEvent.RequestId, out var headers);
                    AppendResponse(output, diagnosticEvent, headers);
                    _requestSection = null;
                    break;
            }

            _writer.Write(output.ToString());
            _writer.Flush();
        }
    }

    private void AppendResponse(
        StringBuilder output, HttpDiagnosticEvent terminal, HttpDiagnosticEvent? headers)
    {
        var failed = terminal.Stage == HttpDiagnosticStage.Failed;
        var completionObserved = failed || terminal.BodyComplete is not null;
        var status = headers?.StatusCode ?? terminal.StatusCode;
        var headingColor = failed ? "31" : StatusColor(status);
        var heading = failed ? "Failed" : "Response";
        if (completionObserved && terminal.Elapsed is { } elapsed)
        {
            heading += string.Create(CultureInfo.InvariantCulture, $" after {elapsed.TotalMilliseconds:F1} ms");
        }

        if (!completionObserved)
        {
            heading += " (body completion not observed)";
        }
        else if (!failed && terminal.BodyComplete == false)
        {
            heading += " (body incomplete)";
        }

        var prefix = failed ? string.Empty : "< ";
        AppendHeading(output, terminal, heading, headingColor, prefix, status);
        AppendHeaders(output, headers?.Headers, prefix);
        AppendField(output, "Content", terminal.Body, prefix);
        AppendField(output, "Detail", terminal.BodyDetail, prefix);
        if (terminal.Failure is { } failure)
        {
            AppendField(output, "Error", failure.ToString(), prefix);
        }
        AppendField(output, "Exception", terminal.ExceptionType, prefix);
        AppendField(output, "Message", terminal.ExceptionMessage, prefix);
    }

    private void AppendHeading(
        StringBuilder output, HttpDiagnosticEvent diagnosticEvent, string heading, string headingColor,
        string prefix, int? status = null)
    {
        if (color)
        {
            output.Append("\u001b[90m");
        }

        output.Append(diagnosticEvent.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        output.Append(CultureInfo.InvariantCulture, $" - {diagnosticEvent.RequestId:N}");
        if (color)
        {
            output.Append("\u001b[0m");
        }

        output.Append(' ').Append(prefix);
        if (color)
        {
            output.Append("\u001b[").Append(headingColor).Append('m');
        }

        output.Append(heading);
        if (diagnosticEvent.Stage == HttpDiagnosticStage.Request)
        {
            if (diagnosticEvent.Method is { } method)
            {
                output.Append(' ').Append(method);
            }

            if (diagnosticEvent.Url is { } url)
            {
                output.Append(' ');
                if (color)
                {
                    output.Append("\u001b[33m");
                }

                output.Append(url);
            }
        }

        if (status is { } code)
        {
            output.Append(" | Status: ").Append(code.ToString(CultureInfo.InvariantCulture));
        }

        if (color)
        {
            output.Append("\u001b[0m");
        }

        output.AppendLine();
    }

    private static void AppendField(StringBuilder output, string label, string? value, string prefix)
    {
        if (value is null)
        {
            return;
        }

        output.Append(prefix).Append(label).Append(": ");
        var remaining = value.AsSpan();
        while (remaining.IndexOfAny('\r', '\n') is var lineEnd && lineEnd >= 0)
        {
            output.Append(remaining[..lineEnd]);
            var newlineLength = remaining[lineEnd] == '\r' && lineEnd + 1 < remaining.Length && remaining[lineEnd + 1] == '\n'
                ? 2 : 1;
            output.Append(remaining.Slice(lineEnd, newlineLength));
            remaining = remaining[(lineEnd + newlineLength)..];
            if (remaining.IsEmpty)
            {
                return;
            }

            output.Append(prefix);
        }

        output.Append(remaining);
        output.AppendLine();
    }

    private static void AppendHeaders(StringBuilder output, IReadOnlyDictionary<string, string>? headers, string prefix)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            AppendField(output, header.Key, header.Value, prefix);
        }
    }

    private static string StatusColor(int? status) => status switch
    {
        >= 200 and < 300 => "32",
        >= 300 and < 400 => "33",
        >= 400 and < 600 => "31",
        _ => "34",
    };
}
