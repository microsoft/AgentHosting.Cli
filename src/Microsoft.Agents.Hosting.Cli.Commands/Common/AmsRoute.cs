using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Common;

/// <summary>The options that choose and check the AMS route of the command that messages name after <c>msagent</c>.</summary>
public sealed record AmsRouteRules(string Command)
{
    /// <summary>Options that select the AMS route whenever they are given.</summary>
    public IReadOnlyList<string> SelectingOptions { get; init; } = ["--environment-id"];

    /// <summary>Options that moved off the command for code agents: each selects the AMS route, which then refuses it with its pointer.</summary>
    public IReadOnlyDictionary<string, string> MovedOptions { get; init; } = new Dictionary<string, string>();

    /// <summary>Options that only the local routes take: they keep the local route, and the AMS route refuses them.</summary>
    public IReadOnlyList<string> LocalOptions { get; init; } = [];

    /// <summary>Whether the AMS route needs an agent.</summary>
    public bool NeedsAgent { get; init; } = true;

    /// <summary>Whether the AMS route needs a deployment as well as an agent.</summary>
    public bool NeedsDeployment { get; init; }
}

/// <summary>Chooses between a command's local routes and its AMS route from local data only, and checks the options of the AMS route.</summary>
public static class AmsRoute
{
    /// <summary>Builds the AMS-gated option that names the agent's home environment.</summary>
    public static CommandOption EnvironmentOption(string description) => OptionCatalog.Value("--environment-id", description) with { RequiredFeatures = [FeatureName.AmsFeatures] };

    /// <summary>Builds the AMS options that name the agent, the deployment when the command needs one, and the home environment.</summary>
    public static IReadOnlyList<CommandSymbol> AddressSymbols(bool withDeployment) =>
    [
        OptionCatalog.Value("--agent-id", "Agent id. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Agent name in the home environment."),
        .. withDeployment ? [OptionCatalog.Value("--deployment-id", "Deployment id. Wins over --deployment-name."), OptionCatalog.Value("--deployment-name", "Deployment name, unique within the agent.")] : Array.Empty<CommandSymbol>(),
        EnvironmentOption("Agent home environment. Defaults to the active environment."),
    ];

    /// <summary>Builds the usage line of a command's AMS route with the agent, the deployment when needed, and the given required options.</summary>
    public static UsageRoute Usage(string summary, bool withDeployment, params string[] requiredOptions) => new([UsageRoute.Agent, .. withDeployment ? [UsageRoute.Deployment] : Array.Empty<string>(), .. requiredOptions]) { Summary = summary, RequiredFeatures = [FeatureName.AmsFeatures] };

    /// <summary>Reports whether an option that always selects the AMS route was given, refusing it while the AMS features are off.</summary>
    public static bool IsSelected(ICommandInput input, IServiceProvider services, AmsRouteRules rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.SelectingOptions.Concat(rules.MovedOptions.Keys).FirstOrDefault(input.WasProvided) is not { } selecting)
        {
            return false;
        }

        FeatureGate.RequireEnabled(services, FeatureName.AmsFeatures, $"The AMS route of `msagent {rules.Command}`, which {selecting} selects,", $"Remove {selecting} to use a local route.");
        return true;
    }

    /// <summary>Reports whether the command takes its AMS route: an option selects it, or the AMS features are on and no record of the local project matches the agent or deployment named.</summary>
    public static bool IsChosen(ICommandInput input, IServiceProvider services, AmsRouteRules rules) => IsSelected(input, services, rules) || (NamesAgent(input) && !rules.LocalOptions.Any(input.WasProvided) && FeatureGate.IsEnabled(services, FeatureName.AmsFeatures) && DeploymentSelector.Resolve(services, LocalQuery(input)).Source is not (DeploymentAgentTypeSource.DeploymentRecord or DeploymentAgentTypeSource.AgentRecord));

    /// <summary>Takes the AMS route when it is chosen, after refusing its moved, misplaced or missing options, or the local route otherwise.</summary>
    public static Task<object> RouteAsync(ICommandInput input, IServiceProvider services, AmsRouteRules rules, Func<Task<object>> amsRoute, Func<Task<object>> localRoute)
    {
        ArgumentNullException.ThrowIfNull(amsRoute);
        ArgumentNullException.ThrowIfNull(localRoute);

        if (!IsChosen(input, services, rules))
        {
            return localRoute();
        }

        Validate(input, rules);
        return amsRoute();
    }

    /// <summary>Refuses an AMS route whose options are moved, misplaced or missing, before anything is resolved or changed.</summary>
    public static void Validate(ICommandInput input, AmsRouteRules rules)
    {
        if (Problem(input, rules) is { } problem)
        {
            throw new CommandUsageException(problem);
        }
    }

    /// <summary>Returns the first moved, misplaced or missing option of the AMS route, or null when the options fit it.</summary>
    public static string? Problem(ICommandInput input, AmsRouteRules rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);

        return rules.MovedOptions.Keys.FirstOrDefault(input.WasProvided) is { } moved ? $"{moved} is not an option of `msagent {rules.Command}` for a code agent: {rules.MovedOptions[moved]}"
            : rules.LocalOptions.FirstOrDefault(input.WasProvided) is { } local ? $"{local} cannot be combined with the AMS route of `msagent {rules.Command}`."
            : rules.NeedsAgent && !NamesAgent(input) ? $"--agent-id or --agent-name is required for the AMS route of `msagent {rules.Command}`."
            : rules.NeedsDeployment && !Names(input, "--deployment-id", "--deployment-name") ? $"--deployment-id or --deployment-name is required for the AMS route of `msagent {rules.Command}`."
            : null;
    }

    /// <summary>Returns the request with the agent, the deployment and the home environment the input names.</summary>
    public static TRequest Address<TRequest>(ICommandInput input, TRequest request) where TRequest : AgentAmsRequest
    {
        ArgumentNullException.ThrowIfNull(input);

        var addressed = (AgentAmsRequest)request with { AgentId = input.GetString("--agent-id"), AgentName = input.GetString("--agent-name"), EnvironmentId = input.GetString("--environment-id") };
        return (TRequest)(addressed is AgentDeploymentAmsRequest deployment ? deployment with { DeploymentId = input.GetString("--deployment-id"), DeploymentName = input.GetString("--deployment-name") } : addressed);
    }

    private static bool NamesAgent(ICommandInput input) => Names(input, "--agent-id", "--agent-name");

    private static bool Names(ICommandInput input, params string[] options) => options.Any(option => !string.IsNullOrWhiteSpace(input.GetString(option)));

    private static DeploymentAgentTypeQuery LocalQuery(ICommandInput input) => new() { Project = input.GetString("--project"), AgentId = input.GetString("--agent-id"), AgentName = input.GetString("--agent-name"), DeploymentId = input.GetString("--deployment-id"), DeploymentName = input.GetString("--deployment-name"), RequireNamedAgentMatch = true };
}
