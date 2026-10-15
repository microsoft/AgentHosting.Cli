using System.Collections.Immutable;
using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CloneSourceResolutionTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static readonly string KnownAgentId = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString();

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

    [Fact]
    public async Task AnIdentifierTheServiceKnowsResolves()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, $$"""{"botid":"{{KnownAgentId}}"}""");

        var resolved = await Create(handler).ResolveAgentIdAsync(Environment(), KnownAgentId, CancellationToken.None);

        Assert.Equal(KnownAgentId, resolved);
    }

    [Fact]
    public async Task AnIdentifierTheServiceDoesNotKnowResolvesToNothing()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.NotFound, string.Empty);

        var resolved = await Create(handler).ResolveAgentIdAsync(Environment(), KnownAgentId, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task AnIdentifierIsCheckedAgainstTheServiceRatherThanReturnedUnread()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.NotFound, string.Empty);

        await Create(handler).ResolveAgentIdAsync(Environment(), KnownAgentId, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AServiceFailureIsNotReportedAsAnUnknownIdentifier()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.Unauthorized, """{"error":{"message":"token rejected"}}""");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ResolveAgentIdAsync(Environment(), KnownAgentId, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
    }
}
