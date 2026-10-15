using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

/// <summary>
/// Pins both registry tables to the field sets agreed against <c>Data.txt</c>.
///
/// The records exist so a row written here lines up with a row from the Agent Management Service, and
/// the omissions are as deliberate as the inclusions: a field the service owns but this build cannot
/// source is left out rather than carried as a column that can only ever be null. Both lists are
/// asserted exactly, so adding, dropping or renaming a field fails here rather than drifting quietly.
///
/// <see cref="LocalAgentDeploymentFields"/> is the one sanctioned exception: Azure Bot Service target
/// coordinates the service schema does not model, carried only for a CodeAgent deployment.
/// </summary>
public sealed class AgentConfigSchemaTests
{
    private static readonly string[] ManagedAgentFields =
    [
        "TenantId",
        "EnvironmentId",
        "AgentId",
        "DisplayName",
        "Description",
        "AgentType",
        "AgentLocation",
        "McsAgentId",
        "McsSchemaName",
        "McsWorkflowId",
        "Origin",
        "Owner",
        "CreatedBy",
        "LastDeployedBy",
        "CreatedTime",
        "ChangedTime",
        "AgentState",
        "EntityTag",
    ];

    private static readonly string[] LocalAgentDeploymentFields =
    [
        "IsDefault",
        "BotResourceId",
        "Location",
        "Sku",
        "OAuthConnections",
        "Channels",
    ];

    private static readonly string[] AgentDeploymentFields =
    [
        "DeploymentId",
        "AgentId",
        "AgentType",
        "DeploymentType",
        "DeploymentName",
        "TenantId",
        "HomeEnvironmentId",
        "EnvironmentId",
        "DisplayName",
        "Description",
        "EntraAppId",
        "EntraAppIdType",
        "Endpoint",
        "CreatedBy",
        "LastDeployedBy",
        "LastDeployedTime",
        "CreatedTime",
        "ChangedTime",
        "EntityTag",
        "IsQuarantined",
        "QuarantinedReason",
        "IsEnabled",
        .. LocalAgentDeploymentFields,
    ];

    private static readonly string[] OmittedManagedAgentFields =
    [
        "PartitionKey",
        "RowKey",
        "SourceControlMetadata",
        "Language",
        "LanguageVersion",
        "PackageName",
        "ClientAppOrigin",
        "Tags",
        "SoftDeletedTime",
        "TimeToLiveInSeconds",
        "RpttlInSeconds",
        "IsDeleted",
    ];

    private static readonly string[] OmittedAgentDeploymentFields =
    [
        "PartitionKey",
        "RowKey",
        "SourceControlMetadata",
        "Language",
        "LanguageVersion",
        "IsCustomEndpoint",
        "PackageName",
    ];

    private static IEnumerable<string> FieldsOf<T>() => typeof(T).GetProperties().Select(property => property.Name);

    [Fact]
    public void ManagedAgentCarriesExactlyTheAgreedFields()
    {
        Assert.Equal(ManagedAgentFields.Order(), FieldsOf<ManagedAgentRecord>().Order());
    }

    [Fact]
    public void AgentDeploymentCarriesExactlyTheAgreedFields()
    {
        Assert.Equal(AgentDeploymentFields.Order(), FieldsOf<AgentDeploymentRecord>().Order());
    }

    [Fact]
    public void NeitherTableCarriesAFieldBeyondTheServiceSchema()
    {
        Assert.Empty(ManagedAgentFields.Except(ServiceFields()));
        Assert.Empty(AgentDeploymentFields.Except(ServiceFields()).Except(LocalAgentDeploymentFields));
    }

    [Fact]
    public void EveryLocalFieldIsAnAzureTargetCoordinateTheServiceDoesNotModel()
    {
        Assert.Empty(LocalAgentDeploymentFields.Intersect(ServiceFields()));
        Assert.All(LocalAgentDeploymentFields, field => Assert.Contains(field, FieldsOf<AgentDeploymentRecord>()));
    }

    [Fact]
    public void OmittedFieldsStayOmitted()
    {
        Assert.Empty(OmittedManagedAgentFields.Intersect(FieldsOf<ManagedAgentRecord>()));
        Assert.Empty(OmittedAgentDeploymentFields.Intersect(FieldsOf<AgentDeploymentRecord>()));
    }

    [Fact]
    public void EveryDocumentedFieldIsEitherCarriedOrDeliberatelyOmitted()
    {
        Assert.Empty(DocumentedManagedAgentFields().Except(ManagedAgentFields).Except(OmittedManagedAgentFields));
        Assert.Empty(DocumentedAgentDeploymentFields().Except(AgentDeploymentFields).Except(OmittedAgentDeploymentFields));
    }

    private static string[] ServiceFields() => [.. DocumentedManagedAgentFields().Concat(DocumentedAgentDeploymentFields())];

    private static string[] DocumentedManagedAgentFields() =>
    [
        "TenantId", "EnvironmentId", "PartitionKey", "RowKey", "AgentId", "DisplayName", "Description",
        "AgentType", "AgentLocation", "McsAgentId", "McsSchemaName", "McsWorkflowId", "SourceControlMetadata", "Language", "LanguageVersion",
        "PackageName", "Origin", "ClientAppOrigin", "Tags", "Owner", "CreatedBy", "LastDeployedBy",
        "CreatedTime", "ChangedTime", "AgentState", "SoftDeletedTime", "EntityTag",
        "TimeToLiveInSeconds", "RpttlInSeconds", "IsDeleted",
    ];

    private static string[] DocumentedAgentDeploymentFields() =>
    [
        "DeploymentId", "PartitionKey", "RowKey", "AgentId", "AgentType", "SourceControlMetadata",
        "DeploymentType", "DeploymentName", "TenantId", "HomeEnvironmentId", "EnvironmentId",
        "EntraAppId", "EntraAppIdType", "Language", "LanguageVersion", "Endpoint", "IsCustomEndpoint",
        "PackageName", "CreatedBy", "LastDeployedBy", "LastDeployedTime", "CreatedTime", "ChangedTime",
        "EntityTag", "IsQuarantined", "QuarantinedReason", "IsEnabled",
    ];
}
