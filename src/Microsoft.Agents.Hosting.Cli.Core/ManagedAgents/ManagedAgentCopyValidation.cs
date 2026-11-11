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

        return string.IsNullOrWhiteSpace(request.Name) ? new UsageException("`msagent agent copy` requires --agent-name: the copy needs a display name of its own.") : null;
    }
}
