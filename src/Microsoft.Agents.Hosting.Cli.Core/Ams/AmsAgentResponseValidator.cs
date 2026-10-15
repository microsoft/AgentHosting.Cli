using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal static class AmsAgentResponseValidator
{
    public static void ValidateBoundCodeAgent(
        AmsAgentReadResult agent,
        PowerPlatformEnvironment environment,
        string agentId)
    {
        if (!EqualsId(agent.AgentId, agentId) ||
            !EqualsId(agent.TenantId, environment.TenantId) ||
            !EqualsId(agent.EnvironmentId, environment.EnvironmentId))
        {
            throw AgentDeployException.ResponseMismatch("Agent read", "identity");
        }

        if (!string.Equals(agent.AgentType, "CodeAgent", StringComparison.Ordinal) ||
            !string.Equals(agent.RepositoryBindingStatus, "Bound", StringComparison.Ordinal) ||
            !TryParseNonEmptyGuid(agent.RepositoryId) ||
            string.IsNullOrWhiteSpace(agent.Language) ||
            string.Equals(agent.Language, "NotSpecified", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(agent.LanguageVersion))
        {
            throw AgentDeployException.AgentNotCompatible(
                $"Agent '{agentId}' is not a repository-bound CodeAgent with a declared language and language version.");
        }
    }

    private static bool EqualsId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNonEmptyGuid(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;
}
