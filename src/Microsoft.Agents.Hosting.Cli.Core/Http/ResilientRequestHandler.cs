using System.Net;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>
/// Retries throttled and transient responses with bounded exponential backoff and jitter, and stamps
/// every request with a client correlation identifier.
/// </summary>
public sealed class ResilientRequestHandler : DelegatingHandler
{
    /// <summary>The header carrying the client-generated correlation identifier.</summary>
    public const string ClientRequestIdHeaderName = "x-ms-client-request-id";

    private const int DefaultMaximumAttempts = 3;

    private readonly int _maximumAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _nextJitterFraction;

    /// <param name="maximumAttempts">Total attempts including the first.</param>
    /// <param name="delay">Waits between attempts; replaced in tests.</param>
    /// <param name="nextJitterFraction">Supplies a value in [0,1) used to spread retries; replaced in tests.</param>
    public ResilientRequestHandler(int maximumAttempts = DefaultMaximumAttempts, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? nextJitterFraction = null)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts), maximumAttempts, "At least one attempt is required.");
        }

        _maximumAttempts = maximumAttempts;
        _delay = delay ?? Task.Delay;
        _nextJitterFraction = nextJitterFraction ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>The delay a throttled response asks for, or the backoff for the attempt when it asks for none.</summary>
    public static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt, double jitterFraction)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until < TimeSpan.Zero ? TimeSpan.Zero : until;
        }

        return Backoff(attempt, jitterFraction);
    }

    /// <summary>Exponential backoff for an attempt, spread by <paramref name="jitterFraction"/>.</summary>
    public static TimeSpan Backoff(int attempt, double jitterFraction)
    {
        var seconds = Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(seconds + (seconds * 0.5 * jitterFraction));
    }

    /// <summary>Whether a status code may be retried for the given method.</summary>
    public static bool ShouldRetry(HttpMethod method, HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests
        || (statusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && IsIdempotent(method));

    private static bool IsIdempotent(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Put || method == HttpMethod.Delete || method == HttpMethod.Head;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.Contains(ClientRequestIdHeaderName))
        {
            request.Headers.TryAddWithoutValidation(ClientRequestIdHeaderName, Guid.NewGuid().ToString("D"));
        }

        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage response = null!;

        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            var attemptRequest = attempt == 1 ? request : await CloneAsync(request, cancellationToken).ConfigureAwait(false);
            var ownsAttemptRequest = !ReferenceEquals(attemptRequest, request);

            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (ownsAttemptRequest)
                {
                    attemptRequest.Dispose();
                }

                throw;
            }

            if (attempt == _maximumAttempts || !ShouldRetry(request.Method, response.StatusCode))
            {
                return response;
            }

            var wait = ResolveRetryDelay(response, attempt, _nextJitterFraction());
            response.Dispose();

            if (ownsAttemptRequest)
            {
                attemptRequest.Dispose();
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var buffered = new MemoryStream();
            await request.Content.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            clone.Content = new StreamContent(buffered);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
