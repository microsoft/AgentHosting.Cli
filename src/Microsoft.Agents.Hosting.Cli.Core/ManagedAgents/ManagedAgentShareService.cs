using System.Collections.ObjectModel;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent share</c>, <c>msagent agent share show</c> and <c>msagent agent unshare</c>.
/// </summary>
public sealed class ManagedAgentShareService
{
    private const string PortalBaseUrl = "https://copilotstudio.microsoft.com";

    private static readonly TimeSpan DefaultTenantNamingTimeout = TimeSpan.FromSeconds(10);

    private readonly ManagedAgentResolver _resolver;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly IAgentSharingPort _sharing;
    private readonly IDirectoryPrincipalPort _directory;
    private readonly ITenantUserDirectoryPort _tenantDirectory;
    private readonly TimeSpan _tenantNamingTimeout;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records from selectors.</param>
    /// <param name="environmentResolver">Resolves the environment the agent lives in.</param>
    /// <param name="catalog">Resolves the cloud agent inside that environment.</param>
    /// <param name="projectIdentities">Reads the project's declared schema name.</param>
    /// <param name="sharing">Reads and writes the agent's sharing state.</param>
    /// <param name="directory">Resolves and names directory principals.</param>
    /// <param name="tenantDirectory">Resolves users the environment has not provisioned.</param>
    /// <param name="tenantNamingTimeout">How long <c>share show</c> waits on the tenant directory for names, showing object ids for any it has not returned by then. Defaults to ten seconds.</param>
    public ManagedAgentShareService(ManagedAgentResolver resolver, CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, CopilotProjectIdentityReader projectIdentities, IAgentSharingPort sharing, IDirectoryPrincipalPort directory, ITenantUserDirectoryPort tenantDirectory, TimeSpan? tenantNamingTimeout = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _sharing = sharing ?? throw new ArgumentNullException(nameof(sharing));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _tenantDirectory = tenantDirectory ?? throw new ArgumentNullException(nameof(tenantDirectory));
        _tenantNamingTimeout = tenantNamingTimeout ?? DefaultTenantNamingTimeout;
    }

    /// <summary>Grants a principal access to the agent.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the share produced.</returns>
    public async Task<ManagedAgentShareResult> ShareAsync(ManagedAgentShareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (AgentShareValidation.ShareGrant(request.User, request.Role, request.ShareWithOrg) is { } invalid)
        {
            throw invalid;
        }

        var namesPrincipal = !string.IsNullOrWhiteSpace(request.User);
        var role = namesPrincipal && AgentShareRoles.TryParse(request.Role, out var named) ? named : AgentSharingRole.Consumer;
        var target = await ResolveTargetAsync(request, "msagent agent share", cancellationToken).ConfigureAwait(false);
        var principal = namesPrincipal ? await ResolvePrincipalAsync(target.Environment, request.User, cancellationToken).ConfigureAwait(false) : null;
        var current = await _sharing.ReadAsync(target.Environment, target.McsAgentId, cancellationToken).ConfigureAwait(false);

        RequireUnambiguous(current, target.DisplayName);

        var held = principal is null ? null : current.TargetFor(principal.ObjectId);

        if (held?.IsOwner == true)
        {
            throw ManagedAgentException.ShareOwnerNotChangeable(principal!.DisplayName);
        }

        var shareWithOrg = request.ShareWithOrg ?? current.ShareWithOrg;
        var grants = principal is not null && held?.HoldsExactly(role) != true;
        var changed = shareWithOrg != current.ShareWithOrg || grants;
        var desired = (grants ? current.With(principal!.ObjectId, role) : current) with { ShareWithOrg = shareWithOrg };
        var applied = changed ? await _sharing.WriteAsync(target.Environment, target.McsAgentId, desired, cancellationToken).ConfigureAwait(false) : current;

        RequireApplied(applied, desired, target.DisplayName, ManagedAgentException.ShareNotApplied);

        return new ManagedAgentShareResult
        {
            Status = changed ? "shared" : "unchanged",
            AgentId = target.AgentId,
            DisplayName = target.DisplayName,
            McsAgentId = target.McsAgentId,
            EnvironmentId = target.Environment.EnvironmentId,
            Principal = principal is null ? null : ToEntry(principal, applied.TargetFor(principal.ObjectId)),
            Role = principal is null ? null : AgentShareRoles.NameOf(role),
            ShareWithOrg = applied.ShareWithOrg,
            Changed = changed,
            PortalUrl = PortalUrlOf(target),
        };
    }

    /// <summary>
    /// Rejects a state that cannot be changed without guessing.
    /// </summary>
    private static void RequireUnambiguous(AgentSharingState state, string displayName)
    {
        if (state.DuplicateObjectIds is { Count: > 0 } duplicates)
        {
            throw ManagedAgentException.ShareStateAmbiguous(displayName, duplicates);
        }
    }

