using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// The environment list/select behavior every later frontend command depends on. It is the CLI's only
/// writer of durable target state, and it is deliberately conservative about what it will accept:
///
/// <list type="bullet">
///   <item>a command that needs an environment fails clearly when no account or no environment is
///   selected, and never falls back to a default;</item>
///   <item>an endpoint from the inventory is trusted only after the
///   <see cref="PowerPlatformCloudMap"/> accepts it, so a compromised or wrong inventory response
///   cannot redirect a bearer token to an arbitrary host;</item>
///   <item>an environment whose tenant differs from the active auth profile is rejected rather than
///   selected and then failed later at the service, and that binding is rechecked every time the
///   selection is used, because the active account can change after the selection is written.</item>
/// </list>
///
/// Nothing here can open identity UI: the inventory is read through <see cref="IEnvironmentClient"/>,
/// whose only token seam is silent acquisition.
/// </summary>
public sealed class EnvironmentSelectionService
{
    private readonly IEnvironmentClient _environmentClient;
    private readonly IEnvironmentSelectionStore _selectionStore;
    private readonly IAuthProfileStore _profileStore;
    private readonly PowerPlatformCloudMap _cloudMap;
    private readonly IClock _clock;
    private readonly IResolvedEnvironmentObserver? _resolvedEnvironmentObserver;
    private readonly PowerPlatformApiTargetConfig _apiTarget;
    private readonly EnvironmentSelectionScopes _selectionScopes;

    public EnvironmentSelectionService(
        IEnvironmentClient environmentClient,
        IEnvironmentSelectionStore selectionStore,
        IAuthProfileStore profileStore,
        PowerPlatformCloudMap cloudMap,
        IClock clock,
        IResolvedEnvironmentObserver? resolvedEnvironmentObserver = null,
        PowerPlatformApiTargetConfig? apiTarget = null,
        EnvironmentSelectionScopes? selectionScopes = null)
    {
        _environmentClient = environmentClient ?? throw new ArgumentNullException(nameof(environmentClient));
        _selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _cloudMap = cloudMap ?? throw new ArgumentNullException(nameof(cloudMap));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolvedEnvironmentObserver = resolvedEnvironmentObserver;
        _apiTarget = apiTarget ?? PowerPlatformApiRingSelection.Default;
        _selectionScopes = selectionScopes ?? new EnvironmentSelectionScopes([_selectionStore]);
    }

    /// <summary>
    /// Whether the active account now targets a different cloud than this process built its clients
    /// for. Reported rather than thrown, so status can show the selection's cloud without failing.
    /// </summary>
    /// <remarks>
    /// A host that composes no target is treated as having composed the default one. Treating it as
    /// unknown instead would turn the guard off for exactly the hosts that cannot rebuild themselves
    /// mid-process, letting a profile for another cloud resolve against default-cloud endpoints.
    /// </remarks>
    /// <param name="ring">The ring the active account records.</param>
    /// <returns><see langword="true"/> when the composition no longer matches the account.</returns>
    public bool IsRetargeted(PowerPlatformApiRing ring) => _apiTarget.Ring != ring;

    /// <summary>
    /// Lists the environments available to the signed-in operator, marking the active one. Every
    /// listed endpoint has passed the cloud map, so an entry that appears here is one the CLI is
    /// willing to call.
    /// </summary>
    public async Task<EnvironmentListResult> ListAsync(CancellationToken cancellationToken)
    {
        await RequireSignedInAsync(cancellationToken).ConfigureAwait(false);

        var environments = await _environmentClient
            .ListEnvironmentsAsync(cancellationToken)
            .ConfigureAwait(false);

        var active = await _selectionStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);

