using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Applies the sync library's agent and custom connector naming rules.</summary>
public sealed class SyncNamingRulesAdapter : ICopilotNamingRules
{
    /// <inheritdoc />
    public int MaximumSchemaNameLength => SchemaNameValidator.MaximumLength;

    /// <inheritdoc />
    public string DefaultPublisherPrefix => SchemaNameGenerator.DefaultPublisherPrefix;

    /// <inheritdoc />
    public bool IsValidSchemaName(string schemaName) => SchemaNameValidator.IsValid(schemaName);

    /// <inheritdoc />
    public bool IsValidPublisherPrefix(string publisherPrefix) => SchemaNameGenerator.IsValidPublisherPrefix(publisherPrefix);

    /// <inheritdoc />
    public string? DeriveAgentSchemaName(string publisherPrefix, string displayName) => SchemaNameGenerator.GenerateAgentSchemaName(publisherPrefix, displayName);

    /// <inheritdoc />
    public string? FindAgentNameProblem(string displayName) => AgentNameValidator.FindProblem(displayName);

    /// <inheritdoc />
    public bool IsCustomConnectorInternalId(string connectorInternalId) => CustomConnectorInternalId.EnvironmentSuffixOf(connectorInternalId) is not null;
}
