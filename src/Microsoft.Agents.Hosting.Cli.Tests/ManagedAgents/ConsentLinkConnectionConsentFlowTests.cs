using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConsentLinkConnectionConsentFlowTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1);

    private static ConsentLinkConnectionConsentFlow Flow(ScriptedHandler handler, RecordingMessagePresenter presenter, RecordingBrowserLauncher browser, TimeSpan? timeout = null, IOperatorInterrupt? interrupt = null, TimeSpan? interval = null) =>
        new(new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler), presenter, browser, CoreServicesClusterCategory.Prod, interrupt, timeout: timeout ?? TimeSpan.FromSeconds(30), interval: interval ?? Interval);

    private static ScriptedHandler Handler(bool authenticates = true, string? consentResponse = null)
    {
        var reads = 0;

        return new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));
            }

            if (request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal))
            {
                return ConnectionFixtures.Ok(consentResponse ?? ConnectionFixtures.ConsentResponse());
            }

            if (request.Method == HttpMethod.Delete)
            {
                return ConnectionFixtures.Ok("{}");
            }

            return ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: authenticates && ++reads >= 2));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_WhenNothingCallsBack_PollingStillReportsTheConnection(bool openBrowser)
    {
        using var handler = Handler();
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher());

        var result = await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.NotNull(result.ConnectionId);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host is "127.0.0.1" or "localhost");
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_WithoutInteraction_StillRedirectsConsentBackToTheLoopback()
    {
        using var handler = Handler();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None);

        var consentRequest = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(consentRequest.Body);
        var redirectUrl = new Uri(body.RootElement.GetProperty("redirectUrl").GetString()!);

        Assert.Equal("127.0.0.1", redirectUrl.Host);
        Assert.StartsWith("/callback/", redirectUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RedirectsConsentBackToTheLoopbackSoTheCodeCanBeCaptured()
    {
        using var handler = Handler();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        var consentRequest = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(consentRequest.Body);
        var redirectUrl = new Uri(body.RootElement.GetProperty("redirectUrl").GetString()!);

        Assert.Equal("127.0.0.1", redirectUrl.Host);
        Assert.StartsWith("/callback/", redirectUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_OffersTheLinkForThisMachineOrAnotherDevice(bool openBrowser)
    {
        using var handler = Handler();
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser, CancellationToken.None);

        var message = Assert.Single(presenter.Messages);

        Assert.Contains("on another device", message, StringComparison.Ordinal);
        Assert.Contains(ConnectionFixtures.ConsentLink, message, StringComparison.Ordinal);
        Assert.DoesNotContain("on this or any other device", message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WhenNobodySignsIn_SaysToTryAgainOnThisMachine()
    {
        using var handler = Handler(authenticates: false);
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), TimeSpan.FromMilliseconds(30));

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Contains(presenter.Messages, message =>
            message.Contains("No sign-in was completed", StringComparison.Ordinal)
            && message.Contains("try again in a browser on this machine", StringComparison.Ordinal));

        Assert.DoesNotContain(presenter.Messages, message => message.Contains("make.powerapps.com", StringComparison.Ordinal));
    }

    private static ScriptedHandler RedeemingHandler(string query, HttpStatusCode confirm = HttpStatusCode.OK, bool authenticatesAnyway = false)
    {
        var confirmed = authenticatesAnyway;

        return new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));
            }

            if (request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Body);
                var redirectUrl = new Uri(body.RootElement.GetProperty("redirectUrl").GetString()!);

                _ = Task.Run(() => ConnectionConsentCallback.SendAsync(redirectUrl, query));

                return ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse());
            }

            if (request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal))
            {
                confirmed = confirm is HttpStatusCode.OK;

                return ConnectionFixtures.Json(confirm, confirmed ? "{}" : "the code was refused");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return ConnectionFixtures.Ok("{}");
            }

            return ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: confirmed));
        });
    }

    [Fact]
    public async Task Create_WhenTheRedirectCarriesTheCode_RedeemsItAndReportsTheConnection()
    {
        using var handler = RedeemingHandler("code=consent-code");
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        var confirm = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(confirm.Body);

        Assert.Equal("consent-code", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    private static ConsentLinkConnectionConsentFlow FlowWith(RecordingTokenProvider tokens, ScriptedHandler handler) =>
        new(
            new PowerAppsConnectionClient(tokens, CoreServicesClusterCategory.Prod, () => handler),
            new RecordingMessagePresenter(),
            new RecordingBrowserLauncher(),
            CoreServicesClusterCategory.Prod,
            timeout: TimeSpan.FromSeconds(30),
            interval: Interval);

    [Fact]
    public async Task Create_RedeemsTheCodeForThePrincipalTheTargetTenantKnows()
    {
        using var handler = RedeemingHandler("code=consent-code");

        var tokens = new RecordingTokenProvider { AccessTokenReturns = FakeJwt.Create(tenantId: "guest-tenant", objectId: "guest-object-id") };
        using var flow = FlowWith(tokens, handler);

        await flow
            .CreateConnectionAsync(ConnectionFixtures.EnvironmentIn("guest-tenant"), "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        var confirm = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(confirm.Body);

        Assert.Equal("guest-object-id", body.RootElement.GetProperty("objectId").GetString());
        Assert.Equal("guest-tenant", body.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Create_WhenTheTokenNamesNoPrincipal_StillRedeemsTheCodeForTheEnvironmentsTenant()
    {
        using var handler = RedeemingHandler("code=consent-code");
        var tokens = new RecordingTokenProvider { AccessTokenReturns = "not-a-compact-jwt" };
        using var flow = FlowWith(tokens, handler);

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.EnvironmentIn("guest-tenant"), "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        var confirm = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(confirm.Body);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.False(body.RootElement.TryGetProperty("objectId", out _));
        Assert.Equal("guest-tenant", body.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Create_WhenTheServiceRefusesTheCode_ReportsTheFailureAndRemovesTheConnection()
    {
        using var handler = RedeemingHandler("code=consent-code", HttpStatusCode.BadRequest);
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Contains("400", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Create_WhenTheRedirectReportsAnErrorButTheServiceAuthenticatesAnyway_KeepsTheConnection()
    {
        using var handler = RedeemingHandler("error=OAuth2CertificateOneDriveForBusiness%20authorization%20flow%20failed", authenticatesAnyway: true);
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    private static ScriptedHandler LostConfirmationHandler(bool authenticatesAnyway)
    {
        var confirmed = false;

        return new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));
            }

            if (request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Body);
                var redirectUrl = new Uri(body.RootElement.GetProperty("redirectUrl").GetString()!);

                _ = Task.Run(() => ConnectionConsentCallback.SendAsync(redirectUrl, "code=consent-code"));

                return ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse());
            }

            if (request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal))
            {
                confirmed = authenticatesAnyway;

                throw new HttpRequestException("the response was lost");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return ConnectionFixtures.Ok("{}");
            }

            return ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: confirmed));
        });
    }

    [Fact]
    public async Task Create_WhenTheConfirmationResponseIsLostButTheServiceCommittedIt_KeepsTheConnection()
    {
        using var handler = LostConfirmationHandler(authenticatesAnyway: true);
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher(), interval: TimeSpan.FromMilliseconds(40));

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Create_WhenTheConfirmationFailsAndNothingAuthenticates_ReportsItAndRemovesTheConnection()
    {
        using var handler = LostConfirmationHandler(authenticatesAnyway: false);
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Contains("could not be confirmed", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Create_WhenTheOperatorInterruptsAfterTheCallback_StopsWaitingInsteadOfRunningOutTheTimeout()
    {
        var interrupt = new ScriptedInterrupt();

        using var handler = new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));
            }

            if (request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Body);
                var redirectUrl = new Uri(body.RootElement.GetProperty("redirectUrl").GetString()!);

                _ = Task.Run(() => ConnectionConsentCallback.SendAsync(redirectUrl, "code=consent-code"));

                return ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse());
            }

            if (request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal))
            {
                interrupt.Interrupt();

                return ConnectionFixtures.Ok("{}");
            }

            if (request.Method == HttpMethod.Delete)
            {
                return ConnectionFixtures.Ok("{}");
            }

            return ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: false));
        });

        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), TimeSpan.FromMinutes(8), interrupt);

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Contains(presenter.Messages, message => message.Contains("was cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_WhenTheRedirectCarriesAnError_ReportsItWithoutRedeemingACode()
    {
        using var handler = RedeemingHandler("error=access_denied");
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Contains("access_denied", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_WithoutInteraction_StillCapturesAndRedeemsTheConsentCode()
    {
        using var handler = RedeemingHandler("code=consent-code");
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher());

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var confirmation = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/confirmConsentCode", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(confirmation.Body);

        Assert.Equal("consent-code", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Contains(ConnectionFixtures.ConsentLink, Assert.Single(presenter.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Create_WithoutInteraction_RemovesTheConnectionOnlyWhenNobodyEverSignsIn()
    {
        using var handler = Handler(authenticates: false);
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher(), TimeSpan.FromMilliseconds(30));

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Create_PresentsAConsentLinkThatCarriesNoLocalCallback()
    {
        using var handler = Handler();
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        var message = Assert.Single(presenter.Messages);

        Assert.Contains(ConnectionFixtures.ConsentLink, message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", message, StringComparison.Ordinal);
        Assert.DoesNotContain("callbackUrl", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_DoesNotLaunchABrowserWhenInteractionIsNotAllowed()
    {
        using var handler = Handler();
        var browser = new RecordingBrowserLauncher();
        using var flow = Flow(handler, new RecordingMessagePresenter(), browser);

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None);

        Assert.Empty(browser.Launched);
    }

    [Fact]
    public async Task Create_LaunchesTheConsentLinkWhenInteractionIsAllowed()
    {
        using var handler = Handler();
        var browser = new RecordingBrowserLauncher();
        using var flow = Flow(handler, new RecordingMessagePresenter(), browser);

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Equal(new Uri(ConnectionFixtures.ConsentLink), Assert.Single(browser.Launched));
    }

    [Fact]
    public async Task Create_StillCompletesWhenTheBrowserCannotBeOpened()
    {
        using var handler = Handler();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher { Throws = new InvalidOperationException("no browser") });

        var result = await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
    }

    [Fact]
    public async Task Create_PollsTheServiceUntilTheOperatorHasSignedIn()
    {
        using var handler = Handler();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task Create_RemovesTheConnectionWhenNobodySignsIn()
    {
        using var handler = Handler(authenticates: false);
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), TimeSpan.FromMilliseconds(30));

        var result = await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
        Assert.Contains(presenter.Messages, message => message.Contains("was removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_RefusesAConsentLinkOnAnUntrustedHost()
    {
        using var handler = Handler(consentResponse: ConnectionFixtures.ConsentResponse(consentLink: "https://attacker.example.com/login"));
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var failure = await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None));

        Assert.Contains("attacker.example.com", failure.Message, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Create_ReportsWhenTheConnectorOffersNoConsentLink()
    {
        using var handler = Handler(consentResponse: ConnectionFixtures.ConsentResponse(withConsentLink: false));
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        var failure = await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, CancellationToken.None));

        Assert.Contains("did not offer a consent link", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_NormalizesAFullProviderPath()
    {
        using var handler = Handler();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "/providers/Microsoft.PowerApps/apis/shared_office365", openBrowser: false, CancellationToken.None);

        Assert.StartsWith("/providers/Microsoft.PowerApps/apis/shared_office365/connections/", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ReturnsImmediatelyForAConnectorThatNeedsNoConsent()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        var presenter = new RecordingMessagePresenter();
        var browser = new RecordingBrowserLauncher();
        using var flow = Flow(handler, presenter, browser);

        var result = await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_rss", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        Assert.Empty(browser.Launched);
        Assert.Empty(presenter.Messages);
    }

    [Fact]
    public async Task Create_WhenTheOperatorStopsWaiting_RemovesTheConnectionAndReportsCancelled()
    {
        using var handler = Handler(authenticates: false);
        var presenter = new RecordingMessagePresenter();
        var interrupt = new ScriptedInterrupt();
        interrupt.Interrupt();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), TimeSpan.FromMinutes(8), interrupt);

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
        Assert.Contains(presenter.Messages, message => message.Contains("was cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_WhenTheOperatorStopsWaiting_DoesNotReportTheTimeoutInstead()
    {
        using var handler = Handler(authenticates: false);
        var presenter = new RecordingMessagePresenter();
        var interrupt = new ScriptedInterrupt();
        interrupt.Interrupt();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), TimeSpan.FromMinutes(8), interrupt);

        await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(presenter.Messages, message => message.Contains("No sign-in was completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_AdvertisesTheInterruptWhenOneIsAvailable()
    {
        using var handler = Handler();
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), interrupt: new ScriptedInterrupt());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Contains("press Esc", Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_DoesNotAdvertiseAnInterruptThatIsNotAvailable()
    {
        using var handler = Handler();
        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher());

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.DoesNotContain("Esc", Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WhenTheOperatorNeverInterrupts_CompletesTheSignInAndAbandonsTheListen()
    {
        using var handler = Handler();
        var interrupt = new ScriptedInterrupt();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher(), interrupt: interrupt);

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.True(interrupt.WasAbandoned);
    }

    [Fact]
    public async Task Create_WhenTheInterruptSourceFails_StillCompletesTheSignIn()
    {
        using var handler = Handler();
        var interrupt = new ScriptedInterrupt();
        interrupt.Fail();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher(), interrupt: interrupt);

        var result = await flow
            .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
    }

    [Fact]
    public async Task Create_WhenTheCommandIsCancelled_RemovesTheHalfMadeConnection()
    {
        using var handler = Handler(authenticates: false);
        using var cancellation = new CancellationTokenSource();
        using var flow = Flow(handler, new RecordingMessagePresenter(), new RecordingBrowserLauncher(), TimeSpan.FromMinutes(8), new ScriptedInterrupt());

        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => flow
                .CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Create_WhenTheServiceRefusesToRemoveTheUnusedConnection_SaysSoRatherThanClaimingItWasRemoved()
    {
        using var handler = new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));
            }

            if (request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal))
            {
                return ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse());
            }

            return request.Method == HttpMethod.Delete
                ? ConnectionFixtures.Json(HttpStatusCode.Forbidden, "you do not own this connection")
                : ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: false));
        });

        var presenter = new RecordingMessagePresenter();
        using var flow = Flow(handler, presenter, new RecordingBrowserLauncher(), TimeSpan.FromMilliseconds(30));

        var result = await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.DoesNotContain(presenter.Messages, message => message.Contains("was removed", StringComparison.Ordinal));
        Assert.Contains(presenter.Messages, message => message.Contains("could not be removed", StringComparison.Ordinal) && message.Contains("msagent deployment connection remove", StringComparison.Ordinal) && message.Contains("--connector shared_office365", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_SendsEveryRequestForTheEnvironmentsTenant()
    {
        using var handler = Handler();
        var tokens = new RecordingTokenProvider();
        using var flow = FlowWith(tokens, handler);

        await flow.CreateConnectionAsync(ConnectionFixtures.EnvironmentIn("guest-tenant"), "shared_office365", openBrowser: false, CancellationToken.None);

        Assert.NotEmpty(tokens.Tenants);
        Assert.All(tokens.Tenants, tenant => Assert.Equal("guest-tenant", tenant));
    }

    private sealed class ScriptedInterrupt : IOperatorInterrupt
    {
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasAbandoned { get; private set; }

        public void Interrupt() => _signal.TrySetResult();

        public void Fail() => _signal.TrySetException(new InvalidOperationException("no console is attached"));

        public async Task WaitForInterruptAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                WasAbandoned = true;
                _signal.TrySetCanceled(cancellationToken);
            });

            await _signal.Task.ConfigureAwait(false);
        }
    }
}
