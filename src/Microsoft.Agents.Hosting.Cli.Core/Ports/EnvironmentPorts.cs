namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// One Power Platform environment as the inventory reports it, carrying the endpoint/audience pair the
/// CLI needs to address it. Environment inventory is a platform capability and is never served by the
/// Agent Management Service, so this record is the CLI's only description of an environment.
///
/// It holds no token material, and the endpoint is not trusted until it has passed the cloud map.
/// </summary>
public sealed record PowerPlatformEnvironment
{
    /// <summary>The environment id the operator passes to <c>msagent env select</c>.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The environment's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The tenant that owns the environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The environment-specific Power Platform API endpoint.</summary>
    public required Uri ApiEndpoint { get; init; }

    /// <summary>The Power Platform API token audience for this environment.</summary>
    public required string ApiAudience { get; init; }

    /// <summary>
    /// The environment's linked Dataverse instance url.
    /// </summary>
    public string? InstanceUrl { get; init; }

    /// <summary>
    /// The Copilot Studio control-plane endpoint the environment advertises, which is where agent
    /// sharing is read and written.
    /// </summary>
    public string? AgentManagementUrl { get; init; }

}

/// <summary>
/// Reads the environment inventory available to the signed-in operator. The implementation acquires a
/// token silently, so listing environments can never open identity UI.
/// </summary>
public interface IEnvironmentClient
{
    /// <summary>Lists the environments the signed-in operator can see.</summary>
    Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The minimal target context observed after normal product resolution. It contains no endpoint,
/// geography, token, or persistent telemetry identity.
/// </summary>
public readonly record struct ResolvedEnvironmentTarget(
    string EnvironmentId,
    string TenantId);

/// <summary>
/// Optional invocation-scoped observer for environments that normal product behavior actually
/// resolves. Enumeration alone is not a target observation.
/// </summary>
public interface IResolvedEnvironmentObserver
{
    /// <summary>Whether the current invocation accepts target observations.</summary>
    bool IsObservationEnabled => true;

    /// <summary>Observes one resolved target without initiating any product work.</summary>
    void Observe(ResolvedEnvironmentTarget target);
}

/// <summary>
/// Reads the authoritative geography for one already-resolved environment. Implementations may
/// authenticate only through the explicitly selected-account silent-token seam.
/// </summary>
public interface IEnvironmentGeographyClient
{
    /// <summary>
    /// Returns the authoritative geography name, or <see langword="null"/> when no explicitly
    /// selected cached account is available.
    /// </summary>
    Task<string?> GetGeographyAsync(
        ResolvedEnvironmentTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// The persisted active-environment record. It is the only durable CLI-side state the frontend
/// skeleton keeps: no operation identifier, no idempotency key, and no pending-request journal, so a
/// lost response or a fresh process recovers through the service rather than through local state.
///
/// It contains no bearer token.
/// </summary>
public sealed record ActiveEnvironment
{
    /// <summary>The selected environment id.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The selected environment's display name at selection time.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The tenant that owns the environment; matched against the active auth profile.</summary>
    public required string TenantId { get; init; }

    /// <summary>The trusted Power Platform API endpoint for the environment.</summary>
    public required Uri ApiEndpoint { get; init; }

    /// <summary>The Power Platform API token audience for the environment.</summary>
    public required string ApiAudience { get; init; }

    /// <summary>
    /// The environment's linked Dataverse instance url, captured at selection time. 
    /// </summary>
    public string? InstanceUrl { get; init; }

    /// <summary>When the selection was made.</summary>
    public DateTimeOffset SelectedAt { get; init; }
}

/// <summary>
/// Durable active-environment selection. Persistence keeps the selection visible to later commands and
/// to status output without the operator re-supplying it.
/// </summary>
public interface IEnvironmentSelectionStore
{
    /// <summary>The active environment, or <see langword="null"/> when none is selected.</summary>
    Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Persists <paramref name="environment"/> as the active selection.</summary>
    Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken);

    /// <summary>Clears the active selection.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
