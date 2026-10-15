using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ArmCompositionTests
{
    [Fact]
    public async Task DeploymentProvisioning_AcquiresArmFromTheConsentClient()
    {
        using var provider = BuildProvider();
        var deploymentTransport = provider.GetRequiredService<IDeploymentTransport>();

        await Assert.ThrowsAsync<ArmConsentProviderSelectedException>(() =>
            deploymentTransport.GetBotAsync(
                "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.BotService/botServices/bot",
                "tenant",
                CancellationToken.None));
    }

    [Fact]
    public async Task BotNameAvailability_AcquiresArmFromTheConsentClient()
    {
        using var provider = BuildProvider();
        var deploymentTransport = provider.GetRequiredService<IDeploymentTransport>();

        await Assert.ThrowsAsync<ArmConsentProviderSelectedException>(() =>
            deploymentTransport.CheckBotNameAvailabilityAsync("bot", "tenant", CancellationToken.None));
    }

    [Fact]
    public async Task SubscriptionResolution_AcquiresArmFromTheConsentClient()
    {
        using var provider = BuildProvider();
        var subscriptions = provider.GetRequiredService<IDeploymentTransport>();

        await Assert.ThrowsAsync<ArmConsentProviderSelectedException>(() =>
            subscriptions.ResolveSubscriptionAsync(null, "tenant", CancellationToken.None));
    }

    [Fact]
    public async Task AzureBotServiceState_AcquiresArmFromTheConsentClient()
    {
        using var provider = BuildProvider();
        var reader = provider.GetRequiredService<IAbsStateReader>();

        await Assert.ThrowsAsync<ArmConsentProviderSelectedException>(() =>
            reader.GetProviderRegistrationAsync("sub", "Microsoft.BotService", "tenant", CancellationToken.None));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5",
            ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        });
        services.AddSingleton<ITokenProvider, ThrowingSilentTokenProvider>();
        services.AddSingleton<IArmConsentTokenProvider, ThrowingArmConsentTokenProvider>();

        return services.BuildServiceProvider();
    }

    private sealed class ThrowingSilentTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new SilentTokenProviderSelectedException();

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new SilentTokenProviderSelectedException();
    }

    private sealed class ThrowingArmConsentTokenProvider : IArmConsentTokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireArmTokenAsync(string? targetTenantId, CancellationToken cancellationToken) =>
            throw new ArmConsentProviderSelectedException();
    }

    private sealed class SilentTokenProviderSelectedException : Exception;

    private sealed class ArmConsentProviderSelectedException : Exception;
}
