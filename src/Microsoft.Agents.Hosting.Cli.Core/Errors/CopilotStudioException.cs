namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Stable, kebab-case error categories emitted as <c>errorKind</c> by the Copilot Studio commands.
/// They exist so an automated caller can branch on a condition without matching on message text.
/// </summary>
public static class CopilotErrorKinds
{
    /// <summary>No active environment, and none supplied.</summary>
    public const string EnvironmentNotSelected = "environment-not-selected";

    /// <summary>The selected environment carries no Dataverse organization url.</summary>
    public const string EnvironmentMissingInstanceUrl = "environment-missing-instance-url";

    /// <summary>A request would have carried a Dataverse token to a different origin.</summary>
    public const string UntrustedDataverseOrigin = "untrusted-dataverse-origin";

    /// <summary>The connection state could not be recorded for the workspace.</summary>
    public const string ConnectionStateNotRecorded = "connection-state-not-recorded";

    /// <summary>No workspace was found at or above the supplied directory.</summary>
    public const string WorkspaceNotFound = "workspace-not-found";

    /// <summary>The target directory already contains files.</summary>
    public const string WorkspaceNotEmpty = "workspace-not-empty";

    /// <summary>The requested agent could not be resolved in the environment.</summary>
    public const string AgentNotFound = "agent-not-found";

    /// <summary>An agent with the requested schema name already exists.</summary>
    public const string AgentAlreadyExists = "agent-already-exists";

    /// <summary>Local and remote both changed the same components; a pull is required first.</summary>
    public const string PushConflictsRequirePull = "push-conflicts-require-pull";

    /// <summary>A destructive command was invoked without confirmation.</summary>
    public const string ConfirmationRequired = "confirmation-required";

    /// <summary>A scaffold or agent template could not be found.</summary>
    public const string TemplateNotFound = "template-not-found";

    /// <summary>The requested component collection could not be resolved.</summary>
    public const string ComponentCollectionNotFound = "component-collection-not-found";

    /// <summary>A clone destination already contains files.</summary>
    public const string DestinationNotEmpty = "destination-not-empty";

    /// <summary>The token lacks the administrative scope the governance endpoints require.</summary>
    public const string QuarantinePermissionDenied = "quarantine-permission-denied";

    /// <summary>The workspace is already bound to a cloud agent.</summary>
    public const string WorkspaceAlreadyConnected = "workspace-already-connected";

    /// <summary>The workspace's authoring shape does not support reattach.</summary>
    public const string ReattachNotSupported = "reattach-not-supported";

    /// <summary>The workspace declares connection references the CLI cannot read.</summary>
    public const string ConnectionReferencesUnreadable = "connection-references-unreadable";

    /// <summary>One connection reference name is declared for two different connectors.</summary>
    public const string ConnectionReferencesConflict = "connection-references-conflict";

    /// <summary>Components were pushed but their knowledge file content was not.</summary>
    public const string KnowledgeUploadFailed = "knowledge-upload-failed";

    /// <summary>A component references an agent absent from the target environment.</summary>
    public const string ConnectedAgentNotInTarget = "connected-agent-not-in-target";

    /// <summary>The agent changed in the cloud after the workspace's change token was issued.</summary>
    public const string RemoteChangedDuringPush = "remote-changed-during-push";

    /// <summary>A component push failed after the service refused a workflow or prompt it depends on.</summary>
    public const string PushFailedAfterRefusedComponents = "push-failed-after-refused-components";

    public const string PushResponseUnreadable = "push-response-unreadable";

    /// <summary>The service refused a connection reference it already holds, so the push did not land.</summary>
    public const string DuplicateConnectionReference = "duplicate-connection-reference";

    /// <summary>The service refused to create a component it already holds, so the push did not land.</summary>
    public const string DuplicateComponent = "duplicate-component";

    /// <summary>The push names a connection reference the environment does not hold, even after the baseline was re-read.</summary>
    public const string MissingConnectionReference = "missing-connection-reference";

    /// <summary>The declaration was removed but the environment still holds the connectionreference record.</summary>
    public const string ConnectionReferenceNotDeleted = "connection-reference-not-deleted";

