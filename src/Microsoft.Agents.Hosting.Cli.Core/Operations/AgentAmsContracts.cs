using System.Globalization;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Names an AMS agent by id or by name in its home environment.</summary>
public abstract record AgentAmsRequest
{
    /// <summary>The agent id, which wins over the name.</summary>
    public string? AgentId { get; init; }
    public string? AgentName { get; init; }

    /// <summary>The agent's home environment, or null for the active environment.</summary>
    public string? EnvironmentId { get; init; }
}

/// <summary>Lists the code agents of an AMS home environment.</summary>
public sealed record AgentAmsListRequest
{
    /// <summary>The home environment, or null for the active environment.</summary>
    public string? EnvironmentId { get; init; }
}

/// <summary>Reads an AMS agent with its deployments and kept bots.</summary>
public sealed record AgentAmsShowRequest : AgentAmsRequest;

/// <summary>Renames an AMS agent or changes its description.</summary>
public sealed record AgentAmsUpdateRequest : AgentAmsRequest
{
    public string? NewName { get; init; }
    public string? Description { get; init; }
}

/// <summary>Deletes an AMS agent with every deployment record and bot it owns, after confirmation.</summary>
public sealed record AgentAmsDeleteRequest : AgentAmsRequest
{
    public bool NonInteractive { get; init; }
}

/// <summary>Lists the package uploads of an AMS agent with the deployments that run each.</summary>
public sealed record AgentCommitListRequest : AgentAmsRequest;

/// <summary>A bot that AMS keeps from the deleted record of a live deployment until the agent is deleted.</summary>
public sealed record AgentAmsKeptBot
{
    public required string DeploymentId { get; init; }
    public string? DeploymentName { get; init; }
    public string? EnvironmentId { get; init; }
    public string? DeploymentType { get; init; }
    public string? EntraAppId { get; init; }
    public string? BotName { get; init; }
    public DateTimeOffset? DeletedTime { get; init; }

    /// <summary>Renders kept bots as a table, or nothing when there are none.</summary>
    public static IReadOnlyList<string> Table(IReadOnlyList<AgentAmsKeptBot> keptBots) => keptBots.Count == 0 ? [] : TextGrid.RenderTable("Bots kept from deleted deployment records", ["DEPLOYMENT", "ID", "ENVIRONMENT", "BOT", "DELETED"], [.. keptBots.Select(bot => new[] { TextGrid.DescribeOrDash(bot.DeploymentName), bot.DeploymentId, TextGrid.DescribeOrDash(bot.EnvironmentId), TextGrid.DescribeOrDash(bot.BotName), TextGrid.DescribeTimestamp(bot.DeletedTime) })]);
}

/// <summary>A deployment, or a kept bot, that an agent delete removed.</summary>
public sealed record AgentAmsRemovedDeployment
{
    public required string DeploymentId { get; init; }
    public string? DeploymentName { get; init; }
    public string? EnvironmentId { get; init; }
    public string? DeploymentType { get; init; }

    /// <summary>The deployment's bot, or null when it never got one.</summary>
    public string? BotName { get; init; }
    public bool BotDeleted { get; init; }
}

