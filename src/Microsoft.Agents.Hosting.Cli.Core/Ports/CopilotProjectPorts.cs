using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The naming rules Copilot Studio applies to agents and custom connectors, as the shared sync library defines them.</summary>
public interface ICopilotNamingRules
{
    /// <summary>The longest schema name Copilot Studio accepts.</summary>
    int MaximumSchemaNameLength { get; }

    /// <summary>The publisher prefix a new agent's schema name carries when nothing names another.</summary>
    string DefaultPublisherPrefix { get; }

    /// <summary>Whether Copilot Studio accepts a schema name's length and characters.</summary>
    /// <param name="schemaName">The schema name.</param>
    /// <returns><see langword="true"/> when the schema name is accepted.</returns>
    bool IsValidSchemaName(string schemaName);

    /// <summary>Whether Copilot Studio accepts a publisher prefix.</summary>
    /// <param name="publisherPrefix">The publisher prefix.</param>
    /// <returns><see langword="true"/> when the publisher prefix is accepted.</returns>
    bool IsValidPublisherPrefix(string publisherPrefix);

    /// <summary>Derives an agent's schema name from its display name under a publisher prefix.</summary>
    /// <param name="publisherPrefix">A publisher prefix <see cref="IsValidPublisherPrefix"/> accepts.</param>
    /// <param name="displayName">The agent's display name.</param>
    /// <returns>The schema name, or <see langword="null"/> when the display name holds no ASCII letter or digit.</returns>
    string? DeriveAgentSchemaName(string publisherPrefix, string displayName);

    /// <summary>Finds why Copilot Studio would refuse an agent's display name.</summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>A clause that reads after a subject naming the display name, or <see langword="null"/> when the name is accepted.</returns>
    string? FindAgentNameProblem(string displayName);

    /// <summary>Whether a connector internal id ends with the environment suffix only custom connectors carry.</summary>
    /// <param name="connectorInternalId">The connector's internal id.</param>
    /// <returns><see langword="true"/> when the id names a custom connector.</returns>
    bool IsCustomConnectorInternalId(string connectorInternalId);
}

/// <summary>Reads the identity an agent project declares in its settings.</summary>
public interface ICopilotProjectIdentityPort
{
    /// <summary>Reads the identity a project declares.</summary>
    /// <param name="projectDirectory">The directory holding the project's settings.</param>
    /// <returns>The identity, or <see langword="null"/> when the project declares none that can be read.</returns>
    ProjectIdentity? Read(string projectDirectory);
}
