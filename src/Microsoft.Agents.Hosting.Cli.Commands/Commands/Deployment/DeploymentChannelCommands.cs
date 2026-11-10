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
            NonInteractive = input.NonInteractive,
        };
    }
}

[Command("update", Description = "Update a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 20)]
public sealed class DeploymentChannelUpdateCommand : DeploymentChannelCommand<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. MutationCommandSymbols,
        AssignmentOption("--add-site", "Add a Direct Line or Web Chat site using Key=Value fields."),
        AssignmentOption("--set-site", "Update a Direct Line or Web Chat site using Key=Value fields."),
        AssignmentOption("--remove-site", "Remove a Direct Line or Web Chat site using `siteName` as a selector."),
        AssignmentOption("--add-page", "Add a Facebook page using Key=Value fields."),
        AssignmentOption("--set-page", "Update a Facebook page using Key=Value fields."),
        AssignmentOption("--remove-page", "Remove a Facebook page using `id` as a selector."),
        AssignmentOption("--add-registration", "Add a LINE registration using Key=Value fields."),
        AssignmentOption("--set-registration", "Update a LINE registration using Key=Value fields."),
        AssignmentOption("--remove-registration", "Remove a LINE registration using `generatedId` as a selector."),
    ];

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
            NonInteractive = input.NonInteractive,
            AddSite = input.GetStrings("--add-site"),
            SetSite = input.GetStrings("--set-site"),
            RemoveSite = input.GetStrings("--remove-site"),
            AddPage = input.GetStrings("--add-page"),
            SetPage = input.GetStrings("--set-page"),
            RemovePage = input.GetStrings("--remove-page"),
            AddRegistration = input.GetStrings("--add-registration"),
            SetRegistration = input.GetStrings("--set-registration"),
            RemoveRegistration = input.GetStrings("--remove-registration"),
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
            $"The channel type. Supported: {string.Join(", ", DeploymentChannelCatalog.SupportedChannelNames)}.")
        {
            IsRequired = true,
        };

    private static CommandOption Parameters =>
        AssignmentOption(
            "--parameters",
            "Channel-specific Key=Value assignments. " +
            "Specify --channel <name> to see that channel's parameters");

    protected static CommandOption AssignmentOption(string name, string description) =>
        OptionCatalog.Strings(name, description) with
        {
            AllowMultipleArgumentsPerToken = true,
        };
}