    /// <summary>The connectionreference record was deleted but the cloud cache still lists it.</summary>
    public const string ConnectionReferenceCacheStale = "connection-reference-cache-stale";

    /// <summary>A workflow tool in the workspace has an empty workflowId.</summary>
    public const string WorkflowToolIdEmpty = "workflow-tool-id-empty";

    /// <summary>Polling exceeded the maximum wait without the agent becoming ready.</summary>
    public const string StatusTimeout = "status-timeout";

    /// <summary>Publishing did not complete within the maximum wait.</summary>
    public const string PublishTimeout = "publish-timeout";

    /// <summary>The workspace was scaffolded and the agent created, but the link could not be written.</summary>
    public const string BootstrapReattachFailed = "bootstrap-reattach-failed";

    /// <summary>The Copilot Studio or Dataverse service reported a failure.</summary>
    public const string ServiceError = "copilot-service-error";

    /// <summary>The workspace holds files the sync library cannot read.</summary>
    public const string WorkspaceFilesUnreadable = "workspace-files-unreadable";
}

/// <summary>
/// A typed failure raised by the Copilot Studio commands. Messages name identifiers and operations
/// only, never tokens, secrets, or request bodies.
/// </summary>
public sealed class CopilotStudioException : AgentHostingException
{
    private CopilotStudioException(int errorCode, string message, int exitCode, string errorKind, string? remediation, Exception? innerException = null) : base(message, exitCode, errorKind, remediation, innerException, errorCode)
    {
    }