    /// <summary>
    /// Rejects a write the service did not carry out as asked.
    /// </summary>
    private static void RequireApplied(AgentSharingState applied, AgentSharingState desired, string displayName, Func<string, IReadOnlyList<string>, ManagedAgentException> failure)
    {
        if (applied.DifferencesFrom(desired) is { Count: > 0 } differences)
        {
            throw failure(displayName, differences);
        }
    }

    /// <summary>Shows the principals the agent is shared with.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current shares.</returns>
    public async Task<ManagedAgentShareShowResult> ShowAsync(ManagedAgentShareShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveTargetAsync(request, "msagent agent share show", cancellationToken).ConfigureAwait(false);
        var state = await _sharing.ReadAsync(target.Environment, target.McsAgentId, cancellationToken).ConfigureAwait(false);
        var names = await NameAsync(target.Environment, [.. state.Targets.Select(share => share.ObjectId)], cancellationToken).ConfigureAwait(false);

        return new ManagedAgentShareShowResult
        {
            AgentId = target.AgentId,
            DisplayName = target.DisplayName,
            McsAgentId = target.McsAgentId,
            EnvironmentId = target.Environment.EnvironmentId,
            ShareWithOrg = state.ShareWithOrg,
            Shares =
            [
                .. state.Targets
                    .Select(share => (Target: share, Entry: ToEntry(names.GetValueOrDefault(share.ObjectId), share.ObjectId, share)))
                    .OrderBy(share => share.Target.IsOwner ? 0 : 1)
                    .ThenBy(share => share.Entry.PrincipalName, StringComparer.OrdinalIgnoreCase)
                    .Select(share => share.Entry),
            ],
            PortalUrl = PortalUrlOf(target),
        };
    }

