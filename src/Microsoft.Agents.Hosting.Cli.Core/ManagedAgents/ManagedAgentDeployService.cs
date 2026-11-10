using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent deploy</c>: the only verb that creates or updates a cloud agent.
/// </summary>
public sealed class ManagedAgentDeployService
{
    private readonly ManagedAgentResolver _resolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly CopilotWorkspaceLocator _workspaceLocator;
    private readonly CopilotWorkspaceService _workspaces;
    private readonly ICopilotComponentCollectionPort _collections;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly ICopilotWorkspaceAttachPort _attach;
    private readonly ICopilotCatalogPort _catalog;
    private readonly CopilotCatalogService _catalogService;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly WorkflowActivationReporter _workflowActivation;
    private readonly IClock _clock;
    private readonly IProgressStatus _progress;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records from selectors.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="environmentResolver">Resolves the target environment.</param>
    /// <param name="workspaceLocator">Reads the project's cloud baseline when it has one.</param>
    /// <param name="workspaces">Pushes workspace content, with the conflict gate.</param>
    /// <param name="collections">Pushes the component collections the agent declares before the agent.</param>
    /// <param name="projectIdentities">Reads the project's declared identity.</param>
    /// <param name="attach">Creates or adopts the cloud agent and binds the workspace to it.</param>
    /// <param name="catalog">Verifies the agent a bound workspace points at still exists.</param>
    /// <param name="catalogService">Publishes the pushed content.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as the deployer.</param>
    /// <param name="workflowActivation">Activates the workflows the push left as drafts.</param>
    /// <param name="clock">Timestamps the deploy.</param>
    /// <param name="progress">Shows the deploy's current step while it runs.</param>
    public ManagedAgentDeployService(ManagedAgentResolver resolver, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, CopilotEnvironmentResolver environmentResolver, CopilotWorkspaceLocator workspaceLocator, CopilotWorkspaceService workspaces, ICopilotComponentCollectionPort collections, CopilotProjectIdentityReader projectIdentities, ICopilotWorkspaceAttachPort attach, ICopilotCatalogPort catalog, CopilotCatalogService catalogService, ManagedAgentPrincipalResolver principals, WorkflowActivationReporter workflowActivation, IClock clock, IProgressStatus? progress = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _workflowActivation = workflowActivation ?? throw new ArgumentNullException(nameof(workflowActivation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _progress = progress ?? NoProgressStatus.Instance;
    }

    /// <summary>
    /// Deploys the local project to a deployment slot.
    ///
    /// A push that reports warnings still records the deploy: promoting an agent uploads its flows
    /// before their connection references are bound in the target, and the service refuses to activate
    /// a flow whose connection is not yet valid, so failing here would make promotion impossible.
    /// <c>--publish</c> is withheld only for content that did not land, never for a notice such as
    /// adopting an agent that already existed, which every repeat promotion reports.
    /// </summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the deploy did.</returns>
    public async Task<ManagedAgentDeployResult> DeployAsync(ManagedAgentDeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var (owner, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName, requireExplicitDeployment: true);
        var agent = ManagedAgentResolver.RequireMcsAgent(owner, "msagent agent deploy");
        var target = await _environmentResolver.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);

        var notices = new List<string>();
        var blockers = new List<string>();
        var componentWarnings = new List<string>();
        var workspaceDirectory = _resolver.ResolveAgentWorkspaceDirectory(projectDirectory, agent);
        var identity = _projectIdentities.Require(workspaceDirectory, agent.AgentId);
        var schemaName = string.IsNullOrWhiteSpace(request.SchemaName) ? identity.SchemaName : request.SchemaName.Trim();
        var workspace = await _workspaceLocator.FindAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var action = ChooseAction(workspace, target.EnvironmentId);

        if (action == "push")
        {
            if (await FindTargetAgentAsync(target, workspace!.AgentId, cancellationToken).ConfigureAwait(false) is { } bound)
            {
                RequireBoundAgentMatches(bound, schemaName, target.EnvironmentId, workspaceDirectory);
                RequireBoundAgentOwned(bound, agent, deployment, schemaName, target.EnvironmentId, request.Overwrite);
            }
            else
            {
                notices.Add($"This directory was connected to agent {workspace.AgentId} in {target.EnvironmentId}, which no longer exists. It was reconnected.");
                action = "reattach";
            }
        }

        string targetAgentId;
        var targetAgentCreated = false;

        if (action == "push")
        {
            WorkspaceConnectionVerifier.RequireOriginMatches(workspace!, target);
            targetAgentId = workspace!.AgentId;
        }
        else
        {
            var adoptable = await RequireAdoptableTargetAsync(target, agent, deployment, schemaName, request.Overwrite, cancellationToken).ConfigureAwait(false);

            var attached = await _attach.AttachAsync(
                new CopilotAttachCommand
                {
                    Environment = target,
                    ProjectDirectory = workspaceDirectory,
                    DisplayName = identity.DisplayName,
                    SchemaName = schemaName,
                    AllowRetarget = workspace is not null,
                    ExpectedAgentId = adoptable,
                },
                cancellationToken).ConfigureAwait(false);

            targetAgentId = attached.AgentId;
            targetAgentCreated = attached.AgentCreated;
            schemaName = attached.SchemaName;
            componentWarnings.AddRange(attached.Warnings);
            notices.AddRange(attached.Notices);

            if (adoptable is not null)
            {
                notices.Add($"An agent already carried the schema name {schemaName} in {target.EnvironmentId}; it was adopted rather than duplicated.");
            }
        }

        _progress.Report("Pushing changes");
        var pushed = await PushAsync(projectDirectory, workspaceDirectory, deployment.DeploymentType, request.Overwrite || targetAgentCreated, target.EnvironmentId, schemaName, DescribeTarget(action, targetAgentCreated), cancellationToken).ConfigureAwait(false);
        var deployedOn = _clock.UtcNow;
        var deployedBy = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false);

        var activation = await _workflowActivation.RunAsync(target, workspaceDirectory, cancellationToken).ConfigureAwait(false);

        componentWarnings.AddRange(pushed.Warnings);
        blockers.AddRange(ExceptSupersededWorkflows(componentWarnings, activation.Reported.Count > 0));
        blockers.AddRange(pushed.CollectionWarnings);
        blockers.AddRange(activation.Warnings);
        try
        {
            _deployments.Update(projectDirectory, deployment with { LastDeployedTime = deployedOn, LastDeployedBy = deployedBy });
            _agents.Update(projectDirectory, agent with { LastDeployedBy = deployedBy, McsAgentId = targetAgentId, McsSchemaName = schemaName });
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.DeployRecordedNowhere(schemaName, target.EnvironmentId, targetAgentId, deployment.DeploymentName, failure);
        }

        var result = new ManagedAgentDeployResult
        {
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            Action = action,
            TargetEnvironmentId = target.EnvironmentId,
            TargetMcsAgentId = targetAgentId,
            SchemaName = schemaName,
            TargetAgentCreated = targetAgentCreated,
            PushedComponentCount = pushed.ComponentCount,
            UploadedFileCount = pushed.UploadedFileCount,
            AuxiliaryChangeCount = pushed.AuxiliaryCount,
            DeployedOn = deployedOn,
            LastPublishedTime = pushed.PublishedOn,
            Warnings = [.. blockers.Concat(notices).Distinct(StringComparer.Ordinal)],
        };

        if (!request.Publish)
        {
            return result;
        }

        return blockers.Count == 0
            ? await PublishAsync(result, target, targetAgentId, cancellationToken).ConfigureAwait(false)
            : throw ManagedAgentException.PublishWithheldAfterIncompletePush(schemaName, target.EnvironmentId, deployment.DeploymentName, [.. blockers.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Drops the per-workflow state claims the activation pass has since replaced with its counts.
    /// </summary>
    /// <param name="warnings">The warnings attaching and pushing the agent's workspace produced.</param>
    /// <param name="passReported">Whether the activation pass observed any workflow.</param>
    /// <returns>The warnings the pass does not supersede.</returns>
    private static IEnumerable<string> ExceptSupersededWorkflows(IReadOnlyList<string> warnings, bool passReported) =>
        passReported ? warnings.Where(warning => !DeployWarningText.IsRestatedByWorkflowCounts(warning)) : warnings;

    private async Task<(int ComponentCount, int UploadedFileCount, int AuxiliaryCount, IReadOnlyList<string> Warnings, IReadOnlyList<string> CollectionWarnings, DateTimeOffset? PublishedOn)> PushAsync(string projectDirectory, string workspaceDirectory, string stage, bool overwrite, string environmentId, string schemaName, string targetDescription, CancellationToken cancellationToken)
    {
        var pushed = await _collections.PushAsync(projectDirectory, workspaceDirectory, (directory, token) => PushWorkspaceAsync(directory, stage, overwrite, environmentId, schemaName, targetDescription, token), (collection, token) => IsConnectedToAsync(collection, environmentId, token), cancellationToken).ConfigureAwait(false);
        var collections = pushed.Collections.Where(collection => collection.Pushed).Select(collection => collection.Result).ToArray();

        return (
            collections.Sum(collection => collection.ComponentCount) + pushed.Agent.ComponentCount,
            collections.Sum(collection => collection.UploadedFileCount) + pushed.Agent.UploadedFileCount,
            collections.Sum(collection => collection.AuxiliaryCount) + pushed.Agent.AuxiliaryCount,
            pushed.Agent.Warnings,
            [.. pushed.Missing.Select(declared => $"The component collection declared as '{declared}' was not pushed: it holds no project inside this one."), .. pushed.Collections.SelectMany(collection => collection.Pushed ? collection.Result.Warnings : [$"The component collection in {collection.Directory} was not pushed: it is not connected to {environmentId}."])],
            pushed.Agent.PublishedOn);
    }

    private async Task<bool> IsConnectedToAsync(string collection, string environmentId, CancellationToken cancellationToken) => await _workspaceLocator.FindAsync(collection, cancellationToken).ConfigureAwait(false) is { } collectionWorkspace && string.Equals(collectionWorkspace.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase);

    private async Task<(int ComponentCount, int UploadedFileCount, int AuxiliaryCount, IReadOnlyList<string> Warnings, DateTimeOffset? PublishedOn)> PushWorkspaceAsync(string workspaceDirectory, string stage, bool overwrite, string environmentId, string schemaName, string targetDescription, CancellationToken cancellationToken)
    {
        try
        {
            var pushed = await _workspaces.PushAsync(
                new CopilotPushRequest { ProjectDirectory = workspaceDirectory, Overwrite = overwrite || stage != DeploymentStages.Dev },
                cancellationToken).ConfigureAwait(false);

            return (pushed.ChangeCount, pushed.UploadedFileCount, pushed.AuxiliaryChangeCount, pushed.Warnings, pushed.PublishedOn);
        }
        catch (CopilotStudioException failure) when (
            failure.ErrorKind is CopilotErrorKinds.PushConflictsRequirePull or CopilotErrorKinds.WorkspaceFilesUnreadable or CopilotErrorKinds.WorkflowToolIdEmpty or CopilotErrorKinds.MissingConnectionReference)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.DeployIncomplete("push", schemaName, environmentId, targetDescription, failure);
        }
    }

    private static string ChooseAction(CopilotWorkspaceInfo? workspace, string targetEnvironmentId) => workspace is null
        ? "reattach"
        : string.Equals(workspace.EnvironmentId, targetEnvironmentId, StringComparison.OrdinalIgnoreCase) ? "push" : "retarget";

    /// <summary>
    /// Finds the agent the workspace is bound to, or nothing when it no longer exists.
    ///
    /// Only a typed "not found" counts as absence. Treating any failure as absence would let a 401, a
    /// 403, a timeout or a server error be read as a deleted agent, and the reattach that follows sets
    /// <c>overwrite</c> — so a transient fault would silently overwrite cloud content the operator never
    /// asked to overwrite. Every other failure is the caller's to see.
    /// </summary>
    private async Task<CopilotAgentSummary?> FindTargetAgentAsync(CopilotEnvironmentContext target, string agentId, CancellationToken cancellationToken)
    {
        try
        {
            return await _catalog.GetAgentAsync(target, agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (CopilotStudioException failure) when (failure.ErrorKind == CopilotErrorKinds.AgentNotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Rejects a push whose workspace is bound to an agent that is not the one the workspace describes,
    /// or to one that cannot be written to at all.
    /// </summary>
    private static void RequireBoundAgentMatches(CopilotAgentSummary bound, string schemaName, string environmentId, string workspaceDirectory)
    {
        if (!string.Equals(bound.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase))
        {
            throw ManagedAgentException.WorkspaceIdentityMismatch(schemaName, bound.SchemaName, bound.AgentId, workspaceDirectory);
        }

        if (bound.IsManaged)
        {
            throw ManagedAgentException.TargetAgentIsReadOnly(schemaName, environmentId);
        }
    }

    private static void RequireBoundAgentOwned(CopilotAgentSummary bound, ManagedAgentRecord agent, AgentDeploymentRecord deployment, string schemaName, string environmentId, bool acknowledged)
    {
        if (UnownedTargetGuard.Applies(agent, deployment, environmentId, acknowledged) && !ProjectOwns(agent, deployment, schemaName, bound.AgentId))
        {
            throw ManagedAgentException.UnverifiedBoundAgent(bound.DisplayName, bound.AgentId, environmentId);
        }
    }


    /// <summary>
    /// Agent to adopt, and returns the identity the adoption is pinned to.
    /// </summary>
    /// <param name="target">The environment being deployed to.</param>
    /// <param name="agent">The record being deployed.</param>
    /// <param name="deployment">The slot being deployed.</param>
    /// <param name="schemaName">The schema name the deploy will use.</param>
    /// <param name="acknowledged">Whether the operator passed <c>--overwrite</c>.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The agent to adopt, or <see langword="null"/> when the schema name is free.</returns>
    private async Task<string?> RequireAdoptableTargetAsync(CopilotEnvironmentContext target, ManagedAgentRecord agent, AgentDeploymentRecord deployment, string schemaName, bool acknowledged, CancellationToken cancellationToken)
    {
        if (await _catalog.ResolveAgentIdAsync(target, schemaName, cancellationToken).ConfigureAwait(false) is not { } candidateId
            || await FindTargetAgentAsync(target, candidateId, cancellationToken).ConfigureAwait(false) is not { } existing)
        {
            return null;
        }

        if (existing.IsManaged)
        {
            throw ManagedAgentException.TargetAgentIsReadOnly(schemaName, target.EnvironmentId);
        }

        return acknowledged || ProjectOwns(agent, deployment, schemaName, existing.AgentId)
            ? existing.AgentId
            : throw ManagedAgentException.UnverifiedAgentInTarget(schemaName, target.EnvironmentId, existing.AgentId);
    }

    /// <summary>
    /// Whether the project can show the agent carrying the schema name is one it already deploys.
    /// </summary>
    /// <param name="agent">The record being deployed.</param>
    /// <param name="deployment">The slot being deployed.</param>
    /// <param name="schemaName">The schema name the deploy will use.</param>
    /// <param name="targetAgentId">The agent that would be adopted.</param>
    /// <returns>Whether adopting the agent needs no acknowledgement.</returns>
    private static bool ProjectOwns(ManagedAgentRecord agent, AgentDeploymentRecord deployment, string schemaName, string targetAgentId) =>
        string.Equals(agent.McsAgentId, targetAgentId, StringComparison.OrdinalIgnoreCase)
        || (deployment.LastDeployedTime is not null && string.Equals(agent.McsSchemaName, schemaName, StringComparison.OrdinalIgnoreCase));

    private static string DescribeTarget(string action, bool agentCreated) => action switch
    {
        "push" => "was already connected to this workspace",
        _ => agentCreated ? "was created" : "was adopted",
    };

    /// <summary>
    /// Publishes the pushed content. A publish failure never undoes the push, so it is reported as a
    /// failure that names the draft rather than as a deploy that did not happen.
    /// </summary>
    private async Task<ManagedAgentDeployResult> PublishAsync(ManagedAgentDeployResult result, CopilotEnvironmentContext target, string targetAgentId, CancellationToken cancellationToken)
    {
        _progress.Report("Publishing");

        try
        {
            var published = await _catalogService.PublishAsync(
                new CopilotPublishRequest { Bot = targetAgentId, Environment = target.EnvironmentId, Force = true },
                cancellationToken).ConfigureAwait(false);

            return result with { Published = true, PublishedOn = published.PublishedOn, LastPublishedTime = published.PublishedOn };
        }
        catch (CopilotStudioException failure) when (failure.ErrorKind is CopilotErrorKinds.PublishFailed or CopilotErrorKinds.PublishTimeout)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.PublishFailedAfterPush(result.SchemaName ?? result.DisplayName, target.EnvironmentId, result.DeploymentName, failure.Message);
        }
    }
}
