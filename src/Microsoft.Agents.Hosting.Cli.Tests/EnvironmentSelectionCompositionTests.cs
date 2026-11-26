using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Composition coverage for the environment selection the real container builds.</summary>
public sealed class EnvironmentSelectionCompositionTests : IDisposable
{
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "ah-env-composition-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AnAccountChange_PreservesEverySelectionTheConfigDirectoryHolds()
    {
        var production = RingStore(PowerPlatformApiRing.Production);
        var preprod = RingStore(PowerPlatformApiRing.Preprod);
        var test = RingStore(PowerPlatformApiRing.Test);

        await production.SetActiveAsync(EnvironmentFixtures.Selection(), CancellationToken.None);
        await preprod.SetActiveAsync(EnvironmentFixtures.Selection(environmentId: "env-preprod"), CancellationToken.None);
        await test.SetActiveAsync(EnvironmentFixtures.Selection(environmentId: "env-test"), CancellationToken.None);

        using var provider = BuildProvider();

        var discarded = await provider.GetRequiredService<EnvironmentSelectionService>()
            .ClearSelectionsOnIdentityChangeAsync(
                EnvironmentFixtures.Account(),
                EnvironmentFixtures.Account(accountKey: "other-key", username: "other@contoso.com"),
                CancellationToken.None);

        Assert.False(discarded);
        Assert.NotNull(await production.GetActiveAsync(CancellationToken.None));
        Assert.NotNull(await preprod.GetActiveAsync(CancellationToken.None));
        Assert.NotNull(await test.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUnchangedIdentity_LeavesEverySelectionInPlace()
    {
        var production = RingStore(PowerPlatformApiRing.Production);
        var test = RingStore(PowerPlatformApiRing.Test);

        await production.SetActiveAsync(EnvironmentFixtures.Selection(), CancellationToken.None);
        await test.SetActiveAsync(EnvironmentFixtures.Selection(environmentId: "env-test"), CancellationToken.None);

        using var provider = BuildProvider();

        var discarded = await provider.GetRequiredService<EnvironmentSelectionService>()
            .ClearSelectionsOnIdentityChangeAsync(
                EnvironmentFixtures.Account(),
                EnvironmentFixtures.Account(),
                CancellationToken.None);

        Assert.False(discarded);
        Assert.NotNull(await production.GetActiveAsync(CancellationToken.None));
        Assert.NotNull(await test.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public void AmsTargets_ResolveThroughTheHostEndpointProfileAndTheSignedInRing()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AmsEndpointProfile { DataPlaneEndpoint = "https://amsdp-alias.example/" });
        services.AddAgentHostAuthentication(new AgentHostAuthOptions { ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5", ConfigDirectory = _configDirectory }, PowerPlatformApiRingSelection.TargetFor(PowerPlatformApiRing.Test));
        using var provider = services.BuildServiceProvider();
        var home = EnvironmentFixtures.Environment("env-1");

        var targets = provider.GetRequiredService<AmsTargets>();

        Assert.Equal((new Uri("https://amsdp-alias.example/"), AmsRouteForm.Direct), (targets.DataPlane(home).Endpoint, targets.DataPlane(home).RouteForm));
        Assert.Equal((new Uri("https://api.test.powerplatform.com/"), AmsRouteForm.TenantHost), (targets.ControlPlane(home).Endpoint, targets.ControlPlane(home).RouteForm));
    }

    private FileEnvironmentSelectionStore RingStore(PowerPlatformApiRing ring) =>
        new(Path.Combine(
            _configDirectory,
            PowerPlatformApiRingSelection.RingScopedName(AgentHostAuthOptions.EnvironmentSelectionFileName, ring)));

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5",
            ConfigDirectory = _configDirectory,
        });
        services.AddSingleton<IClock>(new FixedClock(EnvironmentFixtures.SelectedAt));
        services.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient());

        return services.BuildServiceProvider();
    }
}
