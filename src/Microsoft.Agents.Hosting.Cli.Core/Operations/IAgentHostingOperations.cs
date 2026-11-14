using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>
/// The scoped subset of the AMH vision's operation facade that this demonstrator implements. The
/// command host binds every verb to exactly one method here and never reaches around it to
/// an adapter, so the same operation core can be re-hosted by a future service without the CLI shell.
///
/// Authentication and Azure Bot Service operations share this facade, and every method accepts a
/// <see cref="CancellationToken"/>. Environment targeting joins them as the foundation the later
/// frontend commands build on.
/// </summary>
public interface IAgentHostingOperations
{
    /// <summary>Signs the operator in per <paramref name="request"/> and returns the resulting session.</summary>
    Task<AuthLoginResult> LoginAsync(AuthLoginRequest request, CancellationToken cancellationToken);

    /// <summary>Reports the current sign-in state without acquiring a token or opening identity UI.</summary>
    Task<AuthStatusReport> GetAuthStatusAsync(CancellationToken cancellationToken);

    /// <summary>Selects <paramref name="accountKey"/> as the active profile without acquiring a token.</summary>
    Task<AuthProfileSummary> SwitchAuthProfileAsync(string accountKey, PowerPlatformApiRing? ring, CancellationToken cancellationToken);

