using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Placeholder for an unavailable ARM read adapter. Every read throws
/// <see cref="PendingImplementationException"/>, so a valid
/// <c>abs preview</c> with a clear "not yet implemented" failure (exit 1) while input validation and
/// the missing-context and format guards — which run before any Azure read — still work.
/// </summary>
public sealed class PendingAbsStateReader : IAbsStateReader
{
    public Task<AbsProviderRegistration> GetProviderRegistrationAsync(
        string subscriptionId,
        string providerNamespace,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        throw new PendingImplementationException("Azure Bot Service state reads");

    public Task<AbsScopeAccess> GetSubscriptionAccessAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        throw new PendingImplementationException("Azure Bot Service state reads");

    public Task<AbsScopeAccess> GetResourceGroupAccessAsync(
        string subscriptionId,
        string resourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        throw new PendingImplementationException("Azure Bot Service state reads");

    public Task<AbsBotSnapshot?> GetBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken) =>
        throw new PendingImplementationException("Azure Bot Service state reads");
}

/// <summary>
/// Placeholder for an unavailable ARM mutation adapter. The preview core never references
/// <see cref="IAbsMutationPort"/>; invoking this adapter throws
/// <see cref="PendingImplementationException"/>.
/// </summary>
public sealed class PendingAbsMutationPort : IAbsMutationPort
{
    public Task<AbsBotSnapshot> UpsertBotAsync(AbsBotUpsert request, CancellationToken cancellationToken) =>
        throw new PendingImplementationException("abs apply");
}