/// <summary>An AMS agent as the agent commands show it.</summary>
public sealed record AgentAmsRecord
{
    public required string AgentId { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string AgentType { get; init; }
    public string? State { get; init; }
    public bool IsDeleting { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public string? Language { get; init; }
    public string? LanguageVersion { get; init; }
    public DateTimeOffset? CreatedTime { get; init; }
    public DateTimeOffset? ChangedTime { get; init; }

    /// <summary>Returns the lines that describe the agent.</summary>
    public IReadOnlyList<string> DetailLines() =>
    [
        $"  agent: {TextGrid.DescribeOrDash(DisplayName)} ({AgentId})",
        $"  type: {AgentType}; language: {TextGrid.DescribeOrDash(Language)} {LanguageVersion}".TrimEnd(),
        .. Description is null ? Array.Empty<string>() : [$"  description: {Description}"],
        $"  home environment: {EnvironmentId} (tenant {TenantId})",
        $"  state: {TextGrid.DescribeOrDash(State)}{(IsDeleting ? ", being deleted" : string.Empty)}",
        $"  changed: {TextGrid.DescribeTimestamp(ChangedTime)}",
    ];
}

/// <summary>The code agents of an AMS home environment.</summary>
public sealed record AgentAmsListResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string EnvironmentId { get; init; }
    public required IReadOnlyList<AgentAmsRecord> Agents { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => Agents.Count == 0
        ? [$"Environment {EnvironmentId} has no code agents."]
        : TextGrid.RenderTable($"Code agents of environment {EnvironmentId}", ["NAME", "ID", "STATE", "CHANGED"], [.. Agents.Select(agent => new[] { TextGrid.DescribeOrDash(agent.DisplayName), agent.AgentId, TextGrid.DescribeOrDash(agent.State), TextGrid.DescribeTimestamp(agent.ChangedTime) })]);
}

/// <summary>An AMS agent with a summary of its deployments and the bots kept from deleted deployment records.</summary>
public sealed record AgentAmsShowResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required AgentAmsRecord Agent { get; init; }

    /// <summary>Whether AMS listed the agent's deployments; false when the list is not enabled.</summary>
    public required bool DeploymentsAvailable { get; init; }
    public required IReadOnlyList<AgentDeploymentAmsRecord> Deployments { get; init; }
    public required IReadOnlyList<AgentAmsKeptBot> KeptBots { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Agent {TextGrid.DescribeOrDash(Agent.DisplayName)} ({Agent.AgentId})",
        .. Agent.DetailLines(),
        .. !DeploymentsAvailable ? ["Deployments: unavailable; AMS does not list deployments in this environment yet."]
            : Deployments.Count == 0 ? ["Deployments: none."]
            : AgentDeploymentAmsRecord.Table("Deployments", Deployments),
        .. AgentAmsKeptBot.Table(KeptBots),
    ];
}

/// <summary>An AMS agent after its name or description changed.</summary>
public sealed record AgentAmsUpdateResult : ITextGridRenderable
{
    /// <summary>The status of an agent that AMS changed.</summary>
    public const string UpdatedStatus = "agent-updated";

    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required AgentAmsRecord Agent { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => [$"Agent {TextGrid.DescribeOrDash(Agent.DisplayName)} ({Agent.AgentId}) updated.", .. Agent.DetailLines()];
}

/// <summary>What an AMS agent delete removed, or why nothing was deleted.</summary>
public sealed record AgentAmsDeleteResult : ITextGridRenderable
{
    /// <summary>The status of an agent that AMS deleted.</summary>
    public const string DeletedStatus = "agent-deleted";

    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required string AgentId { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<AgentAmsRemovedDeployment> Deployments { get; init; } = [];
    public IReadOnlyList<AgentAmsRemovedDeployment> KeptBots { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => Status != DeletedStatus
        ? AgentAmsConfirmation.StoppedLines(Status, DisplayName ?? AgentId)
        :
        [
            $"Agent {TextGrid.DescribeOrDash(DisplayName)} ({AgentId}) deleted.",
            "Removed:",
            .. Deployments.Select(deployment => $"  deployment {TextGrid.DescribeOrDash(deployment.DeploymentName)} ({deployment.DeploymentId}, {TextGrid.DescribeOrDash(deployment.DeploymentType)}) in {TextGrid.DescribeOrDash(deployment.EnvironmentId)}{BotText(deployment)}"),
            .. KeptBots.Select(bot => $"  kept bot of deleted deployment {TextGrid.DescribeOrDash(bot.DeploymentName)} ({bot.DeploymentId}){BotText(bot)}"),
            "  the agent record",
        ];

    private static string BotText(AgentAmsRemovedDeployment deployment) => deployment.BotName is null ? ", no bot" : deployment.BotDeleted ? $", bot {deployment.BotName} deleted" : $", bot {deployment.BotName} already gone";
}

/// <summary>One package upload of an AMS agent with the deployments that run it.</summary>
public sealed record AgentCommitRecord
{
    /// <summary>The commit of the upload, or null for an upload made before AMS recorded commits.</summary>
    public string? CommitId { get; init; }
    public required string PackageRevisionId { get; init; }
    public DateTimeOffset? CreatedTime { get; init; }
    public long? SizeInBytes { get; init; }
    public string? Sha256 { get; init; }

    /// <summary>The names of the deployments that run the upload.</summary>
    public required IReadOnlyList<string> Deployments { get; init; }
}

/// <summary>The package uploads of an AMS agent, newest first.</summary>
public sealed record AgentCommitListResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string AgentId { get; init; }

    /// <summary>Whether AMS listed the agent's deployments; false when the list is not enabled.</summary>
    public required bool DeploymentsAvailable { get; init; }
    public required IReadOnlyList<AgentCommitRecord> Commits { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        .. Commits.Count == 0
            ? [$"Agent {AgentId} has no uploads."]
            : TextGrid.RenderTable($"Commits of agent {AgentId}, newest first", ["COMMIT", "UPLOADED", "SIZE", "DEPLOYMENTS"], [.. Commits.Select(commit => new[] { TextGrid.DescribeOrDash(commit.CommitId), TextGrid.DescribeTimestamp(commit.CreatedTime), commit.SizeInBytes?.ToString(CultureInfo.InvariantCulture) ?? TextGrid.Dash, DeploymentsText(commit) })]),
        .. DeploymentsAvailable ? Array.Empty<string>() : ["Deployments: unavailable; AMS does not list deployments in this environment yet."],
    ];

    private string DeploymentsText(AgentCommitRecord commit) => !DeploymentsAvailable ? TextGrid.Dash : commit.Deployments.Count == 0 ? TextGrid.Dash : string.Join(", ", commit.Deployments);
}

/// <summary>The statuses and text that the AMS deletes asking for confirmation share.</summary>
public static class AgentAmsConfirmation
{
    /// <summary>The status of a delete the operator declined.</summary>
    public const string CancelledStatus = "cancelled";

    /// <summary>The status of a delete whose confirmation did not match the name.</summary>
    public const string NameMismatchStatus = "name-mismatch";

    /// <summary>Returns the text of a delete that stopped before changing anything.</summary>
    public static IReadOnlyList<string> StoppedLines(string status, string name) => status == NameMismatchStatus ? [$"The name typed did not match '{name}'; nothing was deleted."] : ["Delete cancelled; nothing was deleted."];

    /// <summary>Returns the line that says a live deployment keeps answering after its record is deleted.</summary>
    public static string StillRunningLine(AgentDeploymentAmsRecord deployment) => $"Still running: the deployment keeps answering in environment {deployment.TargetEnvironmentId}{(deployment.BotService is null ? string.Empty : $" through its Azure Bot Service instance {deployment.BotService.Name}")}.";

    /// <summary>Returns what keeps running after the record of a live deployment is deleted.</summary>
    public static IReadOnlyList<string> LiveDeploymentKeptLines(AgentDeploymentAmsRecord deployment) =>
    [
        StillRunningLine(deployment),
        "Until `msagent agent delete` removes it, its channels and OAuth connections cannot be changed, AMS issues no new Direct Line tokens for it, and this agent cannot get another deployment in that environment. `msagent agent show` lists the kept bot.",
    ];
}
