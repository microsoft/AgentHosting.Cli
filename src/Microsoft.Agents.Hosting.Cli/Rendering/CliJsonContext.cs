using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>
/// Source-generated <see cref="System.Text.Json"/> metadata for the command-result payloads that the
/// renderer flattens into the JSON success envelope. camelCase property naming matches the shipped
/// TypeScript property names carried by the provisional oracle.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuthLoginResult))]
[JsonSerializable(typeof(AuthStatusReport))]
[JsonSerializable(typeof(AuthProfileSummary))]
[JsonSerializable(typeof(AuthLogoutResult))]
[JsonSerializable(typeof(AuthSetupGitResult))]
[JsonSerializable(typeof(AuthGitCredentialResult))]
[JsonSerializable(typeof(EnvironmentListResult))]
[JsonSerializable(typeof(EnvironmentSelectionResult))]
[JsonSerializable(typeof(AgentCreateResult))]
[JsonSerializable(typeof(AgentDeployResult))]
[JsonSerializable(typeof(AgentPackageUploadResult))]
[JsonSerializable(typeof(McsAmsAgentDeployResult))]
[JsonSerializable(typeof(McsAmsOperationShowResult))]
[JsonSerializable(typeof(McsAmsAgentCaptureResult))]
[JsonSerializable(typeof(AgentPackageUploadPartialResult))]
[JsonSerializable(typeof(AbsProvisioningReport))]
[JsonSerializable(typeof(BlueprintIdentityReport))]
[JsonSerializable(typeof(AppRegistrationIdentityReport))]
[JsonSerializable(typeof(ManagedAgentCreateResult))]
[JsonSerializable(typeof(ManagedAgentInitResult))]
[JsonSerializable(typeof(ManagedAgentListResult))]
[JsonSerializable(typeof(ManagedAgentShowResult))]
[JsonSerializable(typeof(ManagedAgentDeleteResult))]
[JsonSerializable(typeof(ManagedAgentShareResult))]
[JsonSerializable(typeof(ManagedAgentShareShowResult))]
[JsonSerializable(typeof(ManagedAgentUnshareResult))]
[JsonSerializable(typeof(ManagedAgentPullResult))]
[JsonSerializable(typeof(ManagedAgentDeployResult))]
[JsonSerializable(typeof(AgentDeploymentCreateResult))]
[JsonSerializable(typeof(AgentDeploymentListResult))]
[JsonSerializable(typeof(AgentDeploymentShowResult))]
[JsonSerializable(typeof(AgentDeploymentUpdateResult))]
[JsonSerializable(typeof(AgentDeploymentDeleteResult))]
[JsonSerializable(typeof(AgentDeploymentConnectionResult))]
[JsonSerializable(typeof(AgentDeploymentConnectorListResult))]
[JsonSerializable(typeof(AgentDeploymentConnectionCreateResult))]
[JsonSerializable(typeof(AgentDeploymentConnectionReferenceCreateResult))]
[JsonSerializable(typeof(AgentDeploymentConnectionRemoveResult))]
[JsonSerializable(typeof(TelemetryControlReport))]
[JsonSerializable(typeof(DeploymentResult<CodeAgentDeployment>))]
[JsonSerializable(typeof(DeploymentResult<DeploymentOAuthConnection>))]
[JsonSerializable(typeof(DeploymentResult<DeploymentOAuthConnection[]>))]
[JsonSerializable(typeof(DeploymentResult<DeploymentChannelRecord>))]
[JsonSerializable(typeof(DeploymentResult<DeploymentChannelRecord[]>))]
[JsonSerializable(typeof(CodeAgentDeployment))]
[JsonSerializable(typeof(DeploymentOAuthConnection[]))]
[JsonSerializable(typeof(DeploymentOAuthConnection))]
[JsonSerializable(typeof(OAuthProviderDefinition[]))]
[JsonSerializable(typeof(OAuthProviderDefinition))]
[JsonSerializable(typeof(OAuthProviderParameterDefinition))]
[JsonSerializable(typeof(ILiveTestStreamingResult))]
[JsonSerializable(typeof(EvaluationListResult))]
[JsonSerializable(typeof(EvaluationRunResult))]
[JsonSerializable(typeof(EvaluationTestSetListResult))]
[JsonSerializable(typeof(EvaluationTestSetResult))]
[JsonSerializable(typeof(EvaluationTestSetDeleteResult))]
[JsonSerializable(typeof(EvaluationGenerateResult))]
[JsonSerializable(typeof(DeploymentChannelRecord))]
[JsonSerializable(typeof(DeploymentChannelRecord[]))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
