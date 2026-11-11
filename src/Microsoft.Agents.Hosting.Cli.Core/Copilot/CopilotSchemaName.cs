using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>Derives and validates the schema names that identify an agent within an environment.</summary>
public sealed class CopilotSchemaName
{
    private readonly ICopilotNamingRules _namingRules;

    /// <summary>Creates the service.</summary>
    /// <param name="namingRules">The naming rules Copilot Studio enforces.</param>
    public CopilotSchemaName(ICopilotNamingRules namingRules) => _namingRules = namingRules ?? throw new ArgumentNullException(nameof(namingRules));

    /// <summary>Derives a schema name, or validates the one the caller supplied.</summary>
    /// <param name="displayName">The agent's display name.</param>
    /// <param name="publisherPrefix">The publisher prefix.</param>
    /// <param name="explicitSchemaName">An explicit schema name that overrides derivation.</param>
    /// <returns>The validated schema name.</returns>
    public string Derive(string? displayName, string? publisherPrefix, string? explicitSchemaName)
    {
        if (!string.IsNullOrWhiteSpace(explicitSchemaName))
        {
            return Validate(explicitSchemaName);
        }

        if (string.IsNullOrWhiteSpace(publisherPrefix))
        {
            throw new UsageException("A publisher prefix is required. Pass --publisher-prefix, or supply --schema-name directly.");
        }

        if (!_namingRules.IsValidPublisherPrefix(publisherPrefix))
        {
            throw new UsageException($"The publisher prefix '{publisherPrefix}' is not valid. Use 2 to 8 alphanumeric characters starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new UsageException("A name is required to derive a schema name. Pass --agent-name, or supply --schema-name directly.");
        }

        return _namingRules.DeriveAgentSchemaName(publisherPrefix, displayName) ?? throw new UsageException($"The name '{displayName}' contains no ASCII letters or digits, so no schema name can be derived. Supply --schema-name.");
    }

    /// <summary>Validates a schema name against the Copilot Studio contract.</summary>
    /// <param name="schemaName">The schema name to validate.</param>
    /// <returns>The schema name, unchanged.</returns>
    public string Validate(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new UsageException("A schema name is required.");
        }

        if (schemaName.Length > _namingRules.MaximumSchemaNameLength)
        {
            throw new UsageException($"The schema name '{schemaName}' is longer than {_namingRules.MaximumSchemaNameLength} characters.");
        }

        if (!_namingRules.IsValidSchemaName(schemaName))
        {
            throw new UsageException($"The schema name '{schemaName}' contains unsupported characters. Use letters, digits, and the characters _ - . {{ }} !");
        }

        return schemaName;
    }
}
