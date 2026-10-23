using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent create</c>, routed on <c>--agent-type</c>.</summary>
[Command("create", Description = "Create an agent project.", ParentGroup = typeof(AgentGroup), SortPriority = 10, ProgressLabel = "Creating agent")]
public sealed class AgentCreateCommand : CommandBase<ManagedAgentCreateRequest, ManagedAgentCreateResult>
{
    private static readonly string[] McsOnlyOptions =
    [
        "--schema-name",
        "--mcs-agent-type",
        "--init-template",
        "--initial-prompt",
        "--mcs-clone-agent-schemaname",
        "--mcs-clone-agent-id",
        "--mcs-clone-workflow-id",
        "--mcs-clone-environment-id",
        "--project",
    ];

    private static readonly string[] McsAgentOnlyOptions =
    [
        "--schema-name",
        "--mcs-agent-type",
        "--init-template",
        "--initial-prompt",
        "--mcs-clone-agent-schemaname",
        "--mcs-clone-agent-id",
    ];

    private static readonly string[] WorkflowOnlyOptions = ["--mcs-clone-workflow-id"];

    private static readonly string[] CodeAgentOnlyOptions = ["--language", "--language-version", "--idempotency-key"];

    private static readonly string[] McsAgentCloneOptions = ["--mcs-clone-agent-id", "--mcs-clone-agent-schemaname", "--mcs-clone-environment-id"];

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--agent-name", "Agent or workflow display name. Optional when cloning, which inherits the source name.") with { ExtraAliases = ["--name", "-n"] },
        OptionCatalog.Value("--description", "Agent description. Required for a new MCS agent."),
        OptionCatalog.Value("--language", "Runtime language: dotnet, python, or node. CodeAgent only.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--language-version", "Runtime language version. CodeAgent only.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--idempotency-key", "Create-retry key. Generated when omitted. CodeAgent only.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--environment-id", "Target environment id. Defaults to the active environment."),
        OptionCatalog.Value("--project", "Project directory to initialize. Defaults to the current directory, created if missing."),
        OptionCatalog.Value("--agent-type", "Agent type. Use MCSAgent for an MCS project, or MCSWorkflow for a standalone cloud flow."),
        OptionCatalog.Value("--schema-name", "MCS schema name. Derived from --agent-name when omitted. MCS only."),
        OptionCatalog.Value("--mcs-agent-type", "Scaffold shape: CLI (default) or Classic. MCS only."),
        OptionCatalog.Value("--init-template", "Scaffold template name. Requires --mcs-agent-type Classic. MCS only."),
        OptionCatalog.Value("--initial-prompt", "Instructions scaffolded into the project. Required for a new MCS agent. MCS only."),
        OptionCatalog.Value("--mcs-clone-agent-schemaname", "Clone this schema name instead of scaffolding. MCS only."),
        OptionCatalog.Value("--mcs-clone-agent-id", "Clone this agent id instead of scaffolding. MCS only."),
        OptionCatalog.Value("--mcs-clone-workflow-id", "Clone this workflow id instead of scaffolding. MCSWorkflow only."),
        OptionCatalog.Value("--mcs-clone-environment-id", "Environment to clone the MCS agent or workflow from. Defaults to --environment-id, then the active one."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(ManagedAgentCreateRequest), typeof(ManagedAgentCreateResult)),
        new RouteMetadata(typeof(AgentCreateRequest), typeof(AgentCreateResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new("--agent-type MCSAgent", "--agent-name", "--description", "--initial-prompt") { Summary = "Scaffold a new MCS agent" },
        new("[--agent-type MCSAgent]", "(--mcs-clone-agent-id <id> | --mcs-clone-agent-schemaname <schema-name>)") { Summary = "Clone an existing MCS agent" },
        new("--agent-type MCSWorkflow", "--agent-name") { Summary = "Scaffold a new standalone workflow" },
        new("--agent-type MCSWorkflow", "--mcs-clone-workflow-id <id>") { Summary = "Clone an existing standalone workflow" },
        new("--agent-type CodeAgent", "--agent-name", "--language", "--language-version")
        {
            Summary = "Create a CodeAgent",
            RequiredFeatures = [FeatureName.AmsFeatures],
        },
    ];

    /// <inheritdoc />
    protected override void EnsureAvailable(ICommandInput input, IServiceProvider services)
    {
        var requestedAgentType = RequestedAgentType(input);
        if (requestedAgentType is not null && !AgentTypes.IsMcs(requestedAgentType))
        {
            FeatureGate.RequireEnabled(
                services,
                FeatureName.AmsFeatures,
                "CodeAgent creation",
                "Use --agent-type MCSAgent for the available MCS project route.");
        }
    }

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (RequestedAgentType(input) is not { } requestedAgentType)
        {
            return $"--agent-type '{input.GetString("--agent-type")}' is not a known agent type. Use {string.Join(" or ", AgentTypes.All)}.";
        }

        if (!CopilotAuthoringModes.TryParse(input.GetString("--mcs-agent-type"), out _))
        {
            return CopilotAuthoringModes.UnknownAgentType(input.GetString("--mcs-agent-type"));
        }

        if (AgentTypes.IsMcsWorkflow(requestedAgentType))
        {
            return FirstSupplied(input, CodeAgentOnlyOptions) is { } workflowCodeOnly
                ? $"{workflowCodeOnly} applies to CodeAgent only, and --agent-type MCSWorkflow was supplied."
                : FirstSupplied(input, McsAgentOnlyOptions) is { } agentOnly
                    ? $"{agentOnly} applies to MCSAgent only, and --agent-type MCSWorkflow was supplied."
                    : null;
        }

        if (AgentTypes.IsMcsAgent(requestedAgentType))
        {
            return FirstSupplied(input, CodeAgentOnlyOptions) is { } codeOnly
                ? $"{codeOnly} applies to CodeAgent only, and --agent-type MCSAgent was supplied."
                : FirstSupplied(input, WorkflowOnlyOptions) is { } workflowOnly
                    ? $"{workflowOnly} applies to MCSWorkflow only. Add --agent-type MCSWorkflow."
                    : null;
        }

        if (FirstSupplied(input, McsOnlyOptions) is { } mcsOnly)
        {
            return $"{mcsOnly} applies to MCS agents only. Add --agent-type MCSAgent.";
        }

        if (string.IsNullOrWhiteSpace(input.GetString("--agent-name")))
        {
            return "--agent-name is required for a CodeAgent.";
        }

        return string.IsNullOrWhiteSpace(input.GetString("--language")) || string.IsNullOrWhiteSpace(input.GetString("--language-version"))
            ? "--language and --language-version are required for a CodeAgent."
            : null;
    }

