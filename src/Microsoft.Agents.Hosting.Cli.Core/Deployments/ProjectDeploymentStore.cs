using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>Project-scoped persistence for CodeAgent deployment records.</summary>
public interface IDeploymentStore
{
    /// <summary>Lists the project's CodeAgent deployments.</summary>
    /// <param name="project">The project directory, or <see langword="null"/> for the current one.</param>
    IReadOnlyList<CodeAgentDeployment> List(string? project);

    /// <summary>Adds or replaces one deployment.</summary>
    /// <param name="project">The project directory, or <see langword="null"/> for the current one.</param>
    /// <param name="deployment">The record to persist.</param>
    void Upsert(string? project, CodeAgentDeployment deployment);

    /// <summary>Removes one deployment record, ignoring an absent id.</summary>
    /// <param name="project">The project directory, or <see langword="null"/> for the current one.</param>
    /// <param name="deploymentId">The record to remove.</param>
    void Remove(string? project, string deploymentId);
}

internal static class DeploymentStoreExtensions
{
    internal static CodeAgentDeployment LoadDeployment(
        this IDeploymentStore store,
        DeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);

        var deploymentId = request.DeploymentId
            ?? DeterministicDeploymentId.Compute(request.AgentId!, request.DeploymentName!).ToString();
        return store.List(request.Project).FirstOrDefault(candidate =>
            string.Equals(candidate.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase))
            ?? throw Errors.DeploymentException.NotFound(deploymentId);
    }
}

/// <summary>Stores CodeAgent deployment records in the project's <c>agent.config.json</c>.</summary>
public sealed class ProjectDeploymentStore : IDeploymentStore
{
    private readonly IAgentDeploymentRegistry _registry;
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the store.</summary>
    /// <param name="registry">The shared deployment registry.</param>
    /// <param name="locator">Resolves the project directory.</param>
    public ProjectDeploymentStore(IAgentDeploymentRegistry registry, AgentConfigLocator locator)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeAgentDeployment> List(string? project) => [.. Stored(project).Select(ToDeployment)];

