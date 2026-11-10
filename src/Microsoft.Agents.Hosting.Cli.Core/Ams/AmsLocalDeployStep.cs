using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The AMS step of a local <c>msagent agent deploy</c>: main's deploy, then the deploy record that tells AMS which commit the deployment holds.</summary>
public sealed class AmsLocalDeployStep : ICommandHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>
{
    private const string Command = "msagent agent deploy";

    private readonly IAgentHostingOperations _operations;
    private readonly ManagedAgentResolver _resolver;
    private readonly AmsDeployRecords _records;

    /// <summary>Creates the step.</summary>
    /// <param name="operations">Runs main's deploy.</param>
    /// <param name="resolver">Reads the deployed agent's record.</param>
    /// <param name="records">Sends the deploy record.</param>
    public AmsLocalDeployStep(IAgentHostingOperations operations, ManagedAgentResolver resolver, AmsDeployRecords records)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _records = records ?? throw new ArgumentNullException(nameof(records));
    }

    /// <inheritdoc />
    public async Task<ManagedAgentDeployResult> HandleAsync(ManagedAgentDeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _operations.DeployManagedAgentAsync(request, cancellationToken).ConfigureAwait(false);
        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        if (_resolver.ListAgents(projectDirectory).FirstOrDefault(agent => string.Equals(agent.AgentId, result.AgentId, StringComparison.OrdinalIgnoreCase)) is not { } agent)
        {
            return result;
        }

        var isWorkflow = agent.AgentType == AgentTypes.McsWorkflow;
        var warning = agent.SourceControlMetadata is null
            ? $"AMS doesn't know agent {agent.DisplayName}, so it wasn't told about this deploy. Run `msagent agent init` to register it."
            : await _records.RecordAsync(projectDirectory, agent, result.DeploymentId, isWorkflow ? null : result.TargetMcsAgentId, isWorkflow ? result.McsWorkflowId : null, result.Published, result.DeployedOn, Command, cancellationToken).ConfigureAwait(false);
        return warning is null ? result : result with { Warnings = [.. result.Warnings, warning] };
    }
}