        var summaries = environments
            .Select(environment => new EnvironmentSummary
            {
                EnvironmentId = environment.EnvironmentId,
                DisplayName = environment.DisplayName,
                TenantId = environment.TenantId,
                ApiEndpoint = _cloudMap
                    .RequireTrusted(environment.ApiEndpoint, $"environment '{environment.EnvironmentId}'")
                    .ToString(),
                IsActive = active is not null &&
                    string.Equals(active.EnvironmentId, environment.EnvironmentId, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

        return new EnvironmentListResult
        {
            Status = "listed",
            Environments = summaries,
        };
    }

    /// <summary>
    /// The environments available to the signed-in operator, with the endpoint check applied but no
    /// projection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The available environments, in the order the service returned them.</returns>
    public async Task<IReadOnlyList<PowerPlatformEnvironment>> ListAvailableAsync(CancellationToken cancellationToken)
    {
        await RequireSignedInAsync(cancellationToken).ConfigureAwait(false);

        var environments = await _environmentClient
            .ListEnvironmentsAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var environment in environments)
        {
            _cloudMap.RequireTrusted(environment.ApiEndpoint, $"environment '{environment.EnvironmentId}'");
        }

        return environments;
    }

    /// <summary>
    /// Selects <paramref name="environmentId"/> as the active environment and persists it. The id is
    /// resolved against the live inventory rather than trusted as typed, so a selection can only name
    /// an environment the operator can actually see.
    /// </summary>
    public async Task<EnvironmentSelectionResult> SelectAsync(string environmentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
        {
            throw new UsageException("An environment id is required for `msagent env select`.");
        }

        var match = await ResolveAvailableAsync(environmentId, cancellationToken).ConfigureAwait(false);

        var selection = new ActiveEnvironment
        {
            EnvironmentId = match.EnvironmentId,
            DisplayName = match.DisplayName,
            TenantId = match.TenantId,
            ApiEndpoint = match.ApiEndpoint,
            ApiAudience = match.ApiAudience,
            InstanceUrl = match.InstanceUrl,
            SelectedAt = _clock.UtcNow,
        };

        await _selectionStore.SetActiveAsync(selection, cancellationToken).ConfigureAwait(false);
        ObserveResolved(match);

        return new EnvironmentSelectionResult
        {
            Status = "selected",
            EnvironmentId = selection.EnvironmentId,
            DisplayName = selection.DisplayName,
            TenantId = selection.TenantId,
            ApiEndpoint = selection.ApiEndpoint.ToString(),
            SelectedAt = selection.SelectedAt,
        };
    }

    /// <summary>
    /// Resolves the environment for one command. An explicit id is validated against the live
    /// inventory without changing the persisted active selection; when the id is omitted, the
    /// existing active selection is used after its tenant binding and endpoint trust are checked.
    /// </summary>
    public async Task<PowerPlatformEnvironment> ResolveForCommandAsync(
        string? environmentId,
        CancellationToken cancellationToken)
    {
        if (environmentId is null)
        {
            var active = await RequireActiveAsync(cancellationToken).ConfigureAwait(false);
            var environment = new PowerPlatformEnvironment
            {
                EnvironmentId = active.EnvironmentId,
                DisplayName = active.DisplayName,
                TenantId = active.TenantId,
                ApiEndpoint = active.ApiEndpoint,
                ApiAudience = active.ApiAudience,
                InstanceUrl = active.InstanceUrl,
            };
            ObserveResolved(environment);
            return environment;
        }

        if (string.IsNullOrWhiteSpace(environmentId))
        {
            throw new UsageException(
                "`msagent agent create --environment-id` requires a non-empty environment id.");
        }

        var resolved = await ResolveAvailableAsync(environmentId, cancellationToken).ConfigureAwait(false);
        ObserveResolved(resolved);
        return resolved;
    }

    /// <summary>
    /// The active environment, or <see langword="null"/> when none is selected. Used by status output;
    /// it never throws for the unselected case.
    ///
    /// The persisted endpoint is re-checked against the cloud map on the way out. Persistence crosses a
    /// process boundary, so a selection written by an older build, edited by hand, or tampered with on
    /// disk must not be trusted merely because it was trusted when it was written.
    /// </summary>
    public async Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var active = await _selectionStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            _cloudMap.RequireTrusted(active.ApiEndpoint, $"stored environment '{active.EnvironmentId}'");
        }

