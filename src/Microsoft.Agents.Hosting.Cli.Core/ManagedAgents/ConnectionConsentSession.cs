using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>How far a connection got towards being signed in.</summary>
public enum ConnectionSignInStatus
{
    /// <summary>The connection is signed in and usable.</summary>
    Completed,

    /// <summary>The wait ended with nobody signing the connection in.</summary>
    Abandoned,

    /// <summary>The service reported something no further waiting can resolve.</summary>
    Failed,
}

/// <summary>What a sign-in step settled on.</summary>
/// <param name="Status">How far the connection got.</param>
/// <param name="Detail">What the service reported, when it reported something.</param>
public sealed record ConnectionSignInResult(ConnectionSignInStatus Status, string? Detail = null)
{
    /// <summary>The connection is signed in.</summary>
    public static ConnectionSignInResult Completed { get; } = new(ConnectionSignInStatus.Completed);

    /// <summary>Nobody signed the connection in before the wait ended.</summary>
    public static ConnectionSignInResult Abandoned { get; } = new(ConnectionSignInStatus.Abandoned);

    /// <summary>The service reported a failure that waiting longer cannot resolve.</summary>
    /// <param name="detail">What the service reported.</param>
    /// <returns>The settled failure.</returns>
    public static ConnectionSignInResult Failed(string detail) => new(ConnectionSignInStatus.Failed, detail);
}

/// <summary>The connection a consent session created, and the link that can authenticate it.</summary>
/// <param name="ConnectionName">The name the connection was created under.</param>
/// <param name="DisplayName">The identity the connection presents.</param>
/// <param name="ConsentLink">The link that signs the connection in through a browser.</param>
public sealed record ConnectionConsentContext(string ConnectionName, string DisplayName, Uri? ConsentLink);

