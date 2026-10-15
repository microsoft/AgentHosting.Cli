using System.Net;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<string> Requests { get; } = [];

    public ScriptedHttpMessageHandler Respond(HttpStatusCode status, string? body = null)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json"),
        });

        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");

        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK));
    }
}

public sealed class DataverseCatalogAdapterTests
{
    private const string AgentId = "5bef50e8-15aa-f111-aaab-7ced8d702753";

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static DataverseCatalogAdapter Create(ScriptedHttpMessageHandler handler)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri("https://contoso.crm.dynamics.com/"));

        return new DataverseCatalogAdapter(new StubSolutionVersionsClient(), accessor, (_, _) => Task.CompletedTask);
    }

    private static DataverseCatalogAdapter Create(ScriptedHttpMessageHandler handler, List<TimeSpan> waits)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri("https://contoso.crm.dynamics.com/"));

        return new DataverseCatalogAdapter(new StubSolutionVersionsClient(), accessor, (interval, _) =>
        {
            waits.Add(interval);
            return Task.CompletedTask;
        });
    }

    private const string StillReferencedBody = """{"error":{"code":"0x8004f01f","message":"The bot component cannot be deleted because it is referenced by 14 other components."}}""";

    private const string MissingRelatedEntityBody = """{"error":{"code":"0x80040216","message":"There should be at least one entity returned for EntityName: botcomponent_msdyn_aimodel, Id: bfbbbc18-89af-f111-aaab-7ced8d71328f"}}""";

    [Fact]
    public async Task DeleteAgent_DeletesEveryComponentBeforeTheAgent()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botcomponentid":"c1"},{"botcomponentid":"c2"}]}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.Equal(
            [
                $"GET /api/data/v9.2/botcomponents?$select=botcomponentid&$filter=_parentbotid_value eq {AgentId}",
                "DELETE /api/data/v9.2/botcomponents(c1)",
                "DELETE /api/data/v9.2/botcomponents(c2)",
                $"DELETE /api/data/v9.2/bots({AgentId})",
            ],
            handler.Requests.Select(request => Uri.UnescapeDataString(request)));
    }

    [Fact]
    public async Task DeleteAgent_WithNoComponents_DeletesOnlyTheAgent()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith($"bots({AgentId})", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAgent_FollowsEveryPageOfComponents()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botcomponentid":"c1"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/botcomponents?$skiptoken=page2"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botcomponentid":"c2"}]}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Contains("skiptoken=page2", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Contains("botcomponents(c1)", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Contains("botcomponents(c2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteAgent_DeletesTheBotOnlyAfterEveryComponent()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botcomponentid":"c1"},{"botcomponentid":"c2"},{"botcomponentid":"c3"}]}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.EndsWith($"bots({AgentId})", handler.Requests[^1], StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count(request => request.Contains("botcomponents(", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ListAgents_WithAMaximumCount_AsksTheServiceForIt()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        await Create(handler).ListAgentsAsync(Environment(), 5, CancellationToken.None);

        Assert.Contains("$top=5", Uri.UnescapeDataString(Assert.Single(handler.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAgents_WithoutAMaximumCount_AsksForEveryRow()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        await Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None);

        Assert.DoesNotContain("$top", Uri.UnescapeDataString(Assert.Single(handler.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAgents_StopsOnceTheMaximumCountIsReached()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"},{"botid":"b2","schemaname":"b_two"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=page2"}""");

        var agents = await Create(handler).ListAgentsAsync(Environment(), 2, CancellationToken.None);

        Assert.Equal(2, agents.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListAgents_FollowsEveryPageWhenUnbounded()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=page2"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b2","schemaname":"b_two"}]}""");

        var agents = await Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None);

        Assert.Equal(["a_one", "b_two"], agents.Select(agent => agent.SchemaName));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DeleteAgent_WhenTheReferenceNeverClears_ReportsWhatDataverseSaid()
    {
        var handler = new ScriptedHttpMessageHandler();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            handler.Respond(HttpStatusCode.OK, """{"value":[]}""").Respond(HttpStatusCode.BadRequest, StillReferencedBody);
        }

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None));

        Assert.Contains("HTTP 400", failure.Message, StringComparison.Ordinal);
        Assert.Contains("referenced by 14 other components", failure.Message, StringComparison.Ordinal);
        Assert.Contains("0x8004f01f", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAgent_RetriesWhileTheAgentIsStillReferenced()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.BadRequest, StillReferencedBody)
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task DeleteAgent_RetriesWhenARelatedEntityHasAlreadyGone()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.BadRequest, MissingRelatedEntityBody)
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task DeleteAgent_ReReadsTheComponentListOnEveryRetry()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botcomponentid":"c1"}]}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.BadRequest, StillReferencedBody)
            .Respond(HttpStatusCode.OK, """{"value":[{"botcomponentid":"c2"}]}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent);

        await Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        var requests = handler.Requests.Select(Uri.UnescapeDataString).ToArray();
        Assert.Equal(2, requests.Count(request => request.StartsWith("GET /api/data/v9.2/botcomponents?", StringComparison.Ordinal)));
        Assert.Contains("DELETE /api/data/v9.2/botcomponents(c2)", requests);
    }

    [Fact]
    public async Task DeleteAgent_WaitsBetweenRetries()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.BadRequest, StillReferencedBody)
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.NoContent);

        await Create(handler, waits).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(15)], waits);
    }

    [Fact]
    public async Task DeleteAgent_DoesNotRetryAFailureThatIsNotADependencyConflict()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler, waits).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None));

        Assert.Empty(waits);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DeleteAgent_WhenTheFailureHasNoBody_StillReportsTheStatus()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.Forbidden);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None));

        Assert.Contains("HTTP 403", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAgent_WhenTheFailureBodyIsNotJson_StillReportsTheStatus()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[]}""")
            .Respond(HttpStatusCode.InternalServerError, "<html>gateway</html>");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).DeleteAgentAsync(Environment(), AgentId, CancellationToken.None));

        Assert.Contains("HTTP 500", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("html", failure.Message, StringComparison.Ordinal);
    }
}
