using Microsoft.Agents.Hosting.Cli.Auth;
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
    public async Task AnAccountChange_DiscardsEverySelectionTheConfigDirectoryHolds()
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

        Assert.True(discarded);
        Assert.Null(await production.GetActiveAsync(CancellationToken.None));
        Assert.Null(await preprod.GetActiveAsync(CancellationToken.None));
        Assert.Null(await test.GetActiveAsync(CancellationToken.None));
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
