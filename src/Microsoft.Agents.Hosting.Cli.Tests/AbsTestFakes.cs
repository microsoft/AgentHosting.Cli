using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// A configurable, offline <see cref="IAbsStateReader"/> for the ABS preview core tests. It returns
/// canned read results (provider registration, subscription/resource-group access, existing bot) with
/// no Azure transport. There are deliberately no Graph read methods — the C# runtime carries no Graph
/// port, so the zero-Graph guarantee is structural (a Graph read is not expressible here).
/// </summary>
internal sealed class FakeAbsStateReader : IAbsStateReader
{
    public string ProviderRegistrationState { get; init; } = "Registered";

    public AbsScopeAccess Subscription { get; init; } = new() { Exists = true, HasReadAccess = true };

    public AbsScopeAccess ResourceGroup { get; init; } = new() { Exists = true, HasReadAccess = true };

    public AbsBotSnapshot? ExistingBot { get; init; }

    /// <summary>
    /// When set, every read throws this exception, modelling an ARM read that fails before returning a
    /// result — for example a non-interactive target-tenant token-acquisition failure that the real
    /// adapter surfaces as an interaction-required auth error (exit 3).
    /// </summary>
    public Exception? ReadFailure { get; init; }

    public Task<AbsProviderRegistration> GetProviderRegistrationAsync(
        string subscriptionId,
        string providerNamespace,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        ReadFailure is not null
            ? throw ReadFailure
            : Task.FromResult(new AbsProviderRegistration
            {
                ProviderNamespace = providerNamespace,
                RegistrationState = ProviderRegistrationState,
            });

    public Task<AbsScopeAccess> GetSubscriptionAccessAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        ReadFailure is not null ? throw ReadFailure : Task.FromResult(Subscription);

    public Task<AbsScopeAccess> GetResourceGroupAccessAsync(
        string subscriptionId,
        string resourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        ReadFailure is not null ? throw ReadFailure : Task.FromResult(ResourceGroup);

    public Task<AbsBotSnapshot?> GetBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken) =>
        ReadFailure is not null ? throw ReadFailure : Task.FromResult(ExistingBot);
}

/// <summary>
/// A recording, offline <see cref="IAbsMutationPort"/> for the apply core tests. It appends
/// <c>"arm.upsert"</c> to <see cref="Calls"/> for every bot PUT (so a test can assert the fixture's
/// <c>mutationCalls</c> exactly), returns a deterministic snapshot derived from the request, and can be
/// configured to throw to exercise the target-error path.
/// </summary>
internal sealed class RecordingAbsMutationPort : IAbsMutationPort
{
    public List<string> Calls { get; } = [];

    public Exception? ThrowOnUpsert { get; init; }

    public Task<AbsBotSnapshot> UpsertBotAsync(AbsBotUpsert request, CancellationToken cancellationToken)
    {
        Calls.Add("arm.upsert");
        if (ThrowOnUpsert is not null)
        {
            throw ThrowOnUpsert;
        }

        var botResourceId =
            $"/subscriptions/{request.SubscriptionId}/resourceGroups/{request.ResourceGroup}" +
            $"/providers/Microsoft.BotService/botServices/{request.Name}";
        return Task.FromResult(new AbsBotSnapshot
        {
            BotResourceId = botResourceId,
            AppId = request.MsaAppId,
            MsaAppType = "SingleTenant",
            MessagingEndpoint = request.Endpoint,
            Sku = request.Sku ?? "F0",
        });
    }
}

/// <summary>
/// An in-memory <see cref="IConnectionMetadataStore"/> for the apply core tests. It records every write
/// (so a test can assert the persisted, non-secret metadata) and reads back what was written for the
/// matching (subscription, resource group, bot) identity.
/// </summary>
internal sealed class InMemoryConnectionMetadataStore : IConnectionMetadataStore
{
    private readonly Dictionary<string, ConnectionMetadata> _store = new(StringComparer.Ordinal);

    public List<ConnectionMetadata> Writes { get; } = [];

    public ConnectionMetadata? Seed { get; init; }

    public Task WriteAsync(ConnectionMetadata metadata, CancellationToken cancellationToken)
    {
        Writes.Add(metadata);
        _store[Key(metadata.SubscriptionId, metadata.ResourceGroup, metadata.BotName)] = metadata;
        return Task.CompletedTask;
    }

    public Task<ConnectionMetadata?> ReadAsync(ConnectionMetadataRef reference, CancellationToken cancellationToken)
    {
        var key = Key(reference.SubscriptionId, reference.ResourceGroup, reference.BotName);
        if (_store.TryGetValue(key, out var stored))
        {
            return Task.FromResult<ConnectionMetadata?>(stored);
        }

        if (Seed is not null &&
            Seed.SubscriptionId == reference.SubscriptionId &&
            Seed.ResourceGroup == reference.ResourceGroup &&
            Seed.BotName == reference.BotName)
        {
            return Task.FromResult<ConnectionMetadata?>(Seed);
        }

        return Task.FromResult<ConnectionMetadata?>(null);
    }

    private static string Key(string subscriptionId, string resourceGroup, string botName) =>
        $"{subscriptionId}/{resourceGroup}/{botName}";
}
