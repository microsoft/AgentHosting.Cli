using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>How a browser consent flow ended.</summary>
public enum ConnectionConsentStatus
{
    /// <summary>A connection was created.</summary>
    Created,

    /// <summary>The operator abandoned the flow, or it timed out.</summary>
    Cancelled,

    /// <summary>The consent experience reported a failure.</summary>
    Error,
}

/// <summary>What a browser consent flow produced.</summary>
/// <param name="Status">How the flow ended.</param>
/// <param name="ConnectionId">The connection that was created.</param>
/// <param name="ConnectionName">The connection's logical name.</param>
/// <param name="DisplayName">The identity the connection was created as.</param>
/// <param name="ErrorMessage">Why the flow did not produce a connection.</param>
public sealed record ConnectionConsentResult(ConnectionConsentStatus Status, string? ConnectionId, string? ConnectionName, string? DisplayName, string? ErrorMessage);

/// <summary>What a consent redirect reported: an authorization code, or the error it carried.</summary>
/// <param name="Code">The authorization code the consent redirect delivered, when it succeeded.</param>
/// <param name="Error">The error the consent redirect carried, when it failed.</param>
public sealed record ConsentCodeCallback(string? Code, string? Error);

/// <summary>
/// A one-shot loopback listener that receives the connection consent callback.
/// </summary>
public sealed class ConnectionConsentLoopback : IDisposable
{
    private const int MaximumRequestLineLength = 8192;

    private const int SessionIdCharacters = 32;

    private readonly TcpListener _listener;
    private readonly Uri? _successRedirect;
    private readonly string _callbackPath;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _serving;
    private bool _signedIn;
    private bool _answered;

    /// <summary>Starts listening on a free loopback port.</summary>
    /// <param name="successRedirect">
    /// Where to send the browser once a connection is created, instead of leaving it on a local
    /// page. Omitted when the browser should stay here.
    /// </param>
    public ConnectionConsentLoopback(Uri? successRedirect = null)
    {
        if (successRedirect is not null
            && (!successRedirect.IsAbsoluteUri || (successRedirect.Scheme != Uri.UriSchemeHttps && successRedirect.Scheme != Uri.UriSchemeHttp)))
        {
            throw new ArgumentException("The success redirect must be an absolute http or https URL.", nameof(successRedirect));
        }

        _successRedirect = successRedirect;
        _callbackPath = $"/callback/{RandomNumberGenerator.GetHexString(SessionIdCharacters, lowercase: true)}";
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>The port the OS assigned.</summary>
    public int Port { get; }

    /// <summary>The URL the consent experience reports back to.</summary>
    public Uri CallbackUrl => new($"http://127.0.0.1:{Port}{_callbackPath}");

    /// <summary>Whether a browser has already reached this session's callback.</summary>
    public bool AnsweredCallback => _answered;

    /// <summary>
    /// Keeps answering this session's callback after the terminal stopped waiting, so a browser
    /// that arrives late still lands where it was promised instead of on a refused connection.
    /// </summary>
    public void ServeLateCallbacks() => _serving ??= ServeSignedInAsync();

    /// <summary>Reads the nonce out of a consent link.</summary>
    /// <param name="consentLink">The link the consent experience was opened with.</param>
    /// <returns>The nonce, or <see langword="null"/> when the link carries none.</returns>
    public static string? ExtractNonce(Uri consentLink)
    {
        ArgumentNullException.ThrowIfNull(consentLink);

        return ParseQuery(consentLink.Query.TrimStart('?')).TryGetValue("nonce", out var nonce) ? nonce : null;
    }

    /// <summary>
    /// Waits for a callback carrying <paramref name="expectedNonce"/>, ignoring anything else.
    /// </summary>
    /// <param name="expectedNonce">The nonce this flow issued.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the consent experience reported.</returns>
    public async Task<ConnectionConsentResult> WaitAsync(string expectedNonce, TimeSpan timeout, CancellationToken cancellationToken) =>
        await ListenAsync(query => ParseCallback(query, expectedNonce), ConnectionPayload, timeout, cancellationToken).ConfigureAwait(false)
            ?? new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, $"Timed out after {timeout.TotalMinutes:0} minute(s) waiting for the connection to be created.");

    public async Task<ConsentCodeCallback?> WaitForConsentCodeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reported = await ListenAsync(ParseConsentCode, ConsentCodePayload, timeout, cancellationToken).ConfigureAwait(false);

        if (reported is not null)
        {
            _serving ??= ServeSignedInAsync();
        }

