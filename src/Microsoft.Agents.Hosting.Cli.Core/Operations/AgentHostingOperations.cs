using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>
/// The <see cref="IAgentHostingOperations"/> facade orchestrates
/// (<see cref="IAuthenticationSession"/>, <see cref="IAuthProfileStore"/>, <see cref="IInteractionPolicy"/>,
/// <see cref="IPlatformInfo"/>, <see cref="IInteractiveMessagePresenter"/>) and contains no MSAL or Azure
/// type — those live only in the <c>Microsoft.Agents.Hosting.Cli.Auth</c> adapter project, so this core stays
/// embeddable and the host's offline guarantee holds. It delegates <c>abs preview</c> to the read-only
/// <see cref="AbsProvisioningService"/>, <c>abs apply</c> to <see cref="AbsApplyService"/>, and
/// <c>env list</c>/<c>env select</c> to <see cref="EnvironmentSelectionService"/>.
///
/// Every method is offline-testable with fakes: the auth verbs never construct MSAL directly, never
/// read the environment, and only <see cref="LoginAsync"/> may open identity UI — and only after the
/// non-interactive gate passes. The environment verbs cannot open identity UI at all.
/// </summary>
public sealed class AgentHostingOperations : IAgentHostingOperations, IAgentEvaluationOperations
{
    private readonly IAuthenticationSession _session;
    private readonly IAuthProfileStore _profileStore;
    private readonly IInteractionPolicy _interactionPolicy;
    private readonly IPlatformInfo _platform;
    private readonly IInteractiveMessagePresenter _messagePresenter;
    private readonly AuthSessionContext _sessionContext;
    private readonly AbsProvisioningService _absProvisioning;
    private readonly AbsApplyService _absApply;
    private readonly EnvironmentSelectionService _environments;
    private readonly AgentCreateService _agentCreate;
    private readonly AgentPackageUploadService _agentPackageUpload;
    private readonly AgentDeployService _agentDeploy;
    private readonly BlueprintIdentityService? _blueprintIdentity;
    private readonly AppRegistrationIdentityService? _appRegistrationIdentity;
    private readonly ManagedAgentServices? _managedAgents;
    private readonly DeploymentService? _deployments;
    private readonly DeploymentOAuthService? _deploymentOAuth;
    private readonly DeploymentChannelService? _deploymentChannels;
    private readonly EvaluationServices? _evaluations;

    public AgentHostingOperations(
        IAuthenticationSession session,
        IAuthProfileStore profileStore,
        IInteractionPolicy interactionPolicy,
        IPlatformInfo platform,
        IInteractiveMessagePresenter messagePresenter,
        AuthSessionContext sessionContext,
        AbsProvisioningService absProvisioning,
        AbsApplyService absApply,
        EnvironmentSelectionService environments,
        AgentCreateService agentCreate,
        AgentPackageUploadService agentPackageUpload,
        AgentDeployService agentDeploy,
        BlueprintIdentityService? blueprintIdentity = null,
        AppRegistrationIdentityService? appRegistrationIdentity = null,
        ManagedAgentServices? managedAgents = null,
        DeploymentService? deployments = null,
        DeploymentOAuthService? deploymentOAuth = null,
        DeploymentChannelService? deploymentChannels = null)
        : this(
            session,
            profileStore,
            interactionPolicy,
            platform,
            messagePresenter,
            sessionContext,
            absProvisioning,
            absApply,
            environments,
            agentCreate,
            agentPackageUpload,
            agentDeploy,
            blueprintIdentity,
            appRegistrationIdentity,
            managedAgents,
            deployments,
            deploymentOAuth,
            deploymentChannels,
            evaluations: null)
    {
    }