/// <summary>Creates a connection, obtains its consent links, and removes it again when authentication does not complete.</summary>
public static class ConnectionConsentSession
{
    /// <summary>Creates a connection and signs it in through the caller's step, unless the service reports it usable as created.</summary>
    /// <param name="client">Sends the connection requests.</param>
    /// <param name="environment">The environment the connection is created in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="parameterSetName">The authentication parameter set to create under, when the connector declares sets.</param>
    /// <param name="redirectUrl">The page the browser lands on once consent completes.</param>
    /// <param name="authenticateAsync">Signs the connection in, reporting how far it got.</param>
    /// <param name="cancellationToken">Cancels the session.</param>
    /// <returns>What the session produced.</returns>
    /// <exception cref="ManagedAgentException">The sign-in failed and the connection it created could not be removed.</exception>
    public static async Task<ConnectionConsentResult> RunAsync(PowerAppsConnectionClient client, CopilotEnvironmentContext environment, string connectorId, string? parameterSetName, string redirectUrl, Func<ConnectionConsentContext, CancellationToken, Task<ConnectionSignInResult>> authenticateAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(authenticateAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var connector = ConnectionConsentLink.NormalizeResourceId(connectorId);
        var connectionName = Guid.NewGuid().ToString("N");
        PowerAppsConnectionResponse created;

        try
        {
            created = await client.CreateConnectionAsync(environment, connector, connectionName, parameterSetName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause)
        {
            throw await OrphanedAsync(client, environment, connector, connectionName, cause).ConfigureAwait(false) ?? cause;
        }

        if (!created.IsSuccess)
        {
            var refused = Unavailable($"creating a connection for connector '{connector}'", created);

            throw ProvesNotCreated(created.StatusCode)
                ? refused
                : await OrphanedAsync(client, environment, connector, connectionName, refused).ConfigureAwait(false) ?? (Exception)refused;
        }

        using var createdDocument = PowerAppsConnectionResource.Parse(created.Body);
        var displayName = createdDocument is null ? connector : PowerAppsConnectionResource.DisplayNameOf(createdDocument.RootElement) ?? connector;

        if (createdDocument is not null && PowerAppsConnectionResource.ReportsReady(createdDocument.RootElement))
        {
            return Created(connectionName, displayName);
        }

        ConnectionSignInResult signIn;

        try
        {
            var consent = await client.GetConsentLinkAsync(environment, connector, connectionName, redirectUrl, cancellationToken).ConfigureAwait(false);
            var context = new ConnectionConsentContext(connectionName, displayName, ConsentLinkOf(consent));

            signIn = await authenticateAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause)
        {
            if (await OrphanedAsync(client, environment, connector, connectionName, cause).ConfigureAwait(false) is { } orphan)
            {
                throw orphan;
            }

            throw;
        }

        return signIn.Status is ConnectionSignInStatus.Completed
            ? Created(connectionName, displayName)
            : Unfinished(connectionName, environment.EnvironmentId, connector, signIn, await RemoveAsync(client, environment, connector, connectionName).ConfigureAwait(false));
    }

    /// <summary>Polls a connection until the service proves it signed in, or reports a failure waiting cannot resolve.</summary>
    /// <param name="client">Sends the connection requests.</param>
    /// <param name="environment">The environment the connection lives in, and the tenant its token is issued for.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="connectionName">The connection to watch.</param>
    /// <param name="timeout">How long to keep watching.</param>
    /// <param name="interval">How long to wait between reads.</param>
    /// <param name="timeProvider">Supplies the clock and the delay, replaced in tests.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How far the connection got.</returns>
    public static async Task<ConnectionSignInResult> WaitUntilAuthenticatedAsync(PowerAppsConnectionClient client, CopilotEnvironmentContext environment, string connectorId, string connectionName, TimeSpan timeout, TimeSpan interval, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var deadline = timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            var remaining = deadline - timeProvider.GetUtcNow();

            if (remaining <= TimeSpan.Zero)
            {
                return ConnectionSignInResult.Abandoned;
            }

            if (await ReadAsync(client, environment, connectorId, connectionName, remaining, timeProvider, cancellationToken).ConfigureAwait(false) is { } settled)
            {
                return settled;
            }

            if (timeProvider.GetUtcNow() + interval >= deadline)
            {
                return ConnectionSignInResult.Abandoned;
            }

            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What one read settles, or <see langword="null"/> when it only settles that the wait should go
    /// on. A transport that failed to answer has reported nothing about the connection, so it leaves
    /// the wait running rather than tearing down a connection the operator may still be signing in.
    /// </summary>
    private static async Task<ConnectionSignInResult?> ReadAsync(PowerAppsConnectionClient client, CopilotEnvironmentContext environment, string connectorId, string connectionName, TimeSpan remaining, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var expiry = new CancellationTokenSource(remaining, timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);

        try
        {
            return Settle(await client.ReadConnectionAsync(environment, connectorId, connectionName, bounded.Token).ConfigureAwait(false));
        }
        catch (Exception failure) when (IsTransientTransport(failure, cancellationToken))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a failed request is the transport rather than an answer, and the caller still wants
    /// the wait to run. A cancellation the caller asked for is never transient.
    /// </summary>
    private static bool IsTransientTransport(Exception failure, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && failure is HttpRequestException or IOException or OperationCanceledException;

    /// <summary>
    /// What one read settles, or <see langword="null"/> when it only settles that the wait should go
    /// on. Waiting out a failure the service has already decided costs the operator the whole
    /// timeout and then reports it as nobody having signed in, which is not what happened.
    /// </summary>
    private static ConnectionSignInResult? Settle(PowerAppsConnectionResponse response)
    {
        if (!response.IsSuccess)
        {
            return Transient(response.StatusCode)
                ? null
                : ConnectionSignInResult.Failed(response.Correlated($"the service answered HTTP {(int)response.StatusCode}"));
        }

        if (response.Oversized)
        {
            return ConnectionSignInResult.Failed(response.Correlated("the service answered with a response too large to read"));
        }

        using var document = PowerAppsConnectionResource.Parse(response.Body);

        if (document is null)
        {
            return null;
        }

        if (PowerAppsConnectionResource.ReportsReady(document.RootElement))
        {
            return ConnectionSignInResult.Completed;
        }

        return PowerAppsConnectionResource.ReportsUnauthenticated(document.RootElement)
            || PowerAppsConnectionResource.ErrorCodeOf(document.RootElement) is not { } failure
            ? null
            : ConnectionSignInResult.Failed(response.Correlated($"the service reported '{failure}'"));
    }

    /// <summary>Whether a status leaves room for the next read to answer differently.</summary>
    private static bool Transient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Whether a refused create proves the service holds no connection under the name that was sent.
    /// A request the service rejected outright created nothing; one it never answered for, or
    /// answered for out of its own failure, may still have been committed.
    /// </summary>
    private static bool ProvesNotCreated(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && !Transient(status);

    private static Uri? ConsentLinkOf(PowerAppsConnectionResponse consent)
    {
        if (!consent.IsSuccess || PowerAppsConnectionResource.Parse(consent.Body) is not { } document)
        {
            return null;
        }

        using (document)
        {
            return PowerAppsConnectionResource.ConsentLinkOf(document.RootElement);
        }
    }

    private static ConnectionConsentResult Created(string connectionName, string displayName) =>
        new(ConnectionConsentStatus.Created, connectionName, connectionName, displayName, null);

    /// <summary>
    /// Reports a session that produced no usable connection, naming an orphan only when one was left
    /// behind. Claiming a removal the service refused would leave a usable connection nobody knows
    /// to clean up.
    /// </summary>
    private static ConnectionConsentResult Unfinished(string connectionName, string environmentId, string connectorId, ConnectionSignInResult signIn, string? removalFailure) =>
        new(
            signIn.Status is ConnectionSignInStatus.Failed ? ConnectionConsentStatus.Error : ConnectionConsentStatus.Cancelled,
            null,
            null,
            null,
            Explain(signIn.Detail, removalFailure is null ? null : Orphan(connectionName, environmentId, connectorId, removalFailure)));

    private static string? Explain(string? detail, string? orphan) =>
        string.Join(' ', new[] { detail, orphan }.Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } explanation
            ? explanation
            : null;

    private static string Orphan(string connectionName, string environmentId, string connectorId, string removalFailure) =>
        $"The unused connection {connectionName} could not be removed: {removalFailure} Delete it with `{ConnectionCommandHints.DeleteConnection(environmentId, connectorId, connectionName)}`.";

    private const string TransportFailure = "the request failed before the service answered.";

    private static SilentConnectionUnavailableException Unavailable(string operation, PowerAppsConnectionResponse response) =>
        new(response.Correlated($"The Power Apps API failed while {operation}: HTTP {(int)response.StatusCode}"));

    /// <summary>
    /// Deletes a connection, reporting why it survived when it did. A cancellation raised here is the
    /// transport giving up rather than the caller asking to stop, because the delete is deliberately
    /// not cancellable: the connection has to be cleaned up whatever ended the session.
    /// </summary>
    /// <returns><see langword="null"/> once the connection is gone, and otherwise what stopped it being removed.</returns>
    private static async Task<string?> RemoveAsync(PowerAppsConnectionClient client, CopilotEnvironmentContext environment, string connectorId, string connectionName)
    {
        try
        {
            var removal = await client.DeleteConnectionAsync(environment, connectorId, connectionName, CancellationToken.None).ConfigureAwait(false);

            return PowerAppsConnectionRemoval.ProvesAbsent(removal)
                ? null
                : removal.Correlated($"HTTP {(int)removal.StatusCode}");
        }
        catch (Exception)
        {
            return TransportFailure;
        }
    }

    private static string Because(Exception cause) =>
        cause is SilentConnectionUnavailableException or AgentHostingException ? cause.Message : TransportFailure;

    /// <summary>
    /// Removes the connection a failed session created, and names it when it survives.
    ///
    /// A caller that is told only why the session failed will try the next way of creating a
    /// connection, so a surviving one has to stop being a silent failure or it is left behind with
    /// nobody aware of it.
    /// </summary>
    /// <returns>The failure to raise instead, or <see langword="null"/> when nothing was left behind.</returns>
    private static async Task<ManagedAgentException?> OrphanedAsync(PowerAppsConnectionClient client, CopilotEnvironmentContext environment, string connectorId, string connectionName, Exception cause) =>
        await RemoveAsync(client, environment, connectorId, connectionName).ConfigureAwait(false) is { } orphaned
            ? ManagedAgentException.ConnectionNotRemoved(
                connectionName,
                environment.EnvironmentId,
                orphaned,
                Because(cause),
                ConnectionCommandHints.DeleteConnection(environment.EnvironmentId, connectorId, connectionName),
                cause)
            : null;
}
