using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class AgentSyncInfoFactoryTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid TenantId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = TenantId.ToString(),
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    [Fact]
    public async Task Create_PopulatesSolutionVersions()
    {
        var syncInfo = await AgentSyncInfoFactory.CreateAsync(new StubSolutionVersionsClient(), Environment(), AgentId, CancellationToken.None);

        Assert.NotNull(syncInfo.SolutionVersions);
    }

    [Fact]
    public async Task Create_PopulatesAccountInfo()
    {
        var syncInfo = await AgentSyncInfoFactory.CreateAsync(new StubSolutionVersionsClient(), Environment(), AgentId, CancellationToken.None);

        Assert.NotNull(syncInfo.AccountInfo);
        Assert.Equal(TenantId, syncInfo.AccountInfo!.TenantId);
    }

    [Fact]
    public async Task Create_CarriesTheAgentAndEnvironment()
    {
        var syncInfo = await AgentSyncInfoFactory.CreateAsync(new StubSolutionVersionsClient(), Environment(), AgentId, CancellationToken.None);

        Assert.Equal(AgentId, syncInfo.AgentId);
        Assert.Equal("env-1", syncInfo.EnvironmentId);
        Assert.Equal(new Uri("https://contoso.crm.dynamics.com/"), syncInfo.DataverseEndpoint);
    }

    [Fact]
    public async Task Create_WithAnUnparsableTenant_FallsBackToEmpty()
    {
        var environment = Environment() with { TenantId = "not-a-guid" };

        var syncInfo = await AgentSyncInfoFactory.CreateAsync(new StubSolutionVersionsClient(), environment, AgentId, CancellationToken.None);

        Assert.Equal(Guid.Empty, syncInfo.AccountInfo!.TenantId);
    }

    [Fact]
    public async Task Create_ReadsSolutionVersionsExactlyOnce()
    {
        var client = new StubSolutionVersionsClient();

        await AgentSyncInfoFactory.CreateAsync(client, Environment(), AgentId, CancellationToken.None);

        Assert.Equal(1, client.SolutionVersionCalls);
    }
}

internal sealed class StubDataverseHttpClientAccessor : IDataverseHttpClientAccessor
{
    public HttpClient CreateClient() => new();
}

internal sealed class StubSolutionVersionsClient : SyncDataverseClient
{
    public StubSolutionVersionsClient() : base(new StubDataverseHttpClientAccessor(), "AgentHost.Cli.Tests")
    {
    }

    public int SolutionVersionCalls { get; private set; }

    public override Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken)
    {
        SolutionVersionCalls++;
        return Task.FromResult(new SolutionInfo());
    }
}
