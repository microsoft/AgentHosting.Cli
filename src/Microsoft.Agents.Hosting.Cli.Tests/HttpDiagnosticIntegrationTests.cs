using System.Net;
using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

[CollectionDefinition(nameof(HttpDiagnosticConsoleCollection), DisableParallelization = true)]
public sealed class HttpDiagnosticConsoleCollection;

[Collection(nameof(HttpDiagnosticConsoleCollection))]
public sealed class HttpDiagnosticIntegrationTests
{
    [Fact]
    public void DisabledDiagnosticsReturnTheOriginalTransport()
    {
        using var transport = TransportDefaults.CreateRedirectRejectingHandler();
        Assert.Same(transport, HttpDiagnostics.Disabled.Wrap(transport));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnablingDiagnosticsPreservesTlsAndRedirectSettings(bool redirects)
    {
        var transport = redirects ? TransportDefaults.CreateRedirectFollowingHandler() : TransportDefaults.CreateRedirectRejectingHandler();
        using var output = new StringWriter();
        using var traced = new HttpDiagnostics(new TextWriterHttpDiagnosticSink(output)).Wrap(transport);

        Assert.Same(transport, Assert.IsType<HttpDiagnosticHandler>(traced).InnerHandler);
        Assert.Equal(redirects, transport.AllowAutoRedirect);
        Assert.Equal(TransportDefaults.EnabledSslProtocols, transport.SslProtocols);
    }

    [Fact]
    public async Task OutputFailuresRemainVisibleToTheHostWithoutFailingTheApiCall()
    {
        var failure = new IOException("diagnostic destination unavailable");
        var diagnostics = new HttpDiagnostics(new CallbackSink(_ => throw failure));
        using var client = new HttpClient(diagnostics.Wrap(new CallbackHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unchanged") }))));

        using var response = await client.GetAsync("https://example.test/");

        Assert.Equal("unchanged", await response.Content.ReadAsStringAsync());
        Assert.Same(failure, diagnostics.DiagnosticWriteError);

        using var output = new StringWriter();
        WriteDiagnosticErrorWithConsole(output, diagnostics);

        Assert.Equal("ERROR: Debug output is incomplete. IOException: diagnostic destination unavailable" + Environment.NewLine,
            output.ToString());
    }

    [Fact]
    public void NoDiagnosticFailureProducesNoErrorOutput()
    {
        using var output = new StringWriter();

        WriteDiagnosticErrorWithConsole(output, new HttpDiagnostics());

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task AnUnavailableErrorDestinationDoesNotPreventHttpAndCanBeReportedAfterRecovery()
    {
        using var unavailable = new UnavailableWriter();
        var diagnostics = new HttpDiagnostics(new TextWriterHttpDiagnosticSink(unavailable));
        using var client = new HttpClient(diagnostics.Wrap(new CallbackHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unchanged") }))));
        using var response = await client.GetAsync("https://example.test/");
        Assert.Equal("unchanged", await response.Content.ReadAsStringAsync());
        Assert.IsType<IOException>(diagnostics.DiagnosticWriteError);
        WriteDiagnosticErrorWithConsole(unavailable, diagnostics);

        using var recovered = new StringWriter();
        WriteDiagnosticErrorWithConsole(recovered, diagnostics);

        Assert.Equal("ERROR: Debug output is incomplete. IOException: output unavailable" + Environment.NewLine,
            recovered.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MsalKeepsItsOwnHttpFactoryWhileDebugTextLoggingRemainsEnabled(bool debug)
    {
        var events = new List<HttpDiagnosticEvent>();
        var diagnostics = new HttpDiagnostics(new CallbackSink(events.Add));
        using var directory = new TempDir();
        using var output = new StringWriter();
        using var provider = new MsalPublicClientProvider(
            new AgentHostAuthOptions
            {
                ConfigDirectory = directory.Path,
                AllowUnprotectedCache = true,
                DebugOutput = debug ? output : null,
            },
            FakePlatformInfo.Linux(), new FakeSecureTokenCacheProbe(false), httpDiagnostics: debug ? diagnostics : null);
        var app = await provider.GetClientAsync(false, null, CancellationToken.None);

        Assert.Null(app.AppConfig.HttpClientFactory);
        if (debug)
        {
            Assert.NotNull(app.AppConfig.LoggingCallback);
            app.AppConfig.LoggingCallback(LogLevel.Info, "MSAL's own HTTP diagnostic message", false);
            Assert.Equal("MSAL's own HTTP diagnostic message" + Environment.NewLine, output.ToString());
        }
        else
        {
            Assert.Null(app.AppConfig.LoggingCallback);
            Assert.Equal(string.Empty, output.ToString());
        }

        Assert.Empty(events);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProfileMsalBuildersKeepSdkHttpAndInvocationDebugLogging(bool debug, bool confidential)
    {
        using var output = new StringWriter();
        var diagnostics = new HttpDiagnostics();
        var services = new ServiceCollection();
        Program.ConfigureProfileAuthentication(services,
            new AgentHostAuthOptions { ConfigDirectory = Path.GetTempPath(), DebugOutput = debug ? output : null },
            PowerPlatformApiTargetConfig.Public, _ => null,
            createOwnedStorage: () => throw new InvalidOperationException("Native storage must not be constructed."),
            createLegacyCache: () => throw new InvalidOperationException("Legacy storage must not be constructed."),
            httpDiagnostics: diagnostics);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IProfileMsalClientFactory>();
        var request = ProfileCredentialSdkTests.Request(confidential ? AuthCredentialMethod.ClientSecret : AuthCredentialMethod.Browser,
            secret: confidential ? "test-secret" : null);
        IClientApplicationBase app = confidential ? factory.CreateConfidential(request, null) : factory.CreatePublic(request, broker: false);

        Assert.Null(app.AppConfig.HttpClientFactory);
        if (debug)
        {
            Assert.NotNull(app.AppConfig.LoggingCallback);
            app.AppConfig.LoggingCallback(LogLevel.Warning, "profile SDK diagnostic", false);
            Assert.Equal("profile SDK diagnostic" + Environment.NewLine, output.ToString());
        }
        else
        {
            Assert.Null(app.AppConfig.LoggingCallback);
            Assert.Equal(string.Empty, output.ToString());
        }
    }

    [Fact]
    public void TheUpdateClientExcludesHttpDiagnosticsWithoutChangingTransportSettings()
    {
        using var client = Program.CreateUpdateHttpClient();
        var handler = Assert.IsType<HttpClientHandler>(Field<HttpMessageHandler>(client));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.Equal(TransportDefaults.EnabledSslProtocols, handler.SslProtocols);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllAuthRegisteredRestClientsUseInvocationDiagnostics(bool debug)
    {
        var events = new List<HttpDiagnosticEvent>();
        var diagnostics = StopBeforeTransport(events);
        var services = new ServiceCollection();
        Program.ConfigureProfileAuthentication(services,
            new AgentHostAuthOptions { ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) },
            PowerPlatformApiTargetConfig.Public, _ => null,
            createOwnedStorage: () => throw new InvalidOperationException("Native storage must not be constructed."),
            createLegacyCache: () => throw new InvalidOperationException("Legacy storage must not be constructed."),
            httpDiagnostics: debug ? diagnostics : null);
        using var provider = services.BuildServiceProvider();
        object[] adapters =
        [
            provider.GetRequiredService<IAbsStateReader>(),
            provider.GetRequiredService<IDeploymentTransport>(),
            provider.GetRequiredService<IDeploymentOAuthTransport>(),
            provider.GetRequiredService<IDeploymentChannelTransport>(),
            provider.GetRequiredService<IIdentityRestClient>(),
            Field<IIdentityRestClient>(Field<IdentityGraphClient>(provider.GetRequiredService<AppRegistrationIdentityService>())),
        ];

        foreach (var adapter in adapters)
        {
            await VerifyRegisteredClientAsync(Field<HttpClient>(adapter), debug);
        }
        await VerifyRegisteredClientAsync(provider.GetRequiredKeyedService<HttpClient>("profile-environments"), debug);
        await VerifyRegisteredClientAsync(provider.GetRequiredKeyedService<HttpClient>("profile-geography"), debug);

        Assert.Equal(debug ? adapters.Length + 2 : 0, events.Count(e => e.Stage == HttpDiagnosticStage.Request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotRegistrationsTraceRepositoryRequestsButNotExternalSdkRequests(bool debug)
    {
        var events = new List<HttpDiagnosticEvent>();
        var diagnostics = StopBeforeTransport(events);
        var services = new ServiceCollection();
        services.AddSingleton<ITokenProvider>(new RecordingTokenProvider());
        services.AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiRingSelection.Default, httpDiagnostics: debug ? diagnostics : null);
        using var provider = services.BuildServiceProvider();
        object[] adapters =
        [
            provider.GetRequiredService<IAgentSharingPort>(),
            provider.GetRequiredService<IAgentEvaluationPort>(),
            Field<PowerAppsConnectionClient>(provider.GetRequiredService<IConnectionLifecyclePort>()),
        ];

        foreach (var adapter in adapters)
        {
            using var client = new HttpClient(Field<Func<HttpMessageHandler>>(adapter)());
            await VerifyRegisteredClientAsync(client, debug);
        }
        await VerifyRegisteredClientAsync(Field<HttpClient>(provider.GetRequiredService<IConnectionCatalogClient>()), debug: false);
        var accessor = provider.GetRequiredService<AgentHostDataverseHttpClientAccessor>();
        var environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "environment",
            TenantId = string.Empty,
            DataverseOrganization = new Uri("https://example.test/"),
        };
        using (var repositoryClient = accessor.CreateClient(environment))
        {
            await VerifyRegisteredClientAsync(repositoryClient, debug);
        }
        var syncAccessor = provider.GetRequiredService<IDataverseHttpClientAccessor>();
        Assert.Same(accessor, syncAccessor);
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, null, null));
        using var syncClient = syncAccessor.CreateClient();
        await VerifyRegisteredClientAsync(syncClient, debug: false);

        Assert.Equal(debug ? adapters.Length + 1 : 0, events.Count(e => e.Stage == HttpDiagnosticStage.Request));
    }

    private static HttpDiagnostics StopBeforeTransport(List<HttpDiagnosticEvent> events) =>
        new(new CallbackSink(e =>
        {
            events.Add(e);
            if (e.Stage == HttpDiagnosticStage.RequestBody)
            {
                throw new StopBeforeTransportException();
            }
        }));

    private static async Task VerifyRegisteredClientAsync(HttpClient client, bool debug)
    {
        if (debug)
        {
            await Assert.ThrowsAsync<StopBeforeTransportException>(() => client.GetAsync("https://example.test/registered"));
        }
        else
        {
            var handler = Field<HttpMessageHandler>(client);
            while (handler is DelegatingHandler { InnerHandler: { } inner })
            {
                handler = inner;
            }
            Assert.IsType<HttpClientHandler>(handler);
        }
    }

    private static T Field<T>(object instance) where T : class
    {
        var values = new List<T>();
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            values.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(field => field.GetValue(instance)).OfType<T>());
        }
        return Assert.Single(values);
    }

    private sealed class CallbackSink(Action<HttpDiagnosticEvent> write) : IHttpDiagnosticSink
    {
        public void Write(HttpDiagnosticEvent diagnosticEvent) => write(diagnosticEvent);
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class UnavailableWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("output unavailable");
        public override void WriteLine(string? value) => throw new IOException("output unavailable");
    }

    private static void WriteDiagnosticErrorWithConsole(TextWriter output, HttpDiagnostics diagnostics)
    {
        var originalError = Console.Error;
        try
        {
            Console.SetError(output);
            Program.WriteDiagnosticError(diagnostics);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private sealed class StopBeforeTransportException : Exception;
}
