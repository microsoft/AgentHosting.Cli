using System.Net.Http;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>Opt-in HTTP tracing. Omit this handler entirely when diagnostics are disabled.</summary>
public sealed class HttpDiagnosticHandler(
    IHttpDiagnosticSink sink,
    HttpMessageHandler innerHandler,
    TimeProvider? timeProvider = null) : DelegatingHandler(innerHandler)
{
    private readonly IHttpDiagnosticSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The latest unavailable-output error. A broken diagnostic destination must not fail HTTP traffic.
    /// Hosts can inspect this when stderr itself cannot report the failure.
    /// </summary>
    public Exception? DiagnosticWriteError { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = _clock.GetTimestamp();
        void Emit(HttpDiagnosticStage stage, string? body = null, int? status = null,
            IReadOnlyDictionary<string, string>? headers = null, Exception? error = null,
            bool? bodyComplete = null, string? bodyDetail = null) =>
            Write(new(id, _clock.GetUtcNow(), stage)
            {
                Method = request.Method.Method,
                Url = request.RequestUri?.AbsoluteUri,
                Headers = headers,
                StatusCode = status,
                Body = body,
                BodyComplete = bodyComplete,
                Elapsed = _clock.GetElapsedTime(started),
                Failure = error is null ? null : Classify(error, cancellationToken),
                ExceptionType = error?.GetType().Name,
                ExceptionMessage = error?.Message,
                BodyDetail = bodyDetail,
            });

        Emit(HttpDiagnosticStage.Request, headers: HttpDiagnosticLoggingPolicy.FormatRequestHeaders(
            request.Content is { } content ? request.Headers.Concat(content.Headers) : request.Headers));
        var originalContent = request.Content;
        HttpDiagnosticContent? requestObserver = null;
        try
        {
            string? requestBody;
            if (request.Content is null)
            {
                requestBody = HttpDiagnosticLoggingPolicy.FormatRequestBody([]);
            }
            else if (request.Content is ByteArrayContent &&
                     request.Content.Headers.ContentLength is <= HttpDiagnosticLoggingPolicy.MaximumBodyBytes)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                requestBody = HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes, request.Content.Headers.ContentType?.ToString());
            }
            else if (request.Content is ByteArrayContent)
            {
                requestBody = HttpDiagnosticLoggingPolicy.GetBodyMarker(request.Content.Headers.ContentType?.ToString(), isRequest: true) ??
                    "[body omitted: capture limit exceeded]";
            }
            else if (request.Content is StreamContent or MultipartContent)
            {
                requestBody = HttpDiagnosticLoggingPolicy.FormatRequestBody([], request.Content.Headers.ContentType?.ToString(), isStreaming: true);
            }
            else
            {
                var requestContentType = request.Content.Headers.ContentType?.ToString();
                var requestMarker = HttpDiagnosticLoggingPolicy.GetBodyMarker(requestContentType, isRequest: true);
                if (requestMarker is not null)
                {
                    requestBody = requestMarker;
                }
                else
                {
                    requestObserver = new HttpDiagnosticContent(request.Content, (bytes, complete, _, _) =>
                        Emit(HttpDiagnosticStage.RequestBody, complete
                            ? HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes.Span, requestContentType)
                            : "[body incomplete: request was not fully sent]"), ownsInner: false);
                    request.Content = requestObserver;
                    requestBody = null;
                }
            }

            if (requestBody is not null)
            {
                Emit(HttpDiagnosticStage.RequestBody, requestBody);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Emit(HttpDiagnosticStage.ResponseHeaders, status: (int)response.StatusCode,
                headers: HttpDiagnosticLoggingPolicy.FormatResponseHeaders(response.Headers.Concat(response.Content.Headers)));

            var responseContent = response.Content;
            var contentType = responseContent.Headers.ContentType?.ToString();
            var marker = HttpDiagnosticLoggingPolicy.GetBodyMarker(contentType, isRequest: false,
                contentDisposition: responseContent.Headers.ContentDisposition?.ToString());
            if (marker is not null)
            {
                Emit(HttpDiagnosticStage.ResponseBody, marker);
            }
            else if (request.Method == HttpMethod.Head || responseContent.Headers.ContentLength == 0 ||
                     response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotModified)
            {
                Emit(HttpDiagnosticStage.ResponseBody, string.Empty, bodyComplete: true);
            }
            else
            {
                response.Content = new HttpDiagnosticContent(responseContent, (bytes, complete, error, observed) =>
                {
                    Emit(error is null ? HttpDiagnosticStage.ResponseBody : HttpDiagnosticStage.Failed, complete
                        ? HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes.Span, contentType)
                        : "[body incomplete: response was not fully consumed]", bodyComplete: complete,
                        error: error,
                        bodyDetail: request.Options.TryGetValue(HttpDiagnostics.ResponseBodyLimit, out var limit) && observed > limit
                            ? FormattableString.Invariant($"response larger than {limit} bytes, discarded unread")
                            : null);
                });
            }

            return response;
        }
        catch (Exception error)
        {
            Emit(HttpDiagnosticStage.Failed, error: error);
            throw;
        }
        finally
        {
            if (requestObserver is not null)
            {
                if (ReferenceEquals(request.Content, requestObserver))
                {
                    request.Content = originalContent;
                }

                requestObserver.Dispose();
            }
        }
    }

    private static HttpDiagnosticFailure Classify(Exception error, CancellationToken cancellationToken) =>
        error is OperationCanceledException canceled
            ? cancellationToken.IsCancellationRequested ||
              canceled.CancellationToken.IsCancellationRequested
                ? HttpDiagnosticFailure.Cancellation
                : HttpDiagnosticFailure.Timeout
            : HttpDiagnosticFailure.Transport;

    private void Write(HttpDiagnosticEvent diagnosticEvent)
    {
        try
        {
            _sink.Write(diagnosticEvent);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            DiagnosticWriteError = error;
        }
    }
}
