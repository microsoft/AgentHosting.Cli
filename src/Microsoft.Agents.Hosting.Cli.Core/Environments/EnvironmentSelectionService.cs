using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// Profile-bound inventory and target resolution. Explicit targets precede owned overrides and
/// profile defaults. Inventory validation runs outside profile transactions; selection commits use
/// the captured active identity and revision.
/// </summary>
public sealed class EnvironmentSelectionService
{
    private readonly IEnvironmentClient? _environmentClient;
    private readonly IProfileEnvironmentClientFactory? _clientFactory;
    private readonly IEnvironmentSelectionStore _selectionStore;
    private readonly IAuthProfileStore _profileStore;
    private readonly IClock _clock;
    private readonly IResolvedEnvironmentObserver? _resolvedEnvironmentObserver;
    private readonly PowerPlatformApiTargetConfig _apiTarget;

    /// <summary>
    /// Compatibility composition for a fixed, injected inventory client. Both cloud and ring must
    /// continue to match that client's composed target.
    /// </summary>
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
        ArgumentNullException.ThrowIfNull(cloudMap);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolvedEnvironmentObserver = resolvedEnvironmentObserver;
        _apiTarget = PowerPlatformApiTargetSelection.Require(apiTarget ?? PowerPlatformApiRingSelection.Default);
    }

    /// <summary>Composition supporting staged, inactive, and per-profile cloud targets.</summary>
    public EnvironmentSelectionService(
        IProfileEnvironmentClientFactory clientFactory,
        IEnvironmentSelectionStore selectionStore,
        IAuthProfileStore profileStore,
        IClock clock,
        IResolvedEnvironmentObserver? resolvedEnvironmentObserver = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolvedEnvironmentObserver = resolvedEnvironmentObserver;
        _apiTarget = PowerPlatformApiTargetConfig.Public;
    }

    /// <summary>Compatibility ring-only check; use the profile overload to check both coordinates.</summary>
    public bool IsRetargeted(PowerPlatformApiRing ring) => _clientFactory is null && _apiTarget.Ring != ring;

    /// <summary>Whether a fixed-client composition no longer matches the captured profile.</summary>
    public bool IsRetargeted(AuthProfile profile) =>
        _clientFactory is null && (_apiTarget.Cloud != profile.Cloud || _apiTarget.Ring != profile.Ring);

    public async Task<EnvironmentListResult> ListAsync(CancellationToken cancellationToken)
    {
        var context = await CaptureActiveAsync(cancellationToken).ConfigureAwait(false);
        var environments = await ListAvailableAsync(context, cancellationToken).ConfigureAwait(false);
        var active = await ResolveDefaultAsync(context, required: false, cancellationToken).ConfigureAwait(false);
        return new EnvironmentListResult
        {
            Status = "listed",
            Environments = environments.Select(environment => new EnvironmentSummary
            {
                EnvironmentId = environment.EnvironmentId,
                DisplayName = environment.DisplayName,
                TenantId = environment.TenantId,
                ApiEndpoint = environment.ApiEndpoint.ToString(),
                IsActive = active is not null && SameId(active.EnvironmentId, environment.EnvironmentId),
            }).ToList(),
        };
    }

    public async Task<IReadOnlyList<PowerPlatformEnvironment>> ListAvailableAsync(CancellationToken cancellationToken) =>
        await ListAvailableAsync(await CaptureActiveAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<EnvironmentSelectionResult> SelectAsync(string environmentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new UsageException("An environment id is required for `msagent env select`.");

        var context = await CaptureActiveAsync(cancellationToken).ConfigureAwait(false);
        var management = _profileStore as IAuthProfileManagementStore
            ?? throw new UsageException("Environment selection requires a profile store supporting transactional default updates.");
        var match = await ResolveAvailableAsync(context, environmentId, cancellationToken).ConfigureAwait(false);
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
        var committed = await management.SetDefaultEnvironmentAsync(
            context.Profile.ProfileId, context.Profile.Revision, selection, cancellationToken).ConfigureAwait(false);
        selection = committed.DefaultEnvironment!;
        try
        {
            await _selectionStore.SetActiveAsync(selection, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentHostingException ex)
        {
            throw new EnvironmentSelectionCommittedException(committed, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new EnvironmentSelectionCommittedException(committed,
                new AgentHostingStateException("The secondary environment selection could not be persisted.",
                    "Check selection storage and the profile default before retrying."));
        }

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

    /// <summary>Resolves without mutating state and carries the captured profile to downstream calls.</summary>
    public async Task<PowerPlatformEnvironment> ResolveForCommandAsync(string? environmentId, CancellationToken cancellationToken)
    {
        if (environmentId is not null && string.IsNullOrWhiteSpace(environmentId))
            throw new UsageException("An explicit environment target must not be empty.");

        var context = await CaptureActiveAsync(cancellationToken).ConfigureAwait(false);
        PowerPlatformEnvironment resolved;
        if (environmentId is null)
        {
            var active = (await ResolveDefaultAsync(context, required: true, cancellationToken).ConfigureAwait(false))!;
            resolved = new PowerPlatformEnvironment
            {
                EnvironmentId = active.EnvironmentId, DisplayName = active.DisplayName, TenantId = active.TenantId,
                ApiEndpoint = active.ApiEndpoint, ApiAudience = active.ApiAudience, InstanceUrl = active.InstanceUrl,
                Context = context,
            };
        }
        else
        {
            resolved = await ResolveAvailableAsync(context, environmentId, cancellationToken).ConfigureAwait(false);
        }

        ObserveResolved(resolved);
        return resolved;
    }

    /// <summary>Validates an environment target against the exact supplied profile, including staged creation.</summary>
    public Task<PowerPlatformEnvironment> ResolveForProfileAsync(
        AuthProfile profile, string environment, CancellationToken cancellationToken) =>
        ResolveAvailableAsync(Capture(profile), environment, cancellationToken);

    /// <summary>Reads selection metadata without looking up an account or constructing a client.</summary>
    public async Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var active = await _selectionStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is null) return null;
        return ValidateStored(active, MetadataTarget(active), resolveEndpoint: false);
    }

    /// <summary>Explicit local reset without profile reads, client construction, or authentication.</summary>
    public Task ClearSelectionsAsync(CancellationToken cancellationToken) =>
        _selectionStore.ClearAsync(cancellationToken);

    public async Task<ActiveEnvironmentBinding?> GetActiveBindingAsync(CancellationToken cancellationToken) =>
        await GetActiveBindingAsync(
            await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Status accessor for an already captured profile. Unowned legacy metadata remains visible;
    /// this path never constructs an inventory client or acquires credentials.
    /// </summary>
    public async Task<ActiveEnvironmentBinding?> GetActiveBindingAsync(AuthProfile? profile, CancellationToken cancellationToken)
    {
        var active = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        bool isProfileDefault = false;
        if ((active is null || !EnvironmentProfileBinding.IsCurrentOverride(active, profile))
            && profile?.DefaultEnvironment is { } profileDefault)
        {
            RequireTenant(profile, profileDefault.TenantId);
            active = ValidateStored(profileDefault, PowerPlatformApiTargetSelection.TargetFor(profile.Cloud, profile.Ring), resolveEndpoint: false);
            isProfileDefault = true;
        }
        return active is null ? null : new ActiveEnvironmentBinding
        {
            Environment = active,
            IsSignedIn = profile is not null,
            AccountUsername = profile?.Username,
            AccountTenantId = profile?.TenantId,
            IsBound = isProfileDefault
                ? active.Owner is null || EnvironmentProfileBinding.IsOwnedBy(active, profile)
                : EnvironmentProfileBinding.IsCurrentOverride(active, profile),
        };
    }

    /// <summary>
    /// Compatibility lifecycle hook. Ownership is checked on use, never erased on account changes.
    /// In particular legacy selections cannot be adopted or discarded using tenant equality.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Retained instance lifecycle API for existing embedded hosts.")]
    public Task<bool> ClearSelectionsOnIdentityChangeAsync(
        AuthProfile? previous, AuthProfile? current, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public async Task<ActiveEnvironment> RequireActiveAsync(CancellationToken cancellationToken)
    {
        var context = await CaptureActiveAsync(cancellationToken).ConfigureAwait(false);
        return (await ResolveDefaultAsync(context, required: true, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<ActiveEnvironment?> ResolveDefaultAsync(
        EnvironmentResolutionContext context, bool required, CancellationToken cancellationToken)
    {
        var profile = context.Profile;
        var persisted = await _selectionStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        // Even ignored selections must remain typed failures when their metadata is unusable.
        if (persisted is not null)
        {
            persisted = ValidateStored(persisted, MetadataTarget(persisted), resolveEndpoint: false);
        }
        var selected = persisted is not null && EnvironmentProfileBinding.IsCurrentOverride(persisted, profile)
            ? persisted : profile.DefaultEnvironment;
        if (selected is null)
        {
            if (!required) return null;
            throw new EnvironmentNotSelectedException("No owned Power Platform environment override or profile default is selected for this command.");
        }

        RequireTenant(profile, selected.TenantId);
        if (selected.Owner is not null && !EnvironmentProfileBinding.IsOwnedBy(selected, profile))
            throw new EnvironmentNotSelectedException("The profile default environment does not belong to the captured authentication profile.");
        return ValidateStored(selected, context.Target) with { Context = context };
    }

    private async Task<PowerPlatformEnvironment> ResolveAvailableAsync(
        EnvironmentResolutionContext context, string environment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environment))
            throw new UsageException("An environment id, URL, or name is required.");
        var requested = environment.Trim();
        if (requested.Length > 2048 || requested.Any(char.IsControl))
            throw new UsageException("The environment target is not a bounded id, URL, or name.");
        Uri? requestedUrl = null;
        if (requested.Contains("://", StringComparison.Ordinal))
        {
            requestedUrl = RequireOrganizationUrl(requested, PowerPlatformCloudMap.ForTarget(context.Target));
        }
        var environments = await ListAvailableAsync(context, cancellationToken).ConfigureAwait(false);
        var exact = environments.Where(e => requestedUrl is null
            && SameId(NormalizeId(e.EnvironmentId), NormalizeId(requested))).ToList();
        if (exact.Count == 0)
            exact = environments.Where(e => requestedUrl is not null
                ? Uri.TryCreate(e.InstanceUrl, UriKind.Absolute, out var linkedUrl) && linkedUrl == requestedUrl
                : SameId(e.UniqueName ?? string.Empty, requested) || SameId(e.DisplayName, requested)).ToList();
        var matches = exact.Count > 0 || requestedUrl is not null
            ? exact
            : environments.Where(e => e.DisplayName.Contains(requested, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
            throw new UsageException("The environment target is ambiguous. Supply an exact environment id or linked Dataverse URL.");
        var match = matches.SingleOrDefault()
            ?? throw new EnvironmentNotFoundException("The requested environment is not available to the captured authentication profile.");
        return match with
        {
            ApiEndpoint = ValidateCoordinates(match.EnvironmentId, match.ApiEndpoint, match.ApiAudience,
                match.InstanceUrl, match.AgentManagementUrl, context.Target),
        };
    }

    private async Task<IReadOnlyList<PowerPlatformEnvironment>> ListAvailableAsync(
        EnvironmentResolutionContext context, CancellationToken cancellationToken)
    {
        var client = _clientFactory?.CreateClient(context) ?? _environmentClient!;
        var environments = await client.ListEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        return environments.Select(environment =>
        {
            RequireTenant(context.Profile, environment.TenantId);
            return environment with
            {
                // Enumeration leaves unavailable optional service endpoints visible to their
                // capability-specific reachability reports. Resolution validates them before use.
                ApiEndpoint = ValidateCoordinates(environment.EnvironmentId, environment.ApiEndpoint, environment.ApiAudience,
                    null, null, context.Target),
                Context = context,
            };
        }).ToList();
    }

    private static ActiveEnvironment ValidateStored(
        ActiveEnvironment environment, PowerPlatformApiTargetConfig target, bool resolveEndpoint = true)
    {
        if (!EnvironmentProfileBinding.HasValidOwnershipMetadata(environment))
            throw new AgentHostingStateException("The stored environment selection ownership is incomplete or invalid.");
        return environment with
        {
            ApiEndpoint = ValidateCoordinates(environment.EnvironmentId, environment.ApiEndpoint, environment.ApiAudience,
                environment.InstanceUrl, null, target, resolveEndpoint),
        };
    }

    private static PowerPlatformApiTargetConfig MetadataTarget(ActiveEnvironment environment)
    {
        if (!EnvironmentProfileBinding.HasValidOwnershipMetadata(environment))
            throw new AgentHostingStateException("The stored environment selection ownership is incomplete or invalid.");
        if (environment.Owner is { } owner)
            return PowerPlatformApiTargetSelection.TargetFor(owner.Cloud, owner.Ring);

        // Classifying unbound metadata against the registry never assigns it to a profile.
        // Actual resolution still requires ownership and the captured profile's target.
        foreach (var token in PowerPlatformApiTargetSelection.AcceptedTokens)
        {
            if (PowerPlatformApiTargetSelection.TryResolve(token, out var target, out _)
                && Uri.TryCreate(environment.ApiAudience, UriKind.Absolute, out var audience)
                && audience == new Uri(target.Audience))
                return target;
        }
        throw new EndpointTrustException("The stored environment selection does not advertise a registered Power Platform audience.");
    }

    private static Uri ValidateCoordinates(
        string environmentId, Uri endpoint, string audience, string? instanceUrl, string? managementUrl,
        PowerPlatformApiTargetConfig target, bool resolveEndpoint = true)
    {
        var map = PowerPlatformCloudMap.ForTarget(target);
        if (string.IsNullOrWhiteSpace(environmentId) || !IsCleanUrl(endpoint) || !Uri.TryCreate(audience, UriKind.Absolute, out var audienceUri)
            || !IsCleanUrl(audienceUri) || audienceUri != new Uri(target.Audience))
            throw new EndpointTrustException("The environment endpoint or audience is not compatible with the captured cloud target.");
        var trusted = map.RequireTrusted(endpoint, "Power Platform environment");
        if (instanceUrl is not null) RequireOrganizationUrl(instanceUrl, map);
        if (managementUrl is not null)
        {
            if (!Uri.TryCreate(managementUrl, UriKind.Absolute, out var gateway) || !IsCleanUrl(gateway))
                throw new EndpointTrustException("The environment advertises an unusable Copilot Studio gateway.");
            map.RequireTrustedAgentManagementGateway(gateway, "Copilot Studio gateway");
        }
        if (!resolveEndpoint || trusted != target.GlobalEndpoint) return trusted;
        if (target.Cloud == Auth.CloudInstance.China)
            throw new EnvironmentNotSelectedException("Derived environment routing is unsupported for China. A verified advertised environment endpoint is required.");
        try
        {
            return PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, environmentId);
        }
        catch (ArgumentException)
        {
            throw new EndpointTrustException("The environment identifier cannot address a verified environment endpoint.");
        }
    }

    private static bool IsCleanUrl(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static Uri RequireOrganizationUrl(string value, PowerPlatformCloudMap map)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !IsCleanUrl(uri) || uri.AbsolutePath != "/")
            throw new EndpointTrustException("The environment target is not a trusted Dataverse organization URL.");
        if (!map.IsTrustedDataverseOrganization(uri))
            throw map.UntrustedDataverseOrganization(uri, "<requested>");
        return uri;
    }

    private static string NormalizeId(string id)
    {
        const string prefix = "/providers/Microsoft.PowerApps/environments/";
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
    }

    private async Task<EnvironmentResolutionContext> CaptureActiveAsync(CancellationToken cancellationToken)
    {
        var profile = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new NotSignedInException("No active AgentHost account is signed in.",
                remediation: "Run `msagent auth create` to sign in, then retry.");
        return Capture(profile);
    }

    private EnvironmentResolutionContext Capture(AuthProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (IsRetargeted(profile))
            throw new CloudRetargetedException(
                $"This process's fixed environment client targets {PowerPlatformApiTargetSelection.CanonicalToken(_apiTarget.Cloud, _apiTarget.Ring).ToLowerInvariant()}, but the captured profile targets {PowerPlatformApiTargetSelection.CanonicalToken(profile.Cloud, profile.Ring).ToLowerInvariant()}.",
                "Run the command again, or compose a profile-bound environment client factory.");
        return new(profile, PowerPlatformApiTargetSelection.TargetFor(profile.Cloud, profile.Ring));
    }

    private static bool SameId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void RequireTenant(AuthProfile profile, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(profile.TenantId) || string.IsNullOrWhiteSpace(tenantId)
            || !string.Equals(profile.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            throw new EnvironmentTenantMismatchException(
                $"The environment tenant '{DescribeTenant(tenantId)}' does not match the captured authentication profile tenant '{DescribeTenant(profile.TenantId)}'.",
                "Select an environment belonging to the profile's tenant.");
    }

    private static string DescribeTenant(string? value) =>
        Guid.TryParse(value, out var tenant) ? tenant.ToString("D") : "<unknown>";

    private void ObserveResolved(PowerPlatformEnvironment environment)
    {
        if (_resolvedEnvironmentObserver is null) return;
        try
        {
            if (!_resolvedEnvironmentObserver.IsObservationEnabled) return;
            var target = new ResolvedEnvironmentTarget(environment.EnvironmentId, environment.TenantId);
            if (_resolvedEnvironmentObserver is IProfileResolvedEnvironmentObserver captured && environment.Context is { } context)
                captured.Observe(target, context);
            else
                _resolvedEnvironmentObserver.Observe(target);
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(OptionalMeasurementFailureCategory.TelemetryEnvironmentObservation);
        }
    }
}

/// <summary>A profile default committed successfully, but its secondary selection write failed.</summary>
public sealed class EnvironmentSelectionCommittedException : AgentHostingException
{
    public AuthProfile CommittedProfile { get; }

    public EnvironmentSelectionCommittedException(AuthProfile committedProfile, AgentHostingException secondary)
        : base("The profile default environment was committed; the secondary environment selection write failed.",
            secondary.ExitCode, secondary.ErrorKind, secondary.Remediation, secondary, secondary.ErrorCode)
    {
        CommittedProfile = committedProfile;
    }
}
