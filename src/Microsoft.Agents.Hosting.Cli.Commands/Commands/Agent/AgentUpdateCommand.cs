using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent update</c>, which renames an AMS code agent or changes its description.</summary>
[Command("update", Description = "Rename an AMS code agent or change its description. Changes only the agent record.", ParentGroup = typeof(AgentGroup), SortPriority = 45, ProgressLabel = "Updating agent")]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class AgentUpdateCommand : CommandBase<AgentAmsUpdateRequest, AgentAmsUpdateResult>
{
    private static readonly AmsRouteRules AmsRules = new("agent update");

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. AmsRoute.AddressSymbols(withDeployment: false),
        OptionCatalog.Value("--new-name", "New agent name, unique in the home environment."),
        OptionCatalog.Value("--description", "New agent description."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [AmsRoute.Usage("Rename an AMS code agent or change its description", withDeployment: false, "(--new-name <new-name> | --description <description>)")];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return AmsRoute.Problem(input, AmsRules) ?? (string.IsNullOrWhiteSpace(input.GetString("--new-name")) && !input.WasProvided("--description") ? "--new-name or --description is required for `msagent agent update`." : null);
    }

    /// <inheritdoc />
    protected override AgentAmsUpdateRequest Bind(ICommandInput input) => AmsRoute.Address(input, new AgentAmsUpdateRequest { NewName = input.GetString("--new-name"), Description = input.WasProvided("--description") ? input.GetString("--description") ?? string.Empty : null });
}
