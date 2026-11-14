using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// Resolves every command's handler from the operations facade, so a host constructed with only
/// <see cref="IAgentHostingOperations"/> can drive the whole command surface.
/// </summary>
public sealed class FacadeHandlerProvider : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _handlers;

    /// <summary>Creates the provider.</summary>
    /// <param name="operations">The facade the handlers delegate to.</param>
    public FacadeHandlerProvider(IAgentHostingOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var handlers = new Dictionary<Type, object>
        {
            [typeof(ICommandHandler<EnvironmentListRequest, EnvironmentListResult>)] = new FacadeHandler<EnvironmentListRequest, EnvironmentListResult>((_, token) => operations.ListEnvironmentsAsync(token)),
            [typeof(ICommandHandler<EnvironmentSelectRequest, EnvironmentSelectionResult>)] = new FacadeHandler<EnvironmentSelectRequest, EnvironmentSelectionResult>((request, token) => operations.SelectEnvironmentAsync(request.EnvironmentId, token)),
            [typeof(ICommandHandler<ManagedAgentInitRequest, ManagedAgentInitResult>)] = new FacadeHandler<ManagedAgentInitRequest, ManagedAgentInitResult>(operations.InitManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentListRequest, ManagedAgentListResult>)] = new FacadeHandler<ManagedAgentListRequest, ManagedAgentListResult>(operations.ListManagedAgentsAsync),
            [typeof(ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>)] = new FacadeHandler<ManagedAgentShowRequest, ManagedAgentShowResult>(operations.ShowManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentPullRequest, ManagedAgentPullResult>)] = new FacadeHandler<ManagedAgentPullRequest, ManagedAgentPullResult>(operations.PullManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentDeleteRequest, ManagedAgentDeleteResult>)] = new FacadeHandler<ManagedAgentDeleteRequest, ManagedAgentDeleteResult>(operations.DeleteManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentShareRequest, ManagedAgentShareResult>)] = new FacadeHandler<ManagedAgentShareRequest, ManagedAgentShareResult>(operations.ShareManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentShareShowRequest, ManagedAgentShareShowResult>)] = new FacadeHandler<ManagedAgentShareShowRequest, ManagedAgentShareShowResult>(operations.ShowManagedAgentSharesAsync),
            [typeof(ICommandHandler<ManagedAgentUnshareRequest, ManagedAgentUnshareResult>)] = new FacadeHandler<ManagedAgentUnshareRequest, ManagedAgentUnshareResult>(operations.UnshareManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentCreateRequest, ManagedAgentCreateResult>)] = new FacadeHandler<ManagedAgentCreateRequest, ManagedAgentCreateResult>(operations.CreateManagedAgentAsync),
            [typeof(ICommandHandler<ManagedAgentCopyRequest, ManagedAgentCopyResult>)] = new FacadeHandler<ManagedAgentCopyRequest, ManagedAgentCopyResult>(operations.CopyManagedAgentAsync),
            [typeof(ICommandHandler<AgentCreateRequest, AgentCreateResult>)] = new FacadeHandler<AgentCreateRequest, AgentCreateResult>(operations.CreateAgentAsync),
            [typeof(ICommandHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>)] = new FacadeHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>(operations.DeployManagedAgentAsync),
            [typeof(ICommandHandler<AgentDeployRequest, AgentDeployResult>)] = new FacadeHandler<AgentDeployRequest, AgentDeployResult>(operations.DeployAgentAsync),
            [typeof(ICommandHandler<AgentDeploymentPackageUpdateRequest, AgentDeployResult>)] = new FacadeHandler<AgentDeploymentPackageUpdateRequest, AgentDeployResult>(operations.UpdateAgentDeploymentPackageAsync),
            [typeof(ICommandHandler<AgentPackageUploadRequest, AgentPackageUploadResult>)] = new FacadeHandler<AgentPackageUploadRequest, AgentPackageUploadResult>(operations.UploadAgentPackageAsync),
            [typeof(ICommandHandler<AgentDeploymentListRequest, AgentDeploymentListResult>)] = new FacadeHandler<AgentDeploymentListRequest, AgentDeploymentListResult>(operations.ListAgentDeploymentsAsync),
            [typeof(ICommandHandler<AgentDeploymentCreateRequest, AgentDeploymentCreateResult>)] = new FacadeHandler<AgentDeploymentCreateRequest, AgentDeploymentCreateResult>(operations.CreateAgentDeploymentAsync),
            [typeof(ICommandHandler<DeploymentCreateRequest, DeploymentResult<CodeAgentDeployment>>)] = new FacadeHandler<DeploymentCreateRequest, DeploymentResult<CodeAgentDeployment>>(operations.CreateDeploymentAsync),
            [typeof(ICommandHandler<AgentDeploymentShowRequest, AgentDeploymentShowResult>)] = new FacadeHandler<AgentDeploymentShowRequest, AgentDeploymentShowResult>(operations.ShowAgentDeploymentAsync),
            [typeof(ICommandHandler<DeploymentShowRequest, DeploymentResult<CodeAgentDeployment>>)] = new FacadeHandler<DeploymentShowRequest, DeploymentResult<CodeAgentDeployment>>(operations.GetDeploymentAsync),
            [typeof(ICommandHandler<AgentDeploymentUpdateRequest, AgentDeploymentUpdateResult>)] = new FacadeHandler<AgentDeploymentUpdateRequest, AgentDeploymentUpdateResult>(operations.UpdateAgentDeploymentAsync),
            [typeof(ICommandHandler<DeploymentUpdateRequest, DeploymentResult<CodeAgentDeployment>>)] = new FacadeHandler<DeploymentUpdateRequest, DeploymentResult<CodeAgentDeployment>>(operations.UpdateDeploymentAsync),
            [typeof(ICommandHandler<AgentDeploymentConnectionRequest, AgentDeploymentConnectionResult>)] = new FacadeHandler<AgentDeploymentConnectionRequest, AgentDeploymentConnectionResult>(operations.UpdateAgentDeploymentConnectionAsync),
            [typeof(ICommandHandler<AgentDeploymentConnectionShowRequest, AgentDeploymentConnectionResult>)] = new FacadeHandler<AgentDeploymentConnectionShowRequest, AgentDeploymentConnectionResult>(operations.ShowAgentDeploymentConnectionAsync),
            [typeof(ICommandHandler<AgentDeploymentConnectorListRequest, AgentDeploymentConnectorListResult>)] = new FacadeHandler<AgentDeploymentConnectorListRequest, AgentDeploymentConnectorListResult>(operations.ListAgentDeploymentConnectorsAsync),
            [typeof(ICommandHandler<AgentDeploymentConnectionCreateRequest, AgentDeploymentConnectionCreateResult>)] = new FacadeHandler<AgentDeploymentConnectionCreateRequest, AgentDeploymentConnectionCreateResult>(operations.CreateAgentDeploymentConnectionAsync),
            [typeof(ICommandHandler<AgentDeploymentConnectionReferenceCreateRequest, AgentDeploymentConnectionReferenceCreateResult>)] = new FacadeHandler<AgentDeploymentConnectionReferenceCreateRequest, AgentDeploymentConnectionReferenceCreateResult>(operations.CreateAgentDeploymentConnectionReferenceAsync),
            [typeof(ICommandHandler<AgentDeploymentConnectionRemoveRequest, AgentDeploymentConnectionRemoveResult>)] = new FacadeHandler<AgentDeploymentConnectionRemoveRequest, AgentDeploymentConnectionRemoveResult>(operations.RemoveAgentDeploymentConnectionAsync),
            [typeof(ICommandHandler<AgentDeploymentDeleteRequest, AgentDeploymentDeleteResult>)] = new FacadeHandler<AgentDeploymentDeleteRequest, AgentDeploymentDeleteResult>(operations.DeleteAgentDeploymentAsync),
            [typeof(ICommandHandler<DeploymentDeleteRequest, DeploymentResult<CodeAgentDeployment>>)] = new FacadeHandler<DeploymentDeleteRequest, DeploymentResult<CodeAgentDeployment>>(operations.DeleteDeploymentAsync),
            [typeof(ICommandHandler<DeploymentOAuthCreateRequest, DeploymentResult<DeploymentOAuthConnection>>)] = new FacadeHandler<DeploymentOAuthCreateRequest, DeploymentResult<DeploymentOAuthConnection>>(operations.CreateDeploymentOAuthAsync),
            [typeof(ICommandHandler<DeploymentOAuthUpdateRequest, DeploymentResult<DeploymentOAuthConnection>>)] = new FacadeHandler<DeploymentOAuthUpdateRequest, DeploymentResult<DeploymentOAuthConnection>>(operations.UpdateDeploymentOAuthAsync),
            [typeof(ICommandHandler<DeploymentOAuthShowRequest, DeploymentResult<DeploymentOAuthConnection>>)] = new FacadeHandler<DeploymentOAuthShowRequest, DeploymentResult<DeploymentOAuthConnection>>(operations.ShowDeploymentOAuthAsync),
            [typeof(ICommandHandler<DeploymentOAuthListRequest, DeploymentResult<DeploymentOAuthConnection[]>>)] = new FacadeHandler<DeploymentOAuthListRequest, DeploymentResult<DeploymentOAuthConnection[]>>((request, token) => operations.ListDeploymentOAuthAsync(request, token)),
            [typeof(ICommandHandler<DeploymentOAuthDeleteRequest, DeploymentResult<DeploymentOAuthConnection>>)] = new FacadeHandler<DeploymentOAuthDeleteRequest, DeploymentResult<DeploymentOAuthConnection>>((request, token) => operations.DeleteDeploymentOAuthAsync(request, token)),
            [typeof(ICommandHandler<DeploymentOAuthProviderListRequest, OAuthProviderDefinition[]>)] = new FacadeHandler<DeploymentOAuthProviderListRequest, OAuthProviderDefinition[]>(operations.ListDeploymentOAuthProvidersAsync),
            [typeof(ICommandHandler<DeploymentChannelCreateRequest, DeploymentResult<DeploymentChannelRecord>>)] = new FacadeHandler<DeploymentChannelCreateRequest, DeploymentResult<DeploymentChannelRecord>>((request, token) => operations.CreateChannelAsync(request, token)),
            [typeof(ICommandHandler<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>)] = new FacadeHandler<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>((request, token) => operations.UpdateChannelAsync(request, token)),
            [typeof(ICommandHandler<DeploymentChannelDeleteRequest, DeploymentResult<DeploymentChannelRecord>>)] = new FacadeHandler<DeploymentChannelDeleteRequest, DeploymentResult<DeploymentChannelRecord>>((request, token) => operations.DeleteChannelAsync(request, token)),
            [typeof(ICommandHandler<DeploymentChannelShowRequest, DeploymentResult<DeploymentChannelRecord>>)] = new FacadeHandler<DeploymentChannelShowRequest, DeploymentResult<DeploymentChannelRecord>>((request, token) => operations.GetChannelAsync(request, token)),
            [typeof(ICommandHandler<DeploymentChannelListRequest, DeploymentResult<DeploymentChannelRecord[]>>)] = new FacadeHandler<DeploymentChannelListRequest, DeploymentResult<DeploymentChannelRecord[]>>((request, token) => operations.ListChannelsAsync(request, token)),
            [typeof(IDeploymentAgentTypeResolution)] = new FacadeAgentTypeResolution(operations),
            [typeof(ICommandHandler<AbsPreviewRequest, AbsProvisioningReport>)] = new FacadeHandler<AbsPreviewRequest, AbsProvisioningReport>(operations.PreviewAzureBotAsync),
            [typeof(ICommandHandler<AbsApplyRequest, AbsProvisioningReport>)] = new FacadeHandler<AbsApplyRequest, AbsProvisioningReport>(operations.ApplyAzureBotAsync),
            [typeof(ICommandHandler<AuthLoginRequest, AuthLoginResult>)] = new FacadeHandler<AuthLoginRequest, AuthLoginResult>(operations.LoginAsync),
            [typeof(ICommandHandler<AuthStatusRequest, AuthStatusReport>)] = new FacadeHandler<AuthStatusRequest, AuthStatusReport>((_, token) => operations.GetAuthStatusAsync(token)),
            [typeof(ICommandHandler<AuthSwitchRequest, AuthProfileSummary>)] = new FacadeHandler<AuthSwitchRequest, AuthProfileSummary>((request, token) => operations.SwitchAuthProfileAsync(request.Account, request.Ring, token)),
            [typeof(ICommandHandler<AuthLogoutRequest, AuthLogoutResult>)] = new FacadeHandler<AuthLogoutRequest, AuthLogoutResult>((_, token) => operations.LogoutAsync(token)),
            [typeof(ICommandHandler<BlueprintIdentityRequest, BlueprintIdentityReport>)] = new FacadeHandler<BlueprintIdentityRequest, BlueprintIdentityReport>(operations.CreateBlueprintIdentityAsync),
            [typeof(ICommandHandler<AppRegistrationIdentityRequest, AppRegistrationIdentityReport>)] = new FacadeHandler<AppRegistrationIdentityRequest, AppRegistrationIdentityReport>(operations.CreateAppRegistrationIdentityAsync),
        };

        if (operations is IAgentEvaluationOperations evaluations)
        {
            foreach (var (contract, handler) in EvaluationHandlers(evaluations))
            {
                handlers[contract] = handler;
            }
        }

        _handlers = handlers;
    }

    private static IEnumerable<(Type Contract, object Handler)> EvaluationHandlers(IAgentEvaluationOperations operations) =>
    [
        (typeof(ICommandHandler<EvaluationListRequest, EvaluationListResult>), new FacadeHandler<EvaluationListRequest, EvaluationListResult>(operations.ListEvaluationsAsync)),
        (typeof(ICommandHandler<EvaluationShowRequest, EvaluationRunResult>), new FacadeHandler<EvaluationShowRequest, EvaluationRunResult>(operations.ShowEvaluationAsync)),
        (typeof(ICommandHandler<EvaluationRunRequest, EvaluationRunResult>), new FacadeHandler<EvaluationRunRequest, EvaluationRunResult>(operations.RunEvaluationAsync)),
        (typeof(ICommandHandler<EvaluationCancelRequest, EvaluationRunResult>), new FacadeHandler<EvaluationCancelRequest, EvaluationRunResult>(operations.CancelEvaluationAsync)),
        (typeof(ICommandHandler<EvaluationDeleteRequest, EvaluationRunResult>), new FacadeHandler<EvaluationDeleteRequest, EvaluationRunResult>(operations.DeleteEvaluationAsync)),
        (typeof(ICommandHandler<EvaluationGenerateRequest, EvaluationGenerateResult>), new FacadeHandler<EvaluationGenerateRequest, EvaluationGenerateResult>(operations.GenerateEvaluationConversationsAsync)),
        (typeof(ICommandHandler<EvaluationTestSetListRequest, EvaluationTestSetListResult>), new FacadeHandler<EvaluationTestSetListRequest, EvaluationTestSetListResult>(operations.ListEvaluationTestSetsAsync)),
        (typeof(ICommandHandler<EvaluationTestSetShowRequest, EvaluationTestSetResult>), new FacadeHandler<EvaluationTestSetShowRequest, EvaluationTestSetResult>(operations.ShowEvaluationTestSetAsync)),
        (typeof(ICommandHandler<EvaluationTestSetCreateRequest, EvaluationTestSetResult>), new FacadeHandler<EvaluationTestSetCreateRequest, EvaluationTestSetResult>(operations.CreateEvaluationTestSetAsync)),
        (typeof(ICommandHandler<EvaluationTestSetUpdateRequest, EvaluationTestSetResult>), new FacadeHandler<EvaluationTestSetUpdateRequest, EvaluationTestSetResult>(operations.UpdateEvaluationTestSetAsync)),
        (typeof(ICommandHandler<EvaluationTestSetExportRequest, EvaluationTestSetResult>), new FacadeHandler<EvaluationTestSetExportRequest, EvaluationTestSetResult>(operations.ExportEvaluationTestSetAsync)),
        (typeof(ICommandHandler<EvaluationTestSetDeleteRequest, EvaluationTestSetDeleteResult>), new FacadeHandler<EvaluationTestSetDeleteRequest, EvaluationTestSetDeleteResult>(operations.DeleteEvaluationTestSetAsync)),
    ];

    /// <inheritdoc />
    public object? GetService(Type serviceType) => _handlers.GetValueOrDefault(serviceType);
}

/// <summary>Adapts the facade's agent-type routing to the resolver seam.</summary>
internal sealed class FacadeAgentTypeResolution : IDeploymentAgentTypeResolution
{
    private readonly IAgentHostingOperations _operations;

    public FacadeAgentTypeResolution(IAgentHostingOperations operations) => _operations = operations;

    public DeploymentAgentTypeResult Resolve(DeploymentAgentTypeQuery query) => _operations.ResolveDeploymentAgentType(query);
}

/// <summary>Adapts a facade call to the command handler seam.</summary>
/// <typeparam name="TRequest">The request the command binds.</typeparam>
/// <typeparam name="TResult">The result produced.</typeparam>
internal sealed class FacadeHandler<TRequest, TResult> : ICommandHandler<TRequest, TResult>
    where TResult : notnull
{
    private readonly Func<TRequest, CancellationToken, Task<TResult>> _invoke;

    public FacadeHandler(Func<TRequest, CancellationToken, Task<TResult>> invoke) => _invoke = invoke;

    public Task<TResult> HandleAsync(TRequest request, CancellationToken cancellationToken) => _invoke(request, cancellationToken);
}
