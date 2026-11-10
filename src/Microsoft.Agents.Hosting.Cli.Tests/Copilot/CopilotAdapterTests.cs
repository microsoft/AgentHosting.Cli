using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class RecordingTokenProvider : ITokenProvider
{
    public List<string> RequestedResources { get; } = [];

    public List<string?> RequestedTargetTenants { get; } = [];

    public string AccessToken { get; init; } = "fake-access-token";

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken)
    {
        RequestedResources.Add(resource);
        RequestedTargetTenants.Add(targetTenantId);
        return Task.FromResult(new TokenAcquisitionResult { AccessToken = AccessToken });
    }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken)
    {
        RequestedResources.AddRange(scopes);
        RequestedTargetTenants.Add(targetTenantId);
        return Task.FromResult(new TokenAcquisitionResult { AccessToken = AccessToken });
    }
}

internal sealed class StubSyncAuthProvider : ISyncAuthProvider
{
    public List<Uri> RequestedAudiences { get; } = [];

    public string AccessToken { get; init; } = "fake-access-token";

    public Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        RequestedAudiences.Add(audience);
        return Task.FromResult(AccessToken);
    }
}

internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    public List<AuthenticationHeaderValue?> CapturedAuthorizationHeaders { get; } = [];

    public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

    public string? Body { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedAuthorizationHeaders.Add(request.Headers.Authorization);
        Requests.Add((request.Method, request.RequestUri!));

        return Task.FromResult(Body is null
            ? new HttpResponseMessage(Status)
            : new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}

public sealed class AgentHostSyncAuthProviderTests
{
    private static readonly string[] ContosoResource = ["https://contoso.crm.dynamics.com/"];

    private static readonly string[] ContosoThenIslandResources =
    [
        "https://contoso.crm.dynamics.com/",
        "https://powervamg.us-il301.gateway.prod.island.powerapps.com/",
    ];
    [Fact]
    public async Task AcquireToken_RequestsTheDataverseOrganizationAsTheResource()
    {
        var tokenProvider = new RecordingTokenProvider();
        var provider = new AgentHostSyncAuthProvider(tokenProvider);

        await provider.AcquireTokenAsync(new Uri("https://contoso.crm.dynamics.com/"), CancellationToken.None);

        Assert.Equal(ContosoResource, tokenProvider.RequestedResources.ToArray());
    }

    [Fact]
    public async Task AcquireToken_PassesTheRequestedAudienceThroughRatherThanAFixedOne()
    {
        var tokenProvider = new RecordingTokenProvider();
        var provider = new AgentHostSyncAuthProvider(tokenProvider);

        await provider.AcquireTokenAsync(new Uri("https://contoso.crm.dynamics.com/"), CancellationToken.None);
        await provider.AcquireTokenAsync(new Uri("https://powervamg.us-il301.gateway.prod.island.powerapps.com/"), CancellationToken.None);

        Assert.Equal(
            ContosoThenIslandResources,
            tokenProvider.RequestedResources.ToArray());
    }

    [Fact]
    public async Task AcquireToken_AcquiresSilentlyWithoutTargetingATenant()
    {
        var tokenProvider = new RecordingTokenProvider();
        var provider = new AgentHostSyncAuthProvider(tokenProvider);

        await provider.AcquireTokenAsync(new Uri("https://contoso.crm.dynamics.com/"), CancellationToken.None);

        Assert.Equal(new string?[] { null }, tokenProvider.RequestedTargetTenants.ToArray());
    }

    [Fact]
    public async Task AcquireToken_ReturnsTheAccessTokenValue()
    {
        var provider = new AgentHostSyncAuthProvider(new RecordingTokenProvider { AccessToken = "issued-token" });

        var token = await provider.AcquireTokenAsync(new Uri("https://contoso.crm.dynamics.com/"), CancellationToken.None);

        Assert.Equal("issued-token", token);
    }

    [Fact]
    public async Task AcquireToken_WithoutAnAudience_Throws()
    {
        var provider = new AgentHostSyncAuthProvider(new RecordingTokenProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.AcquireTokenAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("api://a522f059-bb65-47c0-8934-7db6e5286414", "api://a522f059-bb65-47c0-8934-7db6e5286414/user_impersonation")]
    [InlineData("api://96ff4394-9197-43aa-b393-6a41652e21f8/", "api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation")]
    public async Task AcquireToken_AsksForAnApplicationAudiencesUserImpersonationPermission(string audience, string expectedScope)
    {
        var tokenProvider = new RecordingTokenProvider();

        await new AgentHostSyncAuthProvider(tokenProvider).AcquireTokenAsync(new Uri(audience), CancellationToken.None);

        Assert.Equal([expectedScope], tokenProvider.RequestedResources);
    }
}

public sealed class StandardErrorSyncProgressTests
{
    [Fact]
    public void Report_WritesToStandardError()
    {
        var standardError = new StringWriter();
        var progress = new StandardErrorSyncProgress(standardError);

        progress.Report("cloning components");

        Assert.Contains("cloning components", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_WritesNothingToStandardOutput()
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var originalOutput = Console.Out;
        Console.SetOut(standardOutput);
        try
        {
            new StandardErrorSyncProgress(standardError).Report("cloning components");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.Equal(string.Empty, standardOutput.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Report_IgnoresBlankMessages(string message)
    {
        var standardError = new StringWriter();

        new StandardErrorSyncProgress(standardError).Report(message);

        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void Report_WhenNotVerbose_WritesNothing()
    {
        var standardError = new StringWriter();

        new StandardErrorSyncProgress(standardError, verbose: false)
            .Report("Could not list connections for connector 'shared_onedriveforbusiness': Connections request failed (404).");

        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void Report_WhenVerbose_WritesTheServiceDiagnostic()
    {
        var standardError = new StringWriter();

        new StandardErrorSyncProgress(standardError, verbose: true)
            .Report("Could not list connections for connector 'shared_teams': Connections request failed (404).");

        Assert.Contains("Could not list connections", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_WhenNotVerbose_StillWritesAFailureTheLibrarySwallowed()
    {
        var standardError = new StringWriter();

        new StandardErrorSyncProgress(standardError, verbose: false)
            .Report("Failed to push custom connector 'shared_contoso': Connector request failed (409).");

        Assert.Contains("Failed to push custom connector", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANullWriterIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new StandardErrorSyncProgress(null!));
    }
}

public sealed class AgentHostDataverseHttpClientAccessorTests
{
    [Fact]
    public void CreateClient_BeforeTheOrganizationIsSet_Throws()
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());

        Assert.Throws<InvalidOperationException>(() => accessor.CreateClient());
    }

    [Fact]
    public async Task CreateClient_AttachesABearerTokenForTheConfiguredOrganization()
    {
        var authProvider = new StubSyncAuthProvider { AccessToken = "issued-token" };
        var innerHandler = new CapturingHttpMessageHandler();
        var accessor = new AgentHostDataverseHttpClientAccessor(authProvider, () => innerHandler);
        accessor.SetDataverseOrganization(new Uri("https://contoso.crm.dynamics.com/"));

        using var client = accessor.CreateClient();
        await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api/data/v9.2/bots"), CancellationToken.None);

        var header = Assert.Single(innerHandler.CapturedAuthorizationHeaders);
        Assert.Equal("Bearer", header?.Scheme);
        Assert.Equal("issued-token", header?.Parameter);
        Assert.Equal(new[] { new Uri("https://contoso.crm.dynamics.com/") }, authProvider.RequestedAudiences.ToArray());
    }

    [Fact]
    public void CreateRedirectRejectingHandler_DoesNotFollowRedirects()
    {
        using var handler = AgentHostDataverseHttpClientAccessor.CreateRedirectRejectingHandler();

        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public void SetDataverseOrganization_WithoutAnOrganization_Throws()
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());

        Assert.Throws<ArgumentNullException>(() => accessor.SetDataverseOrganization(null!));
    }

    [Fact]
    public void Constructor_WithoutATransportFactory_Throws()
    {
        Assert.Equal("innerHandlerFactory", Assert.Throws<ArgumentNullException>(() => new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), null!)).ParamName);
    }

    [Theory]
    [InlineData("https://evil.example.com:8443/api/data/v9.2/bots?$top=1", "https://evil.example.com:8443")]
    [InlineData(null, "(none)")]
    public void Refusal_NamesTheRequestedOriginAndTheOrganizationsOrigin(string? requestUri, string requestedOrigin)
    {
        var refusal = AgentHostDataverseHttpClientAccessor.Refusal(requestUri is null ? null : new Uri(requestUri), new Uri("https://contoso.crm.dynamics.com/api/data/v9.2/"));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, refusal.ErrorKind);
        Assert.Equal($"A Dataverse request targeted '{requestedOrigin}' but the credential is bound to 'https://contoso.crm.dynamics.com'.", refusal.Message);
    }
}

public sealed class DataverseWebApiClientTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri(Organization),
    };

    private static DataverseWebApiClient Create(ScriptedHttpMessageHandler handler)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        return new DataverseWebApiClient(new StubSolutionVersionsClient(), accessor);
    }

    [Fact]
    public async Task Send_AFailedRequest_IsAServiceErrorThatNeverEchoesTheRawBody()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.Forbidden, """{"error":{"code":"0x80040220","message":"Principal user is missing prvReadBot privilege."},"trace":"sample.person@contoso.com"}""");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(handler).SendAsync(Environment(), HttpMethod.Get, "api/data/v9.2/bots", CancellationToken.None));

        Assert.Equal("The Copilot Studio service reported a failure during 'GET api/data/v9.2/bots'. HTTP 403 Forbidden. Principal user is missing prvReadBot privilege. (0x80040220)", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task ReadCollection_ARunOfPagesPastTheCap_IsRefusedNamingTheFirstPage()
    {
        var handler = new ScriptedHttpMessageHandler();

        for (var page = 1; page <= 1000; page++)
        {
            handler.Respond(HttpStatusCode.OK, $$"""{"value":[],"@odata.nextLink":"{{Organization}}api/data/v9.2/bots?$skiptoken={{page}}"}""");
        }

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(handler).ReadCollectionAsync(Environment(), "api/data/v9.2/bots", row => row.GetRawText(), CancellationToken.None));

        Assert.Equal("The Copilot Studio service reported a failure during 'GET api/data/v9.2/bots'. The service returned more than 1000 pages.", failure.Message);
        Assert.Equal(1000, handler.Requests.Count);
    }
}
