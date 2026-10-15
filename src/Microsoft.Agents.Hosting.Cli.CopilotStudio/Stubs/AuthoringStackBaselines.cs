using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Abstractions;
using Microsoft.Agents.ObjectModel.Expressions;
using Microsoft.Agents.ObjectModel.PowerFx;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.Stubs;

/// <summary>
/// Baseline implementations of the authoring-stack services the sync library resolves but does not
/// register.
/// </summary>
public sealed class BaselineFeatureConfiguration : IFeatureConfiguration
{
    /// <inheritdoc />
    public long GetInt64Value(string settingName, long defaultValue) => defaultValue;

    /// <inheritdoc />
    public string GetStringValue(string settingName, string defaultValue) => defaultValue;

    /// <inheritdoc />
    public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue) => defaultValue;

    /// <inheritdoc />
    public bool IsTenantFeatureEnabled(string featureName, bool defaultValue) => defaultValue;
}

/// <summary>Supplies <see cref="BaselineFeatureConfiguration"/> for every tenant and environment.</summary>
public sealed class BaselineFeatureConfigurationProvider : IFeatureConfigurationProvider
{
    /// <inheritdoc />
    public IFeatureConfiguration GetConfiguration(Guid azureAdTenantId, string environmentId) => new BaselineFeatureConfiguration();
}

/// <summary>Discards authoring telemetry, which a command-line host does not emit.</summary>
public sealed class NoOperationAuthoringStatisticLogger : IAuthoringStatisticLogger
{
    /// <inheritdoc />
    public void LogTelemetry(ImmutableArray<BotComponentChange> BotComponentChanges, ImmutableArray<EnvironmentVariableChange> EnvironmentVariableChanges, ImmutableArray<ConnectionReferenceChange> ConnectionReferenceChanges, BotEntity? changedEntity, AuthoringOperationType operationType)
    {
    }
}

/// <summary>Returns connector definitions carrying only the connector identifier.</summary>
public sealed class MinimalConnectorDefinitionMetadataService : IConnectorDefinitionMetadataService
{
    /// <inheritdoc />
    public Task<ConnectorDefinition> CreateConnectorDefinitionAsync(AuthoringOperationContextBase authoringOperation, ConnectorDefinitionComponent component, CancellationToken ct) =>
        Task.FromResult(new ConnectorDefinition(connectorId: component.ConnectorId));

    /// <inheritdoc />
    public Task<ConnectorDefinition?> GetHostedConnectorDefinitionAsync(AuthoringOperationContextBase authoringOperation, ConnectorId connectorId, CancellationToken ct) =>
        Task.FromResult<ConnectorDefinition?>(new ConnectorDefinition(connectorId: connectorId));
}

/// <summary>Returns plugin operations unchanged.</summary>
public sealed class PassThroughPluginEnrichmentService : IPluginEnrichmentService
{
    /// <inheritdoc />
    public Task<AIPluginOperation> EnrichAIPluginOperationAsync(AuthoringOperationContextBase context, AIPluginOperation operation, CancellationToken ct) => Task.FromResult(operation);
}

/// <summary>Returns model definitions unchanged.</summary>
public sealed class PassThroughModelEnrichmentService : IAIModelEnrichmentService
{
    /// <inheritdoc />
    public Task<ImmutableArray<AIModelDefinition>> EnrichAIModelsAsync(AuthoringOperationContextBase context, ImmutableArray<AIModelDefinition> aiModelDefinitions, CancellationToken ct) => Task.FromResult(aiModelDefinitions);
}

/// <summary>Resolves no cloud flow definitions; workflows are synchronized separately.</summary>
public sealed class EmptyCloudFlowDefinitionEnrichementService : ICloudFlowDefinitionEnrichementService
{
    /// <inheritdoc />
    public Task<ImmutableArray<CloudFlowDefinition>> GetCloudFlowDefinitionsAsync(AuthoringOperationContextBase context, IEnumerable<Guid> workflowIds, CancellationToken ct) => Task.FromResult(ImmutableArray<CloudFlowDefinition>.Empty);
}

/// <summary>Loads no skill-resource bodies; attachment content is not fetched during sync.</summary>
public sealed class NoSkillResourceContentLoader : ISkillResourceContentLoader
{
    /// <inheritdoc />
    public Task<SkillResource?> LoadAsync(AuthoringOperationContextBase context, FileAttachmentComponent attachment, CancellationToken cancellationToken) => Task.FromResult<SkillResource?>(null);
}

/// <summary>Supplies the Power Fx expression analyzer used to validate expressions in components.</summary>
public sealed class PowerFxExpressionSyntaxAnalyzerProvider : IExpressionSyntaxAnalyzerProvider
{
    /// <inheritdoc />
    public IExpressionSyntaxAnalyzer GetExpressionSyntaxAnalyzer(Guid azureAdTenantId, string environmentId) => new PowerFxSyntaxAnalyzer(new BaselineFeatureConfiguration());
}

/// <summary>
/// Resolves Dataverse user identifiers, which the sync paths this host uses never request.
/// </summary>
public sealed class UnsupportedDataverseUserIdProvider : IDataverseUserIdProvider
{
    /// <inheritdoc />
    public Task<Guid> GetAADUserIdAsync(CdsOrganizationInfo organizationInfo, BotReference botReference, Guid systemUserId, CancellationToken ct) =>
        throw new NotSupportedException("Resolving a Dataverse user id is not supported by the AgentHost CLI.");

    /// <inheritdoc />
    public Task<Guid> GetDataverseUserIdAsync(AuthoringOperationContext context, CancellationToken ct) =>
        throw new NotSupportedException("Resolving a Dataverse user id is not supported by the AgentHost CLI.");
}
