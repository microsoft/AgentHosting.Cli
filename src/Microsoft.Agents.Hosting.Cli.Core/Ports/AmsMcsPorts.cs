namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Where an MCS agent's source lives.</summary>
public sealed record AmsMcsSourceControl
{
    /// <summary>The GRS repository.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>The repository clone url.</summary>
    public string? GitUrl { get; init; }

    /// <summary>The repository branch.</summary>
    public string? Branch { get; init; }

    /// <summary>The agent's top-level folder in the repository.</summary>
    public string? Directory { get; init; }

    /// <summary>Whether the register created the repository, in the register answer only.</summary>
    public bool RepositoryCreated { get; init; }
}

/// <summary>The MCS properties of an agent record.</summary>
public sealed record AmsMcsAgentProperties
{
    /// <summary>The bot schema name.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The authoring shape.</summary>
    public string? AuthoringShape { get; init; }

    /// <summary>The home Dev deployment, or null until one is created.</summary>
    public string? HomeDeploymentId { get; init; }
}

/// <summary>An agent record as the Agent Management Service data plane reports it.</summary>
public sealed record AmsMcsAgentRecord
{
    /// <summary>The agent id.</summary>
    public required string AgentId { get; init; }

    /// <summary>The owning tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The home environment.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The description.</summary>
    public string? Description { get; init; }

    /// <summary>The agent type, such as MCSAgent or CodeAgent.</summary>
    public required string AgentType { get; init; }

    /// <summary>The bound repository.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>Where the agent's source lives.</summary>
    public AmsMcsSourceControl? SourceControl { get; init; }

    /// <summary>The MCS properties.</summary>
    public AmsMcsAgentProperties? Mcs { get; init; }

    /// <summary>When the record last changed.</summary>
    public DateTimeOffset? ChangedTime { get; init; }

    /// <summary>The strong ETag the agent delete sends as If-Match.</summary>
    public string? ETag { get; init; }

    /// <summary>The agent's deployments, in the register answer.</summary>
    public IReadOnlyList<AmsMcsDeploymentRecord> Deployments { get; init; } = [];
}

/// <summary>One agent record as the agent list reports it, with the fields the list adds for MCS agents.</summary>
public sealed record AmsMcsAgentListItem
{
    /// <summary>The agent id.</summary>
    public required string AgentId { get; init; }

    /// <summary>The owning tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The home environment.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent's description.</summary>
    public string? Description { get; init; }

    /// <summary>The agent type, such as MCSAgent or CodeAgent.</summary>
    public required string AgentType { get; init; }

    /// <summary>The bot schema name.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The GRS repository.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>The agent's top-level folder in the repository.</summary>
    public string? AgentFolder { get; init; }

    /// <summary>The repository branch.</summary>
    public string? Branch { get; init; }

    /// <summary>The home Dev deployment.</summary>
    public string? HomeDeploymentId { get; init; }

    /// <summary>How many deployments the agent has.</summary>
    public int? DeploymentCount { get; init; }

    /// <summary>NotDeployed until a deployment of the agent completes its first deploy, then Deployed.</summary>
    public string? CloudState { get; init; }

    /// <summary>The agent's deployments, when the repository read asked for them.</summary>
    public IReadOnlyList<AmsMcsDeploymentRecord> Deployments { get; init; } = [];
}

/// <summary>The AMS agent that holds a Copilot Studio agent or flow through one of its deployments.</summary>
public sealed record AmsMcsHolder
{
    /// <summary>The holding agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The holding agent's home environment.</summary>
    public required string HomeEnvironmentId { get; init; }

    /// <summary>The deployment that holds the Copilot Studio agent or flow, if any.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>Active, Pending or Releasing.</summary>
    public required string State { get; init; }

    /// <summary>The holding agent's list item, set only when it is homed in the request's environment.</summary>
    public AmsMcsAgentListItem? Agent { get; init; }
}

/// <summary>The states of an AMS agent's hold on a Copilot Studio agent or flow.</summary>
public static class AmsMcsHolderState
{
    /// <summary>Claimed while the agent's register runs.</summary>
    public const string Pending = "Pending";

    /// <summary>Held by the agent.</summary>
    public const string Active = "Active";

    /// <summary>Being released while the agent is deleted.</summary>
    public const string Releasing = "Releasing";
}

/// <summary>A GRS repository and the agents AMS registered in it.</summary>
public sealed record AmsMcsRepository
{
    /// <summary>The repository.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>GRS's display name of the repository.</summary>
    public string? Name { get; init; }

