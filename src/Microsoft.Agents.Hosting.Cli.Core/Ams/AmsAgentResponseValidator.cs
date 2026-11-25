using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Checks that an agent AMS returned is the one asked for and can take the operation.</summary>
internal static class AmsAgentResponseValidator
{
    /// <summary>Refuses an agent that is not the requested agent of the home environment or not a code agent.</summary>
    public static void ValidateCodeAgent(AmsAgentReadResult agent, PowerPlatformEnvironment environment, string agentId)
    {
        if (!EqualsId(agent.AgentId, agentId) || !EqualsId(agent.TenantId, environment.TenantId) || !EqualsId(agent.EnvironmentId, environment.EnvironmentId))
        {
            throw AgentDeployException.ResponseMismatch("Agent read", "identity");
        }

        if (!string.Equals(agent.AgentType, "CodeAgent", StringComparison.Ordinal))
        {
            throw AgentDeployException.AgentNotCompatible($"Agent '{agentId}' is a {agent.AgentType} agent, not a CodeAgent; use the commands of its own agent type.");
        }
    }

    /// <summary>Refuses an agent that is not a repository-bound code agent with a declared language and language version.</summary>
    public static void ValidateBoundCodeAgent(AmsAgentReadResult agent, PowerPlatformEnvironment environment, string agentId)
    {
        ValidateCodeAgent(agent, environment, agentId);
        if (!string.Equals(agent.RepositoryBindingStatus, "Bound", StringComparison.Ordinal) || !TryParseNonEmptyGuid(agent.RepositoryId) || string.IsNullOrWhiteSpace(agent.Language) || string.Equals(agent.Language, "NotSpecified", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(agent.LanguageVersion))
        {
            throw AgentDeployException.AgentNotCompatible($"Agent '{agentId}' is not a repository-bound CodeAgent with a declared language and language version.");
        }
    }

    private static bool EqualsId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNonEmptyGuid(string? value) => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;
}
