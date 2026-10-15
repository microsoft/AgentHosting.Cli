using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class AgentTypeParsingTests
{
    [Theory]
    [InlineData(null, AgentTypes.CodeAgent)]
    [InlineData("", AgentTypes.CodeAgent)]
    [InlineData("   ", AgentTypes.CodeAgent)]
    [InlineData("CodeAgent", AgentTypes.CodeAgent)]
    [InlineData("codeagent", AgentTypes.CodeAgent)]
    [InlineData("MCSAgent", AgentTypes.McsAgent)]
    [InlineData("mcsagent", AgentTypes.McsAgent)]
    [InlineData("mcs", AgentTypes.McsAgent)]
    public void Parse_ResolvesAKnownType(string? value, string expected)
    {
        Assert.Equal(expected, AgentTypes.Parse(value));
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("Code")]
    [InlineData("MCSAgent2")]
    [InlineData("-")]
    public void Parse_RefusesToDefaultAnUnknownType(string value)
    {
        Assert.Null(AgentTypes.Parse(value));
    }
}

public sealed class SyncAuthProviderTenantTests
{
    private static readonly Uri Audience = new("https://contoso.crm.dynamics.com/");

    private static CopilotEnvironmentContext Environment(string? tenantId) => new()
    {
        EnvironmentId = "env-1",
        TenantId = tenantId!,
        DataverseOrganization = Audience,
    };

    private static AgentHostSyncAuthProvider Targeted(RecordingTokenProvider tokens, CopilotEnvironmentContext? environment)
    {
        var provider = new AgentHostSyncAuthProvider(tokens);

        if (environment is not null)
        {
            using var accessor = new AgentHostDataverseHttpClientAccessor(provider);
            DataverseTargeting.Connect((Microsoft.CopilotStudio.Sync.Dataverse.ISyncDataverseClient)new StubSolutionVersionsClient(), accessor, environment);
        }

        return provider;
    }

    [Fact]
    public async Task AcquireToken_AfterTargetingAnEnvironment_AsksForItsTenant()
    {
        var tokens = new RecordingTokenProvider();

        await Targeted(tokens, Environment("tenant-9")).AcquireTokenAsync(Audience, CancellationToken.None);

        Assert.Equal("tenant-9", Assert.Single(tokens.RequestedTargetTenants));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AcquireToken_WhenTheEnvironmentNamesNoTenant_AsksForNoParticularTenant(string? tenantId)
    {
        var tokens = new RecordingTokenProvider();

        await Targeted(tokens, Environment(tenantId)).AcquireTokenAsync(Audience, CancellationToken.None);

        Assert.Null(Assert.Single(tokens.RequestedTargetTenants));
    }

    [Fact]
    public async Task AcquireToken_AfterTargetingAWorkspace_AsksForTheTenantItRecords()
    {
        var tokens = new RecordingTokenProvider();
        var provider = new AgentHostSyncAuthProvider(tokens);
        using var accessor = new AgentHostDataverseHttpClientAccessor(provider);

        DataverseTargeting.Connect(new StubSolutionVersionsClient(), accessor, new CopilotWorkspaceInfo
        {
            WorkspacePath = "C:/workspace",
            AgentId = "agent-1",
            EnvironmentId = "env-1",
            DataverseOrganization = Audience,
            TenantId = "tenant-7",
        });

        await provider.AcquireTokenAsync(Audience, CancellationToken.None);

        Assert.Equal("tenant-7", Assert.Single(tokens.RequestedTargetTenants));
    }

    [Fact]
    public async Task ConcurrentOperations_DoNotOverwriteEachOthersTenant()
    {
        var first = new RecordingTokenProvider();
        var second = new RecordingTokenProvider();

        async Task AcquireAsync(RecordingTokenProvider tokens, string tenantId, TimeSpan pause)
        {
            var provider = Targeted(tokens, Environment(tenantId));
            await Task.Delay(pause, CancellationToken.None);
            await provider.AcquireTokenAsync(Audience, CancellationToken.None);
        }

        await Task.WhenAll(
            Task.Run(() => AcquireAsync(first, "tenant-a", TimeSpan.FromMilliseconds(60))),
            Task.Run(() => AcquireAsync(second, "tenant-b", TimeSpan.FromMilliseconds(10))));

        Assert.Equal("tenant-a", Assert.Single(first.RequestedTargetTenants));
        Assert.Equal("tenant-b", Assert.Single(second.RequestedTargetTenants));
    }
}

public sealed class DataverseHttpClientAccessorTests
{
    private static readonly Uri Organization = new("https://contoso.crm.dynamics.com/");

    [Fact]
    public void CreateClient_ReusesOneTransportAcrossEveryClient()
    {
        var handlers = 0;
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () =>
        {
            handlers++;
            return new ScriptedHttpMessageHandler();
        });

        accessor.SetDataverseOrganization(Organization);

        for (var index = 0; index < 5; index++)
        {
            using var client = accessor.CreateClient();
        }

        Assert.Equal(1, handlers);
    }

    [Fact]
    public async Task DisposingAClient_LeavesTheSharedTransportUsable()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(System.Net.HttpStatusCode.OK, "{}")
            .Respond(System.Net.HttpStatusCode.OK, "{}");

        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(Organization);

        using (var first = accessor.CreateClient())
        {
            await first.GetAsync(Organization, CancellationToken.None);
        }

        using var second = accessor.CreateClient();
        var response = await second.GetAsync(Organization, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void CreateClient_BeforeTargetingAnOrganization_Fails()
    {
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new ScriptedHttpMessageHandler());

        Assert.Throws<InvalidOperationException>(accessor.CreateClient);
    }
}
