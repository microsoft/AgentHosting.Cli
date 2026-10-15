using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;
public sealed class CopilotPublishServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset PreviouslyPublished = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset NewlyPublished = new(2026, 9, 2, 12, 5, 0, TimeSpan.Zero);

    private static CopilotCatalogService Create(FakeCopilotCatalogPort catalog, FakeCopilotWorkspacePort? workspaces = null, FakeCopilotFileSystem? fileSystem = null, AdvancingClock? clock = null)
    {
        var files = fileSystem ?? new FakeCopilotFileSystem();

        return new CopilotCatalogService(
            catalog,
            CopilotTestFixtures.Resolver(),
            CopilotTestFixtures.Locator(workspaces ?? new FakeCopilotWorkspacePort { WorkspaceExists = false }, files),
            clock ?? new AdvancingClock(Start, TimeSpan.FromSeconds(1)),
            (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Publish_WhenTheServiceReportsAConflict_RetriesOnceAndSucceeds()
    {
        var catalog = Catalog();
        catalog.PublishThrowsSequence.Enqueue(CopilotStudioException.ServiceError("POST PvaPublish", "HTTP 400 Bad Request. {\"statuscode\":409,\"statusdescription\":\"Conflict\"}"));
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(Start);
        var delays = new List<TimeSpan>();

        var result = await CreateRecordingDelays(catalog, delays).PublishAsync(
            new CopilotPublishRequest { Bot = "agent-1", Environment = "env-1", Force = true },
            CancellationToken.None);

        Assert.Equal("published", result.Status);
        Assert.Equal(2, catalog.PublishCalls);
        Assert.Equal(TimeSpan.FromSeconds(5), delays[0]);
    }

    [Fact]
    public async Task Publish_WhenTheRetryAlsoFails_ReportsTheFailure()
    {
        var catalog = Catalog();
        catalog.PublishThrowsSequence.Enqueue(CopilotStudioException.ServiceError("POST PvaPublish", "conflict"));
        catalog.PublishThrowsSequence.Enqueue(CopilotStudioException.ServiceError("POST PvaPublish", "conflict"));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(catalog).PublishAsync(
            new CopilotPublishRequest { Bot = "agent-1", Environment = "env-1", Force = true },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
        Assert.Equal(2, catalog.PublishCalls);
    }

    [Fact]
    public async Task Publish_WhenTheAgentIsNotFound_IsNotRetried()
    {
        var catalog = Catalog();
        catalog.PublishThrowsSequence.Enqueue(CopilotStudioException.AgentNotFound("agent-1"));

        await Assert.ThrowsAsync<CopilotStudioException>(() => Create(catalog).PublishAsync(
            new CopilotPublishRequest { Bot = "agent-1", Environment = "env-1", Force = true },
            CancellationToken.None));

        Assert.Equal(1, catalog.PublishCalls);
    }

    [Fact]
    public async Task Publish_WhenItSucceedsFirstTime_DoesNotDelay()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(Start);
        var delays = new List<TimeSpan>();

        await CreateRecordingDelays(catalog, delays).PublishAsync(
            new CopilotPublishRequest { Bot = "agent-1", Environment = "env-1", Force = true },
            CancellationToken.None);

        Assert.Equal(1, catalog.PublishCalls);
        Assert.Empty(delays);
    }

    private static CopilotCatalogService CreateRecordingDelays(FakeCopilotCatalogPort catalog, List<TimeSpan> delays) =>
        new(
            catalog,
            CopilotTestFixtures.Resolver(),
            CopilotTestFixtures.Locator(new FakeCopilotWorkspacePort { WorkspaceExists = false }, new FakeCopilotFileSystem()),
            new AdvancingClock(Start, TimeSpan.FromSeconds(1)),
            (interval, _) =>
            {
                delays.Add(interval);
                return Task.CompletedTask;
            });

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
        var catalog = Catalog();

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1" }, CancellationToken.None);

        Assert.Equal("confirmation-required", result.Status);
        Assert.Equal(0, catalog.PublishCalls);
    }

    [Fact]
    public async Task Publish_WithoutForce_ReportsNoPublishTimes()
    {
        var catalog = Catalog();
        catalog.PublishedOn = PreviouslyPublished;

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1" }, CancellationToken.None);

        Assert.Null(result.PublishedOn);
        Assert.Null(result.PreviousPublishedOn);
        Assert.Equal(TimeSpan.Zero, result.Waited);
    }

    [Fact]
    public async Task Publish_WithForce_PublishesOnce()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("published", result.Status);
        Assert.Equal(1, catalog.PublishCalls);
    }

    [Fact]
    public async Task Publish_ReportsBothPublishTimes()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal(PreviouslyPublished, result.PreviousPublishedOn);
        Assert.Equal(NewlyPublished, result.PublishedOn);
    }

    [Fact]
    public async Task Publish_PollsUntilPublishedOnChanges()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("published", result.Status);
        Assert.Equal(NewlyPublished, result.PublishedOn);
        Assert.Empty(catalog.PublishedOnSequence);
    }

    [Fact]
    public async Task Publish_WhenPublishedOnNeverChanges_ThrowsPublishTimeout()
    {
        var catalog = Catalog();
        catalog.PublishedOn = PreviouslyPublished;

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(catalog, clock: new AdvancingClock(Start, TimeSpan.FromMinutes(5)))
                .PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true, MaximumWait = TimeSpan.FromMinutes(1) }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PublishTimeout, failure.ErrorKind);
        Assert.Equal(ExitCodes.Failure, failure.ExitCode);
    }

    [Fact]
    public async Task Publish_WhenAgentWasNeverPublished_TreatsFirstTimestampAsCompletion()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("published", result.Status);
        Assert.Null(result.PreviousPublishedOn);
        Assert.Equal(NewlyPublished, result.PublishedOn);
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
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog, workspaces).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("bot-argument", result.ResolvedFrom);
        Assert.Equal("published", result.Status);
    }

    [Fact]
    public async Task Publish_WithoutBot_UsesTheConnectedWorkspace()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog, new FakeCopilotWorkspacePort { WorkspaceExists = true })
            .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None);

        Assert.Equal("workspace", result.ResolvedFrom);
        Assert.Equal("published", result.Status);
    }

    [Fact]
    public async Task Publish_WithoutBot_DoesNotResolveAnAgentIdentifier()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        await Create(catalog, new FakeCopilotWorkspacePort { WorkspaceExists = true })
            .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None);

        Assert.Empty(catalog.ResolvedValues);
    }

    [Fact]
    public async Task Publish_WithNeitherBotNorWorkspace_ThrowsWorkspaceNotFound()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Catalog(), new FakeCopilotWorkspacePort { WorkspaceExists = false })
                .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceNotFound, failure.ErrorKind);
    }

    [Fact]
    public async Task Publish_WithNeitherBotNorWorkspace_DoesNotPublish()
    {
        var catalog = Catalog();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(catalog, new FakeCopilotWorkspacePort { WorkspaceExists = false })
                .PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None));

        Assert.Equal(0, catalog.PublishCalls);
    }

    [Fact]
    public async Task Publish_ReportsTheAgentAndEnvironment()
    {
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        var result = await Create(catalog).PublishAsync(new CopilotPublishRequest { Bot = "agent-1", Force = true }, CancellationToken.None);

        Assert.Equal("agent-1", result.AgentId);
        Assert.Equal("con_agent", result.SchemaName);
        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Publish_DoesNotWriteToTheWorkspace()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var catalog = Catalog();
        catalog.PublishedOnSequence.Enqueue(PreviouslyPublished);
        catalog.PublishedOnSequence.Enqueue(NewlyPublished);

        await Create(catalog, workspaces).PublishAsync(new CopilotPublishRequest { Force = true }, CancellationToken.None);

        Assert.Equal(0, workspaces.PullCalls);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Fact]
    public async Task Publish_WhenItTimesOut_DoesNotWriteToTheWorkspace()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var catalog = Catalog();
        catalog.PublishedOn = PreviouslyPublished;

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(catalog, workspaces, clock: new AdvancingClock(Start, TimeSpan.FromMinutes(5)))
                .PublishAsync(new CopilotPublishRequest { Force = true, MaximumWait = TimeSpan.FromMinutes(1) }, CancellationToken.None));

        Assert.Equal(0, workspaces.PullCalls);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, workspaces.CloneCalls);
    }
}

internal sealed class AdvancingClock : Core.Ports.IClock
{
    private readonly TimeSpan _step;
    private DateTimeOffset _now;

    public AdvancingClock(DateTimeOffset start, TimeSpan? step = null)
    {
        _now = start;
        _step = step ?? TimeSpan.Zero;
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            var current = _now;
            _now = _now.Add(_step);
            return current;
        }
    }
}