    /// <summary>No environment is selected and none was supplied.</summary>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException EnvironmentNotSelected() => new(
        ErrorCodes.CopilotStudio.EnvironmentNotSelected,
        "No environment is selected.",
        ExitCodes.Usage,
        CopilotErrorKinds.EnvironmentNotSelected,
        "Run 'msagent env select <environment-id>', or pass --environment-id.");

    /// <summary>The selected environment has no linked Dataverse organization url.</summary>
    /// <param name="environmentId">The environment that is missing the url.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException EnvironmentMissingInstanceUrl(string environmentId) => new(
        ErrorCodes.CopilotStudio.EnvironmentMissingInstanceUrl,
        $"Environment '{environmentId}' has no linked Dataverse organization url.",
        ExitCodes.Usage,
        CopilotErrorKinds.EnvironmentMissingInstanceUrl,
        $"Re-run 'msagent env select {environmentId}'. If the environment has no Dataverse organization, Copilot Studio commands cannot target it.");

    /// <summary>The environment advertises no Copilot Studio control-plane endpoint to reach.</summary>
    /// <param name="environmentId">The environment that was resolved.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException AgentManagementEndpointMissing(string environmentId) => new(
        ErrorCodes.CopilotStudio.AgentManagementEndpointMissing,
        $"Environment '{environmentId}' advertises no Copilot Studio endpoint, so its agents cannot be reached.",
        ExitCodes.Failure,
        "agent-management-endpoint-missing",
        $"Re-run 'msagent env select {environmentId}' to refresh the environment inventory. An environment with no Copilot Studio endpoint hosts no agents.");

    /// <summary>The sharing snapshot the service returned could not be read as a complete state.</summary>
    /// <param name="operation">The request that returned it.</param>
    /// <param name="innerException">The parse failure, when there was one.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException SharingStateNotReturned(string operation, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.SharingStateNotReturned,
        $"'{operation}' did not return a readable sharing state. Sharing is written whole, so a state that cannot be read cannot be safely changed.",
        ExitCodes.Failure,
        "sharing-state-not-returned",
        "Run `msagent agent share show` to see what access the agent currently has. If that also fails, the service is not returning the sharing contract this build reads and the CLI must not write over it.",
        innerException);

    /// <summary>The user selector matched more than one account, so it names nobody in particular.</summary>
    /// <param name="selector">The value that was supplied.</param>
    /// <param name="matches">The accounts it matched.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException SharePrincipalAmbiguous(string selector, IEnumerable<string> matches) => new(
        ErrorCodes.CopilotStudio.SharePrincipalAmbiguous,
        $"'{selector}' matches more than one account in this environment: {string.Join(", ", matches)}.",
        ExitCodes.Usage,
        "share-principal-ambiguous",
        "Supply the Entra object id instead, which names exactly one principal.");

    /// <summary>The connection state could not be written into the workspace.</summary>
    /// <param name="directory">The workspace that could not be written.</param>
    /// <param name="reason">What the file system reported.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConnectionStateNotRecorded(string directory, string reason) => new(
        ErrorCodes.CopilotStudio.ConnectionStateNotRecorded,
        $"The connection state could not be recorded in '{directory}': {reason}",
        ExitCodes.Failure,
        CopilotErrorKinds.ConnectionStateNotRecorded,
        "Whether a workflow can be enabled is decided from that file, so it is read before anything is enabled. Close anything holding the workspace, check its permissions, and run the command again.");

    /// <summary>No workspace was found at or above the supplied directory.</summary>
    /// <param name="directory">The directory that was searched.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException WorkspaceNotFound(string directory) => new(
        ErrorCodes.Workspace.CopilotWorkspaceNotFound,
        $"No Copilot Studio workspace was found at or above '{directory}'.",
        ExitCodes.Usage,
        CopilotErrorKinds.WorkspaceNotFound,
        "Run 'msagent agent pull' to bring the agent's content into this directory, or run the command from the project directory.");

    /// <summary>The clone target directory is not empty.</summary>
    /// <param name="directory">The non-empty directory.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException WorkspaceNotEmpty(string directory) => new(
        ErrorCodes.Workspace.CopilotWorkspaceNotEmpty,
        $"The directory '{directory}' is not empty.",
        ExitCodes.Usage,
        CopilotErrorKinds.WorkspaceNotEmpty,
        "Choose an empty --output-dir, or remove the existing contents.");

    /// <summary>The requested agent could not be resolved.</summary>
    /// <param name="botIdOrSchemaName">The value that could not be resolved.</param>
    /// <param name="environmentId">The environment that was searched, when it is known.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException AgentNotFound(string botIdOrSchemaName, string? environmentId = null) => new(
        ErrorCodes.CopilotStudio.AgentNotFound,
        environmentId is null
            ? $"No agent matching '{botIdOrSchemaName}' was found in the environment."
            : $"No agent matching '{botIdOrSchemaName}' was found in environment {environmentId}.",
        ExitCodes.Usage,
        CopilotErrorKinds.AgentNotFound,
        environmentId is null
            ? "Run 'msagent agent list' to see the available agents."
            : $"Run 'msagent agent list --environment-id {environmentId}' to see the agents it holds, or deploy this one with 'msagent agent deploy'.");

    /// <summary>An agent with the requested schema name already exists.</summary>
    /// <param name="schemaName">The schema name that is already taken.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException AgentAlreadyExists(string schemaName) => new(
        ErrorCodes.CopilotStudio.AgentAlreadyExists,
        $"An agent with schema name '{schemaName}' already exists in the environment.",
        ExitCodes.Usage,
        CopilotErrorKinds.AgentAlreadyExists,
        "Choose a different --schema-name, or run 'msagent agent pull' to work on the existing agent.");

    /// <summary>Local and remote changes overlap, so the push cannot proceed.</summary>
    /// <param name="conflictingSchemaNames">The components changed on both sides.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException PushConflictsRequirePull(IReadOnlyCollection<string> conflictingSchemaNames) => new(
        ErrorCodes.CopilotStudio.PushConflictsRequirePull,
        $"The agent has {conflictingSchemaNames.Count} remote change{(conflictingSchemaNames.Count == 1 ? string.Empty : "s")} that must be retrieved before your local changes can be applied: {string.Join(", ", conflictingSchemaNames)}.",
        ExitCodes.Usage,
        CopilotErrorKinds.PushConflictsRequirePull,
        "There is a conflict with a remote change. Run `msagent agent pull` to get the latest changes and resolve the conflict, then deploy again — or re-run with --overwrite to replace the cloud with your local content.");

    /// <summary>A destructive command was invoked without confirmation.</summary>
    /// <param name="operation">The operation that requires confirmation.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConfirmationRequired(string operation) => new(
        ErrorCodes.CopilotStudio.ConfirmationRequired,
        $"The '{operation}' operation requires confirmation.",
        ExitCodes.Usage,
        CopilotErrorKinds.ConfirmationRequired,
        "Re-run the command with --force to confirm.");

    /// <summary>A scaffold or agent template could not be found.</summary>
    /// <param name="template">The template that could not be resolved.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException TemplateNotFound(string template) => new(
        ErrorCodes.CopilotStudio.TemplateNotFound,
        $"The template '{template}' was not found.",
        ExitCodes.Usage,
        CopilotErrorKinds.TemplateNotFound,
        "The classic scaffold ships a single template. Omit --init-template, or pass --init-template default.");

    /// <summary>A request would have carried the Dataverse token to a different origin.</summary>
    /// <param name="requested">The origin the request targeted.</param>
    /// <param name="expected">The origin the token was minted for.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException UntrustedDataverseOrigin(string requested, string expected) => new(
        ErrorCodes.CopilotStudio.UntrustedDataverseOrigin,
        $"A Dataverse request targeted '{requested}' but the credential is bound to '{expected}'.",
        ExitCodes.Failure,
        CopilotErrorKinds.UntrustedDataverseOrigin,
        "The request was blocked before the credential was sent. Reselect the environment with 'msagent env select', and report this if it persists.");

    /// <summary>The agent was created, but the workspace could not be linked to it.</summary>
    /// <param name="agentId">The agent that now exists in the environment.</param>
    /// <param name="schemaName">The agent's schema name.</param>
    /// <param name="innerException">The failure that interrupted the link.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException BootstrapReattachFailed(string agentId, string schemaName, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.BootstrapReattachFailed,
        $"Agent '{schemaName}' ({agentId}) exists, but the workspace could not be linked to it: {(innerException is null ? "no further detail was reported." : Describe(innerException))}",
        ExitCodes.Failure,
        CopilotErrorKinds.BootstrapReattachFailed,
        $"The agent exists and was not removed. Run 'msagent agent pull --agent-id {agentId}' into an empty directory to work on it, or 'msagent agent delete --agent-id {agentId}' to remove it.",
        innerException);

    /// <summary>The workspace's recorded organization is not the one its environment resolves to.</summary>
    /// <param name="recordedOrigin">The origin recorded in the workspace.</param>
    /// <param name="expectedOrigin">The origin the environment resolves to.</param>
    /// <param name="environmentId">The environment being targeted.</param>
    /// <param name="projectDirectory">The workspace directory.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException WorkspaceOriginMismatch(string recordedOrigin, string expectedOrigin, string environmentId, string projectDirectory) => new(
        ErrorCodes.Workspace.CopilotWorkspaceOriginMismatch,
        $"{projectDirectory} records the Dataverse organization {recordedOrigin}, but environment {environmentId} is {expectedOrigin}.",
        ExitCodes.Usage,
        CopilotErrorKinds.UntrustedDataverseOrigin,
        "The workspace connection is stale or was edited. Run `msagent agent pull` into a fresh directory, or deploy to a deployment naming the environment the workspace belongs to.");

    /// <summary>The attach failed before any agent was created or adopted.</summary>
    /// <param name="schemaName">The schema name the attach targeted.</param>
    /// <param name="environmentId">The environment the attach targeted.</param>
    /// <param name="innerException">The underlying failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException BootstrapAttachFailed(string schemaName, string environmentId, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.BootstrapAttachFailed,
        $"Agent '{schemaName}' could not be created or adopted in {environmentId}.",
        ExitCodes.Failure,
        CopilotErrorKinds.BootstrapReattachFailed,
        $"Nothing was left behind in {environmentId}. Re-run the command once the cause is resolved.",
        innerException);

    /// <summary>The attach failed before any agent was created or adopted, and that could not be confirmed.</summary>
    /// <param name="schemaName">The schema name the attach targeted.</param>
    /// <param name="environmentId">The environment the attach targeted.</param>
    /// <param name="innerException">The underlying failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException BootstrapAttachUncertain(string schemaName, string environmentId, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.BootstrapAttachUncertain,
        $"Agent '{schemaName}' could not be created or adopted in {environmentId}, and the environment could not be re-read to confirm what it now holds.",
        ExitCodes.Failure,
        CopilotErrorKinds.BootstrapReattachFailed,
        $"An agent named '{schemaName}' may or may not exist in {environmentId}. Run `msagent agent list --environment-id {environmentId}` before re-running.",
        innerException);

    /// <summary>Polling exceeded the maximum wait.</summary>
    /// <param name="agentId">The agent that was polled.</param>
    /// <param name="maximumWait">How long polling ran.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException StatusTimeout(string agentId, TimeSpan maximumWait) => new(
        ErrorCodes.CopilotStudio.StatusTimeout,
        $"Agent '{agentId}' was still not ready after {maximumWait}.",
        ExitCodes.Failure,
        CopilotErrorKinds.StatusTimeout,
        "Increase --max-wait, or check the agent's state in Copilot Studio.");

    /// <summary>The service reported a failure.</summary>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="detail">What the service said, when it said anything useful.</param>
    /// <param name="innerException">The underlying failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ServiceError(string operation, string? detail = null, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.ServiceError,
        string.IsNullOrWhiteSpace(detail)
            ? $"The Copilot Studio service reported a failure during '{operation}'."
            : $"The Copilot Studio service reported a failure during '{operation}'. {detail}",
        ExitCodes.Failure,
        CopilotErrorKinds.ServiceError,
        "Retry the operation. If it persists, check your permissions on the environment.",
        innerException);

    /// <summary>Publishing did not complete within the maximum wait.</summary>
    /// <param name="agentId">The agent that was published.</param>
    /// <param name="maximumWait">How long polling ran.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException PublishTimeout(string agentId, TimeSpan maximumWait) => new(
        ErrorCodes.CopilotStudio.PublishTimeout,
        $"Publishing agent '{agentId}' was not confirmed complete after {maximumWait}.",
        ExitCodes.Failure,
        CopilotErrorKinds.PublishTimeout,
        "The publish may still be in progress. Increase --max-wait, or check the agent in Copilot Studio.");

    /// <summary>A requested component collection could not be resolved.</summary>
    /// <param name="value">The value that could not be resolved.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ComponentCollectionNotFound(string value) => new(
        ErrorCodes.CopilotStudio.ComponentCollectionNotFound,
        $"No component collection matching '{value}' was found in the environment.",
        ExitCodes.Usage,
        CopilotErrorKinds.ComponentCollectionNotFound,
        "Pass component collection ids as GUIDs separated by ';', or use their schema names.");

    /// <summary>A clone destination already contains files.</summary>
    /// <param name="path">The non-empty destination.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException DestinationNotEmpty(string path) => new(
        ErrorCodes.Workspace.CopilotDestinationNotEmpty,
        $"The directory '{path}' already holds content a clone would overwrite.",
        ExitCodes.Usage,
        CopilotErrorKinds.DestinationNotEmpty,
        "Clone into a directory holding nothing but the agent's own .config, or remove the existing contents.");

    /// <summary>The governance endpoints rejected the token for lack of an administrative permission.</summary>
    /// <param name="agentId">The agent the operation targeted.</param>
    /// <param name="innerException">The underlying failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException QuarantinePermissionDenied(string agentId, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.QuarantinePermissionDenied,
        $"The quarantine API rejected the request for agent '{agentId}' (HTTP 403).",
        ExitCodes.Failure,
        CopilotErrorKinds.QuarantinePermissionDenied,
        "Quarantine is an administrative operation. A tenant or Power Platform admin directory role is "
            + "necessary but not sufficient — the signed-in token must also carry the administrative "
            + "permission this endpoint requires. Check the CLI's app registration.",
        innerException);

    /// <summary>The workspace is already bound to a cloud agent and rebinding was not permitted.</summary>
    /// <param name="directory">The workspace directory.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException WorkspaceAlreadyConnected(string directory) => new(
        ErrorCodes.Workspace.CopilotWorkspaceAlreadyConnected,
        $"The workspace at '{directory}' is already connected to a cloud agent.",
        ExitCodes.Usage,
        CopilotErrorKinds.WorkspaceAlreadyConnected,
        "Deploy to the deployment this workspace is already bound to, or use a deployment in a different environment to rebind it.");

    /// <summary>The workspace's authoring shape does not support being reattached.</summary>
    /// <param name="directory">The workspace directory.</param>
    /// <param name="reason">The gate's own explanation.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ReattachNotSupported(string directory, string reason) => new(
        ErrorCodes.Workspace.CopilotReattachNotSupported,
        $"The project at '{directory}' cannot be connected to a cloud agent: {reason}",
        ExitCodes.Usage,
        CopilotErrorKinds.ReattachNotSupported,
        "Open the project in Copilot Studio, or convert it to a supported authoring shape.");

    /// <summary>The declared connection references are present but could not be read.</summary>
    /// <param name="path">The declaration file.</param>
    /// <param name="reason">Why it could not be read.</param>
    /// <param name="innerException">The underlying failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConnectionReferencesUnreadable(string path, string reason, Exception? innerException = null) => new(
        ErrorCodes.CopilotStudio.ConnectionReferencesUnreadable,
        $"The connection references declared at '{path}' could not be read: {reason}",
        ExitCodes.Usage,
        CopilotErrorKinds.ConnectionReferencesUnreadable,
        "Fix or remove the declaration; deploying without it would omit references the agent's actions name.",
        innerException);

    /// <summary>The workspace holds files that must be fixed before it can be synchronized.</summary>
    /// <param name="summary">The diagnostic summary naming each file and position.</param>
    /// <param name="innerException">The failure the sync library raised.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException WorkspaceFilesUnreadable(string summary, Exception innerException) => new(
        ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable,
        summary,
        ExitCodes.Usage,
        CopilotErrorKinds.WorkspaceFilesUnreadable,
        "Fix the files listed above, then run the command again. A file left with merge conflict markers must have them resolved first.",
        innerException);

    /// <summary>
    /// One connection reference name is declared twice, for different connectors.
    /// </summary>
    /// <param name="logicalName">The name both declarations use.</param>
    /// <param name="firstPath">The declaration read first.</param>
    /// <param name="firstConnectorId">The connector it names.</param>
    /// <param name="secondPath">The declaration that disagrees with it.</param>
    /// <param name="secondConnectorId">The connector that one names.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConnectionReferencesConflict(
        string logicalName, string firstPath, string firstConnectorId, string secondPath, string secondConnectorId) => new(
        ErrorCodes.CopilotStudio.ConnectionReferencesConflict,
        $"Connection reference '{logicalName}' is declared for two connectors: '{firstConnectorId}' at '{firstPath}' and '{secondConnectorId}' at '{secondPath}'.",
        ExitCodes.Usage,
        CopilotErrorKinds.ConnectionReferencesConflict,
        "Point both declarations at the same connector, or give one of them its own connection reference name. Which connector the reference is created against would otherwise depend on the order the files are read.");

    /// <summary>
    /// Components were pushed but their knowledge file content was not.
    /// </summary>
    /// <param name="pushedComponentCount">How many components were pushed before the upload failed.</param>
    /// <param name="innerException">The transport failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException KnowledgeUploadFailed(int pushedComponentCount, Exception innerException) => new(
        ErrorCodes.CopilotStudio.KnowledgeUploadFailed,
        $"{pushedComponentCount} component(s) were pushed, but uploading their knowledge file content failed: {Describe(innerException)}",
        ExitCodes.Failure,
        CopilotErrorKinds.KnowledgeUploadFailed,
        "The agent's components are up to date and its knowledge files are empty. Re-run the same command to retry the upload; pushing again is safe.",
        innerException);

    /// <summary>
    /// A component references an agent that does not exist in the target environment.
    /// </summary>
    /// <param name="environmentId">The environment the push was aimed at.</param>
    /// <param name="innerException">The sync library's failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConnectedAgentNotInTarget(string environmentId, Exception innerException) => new(
        ErrorCodes.CopilotStudio.ConnectedAgentNotInTarget,
        $"A component references another agent that does not exist in {environmentId}. Connected and child agents are referenced by id, and an id only exists in the environment that issued it.",
        ExitCodes.Failure,
        CopilotErrorKinds.ConnectedAgentNotInTarget,
        "Deploy the referenced agent to this environment first, or remove the connected-agent action from the workspace before deploying here.",
        innerException);

    /// <summary>
    /// The service rejected a push because the agent changed in the cloud after the workspace's change
    /// token was issued.
    /// </summary>
    /// <param name="innerException">The sync library's failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException RemoteChangedDuringPush(Exception innerException) => new(
        ErrorCodes.CopilotStudio.RemoteChangedDuringPush,
        "The agent changed in the cloud while this push was being prepared.",
        ExitCodes.Usage,
        CopilotErrorKinds.RemoteChangedDuringPush,
        "Run `msagent agent pull` to get the latest changes, then deploy again — or re-run with --overwrite to replace the cloud with your local content.",
        innerException);

    /// <summary>
    /// A component push failed after the service had already refused a workflow or AI prompt.
    /// </summary>
    /// <param name="environmentId">The environment that was pushed to.</param>
    /// <param name="refusals">What the service refused earlier in the same push.</param>
    /// <param name="innerException">The component push failure.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException PushFailedAfterRefusedComponents(string environmentId, IReadOnlyList<string> refusals, Exception innerException) => new(
        ErrorCodes.CopilotStudio.PushFailedAfterRefusedComponents,
        $"A component push to {environmentId} failed because the service did not store a workflow or prompt it depends on, after refusing {Count(refusals)} earlier in the same push.",
        ExitCodes.Failure,
        CopilotErrorKinds.PushFailedAfterRefusedComponents,
        "A component references a workflow or prompt the service did not store, so it could not be resolved. Fix the reported refusal — an unbound connection reference is the usual cause, and `msagent deployment connection update` binds it — then deploy again.",
        innerException);

    public static CopilotStudioException PushResponseUnreadable(string environmentId, int attempts, Exception innerException) => new(
        ErrorCodes.CopilotStudio.PushResponseUnreadable,
        $"The service's reply to this push could not be read after {attempts} attempt(s), so what it stored is unknown: {Describe(innerException)} Environment {environmentId}.",
        ExitCodes.Failure,
        CopilotErrorKinds.PushResponseUnreadable,
        "Some or all components may already be stored. Run `msagent agent pull` to see what the environment actually holds, then deploy again to send whatever is still missing.",
        innerException);

    /// <summary>
    /// The service refused a connection reference it already holds, after the baseline was re-read.
    /// </summary>
    /// <param name="environmentId">The environment that was pushed to.</param>
    /// <param name="refusals">Anything the service had already refused in this push.</param>
    /// <param name="innerException">The service's refusal.</param>
    /// <returns>The exception to throw.</returns>
    public static CopilotStudioException DuplicateConnectionReference(string environmentId, IReadOnlyList<string> refusals, Exception innerException) => ComponentPushNotCompleted(ErrorCodes.CopilotStudio.DuplicateConnectionReference, $"A connection reference this push declares already exists in {environmentId}", CopilotErrorKinds.DuplicateConnectionReference, "Delete or rename the conflicting connection reference, or run `msagent deployment connection update` to bind it, then deploy again.", refusals, innerException);

    /// <summary>The service refused to create a component it already holds, after the baseline was re-read.</summary>
    /// <param name="environmentId">The environment that was pushed to.</param>
    /// <param name="schemaName">The refused component's schema name, when the service named it.</param>
    /// <param name="failure">The service's refusal.</param>
    /// <returns>The exception to throw.</returns>
    public static CopilotStudioException DuplicateComponent(string environmentId, string? schemaName, Exception failure) => ComponentPushNotCompleted(ErrorCodes.CopilotStudio.DuplicateComponent, schemaName is null ? $"A component this push creates already exists in {environmentId}" : $"A component this push creates, {schemaName}, already exists in {environmentId}", CopilotErrorKinds.DuplicateComponent, "Run `msagent agent pull` to bring the cloud copy into the workspace, or rename the local component.", [], failure);

    /// <summary>The service reported a connection reference missing after the baseline was re-read from the environment.</summary>
    /// <param name="environmentId">The environment that was pushed to.</param>
    /// <param name="refusals">Anything the service had already refused in this push.</param>
    /// <param name="innerException">The service's refusal.</param>
    /// <returns>The exception to throw.</returns>
    public static CopilotStudioException MissingConnectionReference(string environmentId, IReadOnlyList<string> refusals, Exception innerException) => ComponentPushNotCompleted(ErrorCodes.CopilotStudio.MissingConnectionReference, $"A connection reference this push names does not exist in {environmentId}, even after the baseline was re-read from the environment", CopilotErrorKinds.MissingConnectionReference, "A component still uses a connection reference the environment does not hold. Run `msagent deployment connection list` to see the references this deployment declares, declare the missing one with `msagent deployment connection add`, or remove the component that uses it, then deploy again.", refusals, innerException);

    private static CopilotStudioException ComponentPushNotCompleted(int errorCode, string cause, string errorKind, string remediation, IReadOnlyList<string> refusals, Exception innerException) => new(errorCode, $"{cause}, so the component push did not complete and the agent definition was left as it is.{Appended(refusals)}", ExitCodes.Failure, errorKind, remediation, innerException);

    /// <summary>The declaration was removed but its connectionreference record survived.</summary>
    /// <param name="logicalName">The reference that was removed.</param>
    /// <param name="environmentId">The environment still holding the record.</param>
    /// <param name="innerException">What stopped the delete.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConnectionReferenceNotDeleted(string logicalName, string environmentId, Exception innerException) => new(
        ErrorCodes.CopilotStudio.ConnectionReferenceNotDeleted,
        $"The {logicalName} declaration was removed from the workspace, but its connectionreference record could not be deleted from {environmentId}: {Describe(innerException)}",
        ExitCodes.Failure,
        CopilotErrorKinds.ConnectionReferenceNotDeleted,
        "The environment refuses to delete a record another component still depends on.",
        innerException);

    /// <summary>The record was deleted but the baseline could not be updated.</summary>
    /// <param name="logicalName">The reference that was retired.</param>
    /// <param name="innerException">What stopped the update.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException ConnectionReferenceCacheStale(string logicalName, Exception innerException) => new(
        ErrorCodes.CopilotStudio.ConnectionReferenceCacheStale,
        $"The {logicalName} declaration and its connectionreference record were both deleted, but the cloud cache could not be updated, so it still lists the reference.",
        ExitCodes.Failure,
        CopilotErrorKinds.ConnectionReferenceCacheStale,
        "Run `msagent agent pull` to re-read the agent from the environment before deploying, or the next deploy recreates the record.",
        innerException);

    /// <summary>Workflow tools have an empty workflowId.</summary>
    /// <param name="tools">The tools with an empty workflowId.</param>
    /// <returns>The typed failure.</returns>
    public static CopilotStudioException WorkflowToolIdEmpty(IReadOnlyList<string> tools) => new(
        ErrorCodes.CopilotStudio.WorkflowToolIdEmpty,
        $"The workspace has {ToolCount(tools)} with an empty workflowId: {string.Join(", ", tools)}.",
        ExitCodes.Usage,
        CopilotErrorKinds.WorkflowToolIdEmpty,
        "Set workflowId, or remove the tool from the workspace, then deploy again.");

    private static string ToolCount(IReadOnlyList<string> tools) =>
        tools.Count == 1 ? "1 workflow tool" : $"{tools.Count} workflow tools";

    private static string Appended(IReadOnlyList<string> refusals) =>
        refusals is { Count: > 0 } ? $" The service had also refused {Count(refusals)} earlier in this push." : string.Empty;

    private static string Count(IReadOnlyList<string> refusals) =>
        refusals.Count == 1 ? "1 component" : $"{refusals.Count} components";

    /// <summary>Renders an exception and its inner chain as one line.</summary>
    /// <param name="exception">The exception to describe.</param>
    /// <returns>The message, with each inner message appended.</returns>
    private static string Describe(Exception exception)
    {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", messages);
    }
}
