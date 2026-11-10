using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

/// <summary>The agent-type values this build understands. Stored as text, never a wire ordinal.</summary>
public static class AgentTypes
{
    /// <summary>A Microsoft Copilot Studio agent.</summary>
    public const string McsAgent = "MCSAgent";

    /// <summary>A standalone Power Automate cloud flow, deployed without an agent.</summary>
    public const string McsWorkflow = "MCSWorkflow";

    /// <summary>A code agent, out of scope for the MCS command surface.</summary>
    public const string CodeAgent = "CodeAgent";

    /// <summary>Every agent type the schema accepts.</summary>
    public static readonly IReadOnlyList<string> All = [McsAgent, McsWorkflow, CodeAgent];

    /// <summary>Whether a caller-supplied agent type names an MCS agent.</summary>
    /// <param name="agentType">The caller's spelling, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value names an MCS agent.</returns>
    public static bool IsMcsAgent(string? agentType) =>
        agentType?.Trim() is { Length: > 0 } value
        && (value.Equals(McsAgent, StringComparison.OrdinalIgnoreCase) || value.Equals("mcs", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a caller-supplied agent type names a standalone workflow.</summary>
    /// <param name="agentType">The caller's spelling, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value names a workflow.</returns>
    public static bool IsMcsWorkflow(string? agentType) =>
        agentType?.Trim() is { Length: > 0 } value && value.Equals(McsWorkflow, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a caller-supplied agent type names an MCS agent or a workflow.</summary>
    /// <param name="agentType">The caller's spelling, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value names either MCS type.</returns>
    public static bool IsMcs(string? agentType) => IsMcsAgent(agentType) || IsMcsWorkflow(agentType);

    /// <summary>
    /// Resolves a caller-supplied agent type to its canonical spelling.
    ///
    /// An omitted value means <see cref="CodeAgent"/>, the documented default. An unrecognised value is
    /// not a default: silently treating it as a code agent would dispatch an MCS command to the wrong
    /// backend, so it resolves to nothing and the caller reports it.
    /// </summary>
    /// <param name="agentType">The caller's spelling, or <see langword="null"/>.</param>
    /// <returns>The canonical type, or <see langword="null"/> when the value names no known type.</returns>
    public static string? Parse(string? agentType) =>
        agentType?.Trim() is not { Length: > 0 } value ? CodeAgent
        : IsMcsAgent(value) ? McsAgent
        : IsMcsWorkflow(value) ? McsWorkflow
        : value.Equals(CodeAgent, StringComparison.OrdinalIgnoreCase) ? CodeAgent
        : null;
}

/// <summary>The deployment stages a slot may occupy.</summary>
public static class DeploymentStages
{
    /// <summary>The agent's home environment.</summary>
    public const string Dev = "dev";

    /// <summary>A named test environment.</summary>
    public const string Test = "test";

    /// <summary>A named production environment.</summary>
    public const string Prod = "prod";

    /// <summary>Every stage, in promotion order.</summary>
    public static readonly IReadOnlyList<string> All = [Dev, Test, Prod];

    /// <summary>The names the Agent Management Service gives the stages, in promotion order.</summary>
    public static readonly IReadOnlyList<string> ServiceNames = ["Dev", "Test", "Prod"];

    /// <summary>The name the Agent Management Service gives a stage.</summary>
    /// <param name="stage">A canonical stage, one of <see cref="All"/>.</param>
    /// <returns>Dev, Test or Prod.</returns>
    public static string ServiceName(string stage) => ServiceNames[Rank(stage) is var rank and < 3 ? rank : throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not a deployment stage.")];

    /// <summary>Matches <paramref name="value"/> against a known stage, ignoring case.</summary>
    /// <param name="value">The caller-supplied stage text.</param>
    /// <param name="stage">The canonical lower-case stage when matched.</param>
    /// <returns><see langword="true"/> when the value names a stage.</returns>
    public static bool TryParse(string? value, out string stage)
    {
        stage = All.FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        return stage.Length > 0;
    }

    /// <summary>Ranks a stage in promotion order, for display.</summary>
    /// <param name="stage">The stage to rank.</param>
    /// <returns>The promotion-order rank, or <see cref="int.MaxValue"/> when unknown.</returns>
    public static int Rank(string stage) => All.ToList().IndexOf(stage) is var index and >= 0 ? index : int.MaxValue;
}

/// <summary>
/// The entity states a row may hold.
///
/// Only <see cref="Saved"/> is reachable here: delete removes rows outright rather than soft-deleting,
/// so the service's <c>Deleted</c> state has no local equivalent.
/// </summary>
public static class EntityStates
{
    /// <summary>The row is live.</summary>
    public const string Saved = "Saved";

    /// <summary>Every state the schema accepts.</summary>
    public static readonly IReadOnlyList<string> All = [Saved];
}

/// <summary>The principal object types a <see cref="PrincipalReference"/> may name.</summary>
public static class PrincipalObjectTypes
{
    /// <summary>A signed-in user.</summary>
    public const string User = "User";
}

/// <summary>What created a managed-agent record.</summary>
public static class ManagedAgentOrigins
{
    /// <summary>This command-line tool.</summary>
    public const string AgentHostCli = "AgentHostCLI";
}

/// <summary>A principal the caller's identity resolved to.</summary>
public sealed record PrincipalReference
{
    /// <summary>The principal's object identifier.</summary>
    public string? ObjectId { get; init; }

    /// <summary>The principal's object type, one of <see cref="PrincipalObjectTypes"/>.</summary>
    public string? ObjectType { get; init; }
}

/// <summary>
/// One row of the managed-agent registry, mirroring the <c>ManagedAgent</c> PPMS entity.
///
/// Field names follow the service's schema rather than .NET convention, so a row written here and a
/// row read from the Agent Management Service line up without a translation table. Service fields this
/// build has no source for — the EDS geography, CodeAgent runtime data, and the Cosmos storage
/// artifacts — are omitted rather than carried as columns that can only ever be null.
/// </summary>
public sealed record ManagedAgentRecord
{
    /// <summary>The tenant owning the home environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// The home environment: the dev-slot target and the default deletion target.
    /// </summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent's identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name, which <c>--agent-name</c> resolves against.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Free-text description held on the record only; never sent to Copilot Studio.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>The agent type, one of <see cref="AgentTypes"/>.</summary>
    public required string AgentType { get; init; }

    /// <summary>Where the agent's workspace sits, relative to the project root.</summary>
    public required string AgentLocation { get; init; }

    /// <summary>The Copilot Studio agent this record's content lives in, once one is known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? McsAgentId { get; init; }

    /// <summary>The Copilot Studio schema name this record deploys under, once one is known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? McsSchemaName { get; init; }

    /// <summary>The Dataverse workflow this record's content lives in, for a standalone workflow.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? McsWorkflowId { get; init; }

    /// <summary>The GRS repository folder AMS registered the agent with, which only AMS mode writes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentSourceControlMetadata? SourceControlMetadata { get; init; }

    /// <summary>What created the record.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Origin { get; init; }

    /// <summary>The principal that owns the agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrincipalReference? Owner { get; init; }

    /// <summary>The principal that created the agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrincipalReference? CreatedBy { get; init; }

    /// <summary>The principal that last deployed the agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrincipalReference? LastDeployedBy { get; init; }

    /// <summary>When the record was created.</summary>
    public DateTimeOffset CreatedTime { get; init; }

    /// <summary>When the record last changed.</summary>
    public DateTimeOffset ChangedTime { get; init; }

    /// <summary>The persisted entity state, one of <see cref="EntityStates"/>.</summary>
    public required string AgentState { get; init; }

    /// <summary>Concurrency token, replaced on every write.</summary>
    public required string EntityTag { get; init; }
}

/// <summary>The repository folder of an agent AMS registered, named as AMS's <c>AppSourceControlMetadata</c> names it.</summary>
/// <param name="RepositoryId">The GRS repository.</param>
/// <param name="Branch">The branch remote deploys take, main.</param>
/// <param name="Directory">The agent's top-level folder in the repository.</param>
public sealed record AgentSourceControlMetadata(string RepositoryId, string Branch, string Directory);

/// <summary>
/// One deployment slot for a managed agent, mirroring the <c>AgentDeployment</c> ESP entity.
///
/// CodeAgent-only columns and the Git repository metadata are omitted for the same reason as on
/// <see cref="ManagedAgentRecord"/>: this build cannot produce a value for them.
/// </summary>
public sealed record AgentDeploymentRecord
{
    /// <summary>The slot's own identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The <see cref="ManagedAgentRecord.AgentId"/> this slot belongs to.</summary>
    public required string AgentId { get; init; }

    /// <summary>The agent type, one of <see cref="AgentTypes"/>.</summary>
    public required string AgentType { get; init; }

    /// <summary>The slot's stage, one of <see cref="DeploymentStages"/>.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The slot's name, unique within the agent.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The tenant owning the deployment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The master environment for this deployment, for an MCS agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HomeEnvironmentId { get; init; }

    /// <summary>The environment the slot deploys to, for an MCS agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; init; }

    /// <summary>The deployment's human-readable name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    /// <summary>The deployment's description.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Whether this is the agent's primary deployment; at most one per agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDefault { get; init; }


    /// <summary>The Azure region hosting the bot, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }

    /// <summary>The ARM resource id of the provisioned bot, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BotResourceId { get; init; }

    /// <summary>The bot's ARM <c>msaAppId</c>, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntraAppId { get; init; }

    /// <summary>The bot's ARM <c>msaAppType</c>, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntraAppIdType { get; init; }

    /// <summary>The bot's ARM SKU, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sku { get; init; }

    /// <summary>The bot's messaging endpoint, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; init; }

    /// <summary>The safe OAuth connection metadata, for a CodeAgent.</summary>
    [JsonPropertyName("oauthConnections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeploymentOAuthConnection>? OAuthConnections { get; init; } = [];

    /// <summary> The channels enabled on the deployment, for a CodeAgent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeploymentChannelRecord>? Channels { get; init; } = [];

    /// <summary>The principal that created the slot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrincipalReference? CreatedBy { get; init; }

    /// <summary>The principal that last deployed to the slot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrincipalReference? LastDeployedBy { get; init; }

    /// <summary>When this slot last deployed successfully, or absent when it never has.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastDeployedTime { get; init; }

    /// <summary>When the slot was created.</summary>
    public DateTimeOffset CreatedTime { get; init; }

    /// <summary>When the slot last changed.</summary>
    public DateTimeOffset ChangedTime { get; init; }

    /// <summary>Concurrency token, replaced on every write.</summary>
    public required string EntityTag { get; init; }

    /// <summary>Whether the gateway should refuse traffic for this deployment.</summary>
    public bool IsQuarantined { get; init; }

    /// <summary>Why the deployment was quarantined.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuarantinedReason { get; init; }

    /// <summary>Whether the deployment may process traffic.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>The schema versions this build reads and writes.</summary>
public static class AgentConfigSchema
{
    /// <summary>The version this build writes.</summary>
    public const int Version = 3;

    /// <summary>The oldest version this build still reads, upgrading it in place on the next write.</summary>
    public const int MinimumReadableVersion = 2;
}

/// <summary>
/// The on-disk shape of <c>agent.config.json</c>: both registry tables in one source-controlled,
/// reviewable document.
/// </summary>
public sealed record AgentConfigDocument
{
    /// <summary>The schema this document declares, which gates whether this build may read it.</summary>
    public int SchemaVersion { get; init; } = AgentConfigSchema.Version;

    /// <summary>The managed-agent table.</summary>
    public IReadOnlyList<ManagedAgentRecord> ManagedAgents { get; init; } = [];

    /// <summary>The agent-deployment table.</summary>
    public IReadOnlyList<AgentDeploymentRecord> AgentDeployments { get; init; } = [];
}

/// <summary>Source-generated <c>System.Text.Json</c> metadata for <see cref="AgentConfigDocument"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentConfigDocument))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class AgentConfigJsonContext : JsonSerializerContext
{
}
