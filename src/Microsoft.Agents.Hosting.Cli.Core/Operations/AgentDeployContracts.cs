using System.Globalization;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Names an AMS deployment by id or by name under its agent.</summary>
public abstract record AgentDeploymentAmsRequest : AgentAmsRequest
{
    /// <summary>The deployment id, which wins over the name.</summary>
    public string? DeploymentId { get; init; }
    public string? DeploymentName { get; init; }
}

/// <summary>Puts an existing AMS deployment live as its record says.</summary>
public sealed record AgentDeployRequest : AgentDeploymentAmsRequest
{
    /// <summary>The upload to deploy to a Dev or Test deployment, or null for its dev endpoint or the newest upload.</summary>
    public string? CommitId { get; init; }
}

/// <summary>Writes a new AMS deployment record without deploying it.</summary>
public sealed record AgentDeploymentAmsCreateRequest : AgentAmsRequest
{
    public const int DeploymentNameMaxLength = 64;

    public string? TargetEnvironmentId { get; init; }
    public string? DeploymentName { get; init; }
    public string? DeploymentType { get; init; }
    public string? EntraId { get; init; }
    public string? EntraIdType { get; init; }

    /// <summary>The dev endpoint of a dev endpoint deployment, which a deploy without a commit puts live.</summary>
    public string? EndpointUrl { get; init; }
}

/// <summary>Lists the deployments of an AMS agent.</summary>
public sealed record AgentDeploymentAmsListRequest : AgentAmsRequest;

/// <summary>Reads an existing AMS deployment.</summary>
public sealed record AgentDeploymentAmsShowRequest : AgentDeploymentAmsRequest;

/// <summary>Renames an AMS deployment record or changes its requested dev endpoint.</summary>
public sealed record AgentDeploymentAmsUpdateRequest : AgentDeploymentAmsRequest
{
    public string? NewName { get; init; }
    public string? EndpointUrl { get; init; }
}

/// <summary>Removes an AMS deployment record, asking first when the deployment is live.</summary>
public sealed record AgentDeploymentAmsDeleteRequest : AgentDeploymentAmsRequest
{
    public bool NonInteractive { get; init; }
}

/// <summary>The Azure Bot Service instance AMS bound to a deployment, exactly as AMS returned it.</summary>
public sealed record AgentDeploymentBotService
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required string MsaAppId { get; init; }
    public string? MsaAppType { get; init; }
    public string? MsaAppTenantId { get; init; }
    public string? Location { get; init; }
    public string? Sku { get; init; }
    public string? Endpoint { get; init; }
}

/// <summary>An AMS deployment record as the deployment and agent commands show it.</summary>
public sealed record AgentDeploymentAmsRecord
{
    /// <summary>The kind of a deployment that runs uploads of the agent's package.</summary>
    public const string PackageKind = "package";

    /// <summary>The kind of a deployment that a dev endpoint serves.</summary>
    public const string DevEndpointKind = "dev-endpoint";

    public required string DeploymentId { get; init; }
    public required string DeploymentName { get; init; }
    public required string AgentId { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public required string DeploymentType { get; init; }
    public required string EntraId { get; init; }
    public required string EntraIdType { get; init; }

    /// <summary>NotDeployed, Deploying, Deployed or Deleting.</summary>
    public required string State { get; init; }

    /// <summary>The deployment kind, <see cref="PackageKind"/> or <see cref="DevEndpointKind"/>.</summary>
    public required string Kind { get; init; }
    public string? PackageRevisionId { get; init; }
    public string? CommitId { get; init; }
    public string? PackageDigest { get; init; }
    public long? PackageSizeInBytes { get; init; }

    /// <summary>The deployed messaging endpoint, or null before the first deploy.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The dev endpoint the next deploy without a commit puts live, switching a package deployment to it.</summary>
    public string? RequestedEndpoint { get; init; }

    /// <summary>Whether the requested dev endpoint differs from the deployed one, so the next deploy without a commit puts it live.</summary>
    public bool EndpointChangePending { get; init; }
    public string? ManagedIdentityId { get; init; }
    public AgentDeploymentBotService? BotService { get; init; }

    /// <summary>Renders deployments as a table.</summary>
    public static IReadOnlyList<string> Table(string title, IReadOnlyList<AgentDeploymentAmsRecord> deployments) => TextGrid.RenderTable(title, ["NAME", "ID", "STAGE", "KIND", "STATE", "BOT", "PENDING ENDPOINT"], [.. deployments.Select(deployment => deployment.Row())]);

    /// <summary>Returns the lines that describe the record.</summary>
    public IReadOnlyList<string> DetailLines() =>
    [
        $"  agent: {AgentId}",
        $"  home environment: {HomeEnvironmentId} (tenant {TenantId})",
        $"  target environment: {TargetEnvironmentId}",
        $"  stage: {DeploymentType}; kind: {KindLabel}; state: {StateLabel}",
        $"  Entra identity: {EntraId} ({EntraIdType})",
        .. PackageLines(),
        $"  endpoint: {Endpoint ?? "none deployed yet"}",
        .. EndpointChangePending ? [$"  requested endpoint: {RequestedEndpoint}, not deployed yet; {PendingEndpointStep}."] : Array.Empty<string>(),
        BotService is null ? "  Azure Bot Service: none" : $"  Azure Bot Service: {BotService.Name}, app {BotService.MsaAppId}",
        .. ManagedIdentityId is null ? Array.Empty<string>() : [$"  managed identity: {ManagedIdentityId}"],
    ];

    internal string PendingEndpointStep => Kind == PackageKind ? "the next `msagent agent deploy` without --commit-id switches the deployment from its package to this dev endpoint" : "the next `msagent agent deploy` without --commit-id puts it live";

    private string KindLabel => Kind == DevEndpointKind ? "dev endpoint" : "package";

    private string StateLabel => State switch
    {
        "NotDeployed" => "not deployed",
        "Deploying" => "deploying",
        "Deployed" => "deployed",
        "Deleting" => "being deleted",
        _ => State,
    };

    private string[] Row() => [DeploymentName, DeploymentId, DeploymentType, KindLabel, StateLabel, TextGrid.DescribeOrDash(BotService?.Name), EndpointChangePending ? RequestedEndpoint! : TextGrid.Dash];

    private string[] PackageLines() =>
        Kind == DevEndpointKind ? []
        : PackageRevisionId is null ? ["  package: none deployed yet"]
        : [$"  package: commit {TextGrid.DescribeOrDash(CommitId)}, Package Revision {PackageRevisionId}, {PackageSizeInBytes?.ToString(CultureInfo.InvariantCulture) ?? TextGrid.Dash} bytes, sha256 {TextGrid.DescribeOrDash(PackageDigest)}"];
}

/// <summary>An AMS deployment record that was created, read or changed.</summary>
public sealed record AgentDeploymentAmsResult : ITextGridRenderable
{
    /// <summary>The status of a record that was written.</summary>
    public const string CreatedStatus = "agent-deployment-created";

