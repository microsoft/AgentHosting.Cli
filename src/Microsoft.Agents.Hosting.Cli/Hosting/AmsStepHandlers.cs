using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>Supplies, ahead of the operations facade, the AMS steps that wrap main's handlers of agent init, agent deploy, agent show, deployment list and deployment show, and nothing else.</summary>
public sealed class AmsStepHandlers : IServiceProvider
{
    private static readonly HashSet<Type> Steps =
    [
        typeof(ICommandHandler<ManagedAgentInitRequest, ManagedAgentInitResult>),
        typeof(ICommandHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>),
        typeof(ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>),
        typeof(ICommandHandler<AgentDeploymentListRequest, AgentDeploymentListResult>),
        typeof(ICommandHandler<AgentDeploymentShowRequest, AgentDeploymentShowResult>),
    ];

    private readonly IServiceProvider _services;

    /// <summary>Creates the provider.</summary>
    /// <param name="services">The application container, which registers the steps in AMS mode.</param>
    public AmsStepHandlers(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType) => Steps.Contains(serviceType) ? _services.GetService(serviceType) : null;
}
