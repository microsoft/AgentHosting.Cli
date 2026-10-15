using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The registration state of an Azure resource provider on a subscription.</summary>
public sealed record AbsProviderRegistration
{
    /// <summary>The provider namespace (for example <c>Microsoft.BotService</c>).</summary>
    public required string ProviderNamespace { get; init; }

    /// <summary>ARM registration state (for example <c>Registered</c> / <c>NotRegistered</c>).</summary>
    public required string RegistrationState { get; init; }
}

/// <summary>
/// Whether an ARM scope (subscription or resource group) exists and the operator can read it —
/// inferred without mutation. Mirrors the TypeScript <c>ArmScopeAccess</c> so the preflight planner
/// can distinguish "not found" from "found but not readable".
/// </summary>
public sealed record AbsScopeAccess
{
    /// <summary>Whether the scope exists and is visible to the signed-in operator.</summary>
    public required bool Exists { get; init; }

    /// <summary>Whether the signed-in operator can read the scope.</summary>
    public required bool HasReadAccess { get; init; }
}

/// <summary>
/// A read-only snapshot of an existing Azure Bot Service resource. Non-secret by construction; carries
/// no credential material.
/// </summary>
public sealed record AbsBotSnapshot
{
    /// <summary>The bot's ARM resource id.</summary>
    public required string BotResourceId { get; init; }

    /// <summary>The app id currently bound to the bot, when set.</summary>
    public string? AppId { get; init; }

    /// <summary>The bot's ARM <c>msaAppType</c>, when known (used to detect a destructive identity-type change).</summary>
    public string? MsaAppType { get; init; }

    /// <summary>The configured messaging endpoint, when set.</summary>
    public string? MessagingEndpoint { get; init; }

    /// <summary>The configured SKU, when known.</summary>
    public string? Sku { get; init; }
}

/// <summary>
/// TypeScript-compatible, non-secret Azure Bot Service correlation metadata. Shares the shipped
/// <c>AbsConnectionMetadata</c> schema and on-disk shape so both implementations interoperate. It
/// contains only public correlation identifiers, never secret material, and is persisted as one file
/// per (subscription, resource group, bot) under the per-user
/// <c>connections</c> directory. Azure remains authoritative: a missing, malformed, stale, or
/// future-version file is ignored and Azure is re-read.
/// </summary>
public sealed record ConnectionMetadata
{
    /// <summary>Metadata schema version; a mismatch makes the store ignore the file.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Target subscription id (part of the file identity).</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Target resource group (part of the file identity).</summary>
    public required string ResourceGroup { get; init; }

    /// <summary>Bot resource name (part of the file identity).</summary>
    public required string BotName { get; init; }

    /// <summary>The provisioned bot's ARM resource id.</summary>
    public required string BotResourceId { get; init; }

    /// <summary>The app id bound to the bot (public identifier, not a secret).</summary>
    public string? AppId { get; init; }

    /// <summary>The Entra application object id, when known (unused on the zero-Graph path).</summary>
    public string? AppObjectId { get; init; }

    /// <summary>ARM <c>msaAppType</c> (for example <c>SingleTenant</c>).</summary>
    public required string MsaAppType { get; init; }

    /// <summary>The bot identity mode (for example <c>ClientSecret</c>).</summary>
    public required string IdentityMode { get; init; }

    /// <summary>The configured messaging endpoint.</summary>
    public required string Endpoint { get; init; }

    /// <summary>ISO-8601 timestamp of first write; preserved across updates.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>ISO-8601 timestamp of the most recent write.</summary>
    public string? UpdatedAt { get; init; }
}

/// <summary>Identifies a connection's metadata file: one file per (subscription, resource group, bot).</summary>
public sealed record ConnectionMetadataRef
{
    /// <summary>Target subscription id.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Target resource group.</summary>
    public required string ResourceGroup { get; init; }

    /// <summary>Bot resource name.</summary>
    public required string BotName { get; init; }
}