    /// <summary>Clears the local session and cached accounts without opening identity UI.</summary>
    Task<AuthLogoutResult> LogoutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists the Power Platform environments available to the signed-in operator. Never opens identity
    /// UI: the inventory is read with a silently acquired token.
    /// </summary>
    Task<EnvironmentListResult> ListEnvironmentsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Selects <paramref name="environmentId"/> as the active environment and persists it. Never opens
    /// identity UI.
    /// </summary>
    Task<EnvironmentSelectionResult> SelectEnvironmentAsync(string environmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an agent and its service-owned repository in an explicit or active environment. Never
    /// opens identity UI: inventory and service tokens are acquired silently.
    /// </summary>
    Task<AgentCreateResult> CreateAgentAsync(AgentCreateRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads exact package bytes for an existing Agent and validates the durable Package Revision.
    /// It never opens identity UI and does not create or project a Deployment.
    /// </summary>
    Task<AgentPackageUploadResult> UploadAgentPackageAsync(
        AgentPackageUploadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates desired Deployment state selecting an existing Package Revision through the current
    /// target-routed projection path. It never opens identity UI and does not prove cross-island
    /// execution or a durable public Deploy Operation.
    /// </summary>
    Task<AgentDeployResult> DeployAgentAsync(AgentDeployRequest request, CancellationToken cancellationToken);

    /// <summary>Conditionally replaces the package selected by an existing AMS deployment.</summary>
    Task<AgentDeployResult> UpdateAgentDeploymentPackageAsync(
        AgentDeploymentPackageUpdateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Produces a deterministic, read-only Azure Bot Service plan; never mutates Azure.</summary>
    Task<AbsProvisioningReport> PreviewAzureBotAsync(AbsProvisioningRequest request, CancellationToken cancellationToken);

    /// <summary>Applies the Azure Bot Service plan when confirmed; otherwise behaves as a preview.</summary>
    Task<AbsProvisioningReport> ApplyAzureBotAsync(AbsProvisioningRequest request, CancellationToken cancellationToken);

    /// <summary>Creates or reconciles an Agent Identity Blueprint and its selected configuration.</summary>
    Task<BlueprintIdentityReport> CreateBlueprintIdentityAsync(
        BlueprintIdentityRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates or reconciles an App Registration identity and its selected configuration.</summary>
    Task<AppRegistrationIdentityReport> CreateAppRegistrationIdentityAsync(
        AppRegistrationIdentityRequest request,
        CancellationToken cancellationToken);

    /// <summary>Scaffolds a new MCS project and registers it, or records a clone source for the first deploy.</summary>
    Task<ManagedAgentCreateResult> CreateManagedAgentAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Prepares a new MCS agent's local content from an existing agent and registers it, without writing to the cloud.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The registered copy.</returns>
    Task<ManagedAgentCopyResult> CopyManagedAgentAsync(ManagedAgentCopyRequest request, CancellationToken cancellationToken);

    /// <summary>Registers an MCS project that already exists on disk.</summary>
    Task<ManagedAgentInitResult> InitManagedAgentAsync(ManagedAgentInitRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the MCS agents in one environment or across the tenant.</summary>
    Task<ManagedAgentListResult> ListManagedAgentsAsync(ManagedAgentListRequest request, CancellationToken cancellationToken);

    /// <summary>Shows the managed-agent table recorded in the project's configuration.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching records.</returns>
    Task<ManagedAgentShowResult> ShowManagedAgentAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes one cloud agent and every record for the managed agent.</summary>
    Task<ManagedAgentDeleteResult> DeleteManagedAgentAsync(ManagedAgentDeleteRequest request, CancellationToken cancellationToken);

    /// <summary>Grants a user access to an MCS agent, or sets org-wide use access.</summary>
    Task<ManagedAgentShareResult> ShareManagedAgentAsync(ManagedAgentShareRequest request, CancellationToken cancellationToken);

    /// <summary>Shows the principals an MCS agent is shared with.</summary>
    Task<ManagedAgentShareShowResult> ShowManagedAgentSharesAsync(ManagedAgentShareShowRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a user's access to an MCS agent.</summary>
    Task<ManagedAgentUnshareResult> UnshareManagedAgentAsync(ManagedAgentUnshareRequest request, CancellationToken cancellationToken);

    /// <summary>Brings the remote MCS agent's content into a local directory.</summary>
    Task<ManagedAgentPullResult> PullManagedAgentAsync(ManagedAgentPullRequest request, CancellationToken cancellationToken);

    /// <summary>Deploys the local MCS project to a deployment slot, creating the cloud agent when needed.</summary>
    Task<ManagedAgentDeployResult> DeployManagedAgentAsync(ManagedAgentDeployRequest request, CancellationToken cancellationToken);

    /// <summary>Records a deployment slot for a managed agent.</summary>
    Task<AgentDeploymentCreateResult> CreateAgentDeploymentAsync(AgentDeploymentCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Lists a managed agent's deployment slots.</summary>
    Task<AgentDeploymentListResult> ListAgentDeploymentsAsync(AgentDeploymentListRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one MCS deployment.</summary>
    Task<AgentDeploymentShowResult> ShowAgentDeploymentAsync(AgentDeploymentShowRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a deployment record, and the agent it targets when asked.</summary>
    Task<AgentDeploymentDeleteResult> DeleteAgentDeploymentAsync(AgentDeploymentDeleteRequest request, CancellationToken cancellationToken);

    /// <summary>Renames a deployment record.</summary>
    Task<AgentDeploymentUpdateResult> UpdateAgentDeploymentAsync(AgentDeploymentUpdateRequest request, CancellationToken cancellationToken);

    /// <summary>Computes connection readiness for a deployment and binds what is unambiguous.</summary>
    Task<AgentDeploymentConnectionResult> UpdateAgentDeploymentConnectionAsync(AgentDeploymentConnectionRequest request, CancellationToken cancellationToken);

    /// <summary>Reports a deployment's connection and workflow state without changing anything.</summary>
    Task<AgentDeploymentConnectionResult> ShowAgentDeploymentConnectionAsync(AgentDeploymentConnectionShowRequest request, CancellationToken cancellationToken);

    Task<AgentDeploymentConnectorListResult> ListAgentDeploymentConnectorsAsync(AgentDeploymentConnectorListRequest request, CancellationToken cancellationToken);

    Task<AgentDeploymentConnectionCreateResult> CreateAgentDeploymentConnectionAsync(AgentDeploymentConnectionCreateRequest request, CancellationToken cancellationToken);

    Task<AgentDeploymentConnectionReferenceCreateResult> CreateAgentDeploymentConnectionReferenceAsync(AgentDeploymentConnectionReferenceCreateRequest request, CancellationToken cancellationToken);

    Task<AgentDeploymentConnectionRemoveResult> RemoveAgentDeploymentConnectionAsync(AgentDeploymentConnectionRemoveRequest request, CancellationToken cancellationToken);

    /// <summary>Resolves which agent type a unified deployment verb targets.</summary>
    DeploymentAgentTypeResult ResolveDeploymentAgentType(DeploymentAgentTypeQuery query);

    /// <summary>Reads a deployment and reconciles it against its backing bot.</summary>
    Task<DeploymentResult<CodeAgentDeployment>> GetDeploymentAsync(DeploymentShowRequest request, CancellationToken cancellationToken);

    /// <summary>Creates an Azure Bot Service deployment.</summary>
    Task<DeploymentResult<CodeAgentDeployment>> CreateDeploymentAsync(DeploymentCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a deployment's mutable Azure Bot Service settings.</summary>
    Task<DeploymentResult<CodeAgentDeployment>> UpdateDeploymentAsync(DeploymentUpdateRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a deployment and its backing Bot.</summary>
    Task<DeploymentResult<CodeAgentDeployment>> DeleteDeploymentAsync(DeploymentRequest request, CancellationToken cancellationToken);

    /// <summary>Creates (enables) an OAuth connection setting on a deployment's backing Bot.</summary>
    Task<DeploymentResult<DeploymentOAuthConnection>> CreateDeploymentOAuthAsync(
        DeploymentOAuthCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Updates supplied fields on an OAuth connection setting.</summary>
    Task<DeploymentResult<DeploymentOAuthConnection>> UpdateDeploymentOAuthAsync(
        DeploymentOAuthUpdateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads a single OAuth connection setting on a deployment's backing Bot.</summary>
    Task<DeploymentResult<DeploymentOAuthConnection>> ShowDeploymentOAuthAsync(
        DeploymentOAuthShowRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists the OAuth connection settings on a deployment's backing Bot.</summary>
    Task<DeploymentResult<DeploymentOAuthConnection[]>> ListDeploymentOAuthAsync(
        DeploymentOAuthListRequest request,
        CancellationToken cancellationToken);

    /// <summary>Deletes an OAuth connection setting on a deployment's backing Bot.</summary>
    Task<DeploymentResult<DeploymentOAuthConnection>> DeleteDeploymentOAuthAsync(
        DeploymentOAuthDeleteRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists the live Azure Bot Service OAuth provider catalogue.</summary>
    Task<OAuthProviderDefinition[]> ListDeploymentOAuthProvidersAsync(
        DeploymentOAuthProviderListRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates (enables) a channel on the deployment's backing bot.</summary>
    Task<DeploymentResult<DeploymentChannelRecord>> CreateChannelAsync(
        DeploymentChannelCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Updates an existing channel on the deployment's backing bot (GET-merge-PATCH).</summary>
    Task<DeploymentResult<DeploymentChannelRecord>> UpdateChannelAsync(
        DeploymentChannelUpdateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Deletes a channel from the deployment's backing bot.</summary>
    Task<DeploymentResult<DeploymentChannelRecord>> DeleteChannelAsync(
        DeploymentChannelDeleteRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads a single channel on the deployment's backing bot.</summary>
    Task<DeploymentResult<DeploymentChannelRecord>> GetChannelAsync(
        DeploymentChannelShowRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists the channels configured on the deployment's backing bot.</summary>
    Task<DeploymentResult<DeploymentChannelRecord[]>> ListChannelsAsync(
        DeploymentChannelListRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The <c>msagent agent eval</c> operations, which a host implements only if it offers them.
/// </summary>
public interface IAgentEvaluationOperations
{
    /// <summary>Lists an agent's Copilot Studio evaluation runs.</summary>
    Task<EvaluationListResult> ListEvaluationsAsync(EvaluationListRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one evaluation run and its per-case results.</summary>
    Task<EvaluationRunResult> ShowEvaluationAsync(EvaluationShowRequest request, CancellationToken cancellationToken);

    /// <summary>Starts an evaluation run, creating the test set when one was not named.</summary>
    Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken);

    /// <summary>Stops an in-flight evaluation run.</summary>
    Task<EvaluationRunResult> CancelEvaluationAsync(EvaluationCancelRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes an evaluation run that has already finished.</summary>
    Task<EvaluationRunResult> DeleteEvaluationAsync(EvaluationDeleteRequest request, CancellationToken cancellationToken);

    /// <summary>Generates evaluation conversations without storing them.</summary>
    Task<EvaluationGenerateResult> GenerateEvaluationConversationsAsync(EvaluationGenerateRequest request, CancellationToken cancellationToken);

    /// <summary>Lists an agent's evaluation test sets.</summary>
    Task<EvaluationTestSetListResult> ListEvaluationTestSetsAsync(EvaluationTestSetListRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one evaluation test set and the conversations it holds.</summary>
    Task<EvaluationTestSetResult> ShowEvaluationTestSetAsync(EvaluationTestSetShowRequest request, CancellationToken cancellationToken);

    /// <summary>Creates an evaluation test set.</summary>
    Task<EvaluationTestSetResult> CreateEvaluationTestSetAsync(EvaluationTestSetCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Adds or replaces the conversations in an evaluation test set.</summary>
    Task<EvaluationTestSetResult> UpdateEvaluationTestSetAsync(EvaluationTestSetUpdateRequest request, CancellationToken cancellationToken);

    /// <summary>Writes an evaluation test set's conversations out as CSV.</summary>
    Task<EvaluationTestSetResult> ExportEvaluationTestSetAsync(EvaluationTestSetExportRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes an evaluation test set and the conversations it holds.</summary>
    Task<EvaluationTestSetDeleteResult> DeleteEvaluationTestSetAsync(EvaluationTestSetDeleteRequest request, CancellationToken cancellationToken);
}
