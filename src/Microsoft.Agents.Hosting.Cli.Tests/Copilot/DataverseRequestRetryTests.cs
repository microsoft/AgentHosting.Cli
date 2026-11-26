using System.Net;
using System.Net.Http.Headers;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataverseRequestRetryTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static HttpResponseMessage Throttled()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);

        return response;
    }

    private static AgentHostDataverseHttpClientAccessor Create(CapturingPackageHandler handler)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        return accessor;
    }

    [Fact]
    public async Task AThrottledRequestIsSentAgainWithTheTokenUnderOneClientRequestId()
    {
        var attempts = 0;
        var handler = new CapturingPackageHandler(_ => ++attempts == 1 ? Throttled() : new HttpResponseMessage(HttpStatusCode.OK));
        using var accessor = Create(handler);
        using var client = accessor.CreateClient();

        using var response = await client.GetAsync(new Uri($"{Organization}api/data/v9.2/WhoAmI"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.True(request.HasAuthorization));
        Assert.True(Guid.TryParse(Assert.Single(handler.Requests.Select(request => request.ClientRequestId).Distinct()), out _));
    }

    [Fact]
    public async Task ARequestTheSyncLibraryRetriesIsNotRetriedAgainByTheAccessor()
    {
        var handler = new CapturingPackageHandler(_ => Throttled());
        using var accessor = Create(handler);
        var client = new SyncDataverseClient(accessor);
        client.SetDataverseUrl(Organization);

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => client.ListWorkflowsAsync(null, null, CancellationToken.None));

        Assert.Equal((HttpStatusCode)429, failure.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(request => request.ClientRequestId).Distinct());
    }

    [Fact]
    public async Task DiagnosticsObserveEachRetryWithoutTakingOwnershipOfTheSharedTransport()
    {
        var attempts = 0;
        var handler = new CapturingPackageHandler(_ => ++attempts == 1 ? Throttled() : new HttpResponseMessage(HttpStatusCode.OK));
        using var output = new StringWriter();
        var diagnostics = new HttpDiagnostics(new TextWriterHttpDiagnosticSink(output));
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler, diagnostics);
        var environment = new CopilotEnvironmentContext { EnvironmentId = "environment", TenantId = string.Empty, DataverseOrganization = new Uri(Organization) };
        using (var client = accessor.CreateClient(environment))
        using (var response = await client.GetAsync(new Uri($"{Organization}api/data/v9.2/WhoAmI")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var client = accessor.CreateClient(environment))
        using (var response = await client.GetAsync(new Uri($"{Organization}api/data/v9.2/WhoAmI")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, output.ToString().Split("Request: GET ", StringSplitOptions.None).Length - 1);
        Assert.Contains("Status: 429", output.ToString());
        Assert.Contains("Authorization: *****", output.ToString());
    }
}
