using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary>The addressing options every sharing verb accepts, and the rules they share.</summary>
public static class AgentShareSelector
{
    /// <summary>The selectors that address the agent being shared.</summary>
    public static IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory to act on. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Agent to act on. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Agent to act on, by name."),
    ];

    /// <summary>The selectors that address the agent, plus the user a grant or revoke names.</summary>
    public static IReadOnlyList<CommandSymbol> SymbolsWithPrincipal =>
    [
        .. Symbols,
        OptionCatalog.Value("--user", "User to grant access to, by sign-in address, email address, or Entra object id."),
    ];

    /// <summary>Reports the agent-selector rule the input breaks, or null when it is valid.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <returns>The failure text, or <see langword="null"/>.</returns>
    public static string? ValidateAgent(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return AgentShareValidation.AgentSelector(input.GetString("--agent-id"), input.GetString("--agent-name"))?.Message;
    }
}

/// <summary><c>msagent agent share</c>.</summary>
[Command("share", Description = "Share an MCS agent with a user.", ParentGroup = typeof(AgentGroup), SortPriority = 80, ProgressLabel = "Sharing agent")]
public sealed class AgentShareCommand : CommandBase<ManagedAgentShareRequest, ManagedAgentShareResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. AgentShareSelector.SymbolsWithPrincipal,
        OptionCatalog.Value("--role", $"The access to grant: {string.Join(" or ", AgentShareRoles.Accepted)}. Required with --user."),
        OptionCatalog.OptionalSwitch("--share-with-org", "Organization-wide access. Omit to leave unchanged, or pass false to turn it off."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Agent, "--user", "--role") { Summary = "Grant a user access" },
        new(UsageRoute.Agent, "--share-with-org") { Summary = "Change organization-wide access" },
    ];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return AgentShareSelector.ValidateAgent(input)
            ?? AgentShareValidation.ShareGrant(input.GetString("--user"), input.GetString("--role"), input.GetOptionalSwitch("--share-with-org"))?.Message;
    }

    /// <inheritdoc />
    protected override ManagedAgentShareRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentShareRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            User = input.GetString("--user"),
            Role = input.GetString("--role"),
            ShareWithOrg = input.GetOptionalSwitch("--share-with-org"),
        };
    }
}

/// <summary><c>msagent agent share show</c>.</summary>
[Command("show", Description = "Show who has access to an MCS agent, and whether everyone in the organization can use it. MCS only.", ParentCommand = typeof(AgentShareCommand), SortPriority = 10)]
public sealed class AgentShareShowCommand : CommandBase<ManagedAgentShareShowRequest, ManagedAgentShareShowResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => AgentShareSelector.Symbols;

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [new(UsageRoute.Agent)];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => AgentShareSelector.ValidateAgent(input);

    /// <inheritdoc />
    protected override ManagedAgentShareShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentShareShowRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
        };
    }
}

/// <summary><c>msagent agent unshare</c>.</summary>
[Command("unshare", Description = "Remove a user's access to an MCS agent. MCS only.", ParentGroup = typeof(AgentGroup), SortPriority = 90, ProgressLabel = "Unsharing agent")]
public sealed class AgentUnshareCommand : CommandBase<ManagedAgentUnshareRequest, ManagedAgentUnshareResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => AgentShareSelector.SymbolsWithPrincipal;

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [new(UsageRoute.Agent, "--user")];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return AgentShareSelector.ValidateAgent(input) ?? AgentShareValidation.UnshareTarget(input.GetString("--user"))?.Message;
    }

    /// <inheritdoc />
    protected override ManagedAgentUnshareRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentUnshareRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            User = input.GetString("--user"),
        };
    }
}
