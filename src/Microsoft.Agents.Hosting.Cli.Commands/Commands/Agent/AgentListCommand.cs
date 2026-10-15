using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent list</c>, where the tenant-wide switch is renamed from <c>--tenant</c>.</summary>
[Command("list", Description = "List the MCS agents or standalone workflows in an environment or across the tenant. One that has never been deployed is not listed.", ParentGroup = typeof(AgentGroup), SortPriority = 30)]
public sealed class AgentListCommand : CommandBase<ManagedAgentListRequest, ManagedAgentListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory to act on. Defaults to the current directory."),
        OptionCatalog.Value("--environment-id", "List this environment instead of the active one."),
        OptionCatalog.Value("--agent-type", "Agent type to list. Use MCSWorkflow to list standalone cloud flows."),
        OptionCatalog.Value("--filter", "Show only workflows whose name contains this text. MCSWorkflow only."),
        OptionCatalog.Switch("--tenant-wide", "List every environment the signed-in account can reach."),
        OptionCatalog.Integer("--limit", "Maximum rows to return. Defaults to 20."),
    ];

    /// <inheritdoc />
    protected override ManagedAgentListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentListRequest
        {
            Project = input.GetString("--project"),
            EnvironmentId = input.GetString("--environment-id"),
            AgentType = input.GetString("--agent-type"),
            Filter = input.GetString("--filter"),
            Tenant = input.GetSwitch("--tenant-wide"),
            Limit = input.GetInt("--limit"),
        };
    }

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var supplied = input.GetString("--agent-type");

        return string.IsNullOrWhiteSpace(supplied) || (AgentTypes.Parse(supplied) is { } agentType && AgentTypes.IsMcs(agentType))
            ? null
            : $"--agent-type '{supplied}' cannot be listed. Use {AgentTypes.McsAgent} or {AgentTypes.McsWorkflow}.";
    }
}