    /// <summary>The repository clone url.</summary>
    public required string GitUrl { get; init; }

    /// <summary>The branch remote deploys take.</summary>
    public string? Branch { get; init; }

    /// <summary>The newest commit of the branch, or null for an empty repository.</summary>
    public string? HeadCommitSha { get; init; }

    /// <summary>Whether the branch holds the agent folder the read asked about.</summary>
    public bool AgentFolderExists { get; init; }

    /// <summary>The agents AMS registered in the repository.</summary>
    public IReadOnlyList<AmsMcsAgentListItem> Agents { get; init; } = [];
}

/// <summary>One deployment an agent's register creates.</summary>
/// <param name="DeploymentId">The deployment id msagent chose.</param>
/// <param name="DeploymentType">Dev, Test or Prod.</param>
/// <param name="DeploymentName">The deployment name.</param>
/// <param name="EnvironmentId">The environment the deployment targets.</param>
/// <param name="BotId">The Copilot Studio agent the deployment claims, or null.</param>
/// <param name="WorkflowId">The workflow the deployment claims, or null.</param>
public sealed record AmsMcsRegistrationDeployment(string DeploymentId, string DeploymentType, string DeploymentName, string EnvironmentId, string? BotId, string? WorkflowId);

/// <summary>Transport input for registering an MCS agent or standalone workflow with its repository folder.</summary>
public sealed record AmsMcsRegistration
{
    /// <summary>The on-behalf-of capable endpoint of the agent's home environment.</summary>
    public required AmsServiceTarget Target { get; init; }

    /// <summary>The agent id msagent chose.</summary>
    public required string AgentId { get; init; }

    /// <summary>The display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The description.</summary>
    public string? Description { get; init; }

    /// <summary>MCSAgent or MCSWorkflow.</summary>
    public required string AgentType { get; init; }

    /// <summary>The bot schema name, null for a workflow.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The authoring shape's wire value, null for a workflow.</summary>
    public string? AuthoringShape { get; init; }

    /// <summary>The repository the folder joins, or null for a new repository.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>The branch remote deploys take.</summary>
    public required string Branch { get; init; }

    /// <summary>The agent's top-level folder in the repository.</summary>
    public required string AgentFolder { get; init; }

    /// <summary>The deployments to create with the agent.</summary>
    public IReadOnlyList<AmsMcsRegistrationDeployment> Deployments { get; init; } = [];
}

/// <summary>Transport input for telling AMS about a deploy msagent made from the working folder.</summary>
public sealed record AmsMcsDeployRecordRequest
{
    /// <summary>The on-behalf-of capable endpoint of the agent's home environment.</summary>
    public required AmsServiceTarget Target { get; init; }

    /// <summary>The agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment that was deployed to.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The Copilot Studio agent the deploy wrote, for an agent.</summary>
    public string? BotId { get; init; }

    /// <summary>The workflow the deploy wrote, for a standalone workflow.</summary>
    public string? WorkflowId { get; init; }

    /// <summary>The commit the deployed content is based on, or null when git named none.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Whether the deployed content is exactly that commit.</summary>
    public bool Exact { get; init; }

    /// <summary>Whether the deploy published.</summary>
    public bool Published { get; init; }

    /// <summary>When the deploy completed.</summary>
    public required DateTimeOffset DeployedTime { get; init; }
}

/// <summary>One page of agent records.</summary>
public sealed record AmsMcsAgentPage
{
    /// <summary>The records on this page.</summary>
    public required IReadOnlyList<AmsMcsAgentListItem> Agents { get; init; }

    /// <summary>Whether the service holds more records than this page.</summary>
    public bool HasMore { get; init; }

    /// <summary>The token that reads the next page, or null on the last page.</summary>
    public string? NextSkipToken { get; init; }
}

/// <summary>Transport input for one MCS deploy request.</summary>
public sealed record AmsMcsDeployTransportRequest
{
    /// <summary>The on-behalf-of capable endpoint the request is sent to.</summary>
    public required AmsServiceTarget Target { get; init; }

    /// <summary>The agent to deploy.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment to deploy to.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The key that makes a retried request start one operation.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The commit to deploy, or null for the commit the service takes for the stage.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Whether the caller asked to publish, which a Dev deploy needs and a Test or Prod deploy does anyway.</summary>
    public bool Publish { get; init; }

    /// <summary>Whether a Dev deploy replaces Dev's edits after saving them, which a Test or Prod deploy does anyway.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Whether the service answers once the deploy started rather than once it ended.</summary>
    public bool RespondAsync { get; init; }
}

