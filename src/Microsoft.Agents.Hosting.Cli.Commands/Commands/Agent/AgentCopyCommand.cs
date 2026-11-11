using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent copy</c>.</summary>
[Command("copy", Description = "Create a new agent from an existing one. Prepares and registers local content only; the first deploy creates the new agent in the cloud.", ParentGroup = typeof(AgentGroup), SortPriority = 15, ProgressLabel = "Copying agent")]
public sealed class AgentCopyCommand : CommandBase<ManagedAgentCopyRequest, ManagedAgentCopyResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--agent-name", "Display name of the new agent. Required."),
        OptionCatalog.Value("--mcs-clone-agent-id", "Cloud agent to copy as it is in its environment, by id, instead of a local agent."),
        OptionCatalog.Value("--project", "Local agent to copy: its folder, or a project holding only that agent. Defaults to the current directory. With --mcs-clone-agent-id, the project to register the new agent in."),
        OptionCatalog.Value("--environment-id", "Environment holding the --mcs-clone-agent-id agent, which defaults to the active environment, or the environment to copy a local agent in when it names none."),
        OptionCatalog.Value("--schema-name", "Schema name of the new agent. Generated from --agent-name when omitted."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new("--agent-name") { Summary = "Copy a local agent project" },
        new("--mcs-clone-agent-id", "--agent-name") { Summary = "Copy a cloud agent as it is in its environment" },
    ];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => ManagedAgentCopyValidation.Problem(Bind(input))?.Message;

    /// <inheritdoc />
    protected override ManagedAgentCopyRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentCopyRequest
        {
            Name = input.GetString("--agent-name"),
            CloneAgentId = input.GetString("--mcs-clone-agent-id"),
            EnvironmentId = input.GetString("--environment-id"),
            SchemaName = input.GetString("--schema-name"),
            Project = input.GetString("--project"),
        };
    }
}
