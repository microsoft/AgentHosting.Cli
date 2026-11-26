using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent commit list</c>, which lists the package uploads of an AMS code agent.</summary>
[Command("list", Description = "List the commits of an AMS code agent's package uploads, newest first, with the deployments that run each.", ParentGroup = typeof(AgentCommitGroup), SortPriority = 10)]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class AgentCommitListCommand : CommandBase<AgentCommitListRequest, AgentCommitListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => AmsRoute.AddressSymbols(withDeployment: false);

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [AmsRoute.Usage("List the commits of an AMS code agent", withDeployment: false)];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => AmsRoute.Problem(input, new AmsRouteRules("agent commit list"));

    /// <inheritdoc />
    protected override AgentCommitListRequest Bind(ICommandInput input) => AmsRoute.Address(input, new AgentCommitListRequest());
}
