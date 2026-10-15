using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataverseOriginTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri(Organization),
    };

    private static DataverseCatalogAdapter Create(ScriptedHttpMessageHandler handler)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        return new DataverseCatalogAdapter(new StubSolutionVersionsClient(), accessor);
    }

    [Theory]
    [InlineData("https://evil.example.com/api/data/v9.2/bots?$skiptoken=x")]
    [InlineData("http://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=x")]
    [InlineData("https://user:pass@contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=x")]
    public async Task ANextLinkThatLeavesTheOrganizationIsRefused(string nextLink)
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, $$"""{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"{{nextLink}}"}""");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, failure.ErrorKind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ANextLinkOnTheSameOriginIsFollowed()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p2"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b2","schemaname":"b_two"}]}""");

        var agents = await Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None);

        Assert.Equal(2, agents.Count);
    }

    [Fact]
    public async Task ANextLinkThatRepeatsItselfDoesNotLoopForever()
    {
        var handler = new ScriptedHttpMessageHandler();

        for (var page = 0; page < 3; page++)
        {
            handler.Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=same"}""");
        }

        await Assert.ThrowsAnyAsync<Exception>(() => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));
    }

    [Fact]
    public async Task AlternatingNextLinksAreRefusedRatherThanFollowedToThePageCap()
    {
        var handler = new ScriptedHttpMessageHandler();

        for (var page = 0; page < 12; page++)
        {
            var token = page % 2 == 0 ? "a" : "b";
            handler.Respond(HttpStatusCode.OK, $$"""{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken={{token}}"}""");
        }

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ALongerNextLinkCycleIsRefused()
    {
        var handler = new ScriptedHttpMessageHandler();

        foreach (var token in new[] { "a", "b", "c", "a", "b", "c" })
        {
            handler.Respond(HttpStatusCode.OK, $$"""{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken={{token}}"}""");
        }

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ANextLinkDifferingOnlyByTokenCaseIsFollowed()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=AbC"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b2","schemaname":"b_two"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=aBc"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b3","schemaname":"c_three"}]}""");

        var agents = await Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None);

        Assert.Equal(3, agents.Count);
    }

    [Fact]
    public async Task ANextLinkDifferingOnlyByHostCaseIsRefusedAsACycle()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p2"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b2","schemaname":"b_two"}],"@odata.nextLink":"https://CONTOSO.CRM.DYNAMICS.COM/api/data/v9.2/bots?$skiptoken=p2"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b3","schemaname":"c_three"}]}""");

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));
    }

    [Fact]
    public async Task DistinctNextLinksAreAllFollowed()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p2"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b2","schemaname":"b_two"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p3"}""")
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b3","schemaname":"c_three"}]}""");

        var agents = await Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None);

        Assert.Equal(3, agents.Count);
    }

    [Fact]
    public async Task ARequestToAnotherOriginNeverCarriesTheToken()
    {
        var handler = new ScriptedHttpMessageHandler();
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        using var client = accessor.CreateClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => client.GetAsync(new Uri("https://evil.example.com/api/data/v9.2/bots"), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, failure.ErrorKind);
        Assert.Empty(handler.Requests);
    }
}