    /// <summary>
    /// Creates the facade, including the evaluation stack.
    /// </summary>
    public AgentHostingOperations(
        IAuthenticationSession session,
        IAuthProfileStore profileStore,
        IInteractionPolicy interactionPolicy,
        IPlatformInfo platform,
        IInteractiveMessagePresenter messagePresenter,
        AuthSessionContext sessionContext,
        AbsProvisioningService absProvisioning,
        AbsApplyService absApply,
        EnvironmentSelectionService environments,
        AgentCreateService agentCreate,
        AgentPackageUploadService agentPackageUpload,
        AgentDeployService agentDeploy,
        BlueprintIdentityService? blueprintIdentity,
        AppRegistrationIdentityService? appRegistrationIdentity,
        ManagedAgentServices? managedAgents,
        DeploymentService? deployments,
        DeploymentOAuthService? deploymentOAuth,
        DeploymentChannelService? deploymentChannels,
        EvaluationServices? evaluations)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _interactionPolicy = interactionPolicy ?? throw new ArgumentNullException(nameof(interactionPolicy));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _messagePresenter = messagePresenter ?? throw new ArgumentNullException(nameof(messagePresenter));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _absProvisioning = absProvisioning ?? throw new ArgumentNullException(nameof(absProvisioning));
        _absApply = absApply ?? throw new ArgumentNullException(nameof(absApply));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _agentCreate = agentCreate ?? throw new ArgumentNullException(nameof(agentCreate));
        _agentPackageUpload = agentPackageUpload ?? throw new ArgumentNullException(nameof(agentPackageUpload));
        _agentDeploy = agentDeploy ?? throw new ArgumentNullException(nameof(agentDeploy));
        _blueprintIdentity = blueprintIdentity;
        _appRegistrationIdentity = appRegistrationIdentity;
        _managedAgents = managedAgents;
        _deployments = deployments;
        _deploymentOAuth = deploymentOAuth;
        _deploymentChannels = deploymentChannels;
        _evaluations = evaluations;
    }
    /// <inheritdoc />
    public Task<ManagedAgentCreateResult> CreateManagedAgentAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = RequireManagedAgents("agent create");

        return ManagedAgents.Storage.AgentTypes.IsMcsWorkflow(request.AgentType)
            ? services.WorkflowCreate.CreateAsync(request, cancellationToken)
            : services.Create.CreateAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ManagedAgentInitResult> InitManagedAgentAsync(ManagedAgentInitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = RequireManagedAgents("agent init");

        return ManagedAgents.Storage.AgentTypes.IsMcsWorkflow(request.AgentType)
            ? services.WorkflowInit.InitAsync(request, cancellationToken)
            : services.Init.InitAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ManagedAgentListResult> ListManagedAgentsAsync(ManagedAgentListRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("agent list").List.ListAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ManagedAgentShowResult> ShowManagedAgentAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("agent show").Show.ShowAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ManagedAgentDeleteResult> DeleteManagedAgentAsync(ManagedAgentDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = RequireManagedAgents("agent delete");

        return services.SelectsWorkflow(request.Project, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName)
            ? services.WorkflowDelete.DeleteAsync(request, cancellationToken)
            : services.Delete.DeleteAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ManagedAgentShareResult> ShareManagedAgentAsync(ManagedAgentShareRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("agent share").Share.ShareAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ManagedAgentShareShowResult> ShowManagedAgentSharesAsync(ManagedAgentShareShowRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("agent share show").Share.ShowAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ManagedAgentUnshareResult> UnshareManagedAgentAsync(ManagedAgentUnshareRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("agent unshare").Share.UnshareAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ManagedAgentPullResult> PullManagedAgentAsync(ManagedAgentPullRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = RequireManagedAgents("agent pull");

        return services.SelectsWorkflow(request.Project, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName)
            ? services.WorkflowPull.PullAsync(request, cancellationToken)
            : services.Pull.PullAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ManagedAgentDeployResult> DeployManagedAgentAsync(ManagedAgentDeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = RequireManagedAgents("agent deploy");

        return services.SelectsWorkflow(request.Project, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName)
            ? services.WorkflowDeploy.DeployAsync(request, cancellationToken)
            : services.Deploy.DeployAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentDeployResult> UpdateAgentDeploymentPackageAsync(
        AgentDeploymentPackageUpdateRequest request,
        CancellationToken cancellationToken) =>
        _agentDeploy.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentCreateResult> CreateAgentDeploymentAsync(AgentDeploymentCreateRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment create").DeploymentCreate.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentListResult> ListAgentDeploymentsAsync(AgentDeploymentListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(RequireManagedAgents("deployment list").DeploymentList.List(request));

    /// <inheritdoc />
    public Task<AgentDeploymentShowResult> ShowAgentDeploymentAsync(AgentDeploymentShowRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(RequireManagedAgents("deployment show").DeploymentShow.Show(request));

    /// <inheritdoc />
    public Task<AgentDeploymentDeleteResult> DeleteAgentDeploymentAsync(AgentDeploymentDeleteRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment delete").DeploymentDeletes.DeleteAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentUpdateResult> UpdateAgentDeploymentAsync(AgentDeploymentUpdateRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment update").DeploymentUpdates.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentConnectionResult> UpdateAgentDeploymentConnectionAsync(AgentDeploymentConnectionRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment connection update").DeploymentConnections.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentConnectionResult> ShowAgentDeploymentConnectionAsync(AgentDeploymentConnectionShowRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment connection list").DeploymentConnectionReads.ShowAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentConnectorListResult> ListAgentDeploymentConnectorsAsync(AgentDeploymentConnectorListRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment connection connectors").DeploymentConnectors.ListAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentConnectionCreateResult> CreateAgentDeploymentConnectionAsync(AgentDeploymentConnectionCreateRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment connection add").DeploymentConnectionCreates.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentConnectionReferenceCreateResult> CreateAgentDeploymentConnectionReferenceAsync(AgentDeploymentConnectionReferenceCreateRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment connection add").DeploymentConnectionReferenceCreates.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeploymentConnectionRemoveResult> RemoveAgentDeploymentConnectionAsync(AgentDeploymentConnectionRemoveRequest request, CancellationToken cancellationToken) =>
        RequireManagedAgents("deployment connection remove").DeploymentConnectionRemoves.RemoveAsync(request, cancellationToken);

    /// <inheritdoc />
    public DeploymentAgentTypeResult ResolveDeploymentAgentType(DeploymentAgentTypeQuery query) =>
        RequireManagedAgents("deployment").AgentTypes.Resolve(query);

    private ManagedAgentServices RequireManagedAgents(string verb) =>
        _managedAgents ?? throw new PendingImplementationException(verb);

    /// <inheritdoc />
    public async Task<AuthLoginResult> LoginAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Non-interactive gate first: fail before any session or flow is touched so no identity UI or
        // device-code callback can fire.
        if (request.NonInteractive || !_interactionPolicy.IsInteractionAllowed)
        {
            throw new InteractionRequiredException(
                "Interactive sign-in is required but interaction is disabled (--non-interactive).",
                remediation: "Re-run `msagent auth login` without --non-interactive on a machine where a browser or the OS broker is available.");
        }

        var flow = AuthLoginFlowSelector.Select(request, _platform);
        var profiles = await _profileStore.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        var previous = profiles.FirstOrDefault(profile => profile.IsActive);
        var ring = RequestedRing(request, profiles);
        var (account, completedFlow) = await SignInAsync(flow, request with { Ring = ring }, cancellationToken).ConfigureAwait(false);

        // PAC's corporate-account protection: block a browser (non-broker) sign-in of a Microsoft
        // employee; broker and device-code flows are exempt.
        MicrosoftCorporateAccountPolicy.EnforceAfterLogin(completedFlow, account);

        bool authenticatedContextMatchesClient = string.Equals(
            string.IsNullOrWhiteSpace(account.AuthenticatedClientId)
                ? null
                : account.AuthenticatedClientId,
            string.IsNullOrWhiteSpace(_sessionContext.ClientId)
                ? null
                : _sessionContext.ClientId,
            StringComparison.OrdinalIgnoreCase);

        var profile = new AuthProfile
        {
            AccountKey = account.HomeAccountId,
            Username = account.Username,
            TenantId = account.TenantId,
            ClientId = _sessionContext.ClientId,
            Authority = _sessionContext.Authority,
            Cloud = _sessionContext.Cloud,
            Ring = ring,
            FlowType = ToFlowType(completedFlow),
            AuthenticatedTenantId =
                authenticatedContextMatchesClient
                    ? account.AuthenticatedTenantId?.ToString("D")
                    : null,
            PrincipalObjectId =
                authenticatedContextMatchesClient
                    && account.AuthenticatedTenantId is not null
                    ? account.PrincipalObjectId?.ToString("D")
                    : null,
        };

        // Upsert activates the profile (FileAuthProfileStore sets the active pointer on upsert).
        await _profileStore.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

        return new AuthLoginResult
        {
            Status = "signed-in",
            Account = account.Username,
            TenantId = account.TenantId,
            Cloud = PowerPlatformApiRingSelection.CanonicalToken(ring),
            EnvironmentCleared = await _environments
                .ClearSelectionsOnIdentityChangeAsync(previous, profile, cancellationToken)
                .ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The ring a sign-in targets. Resolved once and used for both the token request and the stored
    /// profile, so the audience the credential was minted for and the cloud the account is recorded
    /// against can never disagree.
    ///
    /// <para>
    /// An explicit <c>--cloud</c> decides. Otherwise a named account keeps the ring it already has,
    /// and anything else inherits the active account's ring: this process is already composed for
    /// that cloud, so signing in against production here would mint a token no command in this
    /// session could use. An account name matching nothing stored falls back the same way, because an
    /// unrecognised name is a new sign-in in the cloud the session is already working in. A first
    /// sign-in, with nothing active, goes to production.
    /// </para>
    /// </summary>
    /// <param name="request">The command input.</param>
    /// <param name="profiles">The stored profiles, read before the sign-in.</param>
    /// <returns>The ring the sign-in requests a token for and records.</returns>
    private static PowerPlatformApiRing RequestedRing(AuthLoginRequest request, IReadOnlyList<AuthProfile> profiles) =>
        request.Ring
        ?? Named(request.Account, profiles)?.Ring
        ?? profiles.FirstOrDefault(profile => profile.IsActive)?.Ring
        ?? PowerPlatformApiRing.Production;

    private static AuthProfile? Named(string? account, IReadOnlyList<AuthProfile> profiles) =>
        profiles.FirstOrDefault(profile => AuthProfileSelection.NamesAccount(account, profile.AccountKey, profile.Username));

    private async Task<(AuthAccount Account, AuthLoginFlow Flow)> SignInAsync(AuthLoginFlow flow, AuthLoginRequest request, CancellationToken cancellationToken)
    {
        if (flow is AuthLoginFlow.DeviceCode)
        {
            return (await SignInWithDeviceCodeAsync(request, cancellationToken).ConfigureAwait(false), AuthLoginFlow.DeviceCode);
        }

        if (await SignInSilentlyAsync(request, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return (held, AuthLoginFlow.Broker);
        }

        try
        {
            return (await _session.LoginWithBrokerAsync(request, cancellationToken).ConfigureAwait(false), AuthLoginFlow.Broker);
        }
        catch (BrokerUnavailableException unavailable)
        {
            await _messagePresenter.PresentAsync(BrokerFallbackNotice.Create(unavailable.Reason), cancellationToken).ConfigureAwait(false);
        }

        return (await SignInWithDeviceCodeAsync(request, cancellationToken).ConfigureAwait(false), AuthLoginFlow.DeviceCode);
    }

    private async Task<AuthAccount?> SignInSilentlyAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Account))
        {
            return null;
        }

        try
        {
            var signedIn = await _session.LoginWithBrokerSilentlyAsync(request, cancellationToken).ConfigureAwait(false);
            return AuthProfileSelection.NamesAccount(request.Account, signedIn.HomeAccountId, signedIn.Username) ? signedIn : null;
        }
        catch (BrokerUnavailableException)
        {
            return null;
        }
    }

    private Task<AuthAccount> SignInWithDeviceCodeAsync(AuthLoginRequest request, CancellationToken cancellationToken) => _session.LoginWithDeviceCodeAsync(request, message => _messagePresenter.PresentAsync(message, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<AuthStatusReport> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var active = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        // Signed-out short-circuit: never build an MSAL client (so status works even with an
        // unconfigured client id) when there is nothing to reconcile.
        if (active is null)
        {
            return await WithEnvironmentAsync(
                new AuthStatusReport { Status = "signed-out", SignedIn = false },
                cancellationToken).ConfigureAwait(false);
        }

        // Broker/OS-account profiles: the account lives in the OS broker (WAM), not the enumerable MSAL
        // file cache. Mirror PAC's AuthTokenStore, which segregates OperatingSystem
        // profiles and never enumerates accounts for them — trust the persisted profile instead.
        // Reconciling a broker profile against a non-broker enumeration (which structurally cannot see
        // the WAM account) would falsely report `stale` after a successful broker login; silent
        // acquisition still succeeds via the OS-account fallback (MsalSilentTokenProvider).
        if (AuthProfileSelection.IsBrokerProfile(active))
        {
            return await WithEnvironmentAsync(
                new AuthStatusReport
                {
                    Status = "signed-in",
                    SignedIn = true,
                    Account = active.Username,
                    TenantId = active.TenantId,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var accounts = await _session.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        var reconciliation = AuthProfileSelection.Reconcile(active, accounts);

        var report = reconciliation.State switch
        {
            ActiveProfileState.Active => new AuthStatusReport
            {
                Status = "signed-in",
                SignedIn = true,
                Account = active.Username,
                TenantId = active.TenantId,
            },
            // A stale pointer is reported explicitly (account named) rather than silently "signed out".
            ActiveProfileState.Stale => new AuthStatusReport
            {
                Status = "stale",
                SignedIn = false,
                Account = active.Username,
                TenantId = active.TenantId,
            },
            _ => new AuthStatusReport { Status = "signed-out", SignedIn = false },
        };

        return await WithEnvironmentAsync(report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the persisted environment selection to a status report. Selection is durable state that
    /// outlives a sign-in, so it is reported even when the account is signed out or stale — otherwise
    /// an operator could not see the target their next command would use. No token material is read or
    /// reported.
    /// </summary>
    private async Task<AuthStatusReport> WithEnvironmentAsync(
        AuthStatusReport report,
        CancellationToken cancellationToken)
    {
        var active = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        var ring = active?.Ring ?? PowerPlatformApiRing.Production;
        var withRing = report with
        {
            Cloud = PowerPlatformApiRingSelection.CanonicalToken(ring),
        };

        if (_environments.IsRetargeted(ring))
        {
            return withRing;
        }

        var binding = await _environments.GetActiveBindingAsync(cancellationToken).ConfigureAwait(false);

        return binding is null
            ? withRing
            : withRing with
            {
                EnvironmentId = binding.Environment.EnvironmentId,
                EnvironmentDisplayName = binding.Environment.DisplayName,
                EnvironmentTenantId = binding.Environment.TenantId,
                EnvironmentTenantBound = binding.IsBound,
            };
    }

    /// <inheritdoc />
    public Task<EnvironmentListResult> ListEnvironmentsAsync(CancellationToken cancellationToken) =>
        _environments.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<EnvironmentSelectionResult> SelectEnvironmentAsync(
        string environmentId,
        CancellationToken cancellationToken) =>
        _environments.SelectAsync(environmentId, cancellationToken);

    /// <inheritdoc />
    public Task<AgentCreateResult> CreateAgentAsync(
        AgentCreateRequest request,
        CancellationToken cancellationToken) =>
        _agentCreate.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<AuthProfileSummary> SwitchAuthProfileAsync(string accountKey, PowerPlatformApiRing? ring, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountKey) && ring is null)
        {
            throw new UsageException("An account selector (home-account id or username) or `--cloud` is required for `msagent auth switch`.");
        }

        var profiles = await _profileStore.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (profiles.Count == 0)
        {
            throw new NotSignedInException(
                "No AgentHost accounts are stored to switch between.",
                remediation: "Run `msagent auth login` to sign in first.");
        }

        var requestedRing = ring is { } named ? PowerPlatformApiRingSelection.Require(named) : (PowerPlatformApiRing?)null;
        var previous = profiles.FirstOrDefault(profile => profile.IsActive);

        // Pure selection: exact home-account id wins; otherwise case-insensitive username, with
        // ambiguity surfaced as a usage error. No MSAL, no UI.
        if (!string.IsNullOrWhiteSpace(accountKey))
        {
            await _profileStore.SetActiveAsync(
                AuthProfileSelection.ResolveAccountKey(profiles, accountKey),
                cancellationToken).ConfigureAwait(false);
        }

        var active = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new NotSignedInException(
                "No AgentHost account is active.",
                remediation: "Run `msagent auth login` to sign in first.");

        if (requestedRing is { } requested && requested != active.Ring)
        {
            active = active with { Ring = requested };
            await _profileStore.UpsertAsync(active, cancellationToken).ConfigureAwait(false);
        }

        return new AuthProfileSummary
        {
            Status = "switched",
            Account = active.Username,
            TenantId = active.TenantId,
            Cloud = PowerPlatformApiRingSelection.CanonicalToken(active.Ring),
            EnvironmentCleared = await _environments
                .ClearSelectionsOnIdentityChangeAsync(previous, active, cancellationToken)
                .ConfigureAwait(false),
        };
    }

    /// <inheritdoc />
    public async Task<AuthLogoutResult> LogoutAsync(CancellationToken cancellationToken)
    {
        // Clear both halves of the local sign-in state, never opening UI: the profile store and the
        // AgentHost MSAL token cache (best-effort in the adapter).
        await _profileStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        await _session.ClearCacheAsync(cancellationToken).ConfigureAwait(false);

        return new AuthLogoutResult { Status = "signed-out" };
    }

    /// <inheritdoc />
    public Task<AgentPackageUploadResult> UploadAgentPackageAsync(
        AgentPackageUploadRequest request,
        CancellationToken cancellationToken) =>
        _agentPackageUpload.UploadAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AgentDeployResult> DeployAgentAsync(AgentDeployRequest request, CancellationToken cancellationToken) =>
        _agentDeploy.DeployAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AbsProvisioningReport> PreviewAzureBotAsync(AbsProvisioningRequest request, CancellationToken cancellationToken) =>
        _absProvisioning.PreviewAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AbsProvisioningReport> ApplyAzureBotAsync(AbsProvisioningRequest request, CancellationToken cancellationToken) =>
        _absApply.ApplyAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<BlueprintIdentityReport> CreateBlueprintIdentityAsync(
        BlueprintIdentityRequest request,
        CancellationToken cancellationToken) =>
        (_blueprintIdentity ?? throw new PendingImplementationException("identity create blueprint"))
            .ExecuteAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AppRegistrationIdentityReport> CreateAppRegistrationIdentityAsync(
        AppRegistrationIdentityRequest request,
        CancellationToken cancellationToken) =>
        (_appRegistrationIdentity ?? throw new PendingImplementationException("identity create app"))
            .ExecuteAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<CodeAgentDeployment>> CreateDeploymentAsync(
        DeploymentCreateRequest request,
        CancellationToken cancellationToken) =>
        Deployments.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<CodeAgentDeployment>> GetDeploymentAsync(
        DeploymentShowRequest request,
        CancellationToken cancellationToken) =>
        Deployments.GetAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<CodeAgentDeployment>> UpdateDeploymentAsync(
        DeploymentUpdateRequest request,
        CancellationToken cancellationToken) =>
        Deployments.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<CodeAgentDeployment>> DeleteDeploymentAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken) =>
        Deployments.DeleteAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentOAuthConnection>> CreateDeploymentOAuthAsync(
        DeploymentOAuthCreateRequest request,
        CancellationToken cancellationToken) =>
        DeploymentOAuth.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentOAuthConnection>> UpdateDeploymentOAuthAsync(
        DeploymentOAuthUpdateRequest request,
        CancellationToken cancellationToken) =>
        DeploymentOAuth.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentOAuthConnection>> ShowDeploymentOAuthAsync(
        DeploymentOAuthShowRequest request,
        CancellationToken cancellationToken) =>
        DeploymentOAuth.GetAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentOAuthConnection[]>> ListDeploymentOAuthAsync(
        DeploymentOAuthListRequest request,
        CancellationToken cancellationToken) =>
        DeploymentOAuth.ListAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentOAuthConnection>> DeleteDeploymentOAuthAsync(
        DeploymentOAuthDeleteRequest request,
        CancellationToken cancellationToken) =>
        DeploymentOAuth.DeleteAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<OAuthProviderDefinition[]> ListDeploymentOAuthProvidersAsync(
        DeploymentOAuthProviderListRequest request,
        CancellationToken cancellationToken) =>
        DeploymentOAuth.ListProvidersAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationListResult> ListEvaluationsAsync(EvaluationListRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval list").Queries.ListAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationRunResult> ShowEvaluationAsync(EvaluationShowRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval show").Queries.ShowAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval run").Runs.RunAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationRunResult> CancelEvaluationAsync(EvaluationCancelRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval cancel").Runs.CancelAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationRunResult> DeleteEvaluationAsync(EvaluationDeleteRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval delete").Runs.DeleteAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationGenerateResult> GenerateEvaluationConversationsAsync(EvaluationGenerateRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval generate").Runs.GenerateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationTestSetListResult> ListEvaluationTestSetsAsync(EvaluationTestSetListRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval testset list").Queries.ListTestSetsAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationTestSetResult> ShowEvaluationTestSetAsync(EvaluationTestSetShowRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval testset show").Queries.ShowTestSetAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationTestSetResult> CreateEvaluationTestSetAsync(EvaluationTestSetCreateRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval testset create").TestSets.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationTestSetResult> UpdateEvaluationTestSetAsync(EvaluationTestSetUpdateRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval testset update").TestSets.UpdateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationTestSetResult> ExportEvaluationTestSetAsync(EvaluationTestSetExportRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval testset export").Queries.ExportTestSetAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<EvaluationTestSetDeleteResult> DeleteEvaluationTestSetAsync(EvaluationTestSetDeleteRequest request, CancellationToken cancellationToken) =>
        RequireEvaluations("agent eval testset delete").TestSets.DeleteAsync(request, cancellationToken);

    private EvaluationServices RequireEvaluations(string verb) =>
        _evaluations ?? throw new InvalidOperationException($"`msagent {verb}` requires the Copilot Studio evaluation stack, which is not configured on this AgentHostingOperations instance.");

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentChannelRecord>> CreateChannelAsync(
        DeploymentChannelCreateRequest request,
        CancellationToken cancellationToken) =>
        DeploymentChannels.CreateChannelAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentChannelRecord>> UpdateChannelAsync(
        DeploymentChannelUpdateRequest request,
        CancellationToken cancellationToken) =>
        DeploymentChannels.UpdateChannelAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentChannelRecord>> DeleteChannelAsync(
        DeploymentChannelDeleteRequest request,
        CancellationToken cancellationToken) =>
        DeploymentChannels.DeleteChannelAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentChannelRecord>> GetChannelAsync(
        DeploymentChannelShowRequest request,
        CancellationToken cancellationToken) =>
        DeploymentChannels.GetChannelAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeploymentResult<DeploymentChannelRecord[]>> ListChannelsAsync(DeploymentChannelListRequest request, CancellationToken cancellationToken) =>
        DeploymentChannels.ListChannelsAsync(request, cancellationToken);

    /// <summary>
    /// Optional at construction — hosts that never expose the deployment surface may omit it.
    /// So accessing it without wiring it is a clear configuration error rather than a null-reference.
    /// </summary>
    private DeploymentService Deployments =>
        _deployments ?? throw new InvalidOperationException(
            "Deployment operations are not configured on this AgentHostingOperations instance.");

    private DeploymentOAuthService DeploymentOAuth =>
        _deploymentOAuth ?? throw new InvalidOperationException(
            "Deployment OAuth operations are not configured on this AgentHostingOperations instance.");

    private DeploymentChannelService DeploymentChannels =>
        _deploymentChannels ?? throw new InvalidOperationException(
            "Deployment channel operations are not configured on this AgentHostingOperations instance.");

    private static string ToFlowType(AuthLoginFlow flow) => flow switch
    {
        AuthLoginFlow.Broker => "broker",
        AuthLoginFlow.DeviceCode => "device-code",
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, "Unknown login flow."),
    };
}
