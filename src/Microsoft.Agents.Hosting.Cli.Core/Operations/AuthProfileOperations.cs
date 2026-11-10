using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Coordinates token-free profile metadata with separately owned credential stages.</summary>
public sealed class AuthProfileOperations : IAuthProfileOperations
{
    private readonly IAuthProfileManagementStore _profiles;
    private readonly IProfileCredentialProvider _credentials;
    private readonly EnvironmentSelectionService _environments;
    private readonly IEnvironmentSelectionStore _selections;
    private readonly IInteractionPolicy _interaction;
    private readonly IClock _clock;
    private readonly IOwnedProfileCredentialCleanup? _cleanup;
    private readonly IAuthProfilePicker? _picker;
    private readonly IAuthenticationSession? _legacySession;
    private readonly EnvironmentSelectionScopes? _selectionScopes;

    public AuthProfileOperations(IAuthProfileManagementStore profiles, IProfileCredentialProvider credentials,
        EnvironmentSelectionService environments, IEnvironmentSelectionStore selections, IInteractionPolicy interaction,
        IClock clock, IOwnedProfileCredentialCleanup? cleanup = null, IAuthProfilePicker? picker = null,
        IAuthenticationSession? legacySession = null, EnvironmentSelectionScopes? selectionScopes = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _selections = selections ?? throw new ArgumentNullException(nameof(selections));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cleanup = cleanup;
        _picker = picker;
        _legacySession = legacySession;
        _selectionScopes = selectionScopes;
    }

    public async Task<AuthProfileOperationResult> CreateAsync(AuthCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AuthCreateInput.Validate(request, _interaction.IsInteractionAllowed);
        ValidateName(request.Name);
        ValidateEnvironment(request.Environment, request.Target);
        RequireTarget(request.Target);
        if (request.Scopes is null || request.Resource is not null && request.Scopes.Count != 0)
            throw new UsageException("Resource and explicit scopes cannot be combined.");
        bool eligibleWithoutUi = request.Username is not null || request.Password is not null
            || request.ClientSecret is not null || request.CertificateDiskPath is not null
            || request.ManagedIdentity || request.GitHubFederated || request.AzureDevOpsFederated;
        bool nonInteractive = request.NonInteractive || !_interaction.IsInteractionAllowed;
        if (!eligibleWithoutUi && nonInteractive)
            throw new InteractionRequiredException("Explicit interactive authentication is not allowed in this invocation.");
        cancellationToken.ThrowIfCancellationRequested();
        var creationStore = _profiles as IAuthProfileCreationStore
            ?? throw new AuthProfileStateException("Authentication creation requires atomic replacement inventory.");
        // Validate persisted metadata before staging; atomic creation checks names against the authenticated identity.
        await _profiles.GetProfilesAsync(cancellationToken).ConfigureAwait(false);

        AuthProfile? staged = null;
        AuthProfile? committed = null;
        bool finalized = false;
        bool cleanupPending = false;
        try
        {
            staged = await _credentials.AuthenticateAsync(
                WithInteraction(request, nonInteractive), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Environment is not null)
            {
                var environment = await _environments.ResolveForProfileAsync(staged, request.Environment, cancellationToken).ConfigureAwait(false);
                staged = staged with { DefaultEnvironment = Default(environment) };
            }
            cancellationToken.ThrowIfCancellationRequested();
            var creation = await creationStore.CreateWithReplacementAsync(staged, cancellationToken).ConfigureAwait(false);
            committed = creation.Committed;
            cleanupPending = creation.Replaced is { } old
                && _credentials.OwnsCredential(old) && old.CredentialReference != committed.CredentialReference;
            await _credentials.CompleteCreationAsync(committed, cancellationToken).ConfigureAwait(false);
            finalized = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (cleanupPending && creation.Replaced is { } replaced)
            {
                await RemoveIfUnownedAsync(replaced, cancellationToken).ConfigureAwait(false);
                cleanupPending = false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new("created", Describe(committed));
        }
        catch (Exception primary)
        {
            if (committed is not null)
            {
                // Translate retained adapter signals here; hosts consume the explicit Core state.
                if (primary is OperationCanceledException
                    && (primary is IAuthOperationPartialState { MetadataCommitted: true, CleanupIncomplete: false }
                        || primary.Data["msagent.metadata-committed"] is true
                            && !primary.Data.Contains("msagent.owned-cleanup")))
                    finalized = true;
                throw Partial(primary, true, !finalized || cleanupPending, Describe(committed));
            }
            bool cleanupIncomplete = primary is IAuthOperationPartialState { CleanupIncomplete: true }
                || primary.Data.Contains("msagent.owned-cleanup");
            if (staged is not null)
            {
                try
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _credentials.RemoveOwnedCredentialAsync(staged, budget.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    cleanupIncomplete = true;
                }
            }
            if (cleanupIncomplete || primary is OperationCanceledException)
                throw Partial(primary, false, cleanupIncomplete, null);
            throw;
        }
    }

    public async Task<AuthProfileListResult> ListAsync(CancellationToken cancellationToken) =>
        new((await _profiles.GetProfilesAsync(cancellationToken).ConfigureAwait(false))
            .Select((profile, index) => Describe(profile, index + 1)).ToList());

    public async Task<AuthProfileOperationResult> SelectAsync(AuthProfileSelectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selector);
        AuthProfile selected;
        if (request.Selector.Index is null && request.Selector.Name is null)
        {
            if (request.NonInteractive || !_interaction.IsInteractionAllowed)
                throw new InteractionRequiredException("An authentication profile selector is required when interaction is disabled.");
            if (_picker is null) throw new UsageException("Supply exactly one profile index or name; no profile picker is available.");
            var profiles = await _profiles.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
            var id = await _picker.PickAsync(profiles.Select((p, i) => Describe(p, i + 1)).ToList(), cancellationToken).ConfigureAwait(false);
            selected = profiles.SingleOrDefault(p => p.ProfileId == id)
                ?? throw new UsageException("No authentication profile was selected from the supplied inventory.");
            await _profiles.SetActiveAsync(selected.ProfileId, cancellationToken).ConfigureAwait(false);
            selected = selected with { IsActive = true };
        }
        else
        {
            ValidateSelector(request.Selector);
            selected = await _profiles.SelectAsync(request.Selector, cancellationToken).ConfigureAwait(false);
        }
        return new("selected", Describe(selected));
    }

