using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Env;

/// <summary><c>msagent env list</c>.</summary>
[Command("list", Description = "List the Power Platform environments available to the signed-in account.", ParentGroup = typeof(EnvGroup), SortPriority = 10)]
public sealed class EnvListCommand : CommandBase<EnvironmentListRequest, EnvironmentListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    /// <inheritdoc />
    protected override EnvironmentListRequest Bind(ICommandInput input) => new();
}
