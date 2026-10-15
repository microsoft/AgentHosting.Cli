using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

internal sealed class ScriptedConsentTier : IConnectionConsentFlow
{
    public List<bool> OpenedBrowser { get; } = [];

    public List<string> Connectors { get; } = [];

    public Exception? Throws { get; init; }

    public ConnectionConsentResult Result { get; init; } = new(ConnectionConsentStatus.Created, "conn-1", "conn-1", "Office 365 Outlook", null);

    public Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken)
    {
        Connectors.Add(connectorId);
        OpenedBrowser.Add(openBrowser);

        return Throws is null ? Task.FromResult(Result) : Task.FromException<ConnectionConsentResult>(Throws);
    }
}

internal sealed class DisposableConsentTier : IConnectionConsentFlow, IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;

    public Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-1", "conn-1", null, null));
}

public sealed class ConnectionConsentLadderTests
{
    private static ConnectionConsentLadder Ladder(params IConnectionConsentFlow[] tiers) =>
        new(tiers);

    [Fact]
    public async Task Create_UsesTheFirstTierAndNeverReachesTheBrowserFallback()
    {
        var stageOne = new ScriptedConsentTier();
        var browser = new ScriptedConsentTier();
        using var ladder = Ladder(stageOne, browser);

        var result = await ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Single(stageOne.Connectors);
        Assert.Empty(browser.Connectors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_FallsBackToTheBrowserFlowInBothInteractiveAndNonInteractiveRuns(bool openBrowser)
    {
        var stageOne = new ScriptedConsentTier { Throws = new SilentConnectionUnavailableException("Connector 'shared_ftp' did not offer a consent link, so it cannot be signed in from a terminal.") };
        var browser = new ScriptedConsentTier();
        var presenter = new RecordingMessagePresenter();
        using var ladder = Ladder(stageOne, browser);

        var result = await ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_ftp", openBrowser, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal(openBrowser, Assert.Single(browser.OpenedBrowser));
        Assert.Equal(openBrowser, Assert.Single(stageOne.OpenedBrowser));
    }

    [Fact]
    public async Task Create_FallsBackWithoutNarratingIt()
    {
        var stageOne = new ScriptedConsentTier { Throws = new SilentConnectionUnavailableException("did not offer a consent link") };
        var presenter = new RecordingMessagePresenter();
        using var ladder = Ladder(stageOne, new ScriptedConsentTier());

        await ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "/providers/Microsoft.PowerApps/apis/shared_ftp", openBrowser: false, CancellationToken.None);

        Assert.Empty(presenter.Messages);
    }

    [Fact]
    public async Task Create_SurfacesTheLastTiersFailureRatherThanFallingOffTheEnd()
    {
        var failure = new SilentConnectionUnavailableException("nothing worked");
        using var ladder = Ladder(new ScriptedConsentTier { Throws = failure }, new ScriptedConsentTier { Throws = failure });

        var thrown = await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_ftp", openBrowser: false, CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Create_DoesNotFallBackWhenTheFirstTierReportsACancelledResult()
    {
        var stageOne = new ScriptedConsentTier { Result = new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, null) };
        var browser = new ScriptedConsentTier();
        using var ladder = Ladder(stageOne, browser);

        var result = await ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Empty(browser.Connectors);
    }

    [Fact]
    public async Task Create_DoesNotFallBackOnAnUnrelatedFailure()
    {
        var browser = new ScriptedConsentTier();
        using var ladder = Ladder(new ScriptedConsentTier { Throws = new InvalidOperationException("boom") }, browser);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None));

        Assert.Empty(browser.Connectors);
    }

    [Fact]
    public void Dispose_DisposesEveryTierThatHoldsATransport()
    {
        var disposable = new DisposableConsentTier();
        var ladder = Ladder(disposable, new ScriptedConsentTier());

        ladder.Dispose();

        Assert.True(disposable.Disposed);
    }

    [Fact]
    public void Construction_RejectsALadderWithNoTiers() =>
        Assert.Throws<ArgumentException>(() => new ConnectionConsentLadder([]));

    [Fact]
    public async Task Create_ComposesStageOneOverTheRealBrowserFlow()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(System.Net.HttpStatusCode.Forbidden, "denied"));
        var presenter = new RecordingMessagePresenter();
        var browserLauncher = new RecordingBrowserLauncher();
        var stageOne = new ConsentLinkConnectionConsentFlow(new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler), presenter, browserLauncher, CoreServicesClusterCategory.Prod, timeout: TimeSpan.FromMilliseconds(30), interval: TimeSpan.FromMilliseconds(1));
        var browserFlow = new ScriptedConsentTier();
        using var ladder = Ladder(stageOne, browserFlow);

        var result = await ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Single(browserFlow.Connectors);
        Assert.Empty(presenter.Messages);
    }

    [Fact]
    public async Task Create_NeverReachesTheBrowserFallbackForAZeroInputConnector()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Json(System.Net.HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        var presenter = new RecordingMessagePresenter();
        var stageOne = new ConsentLinkConnectionConsentFlow(new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler), presenter, new RecordingBrowserLauncher(), CoreServicesClusterCategory.Prod, timeout: TimeSpan.FromMilliseconds(30), interval: TimeSpan.FromMilliseconds(1));
        var browserFlow = new ScriptedConsentTier();
        using var ladder = Ladder(stageOne, browserFlow);

        var result = await ladder.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_rss", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Empty(browserFlow.Connectors);
        Assert.Empty(presenter.Messages);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
    }
}
