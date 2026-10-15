using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Env;

/// <summary><c>msagent env select</c>, which takes the environment id positionally.</summary>
[Command("select", Description = "Select the active Power Platform environment for later commands.", ParentGroup = typeof(EnvGroup), SortPriority = 20)]
public sealed class EnvSelectCommand : CommandBase<EnvironmentSelectRequest, EnvironmentSelectionResult>
{
    private const string EnvironmentId = "environment-id";

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
        [OptionCatalog.Argument(EnvironmentId, "Environment id to make active.")];

    /// <inheritdoc />
    protected override EnvironmentSelectRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EnvironmentSelectRequest { EnvironmentId = input.GetArgument(EnvironmentId) ?? string.Empty };
    }
}
