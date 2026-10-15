using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionConsentSessionTests
{
    private static PowerAppsConnectionClient Client(ScriptedHandler handler) =>
        new(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler);

    private static PowerAppsConnectionClient Client(HttpMessageHandler handler) =>
        new(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler);

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static ScriptedHandler Routed(Func<CapturedRequest, HttpResponseMessage> responder) => new(responder);

    private static HttpResponseMessage Route(CapturedRequest request) =>
        request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal)
            ? ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse())
            : ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));

    [Fact]
    public async Task Run_CreatesTheConnectionThenAsksForItsConsentLinks()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        ConnectionConsentContext? seen = null;

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (context, _) =>
        {
            seen = context;

            return Task.FromResult(ConnectionSignInResult.Completed);
        }, CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/getConsentLink", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(new Uri(ConnectionFixtures.ConsentLink), seen!.ConsentLink);
    }

    [Fact]
    public async Task Run_ReportsTheConnectionItNamed()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal(handler.Requests[0].Uri.AbsolutePath.Split('/')[^1], result.ConnectionId);
        Assert.Equal("Office 365 Outlook", result.DisplayName);
    }

    [Fact]
    public async Task Run_SurfacesAFailedCreateWithoutDeletingAnything()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"MissingEnvironmentName\"}}"));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None));

        Assert.Contains("400", failure.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Run_DeletesTheConnectionWhenAuthenticationDoesNotComplete()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Run_DeletesTheConnectionWhenAuthenticationThrows()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => throw new SilentConnectionUnavailableException("nope"), CancellationToken.None));

        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Run_DeletesTheConnectionWhenTheOperatorInterrupts()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => throw new OperationCanceledException(), CancellationToken.None));

        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Run_StillAuthenticatesWhenTheConsentLinkCallFails()
    {
        using var handler = Routed(request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal)
            ? ConnectionFixtures.Json(HttpStatusCode.InternalServerError, "boom")
            : ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false)));
        using var client = Client(handler);

        ConnectionConsentContext? seen = null;

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (context, _) =>
        {
            seen = context;

            return Task.FromResult(ConnectionSignInResult.Completed);
        }, CancellationToken.None);

        Assert.Null(seen!.ConsentLink);
    }

    [Fact]
    public async Task Run_PassesTheParameterSetThroughToTheCreate()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_sql", "oauth", ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Contains("\"oauth\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_StopsAtTheCreateWhenTheServiceReportsTheConnectionReady()
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_rss", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal(request.Uri.AbsolutePath.Split('/')[^1], result.ConnectionId);
        Assert.Equal(result.ConnectionId, result.ConnectionName);
    }

    [Fact]
    public async Task Run_DoesNotAskForAConsentLinkWhenTheConnectionIsReady()
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_rss", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_DoesNotRunTheAuthenticationStepWhenTheConnectionIsReady()
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_rss", null, ConnectionFixtures.LandingUrl, (_, _) => throw new InvalidOperationException("The authentication step must not be reached."), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
    }

    [Fact]
    public async Task Run_DoesNotDeleteAConnectionThatWasReadyOnCreation()
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_rss", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Run_ReportsTheDisplayNameTheCreateReturnedForAReadyConnection()
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_rss", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Equal("Office 365 Outlook", result.DisplayName);
    }

    [Fact]
    public async Task Run_NamesTheConnectorWhenAReadyConnectionCarriesNoDisplayName()
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ReadyConnection(withDisplayName: false)));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_rss", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Equal("shared_rss", result.DisplayName);
    }

    [Fact]
    public async Task Run_StillAsksForConsentWhenTheCreatedConnectionReportsANonAuthenticationError()
    {
        using var handler = Routed(request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal)
            ? ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse())
            : ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.ConnectionWithStatus("{\"status\":\"Error\",\"error\":{\"code\":\"Throttled\"}}")));
        using var client = Client(handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_ftp", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_StillAsksForConsentWhenTheCreateBodyIsNotJson()
    {
        using var handler = Routed(request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal)
            ? ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse())
            : ConnectionFixtures.Json(HttpStatusCode.Created, "not json"));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));
        Assert.Equal("shared_office365", result.DisplayName);
    }

    [Fact]
    public async Task Run_StillAsksForConsentWhenTheCreateBodyReportsNothingButTheConnectorNeedsSigningIn()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitUntilAuthenticated_ReturnsAsSoonAsTheServiceStopsReportingUnauthenticated()
    {
        var reads = 0;
        using var handler = Routed(_ => ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: ++reads >= 3)));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Completed, outcome.Status);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task WaitUntilAuthenticated_GivesUpAtTheTimeout()
    {
        var clock = new SteppedClock(TimeSpan.FromSeconds(1));
        using var handler = Routed(_ =>
        {
            clock.Advance();

            return ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: false));
        });
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            clock,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Abandoned, outcome.Status);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task WaitUntilAuthenticated_StopsWhenCancelled()
    {
        using var handler = Routed(_ => ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: false)));
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            cancellation.Token));
    }

    [Fact]
    public async Task WaitUntilAuthenticated_ReportsAnOversizedAnswerRatherThanReadingIt()
    {
        var oversized = "{\"properties\":{\"displayName\":\"" + new string('x', (1024 * 1024) + 5000) + "\"}}";

        using var handler = Routed(_ => ConnectionFixtures.Ok(oversized));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMinutes(8),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Failed, outcome.Status);
        Assert.Contains("too large to read", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WaitUntilAuthenticated_EndsAtTheDeadlineEvenWhileAReadIsStalled()
    {
        using var handler = new StallingHandler();
        using var client = Client(handler);
        var started = DateTimeOffset.UtcNow;

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(10),
            TimeProvider.System,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ConnectionSignInStatus.Abandoned, outcome.Status);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("Throttled")]
    [InlineData("ApiConnectionLimitExceeded")]
    public async Task WaitUntilAuthenticated_ReportsANonAuthenticationErrorWithoutWaitingOutTheTimeout(string code)
    {
        using var handler = Routed(_ => ConnectionFixtures.Ok(ConnectionFixtures.ConnectionWithStatus("{\"status\":\"Error\",\"error\":{\"code\":\"" + code + "\",\"message\":\"no capacity\"}}")));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMinutes(8),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Failed, outcome.Status);
        Assert.Contains(code, outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("no capacity", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("clientRequestId=", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WaitUntilAuthenticated_ReportsAFailingStatusCarryingNoDetailWithoutWaitingOutTheTimeout()
    {
        using var handler = Routed(_ => ConnectionFixtures.Ok(ConnectionFixtures.ConnectionWithStatus("{\"status\":\"Error\"}")));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMinutes(8),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Failed, outcome.Status);
        Assert.Contains("Error", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task WaitUntilAuthenticated_ReportsAReadTheServiceWillNotAnswerDifferently(HttpStatusCode status)
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(status, "the connection is gone"));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMinutes(8),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Failed, outcome.Status);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("the connection is gone", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("clientRequestId=", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task WaitUntilAuthenticated_KeepsWaitingThroughAReadTheServiceMayAnswerDifferently(HttpStatusCode status)
    {
        var reads = 0;
        using var handler = Routed(_ => ++reads < 3
            ? ConnectionFixtures.Json(status, "later")
            : ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Completed, outcome.Status);
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"conn-1\"}")]
    public async Task WaitUntilAuthenticated_DoesNotTreatABodyThatDescribesNoConnectionAsSignedIn(string body)
    {
        using var handler = Routed(_ => ConnectionFixtures.Ok(body));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(20),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Abandoned, outcome.Status);
        Assert.Null(outcome.Detail);
    }

    [Fact]
    public async Task WaitUntilAuthenticated_ReturnsOnceTheServiceReportsTheConnectionConnected()
    {
        var reads = 0;
        using var handler = Routed(_ => ConnectionFixtures.Ok(++reads >= 2 ? ConnectionFixtures.ReadyConnection() : ConnectionFixtures.Connection(authenticated: false)));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Completed, outcome.Status);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Run_SendsEveryRequestForTheEnvironmentsTenant()
    {
        using var handler = Routed(Route);
        var tokens = new RecordingTokenProvider();
        using var client = new PowerAppsConnectionClient(tokens, CoreServicesClusterCategory.Prod, () => handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.EnvironmentIn("guest-tenant"), "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.NotEmpty(tokens.Tenants);
        Assert.All(tokens.Tenants, tenant => Assert.Equal("guest-tenant", tenant));
    }

    [Fact]
    public async Task Run_WhenTheServiceRefusesToDeleteTheUnusedConnection_SaysSoRatherThanClaimingItWasRemoved()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.Forbidden, "you do not own this connection")
            : Route(request));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Contains("could not be removed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("403", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("you do not own this connection", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("clientRequestId=", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection remove", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("--connector shared_office365", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains($"--environment-id {ConnectionFixtures.EnvironmentId}", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(handler.Requests[0].Uri.AbsolutePath.Split('/')[^1], result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenTheDeleteFails_SaysSoRatherThanClaimingItWasRemoved()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? throw new HttpRequestException("the network is unreachable")
            : Route(request));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Contains("could not be removed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("the request failed before the service answered", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("the network is unreachable", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenTheUnusedConnectionIsDeleted_ReportsNothingToCleanUp()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Run_WhenTheConnectionWasAlreadyGone_ReportsNothingToCleanUp()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ConnectionNotFound\"}}")
            : Route(request));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Run_WhenTheSignInFails_ReportsTheServicesReasonAndRemovesTheConnection()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Failed("ApiConnectionLimitExceeded: no capacity")), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Null(result.ConnectionId);
        Assert.Equal("ApiConnectionLimitExceeded: no capacity", result.ErrorMessage);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Run_WhenTheSignInFailsAndTheConnectionSurvives_ReportsBoth()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.Forbidden, "you do not own this connection")
            : Route(request));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Failed("Throttled")), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.Contains("Throttled", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("could not be removed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(handler.Requests[0].Uri.AbsolutePath.Split('/')[^1], result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenAuthenticationThrowsAndTheConnectionSurvives_ReportsTheOrphanRatherThanOnlyTheCause()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.Forbidden, "you do not own this connection")
            : Route(request));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => throw new SilentConnectionUnavailableException("no consent link"), CancellationToken.None));

        Assert.Equal("connection-not-deleted", failure.ErrorKind);
        Assert.Contains("no consent link", failure.Message, StringComparison.Ordinal);
        Assert.Contains("403", failure.Message, StringComparison.Ordinal);
        Assert.Contains(handler.Requests[0].Uri.AbsolutePath.Split('/')[^1], failure.Message, StringComparison.Ordinal);
        Assert.IsType<SilentConnectionUnavailableException>(failure.InnerException);
    }

    [Fact]
    public async Task Run_WhenAuthenticationThrowsAndTheConnectionSurvives_DoesNotLetTheLadderFallBackSilently()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? throw new HttpRequestException("the network is unreachable")
            : Route(request));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => throw new SilentConnectionUnavailableException("no consent link"), CancellationToken.None));

        Assert.IsNotType<SilentConnectionUnavailableException>(failure);
        Assert.Contains("no consent link", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the request failed before the service answered", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the network is unreachable", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection remove", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("--connector shared_office365", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains($"--environment-id {ConnectionFixtures.EnvironmentId}", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenAuthenticationThrowsAndTheConnectionIsRemoved_KeepsTheOriginalFailure()
    {
        using var handler = Routed(Route);
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => throw new SilentConnectionUnavailableException("no consent link"), CancellationToken.None));

        Assert.Equal("no consent link", failure.Message);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Run_WhenTheCleanupDeleteTimesOut_ReportsTheOrphanRatherThanTheTimeout()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
            : Route(request));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.Contains("could not be removed", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"EnvironmentNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"RouteNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"ServiceDecommissioned\"}}")]
    public async Task Run_WhenTheDeleteNeverReachedTheConnection_DoesNotClaimItWasRemoved(string body)
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.NotFound, body)
            : Route(request));
        using var client = Client(handler);

        var result = await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Abandoned), CancellationToken.None);

        Assert.Contains("could not be removed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("404", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Run_WhenTheServiceRefusedToCreate_FallsBackWithoutDeletingAnything(HttpStatusCode status)
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Json(status, "{\"error\":{\"code\":\"MissingEnvironmentName\"}}"));
        using var client = Client(handler);

        await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Run_WhenTheCreateWasNeverAnsweredFor_RemovesWhateverItMayHaveMade(HttpStatusCode status)
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ConnectionNotFound\"}}")
            : ConnectionFixtures.Json(status, "the gateway gave up"));
        using var client = Client(handler);

        await Assert.ThrowsAsync<SilentConnectionUnavailableException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None));

        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Run_WhenTheCreateMayHaveBeenCommittedAndSurvives_DoesNotLetTheLadderFallBackSilently()
    {
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.Forbidden, "you do not own this connection")
            : ConnectionFixtures.Json(HttpStatusCode.GatewayTimeout, "the gateway gave up"));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None));

        Assert.Equal("connection-not-deleted", failure.ErrorKind);
        Assert.Contains(handler.Requests[0].Uri.AbsolutePath.Split('/')[^1], failure.Message, StringComparison.Ordinal);
        Assert.IsType<SilentConnectionUnavailableException>(failure.InnerException);
    }

    [Fact]
    public async Task Run_WhenTheCreateTransportFailsAndTheConnectionSurvives_DoesNotLetTheLadderFallBackSilently()
    {
        var attempts = 0;
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.Forbidden, "you do not own this connection")
            : ++attempts == 1
                ? throw new HttpRequestException("the connection was reset")
                : Route(request));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None));

        Assert.DoesNotContain("the connection was reset", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the request failed before the service answered", failure.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    [Fact]
    public async Task Run_WhenTheCreateTransportFailsAndNothingSurvives_KeepsTheOriginalFailure()
    {
        var attempts = 0;
        using var handler = Routed(request => request.Method == HttpMethod.Delete
            ? ConnectionFixtures.Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ConnectionNotFound\"}}")
            : ++attempts == 1
                ? throw new HttpRequestException("the connection was reset")
                : Route(request));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None));

        Assert.Equal("the connection was reset", failure.Message);
    }

    [Fact]
    public async Task Run_DoesNotTreatAnEmptyCreateBodyAsAUsableConnection()
    {
        using var handler = Routed(request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal)
            ? ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse())
            : ConnectionFixtures.Json(HttpStatusCode.Created, string.Empty));
        using var client = Client(handler);

        await ConnectionConsentSession.RunAsync(client, ConnectionFixtures.Environment, "shared_office365", null, ConnectionFixtures.LandingUrl, (_, _) => Task.FromResult(ConnectionSignInResult.Completed), CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitUntilAuthenticated_KeepsWaitingThroughATransportFailure()
    {
        var reads = 0;
        using var handler = Routed(_ => ++reads < 3
            ? throw new HttpRequestException("the connection was reset")
            : ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Completed, outcome.Status);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task WaitUntilAuthenticated_KeepsWaitingThroughATransportTimeout()
    {
        var reads = 0;
        using var handler = Routed(_ => ++reads < 3
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
            : ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Completed, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported)]
    public async Task WaitUntilAuthenticated_ReportsATerminalServerFailureWithoutWaitingOutTheTimeout(HttpStatusCode status)
    {
        using var handler = Routed(_ => ConnectionFixtures.Json(status, "this build cannot serve that"));
        using var client = Client(handler);

        var outcome = await ConnectionConsentSession.WaitUntilAuthenticatedAsync(
            client,
            ConnectionFixtures.Environment,
            "shared_office365",
            "conn-1",
            TimeSpan.FromMinutes(8),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(ConnectionSignInStatus.Failed, outcome.Status);
        Assert.Single(handler.Requests);
    }
}
