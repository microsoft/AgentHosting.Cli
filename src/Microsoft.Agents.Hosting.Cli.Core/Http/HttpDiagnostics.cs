namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>Shares opt-in diagnostics across an invocation without changing disabled transports.</summary>
public sealed class HttpDiagnostics
{
    private readonly IHttpDiagnosticSink? _sink;
    private Exception? _writeError;

    public HttpDiagnostics(IHttpDiagnosticSink? sink = null)
    {
        _sink = sink is null ? null : new ObservedSink(sink, RecordWriteError);
    }

    public static HttpDiagnostics Disabled { get; } = new();

    /// <summary>The caller's response-size rejection limit, for reporting discarded bodies without changing consumption.</summary>
    public static HttpRequestOptionsKey<int> ResponseBodyLimit { get; } = new("AgentHost.ResponseBodyLimit");

    /// <summary>The latest output failure, available even when stderr cannot report it.</summary>
    public Exception? DiagnosticWriteError => Volatile.Read(ref _writeError);

    /// <summary>Retains a host output failure alongside transport diagnostic failures.</summary>
    public void RecordWriteError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Volatile.Write(ref _writeError, error);
    }

    /// <summary>Wraps the existing transport, transferring its ownership to the returned handler.</summary>
    public HttpMessageHandler Wrap(HttpMessageHandler transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return _sink is null ? transport : new HttpDiagnosticHandler(_sink, transport);
    }

    private sealed class ObservedSink(IHttpDiagnosticSink inner, Action<Exception> recordError) : IHttpDiagnosticSink
    {
        public void Write(HttpDiagnosticEvent diagnosticEvent)
        {
            try
            {
                inner.Write(diagnosticEvent);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                recordError(error);
                throw;
            }
        }
    }
}
