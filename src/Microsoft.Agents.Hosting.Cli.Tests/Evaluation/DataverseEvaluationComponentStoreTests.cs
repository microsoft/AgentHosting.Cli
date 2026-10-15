using System.Net;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class DataverseEvaluationComponentStoreTests
{
    private const string Organization = "https://org.crm.dynamics.com/";

    private static CopilotEnvironmentContext Context() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri(Organization),
    };

    private static (DataverseEvaluationComponentStore Store, RecordingDeleteHandler Handler) Create()
    {
        var handler = new RecordingDeleteHandler();
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        return (new DataverseEvaluationComponentStore(new DataverseWebApiClient(new StubSolutionVersionsClient(), accessor)), handler);
    }

    [Fact]
    public async Task DeleteRemovesTheBotComponentRowSoItsChildrenCascade()
    {
        var (store, handler) = Create();

        await store.DeleteAsync(Context(), "5abcfcf7-0000-0000-0000-000000000000", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{Organization}api/data/v9.2/botcomponents(5abcfcf7-0000-0000-0000-000000000000)", request.Url);
    }

    [Fact]
    public async Task DeleteNeverReachesTheEvaluationsApi()
    {
        var (store, handler) = Create();

        await store.DeleteAsync(Context(), "testset-1", CancellationToken.None);

        Assert.DoesNotContain("makerevaluations", Assert.Single(handler.Requests).Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteEscapesTheComponentId()
    {
        var (store, handler) = Create();

        await store.DeleteAsync(Context(), "a b", CancellationToken.None);

        Assert.Contains("botcomponents(a%20b)", Assert.Single(handler.Requests).Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteRefusesAnEmptyComponentId(string componentId)
    {
        var (store, handler) = Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(Context(), componentId, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private sealed record RecordedDelete(HttpMethod Method, string Url);

    private sealed class RecordingDeleteHandler : HttpMessageHandler
    {
        public List<RecordedDelete> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedDelete(request.Method, request.RequestUri!.AbsoluteUri));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") });
        }
    }
}
