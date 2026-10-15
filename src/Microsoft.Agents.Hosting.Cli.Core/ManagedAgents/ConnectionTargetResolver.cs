using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The environment a connector verb acts on, and the workspace when one was resolved.</summary>
/// <param name="Environment">The environment the verb acts on.</param>
/// <param name="WorkspaceDirectory">The agent workspace on disk, absent when the environment was named directly.</param>
public sealed record ConnectionTarget(CopilotEnvironmentContext Environment, string? WorkspaceDirectory);

/// <summary>
/// Resolves the environment a connector verb acts on, either from a named environment or from the
/// deployment that targets one.
/// </summary>
public sealed class ConnectionTargetResolver
{
    private readonly AgentDeploymentConnectionResolver _deployments;
    private readonly CopilotEnvironmentResolver _environments;

    /// <summary>Creates the resolver.</summary>
    /// <param name="deployments">Resolves the deployment, environment and workspace a connection verb acts on.</param>
    /// <param name="environments">Resolves an environment named directly.</param>
    public ConnectionTargetResolver(AgentDeploymentConnectionResolver deployments, CopilotEnvironmentResolver environments)
    {
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
    }

    /// <summary>Resolves the environment one request addresses.</summary>
    /// <param name="selector">The deployment selectors the request carries.</param>
    /// <param name="environmentId">The environment named directly, when one was.</param>
    /// <param name="verb">The verb named in usage messages.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The environment, and the workspace when a deployment resolved one.</returns>
    /// <exception cref="UsageException">The request names an environment and a deployment at once.</exception>
    public async Task<ConnectionTarget> ResolveAsync(IAgentDeploymentConnectionSelector selector, string? environmentId, string verb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (string.IsNullOrWhiteSpace(environmentId))
        {
            var target = await _deployments.ResolveWorkspaceAsync(selector, verb, cancellationToken).ConfigureAwait(false);

            return new ConnectionTarget(target.Environment, target.WorkspaceDirectory);
        }

        RequireNoDeploymentSelector(selector, verb);

        return new ConnectionTarget(await _environments.ResolveAsync(environmentId.Trim(), cancellationToken).ConfigureAwait(false), null);
    }

    private static void RequireNoDeploymentSelector(IAgentDeploymentConnectionSelector selector, string verb)
    {
        string[] supplied =
        [
            .. new[]
            {
                ("--deployment-id", selector.DeploymentId),
                ("--deployment-name", selector.DeploymentName),
                ("--agent-id", selector.AgentId),
                ("--agent-name", selector.AgentName),
            }
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Item2))
            .Select(entry => entry.Item1),
        ];

        if (supplied.Length > 0)
        {
            throw new UsageException($"`{verb}` takes --environment-id or a deployment, not both. Drop --environment-id, or remove {string.Join(" and ", supplied)}.");
        }
    }
}
