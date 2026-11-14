using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Builds the follow-up commands a connection verb suggests, carrying the selectors the operator
/// supplied so the suggestion runs as printed.
/// </summary>
public static class ConnectionCommandHints
{
    /// <summary>Shown where a suggestion needs a deployment the request never named.</summary>
    public const string DeploymentPlaceholder = "--deployment-name <name>";

    /// <summary>
    /// Repeats the selectors a request supplied, so a suggested command addresses the same deployment.
    /// </summary>
    /// <param name="request">The command input whose selectors are echoed.</param>
    /// <param name="agentId">The record the request's agent selector resolved to, which the suggestion names by identifier; <see langword="null"/> when it is not known.</param>
    /// <param name="includeSchemaName">Whether the suggested command accepts <c>--schema-name</c>.</param>
    /// <returns>The selector arguments, in the order the commands declare them.</returns>
    public static string Selectors(IAgentDeploymentConnectionSelector request, string? agentId, bool includeSchemaName = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        string[] supplied =
        [
            .. Candidates(request, agentId, includeSchemaName).Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).Select(entry => $"{entry.Option} {CommandLineArgument.Quote(entry.Value!)}"),
        ];

        return NamesDeployment(request) ? string.Join(" ", supplied) : string.Join(" ", [.. supplied, DeploymentPlaceholder]);
    }

    /// <summary>The command that binds every reference on one connector to a connection.</summary>
    /// <param name="request">The request whose selectors the suggestion repeats.</param>
    /// <param name="agentId">The record the request's agent selector resolved to, or <see langword="null"/> when it is not known.</param>
    /// <param name="connectorId">The connector whose references would be bound.</param>
    /// <param name="connectionId">The connection to bind.</param>
    /// <returns>A command line that can be run as printed.</returns>
    public static string BindConnector(IAgentDeploymentConnectionSelector request, string? agentId, string connectorId, string connectionId) =>
        BindConnectorCore(Selectors(request, agentId), connectorId, connectionId);

    public static string BindConnector(string connectorId, string connectionId) =>
        BindConnectorCore(DeploymentPlaceholder, connectorId, connectionId);

    private static string BindConnectorCore(string scope, string connectorId, string connectionId) =>
        $"msagent deployment connection update {scope} --connector {connectorId} --connection-id {connectionId}";

    /// <summary>The command that binds one connection reference.</summary>
    /// <param name="request">The request whose selectors the suggestion repeats.</param>
    /// <param name="agentId">The record the request's agent selector resolved to, or <see langword="null"/> when it is not known.</param>
    /// <param name="logicalName">The reference to bind.</param>
    /// <returns>A command line that can be run as printed.</returns>
    public static string BindReference(IAgentDeploymentConnectionSelector request, string? agentId, string logicalName) =>
        $"msagent deployment connection update {Selectors(request, agentId)} --connection-reference {logicalName}";

    /// <summary>The command that lists this agent's connection references.</summary>
    /// <param name="request">The request whose selectors the suggestion repeats.</param>
    /// <param name="agentId">The record the request's agent selector resolved to, or <see langword="null"/> when it is not known.</param>
    /// <returns>A command line that can be run as printed.</returns>
    public static string ListConnections(IAgentDeploymentConnectionSelector request, string? agentId) =>
        $"msagent deployment connection list {Selectors(request, agentId)}";

    /// <summary>
    /// The command that deletes one connection on its own, naming the connector the delete address
    /// needs, which a removed reference no longer records.
    /// </summary>
    /// <param name="request">The request whose selectors the suggestion repeats.</param>
    /// <param name="agentId">The record the request's agent selector resolved to, or <see langword="null"/> when it is not known.</param>
    /// <param name="environmentId">The environment the request named directly, when it named one.</param>
    /// <param name="connectorId">The connector the connection serves.</param>
    /// <param name="connectionId">The connection to delete.</param>
    /// <param name="force">Whether the deletion still needs consent for what else holds it.</param>
    /// <returns>A command line that can be run as printed.</returns>
    public static string DeleteConnection(IAgentDeploymentConnectionSelector request, string? agentId, string? environmentId, string connectorId, string connectionId, bool force)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DeleteConnectionCore(string.IsNullOrWhiteSpace(environmentId) ? Selectors(request, agentId) : EnvironmentScope(environmentId), connectorId, connectionId, force);
    }

    public static string DeleteConnection(string environmentId, string connectorId, string connectionId) =>
        DeleteConnectionCore(EnvironmentScope(environmentId), connectorId, connectionId, force: false);

    private static string DeleteConnectionCore(string scope, string connectorId, string connectionId, bool force) =>
        $"msagent deployment connection remove {scope} --connector {connectorId} --connection-id {connectionId}{(force ? " --force" : string.Empty)}";

    private static string EnvironmentScope(string environmentId) => $"--environment-id {CommandLineArgument.Quote(environmentId)}";

    /// <summary>
    /// The command that lists the environment's connectors, addressed the way this request was.
    /// </summary>
    /// <param name="request">The request whose selectors the suggestion repeats.</param>
    /// <param name="agentId">The record the request's agent selector resolved to, or <see langword="null"/> when it is not known.</param>
    /// <param name="environmentId">The environment the request named directly, when it named one.</param>
    /// <returns>A command line that can be run as printed.</returns>
    public static string ListConnectors(IAgentDeploymentConnectionSelector request, string? agentId, string? environmentId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(environmentId)
            ? $"msagent deployment connection connectors {Selectors(request, agentId, includeSchemaName: false)} --filter <text>"
            : $"msagent deployment connection connectors {EnvironmentScope(environmentId)} --filter <text>";
    }

    private static bool NamesDeployment(IAgentDeploymentConnectionSelector request) =>
        !string.IsNullOrWhiteSpace(request.DeploymentId) || !string.IsNullOrWhiteSpace(request.DeploymentName);

    private static (string Option, string? Value)[] Candidates(IAgentDeploymentConnectionSelector request, string? agentId, bool includeSchemaName)
    {
        (string Option, string? Value)[] records =
        [
            ("--project", request.Project),
            ("--agent-id", string.IsNullOrWhiteSpace(request.AgentId) && string.IsNullOrWhiteSpace(request.AgentName) ? null : agentId ?? request.AgentId),
            ("--deployment-id", request.DeploymentId),
            ("--deployment-name", request.DeploymentName),
        ];

        return includeSchemaName ? [.. records, ("--schema-name", request.SchemaName)] : records;
    }
}
