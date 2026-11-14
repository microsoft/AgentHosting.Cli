using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>Checks agent display names against the rules Copilot Studio enforces.</summary>
public sealed class CopilotAgentName
{
    private readonly ICopilotNamingRules _namingRules;

    /// <summary>Creates the service.</summary>
    /// <param name="namingRules">The naming rules Copilot Studio enforces.</param>
    public CopilotAgentName(ICopilotNamingRules namingRules) => _namingRules = namingRules ?? throw new ArgumentNullException(nameof(namingRules));

    /// <summary>Finds why Copilot Studio would refuse an agent's display name.</summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>A clause that reads after a subject naming the display name, or <see langword="null"/> when the name is accepted.</returns>
    public string? FindProblem(string displayName) => _namingRules.FindAgentNameProblem(displayName);

    /// <summary>Refuses an agent display name Copilot Studio would refuse.</summary>
    /// <param name="displayName">The display name.</param>
    /// <param name="subject">What names the display name in the refusal, such as the option that supplied it.</param>
    /// <returns>The display name, unchanged.</returns>
    public string Require(string displayName, string subject) => FindProblem(displayName) is { } problem ? throw new UsageException($"{subject} {problem}.") : displayName;
}
