using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The option rules <c>msagent agent copy</c> enforces before it reads anything.</summary>
public static class ManagedAgentCopyValidation
{
    /// <summary>The failure a copy request's options break, or <see langword="null"/> when they are consistent.</summary>
    /// <param name="request">The command input.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public static UsageException? Problem(ManagedAgentCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new UsageException("`msagent agent copy` requires --agent-name: the copy needs a display name of its own.");
        }

        if (!string.IsNullOrWhiteSpace(request.CloneAgentId) && !string.IsNullOrWhiteSpace(request.CloneWorkflowId))
        {
            return new UsageException("Pass one cloud source: --mcs-clone-agent-id for an agent or --mcs-clone-workflow-id for a workflow.");
        }

        return !string.IsNullOrWhiteSpace(request.CloneWorkflowId) && !string.IsNullOrWhiteSpace(request.SchemaName) ? SchemaNameForWorkflow() : null;
    }

    /// <summary>The failure of a workflow copy given <c>--schema-name</c>, which only an agent has.</summary>
    /// <returns>The failure.</returns>
    public static UsageException SchemaNameForWorkflow() => new("--schema-name names an agent's schema; a workflow has none. Remove --schema-name.");
}
