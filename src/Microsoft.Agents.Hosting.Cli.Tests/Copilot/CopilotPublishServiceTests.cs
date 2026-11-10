using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;
public sealed class CopilotPublishServiceTests
{
    private static readonly DateTimeOffset NewlyPublished = new(2026, 9, 2, 12, 5, 0, TimeSpan.Zero);

    private static CopilotCatalogService Create(FakeCopilotCatalogPort catalog, FakeCopilotPublishPort? publisher = null, FakeCopilotWorkspacePort? workspaces = null) =>
        new(
            catalog,
            publisher ?? new FakeCopilotPublishPort(),
            CopilotTestFixtures.Resolver(),
            CopilotTestFixtures.Locator(workspaces ?? new FakeCopilotWorkspacePort { WorkspaceExists = false }, new FakeCopilotFileSystem()));

    private static CopilotPublishRequest ForcedRequest() => new() { Bot = "agent-1", Environment = "env-1", Force = true };

    [Fact]
    public async Task Publish_WhenThePublishCannotBeRequested_LeavesTheRetryToTheSyncLibrary()
    {
        var publisher = new FakeCopilotPublishPort();
        publisher.ThrowsSequence.Enqueue(CopilotStudioException.ServiceError("publish agent", "HTTP 400 Bad Request."));

        Assert.Equal(CopilotErrorKinds.ServiceError, (await Assert.ThrowsAsync<CopilotStudioException>(() => Create(Catalog(), publisher).PublishAsync(ForcedRequest(), CancellationToken.None))).ErrorKind);
        Assert.Single(publisher.Requests);
    }

    [Fact]
    public async Task Publish_WaitsUpToFiveMinutesForCopilotStudioToFinish()
    {
        var publisher = new FakeCopilotPublishPort();

        await Create(Catalog(), publisher).PublishAsync(ForcedRequest(), CancellationToken.None);

        var request = Assert.Single(publisher.Requests);
        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("env-1", request.Environment.EnvironmentId);
        Assert.Equal(TimeSpan.FromMinutes(5), request.MaximumWait);
    }

    private static FakeCopilotCatalogPort Catalog() => new() { ResolveReturns = "agent-1" };

    [Fact]
    public async Task ResolveAgentId_WithAnUnknownValue_FailsWithAgentNotFound()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(new FakeCopilotCatalogPort { ResolveReturns = null })
                .ResolveAgentIdAsync(
                    new CopilotEnvironmentContext
                    {
                        EnvironmentId = "env-1",
                        TenantId = EnvironmentFixtures.ContosoTenant,
                        DataverseOrganization = new Uri(CopilotTestFixtures.DataverseOrganization),
                    },
                    "missing",
                    CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.AgentNotFound, failure.ErrorKind);
    }

    [Fact]
    public async Task Publish_WithoutForce_ReportsConfirmationRequiredAndDoesNotPublish()
    {
        var publisher = new FakeCopilotPublishPort();

        Assert.Equal("confirmation-required", (await Create(Catalog(), publisher).PublishAsync(new CopilotPublishRequest { Bot = "agent-1" }, CancellationToken.None)).Status);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Publish_WithoutForce_ReportsNoPublishTime()
    {
        var catalog = Catalog();
        catalog.PublishedOn = NewlyPublished;

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1" }, CancellationToken.None);

        Assert.Null(result.PublishedOn);
    }

    [Fact]
    public async Task Publish_WithForce_PublishesOnce()
    {
        var publisher = new FakeCopilotPublishPort();

        Assert.Equal("published", (await Create(Catalog(), publisher).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None)).Status);
        Assert.Single(publisher.Requests);
    }

    [Fact]
    public async Task Publish_ReportsWhenTheAgentWasPublished()
    {
        var catalog = Catalog();
        catalog.PublishedOn = NewlyPublished;

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal(NewlyPublished, result.PublishedOn);
    }

    [Fact]
    public async Task Publish_ReportsWhenCopilotStudioFinishedThePublish_WithoutReadingTheAgentAgain()
    {
        var finishedOn = NewlyPublished.AddSeconds(-3);
        var catalog = Catalog();
        catalog.PublishedOn = NewlyPublished;

        var result = await Create(catalog, new FakeCopilotPublishPort { FinishedOn = finishedOn }).PublishAsync(ForcedRequest(), CancellationToken.None);

        Assert.Equal(finishedOn, result.PublishedOn);
    }

    [Fact]
    public async Task Publish_WithAnUnresolvableBot_ThrowsAgentNotFound()
    {
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = null };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "missing", Force = true }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.AgentNotFound, failure.ErrorKind);
    }

    [Fact]
    public async Task Publish_WithBotArgument_DoesNotReadAWorkspace()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var result = await Create(Catalog(), workspaces: workspaces).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("bot-argument", result.ResolvedFrom);
        Assert.Equal("published", result.Status);
    }

    [Fact]
    public async Task Publish_WithoutBot_UsesTheConnectedWorkspace()
    {
        var result = await Create(Catalog(), workspaces: new FakeCopilotWorkspacePort { WorkspaceExists = true })
            .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None);

        Assert.Equal("workspace", result.ResolvedFrom);
        Assert.Equal("published", result.Status);
    }

    [Fact]
    public async Task Publish_WithoutBot_DoesNotResolveAnAgentIdentifier()
    {
        var catalog = Catalog();

        await Create(catalog, workspaces: new FakeCopilotWorkspacePort { WorkspaceExists = true })
            .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None);

        Assert.Empty(catalog.ResolvedValues);
    }

    [Fact]
    public async Task Publish_WithNeitherBotNorWorkspace_ThrowsWorkspaceNotFound()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Catalog(), workspaces: new FakeCopilotWorkspacePort { WorkspaceExists = false })
                .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceNotFound, failure.ErrorKind);
    }

    [Fact]
    public async Task Publish_WithNeitherBotNorWorkspace_DoesNotPublish()
    {
        var publisher = new FakeCopilotPublishPort();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Catalog(), publisher, new FakeCopilotWorkspacePort { WorkspaceExists = false })
                .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None));

        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Publish_ReportsTheAgentAndEnvironment()
    {
        var result = await Create(Catalog()).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("agent-1", result.AgentId);
        Assert.Equal("con_agent", result.SchemaName);
        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Publish_DoesNotWriteToTheWorkspace()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await Create(Catalog(), workspaces: workspaces).PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None);

        Assert.Equal(0, workspaces.PullCalls);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Fact]
    public async Task Publish_WhenItTimesOut_DoesNotWriteToTheWorkspace()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var publisher = new FakeCopilotPublishPort();
        publisher.ThrowsSequence.Enqueue(CopilotStudioException.PublishTimeout("agent-1", TimeSpan.FromMinutes(5)));

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Catalog(), publisher, workspaces).PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None));

        Assert.Equal(0, workspaces.PullCalls);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, workspaces.CloneCalls);
    }
}