    /// <inheritdoc />
    public void Upsert(string? project, CodeAgentDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var directory = _locator.ResolveProjectDirectory(project);
        var existing = _registry.List(directory).FirstOrDefault(candidate => string.Equals(candidate.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _registry.Add(directory, ToAgentDeploymentRecord(deployment, string.Empty));
        }
        else
        {
            _registry.Update(directory, Preserve(ToAgentDeploymentRecord(deployment, existing.EntityTag), existing));
        }
    }

    private static AgentDeploymentRecord Preserve(AgentDeploymentRecord projected, AgentDeploymentRecord existing) => projected with
    {
        CreatedTime = existing.CreatedTime,
        CreatedBy = existing.CreatedBy,
        LastDeployedBy = existing.LastDeployedBy,
        LastDeployedTime = existing.LastDeployedTime,
        IsQuarantined = existing.IsQuarantined,
        QuarantinedReason = existing.QuarantinedReason,
        IsEnabled = existing.IsEnabled,
    };

    /// <inheritdoc />
    public void Remove(string? project, string deploymentId)
    {
        var directory = _locator.ResolveProjectDirectory(project);
        var existing = _registry.List(directory).FirstOrDefault(candidate => string.Equals(candidate.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _registry.Remove(directory, existing.DeploymentId, existing.EntityTag);
        }
    }

    private IEnumerable<AgentDeploymentRecord> Stored(string? project) =>
        _registry.List(_locator.ResolveProjectDirectory(project)).Where(record => string.Equals(record.AgentType, AgentTypes.CodeAgent, StringComparison.Ordinal));

    /// <summary>Projects a stored row onto the service's working record.</summary>
    /// <param name="record">The stored row.</param>
    public static CodeAgentDeployment ToDeployment(AgentDeploymentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new CodeAgentDeployment
        {
            DeploymentId = record.DeploymentId,
            AgentId = record.AgentId,
            DeploymentName = record.DeploymentName,
            DeploymentType = ParseStage(record.DeploymentType),
            IsDefault = record.IsDefault == true,
            Bot = new DeploymentBotResource
            {
                Name = record.DeploymentName,
                EntraId = record.EntraAppId ?? string.Empty,
                EntraIdType = record.EntraAppIdType ?? string.Empty,
                ResourceId = record.BotResourceId ?? string.Empty,
                TenantId = record.TenantId,
                Sku = record.Sku ?? string.Empty,
                Location = record.Location ?? string.Empty,
                DisplayName = record.DisplayName ?? record.DeploymentName,
                Description = record.Description,
                Endpoint = record.Endpoint,
            },
            OAuthConnections = (record.OAuthConnections ?? [])
                .Select(connection => DeploymentOAuthParameters.Sanitize(
                    connection,
                    provider: null,
                    includeArmResponse: false,
                    omitSecrets: true))
                .ToArray(),
            Channels = (record.Channels ?? [])
                .Select(channel => DeploymentChannelCatalog.Sanitize(
                    channel,
                    includeArmResponse: false,
                    omitSecrets: true))
                .OfType<DeploymentChannelRecord>()
                .ToArray(),
        };
    }

    /// <summary>Projects the service's working record onto a stored row.</summary>
    /// <param name="deployment">The working record.</param>
    /// <param name="entityTag">The concurrency token, empty for a new row.</param>
    public static AgentDeploymentRecord ToAgentDeploymentRecord(CodeAgentDeployment deployment, string entityTag)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        return new AgentDeploymentRecord
        {
            DeploymentId = deployment.DeploymentId,
            AgentId = deployment.AgentId,
            AgentType = AgentTypes.CodeAgent,
            DeploymentType = Stage(deployment.DeploymentType),
            DeploymentName = deployment.DeploymentName,
            IsDefault = deployment.IsDefault ? true : null,
            EntraAppId = deployment.Bot.EntraId,
            EntraAppIdType = deployment.Bot.EntraIdType,
            BotResourceId = deployment.Bot.ResourceId,
            TenantId = deployment.Bot.TenantId,
            Sku = deployment.Bot.Sku,
            Location = deployment.Bot.Location,
            DisplayName = deployment.Bot.DisplayName,
            Description = deployment.Bot.Description,
            Endpoint = deployment.Bot.Endpoint,
            OAuthConnections = deployment.OAuthConnections is { Count: > 0 } connections
                ? connections
                    .Select(connection => DeploymentOAuthParameters.Sanitize(
                        connection,
                        provider: null,
                        includeArmResponse: false,
                        omitSecrets: true))
                    .ToArray()
                : null,
            Channels = deployment.Channels is { Count: > 0 } channels
                ? channels
                    .Select(channel => DeploymentChannelCatalog.Sanitize(
                        channel,
                        includeArmResponse: false,
                        omitSecrets: true))
                    .OfType<DeploymentChannelRecord>()
                    .ToArray()
                : null,
            EntityTag = entityTag,
        };
    }

    /// <summary>The stored text for a deployment stage.</summary>
    /// <param name="type">The stage.</param>
    public static string Stage(DeploymentType type) => type switch
    {
        DeploymentType.Test => DeploymentStages.Test,
        DeploymentType.Prod => DeploymentStages.Prod,
        _ => DeploymentStages.Dev,
    };

    /// <summary>The stage a stored text names, defaulting to <see cref="DeploymentType.Dev"/>.</summary>
    /// <param name="stage">The stored text.</param>
    public static DeploymentType ParseStage(string? stage) => string.Equals(stage, DeploymentStages.Prod, StringComparison.OrdinalIgnoreCase)
        ? DeploymentType.Prod
        : string.Equals(stage, DeploymentStages.Test, StringComparison.OrdinalIgnoreCase) ? DeploymentType.Test : DeploymentType.Dev;
}
