using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests;

/// <summary>
/// Minimal, self-contained fakes for the ports the embedded <see cref="AgentHostingOperations"/> facade
/// consumes. They live in this Core-only assembly (they cannot reuse the command-host test fakes without
/// dragging in the host reference), so the embeddability proof stays hermetic: no filesystem, no MSAL,
/// no Azure transport, no console. Every login flow throws — a headless host that only reads status and
/// previews must never trigger interactive sign-in.
/// </summary>
internal sealed class EmbeddedAuthenticationSession : IAuthenticationSession
{
    public int LoginFlowCalls { get; private set; }
    public int GetAccountsCalls { get; private set; }
    public int ClearCacheCalls { get; private set; }

    public Task<AuthAccount> LoginWithBrokerAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        LoginFlowCalls++;
        throw new InvalidOperationException("An embedded status/preview host must never open a broker sign-in.");
    }

    public Task<AuthAccount> LoginWithBrokerSilentlyAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        LoginFlowCalls++;
        throw new InvalidOperationException("An embedded status/preview host must never sign in through the broker.");
    }

    public Task<AuthAccount> LoginWithDeviceCodeAsync(
        AuthLoginRequest request,
        Func<string, Task> deviceCodeCallback,
        CancellationToken cancellationToken)
    {
        LoginFlowCalls++;
        throw new InvalidOperationException("An embedded status/preview host must never start a device-code sign-in.");
    }

    public Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        GetAccountsCalls++;
        return Task.FromResult<IReadOnlyList<AuthAccount>>([]);
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        ClearCacheCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory <see cref="IAuthProfileStore"/> seeded empty, so status reports signed-out.</summary>
internal sealed class EmbeddedAuthProfileStore : IAuthProfileStore
{
    private readonly List<AuthProfile> _profiles = [];

    public Task<AuthProfile?> GetActiveProfileAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_profiles.FirstOrDefault(p => p.IsActive));

    public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuthProfile>>(_profiles.ToList());

    public Task UpsertAsync(AuthProfile profile, CancellationToken cancellationToken)
    {
        _profiles.RemoveAll(p => p.AccountKey == profile.AccountKey);
        _profiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task SetActiveAsync(string accountKey, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _profiles.Count; i++)
        {
            _profiles[i] = _profiles[i] with { IsActive = _profiles[i].AccountKey == accountKey };
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _profiles.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>A no-op <see cref="IInteractiveMessagePresenter"/>; the embedded host surfaces nothing.</summary>
internal sealed class SilentMessagePresenter : IInteractiveMessagePresenter
{
    public int PresentCalls { get; private set; }

    public Task PresentAsync(string message, CancellationToken cancellationToken)
    {
        PresentCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IEnvironmentClient"/> that reports no environments. The embedded host reads status
/// and previews; it never lists or selects, so an empty inventory is the honest shape and it requires
/// no HTTP seam.
/// </summary>
internal sealed class EmptyEnvironmentClient : IEnvironmentClient
{
    public Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>([]);
}

/// <summary>An in-memory selection store seeded empty, so status reports no environment.</summary>
internal sealed class EmbeddedEnvironmentSelectionStore : IEnvironmentSelectionStore
{
    private ActiveEnvironment? _active;

    public Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_active);

    public Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken)
    {
        _active = environment;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _active = null;
        return Task.CompletedTask;
    }
}

/// <summary>A fixed clock, so the embedded composition needs no ambient time source.</summary>
internal sealed class EmbeddedClock : IClock
{
    public DateTimeOffset UtcNow => new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// A canned, offline <see cref="IAbsStateReader"/> for the embedded preview: a registered provider,
/// readable subscription and resource group, and no existing bot (so the plan is a clean create). No
/// Azure transport, and — as in the host tests — there is no Graph read method to express.
/// </summary>
internal sealed class EmbeddedAbsStateReader : IAbsStateReader
{
    public Task<AbsProviderRegistration> GetProviderRegistrationAsync(
        string subscriptionId,
        string providerNamespace,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AbsProviderRegistration
        {
            ProviderNamespace = providerNamespace,
            RegistrationState = "Registered",
        });

    public Task<AbsScopeAccess> GetSubscriptionAccessAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AbsScopeAccess { Exists = true, HasReadAccess = true });

    public Task<AbsScopeAccess> GetResourceGroupAccessAsync(
        string subscriptionId,
        string resourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AbsScopeAccess { Exists = true, HasReadAccess = true });

    public Task<AbsBotSnapshot?> GetBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken) =>
        Task.FromResult<AbsBotSnapshot?>(null);
}

/// <summary>
/// The publication transport for the embedded composition. Nothing in these tests publishes, so a
/// call is a test failure rather than a silent no-op: an embedded host must be able to construct the
/// facade without acquiring a transport that reaches anything.
/// </summary>
internal sealed class PendingAmsPackagePublicationTransport : IAmsPackagePublicationTransport
{
    public Task<AmsPackagePublicationResult> PublishAsync(
        AmsPackagePublicationTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent package publication was not expected in this test.");
}

internal sealed class PendingAmsAgentReadTransport : IAmsAgentReadTransport
{
    public Task<AmsAgentReadResult> GetAsync(
        AmsAgentReadTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent read was not expected in this test.");
}

internal sealed class PendingAmsDeploymentTransport : IAmsDeploymentTransport
{
    public Task<AmsDeploymentCurrentResult> GetAsync(
        AmsDeploymentReadTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deployment read was not expected in this test.");

    public Task<AmsDeploymentCreateResult> CreateAsync(
        AmsDeploymentCreateTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deployment creation was not expected in this test.");

    public Task<AmsDeploymentCreateResult> UpdateAsync(
        AmsDeploymentUpdateTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deployment update was not expected in this test.");
}

/// <summary>The embedded composition's pending AMS agent-create transport.</summary>
internal sealed class PendingAmsAgentCreationTransport : IAmsAgentCreationTransport
{
    public Task<AmsAgentCreationResult> CreateAsync(
        AmsAgentCreationTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent creation was not expected in this test.");
}