        return active;
    }

    /// <summary>
    /// The active environment, together with whether it is still bound to the signed-in account, or
    /// <see langword="null"/> when none is selected. Status uses this: it must be able to show an
    /// unusable selection and say why, rather than either hiding it or throwing.
    /// </summary>
    public async Task<ActiveEnvironmentBinding?> GetActiveBindingAsync(CancellationToken cancellationToken)
    {
        var active = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return null;
        }

        var profile = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        return new ActiveEnvironmentBinding
        {
            Environment = active,
            IsSignedIn = profile is not null,
            AccountUsername = profile?.Username,
            AccountTenantId = profile?.TenantId,
            IsBound = IsBoundTo(active, profile),
        };
    }

    /// <summary>
    /// Discards every cloud's persisted selection when the account or cloud that has just become
    /// active is not the one the selections were made under, and reports whether anything was discarded.
    /// </summary>
    /// <param name="previous">The account that was active before the change, or <see langword="null"/> when none was.</param>
    /// <param name="current">The account that is active after the change, or <see langword="null"/> when none is.</param>
    /// <param name="cancellationToken">Cancels the reads and the clears.</param>
    /// <returns><see langword="true"/> when at least one selection was discarded.</returns>
    public Task<bool> ClearSelectionsOnIdentityChangeAsync(
        AuthProfile? previous,
        AuthProfile? current,
        CancellationToken cancellationToken) =>
        IsSameIdentity(previous, current)
            ? Task.FromResult(false)
            : _selectionScopes.ClearAllAsync(cancellationToken);

    private static bool IsSameIdentity(AuthProfile? previous, AuthProfile? current) =>
        previous is not null &&
        current is not null &&
        string.Equals(previous.AccountKey, current.AccountKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(previous.TenantId, current.TenantId, StringComparison.OrdinalIgnoreCase) &&
        previous.Ring == current.Ring;

    private static bool IsBoundTo(ActiveEnvironment environment, AuthProfile? profile) =>
        profile is not null &&
        !string.IsNullOrWhiteSpace(profile.TenantId) &&
        !string.IsNullOrWhiteSpace(environment.TenantId) &&
        string.Equals(profile.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The active environment, or a typed failure when none is selected or the selection is no longer
    /// usable. This is the accessor later frontend commands use, so "no environment selected" is one
    /// clear error rather than a null dereference or an implicit default.
    ///
    /// <para>
    /// Two write-time decisions are rechecked here, for the same reason. The endpoint is rechecked
    /// against the cloud map because persistence crosses a process boundary. The tenant binding is
    /// rechecked against the <em>current</em> active profile because the account itself can change
    /// after the selection is written: `msagent auth login --tenant` and `msagent auth switch` both make a
    /// different profile active without touching the selection. A selection that was correctly
    /// tenant-checked when it was made can therefore be pointing at another tenant by the time a
    /// command uses it, and a command must not run there merely because the check once passed.
    /// </para>
    ///
    /// <para>
    /// The selection is rejected, never cleared. Silently forgetting it would send the next command at
    /// a target the operator did not choose, and would destroy the record of what was chosen.
    /// </para>
    /// </summary>
    public async Task<ActiveEnvironment> RequireActiveAsync(CancellationToken cancellationToken)
    {
        var profile = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        RequireComposedRing(profile?.Ring ?? PowerPlatformApiRing.Production);

        var binding = await GetActiveBindingAsync(cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            throw new EnvironmentNotSelectedException(
                "No Power Platform environment is selected for this command.");
        }

        if (!binding.IsSignedIn)
        {
            throw new NotSignedInException(
                $"No active AgentHost account is signed in, so environment '{binding.Environment.EnvironmentId}' cannot be used.",
                remediation: "Run `msagent auth login` to sign in, then retry.");
        }

        if (!binding.IsBound)
        {
            throw new EnvironmentTenantMismatchException(
                $"The selected environment '{binding.Environment.EnvironmentId}' is in tenant '{Describe(binding.Environment.TenantId)}', but the active account '{binding.AccountUsername}' is signed in to tenant '{Describe(binding.AccountTenantId)}'.",
                remediation: string.IsNullOrWhiteSpace(binding.Environment.TenantId)
                    ? "The stored selection records no tenant, so it cannot be safely used. Run `msagent env select` to make the selection again."
                    : $"Run `msagent auth login --tenant {binding.Environment.TenantId}` with an account in that tenant, or run `msagent env select` to choose an environment in the signed-in account's tenant.");
        }

        return binding.Environment;
    }

    private static string Describe(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? "<unknown>" : tenantId;

    private async Task<PowerPlatformEnvironment> ResolveAvailableAsync(
        string environmentId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireSignedInAsync(cancellationToken).ConfigureAwait(false);
        var requested = environmentId.Trim();
        var environments = await _environmentClient
            .ListEnvironmentsAsync(cancellationToken)
            .ConfigureAwait(false);

        var match = environments.FirstOrDefault(e =>
            string.Equals(e.EnvironmentId, requested, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new EnvironmentNotFoundException(
                $"Environment '{requested}' is not available to the signed-in account.");
        }

        // A command-scoped target receives the same checks as a persisted selection. Unknown tenant
        // data is a rejection rather than a reason to disable the boundary.
        if (string.IsNullOrWhiteSpace(profile.TenantId) ||
            string.IsNullOrWhiteSpace(match.TenantId) ||
            !string.Equals(profile.TenantId, match.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentTenantMismatchException(
                $"Environment '{match.EnvironmentId}' is in tenant '{Describe(match.TenantId)}', but the active account '{profile.Username}' is signed in to tenant '{Describe(profile.TenantId)}'.",
                remediation: string.IsNullOrWhiteSpace(match.TenantId)
                    ? "The environment inventory did not report a tenant for this environment, so it cannot be safely targeted. Re-run `msagent env list` to refresh the inventory."
                    : $"Run `msagent auth login --tenant {match.TenantId}` with an account in that tenant, or target an environment in the signed-in account's tenant.");
        }

        return match with
        {
            ApiEndpoint = _cloudMap.RequireTrusted(
                match.ApiEndpoint,
                $"environment '{match.EnvironmentId}'"),
        };
    }

    private async Task<AuthProfile> RequireSignedInAsync(CancellationToken cancellationToken)
    {
        var active = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        var profile = active ?? throw new NotSignedInException(
            "No active AgentHost account is signed in, so environments cannot be listed or selected.",
            remediation: "Run `msagent auth login` to sign in, then retry.");

        RequireComposedRing(profile.Ring);

        return profile;
    }

    /// <summary>
    /// Refuses to resolve a target when the active account has been pointed at another cloud since
    /// this process built its clients. Those clients still address the cloud they were built for, so
    /// resolving here would hand a command an endpoint and audience from the cloud the operator just
    /// left. Checked at this boundary because every command reaches a target through it.
    /// </summary>
    /// <param name="ring">The ring the active account records.</param>
    /// <exception cref="CloudRetargetedException">The account moved to another cloud.</exception>
    private void RequireComposedRing(PowerPlatformApiRing ring)
    {
        if (!IsRetargeted(ring))
        {
            return;
        }

        throw new CloudRetargetedException(
            $"This process targets the {PowerPlatformApiRingSelection.CanonicalToken(_apiTarget.Ring)} cloud, but the active account now targets {PowerPlatformApiRingSelection.CanonicalToken(ring)}.",
            remediation: "Run the command again — a new invocation picks up the account's cloud. An embedded host has to be rebuilt after `msagent auth switch --cloud`.");
    }

    private void ObserveResolved(PowerPlatformEnvironment environment)
    {
        if (_resolvedEnvironmentObserver is null)
        {
            return;
        }

        try
        {
            if (!_resolvedEnvironmentObserver.IsObservationEnabled)
            {
                return;
            }

            _resolvedEnvironmentObserver.Observe(
                new ResolvedEnvironmentTarget(
                    environment.EnvironmentId,
                    environment.TenantId));
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryEnvironmentObservation);
        }
    }
}
