using Microsoft.Agents.Hosting.Cli.Commands.Features;

namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>One set of options a command requires, rendered as its own line in help usage.</summary>
/// <param name="Options">
/// The required options in the order they should read. A bare option name renders with a value
/// placeholder; a token holding a space renders verbatim, for a pinned value or an either/or group.
/// </param>
public sealed record UsageRoute(params string[] Options)
{
    /// <summary>Either agent selector, for a verb that acts on one agent.</summary>
    public const string Agent = "(--agent-id <agent-id> | --agent-name <agent-name>)";

    /// <summary>Either deployment selector, for a verb that acts on one deployment.</summary>
    public const string Deployment = "(--deployment-id <deployment-id> | --deployment-name <deployment-name>)";

    /// <summary>The deployment id, or the owning agent id with the deployment name.</summary>
    public const string DeploymentAddress = "(--deployment-id <deployment-id> | --agent-id <agent-id> --deployment-name <deployment-name>)";

    /// <summary>What this form is for, shown above the line when a command has more than one.</summary>
    public string? Summary { get; init; }

    /// <summary>Features that must all be enabled before this route appears in usage.</summary>
    public IReadOnlyList<FeatureName> RequiredFeatures { get; init; } = [];
}