/// <summary>
/// The single SingleTenant Azure Bot Service create/update input — the ARM upsert contract, derived
/// from the validated desired state before the mutation is issued. Mirrors the shipped TypeScript
/// <c>CreateOrUpdateBotServiceParams</c> so the direct-ARM PUT body stays byte-conformant.
/// </summary>
public sealed record AbsBotUpsert
{
    /// <summary>Target subscription id.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Target resource group.</summary>
    public required string ResourceGroup { get; init; }

    /// <summary>Bot resource name (also the ARM resource-name segment).</summary>
    public required string Name { get; init; }

    /// <summary>HTTPS messaging endpoint.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The bot's MSA/AAD app (client) id.</summary>
    public required string MsaAppId { get; init; }

    /// <summary>Tenant the single-tenant app lives in (ARM <c>msaAppTenantId</c>).</summary>
    public required string TenantId { get; init; }

    /// <summary>Display name; defaults to <see cref="Name"/> when null.</summary>
    public string? DisplayName { get; init; }

    /// <summary>SKU name; defaults to <c>F0</c> when null.</summary>
    public string? Sku { get; init; }

    /// <summary>Azure region for the control-plane resource; defaults to <c>global</c> when null.</summary>
    public string? Location { get; init; }
}

/// <summary>
/// GET-only Azure reads for provider registration, subscription, resource-group, and bot state. The
/// read side of the ARM adapter is structurally unable to mutate.
///
/// Every read takes the operator's intended <c>targetTenantId</c> so the ARM token is minted for that
/// tenant (MSAL <c>WithTenantId</c>), never the signed-in account's home tenant. Without it, an account
/// whose home tenant differs from the target subscription's tenant (for example a personal MSA on the
/// <c>/consumers</c> authority) yields a home-tenant token that ARM rejects (AADSTS9002332). A null
/// target falls back to the home authority.
/// </summary>
public interface IAbsStateReader
{
    /// <summary>Reads the registration state of a resource provider on the subscription.</summary>
    Task<AbsProviderRegistration> GetProviderRegistrationAsync(
        string subscriptionId,
        string providerNamespace,
        string? targetTenantId,
        CancellationToken cancellationToken);

    /// <summary>Reads whether the subscription exists and is readable by the operator.</summary>
    Task<AbsScopeAccess> GetSubscriptionAccessAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken);

    /// <summary>Reads whether the resource group exists and is readable by the operator.</summary>
    Task<AbsScopeAccess> GetResourceGroupAccessAsync(
        string subscriptionId,
        string resourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken);

    /// <summary>Reads an existing bot by resource id, or <see langword="null"/> when none exists.</summary>
    Task<AbsBotSnapshot?> GetBotAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The single Azure Bot Service create/update operation and the only mutating Azure surface. It is an
/// idempotent ARM upsert. Isolating it lets preview inject a mutation fake that fails the test if
/// preview ever resolves or calls it.
/// </summary>
public interface IAbsMutationPort
{
    /// <summary>Creates or updates the bot resource and returns the resulting snapshot.</summary>
    Task<AbsBotSnapshot> UpsertBotAsync(AbsBotUpsert request, CancellationToken cancellationToken);
}

/// <summary>
/// Persists and reads the shared-format, non-secret Azure Bot Service connection metadata. The store
/// treats the file strictly as a cache, never the source of truth: a missing, malformed, stale, or
/// wrong-version/wrong-identity file reads as <see langword="null"/> (Azure is re-read), and every
/// write asserts the payload carries no secret material.
/// </summary>
public interface IConnectionMetadataStore
{
    /// <summary>Persists non-secret connection metadata after asserting it is secret-free.</summary>
    Task WriteAsync(ConnectionMetadata metadata, CancellationToken cancellationToken);

    /// <summary>Reads validated metadata for a connection, or <see langword="null"/> when absent/invalid.</summary>
    Task<ConnectionMetadata?> ReadAsync(ConnectionMetadataRef reference, CancellationToken cancellationToken);
}
