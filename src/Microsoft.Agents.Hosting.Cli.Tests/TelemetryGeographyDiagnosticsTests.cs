using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryGeographyDiagnosticsTests
{
    private static readonly ResolvedEnvironmentTarget Target = new(
        "11111111-2222-3333-4444-555555555555", EnvironmentFixtures.ContosoTenant);
    private const string ApiKey = "00000000000000000000000000000000-11111111-2222-3333-4444-555555555555-6666";

    [Theory]
    [InlineData(" Europe EUAP ", "europe", "Europe")]
    [InlineData("unitedstates", "unitedstates", "Global")]
    [InlineData("private-response-data\r\nfake-log", "unrecognized", "Europe")]
    [InlineData(null, "missing", "Europe")]
    public async Task TraceReportsOnlyRecognizedGeography(string? geography, string label, string location)
    {
        var output = new StringWriter();
        var resolver = Resolver(new GeographyClient(_ => Task.FromResult(geography)), new(output));

        var destination = await resolver.ResolveAsync(TelemetryEnvironmentTarget.Resolved([Target]), default);

        Assert.Equal(location, destination!.Location.ToString());
        string trace = output.ToString();
        Assert.Contains("geography=" + label, trace);
        Assert.Contains("collector=" + (location == "Europe" ? "EU" : "Global"), trace);
        Assert.Contains("budgetMs=5000", trace);
        Assert.Contains("elapsedMs=", trace);
        Assert.DoesNotContain("private-response-data", trace);
        Assert.DoesNotContain(Target.EnvironmentId, trace);
        Assert.DoesNotContain(Target.TenantId, trace);
        Assert.DoesNotContain(ApiKey, trace);
    }

    [Theory]
    [InlineData("nonproduction")]
    [InlineData("target-incomplete")]
    [InlineData("geography-client-unavailable")]
    [InlineData("cloud-no-send")]
    [InlineData("ingestion-key-unavailable")]
    public async Task SkippedLookupExplainsWhyWithoutCallingService(string reason)
    {
        var output = new StringWriter();
        var client = new GeographyClient(_ => throw new InvalidOperationException("No lookup expected."));
        var target = reason == "nonproduction" ? PowerPlatformApiTargetConfig.Test : PowerPlatformApiTargetConfig.Public;
        if (reason == "cloud-no-send")
            target = target with { Cloud = CloudInstance.UsGov };
        var resolver = new TelemetryDestinationResolver(target,
            reason == "geography-client-unavailable" ? null : client,
            reason == "ingestion-key-unavailable" ? null : ApiKey, ApiKey, diagnostics: new(output));

        var destination = await resolver.ResolveAsync(reason == "target-incomplete"
            ? TelemetryEnvironmentTarget.Unknown : TelemetryEnvironmentTarget.Resolved([Target]), default);

        Assert.Contains(reason, output.ToString());
        Assert.Equal(0, client.Calls);
        if (reason is "cloud-no-send" or "ingestion-key-unavailable")
            Assert.Null(destination);
        else
            Assert.Equal(reason == "nonproduction" ? TelemetryLocation.Global : TelemetryLocation.Europe, destination!.Location);
    }

    [Fact]
    public async Task FailedLookupHasFixedReasonRatherThanExceptionText()
    {
        var output = new StringWriter();
        var resolver = Resolver(new GeographyClient(_ => throw new HttpRequestException("private-failure-data")), new(output));

        var destination = await resolver.ResolveAsync(TelemetryEnvironmentTarget.Resolved([Target]), default);

        Assert.Equal(TelemetryLocation.Europe, destination!.Location);
        Assert.Contains("lookup-failed", output.ToString());
        Assert.Contains("collector=EU", output.ToString());
        Assert.DoesNotContain("private-failure-data", output.ToString());
    }

    [Theory]
    [InlineData(EnvironmentGeographyStage.TokenAcquisitionStarted)]
    [InlineData(EnvironmentGeographyStage.RequestStarted)]
    public async Task DeadlineReportsPendingStageAndLateDiagnosticsAreClosed(EnvironmentGeographyStage pendingStage)
    {
        var stageReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualTimeProvider();
        var output = new StringWriter();
        var diagnostics = new TelemetryRoutingDiagnostics(output);
        var client = new GeographyClient(async _ =>
        {
            diagnostics.Report(pendingStage);
            stageReached.SetResult();
            await release.Task;
            diagnostics.Report(EnvironmentGeographyStage.GeographyRead);
            delivered.SetResult();
            return "europe";
        });
        var resolver = Resolver(client, diagnostics, timeProvider: time);
        try
        {
            var resolving = resolver.ResolveAsync(TelemetryEnvironmentTarget.Resolved([Target]), default).AsTask();
            await stageReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(resolving.IsCompleted);
            time.Advance(TelemetryDestinationResolver.DefaultMetadataTimeout);
            var destination = await resolving.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TelemetryLocation.Europe, destination!.Location);
            Assert.Contains("stage=lookup-deadline pendingStage=" + pendingStage, output.ToString());
            string completed = output.ToString();
            release.TrySetResult();
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(completed, output.ToString());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(200, """{"geoLongName":"europe","secret":"private-body"}""", "GeographyRead")]
    [InlineData(403, """{"secret":"private-body"}""", "ResponseReceived")]
    [InlineData(200, """{"secret":"private-body"}""", "GeographyMissing")]
    [InlineData(200, "private-body-not-json", "ResponseInvalid")]
    public async Task RealRequestReportsStatusAndParsingWithoutLeakingData(int status, string body, string lastStage)
    {
        var output = new StringWriter();
        var diagnostics = new TelemetryRoutingDiagnostics(output);
        var handler = new StubHttpMessageHandler(_ => new((HttpStatusCode)status) { Content = new StringContent(body) });
        var token = Token();
        var client = new PowerPlatformApiEnvironmentGeographyClient(new HttpClient(handler), new Tokens(token),
            PowerPlatformApiTargetConfig.Public, new(), diagnostics: diagnostics);

        var destination = await Resolver(client, diagnostics).ResolveAsync(
            TelemetryEnvironmentTarget.Resolved([Target]), default);

        Assert.Equal(TelemetryLocation.Europe, destination!.Location);
        string trace = output.ToString();
        Assert.Contains("TokenAcquisitionStarted", trace);
        Assert.Contains("TokenAvailable", trace);
        Assert.Contains("RequestStarted", trace);
        Assert.Contains("httpStatus=" + status, trace);
        Assert.Contains(lastStage, trace);
        Assert.DoesNotContain("private-body", trace);
        Assert.DoesNotContain(token.AccessToken, trace);
        Assert.DoesNotContain(Target.EnvironmentId, trace);
        Assert.DoesNotContain(Target.TenantId, trace);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false, "TokenUnavailable")]
    [InlineData(true, "TokenRejected")]
    public async Task UnavailableOrRejectedTokenDoesNotSendHttp(bool wrongTenant, string stage)
    {
        var output = new StringWriter();
        var diagnostics = new TelemetryRoutingDiagnostics(output);
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP expected."));
        var client = new PowerPlatformApiEnvironmentGeographyClient(new HttpClient(handler),
            new Tokens(wrongTenant ? Token(EnvironmentFixtures.FabrikamTenant) : null),
            PowerPlatformApiTargetConfig.Public, new(), diagnostics: diagnostics);

        var destination = await Resolver(client, diagnostics).ResolveAsync(
            TelemetryEnvironmentTarget.Resolved([Target]), default);

        Assert.Equal(TelemetryLocation.Europe, destination!.Location);
        Assert.Contains(stage, output.ToString());
        Assert.DoesNotContain("RequestStarted", output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ThrowingCoreObserverCannotFailRequest()
    {
        var diagnostics = new ThrowingDiagnostics();
        var handler = new StubHttpMessageHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"geoLongName":"europe"}"""),
        });
        var client = new PowerPlatformApiEnvironmentGeographyClient(new HttpClient(handler), new Tokens(Token()),
            PowerPlatformApiTargetConfig.Public, new(), diagnostics: diagnostics);

        Assert.Equal("europe", await client.GetGeographyAsync(Target, default));
        Assert.True(diagnostics.Calls > 0);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ThrowingDebugWriterCannotChangeRoute()
    {
        var writer = new ThrowingWriter();
        var client = new GeographyClient(_ => Task.FromResult<string?>("europe"));
        var destination = await Resolver(client, new(writer)).ResolveAsync(
            TelemetryEnvironmentTarget.Resolved([Target]), default);

        Assert.Equal(TelemetryLocation.Europe, destination!.Location);
        Assert.True(writer.Calls > 0);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAFallback()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var output = new StringWriter();
        var diagnostics = new TelemetryRoutingDiagnostics(output);
        var client = new GeographyClient(ct => Task.FromCanceled<string?>(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Resolver(client, diagnostics).ResolveAsync(
                TelemetryEnvironmentTarget.Resolved([Target]), cancellation.Token));

        Assert.Contains("lookup-cancelled", output.ToString());
        Assert.DoesNotContain("collector=", output.ToString());
        string completed = output.ToString();
        diagnostics.Report(EnvironmentGeographyStage.TokenAvailable);
        Assert.Equal(completed, output.ToString());
    }

    private static TelemetryDestinationResolver Resolver(IEnvironmentGeographyClient client,
        TelemetryRoutingDiagnostics diagnostics, TimeProvider? timeProvider = null) =>
        new(PowerPlatformApiTargetConfig.Public, client, ApiKey, ApiKey,
            diagnostics: diagnostics, timeProvider: timeProvider);

    private static TokenAcquisitionResult Token(string? tenant = null) => new()
    {
        AccessToken = FakeJwt.Create(tenantId: tenant ?? EnvironmentFixtures.ContosoTenant),
        TenantId = tenant ?? EnvironmentFixtures.ContosoTenant,
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
    };

    private sealed class GeographyClient(Func<CancellationToken, Task<string?>> read) : IEnvironmentGeographyClient
    {
        internal int Calls { get; private set; }
        public Task<string?> GetGeographyAsync(ResolvedEnvironmentTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            return read(cancellationToken);
        }
    }

    private sealed class Tokens(TokenAcquisitionResult? token) : ISelectedAccountTokenProvider
    {
        public Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(string resource, string targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(token);
    }

    private sealed class ThrowingDiagnostics : IEnvironmentGeographyDiagnostics
    {
        internal int Calls { get; private set; }
        public void Report(EnvironmentGeographyStage stage, int? httpStatusCode = null)
        {
            Calls++;
            throw new InvalidOperationException("Observer unavailable.");
        }
    }

    private sealed class ThrowingWriter : StringWriter
    {
        internal int Calls { get; private set; }
        public override void WriteLine(string? value)
        {
            Calls++;
            throw new IOException("Writer unavailable.");
        }
    }
}