    /// <summary>
    /// Names a set of principals, best effort.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, DirectoryPrincipal>> NameAsync(CopilotEnvironmentContext environment, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken)
    {
        if (objectIds.Count == 0)
        {
            return ReadOnlyDictionary<string, DirectoryPrincipal>.Empty;
        }

        var names = new Dictionary<string, DirectoryPrincipal>(await BestEffortAsync(() => _directory.ResolveManyAsync(environment, objectIds, cancellationToken), ReadOnlyDictionary<string, DirectoryPrincipal>.Empty, IsRecoverable).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        var unnamed = objectIds.Where(objectId => !names.ContainsKey(objectId)).ToArray();

        if (unnamed.Length > 0)
        {
            await NameThroughTenantAsync(environment.TenantId, unnamed, names, cancellationToken).ConfigureAwait(false);
        }

        return names;
    }

    private async Task NameThroughTenantAsync(string tenantId, IReadOnlyCollection<string> objectIds, Dictionary<string, DirectoryPrincipal> names, CancellationToken cancellationToken)
    {
        using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeLimit.CancelAfter(_tenantNamingTimeout);

        try
        {
            await foreach (var principal in _tenantDirectory.ResolveManyAsync(tenantId, objectIds, timeLimit.Token).ConfigureAwait(false))
            {
                names.TryAdd(principal.ObjectId, principal);
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && (failure is AgentHostingException or OperationCanceledException || IsRecoverable(failure)))
        {
        }
    }

    /// <summary>Removes a principal's access to the agent.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the revoke produced.</returns>
    public async Task<ManagedAgentUnshareResult> UnshareAsync(ManagedAgentUnshareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (AgentShareValidation.UnshareTarget(request.User) is { } invalid)
        {
            throw invalid;
        }

        var target = await ResolveTargetAsync(request, "msagent agent unshare", cancellationToken).ConfigureAwait(false);
        var principal = await ResolvePrincipalAsync(target.Environment, request.User, cancellationToken).ConfigureAwait(false);
        var current = await _sharing.ReadAsync(target.Environment, target.McsAgentId, cancellationToken).ConfigureAwait(false);

        RequireUnambiguous(current, target.DisplayName);

        var held = current.TargetFor(principal.ObjectId);

        if (held?.IsOwner == true)
        {
            throw ManagedAgentException.ShareOwnerNotChangeable(principal.DisplayName);
        }

        var desired = current.Without(principal.ObjectId);
        var applied = held is null
            ? current
            : await _sharing.WriteAsync(target.Environment, target.McsAgentId, desired, cancellationToken).ConfigureAwait(false);

        RequireApplied(applied, desired, target.DisplayName, (displayName, differences) => ManagedAgentException.ShareNotRemoved(principal.DisplayName, displayName, differences));

        return new ManagedAgentUnshareResult
        {
            Status = held is null ? "unchanged" : "unshared",
            AgentId = target.AgentId,
            DisplayName = target.DisplayName,
            McsAgentId = target.McsAgentId,
            EnvironmentId = target.Environment.EnvironmentId,
            Principal = ToEntry(principal, held),
            ShareWithOrg = applied.ShareWithOrg,
            Changed = held is not null,
        };
    }

    private sealed record ShareTarget(string AgentId, string DisplayName, CopilotEnvironmentContext Environment, string McsAgentId);

    /// <summary>
    /// Resolves the agent a command addresses, and the environment its access lives in.
    /// </summary>
    private async Task<ShareTarget> ResolveTargetAsync(ManagedAgentShareSelector request, string verb, CancellationToken cancellationToken)
    {
        AgentShareValidation.RequireAgentSelector(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var agent = ManagedAgentResolver.RequireMcsAgent(_resolver.ResolveAgent(projectDirectory, request.AgentId, request.AgentName), verb);
        var deployments = _resolver.ListDeployments(projectDirectory, agent.AgentId);

        if (!HasEverDeployed(agent, deployments) || SelectorFor(projectDirectory, agent) is not { } selector)
        {
            throw ManagedAgentException.NotDeployedYet(agent.DisplayName, agent.AgentId);
        }

        var environment = await _environmentResolver
            .ResolveAsync(MostRecentlyDeployedEnvironment(deployments) ?? agent.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        var mcsAgentId = await _catalog.ResolveAgentIdAsync(environment, selector, cancellationToken).ConfigureAwait(false)
            ?? throw ManagedAgentException.ShareTargetNotInEnvironment(agent.DisplayName, agent.AgentId, environment.EnvironmentId);

        return new ShareTarget(agent.AgentId, agent.DisplayName, environment, mcsAgentId);
    }

    /// <summary>
    /// Whether this agent has ever reached an environment.
    /// </summary>
    private static bool HasEverDeployed(ManagedAgentRecord agent, IReadOnlyList<AgentDeploymentRecord> deployments) =>
        !string.IsNullOrWhiteSpace(agent.McsAgentId) || deployments.Any(deployment => deployment.LastDeployedTime is not null);

    /// <summary>
    /// What this record can be looked up by inside an environment.
    /// </summary>
    private string? SelectorFor(string projectDirectory, ManagedAgentRecord agent)
    {
        var workspaceDirectory = _resolver.FindAgentWorkspaceDirectory(projectDirectory, agent);
        var schemaName = ManagedAgentResolver.McsSchemaNameOf(agent, workspaceDirectory is null ? null : _projectIdentities.Find(workspaceDirectory)?.SchemaName);

        return schemaName ?? (string.IsNullOrWhiteSpace(agent.McsAgentId) ? null : agent.McsAgentId.Trim());
    }

    /// <summary>
    /// The environment of the slot this agent most recently deployed to, or <see langword="null"/> when
    /// it has never deployed and the home environment stands.
    /// </summary>
    private static string? MostRecentlyDeployedEnvironment(IReadOnlyList<AgentDeploymentRecord> deployments) =>
        deployments
            .Where(deployment => deployment.LastDeployedTime is not null && !string.IsNullOrWhiteSpace(deployment.EnvironmentId))
            .MaxBy(deployment => deployment.LastDeployedTime)
            ?.EnvironmentId;

    private static ManagedAgentShareEntry ToEntry(DirectoryPrincipal principal, AgentShareTarget? share) => ToEntry(principal, principal.ObjectId, share);

    private static ManagedAgentShareEntry ToEntry(DirectoryPrincipal? principal, string objectId, AgentShareTarget? share) => new()
    {
        ObjectId = objectId,
        PrincipalName = principal?.DisplayName ?? objectId,
        PrincipalType = principal?.PrincipalType ?? "Unknown",
        SignInAddress = principal?.SignInAddress,
        Role = AgentShareRoles.NameOf(share),
    };

    private static string PortalUrlOf(ShareTarget target) => $"{PortalBaseUrl}/environments/{target.Environment.EnvironmentId}/agents/{target.McsAgentId}";

    /// <summary>
    /// Resolves the user a grant or revoke names.
    /// </summary>
    private async Task<DirectoryPrincipal> ResolvePrincipalAsync(CopilotEnvironmentContext environment, string? user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            throw ManagedAgentException.SharePrincipalSelectorRequired();
        }

        var selector = user.Trim();

        if (Guid.TryParse(selector, out var objectId))
        {
            return await BestEffortAsync(() => _directory.ResolveUserAsync(environment, selector, cancellationToken), null, IsRecoverable).ConfigureAwait(false) ?? new DirectoryPrincipal { ObjectId = objectId.ToString(), DisplayName = objectId.ToString(), PrincipalType = "Unknown" };
        }

        return await _directory.ResolveUserAsync(environment, selector, cancellationToken).ConfigureAwait(false) ?? await _tenantDirectory.FindUserAsync(environment.TenantId, selector, cancellationToken).ConfigureAwait(false) ?? throw ManagedAgentException.SharePrincipalNotFound(selector, environment.TenantId);
    }

    private static async Task<TResult> BestEffortAsync<TResult>(Func<Task<TResult>> lookup, TResult fallback, Func<Exception, bool> isRecoverable)
    {
        try
        {
            return await lookup().ConfigureAwait(false);
        }
        catch (Exception failure) when (isRecoverable(failure))
        {
            return fallback;
        }
    }

    /// <summary>
    /// Whether a directory failure may be absorbed.
    /// </summary>
    private static bool IsRecoverable(Exception failure) =>
        failure is HttpRequestException or IOException or TimeoutException
            or CopilotStudioException { ErrorKind: not CopilotErrorKinds.UntrustedDataverseOrigin };
}
