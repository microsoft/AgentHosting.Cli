using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary>Shared deployment-address binding and CodeAgent routing guard.</summary>
public abstract class CodeAgentDeploymentCommand<TRequest, TResult> : CommandBase<TRequest, TResult>
    where TResult : notnull
{
    protected abstract string CommandPath { get; }

    protected static IReadOnlyList<CommandSymbol> DeploymentAddressSymbols =>
    [
        OptionCatalog.Value(
            "--project",
            "The project directory holding the configuration. Defaults to the current directory."),
        OptionCatalog.Value(
            "--agent-id",
            "The owning agent id, used with the deployment name to resolve the deployment."),
        OptionCatalog.Value(
            "--deployment-id",
            "The deployment id. Provide this, or the agent id and deployment name."),
        OptionCatalog.Value(
            "--deployment-name",
            "The deployment name, used with the agent id to resolve the deployment."),
    ];

    protected static IReadOnlyList<UsageRoute> DeploymentUsage(params string[] requiredOptions) =>
        [new([UsageRoute.DeploymentAddress, .. requiredOptions])];

    protected DeploymentRequest BindDeploymentRequest(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new DeploymentRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
        };
    }

    protected sealed override void EnsureAvailable(ICommandInput input, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(services);

        EnsureCodeAgentDeployment(BindDeploymentRequest(input), services);
    }

    private void EnsureCodeAgentDeployment(DeploymentRequest request, IServiceProvider services)
    {
        var resolvedDeploymentAgentType = DeploymentSelector.Resolve(
            services,
            new DeploymentAgentTypeQuery
            {
                Project = request.Project,
                AgentId = request.AgentId,
                DeploymentId = request.DeploymentId,
                DeploymentName = request.DeploymentName,
                HasCodeAgentOptions = true,
            });

        if (resolvedDeploymentAgentType.IsMcs)
        {
            throw new CommandUsageException(
                $"`{CommandPath}` applies to a CodeAgent deployment, and this command resolved a {resolvedDeploymentAgentType.AgentType} deployment.");
        }
    }
}
