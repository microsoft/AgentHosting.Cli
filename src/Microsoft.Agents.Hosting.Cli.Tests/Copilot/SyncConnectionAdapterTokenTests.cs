using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncConnectionAdapterTokenTests
{
    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static string Workspace() => Path.Combine(Path.GetTempPath(), "ah-connection-token");

    private static SyncConnectionAdapter Create(ITokenProvider tokenProvider) =>
        CopilotTestFixtures.ConnectionAdapter(tokenProvider: tokenProvider);

    private sealed class ForeignTenantTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenAcquisitionResult { AccessToken = "token-1", TenantId = "99999999-9999-9999-9999-999999999999", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenAcquisitionResult { AccessToken = "token-1", TenantId = "99999999-9999-9999-9999-999999999999", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });
    }

    private sealed class InteractionRequiredTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("interactive sign-in is required");

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("interactive sign-in is required");
    }

    [Fact]
    public async Task RemoveConnectionReference_AcquiresNoCatalogToken()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).RemoveConnectionReferenceAsync(Environment(), Workspace(), "cr_teams", confirmed: false, CancellationToken.None);

        Assert.Empty(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task RemoveConnectionReference_ProceedsWhenSilentAcquisitionWouldNeedInteraction()
    {
        var removal = await Create(new InteractionRequiredTokenProvider())
            .RemoveConnectionReferenceAsync(Environment(), Workspace(), "cr_teams", confirmed: false, CancellationToken.None);

        Assert.NotNull(removal);
    }

    [Fact]
    public async Task CreateConnectionReference_AcquiresNoCatalogToken()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).CreateConnectionReferenceAsync(Environment(), Workspace(), "shared_teams", CancellationToken.None);

        Assert.Empty(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task CreateConnectionReference_ProceedsWhenSilentAcquisitionWouldNeedInteraction()
    {
        var logicalName = await Create(new InteractionRequiredTokenProvider())
            .CreateConnectionReferenceAsync(Environment(), Workspace(), "shared_teams", CancellationToken.None);

        Assert.NotNull(logicalName);
    }

    [Fact]
    public async Task ReadDeclaredConnectors_AcquiresNoCatalogToken()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).ReadDeclaredConnectorsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.Empty(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task ReadConnections_AcquiresThePowerPlatformApiTokenItsCatalogLookupNeeds()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.Equal(PowerPlatformApiTargetConfig.Public.Audience, Assert.Single(tokenProvider.RequestedResources));
    }

    [Fact]
    public async Task ListConnections_RefusesATokenTheLibraryAcquiredForAnotherTenant()
    {
        var error = await Assert.ThrowsAsync<TokenClaimException>(() =>
            Create(new ForeignTenantTokenProvider()).ListConnectionsAsync(Environment(), "shared_teams", CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }

    [Fact]
    public async Task ListConnectors_RefusesATokenTheLibraryAcquiredForAnotherTenant()
    {
        var error = await Assert.ThrowsAsync<TokenClaimException>(() =>
            Create(new ForeignTenantTokenProvider()).ListConnectorsAsync(Environment(), CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }
}
