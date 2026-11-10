using Microsoft.Agents.Hosting.Cli.Core.Environments;
using System.Net.Sockets;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionConsentLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static Task SendAsync(Uri callbackUrl, string query) => ConnectionConsentCallback.SendAsync(callbackUrl, query);

    private static async Task SendInPiecesAsync(Uri callbackUrl, params string[] pieces)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", callbackUrl.Port);

        await using var stream = client.GetStream();

        foreach (var piece in pieces)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(piece));
            await stream.FlushAsync();
            await Task.Delay(25);
        }

        try
        {
            await ConnectionConsentCallback.DrainAsync(stream);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void CallbackUrl_IsOnLoopbackWithTheAssignedPort()
    {
        using var loopback = new ConnectionConsentLoopback();

        Assert.Equal("127.0.0.1", loopback.CallbackUrl.Host);
        Assert.StartsWith("/callback/", loopback.CallbackUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(loopback.Port, loopback.CallbackUrl.Port);
        Assert.NotEqual(0, loopback.Port);
    }

    [Fact]
    public void ExtractNonce_ReadsTheNonceFromAConsentLink()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Prod, "env-1", "shared_sql", new Uri("http://127.0.0.1:5000/callback"), "nonce-1");

        Assert.Equal("nonce-1", ConnectionConsentLoopback.ExtractNonce(link));
    }

    [Fact]
    public void ExtractNonce_WithoutANonce_ReturnsNull()
    {
        var link = ConnectionConsentLink.Build(CoreServicesClusterCategory.Prod, "env-1", "shared_sql", null, null);

        Assert.Null(ConnectionConsentLoopback.ExtractNonce(link));
    }

    [Fact]
    public async Task Wait_WithAMatchingNonce_ReturnsTheCreatedConnection()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendAsync(loopback.CallbackUrl, "nonce=nonce-1&status=created&connectionId=conn-1&displayName=user%40contoso.com");

        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("conn-1", result.ConnectionId);
        Assert.Equal("user@contoso.com", result.DisplayName);
    }

    [Fact]
    public async Task Wait_WithAProviderPathConnectionId_ReturnsTheBareConnectionId()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendAsync(
            loopback.CallbackUrl,
            "nonce=nonce-1&status=created&connectionId=%2Fproviders%2FMicrosoft.PowerApps%2Fapis%2Fshared_teams%2Fconnections%2F1b7e5cddf05642f2846898f5cc5223a0&displayName=user%40contoso.com");

        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("1b7e5cddf05642f2846898f5cc5223a0", result.ConnectionId);
        Assert.Equal("1b7e5cddf05642f2846898f5cc5223a0", result.ConnectionName);
    }

    [Fact]
    public async Task Wait_WithARequestLineSplitAcrossPackets_StillReadsIt()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendInPiecesAsync(
            loopback.CallbackUrl,
            $"GET {loopback.CallbackUrl.AbsolutePath}?nonce=nonce-1&status=cre",
            "ated&connectionId=conn-split HTTP/1.1\r\n",
            "Host: 127.0.0.1\r\n\r\n");

        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("conn-split", result.ConnectionId);
    }

    [Fact]
    public async Task Wait_IgnoresAMismatchedNonceAndKeepsWaiting()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendAsync(loopback.CallbackUrl, "nonce=someone-else&status=created&connectionId=stale");
        await SendAsync(loopback.CallbackUrl, "nonce=nonce-1&status=created&connectionId=conn-1");

        var result = await waiting;

        Assert.Equal("conn-1", result.ConnectionId);
    }

    [Fact]
    public async Task Wait_WithAnErrorStatus_ReportsTheMessage()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendAsync(loopback.CallbackUrl, "nonce=nonce-1&status=error&message=consent%20denied");

        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Equal("consent denied", result.ErrorMessage);
        Assert.Null(result.ConnectionId);
    }

    [Fact]
    public async Task Wait_WithAnUnknownStatus_IsCancelled()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendAsync(loopback.CallbackUrl, "nonce=nonce-1&status=abandoned");

        Assert.Equal(ConnectionConsentStatus.Cancelled, (await waiting).Status);
    }

    [Fact]
    public async Task Wait_WhenNothingArrives_TimesOutWithoutThrowing()
    {
        using var loopback = new ConnectionConsentLoopback();

        var result = await loopback.WaitAsync("nonce-1", TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Contains("Timed out", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_WhenTheCallerCancels_Throws()
    {
        using var loopback = new ConnectionConsentLoopback();
        using var cancellation = new CancellationTokenSource();

        var waiting = loopback.WaitAsync("nonce-1", Timeout, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}

public sealed class BrowserConnectionConsentFlowTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);

    /// <summary>The account <see cref="ManagedAgentFixtures.Principals"/> reports as signed in.</summary>
    private const string Operator = "op@contoso.com";

    private static readonly CopilotEnvironmentContext Target = new()
    {
        EnvironmentId = "env-1",
        TenantId = Guid.Empty.ToString(),
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static CopilotConnectionCandidate Candidate(string id, string status = "Connected", string? owner = Operator) =>
        new() { ConnectionId = id, DisplayName = $"{id}-display", Status = status, Owner = owner };

    /// <summary>Answers the flow's loopback the way the consent page does, naming the connection it created.</summary>
    private static async Task ReportAsync(RecordingBrowserLauncher browser, string connectionId)
    {
        var link = await browser.FirstLaunch.WaitAsync(TimeSpan.FromSeconds(10));
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);

        await ConnectionConsentCallback.SendAsync(
            new Uri(query["callbackUrl"]!),
            $"nonce={query["nonce"]}&status=created&connectionId={connectionId}&displayName=op%40contoso.com");
    }

    [Fact]
    public async Task CreateConnection_WhenNothingTiesAConnectionToTheRequest_NamesTheCommandThatBindsIt()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-one"), Candidate("conn-two")]);

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10))
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Contains("msagent deployment connection update", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("--deployment-name <name>", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("--connector shared_sql", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("--connection-id", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("--connection-only", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("connection add", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenNonInteractive_NeverPromptsForAnUncorrelatedConnection()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10), confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("--connection-id", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenNonInteractive_DoesNotOfferAnotherDevice()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(new RecordingBrowserLauncher(), presenter, Brief, confirmations: new ScriptedConfirmationPrompt())
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: false, CancellationToken.None);

        Assert.Contains("in a browser on this device", presenter.Messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("any other device", presenter.Messages[0], StringComparison.Ordinal);
    }

    private static BrowserConnectionConsentFlow Create(
        RecordingBrowserLauncher browser,
        RecordingMessagePresenter presenter,
        TimeSpan? timeout = null,
        IConnectorCatalogPort? connectors = null,
        IOperatorInterrupt? interrupt = null,
        ManagedAgentPrincipalResolver? principals = null,
        TimeSpan? interval = null,
        TimeSpan? callbackGrace = null,
        IConfirmationPrompt? confirmations = null) =>
        new(
            browser,
            presenter,
            connectors ?? new CallbackCatalog(_ => []),
            ManagedAgentFixtures.Rules,
            principals ?? ManagedAgentFixtures.Principals(),
            CoreServicesClusterCategory.Prod,
            interrupt,
            timeout ?? Brief,
            interval,
            callbackGrace,
            confirmations: confirmations);

    private static BrowserConnectionConsentFlow Watching(
        RecordingBrowserLauncher browser,
        RecordingMessagePresenter presenter,
        IConnectorCatalogPort connectors,
        ManagedAgentPrincipalResolver? principals = null,
        IConfirmationPrompt? confirmations = null) =>
        Create(
            browser,
            presenter,
            TimeSpan.FromSeconds(30),
            connectors,
            principals: principals,
            interval: TimeSpan.FromMilliseconds(10),
            callbackGrace: TimeSpan.FromMilliseconds(20),
            confirmations: confirmations);

    [Fact]
    public async Task CreateConnection_WhenConsentFinishesOnAnotherDevice_AdoptsTheOneNewConnectionTheOperatorAccepts()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Watching(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), catalog, confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("conn-elsewhere", result.ConnectionId);
        Assert.Equal(1, prompt.Calls);
        Assert.Contains("appeared while waiting", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Contains("Accept it only if you created it for this request", prompt.LastDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("never reported creating it", prompt.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_AsksForAShortAnswerRatherThanTheConnectionName()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);
        var prompt = new ScriptedConfirmationPrompt();

        await Watching(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), catalog, confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("yes", prompt.LastExpected);
        Assert.Contains("conn-elsewhere", prompt.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenConsentFinishesOnAnotherDevice_DoesNotWaitOutTheWholeTimeout()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);
        var started = DateTimeOffset.UtcNow;

        await Watching(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), catalog, confirmations: new ScriptedConfirmationPrompt())
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CreateConnection_WhenTheOperatorDeclinesTheUncorrelatedConnection_DoesNotBindIt()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);
        var prompt = new ScriptedConfirmationPrompt { Answer = false };

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10), confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Equal(1, prompt.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhenTheOperatorMistypesTheUncorrelatedConnection_DoesNotBindIt()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);
        var prompt = new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Mismatched };

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10), confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
    }

    [Fact]
    public async Task CreateConnection_WhenNothingCanAskTheOperator_NeverBindsAnUncorrelatedConnection()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-elsewhere")]);

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10))
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
    }

    [Fact]
    public async Task CreateConnection_WhenTheBrowserReportsTooLate_StillPrefersWhatItNamed()
    {
        var browser = new RecordingBrowserLauncher();
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-sighted")]);
        var prompt = new ScriptedConfirmationPrompt();

        var flow = Create(
            browser,
            new RecordingMessagePresenter(),
            TimeSpan.FromSeconds(30),
            catalog,
            interval: TimeSpan.FromMilliseconds(10),
            callbackGrace: TimeSpan.FromSeconds(10),
            confirmations: prompt);

        var creating = flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        await ReportAsync(browser, "conn-reported");

        var result = await creating.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("conn-reported", result.ConnectionId);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhileWatching_DoesNotAdoptTwoNewConnectionsTheOperatorOwns()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-one"), Candidate("conn-two")]);
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10), confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhileWatching_DoesNotAdoptAConnectionThatIsNotConnected()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-half", status: "Error")]);
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10), confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhileWatching_DoesNotAdoptAnotherOperatorsConnection()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-theirs", owner: "someone-else@contoso.com")]);
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog, interval: TimeSpan.FromMilliseconds(10), confirmations: prompt)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhenNobodyIsSignedIn_OffersOnlyThisDevice()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(
            new RecordingBrowserLauncher(),
            presenter,
            Brief,
            principals: new ManagedAgentPrincipalResolver(InMemoryAuthProfileStore.SignedOut()))
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: false, CancellationToken.None);

        Assert.Contains("in a browser on this device", presenter.Messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("any other device", presenter.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenTheOperatorIsKnown_OffersAnyDevice()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(new RecordingBrowserLauncher(), presenter, Brief)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Contains("on this or any other device", presenter.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_SendsTheConnectorInternalIdNotTheProviderPath()
    {
        var browser = new RecordingBrowserLauncher();

        await Create(browser, new RecordingMessagePresenter()).CreateConnectionAsync(Target, "/providers/Microsoft.PowerApps/apis/shared_teams", openBrowser: true, CancellationToken.None);

        Assert.Equal("shared_teams", System.Web.HttpUtility.ParseQueryString(Assert.Single(browser.Launched).Query)["connector"]);
    }

    [Fact]
    public async Task CreateConnection_NamesTheConnectorByItsInternalIdInTheMessage()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(new RecordingBrowserLauncher(), presenter).CreateConnectionAsync(Target, "/providers/Microsoft.PowerApps/apis/shared_teams", openBrowser: true, CancellationToken.None);

        Assert.Contains("a shared_teams connection", Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_OpensAConsentLinkCarryingTheLiveCallback()
    {
        var browser = new RecordingBrowserLauncher();

        await Create(browser, new RecordingMessagePresenter()).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        var link = Assert.Single(browser.Launched);
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);

        Assert.Equal("shared_sql", query["connector"]);
        Assert.StartsWith("http://127.0.0.1:", query["callbackUrl"], StringComparison.Ordinal);
        Assert.NotNull(query["nonce"]);
    }

    [Fact]
    public async Task CreateConnection_TellsTheOperatorWhatIsHappeningAndHowLong()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(new RecordingBrowserLauncher(), presenter).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        var message = Assert.Single(presenter.Messages);

        Assert.Contains("shared_sql", message, StringComparison.Ordinal);
        Assert.Contains("Waiting up to", message, StringComparison.Ordinal);
        Assert.Contains("callbackUrl=http", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WordsTheWaitTheSameWayTheConsentLinkFlowDoes()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(new RecordingBrowserLauncher(), presenter).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Contains(ConnectionConsentLink.WaitNotice(Brief, canInterrupt: false), Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenAnInterruptExists_OffersItLikeTheConsentLinkFlow()
    {
        var presenter = new RecordingMessagePresenter();

        await Create(new RecordingBrowserLauncher(), presenter, Brief, null, new NeverFiresInterrupt())
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Contains("press Esc", Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenTheOperatorStopsWaiting_DoesNotSitOutTheTimeout()
    {
        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), TimeSpan.FromMinutes(8), null, new ImmediateInterrupt())
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
    }

    private sealed class ImmediateInterrupt : IOperatorInterrupt
    {
        public Task WaitForInterruptAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NeverFiresInterrupt : IOperatorInterrupt
    {
        public Task WaitForInterruptAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [Fact]
    public async Task CreateConnection_WhenNoBrowserOpens_KeepsListeningAndStillShowsTheLink()
    {
        var browser = new RecordingBrowserLauncher { Throws = new InvalidOperationException("no handler") };
        var presenter = new RecordingMessagePresenter();

        var result = await Create(browser, presenter).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Contains("callbackUrl=http", Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenNothingCallsBack_TimesOut()
    {
        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter()).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task CreateConnection_WithoutABrowser_PrintsTheLinkAndLaunchesNothing()
    {
        var browser = new RecordingBrowserLauncher();
        var presenter = new RecordingMessagePresenter();

        await Create(browser, presenter).CreateConnectionAsync(Target, "shared_sql", openBrowser: false, CancellationToken.None);

        var message = Assert.Single(presenter.Messages);

        Assert.Empty(browser.Launched);
        Assert.Contains("Create a shared_sql connection here", message, StringComparison.Ordinal);
        Assert.Contains("callbackUrl=http", message, StringComparison.Ordinal);
        Assert.Contains("nonce=", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WithoutABrowser_StillListensAndTimesOut()
    {
        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter()).CreateConnectionAsync(Target, "shared_sql", openBrowser: false, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task CreateConnection_UsesADistinctNoncePerCall()
    {
        var browser = new RecordingBrowserLauncher();
        var flow = Create(browser, new RecordingMessagePresenter());

        await flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);
        await flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        var nonces = browser.Launched.Select(link => System.Web.HttpUtility.ParseQueryString(link.Query)["nonce"]).ToArray();

        Assert.Equal(2, nonces.Distinct().Count());
    }

    [Fact]
    public async Task CreateConnection_WhenTheCallbackNeverArrives_NamesWhatTheServiceShowsRatherThanBindingIt()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Null(result.ConnectionName);
        Assert.Contains("conn-1", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Nothing was removed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("--connection-id", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenTheCallbackReportsTheConnectionItCreated_BindsThatExactConnection()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1")]);
        var browser = new RecordingBrowserLauncher();
        var flow = Create(browser, new RecordingMessagePresenter(), TimeSpan.FromSeconds(10), catalog);

        var creating = flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        await ReportAsync(browser, "conn-callback");

        var result = await creating.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("conn-callback", result.ConnectionId);
    }

    [Fact]
    public async Task CreateConnection_IgnoresConnectionsThatExistedBeforeTheFlowStarted()
    {
        var catalog = new CallbackCatalog(_ => [Candidate("already-there")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task CreateConnection_NamesAHalfMadeConnectionTheSignInNeverFinished()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1", "Disconnected")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("conn-1", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Nothing was removed", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenSeveralNewConnectionsAppear_RefusesToGuess()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1"), Candidate("conn-2")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("conn-1", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("conn-2", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenTheWaitEnds_StillNamesAConnectionCreatedTooLate()
    {
        // Polling never completes a cycle, so only the snapshot and the closing read happen: this is
        // the connection that used to be left orphaned behind a timeout.
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-late")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("conn-late", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenTheCatalogCannotBeRead_FallsBackToTheCallbackAlone()
    {
        var catalog = new CallbackCatalog(_ => throw new InvalidOperationException("catalog unavailable"));

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Equal(1, catalog.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhenDiscoveryFindsOne_DoesNotClaimItCameFromThisRequest()
    {
        var presenter = new RecordingMessagePresenter();
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            presenter,
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.DoesNotContain(presenter.Messages, message => message.Contains("was read from the service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateConnection_DoesNotAdoptAConnectionAnotherOperatorCreated()
    {
        var presenter = new RecordingMessagePresenter();
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-theirs", owner: "someone-else@contoso.com")]);

        var result = await Create(new RecordingBrowserLauncher(), presenter, Brief, catalog)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Null(result.ConnectionName);
        Assert.Contains("conn-theirs", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("--connection-id", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(presenter.Messages, message => message.Contains("was read from the service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateConnection_DoesNotAdoptAConnectionTheCatalogNamesNoOwnerFor()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1", owner: null)]);

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
    }

    [Fact]
    public async Task CreateConnection_WhenNobodyIsSignedIn_DoesNotAdoptADiscoveredConnection()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-1")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog,
            principals: new ManagedAgentPrincipalResolver(InMemoryAuthProfileStore.SignedOut()))
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
    }

    [Fact]
    public async Task CreateConnection_WhileAnotherOperatorCreatesOneToo_NamesOnlyTheOperatorsOwn()
    {
        var catalog = new CallbackCatalog(call => call == 1
            ? []
            : [Candidate("conn-theirs", owner: "someone-else@contoso.com"), Candidate("conn-mine")]);

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("conn-mine", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("conn-theirs", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenTheOperatorHasTwoWindowsOpen_RefusesToGuessWhichOneThisRequestMade()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-first"), Candidate("conn-second")]);

        var result = await Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), Brief, catalog)
            .CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("conn-first", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("conn-second", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_WhenOnlyAnotherOperatorsConnectionAppears_NamesItWithoutBindingIt()
    {
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-theirs", owner: "someone-else@contoso.com")]);

        var result = await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Contains("conn-theirs", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnection_DoesNotAskTheCatalogWhileItIsStillWaitingForTheBrowser()
    {
        var catalog = new CallbackCatalog(_ => []);

        await Create(
            new RecordingBrowserLauncher(),
            new RecordingMessagePresenter(),
            Brief,
            catalog).CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        Assert.Equal(2, catalog.Calls);
    }

    [Fact]
    public async Task CreateConnection_WhenTheServiceListsTheConnectionBeforeTheBrowserReports_StillTakesTheCallback()
    {
        var browser = new RecordingBrowserLauncher();
        var catalog = new CallbackCatalog(call => call == 1 ? [] : [Candidate("conn-listed-first")]);
        var flow = Create(browser, new RecordingMessagePresenter(), TimeSpan.FromSeconds(10), catalog);

        var creating = flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, CancellationToken.None);

        await Task.Delay(100);
        await ReportAsync(browser, "conn-callback");

        var result = await creating.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal("conn-callback", result.ConnectionId);
    }

    [Fact]
    public async Task CreateConnection_WhenTheCallerCancels_StaysACancellation()
    {
        var catalog = new CallbackCatalog(_ => []);
        using var cancellation = new CancellationTokenSource();
        var flow = Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), TimeSpan.FromMinutes(8), catalog);

        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CreateConnection_WhenTheCatalogCannotBeReadAndTheCallerCancels_StaysACancellation()
    {
        var catalog = new CallbackCatalog(_ => throw new InvalidOperationException("catalog unavailable"));
        using var cancellation = new CancellationTokenSource();
        var flow = Create(new RecordingBrowserLauncher(), new RecordingMessagePresenter(), TimeSpan.FromMinutes(8), catalog);

        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => flow.CreateConnectionAsync(Target, "shared_sql", openBrowser: true, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A catalog whose answer depends on how many times it has been asked.</summary>
    private sealed class CallbackCatalog(Func<int, IReadOnlyList<CopilotConnectionCandidate>> respond) : IConnectorCatalogPort
    {
        private int _calls;

        public int Calls => _calls;

        public Task<IReadOnlyList<CopilotConnectionCandidate>> ListConnectionsAsync(CopilotEnvironmentContext environment, string connectorInternalId, CancellationToken cancellationToken) =>
            Task.FromResult(respond(Interlocked.Increment(ref _calls)));

        public Task<IReadOnlyList<CopilotConnectorInfo>> ListConnectorsAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotConnectorInfo>>([]);
    }
}