/// <summary>Transport input for one capture request.</summary>
public sealed record AmsMcsCaptureTransportRequest
{
    /// <summary>The on-behalf-of capable endpoint the request is sent to.</summary>
    public required AmsServiceTarget Target { get; init; }

    /// <summary>The agent whose Copilot Studio content is captured.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment whose Copilot Studio content is captured.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The key that makes a retried request start one operation.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The commit message, or null for the service's.</summary>
    public string? Message { get; init; }

    /// <summary>The side that wins every conflict, as the caller named it, or null to fail on a conflict.</summary>
    public string? Resolve { get; init; }

    /// <summary>Whether the service answers once the capture started rather than once it ended.</summary>
    public bool RespondAsync { get; init; }
}

/// <summary>An accepted deploy or capture, and the commit the service pinned on its operation.</summary>
public sealed record AmsMcsOperationAccepted
{
    /// <summary>The operation that runs the deploy or capture.</summary>
    public required string OperationId { get; init; }

    /// <summary>The operation status when accepted.</summary>
    public required string Status { get; init; }

    /// <summary>The commit the operation deploys.</summary>
    public string? SourceCommitSha { get; init; }

    /// <summary>The operation the service answered with once it ended, or null when it answered while the operation ran.</summary>
    public AmsMcsOperationRecord? Finished { get; init; }
}

/// <summary>The customer-safe failure an operation recorded.</summary>
public sealed record AmsMcsOperationError
{
    /// <summary>The error code, such as McsSourceValidationFailed.</summary>
    public string? Code { get; init; }

    /// <summary>The customer-safe message.</summary>
    public string? Message { get; init; }

    /// <summary>The customer-safe details, such as the connection reference a deploy could not use.</summary>
    public IReadOnlyList<AmsMcsOperationErrorDetail> Details { get; init; } = [];
}

/// <summary>One customer-safe detail of an operation failure.</summary>
/// <param name="Code">The detail code, such as ConnectionReferenceUnusable.</param>
/// <param name="Message">The detail message.</param>
public sealed record AmsMcsOperationErrorDetail(string? Code, string? Message);

/// <summary>A customer-safe notice an operation reported.</summary>
public sealed record AmsMcsOperationNotice
{
    /// <summary>FlowLeftInDraft, CollectionSharedWithOtherBots, PushWarning, or a kind this build does not know.</summary>
    public required string Kind { get; init; }

    /// <summary>The flow or collection the notice is about, or the subject of a push warning's finding.</summary>
    public string? Target { get; init; }

    /// <summary>The related names, such as the unbound connection references or the other agents linked to a collection.</summary>
    public IReadOnlyList<string> RelatedItems { get; init; } = [];

    /// <summary>The code of a push warning's finding, such as PromptRefused.</summary>
    public string? Code { get; init; }

    /// <summary>The service's reason for a push warning's finding, already redacted.</summary>
    public string? Detail { get; init; }

    /// <summary>The sync library's sentence for a push warning's finding.</summary>
    public string? Message { get; init; }
}

/// <summary>The notice kinds the service reports.</summary>
public static class AmsMcsOperationNoticeKind
{
    /// <summary>A flow was left in draft, for example because a connection reference is unbound.</summary>
    public const string FlowLeftInDraft = "FlowLeftInDraft";

    /// <summary>A pushed component collection is also linked to other Copilot Studio agents.</summary>
    public const string CollectionSharedWithOtherBots = "CollectionSharedWithOtherBots";

    /// <summary>The shared push reported a finding on a deploy that still succeeded.</summary>
    public const string PushWarning = "PushWarning";
}

/// <summary>An MCS operation as the Agent Management Service data plane reports it.</summary>
public sealed record AmsMcsOperationRecord
{
    /// <summary>The operation id.</summary>
    public required string OperationId { get; init; }

    /// <summary>The operation kind, such as Deploy or Capture.</summary>
    public required string Kind { get; init; }

    /// <summary>Accepted, Running, Succeeded or Failed.</summary>
    public required string Status { get; init; }

    /// <summary>The agent the operation belongs to.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment the operation acts on.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The commit the operation reads.</summary>
    public string? SourceCommitSha { get; init; }

    /// <summary>The commit the operation produced.</summary>
    public string? ResultCommitSha { get; init; }

    /// <summary>The commit a Dev deploy's capture of Dev's edits made.</summary>
    public string? CapturedCommitSha { get; init; }

    /// <summary>The commit whose later changes a capture merged with Dev's edits.</summary>
    public string? MergedCommitSha { get; init; }

