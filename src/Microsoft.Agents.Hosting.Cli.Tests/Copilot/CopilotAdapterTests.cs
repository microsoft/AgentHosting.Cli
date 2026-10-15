using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
}
