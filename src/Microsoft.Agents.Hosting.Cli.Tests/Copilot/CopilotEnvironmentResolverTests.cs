using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotEnvironmentResolverTests
{
    private static (CopilotEnvironmentResolver Resolver, FakeEnvironmentClient Client) Create(params PowerPlatformEnvironment[] environments)
    {
        var client = new FakeEnvironmentClient(environments);
        var service = EnvironmentFixtures.Service(client, new InMemoryEnvironmentSelectionStore(), InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        return (new CopilotEnvironmentResolver(service, PowerPlatformCloudMap.ForCloud(CloudInstance.Public)), client);
    }

    [Fact]
    public async Task List_ReadsTheInventoryOnceForEveryEnvironment()
    {
        var (resolver, client) = Create(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-3", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Equal(3, inventory.Reachable.Count);
        Assert.Equal(1, client.ListCalls);
    }

    [Fact]
    public async Task List_OrdersTheInventoryByEnvironmentId()
    {
        var (resolver, _) = Create(
            EnvironmentFixtures.Environment("env-c", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-a", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-b", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Equal(["env-a", "env-b", "env-c"], inventory.Reachable.Select(environment => environment.EnvironmentId));
    }

    [Fact]
    public async Task List_CarriesTheDataverseOrganizationFromTheListing()
    {
        var (resolver, _) = Create(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Equal(new Uri(CopilotTestFixtures.DataverseOrganization), Assert.Single(inventory.Reachable).DataverseOrganization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    [InlineData("https://user:secret@contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/")]
    public async Task List_ReportsAnEnvironmentWithoutAUsableOrganizationUrlAsUnreachable(string? instanceUrl)
    {
        var (resolver, _) = Create(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: instanceUrl));

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Equal("env-1", Assert.Single(inventory.Reachable).EnvironmentId);
        Assert.Equal("env-2", Assert.Single(inventory.Unreachable));
    }

    [Fact]
    public async Task List_WithNoEnvironments_ReportsAnEmptyInventory()
    {
        var (resolver, _) = Create();

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Empty(inventory.Reachable);
        Assert.Empty(inventory.Unreachable);
    }

    [Fact]
    public async Task Resolve_ForAnEnvironmentWithoutAnOrganizationUrl_Fails()
    {
        var (resolver, _) = Create(EnvironmentFixtures.Environment("env-1", instanceUrl: null));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => resolver.ResolveAsync("env-1", CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.EnvironmentMissingInstanceUrl, failure.ErrorKind);
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://contoso.crm.dynamics.com.attacker.example/")]
    [InlineData("https://contoso.crm.dynamics.cn/")]
    public async Task List_ReportsAnEnvironmentOutsideTheCloudsDataverseHostsAsUnreachable(string instanceUrl)
    {
        var (resolver, _) = Create(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: instanceUrl));

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Equal("env-1", Assert.Single(inventory.Reachable).EnvironmentId);
        Assert.Equal("env-2", Assert.Single(inventory.Unreachable));
    }

    [Theory]
    [InlineData("https://contoso.crm4.dynamics.com/")]
    [InlineData("https://org-with-dashes.crm16.dynamics.com/")]
    public async Task List_ReportsARegionalCommercialOrganizationAsReachable(string instanceUrl)
    {
        var (resolver, _) = Create(EnvironmentFixtures.Environment("env-1", instanceUrl: instanceUrl));

        var inventory = await resolver.ListAsync(CancellationToken.None);

        Assert.Equal(new Uri(instanceUrl), Assert.Single(inventory.Reachable).DataverseOrganization);
        Assert.Empty(inventory.Unreachable);
    }

    [Fact]
    public async Task Resolve_ForAnEnvironmentOutsideTheCloudsDataverseHosts_FailsWithoutMintingAToken()
    {
        var (resolver, _) = Create(EnvironmentFixtures.Environment("env-1", instanceUrl: "https://evil.example.com/"));

        var failure = await Assert.ThrowsAsync<EndpointTrustException>(
            () => resolver.ResolveAsync("env-1", CancellationToken.None));

        Assert.Contains("evil.example.com", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ForATrustedEnvironment_CarriesTheOrganizationUrl()
    {
        var (resolver, _) = Create(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var context = await resolver.ResolveAsync("env-1", CancellationToken.None);

        Assert.Equal(new Uri(CopilotTestFixtures.DataverseOrganization), context.DataverseOrganization);
    }
}
