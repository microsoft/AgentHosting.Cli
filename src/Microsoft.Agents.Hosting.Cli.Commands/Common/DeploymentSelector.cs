using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Common;

/// <summary>The selector options every addressing <c>msagent deployment</c> verb shares, and the routing query they produce.</summary>
public static class DeploymentSelector
{
    /// <summary>The selectors that address a record, which every deployment verb accepts.</summary>
    public static IReadOnlyList<CommandSymbol> McsSymbols =>
    [
        OptionCatalog.Value("--project", "Project directory holding the configuration. Defaults to the current directory."),
        OptionCatalog.Value("--deployment-id", "Deployment id. Supply this, or the agent id with the deployment name."),
        OptionCatalog.Value("--deployment-name", "Deployment name. Resolved together with the agent id."),
        OptionCatalog.Value("--agent-id", "Owning agent id. Resolved together with the deployment name."),
        OptionCatalog.Value("--agent-name", "Owning agent name, used when the agent id is omitted. MCS only."),
    ];

    /// <summary>The record selectors plus the Azure coordinates a CodeAgent deployment needs.</summary>
    public static IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. McsSymbols,
        OptionCatalog.Value("--resource-group", "Target resource group. Auto-resolved when the subscription has exactly one. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--subscription", "Target subscription id. Auto-resolved from the signed-in account when omitted. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--tenant", "Target tenant id. Taken from the resolved subscription when omitted. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
    ];

    /// <summary>Builds the routing query from the parsed selectors.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="hasMcsOptions">Whether a Copilot Studio option was supplied.</param>
    /// <param name="hasCodeAgentOptions">Whether an Azure option was supplied.</param>
    public static DeploymentAgentTypeQuery Query(ICommandInput input, bool hasMcsOptions = false, bool hasCodeAgentOptions = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new DeploymentAgentTypeQuery
        {
            Project = input.GetString("--project"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            HasMcsOptions = hasMcsOptions || !string.IsNullOrWhiteSpace(input.GetString("--agent-name")),
            HasCodeAgentOptions = hasCodeAgentOptions
                || !string.IsNullOrWhiteSpace(input.GetString("--resource-group"))
                || !string.IsNullOrWhiteSpace(input.GetString("--subscription"))
                || !string.IsNullOrWhiteSpace(input.GetString("--tenant")),
        };
    }

    /// <summary>Resolves which agent type the verb targets.</summary>
    /// <param name="services">The resolved service provider.</param>
    /// <param name="query">The routing query.</param>
    public static DeploymentAgentTypeResult Resolve(IServiceProvider services, DeploymentAgentTypeQuery query)
    {
        ArgumentNullException.ThrowIfNull(services);

        return ((IDeploymentAgentTypeResolution?)services.GetService(typeof(IDeploymentAgentTypeResolution))
            ?? throw new CommandConfigurationException("No deployment agent-type resolver is registered.")).Resolve(query);
    }

    /// <summary>Names the selectors the caller supplied, or <see langword="null"/> when none were.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="options">The selector options that count towards addressing a record.</param>
    public static string? SuppliedSelectors(ICommandInput input, params string[] options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        var supplied = options
            .Select(option => (Option: option, Value: input.GetString(option)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => $"{entry.Option} '{entry.Value!.Trim()}'")
            .ToArray();

        return supplied.Length == 0 ? null : string.Join(" and ", supplied);
    }

    /// <summary>Whether the selectors form an address Azure can resolve without a stored record.</summary>
    /// <param name="input">The parsed command line.</param>
    public static bool NamesACloudDeployment(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return !string.IsNullOrWhiteSpace(input.GetString("--agent-id"))
            && !string.IsNullOrWhiteSpace(input.GetString("--deployment-name"));
    }

    /// <summary>
    /// Refuses a verb whose selectors identify nothing to act on, rather than guessing a route and
    /// failing somewhere unrelated. A record the project already holds always passes; naming nothing
    /// at all asks for the selector; naming something the project does not hold reports the mismatch.
    /// </summary>
    /// <param name="resolved">The resolved agent type.</param>
    /// <param name="supplied">The selectors the caller named, or null when none were.</param>
    /// <param name="selector">The selector the verb needs, as it should read in the message.</param>
    /// <param name="listCommand">The command that lists what the project holds.</param>
    /// <exception cref="CommandUsageException">The selectors identify nothing.</exception>
    public static void RequireIdentified(DeploymentAgentTypeResult resolved, string? supplied, string selector, string listCommand)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (resolved.Source is DeploymentAgentTypeSource.DeploymentRecord or DeploymentAgentTypeSource.AgentRecord)
        {
            return;
        }

        if (supplied is null)
        {
            throw new CommandUsageException(UsageMessages.AgentSelectorRequired(selector));
        }

        if (resolved.Source == DeploymentAgentTypeSource.Unresolved)
        {
            throw new CommandUsageException(UsageMessages.NoMatchInProject(supplied, listCommand));
        }
    }

    /// <summary>
    /// Refuses an addressing verb whose selectors identify nothing, unless they form a cloud address
    /// an enabled Azure Bot Service route can still resolve against the service.
    /// </summary>
    /// <param name="services">The resolved service provider.</param>
    /// <param name="input">The parsed command line.</param>
    /// <param name="resolved">The resolved agent type.</param>
    /// <exception cref="CommandUsageException">The selectors identify nothing and name no cloud address.</exception>
    public static void RequireAddressable(IServiceProvider services, ICommandInput input, DeploymentAgentTypeResult resolved)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (NamesACloudDeployment(input) && FeatureGate.IsEnabled(services, FeatureName.AzureBotServiceFeatures))
        {
            return;
        }

        RequireIdentified(
            resolved,
            SuppliedSelectors(input, "--deployment-id", "--deployment-name", "--agent-id", "--agent-name"),
            "--deployment-id or --deployment-name",
            "msagent deployment list");
    }

    /// <summary>
    /// Refuses a Copilot Studio verb whose selectors resolved to a different agent type.
    /// </summary>
    /// <param name="services">The resolved service provider.</param>
    /// <param name="input">The parsed command line.</param>
    /// <param name="verb">The verb named in the usage message.</param>
    /// <exception cref="CommandUsageException">The selectors resolved a non-MCS deployment.</exception>
    public static void RequireMcs(IServiceProvider services, ICommandInput input, string verb)
    {
        var resolved = Resolve(services, Query(input, hasMcsOptions: true));

        if (!resolved.IsMcs)
        {
            throw new CommandUsageException($"`{verb}` applies to an MCS deployment, and this command resolved a {resolved.AgentType} deployment.");
        }
    }
}
