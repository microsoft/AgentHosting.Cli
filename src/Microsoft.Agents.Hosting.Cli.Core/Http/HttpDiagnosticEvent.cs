namespace Microsoft.Agents.Hosting.Cli.Core.Http;

public enum HttpDiagnosticStage
{
    Request,
    RequestBody,
    ResponseHeaders,
    ResponseBody,
    Failed,
}

public enum HttpDiagnosticFailure
{
    Transport,
    Timeout,
    Cancellation,
}

/// <summary>
/// A display-ready HTTP diagnostic snapshot. Header masks are applied before emission;
/// ordinary URLs and bodies can contain sensitive data.
/// </summary>
public sealed record HttpDiagnosticEvent(
    Guid RequestId,
    DateTimeOffset Timestamp,
    HttpDiagnosticStage Stage)
{
    public string? Method { get; init; }
    /// <summary>The transport's full request URL, without diagnostic rewriting or query masking.</summary>
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public int? StatusCode { get; init; }
    public string? Body { get; init; }
    /// <summary>
    /// For terminal response events, whether consumption completed. Null means body completion is not observed.
    /// </summary>
    public bool? BodyComplete { get; init; }
    public TimeSpan? Elapsed { get; init; }
    public HttpDiagnosticFailure? Failure { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? BodyDetail { get; init; }
}

/// <summary>
/// Receives policy-formatted events, not HTTP messages or exceptions. Implementations serialize
/// concurrent writes and flush events without writing to stdout, files or telemetry.
/// </summary>
public interface IHttpDiagnosticSink
{
    void Write(HttpDiagnosticEvent diagnosticEvent);
}