    /// <summary>The status of a record that was only read.</summary>
    public const string ReadStatus = "agent-deployment-read";

    /// <summary>The status of a record that was changed.</summary>
    public const string UpdatedStatus = "agent-deployment-updated";

    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required AgentDeploymentAmsRecord Deployment { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        Status switch
        {
            CreatedStatus => $"Deployment {Deployment.DeploymentName} ({Deployment.DeploymentId}) created. It is a record only: nothing runs until it is deployed.",
            UpdatedStatus => $"Deployment {Deployment.DeploymentName} ({Deployment.DeploymentId}) updated. Only its record changed.",
            _ => $"Deployment {Deployment.DeploymentName} ({Deployment.DeploymentId})",
        },
        .. Deployment.DetailLines(),
    ];
}

/// <summary>The deployments of an AMS agent and the bots kept from deleted records of live deployments.</summary>
public sealed record AgentDeploymentAmsListResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string AgentId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required IReadOnlyList<AgentDeploymentAmsRecord> Deployments { get; init; }
    public required IReadOnlyList<AgentAmsKeptBot> KeptBots { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        .. Deployments.Count == 0 ? [$"Agent {AgentId} has no deployments."] : AgentDeploymentAmsRecord.Table($"Deployments of agent {AgentId}", Deployments),
        .. AgentAmsKeptBot.Table(KeptBots),
    ];
}

/// <summary>The removal of an AMS deployment record, or why nothing was removed.</summary>
public sealed record AgentDeploymentAmsDeleteResult : ITextGridRenderable
{
    /// <summary>The status of a deployment record that AMS removed.</summary>
    public const string DeletedStatus = "agent-deployment-deleted";

    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required AgentDeploymentAmsRecord Deployment { get; init; }

    /// <summary>Whether the deployment was live, so it keeps running after its record is removed.</summary>
    public bool WasLive { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => Status == DeletedStatus
        ? [$"Deployment record {Deployment.DeploymentName} ({Deployment.DeploymentId}) deleted.", .. WasLive ? [AgentAmsConfirmation.StillRunningLine(Deployment)] : Array.Empty<string>()]
        : AgentAmsConfirmation.StoppedLines(Status, Deployment.DeploymentName);
}

/// <summary>An AMS deployment that was put live, with what went live and what happened to its bot.</summary>
public sealed record AgentDeployResult : ITextGridRenderable
{
    /// <summary>The status of a deployment that AMS deployed.</summary>
    public const string DeployedStatus = "agent-deployment-deployed";

    /// <summary>The bot value when this deploy created the deployment's bot.</summary>
    public const string BotCreated = "created";

    /// <summary>The bot value when the deployment kept the bot it had.</summary>
    public const string BotKept = "kept";

    /// <summary>The bot value when AMS bound no bot to the deployment.</summary>
    public const string BotNone = "none";

    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required AgentDeploymentAmsRecord Deployment { get; init; }

    /// <summary>What happened to the deployment's bot: <see cref="BotCreated"/>, <see cref="BotKept"/> or <see cref="BotNone"/>.</summary>
    public required string Bot { get; init; }

    /// <summary>The endpoint deployed before this deploy, when this deploy changed it.</summary>
    public string? PreviousEndpoint { get; init; }

    /// <summary>Whether this deploy switched the deployment between a package and a dev endpoint.</summary>
    public bool KindSwitched { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Deployment {Deployment.DeploymentName} ({Deployment.DeploymentId}) deployed.",
        .. Deployment.DetailLines(),
        .. KindSwitched ? [Deployment.Kind == AgentDeploymentAmsRecord.DevEndpointKind ? $"Switched from the package to the dev endpoint {Deployment.Endpoint}." : $"Switched from the dev endpoint to commit {TextGrid.DescribeOrDash(Deployment.CommitId)}; {(Deployment.BotService is null ? "the deployment now answers" : "the bot now points")} at {Deployment.Endpoint}."] : Array.Empty<string>(),
        Bot switch
        {
            BotCreated => $"Azure Bot Service: created {Deployment.BotService?.Name}.",
            BotKept => $"Azure Bot Service: kept {Deployment.BotService?.Name}.",
            _ => "Azure Bot Service: none; AMS bound no bot to this deployment.",
        },
    ];
}