        return reported;
    }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.Stop();
    }

    private async Task ServeSignedInAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                using var stream = client.GetStream();

                await ReadRequestLineAsync(stream, _lifetime.Token).ConfigureAwait(false);
                await WriteAsync(stream, SignedInResponse, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private async Task<T?> ListenAsync<T>(Func<string, T?> interpret, Func<T?, string> respond, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(timeoutSource.Token).ConfigureAwait(false);
                using var stream = client.GetStream();

                var request = ParseRequestTarget(await ReadRequestLineAsync(stream, timeoutSource.Token).ConfigureAwait(false));
                var addressed = AddressesThisSession(request);
                var interpreted = addressed ? interpret(request.Query) : null;

                _answered |= addressed;

                await WriteAsync(stream, respond(interpreted), timeoutSource.Token).ConfigureAwait(false);

                if (interpreted is not null)
                {
                    return interpreted;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether a request is a browser navigation carrying this session's callback value.</summary>
    private bool AddressesThisSession(ConsentRequest request) =>
        string.Equals(request.Method, "GET", StringComparison.Ordinal)
        && string.Equals(request.Path, _callbackPath, StringComparison.Ordinal);

    /// <summary>
    /// Reads the request line, which a client is free to deliver in more than one packet.
    /// </summary>
    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var line = new StringBuilder();

        while (line.Length < MaximumRequestLineLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            line.Append(Encoding.ASCII.GetString(buffer, 0, read));

            var text = line.ToString();
            var newline = text.IndexOf('\n');

            if (newline >= 0)
            {
                return text[..newline].Trim();
            }
        }

        return line.ToString().Trim();
    }

    private static ConsentRequest ParseRequestTarget(string requestLine)
    {
        var parts = requestLine.Split(' ');

        if (parts.Length < 2)
        {
            return new ConsentRequest(string.Empty, string.Empty, string.Empty);
        }

        var target = parts[1];
        var question = target.IndexOf('?');

        return question >= 0 ? new ConsentRequest(parts[0], target[..question], target[(question + 1)..]) : new ConsentRequest(parts[0], target, string.Empty);
    }

    private readonly record struct ConsentRequest(string Method, string Path, string Query);

    private static async Task WriteAsync(NetworkStream stream, string payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ConnectionPayload(ConnectionConsentResult? result) =>
        result?.Status is ConnectionConsentStatus.Created && _successRedirect is not null
            ? RedirectTo(_successRedirect)
            : Respond(PageFor(result));

    private string ConsentCodePayload(ConsentCodeCallback? reported)
    {
        if (reported is { Code: not null })
        {
            _signedIn = true;
        }

        if (_successRedirect is not null)
        {
            return RedirectTo(_successRedirect);
        }

        if (reported is { Error: { } failure })
        {
            return Respond(Page("Sign-in failed", failure));
        }

        return Respond(_signedIn
            ? Page("Signed in", "You can close this tab and return to your terminal.")
            : Page("Signing in", "Your terminal is completing this sign-in and will report the result. You can close this tab."));
    }

    private string SignedInResponse =>
        _successRedirect is not null
            ? RedirectTo(_successRedirect)
            : Respond(Page("Signed in", "You can close this tab and return to your terminal."));

    private static ConsentCodeCallback? ParseConsentCode(string query)
    {
        var parameters = ParseQuery(query);

        if (parameters.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code))
        {
            return new ConsentCodeCallback(code, null);
        }

        if (!parameters.TryGetValue("error", out var error) || string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        parameters.TryGetValue("error_description", out var description);

        return new ConsentCodeCallback(null, string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}");
    }

    private static string Respond(string html) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}";

    private static string RedirectTo(Uri destination)
    {
        var html = Page("Connection created", "Returning you to the connections list.");

        return $"HTTP/1.1 302 Found\r\nLocation: {destination.AbsoluteUri}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}";
    }

    private static string PageFor(ConnectionConsentResult? result) => result?.Status switch
    {
        ConnectionConsentStatus.Created => Page("Connection created", "You can close this tab and return to your terminal."),
        ConnectionConsentStatus.Error => Page("Connection creation failed", result.ErrorMessage ?? "The consent experience reported a failure."),
        ConnectionConsentStatus.Cancelled => Page("Connection creation cancelled", "You can close this tab and return to your terminal."),
        _ => Page("Nothing to report", "This request did not match the connection your terminal is waiting for."),
    };

    private static string Page(string heading, string detail) =>
        $"<!doctype html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;padding:2rem\"><h2>{WebUtility.HtmlEncode(heading)}</h2><p>{WebUtility.HtmlEncode(detail)}</p></body></html>";

    private static ConnectionConsentResult? ParseCallback(string query, string expectedNonce)
    {
        var parameters = ParseQuery(query);

        if (!parameters.TryGetValue("nonce", out var nonce) || !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
        {
            return null;
        }

        parameters.TryGetValue("status", out var status);

        if (string.Equals(status, "created", StringComparison.OrdinalIgnoreCase))
        {
            parameters.TryGetValue("connectionId", out var connectionId);
            parameters.TryGetValue("connectionName", out var connectionName);
            parameters.TryGetValue("displayName", out var displayName);

            var normalized = string.IsNullOrWhiteSpace(connectionId) ? null : ConnectionConsentLink.NormalizeResourceId(connectionId);

            return new ConnectionConsentResult(ConnectionConsentStatus.Created, normalized, connectionName ?? normalized, displayName, null);
        }

        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            parameters.TryGetValue("message", out var message);

            return new ConnectionConsentResult(ConnectionConsentStatus.Error, null, null, null, message ?? "Connection creation failed.");
        }

        return new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, "Connection creation was cancelled.");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');

            if (equals > 0)
            {
                result[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return result;
    }
}