    /// <summary>When the deployed agent was last published, by this deploy or before it.</summary>
    public DateTimeOffset? LastPublishedTime { get; init; }

    /// <summary>The principal that started the operation.</summary>
    public string? StartedBy { get; init; }

    /// <summary>When the operation started.</summary>
    public DateTimeOffset? StartedTime { get; init; }

    /// <summary>When the operation last reported progress.</summary>
    public DateTimeOffset? LastHeartbeatTime { get; init; }

    /// <summary>When the operation finished.</summary>
    public DateTimeOffset? CompletedTime { get; init; }

    /// <summary>Whether the deploy publishes, as the service decided from the request and the stage, true when the service does not say, since every deploy published before the choice existed.</summary>
    public bool Publish { get; init; }

    /// <summary>The failure the operation recorded.</summary>
    public AmsMcsOperationError? Error { get; init; }

    /// <summary>Customer-safe notices, such as flows left in draft or other bots using a pushed collection.</summary>
    public IReadOnlyList<AmsMcsOperationNotice> Notices { get; init; } = [];

    /// <summary>Whether the operation has finished.</summary>
    public bool IsTerminal => Status is AmsMcsOperationStatus.Succeeded or AmsMcsOperationStatus.Failed;
}

/// <summary>The operation status values the service reports.</summary>
public static class AmsMcsOperationStatus
{
    /// <summary>Accepted and waiting for a worker.</summary>
    public const string Accepted = "Accepted";

    /// <summary>Running.</summary>
    public const string Running = "Running";

    /// <summary>Finished successfully.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>Finished with a failure.</summary>
    public const string Failed = "Failed";
}

/// <summary>A deployment of an MCS agent as the Agent Management Service control plane reports it.</summary>
public sealed record AmsMcsDeploymentRecord
{
    /// <summary>The deployment id.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The owning agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The agent's home environment.</summary>
    public required string HomeEnvironmentId { get; init; }

    /// <summary>The environment the deployment targets.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The deployment name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>Dev, Test or Prod.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The agent type the deployment serves.</summary>
    public string? AgentType { get; init; }

    /// <summary>NotDeployed, Deploying, Deployed or Failed.</summary>
    public string? State { get; init; }

    /// <summary>The bot the deployment controls.</summary>
    public string? BotId { get; init; }

    /// <summary>The standalone workflow the deployment controls.</summary>
    public string? WorkflowId { get; init; }

    /// <summary>The bot schema name.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The commit the deployment's last deploy deployed.</summary>
    public string? LastDeployedCommitSha { get; init; }

    /// <summary>Remote or Local: which kind of deploy deployed last.</summary>
    public string? LastDeployOrigin { get; init; }

    /// <summary>The commit the deployment's Copilot Studio content was last known to match.</summary>
    public string? CloudBaseCommitSha { get; init; }

    /// <summary>Whether the deployment's Copilot Studio content matched <see cref="CloudBaseCommitSha"/> exactly.</summary>
    public bool? CloudBaseExact { get; init; }

    /// <summary>Who deployed last.</summary>
    public string? LastDeployedBy { get; init; }

    /// <summary>When the last deploy finished.</summary>
    public DateTimeOffset? LastDeployedTime { get; init; }
}

/// <summary>Transport input for creating a Dev, Test or Prod deployment of an MCS agent.</summary>
public sealed record AmsMcsDeploymentCreateTransportRequest
{
    /// <summary>The control-plane endpoint addressed through the agent's home environment.</summary>
    public required AmsServiceTarget Target { get; init; }

    /// <summary>The owning agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment id msagent chose, which the service keeps.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The wire agent type, MCSAgent or MCSWorkflow.</summary>
    public required string AgentType { get; init; }

    /// <summary>Dev, Test or Prod.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The deployment name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The environment the deployment targets, which is the agent's home environment for Dev.</summary>
    public required string TargetEnvironmentId { get; init; }
}

/// <summary>Transport input for renaming an MCS deployment.</summary>
public sealed record AmsMcsDeploymentRenameTransportRequest
{
    /// <summary>The control-plane endpoint addressed through the agent's home environment.</summary>
    public required AmsServiceTarget Target { get; init; }

    /// <summary>The owning agent.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment to rename.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The MCS agent type of the deployment's record, sent as the hint that selects the shared route's MCS path.</summary>
    public required string AgentType { get; init; }

    /// <summary>The deployment's new name.</summary>
    public required string DeploymentName { get; init; }
}

