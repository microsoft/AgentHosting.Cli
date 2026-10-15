using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Creates a connection by opening the consent experience in a browser.
/// </summary>
public sealed class BrowserConnectionConsentFlow : IConnectionConsentFlow
{
    private static readonly TimeSpan DefaultTimeout = ConnectionConsentLink.DefaultConsentTimeout;
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCallbackGrace = TimeSpan.FromSeconds(5);

    private readonly IBrowserLauncher _browser;
    private readonly IInteractiveMessagePresenter _presenter;
    private readonly IConnectorCatalogPort _connectors;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly CoreServicesClusterCategory _cluster;
    private readonly IOperatorInterrupt _interrupt;
    private readonly bool _canInterrupt;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _callbackGrace;
    private readonly TimeProvider _timeProvider;
    private readonly IConfirmationPrompt? _confirmations;

    /// <summary>Creates the flow.</summary>
    /// <param name="browser">Opens the consent link.</param>
    /// <param name="presenter">Explains the wait on stderr.</param>
    /// <param name="connectors">Reads the connections an environment holds, so a connection left behind can still be named.</param>
    /// <param name="principals">Names the signed-in operator, so a connection read from the service can be told from somebody else's.</param>
    /// <param name="cluster">The cluster category the environment belongs to.</param>
    /// <param name="interrupt">Reports that the operator gave up waiting. Omitted when nothing can interrupt.</param>
    /// <param name="timeout">How long to wait for one connection. Defaults to <see cref="ConnectionConsentLink.DefaultConsentTimeout"/>.</param>
    /// <param name="interval">How long to wait between reads of the environment's connections. Defaults to five seconds.</param>
    /// <param name="callbackGrace">How long a sighted connection waits for the browser to report one of its own. Defaults to five seconds.</param>
    /// <param name="timeProvider">Supplies the clock and the delay, replaced in tests.</param>
    /// <param name="confirmations">Asks the operator to accept a connection the browser never reported. Omitted when nothing can ask, which refuses it instead.</param>
    public BrowserConnectionConsentFlow(
        IBrowserLauncher browser,
        IInteractiveMessagePresenter presenter,
        IConnectorCatalogPort connectors,
        ManagedAgentPrincipalResolver principals,
        CoreServicesClusterCategory cluster,
        IOperatorInterrupt? interrupt = null,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        TimeSpan? callbackGrace = null,
        TimeProvider? timeProvider = null,
        IConfirmationPrompt? confirmations = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _cluster = cluster;
        _interrupt = interrupt ?? NeverInterrupts.Instance;
        _canInterrupt = interrupt is not null;
        _timeout = timeout ?? DefaultTimeout;
        _interval = interval ?? DefaultInterval;
        _callbackGrace = callbackGrace ?? DefaultCallbackGrace;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _confirmations = confirmations;
    }

    /// <inheritdoc />
    public async Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        using var loopback = new ConnectionConsentLoopback(ConnectionConsentLink.BuildConnectionsListLink(_cluster, environment.EnvironmentId));

        var nonce = Guid.NewGuid().ToString("N");
        var connector = ConnectionConsentLink.NormalizeResourceId(connectorId);
        var consentLink = ConnectionConsentLink.Build(_cluster, environment.EnvironmentId, connectorId, loopback.CallbackUrl, nonce);

        var known = await KnownConnectionsAsync(environment, connector, cancellationToken).ConfigureAwait(false);
        var principal = await PrincipalAsync(cancellationToken).ConfigureAwait(false);
        var scope = known is null ? null : new DiscoveryScope(environment, connector, known, principal);

        if (openBrowser)
        {
            LaunchQuietly(consentLink);
        }

        await _presenter.PresentAsync($"Create a {connector} connection {Where(scope, openBrowser)}. {ConnectionConsentLink.WaitNotice(_timeout, _canInterrupt)}{Environment.NewLine}{consentLink}", cancellationToken).ConfigureAwait(false);

