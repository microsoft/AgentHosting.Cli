using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Deterministic, offline fakes for the environment-targeting surface. No live credential, no consent,
/// no browser, and no network: the inventory is a canned list and the clock is fixed, so every
/// selection, rejection, and persisted record is byte-stable across runs.
/// </summary>
internal sealed class FakeEnvironmentClient : IEnvironmentClient
{
    private readonly IReadOnlyList<PowerPlatformEnvironment> _environments;

    public FakeEnvironmentClient(params PowerPlatformEnvironment[] environments) => _environments = environments;

    /// <summary>Counts inventory reads, so "select resolves against the live inventory" is assertable.</summary>
    public int ListCalls { get; private set; }

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        return Failure is null ? Task.FromResult(_environments) : Task.FromException<IReadOnlyList<PowerPlatformEnvironment>>(Failure);
    }
}

/// <summary>An in-memory <see cref="IEnvironmentSelectionStore"/> for selection behavior under test.</summary>
internal sealed class InMemoryEnvironmentSelectionStore : IEnvironmentSelectionStore
{
    public ActiveEnvironment? Active { get; private set; }

    public int WriteCalls { get; private set; }

    public Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Active);

    public Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken)
    {
        WriteCalls++;
        Active = environment;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Active = null;
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory <see cref="IAuthProfileStore"/> seeded with zero or one active profile.</summary>
internal sealed class InMemoryAuthProfileStore : IAuthProfileStore
{
    private readonly List<AuthProfile> _profiles = [];
    private string? _activeKey;

    public static InMemoryAuthProfileStore SignedOut() => new();

    public static InMemoryAuthProfileStore SignedIn(string username, string? tenantId)
    {
        var store = new InMemoryAuthProfileStore();
        var profile = new AuthProfile
        {
            AccountKey = $"{username}-key",
            Username = username,
            TenantId = tenantId,
        };
        store._profiles.Add(profile);
        store._activeKey = profile.AccountKey;
        return store;
    }

    public Task<AuthProfile?> GetActiveProfileAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_profiles.FirstOrDefault(p => p.AccountKey == _activeKey) is { } match
            ? match with { IsActive = true }
            : null);

    public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuthProfile>>(
            [.. _profiles.Select(p => p with { IsActive = p.AccountKey == _activeKey })]);

    public Task UpsertAsync(AuthProfile profile, CancellationToken cancellationToken)
    {
        _profiles.RemoveAll(p => p.AccountKey == profile.AccountKey);
        _profiles.Add(profile);
        _activeKey = profile.AccountKey;
        return Task.CompletedTask;
    }

    public Task SetActiveAsync(string accountKey, CancellationToken cancellationToken)
    {
        _activeKey = accountKey;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _profiles.Clear();
        _activeKey = null;
        return Task.CompletedTask;
    }
}

/// <summary>A fixed <see cref="IClock"/>, so a persisted selection timestamp is deterministic.</summary>
internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; }
}

/// <summary>Shared builders for environment-selection test fixtures.</summary>
internal static class EnvironmentFixtures
{
    public const string ContosoTenant = "11111111-1111-1111-1111-111111111111";
    public const string FabrikamTenant = "22222222-2222-2222-2222-222222222222";
    public const string Audience = "https://api.powerplatform.com/";

    public static readonly DateTimeOffset SelectedAt =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    public static PowerPlatformEnvironment Environment(
        string environmentId,
        string displayName = "Contoso Dev",
        string tenantId = ContosoTenant,
        string endpoint = "https://env-1.api.powerplatform.com/",
        string? instanceUrl = null) => new()
        {
            EnvironmentId = environmentId,
            DisplayName = displayName,
            TenantId = tenantId,
            ApiEndpoint = new Uri(endpoint),
            ApiAudience = Audience,
            InstanceUrl = instanceUrl,
        };

    /// <summary>A persisted selection for an environment in the given tenant.</summary>
    public static ActiveEnvironment Selection(
        string environmentId = "env-1",
        string displayName = "Contoso Dev",
        string tenantId = ContosoTenant,
        string endpoint = "https://env-1.api.powerplatform.com/") => new()
        {
            EnvironmentId = environmentId,
            DisplayName = displayName,
            TenantId = tenantId,
            ApiEndpoint = new Uri(endpoint),
            ApiAudience = Audience,
            SelectedAt = SelectedAt,
        };

    public static EnvironmentSelectionService Service(
        IEnvironmentClient client,
        IEnvironmentSelectionStore selectionStore,
        IAuthProfileStore profileStore,
        IResolvedEnvironmentObserver? resolvedEnvironmentObserver = null,
        PowerPlatformApiTargetConfig? apiTarget = null,
        EnvironmentSelectionScopes? selectionScopes = null) =>
        new(
            client,
            selectionStore,
            profileStore,
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public),
            new FixedClock(SelectedAt),
            resolvedEnvironmentObserver,
            apiTarget,
            selectionScopes);

    /// <summary>An account profile that differs from <see cref="SelectedEnvironmentAsync"/>'s only where asked.</summary>
    public static AuthProfile Account(
        string accountKey = "op@contoso.com-key",
        string username = "op@contoso.com",
        string? tenantId = ContosoTenant,
        PowerPlatformApiRing ring = PowerPlatformApiRing.Production) => new()
        {
            AccountKey = accountKey,
            Username = username,
            TenantId = tenantId,
            Ring = ring,
        };

    /// <summary>A Contoso environment selected by a signed-in Contoso account.</summary>
    public static async Task<(EnvironmentSelectionService Service, InMemoryEnvironmentSelectionStore Selection, InMemoryAuthProfileStore Profiles)>
        SelectedEnvironmentAsync()
    {
        var selection = new InMemoryEnvironmentSelectionStore();
        var profiles = InMemoryAuthProfileStore.SignedIn("op@contoso.com", ContosoTenant);
        var service = Service(
            new FakeEnvironmentClient(Environment("env-1", "Contoso Dev")),
            selection,
            profiles);

        await service.SelectAsync("env-1", CancellationToken.None);
        return (service, selection, profiles);
    }
}