/// <summary>Reads agent records through the data plane.</summary>
public interface IAmsMcsAgentReadTransport
{
    /// <summary>Reads one agent.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsAgentRecord> GetAgentAsync(AmsServiceTarget target, string agentId, CancellationToken cancellationToken);

    /// <summary>Reads one page of the MCS agent and standalone workflow records in the environment.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="top">The page size.</param>
    /// <param name="agentType">MCSAgent or MCSWorkflow to list, or null for both.</param>
    /// <param name="repositoryId">The repository whose agents to list, or null for every repository.</param>
    /// <param name="skipToken">The token a previous page returned, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsAgentPage> ListAgentsAsync(AmsServiceTarget target, int top, string? agentType, string? repositoryId, string? skipToken, CancellationToken cancellationToken);

    /// <summary>Reads a repository and the agents AMS registered in it.</summary>
    /// <param name="target">The data-plane endpoint of the home environment.</param>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="agentFolder">The folder whose presence on the branch to report, or null.</param>
    /// <param name="includeDeployments">Whether each agent carries its deployments.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsRepository> ReadRepositoryAsync(AmsServiceTarget target, string repositoryId, string? agentFolder, bool includeDeployments, CancellationToken cancellationToken);

    /// <summary>Reads the AMS agents that hold a Copilot Studio agent or flow, which are none or one.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="botId">The Copilot Studio agent, or null for a flow.</param>
    /// <param name="workflowId">The flow, or null for a Copilot Studio agent.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<AmsMcsHolder>> FindHoldersAsync(AmsServiceTarget target, string? botId, string? workflowId, CancellationToken cancellationToken);
}

/// <summary>Registers MCS agents and standalone workflows with their repository folders.</summary>
public interface IAmsMcsRegistrationTransport
{
    /// <summary>Registers an agent and the deployments it starts with, creating the repository when none is named; registering the same agent again answers the same record.</summary>
    /// <param name="request">The register request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsAgentRecord> RegisterAsync(AmsMcsRegistration request, CancellationToken cancellationToken);
}

/// <summary>Starts and reads MCS operations through the data plane.</summary>
public interface IAmsMcsOperationTransport
{
    /// <summary>Starts a deploy operation, which the result carries finished when the service answered once it ended.</summary>
    /// <param name="request">The deploy request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsOperationAccepted> DeployAsync(AmsMcsDeployTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Starts a capture operation, which the result carries finished when the service answered once it ended.</summary>
    /// <param name="request">The capture request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsOperationAccepted> CaptureAsync(AmsMcsCaptureTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one operation.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsOperationRecord> GetOperationAsync(AmsServiceTarget target, string agentId, string operationId, CancellationToken cancellationToken);

    /// <summary>Records a deploy msagent made from the working folder.</summary>
    /// <param name="request">The deploy record.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsDeploymentRecord> RecordDeployAsync(AmsMcsDeployRecordRequest request, CancellationToken cancellationToken);
}

/// <summary>Manages MCS deployment records through the control plane.</summary>
public interface IAmsMcsDeploymentTransport
{
    /// <summary>Creates a Dev, Test or Prod deployment with the id msagent chose.</summary>
    /// <param name="request">The create request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsDeploymentRecord> CreateMcsDeploymentAsync(AmsMcsDeploymentCreateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Renames a deployment, changing nothing else.</summary>
    /// <param name="request">The rename request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AmsMcsDeploymentRecord> RenameMcsDeploymentAsync(AmsMcsDeploymentRenameTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes one deployment record of an MCS agent or standalone workflow.</summary>
    /// <param name="target">The control-plane endpoint addressed through the agent's home environment.</param>
    /// <param name="agentId">The owning agent.</param>
    /// <param name="deploymentId">The deployment to delete.</param>
    /// <param name="agentType">The MCS agent type of the deployment's record, sent as the hint that selects the shared route's MCS path.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task DeleteDeploymentAsync(AmsServiceTarget target, string agentId, string deploymentId, string agentType, CancellationToken cancellationToken);
}

/// <summary>Deletes the AMS records of MCS agents.</summary>
public interface IAmsMcsAgentDeleteTransport
{
    /// <summary>Deletes an agent's AMS records: the agent, its deployments, its folder lease and its bot claims. The repository and the Copilot Studio resources stay.</summary>
    /// <param name="target">The data-plane endpoint of the agent's home environment.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="eTag">The agent's strong ETag, sent as If-Match.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task DeleteAgentAsync(AmsServiceTarget target, string agentId, string eTag, CancellationToken cancellationToken);
}