    /// <inheritdoc />
    protected override ManagedAgentCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentCreateRequest
        {
            Name = input.GetString("--agent-name"),
            Description = input.GetString("--description"),
            SchemaName = input.GetString("--schema-name"),
            McsAgentType = input.GetString("--mcs-agent-type"),
            InitTemplate = input.GetString("--init-template"),
            InitialPrompt = input.GetString("--initial-prompt"),
            CloneSchemaName = input.GetString("--mcs-clone-agent-schemaname"),
            CloneAgentId = input.GetString("--mcs-clone-agent-id"),
            CloneWorkflowId = input.GetString("--mcs-clone-workflow-id"),
            CloneEnvironmentId = input.GetString("--mcs-clone-environment-id"),
            EnvironmentId = input.GetString("--environment-id"),
            AgentType = RequestedAgentType(input),
            Project = input.GetString("--project"),
        };
    }

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (IsMcs(input))
        {
            return InvokeAsync<ManagedAgentCreateRequest, ManagedAgentCreateResult>(services, Bind(input), cancellationToken);
        }

        return InvokeAsync<AgentCreateRequest, AgentCreateResult>(
            services,
            new AgentCreateRequest
            {
                DisplayName = input.GetString("--agent-name"),
                Description = input.GetString("--description"),
                EnvironmentId = input.GetString("--environment-id"),
                Language = input.GetString("--language"),
                LanguageVersion = input.GetString("--language-version"),
                IdempotencyKey = input.GetString("--idempotency-key"),
            },
            cancellationToken);
    }

    private static bool IsMcs(ICommandInput input) => RequestedAgentType(input) is { } agentType && AgentTypes.IsMcs(agentType);

    private static string? RequestedAgentType(ICommandInput input) =>
        string.IsNullOrWhiteSpace(input.GetString("--agent-type"))
        && FirstSupplied(input, CodeAgentOnlyOptions) is null
        && FirstSupplied(input, McsAgentCloneOptions) is not null
            ? AgentTypes.McsAgent
            : AgentTypes.Parse(input.GetString("--agent-type"));

    private static string? FirstSupplied(ICommandInput input, IEnumerable<string> options) =>
        options.FirstOrDefault(option => !string.IsNullOrWhiteSpace(input.GetString(option)));
}