    public async Task<AuthProfileOperationResult> DeleteAsync(AuthProfileSelector selector, CancellationToken cancellationToken)
    {
        ValidateSelector(selector);
        var deletion = await _profiles.DeleteAsync(selector, cancellationToken).ConfigureAwait(false);
        var deleted = Describe(deletion.Deleted) with { IsActive = false };
        var active = deletion.Active is null ? null : Describe(deletion.Active);
        bool cleanupComplete = false;
        try
        {
            await RemoveIfUnownedAsync(deletion.Deleted, cancellationToken).ConfigureAwait(false);
            cleanupComplete = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception primary)
        {
            throw Partial(primary, true, !cleanupComplete, deleted, profileDeleted: true, active);
        }
        return new("deleted", deleted, active);
    }

    public async Task<AuthProfileOperationResult> NameAsync(AuthProfileSelector selector, string name, CancellationToken cancellationToken)
    {
        ValidateSelector(selector);
        if (name is null) throw new UsageException("An authentication profile name is required.");
        ValidateName(name);
        var profile = await _profiles.UpdateAsync(selector, new(Name: name), cancellationToken).ConfigureAwait(false);
        return new("named", Describe(profile));
    }

    public async Task<AuthWhoResult> WhoAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        var binding = await _environments.GetActiveBindingAsync(profile, cancellationToken).ConfigureAwait(false);
        return new(profile is null ? "signed-out" : "selected", profile is null ? null : Describe(profile),
            binding is null ? null : DescribeEnvironment(binding.Environment), binding?.IsBound);
    }

    public async Task<AuthLogoutResult> ClearAsync(CancellationToken cancellationToken)
    {
        // Resolve cleanup availability before mutation, without requiring readable profile metadata.
        if (_legacySession is null && _cleanup is null)
            throw new AuthProfileStateException("Owned credential cleanup is unavailable; authentication state was not cleared.");
        bool committed = false;
        bool cleanupComplete = false;
        try
        {
            await _profiles.ClearAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            await _selections.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (_selectionScopes is not null)
                await _selectionScopes.ClearAllAsync(cancellationToken).ConfigureAwait(false);
            // The registered session already performs owned cleanup and dedicated legacy cleanup.
            if (_legacySession is not null)
                await _legacySession.ClearCacheAsync(cancellationToken).ConfigureAwait(false);
            else
                await _cleanup!.ClearOwnedCredentialsAsync(cancellationToken).ConfigureAwait(false);
            cleanupComplete = true;
            cancellationToken.ThrowIfCancellationRequested();
            return new() { Status = "signed-out" };
        }
        catch (Exception primary) when (committed)
        {
            throw Partial(primary, true, !cleanupComplete, null);
        }
    }

    private async Task RemoveIfUnownedAsync(AuthProfile profile, CancellationToken cancellationToken)
    {
        if (!_credentials.OwnsCredential(profile)) return;
        var remaining = await _profiles.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (remaining.Any(p => p.CredentialReference == profile.CredentialReference)) return;
        // The provider rechecks ownership under its exclusive reference lease before removal.
        await _credentials.RemoveOwnedCredentialAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    internal static AuthProfileInfo Describe(AuthProfile profile, int? index = null) => new()
    {
        ProfileId = profile.ProfileId, Index = index, IsActive = profile.IsActive, Name = profile.Name,
        Account = profile.Username, TenantId = profile.TenantId, Cloud = CloudToken(profile),
        ClientId = profile.ClientId, PrincipalType = profile.PrincipalType, CredentialMethod = profile.CredentialMethod,
        ConnectionUrl = profile.ConnectionUrl,
        DefaultEnvironment = profile.DefaultEnvironment is null ? null : DescribeEnvironment(profile.DefaultEnvironment),
    };

    internal static string CloudToken(AuthProfile profile) => profile.Cloud == CloudInstance.Public
        ? PowerPlatformApiRingSelection.CanonicalToken(profile.Ring)
        : PowerPlatformApiTargetSelection.CanonicalToken(profile.Cloud, profile.Ring);

    private static AuthEnvironmentInfo DescribeEnvironment(ActiveEnvironment environment) =>
        new(environment.EnvironmentId, environment.DisplayName, environment.TenantId, environment.ApiEndpoint.AbsoluteUri, environment.InstanceUrl);

    private ActiveEnvironment Default(PowerPlatformEnvironment environment) => new()
    {
        EnvironmentId = environment.EnvironmentId, DisplayName = environment.DisplayName, TenantId = environment.TenantId,
        ApiEndpoint = environment.ApiEndpoint, ApiAudience = environment.ApiAudience, InstanceUrl = environment.InstanceUrl,
        SelectedAt = _clock.UtcNow,
    };

    internal static Exception Partial(Exception primary, bool committed, bool cleanupIncomplete, AuthProfileInfo? profile) =>
        Partial(primary, committed, cleanupIncomplete, profile, false, null);

    private static Exception Partial(Exception primary, bool committed, bool cleanupIncomplete, AuthProfileInfo? profile,
        bool profileDeleted, AuthProfileInfo? activeProfile) =>
        primary is OperationCanceledException canceled
            ? new AuthOperationCanceledException(committed, cleanupIncomplete, profile, canceled, profileDeleted, activeProfile)
            : new AuthOperationException(committed, cleanupIncomplete, profile,
                primary as AgentHostingException ?? new AuthProfileStateException(
                    "Authentication storage or finalization failed.", "Repair owned authentication storage before retrying."),
                profileDeleted, activeProfile);

    private static void ValidateSelector(AuthProfileSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Index.HasValue == (selector.Name is not null)
            || selector.Index is <= 0 || selector.Name is "")
            throw new UsageException("Supply exactly one one-based profile index or case-sensitive name.");
    }

    private static void ValidateName(string? name)
    {
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 30 || name.Any(char.IsControl)))
            throw new UsageException("An authentication profile name must contain 1 to 30 characters.");
    }

    internal static PowerPlatformApiTargetConfig RequireTarget(PowerPlatformApiTargetConfig target)
    {
        try { return PowerPlatformApiTargetSelection.Require(target); }
        catch (ArgumentException) { throw new UsageException("A registered Power Platform API cloud target is required."); }
    }

    private static void ValidateEnvironment(string? environment, PowerPlatformApiTargetConfig target)
    {
        if (environment is null) return;
        if (string.IsNullOrWhiteSpace(environment) || environment.Length > 2048 || environment.Any(char.IsControl))
            throw new UsageException("An environment id, URL, or name is required and must be bounded.");
        if (environment.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(environment.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath is not ("" or "/")
                || !PowerPlatformCloudMap.ForTarget(RequireTarget(target)).IsTrustedDataverseOrganization(uri))
                throw new EndpointTrustException("The environment URL is not a trusted organization URL for the requested cloud.");
        }
    }

    private static AuthCreateRequest WithInteraction(AuthCreateRequest request, bool nonInteractive) => new()
    {
        Name = request.Name, Environment = request.Environment, Target = request.Target, Tenant = request.Tenant,
        LoginHint = request.LoginHint, Resource = request.Resource, Scopes = request.Scopes, DeviceCode = request.DeviceCode,
        Username = request.Username, Password = request.Password, ApplicationId = request.ApplicationId, ClientSecret = request.ClientSecret,
        CertificateDiskPath = request.CertificateDiskPath, CertificatePassword = request.CertificatePassword,
        ManagedIdentity = request.ManagedIdentity, GitHubFederated = request.GitHubFederated, AzureDevOpsFederated = request.AzureDevOpsFederated,
        NonInteractive = nonInteractive,
    };
}
