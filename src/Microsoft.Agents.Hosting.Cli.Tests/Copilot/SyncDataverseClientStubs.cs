using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class StubDataverseHttpClientAccessor : IDataverseHttpClientAccessor
{
    public HttpClient CreateClient() => new();
}

internal abstract class OfflineSyncDataverseClient : SyncDataverseClient
{
    protected OfflineSyncDataverseClient() : base(new StubDataverseHttpClientAccessor(), "AgentHost.Cli.Tests")
    {
    }

    public string? ProvisioningStatus { get; init; }

    public override Task<AgentProvisioningStatus> GetProvisioningStatusAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(new AgentProvisioningStatus(ProvisioningStatus, null));
}

internal sealed class StubSolutionVersionsClient : OfflineSyncDataverseClient
{
    public int SolutionVersionCalls { get; private set; }

    public override Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken)
    {
        SolutionVersionCalls++;
        return Task.FromResult(new SolutionInfo());
    }
}
