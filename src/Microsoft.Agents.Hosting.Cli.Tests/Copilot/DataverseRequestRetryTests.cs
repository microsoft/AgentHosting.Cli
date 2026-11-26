using System.Net;
using System.Net.Http.Headers;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
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
    public async Task SessionCorrelationSurvivesPinnedSyncRetryAndPreservesExistingRequestId()
    {
        var id = Guid.NewGuid();
        var requestId = Guid.NewGuid().ToString("D");
        var context = new InvocationContext { SessionId = id };
        var sessions = new List<string?>();
        var handler = new CapturingPackageHandler(request =>
        {
            sessions.Add(request.Headers.TryGetValues("x-ms-client-session-id", out var values) ? Assert.Single(values) : null);
            return sessions.Count == 1 ? Throttled() : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler, context);
        accessor.SetDataverseOrganization(new Uri(Organization));
        using var client = accessor.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Organization}api/data/v9.2/WhoAmI");
        request.Headers.Add("x-ms-client-request-id", requestId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, value => Assert.Equal(id.ToString("D"), value));
        Assert.All(handler.Requests, value => Assert.Equal(requestId, value.ClientRequestId));
    }

    [Fact]
    public async Task SharedClientUsesAsyncInvocationContextWithoutDefaultHeaders()
    {
        var context = new InvocationContext();
        var received = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CorrelationHandler(async request =>
        {
            received[request.RequestUri!.AbsolutePath] =
                request.Headers.TryGetValues("x-ms-client-session-id", out var values) ? Assert.Single(values) : null;
            if (received.Count == 2)
            {
                bothEntered.TrySetResult();
            }
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler, context);
        accessor.SetDataverseOrganization(new Uri(Organization));
        using var client = accessor.CreateClient();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        async Task SendAsync(string path, Guid id)
        {
            context.SessionId = id;
            using var response = await client.GetAsync(Organization + path);
        }

        var first = SendAsync("first", firstId);
        var second = SendAsync("second", secondId);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
        Assert.Equal(firstId.ToString("D"), received["/first"]);
        Assert.Equal(secondId.ToString("D"), received["/second"]);
        Assert.False(client.DefaultRequestHeaders.Contains("x-ms-client-session-id"));
        Assert.Null(context.SessionId);
    }

    [Fact]
    public async Task SuppressionDoesNotAddSessionHeadersOrRemoveOperationalRequestIds()
    {
        var context = new InvocationContext();
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler, context);
        accessor.SetDataverseOrganization(new Uri(Organization));
        using var client = accessor.CreateClient();
        using var response = await client.GetAsync(Organization);

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("x-ms-client-session-id", request.HeaderNames);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
    }

    [Fact]
    public async Task GatewayDoesNotReceiveDataverseSessionHeader()
    {
        var context = new InvocationContext { SessionId = Guid.NewGuid() };
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler, context);
        using var target = DataverseOperationScope.Push(null);
        using var client = accessor.CreateGatewayClient(
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public),
            Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory.Prod);
        using var response = await client.GetAsync("https://gateway.island.powerapps.com/");

        Assert.DoesNotContain("x-ms-client-session-id", Assert.Single(handler.Requests).HeaderNames);
    }

    private sealed class InvocationContext : ICommandInvocationContext
    {
        private readonly AsyncLocal<Guid?> _current = new();

        public Guid? SessionId { get => _current.Value; set => _current.Value = value; }
    }

    private sealed class CorrelationHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }

    [Fact]
    public async Task DiagnosticsObserveEachRetryWithoutTakingOwnershipOfTheSharedTransport()
    {
        var attempts = 0;
        var handler = new CapturingPackageHandler(_ => ++attempts == 1 ? Throttled() : new HttpResponseMessage(HttpStatusCode.OK));
        using var output = new StringWriter();
        var diagnostics = new HttpDiagnostics(new TextWriterHttpDiagnosticSink(output));
        var context = new InvocationContext { SessionId = Guid.NewGuid() };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler,
            context, httpDiagnostics: diagnostics);
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
        Assert.All(handler.Requests, request => Assert.Contains("x-ms-client-session-id", request.HeaderNames));
        Assert.Equal(3, output.ToString().Split("Request: GET ", StringSplitOptions.None).Length - 1);
        Assert.Contains("Status: 429", output.ToString());
        Assert.Contains("Authorization: *****", output.ToString());
    }
}
