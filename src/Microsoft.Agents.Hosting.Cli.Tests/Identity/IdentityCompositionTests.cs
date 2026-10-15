using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class IdentityCompositionTests
{
    [Fact]
    public async Task BlueprintIdentityRestClient_UsesArmConsentProviderForArmRequests()
    {
        using var provider = BuildProvider();
        var rest = provider.GetRequiredService<IIdentityRestClient>();

        await Assert.ThrowsAsync<ArmConsentProviderSelectedException>(() =>
            rest.SendArmAsync(
                HttpMethod.Get,
                "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi",
                body: null,
                "tenant",
                cancellationToken: default));
    }

    [Fact]
    public async Task BlueprintIdentityRestClient_KeepsAgentHostTokenProviderForGraphRequests()
    {
        using var provider = BuildProvider();
        var rest = provider.GetRequiredService<IIdentityRestClient>();

        await Assert.ThrowsAsync<AgentHostTokenProviderSelectedException>(() =>
            rest.SendGraphAsync(
                HttpMethod.Get,
                "/beta/applications",
                body: null,
                [IdentityProvisioningConfig.GraphReadWriteScope],
                "tenant",
                cancellationToken: default));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5",
            ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        });
        services.AddSingleton<ITokenProvider, ThrowingAgentHostTokenProvider>();
        services.AddSingleton<IArmConsentTokenProvider, ThrowingArmConsentTokenProvider>();

        return services.BuildServiceProvider();
    }

    private sealed class ThrowingAgentHostTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            string resource,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            Task.FromException<TokenAcquisitionResult>(new AgentHostTokenProviderSelectedException());

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            Task.FromException<TokenAcquisitionResult>(new AgentHostTokenProviderSelectedException());
    }

    private sealed class ThrowingArmConsentTokenProvider : IArmConsentTokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireArmTokenAsync(
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            Task.FromException<TokenAcquisitionResult>(new ArmConsentProviderSelectedException());
    }

    private sealed class AgentHostTokenProviderSelectedException : Exception;

    private sealed class ArmConsentProviderSelectedException : Exception;
}
