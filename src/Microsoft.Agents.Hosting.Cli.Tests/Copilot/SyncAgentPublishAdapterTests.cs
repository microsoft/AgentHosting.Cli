using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class ImmediateTimeProvider : TimeProvider
{
    private long _elapsedTicks;

    public List<TimeSpan> Delays { get; } = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        Interlocked.Add(ref _elapsedTicks, Math.Max(dueTime.Ticks, 0));
        return TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
}

public sealed class SyncAgentPublishAdapterTests
{
    private const string AgentId = "5bef50e8-15aa-f111-aaab-7ced8d702753";

    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    internal static SyncAgentPublishAdapter SyncPublisher(ScriptedHttpMessageHandler handler, TimeProvider timeProvider)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        var client = new SyncDataverseClient(accessor);
        return new SyncAgentPublishAdapter(client, new AgentPublishService(client, timeProvider), accessor);
    }

    internal static ScriptedHttpMessageHandler ProvisionedAgent() => new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """{"synchronizationstatus":null}""");

    internal static string FinishedPublish(string endedAt) => $$$"""{"synchronizationstatus":"{\"lastFinishedPublishOperation\":{\"status\":\"Succeeded\",\"operationEnd\":\"{{{endedAt}}}\"}}"}""";

    private static SyncAgentPublishAdapter Adapter(IAgentPublishDataverseClient client, IAgentPublishService service) =>
        new(client, service, new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new ScriptedHttpMessageHandler()));

    private static ScriptedPublishService Returning(AgentPublishOutcomeKind kind, string? status = null, string? failureReason = null) =>
        new(_ => Task.FromResult(new AgentPublishOutcome(kind, status, failureReason)));

    private static Task<DateTimeOffset?> PublishAsync(ScriptedPublishService service, string agentId = AgentId) =>
        Adapter(new RecordingPublishClient(), service).PublishAsync(Environment(), agentId, FiveMinutes, CancellationToken.None);

    [Fact]
    public async Task Publish_WhenCopilotStudioReportsSucceeded_PublishesTheAgentInItsEnvironment_AndSaysWhenItFinished()
    {
        var finishedOn = new DateTimeOffset(2026, 10, 27, 13, 30, 47, TimeSpan.Zero);
        var client = new RecordingPublishClient();
        string? targetedWhenPublishing = null;
        var service = new ScriptedPublishService(_ =>
        {
            targetedWhenPublishing = client.TargetedUrls.LastOrDefault();
            return Task.FromResult(new AgentPublishOutcome(AgentPublishOutcomeKind.Succeeded, "Succeeded", null) { FinishedOn = finishedOn });
        });

        var published = await Adapter(client, service).PublishAsync(Environment(), AgentId, FiveMinutes, CancellationToken.None);

        Assert.Equal(finishedOn, published);
        Assert.Equal("https://contoso.crm.dynamics.com/", targetedWhenPublishing);
        Assert.Equal((Guid.Parse(AgentId), FiveMinutes), Assert.Single(service.Requests));
    }

    [Fact]
    public async Task Publish_WhenCopilotStudioFinishesWithAnotherStatus_FailsNamingTheStatus()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => PublishAsync(Returning(AgentPublishOutcomeKind.Failed, "Failed")));

        Assert.Equal(ErrorCodes.CopilotStudio.PublishFailed, failure.ErrorCode);
        Assert.Equal(CopilotErrorKinds.PublishFailed, failure.ErrorKind);
        Assert.Equal($"Copilot Studio finished publishing agent '{AgentId}' with status 'Failed'.", failure.Message);
        Assert.Contains("saved as a draft", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_WhenTheProgressCannotBeRead_FailsWithTheRedactedReason()
    {
        const string reason = "Dataverse answered HTTP 403 to the request for the publish's progress.";

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => PublishAsync(Returning(AgentPublishOutcomeKind.Failed, failureReason: reason)));

        Assert.Equal(ErrorCodes.CopilotStudio.PublishFailed, failure.ErrorCode);
        Assert.Equal($"Copilot Studio was asked to publish agent '{AgentId}', but the publish's progress could not be read. {reason}", failure.Message);
        Assert.Contains("run the deploy again to confirm", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_WhenCopilotStudioDoesNotFinishWithinTheWait_SaysThePublishMayStillComplete()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => PublishAsync(Returning(AgentPublishOutcomeKind.TimedOut)));

        Assert.Equal(ErrorCodes.CopilotStudio.PublishTimeout, failure.ErrorCode);
        Assert.Equal(CopilotErrorKinds.PublishTimeout, failure.ErrorKind);
        Assert.Equal($"Copilot Studio did not finish publishing agent '{AgentId}' within 5 minutes. The publish was not cancelled and may still complete.", failure.Message);
        Assert.Equal("Run the deploy again to confirm the publish finished.", failure.Remediation);
    }

    [Fact]
    public async Task Publish_WhenCopilotStudioWasStillProvisioningTheAgent_SaysNoPublishWasAskedFor()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => PublishAsync(Returning(AgentPublishOutcomeKind.TimedOut, failureReason: "Copilot Studio was still provisioning the agent after 00:05:00, so no publish was asked for.")));

        Assert.Equal((ErrorCodes.CopilotStudio.AgentNotProvisioned, ExitCodes.Failure, CopilotErrorKinds.AgentNotProvisioned), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal("Copilot Studio was still provisioning the agent after 00:05:00, so no publish was asked for.", failure.Message);
        Assert.Equal("Wait until Copilot Studio finishes provisioning the agent, then run the same command again; it reuses the same agent.", failure.Remediation);
    }

    [Fact]
    public async Task Publish_WhenBothPublishRequestsFail_ReportsTheRedactedServiceError()
    {
        var handler = ProvisionedAgent()
            .Respond(HttpStatusCode.OK, """{"synchronizationstatus":null}""")
            .Respond(HttpStatusCode.BadRequest, """{"error":{"code":"0x80040265","message":"The agent is still being updated."}}""")
            .Respond(HttpStatusCode.OK, """{"synchronizationstatus":null}""")
            .Respond(HttpStatusCode.Forbidden, """{"error":{"code":"0x80040220","message":"The user lacks the publish privilege."},"trace":"raw response sentinel"}""");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => SyncPublisher(handler, new ImmediateTimeProvider()).PublishAsync(Environment(), AgentId, FiveMinutes, CancellationToken.None));

        Assert.Equal(ErrorCodes.CopilotStudio.ServiceError, failure.ErrorCode);
        Assert.Equal("The Copilot Studio service reported a failure during 'publish agent'. HTTP 403 Forbidden. The user lacks the publish privilege. (0x80040220)", failure.Message);
        Assert.DoesNotContain("sentinel", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
        Assert.Equal(["GET", "GET", "POST", "GET", "POST"], handler.Requests.Select(request => request.Split(' ')[0]));
    }

    [Fact]
    public async Task Publish_WhenCancelled_LetsTheCancellationThrough()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var service = new ScriptedPublishService(token => Task.FromCanceled<AgentPublishOutcome>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(new RecordingPublishClient(), service).PublishAsync(Environment(), AgentId, FiveMinutes, cancellation.Token));
    }

    [Fact]
    public async Task Publish_WithAnAgentIdThatIsNotAGuid_FailsAsNotFoundWithoutPublishing()
    {
        var service = Returning(AgentPublishOutcomeKind.Succeeded, "Succeeded");

        Assert.Equal(CopilotErrorKinds.AgentNotFound, (await Assert.ThrowsAsync<CopilotStudioException>(() => PublishAsync(service, "agent-1"))).ErrorKind);
        Assert.Empty(service.Requests);
    }

    private sealed class ScriptedPublishService(Func<CancellationToken, Task<AgentPublishOutcome>> publish) : IAgentPublishService
    {
        public List<(Guid AgentId, TimeSpan Timeout)> Requests { get; } = [];

        public Task<AgentPublishOutcome> PublishAsync(Guid agentId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add((agentId, timeout));
            return publish(cancellationToken);
        }
    }

    private sealed class RecordingPublishClient : IAgentPublishDataverseClient
    {
        public List<string> TargetedUrls { get; } = [];

        public void SetDataverseUrl(string dataverseUrl) => TargetedUrls.Add(dataverseUrl);

        public Task PublishAgentAsync(Guid agentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentPublishOperation?> GetLastPublishOperationAsync(Guid agentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
