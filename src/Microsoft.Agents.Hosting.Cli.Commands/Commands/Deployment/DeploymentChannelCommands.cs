using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment.Channel;

[Command("create", Description = "Create (enable) a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 10)]
public sealed class DeploymentChannelCreateCommand : DeploymentChannelCommand<DeploymentChannelCreateRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => MutationCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--channel");

    protected override DeploymentChannelCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelCreateRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
            Parameters = input.GetStrings("--parameters"),
        };
    }
}

[Command("update", Description = "Update a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 20)]
public sealed class DeploymentChannelUpdateCommand : DeploymentChannelCommand<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => MutationCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--channel");

    protected override DeploymentChannelUpdateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelUpdateRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
            Parameters = input.GetStrings("--parameters"),
        };
    }
}

[Command("delete", Description = "Delete a channel from a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 30)]
public sealed class DeploymentChannelDeleteCommand : DeploymentChannelCommand<DeploymentChannelDeleteRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ChannelCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--channel");

    protected override DeploymentChannelDeleteRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelDeleteRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
        };
    }
}

[Command("show", Description = "Show details of a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 40)]
public sealed class DeploymentChannelShowCommand : DeploymentChannelCommand<DeploymentChannelShowRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ChannelCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--channel");

    protected override DeploymentChannelShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelShowRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
        };
    }
}

[Command("list", Description = "List all channels on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 50)]
public sealed class DeploymentChannelListCommand : DeploymentChannelCommand<DeploymentChannelListRequest, DeploymentResult<DeploymentChannelRecord[]>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => DeploymentAddressSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage();

    protected override DeploymentChannelListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelListRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
        };
    }
}

public abstract class DeploymentChannelCommand<TRequest, TResult> : CodeAgentDeploymentCommand<TRequest, TResult>
    where TResult : notnull
{
    protected override string CommandPath => "msagent deployment channel";

    protected static IReadOnlyList<CommandSymbol> ChannelCommandSymbols =>
    [
        .. DeploymentAddressSymbols,
        ChannelName,
    ];

    protected static IReadOnlyList<CommandSymbol> MutationCommandSymbols =>
    [
        .. ChannelCommandSymbols,
        Parameters,
    ];

    private static CommandOption ChannelName =>
        new(
            "--channel",
            "The channel type. Supported: directline, m365extensions, msteams, slack, webchat.")
        {
            IsRequired = true,
        };

    private static CommandOption Parameters =>
        OptionCatalog.Strings(
            "--parameters",
            "Channel-specific Key=Value assignments. " +
            "Specify --channel <name> to see that channel's parameters") with
        {
            AllowMultipleArgumentsPerToken = true,
        };

}