        return await AwaitOutcomeAsync(loopback, nonce, scope, openBrowser, cancellationToken).ConfigureAwait(false);
    }

    private static string Where(DiscoveryScope? scope, bool mayPrompt) =>
        scope?.Principal is null || !mayPrompt ? "here, in a browser on this device" : "here, on this or any other device";

    /// <summary>The signed-in operator, or <see langword="null"/> when nobody is or the profile cannot be read.</summary>
    private async Task<string?> PrincipalAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _principals.ResolveUsernameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    private void LaunchQuietly(Uri consentLink)
    {
        try
        {
            _browser.Launch(consentLink);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
        }
    }

    private async Task<ConnectionConsentResult> AwaitOutcomeAsync(
        ConnectionConsentLoopback loopback,
        string nonce,
        DiscoveryScope? scope,
        bool mayPrompt,
        CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var callback = loopback.WaitAsync(nonce, _timeout, race.Token);
        var interrupted = _interrupt.WaitForInterruptAsync(race.Token);
        var sighting = WatchAsync(scope, race.Token);
        ConnectionConsentResult? reported = null;
        CopilotConnectionCandidate? sighted = null;

        try
        {
            await Task.WhenAny((Task)callback, interrupted, sighting).ConfigureAwait(false);

            if (!callback.IsCompleted && sighting.IsCompletedSuccessfully)
            {
                await YieldToCallbackAsync(callback, race.Token).ConfigureAwait(false);
            }

            if (callback.IsCompletedSuccessfully)
            {
                reported = await callback.ConfigureAwait(false);

                if (reported.Status is ConnectionConsentStatus.Created or ConnectionConsentStatus.Error)
                {
                    return reported;
                }
            }

            if (sighting.IsCompletedSuccessfully)
            {
                sighted = await sighting.ConfigureAwait(false);
            }
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false);
            await QuiesceAsync(callback, interrupted, sighting).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (sighted is { } candidate && await AcceptsAsync(candidate, mayPrompt, cancellationToken).ConfigureAwait(false))
        {
            return Sighted(candidate);
        }

        return scope is null
            ? reported ?? Abandoned()
            : await ReconcileAsync(scope, reported, cancellationToken).ConfigureAwait(false);
    }

    private const string AcceptResponse = "yes";

    private async Task<bool> AcceptsAsync(CopilotConnectionCandidate candidate, bool mayPrompt, CancellationToken cancellationToken)
    {
        if (!mayPrompt || _confirmations is null)
        {
            return false;
        }

        var name = ConnectionConsentLink.NormalizeResourceId(candidate.ConnectionId);

        var outcome = await _confirmations.ConfirmAsync(
            $"Connection {name} ({candidate.DisplayName}) appeared while waiting. Accept it only if you created it for this request.",
            AcceptResponse,
            cancellationToken).ConfigureAwait(false);

        return outcome is ConfirmationOutcome.Confirmed;
    }

    private async Task YieldToCallbackAsync(Task callback, CancellationToken cancellationToken)
    {
        try
        {
            await callback.WaitAsync(_callbackGrace, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
        {
        }
    }

    private async Task<CopilotConnectionCandidate?> WatchAsync(DiscoveryScope? scope, CancellationToken cancellationToken)
    {
        if (scope is null || scope.Principal is null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, cancellationToken).ConfigureAwait(false);

            return null;
        }

        while (true)
        {
            await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);

            if (Unambiguous(await DiscoverAsync(scope, cancellationToken).ConfigureAwait(false), scope) is { } sighted)
            {
                return sighted;
            }
        }
    }

    private static CopilotConnectionCandidate? Unambiguous(List<CopilotConnectionCandidate> appeared, DiscoveryScope scope) =>
        Owned(appeared, scope) is [var only] && ConnectionReadinessPlanner.IsConnected(only) ? only : null;

    private static ConnectionConsentResult Sighted(CopilotConnectionCandidate candidate) =>
        new(
            ConnectionConsentStatus.Created,
            ConnectionConsentLink.NormalizeResourceId(candidate.ConnectionId),
            ConnectionConsentLink.NormalizeResourceId(candidate.ConnectionId),
            candidate.DisplayName,
            null);

    /// <summary>
    /// Every connection that appeared while this flow waited, or nothing when the service shows none
    /// or cannot be read right now.
    ///
    /// One that is not connected is reported too. It cannot satisfy a reference, but it is the half
    /// made connection an abandoned sign-in leaves behind, and an operator who is not told its name
    /// has no way to find it.
    /// </summary>
    private async Task<List<CopilotConnectionCandidate>> DiscoverAsync(
        DiscoveryScope scope,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CopilotConnectionCandidate> current;

        try
        {
            current = await _connectors.ListConnectionsAsync(scope.Environment, scope.Connector, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return [];
        }

        return [.. current.Where(candidate => !scope.Known.Contains(candidate.ConnectionId))];
    }

    /// <summary>The candidates the catalog records as the signed-in operator's own.</summary>
    private static List<CopilotConnectionCandidate> Owned(List<CopilotConnectionCandidate> appeared, DiscoveryScope scope) =>
        [.. appeared.Where(candidate => ConnectionReadinessPlanner.IsOwnedBy(candidate, scope.Principal))];

    /// <summary>
    /// Asks what appeared after the wait ends. Without this, a connection created just as the flow
    /// gave up is reported as a plain cancellation and left orphaned in the environment.
    /// </summary>
    private async Task<ConnectionConsentResult> ReconcileAsync(
        DiscoveryScope scope,
        ConnectionConsentResult? reported,
        CancellationToken cancellationToken)
    {
        if (await DiscoverAsync(scope, cancellationToken).ConfigureAwait(false) is { Count: > 0 } appeared)
        {
            return Unverified(scope, Owned(appeared, scope) is { Count: > 0 } owned ? owned : appeared);
        }

        return reported ?? new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, $"Timed out after {_timeout.TotalMinutes:0} minute(s) waiting for the connection to be created.");
    }

    private static ConnectionConsentResult Abandoned() =>
        new(ConnectionConsentStatus.Cancelled, null, null, null, "Waiting was stopped before a connection was reported.");

    /// <summary>
    /// The connections that already exist, or <see langword="null"/> when the catalog cannot be read
    /// and a new connection could not be told from an existing one.
    /// </summary>
    private async Task<IReadOnlySet<string>?> KnownConnectionsAsync(CopilotEnvironmentContext environment, string connector, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _connectors.ListConnectionsAsync(environment, connector, cancellationToken).ConfigureAwait(false);

            return existing.Select(candidate => candidate.ConnectionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names what appeared rather than guessing which one this request produced.
    ///
    /// Only the browser reports the connection it created. Without that report, a connection the
    /// service lists is not tied to this request by anything: a second window of this operator's own,
    /// or a colleague working on the same connector, produces one that is indistinguishable from it.
    /// A connection runs as the identity that created it, so binding the wrong one silently decides
    /// which identity the agent runs as — worse than asking the operator to name it.
    /// </summary>
    private static ConnectionConsentResult Unverified(DiscoveryScope scope, List<CopilotConnectionCandidate> candidates) =>
        new(
            ConnectionConsentStatus.Error,
            null,
            null,
            null,
            $"{candidates.Count} new {scope.Connector} connection(s) appeared while waiting, but the browser did not report which one this request created: {Names(candidates)}. Nothing was removed. Bind one with `{ConnectionCommandHints.BindConnector(scope.Connector, "<id>")}`.");

    private static string Names(List<CopilotConnectionCandidate> candidates) =>
        string.Join(", ", candidates.Select(candidate => ConnectionConsentLink.NormalizeResourceId(candidate.ConnectionId)));

    /// <summary>Lets the losers of the race finish, so neither faults in the background.</summary>
    private static async Task QuiesceAsync(params Task[] abandoned)
    {
        foreach (var task in abandoned)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The outcome is already settled; a cancelled or failed straggler must not replace it.
            }
        }
    }

    /// <summary>What one flow watches for: the connections it did not create, and who it may accept one from.</summary>
    private sealed record DiscoveryScope(CopilotEnvironmentContext Environment, string Connector, IReadOnlySet<string> Known, string? Principal);
}
