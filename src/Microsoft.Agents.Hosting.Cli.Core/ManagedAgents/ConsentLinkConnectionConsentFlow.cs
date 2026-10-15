using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Creates a connection, shows its consent link, and watches the service until the operator has signed it in.</summary>
public sealed class ConsentLinkConnectionConsentFlow : IConnectionConsentFlow, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = ConnectionConsentLink.DefaultConsentTimeout;
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    private const int SettleIntervals = 5;

    private readonly PowerAppsConnectionClient _client;
    private readonly IInteractiveMessagePresenter _presenter;
    private readonly IBrowserLauncher _browser;
    private readonly CoreServicesClusterCategory _cluster;
    private readonly IOperatorInterrupt _interrupt;
    private readonly bool _canInterrupt;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _interval;

    /// <summary>Creates the flow.</summary>
    /// <param name="client">Sends the connection requests.</param>
    /// <param name="presenter">Shows the consent link on stderr.</param>
    /// <param name="browser">Opens the consent link when interaction is allowed.</param>
    /// <param name="cluster">The cluster category whose maker portal the browser is sent to when it finishes.</param>
    /// <param name="interrupt">Reports that the operator gave up waiting. Omitted when nothing can interrupt.</param>
    /// <param name="timeProvider">Supplies the clock and the delay, replaced in tests.</param>
    /// <param name="timeout">How long to wait for the operator. Defaults to <see cref="ConnectionConsentLink.DefaultConsentTimeout"/>.</param>
    /// <param name="interval">How long to wait between reads. Defaults to three seconds.</param>
    public ConsentLinkConnectionConsentFlow(PowerAppsConnectionClient client, IInteractiveMessagePresenter presenter, IBrowserLauncher browser, CoreServicesClusterCategory cluster, IOperatorInterrupt? interrupt = null, TimeProvider? timeProvider = null, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _cluster = cluster;
        _interrupt = interrupt ?? NeverInterrupts.Instance;
        _canInterrupt = interrupt is not null;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Disposes the transport shared by every request this flow sent.</summary>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc />
    public async Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var connector = ConnectionConsentLink.NormalizeResourceId(connectorId);
        var connectionsList = ConnectionConsentLink.BuildConnectionsListLink(_cluster, environment.EnvironmentId);

        using var loopback = new ConnectionConsentLoopback(connectionsList);

        var result = await ConnectionConsentSession.RunAsync(
            _client,
            environment,
            connector,
            null,
            loopback.CallbackUrl.ToString(),
            (context, token) => SignInAsync(environment, connector, context, loopback, openBrowser, token),
            cancellationToken).ConfigureAwait(false);

        if (result.Status is not ConnectionConsentStatus.Created)
        {
            await _presenter
                .PresentAsync(result.ErrorMessage ?? $"The unused {connector} connection was removed.", cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ConnectionSignInResult> SignInAsync(CopilotEnvironmentContext environment, string connector, ConnectionConsentContext context, ConnectionConsentLoopback loopback, bool openBrowser, CancellationToken cancellationToken)
    {
        if (context.ConsentLink is not { } consentLink)
        {
            throw new SilentConnectionUnavailableException($"Connector '{connector}' did not offer a consent link, so it cannot be signed in from a terminal.");
        }

        ConsentEndpointTrust.Require(consentLink);

        if (openBrowser)
        {
            LaunchQuietly(consentLink);
        }

        await _presenter.PresentAsync($"Sign in to {connector} here, or open this link on another device. {WaitNotice()}{Environment.NewLine}{consentLink}", cancellationToken).ConfigureAwait(false);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var waiting = ConnectionConsentSession.WaitUntilAuthenticatedAsync(_client, environment, connector, context.ConnectionName, _timeout, _interval, _timeProvider, race.Token);
        var redirected = loopback.WaitForConsentCodeAsync(_timeout, race.Token);
        var interrupted = _interrupt.WaitForInterruptAsync(race.Token);
        ConnectionSignInResult outcome;

        try
        {
            var settled = await Task.WhenAny(waiting, redirected, interrupted).ConfigureAwait(false);

            if (ReferenceEquals(settled, interrupted) && interrupted.IsCompletedSuccessfully && !waiting.IsCompleted && !redirected.IsCompleted)
            {
                return await CancelledAsync(connector, cancellationToken).ConfigureAwait(false);
            }

            string? reportedFailure = null;

            if (ReferenceEquals(settled, redirected)
                && redirected.IsCompletedSuccessfully
                && await redirected.ConfigureAwait(false) is { } reported)
            {
                reportedFailure = await RedeemAsync(environment, connector, context.ConnectionName, reported, cancellationToken).ConfigureAwait(false);
            }

            if (reportedFailure is not null && !await SettlesAsync(waiting, race.Token).ConfigureAwait(false))
            {
                return ConnectionSignInResult.Failed(reportedFailure);
            }

            outcome = await AwaitUnlessInterruptedAsync(waiting, interrupted, connector, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false);
            await QuiesceAsync(waiting, redirected, interrupted).ConfigureAwait(false);
        }

        if (outcome.Status is ConnectionSignInStatus.Completed)
        {
            await LandTheBrowserAsync(loopback, cancellationToken).ConfigureAwait(false);
        }

        if (outcome.Status is ConnectionSignInStatus.Abandoned)
        {
            await _presenter.PresentAsync($"No sign-in was completed for {connector} within {_timeout.TotalMinutes:0} minutes. If it was done on another device, try again in a browser on this machine.", cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task LandTheBrowserAsync(ConnectionConsentLoopback loopback, CancellationToken cancellationToken)
    {
        if (loopback.AnsweredCallback)
        {
            return;
        }

        loopback.ServeLateCallbacks();

        try
        {
            await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> RedeemAsync(CopilotEnvironmentContext environment, string connector, string connectionName, ConsentCodeCallback reported, CancellationToken cancellationToken)
    {
        if (reported.Code is not { } code)
        {
            return reported.Error is { } failure
                ? $"The consent experience reported '{failure}'."
                : $"The consent experience returned no authorization code for {connector}.";
        }

        try
        {
            var confirmed = await _client.ConfirmConsentCodeAsync(environment, connector, connectionName, code, cancellationToken).ConfigureAwait(false);

            return confirmed.IsSuccess
                ? null
                : confirmed.Correlated($"the service refused the consent code with HTTP {(int)confirmed.StatusCode}");
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is HttpRequestException or IOException or OperationCanceledException)
        {
            return $"The consent code for {connector} could not be confirmed: {failure.Message}";
        }
    }

    private async Task<ConnectionSignInResult> CancelledAsync(string connector, CancellationToken cancellationToken)
    {
        await _presenter.PresentAsync($"Waiting for {connector} was cancelled.", cancellationToken).ConfigureAwait(false);

        return ConnectionSignInResult.Abandoned;
    }

    private async Task<ConnectionSignInResult> AwaitUnlessInterruptedAsync(Task<ConnectionSignInResult> waiting, Task interrupted, string connector, CancellationToken cancellationToken) =>
        ReferenceEquals(await Task.WhenAny(waiting, interrupted).ConfigureAwait(false), interrupted) && interrupted.IsCompletedSuccessfully && !waiting.IsCompleted
            ? await CancelledAsync(connector, cancellationToken).ConfigureAwait(false)
            : await waiting.ConfigureAwait(false);

    private async Task<bool> SettlesAsync(Task<ConnectionSignInResult> waiting, CancellationToken cancellationToken)
    {
        var grace = Task.Delay(TimeSpan.FromTicks(_interval.Ticks * SettleIntervals), _timeProvider, cancellationToken);

        return ReferenceEquals(await Task.WhenAny(waiting, grace).ConfigureAwait(false), waiting);
    }

    private string WaitNotice() => ConnectionConsentLink.WaitNotice(_timeout, _canInterrupt);

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
            }
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
}
