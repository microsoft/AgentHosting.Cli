using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// A failure raised by the managed-agent registry: a missing or malformed <c>agent.config.json</c>,
/// a selector that resolves to nothing or to too much, a violated table invariant, or a concurrent
/// modification. Every factory carries remediation, because each of these is something the caller
/// can act on.
/// </summary>
public sealed class ManagedAgentException : AgentHostingException
{
    private ManagedAgentException(string message, int exitCode, string errorKind, string remediation, Exception? innerException = null)
        : base(message, exitCode, errorKind, remediation, innerException)
    {
    }

    /// <summary>No configuration exists in the directory the command was run from.</summary>
    /// <param name="searchedPath">The configuration path that was looked for.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConfigNotFound(string searchedPath) => new(
        $"No agent configuration was found at '{searchedPath}'.",
        ExitCodes.Usage,
        "config-not-found",
        "Run this command from an agent project directory, or create one with `msagent agent create`.");

    /// <summary>The configuration exists but cannot be used.</summary>
    /// <param name="path">The configuration file.</param>
    /// <param name="reason">What is wrong with it.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConfigInvalid(string path, string reason) => new(
        $"The agent configuration at '{path}' is not usable: {reason}",
        ExitCodes.Usage,
        "config-invalid",
        "Repair the file, or restore it from source control.");

    /// <summary>The configuration declares a schema this build cannot read.</summary>
    /// <param name="path">The configuration file.</param>
    /// <param name="found">The version the file declares.</param>
    /// <param name="supported">The version this build writes.</param>
    public static ManagedAgentException ConfigUnsupportedVersion(string path, int found, int supported) => new(
        $"The agent configuration at '{path}' declares schema version {found}, and this build reads versions {AgentConfigSchema.MinimumReadableVersion} to {supported}.",
        ExitCodes.Usage,
        "config-version-unsupported",
        found > supported
            ? "Upgrade the CLI to a build that understands this configuration."
            : "Restore the file from source control, or add the fields this version requires: `agentLocation` on every managed agent. Do not delete it — it is the only copy of the deployment records.");

    /// <summary>Deleting the agent a deployment targets failed, so the record was kept for a retry.</summary>
    /// <param name="displayName">The agent that owns the deployment.</param>
    /// <param name="deploymentName">The deployment that was being deleted.</param>
    /// <param name="environmentId">The environment the target lives in.</param>
    /// <param name="targetAgentId">The cloud agent that could not be deleted.</param>
    /// <param name="innerException">The failure the service reported.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException DeploymentTargetDeleteFailed(string displayName, string deploymentName, string environmentId, string targetAgentId, Exception innerException) => new(
        $"The Copilot Studio agent {targetAgentId} in {environmentId} could not be deleted, so deployment '{deploymentName}' of {displayName} was kept: {innerException.Message}",
        ExitCodes.Failure,
        "deployment-target-delete-failed",
        "Nothing was removed. Resolve the reported problem and run the same command again, or drop --delete-deployed-mcs-agent to remove only the record.",
        innerException);

    /// <summary>An agent selector matched nothing.</summary>
    /// <param name="selector">The supplied selector.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException AgentNotFound(string selector) => new(        $"No agent matching '{selector}' is registered here.",
        ExitCodes.Usage,
        "agent-not-found",
        "Run `msagent agent show` to list the agents this project holds, or register this project with `msagent agent init`.");

    /// <summary>A project root holds more than one workspace that is not registered.</summary>
    /// <param name="locations">The workspace locations that could have been meant.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceAmbiguous(IEnumerable<string> locations) => new(
        $"This directory holds more than one unregistered workspace: {string.Join(", ", locations)}.",
        ExitCodes.Usage,
        "workspace-ambiguous",
        "Point --mcs-agent-source at the workspace to register.");

    /// <summary>An agent's recorded location holds no project.</summary>
    /// <param name="agentId">The record whose workspace is missing.</param>
    /// <param name="agentLocation">The location the record names.</param>
    /// <param name="projectDirectory">The project the location is relative to.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceMissing(string agentId, string agentLocation, string projectDirectory) => new(
        $"Agent '{agentId}' records its workspace at '{agentLocation}', which holds no Copilot Studio project under {projectDirectory}.",
        ExitCodes.Usage,
        "workspace-missing",
        "Restore the workspace from source control, or remove the record with `msagent agent delete`.");

    /// <summary>The supplied agent id names a different agent than the project holds.</summary>
    /// <param name="suppliedAgentId">The identifier the caller supplied.</param>
    /// <param name="projectAgentId">The identifier the project holds.</param>
    /// <param name="projectDisplayName">The name the project holds.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException AgentIdConflictsWithProject(string suppliedAgentId, string projectAgentId, string projectDisplayName) => new(
        $"--agent-id names {suppliedAgentId}, but this project holds {projectDisplayName} ({projectAgentId}).",
        ExitCodes.Usage,
        "agent-id-conflict",
        "Nothing was changed. Drop --agent-id to use the project's own agent, or run the command against the project that holds the one you named.");

    /// <summary>The agent in the target environment came from a managed solution and cannot be written to.</summary>
    /// <param name="schemaName">The schema name that is read-only there.</param>
    /// <param name="environmentId">The environment holding it.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException TargetAgentIsReadOnly(string schemaName, string environmentId) => new(
        $"The agent {schemaName} in {environmentId} came from a managed solution, so it cannot be deployed to.",
        ExitCodes.Usage,
        "target-agent-read-only",
        "Nothing was pushed. Deploy to an environment where this agent is not installed from a managed solution.");

    /// <summary>
    /// An agent already carries the schema name in the target environment, and nothing the project holds
    /// shows it is the one this deployment owns.
    /// </summary>
    /// <param name="schemaName">The schema name that collided.</param>
    /// <param name="environmentId">The environment holding it.</param>
    /// <param name="targetAgentId">The agent that would have been adopted.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException UnverifiedAgentInTarget(string schemaName, string environmentId, string targetAgentId) => new(
        $"An agent named {schemaName} already exists in {environmentId} as {targetAgentId}, and nothing here shows it is this deployment's.",
        ExitCodes.Usage,
        "unverified-agent-in-target",
        "Nothing was pushed. Re-run with --overwrite to adopt that agent and replace its content, or with --schema-name to deploy under a name this project owns.");

    /// <summary>No selector was supplied and the configuration holds more than one agent.</summary>
    /// <param name="candidates">The display names that could have been meant.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException AgentAmbiguous(IEnumerable<string> candidates) => new(
        $"This directory registers more than one agent: {string.Join(", ", candidates)}.",
        ExitCodes.Usage,
        "agent-ambiguous",
        "Name the agent with --agent-id or --agent-name.");

    /// <summary>The agent's schema name is available from neither the project nor a clone source.</summary>
    /// <param name="agentId">The agent that has no schema name.</param>
    /// <param name="searchedPath">Where a project file was looked for.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SchemaNameUnavailable(string agentId, string searchedPath) => new(
        $"Agent '{agentId}' has no schema name: no project was found at '{searchedPath}' and the record carries no clone source.",
        ExitCodes.Usage,
        "schema-name-unavailable",
        "Run the command from the agent's project directory.");

    /// <summary>A deployment selector matched nothing.</summary>
    /// <param name="selector">The supplied selector.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SlotNotFound(string selector) => new(
        $"No deployment matching '{selector}' is registered for this agent.",
        ExitCodes.Usage,
        "deployment-not-found",
        "Run `msagent deployment list --agent-id <id>` to see the agent's deployments.");

    /// <summary>A deployment selector matched more than one deployment.</summary>
    /// <param name="selector">The supplied selector, or <see langword="null"/> when none was.</param>
    /// <param name="matches">The deployments that matched.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SlotAmbiguous(string? selector, IEnumerable<string> matches) => new(
        selector is null
            ? $"This agent has more than one deployment: {string.Join(", ", matches)}."
            : $"More than one deployment matches '{selector}': {string.Join(", ", matches)}.",
        ExitCodes.Usage,
        "deployment-ambiguous",
        selector is null
            ? "Name the deployment with --deployment-id or --deployment-name."
            : "Name the deployment with --deployment-id, or narrow it with --agent-id or --agent-name.");

    /// <summary>A deployment would violate one of the slot uniqueness rules.</summary>
    /// <param name="constraint">The rule that would be broken.</param>
    /// <param name="value">The conflicting value.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SlotAlreadyExists(string constraint, string value) => new(
        $"This agent already has a deployment with {constraint} '{value}'.",
        ExitCodes.Usage,
        "deployment-already-exists",
        "Choose a different name or stage, or delete the existing deployment first.");

    /// <summary>The record changed between being read and being written.</summary>
    /// <param name="recordId">The record that moved underneath the command.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ETagMismatch(string recordId) => new(
        $"Record '{recordId}' was modified by something else while this command was running.",
        ExitCodes.Failure,
        "etag-mismatch",
        "Re-run the command.");

    /// <summary>An MCS-only verb was aimed at a record of another agent type.</summary>
    /// <param name="agentId">The agent that was selected.</param>
    /// <param name="verb">The verb that does not apply.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException NotAnMcsAgent(string agentId, string verb) => new(
        $"`{verb}` applies only to MCS agent types, and '{agentId}' is not one.",
        ExitCodes.Usage,
        "not-an-mcs-agent",
        "Select an MCS agent, or use the CodeAgent equivalent of this command.");

    /// <summary>The selected record is not a standalone workflow.</summary>
    /// <param name="agentId">The record that was selected.</param>
    /// <param name="verb">The command being run.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException NotAnMcsWorkflow(string agentId, string verb) => new(
        $"`{verb}` applies only to MCSWorkflow records, and '{agentId}' is not one.",
        ExitCodes.Usage,
        "not-an-mcs-workflow",
        "Select a record created with --agent-type MCSWorkflow.");

    /// <summary>A workflow record carries no Dataverse workflow identifier.</summary>
    /// <param name="agentId">The record that is missing one.</param>
    /// <param name="displayName">The record's name.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowIdMissing(string agentId, string displayName) => new(
        $"The record '{displayName}' ({agentId}) carries no workflow id, so there is nothing to address in Dataverse.",
        ExitCodes.Usage,
        "workflow-id-missing",
        "Re-register the workflow with `msagent agent init --agent-type MCSWorkflow`.");

    /// <summary>A directory holds no workflow to register.</summary>
    /// <param name="workflowRoot">The directory that was searched.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowNotFound(string workflowRoot) => new(
        $"{workflowRoot} holds no workflow. A workflow lives in workflows/<name>-<id>/ and carries both workflow.json and metadata.yml.",
        ExitCodes.Usage,
        "workflow-not-found",
        "Point --mcs-workflow-source at the directory holding the workflows folder.");

    /// <summary>A directory holds more than one workflow.</summary>
    /// <param name="folders">The workflow folders that were found.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowAmbiguous(IEnumerable<string> folders) => new(
        $"This directory holds more than one workflow: {string.Join(", ", folders)}.",
        ExitCodes.Usage,
        "workflow-ambiguous",
        "Register one workflow per record. Point --mcs-workflow-source at a directory holding a single workflow.");

    /// <summary>A workflow's metadata file cannot be read.</summary>
    /// <param name="metadataPath">The file that could not be read.</param>
    /// <param name="reason">What went wrong.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowMetadataUnreadable(string metadataPath, string reason) => new(
        $"{metadataPath} could not be read as workflow metadata: {reason}",
        ExitCodes.Usage,
        "workflow-metadata-unreadable",
        "Pull the workflow again, or restore the file from source control.");

    /// <summary>The workflow a clone names does not exist in its environment.</summary>
    /// <param name="workflowId">The workflow that was requested.</param>
    /// <param name="environmentId">The environment that was searched.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowSourceNotFound(string workflowId, string environmentId) => new(
        $"No workflow with id {workflowId} exists in {environmentId}.",
        ExitCodes.Usage,
        "workflow-source-not-found",
        "Check the id with `msagent agent list`, or supply --mcs-clone-environment-id for the environment holding it.");

    /// <summary>The service refused the workflow the push sent.</summary>
    /// <param name="workflowName">The workflow that was pushed.</param>
    /// <param name="environmentId">The environment it was pushed to.</param>
    /// <param name="reason">What the service reported.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowPushFailed(string workflowName, string environmentId, string reason) => new(
        $"The workflow '{workflowName}' could not be written to {environmentId}: {reason}",
        ExitCodes.Failure,
        "workflow-push-failed",
        "Retry the operation. If it persists, check your permissions on the environment.");

    /// <summary>The environment refused the workflow because a connection reference it uses has no usable connection.</summary>
    /// <param name="workflowName">The workflow that could not be written.</param>
    /// <param name="environmentId">The environment that refused it.</param>
    /// <param name="connectionReference">The connection reference the service named.</param>
    public static ManagedAgentException WorkflowConnectionsNotReady(string workflowName, string environmentId, string connectionReference) => new(
        $"The workflow '{workflowName}' already exists in {environmentId} and the environment will not accept a change to it while '{connectionReference}' has no usable connection.",
        ExitCodes.Failure,
        "workflow-connections-not-ready",
        "Run `msagent deployment connection update` to bind the connections, then deploy again.");

    /// <summary>The workflow on disk is not the workflow the record addresses.</summary>
    /// <param name="displayName">The record that was selected.</param>
    /// <param name="registeredWorkflowId">The workflow the record addresses.</param>
    /// <param name="folderName">The workflow folder that was found.</param>
    /// <param name="folderWorkflowId">The workflow that folder holds.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowFolderAddressesAnotherWorkflow(string displayName, string registeredWorkflowId, string folderName, string folderWorkflowId) => new(
        $"'{displayName}' is registered against workflow {registeredWorkflowId}, but its folder {folderName} holds workflow {folderWorkflowId}.",
        ExitCodes.Usage,
        "workflow-folder-mismatch",
        "Restore the registered workflow's files, or register the workflow that is on disk with `msagent agent init --agent-type MCSWorkflow`.");

    /// <summary>A refused update was retried as a replacement, and the original workflow could not be put back.</summary>
    /// <param name="workflowName">The workflow that was lost.</param>
    /// <param name="environmentId">The environment it was lost from.</param>
    /// <param name="writeFailure">Why writing the new copy failed.</param>
    /// <param name="restoreFailure">Why restoring the original failed.</param>
    public static ManagedAgentException WorkflowReplaceLostTheCloudCopy(string workflowName, string environmentId, string writeFailure, string restoreFailure) => new(
        $"The workflow '{workflowName}' no longer exists in {environmentId}. The environment refused an update, so it was deleted and written again, but writing it back failed ({writeFailure}) and so did restoring the original ({restoreFailure}).",
        ExitCodes.Failure,
        "workflow-replace-lost-the-cloud-copy",
        "Deploy again to recreate the workflow from the local copy, which is unchanged.");

    /// <summary>The project a clone would write over holds a workflow edit the environment has not seen.</summary>
    /// <param name="projectFolder">The folder that would have been written over.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowProjectHoldsUnpushedChanges(string projectFolder) => new(
        $"The folder '{projectFolder}' already holds this workflow with changes the environment has not seen.",
        ExitCodes.Usage,
        "workflow-project-holds-unpushed-changes",
        "Deploy or discard the local changes, or clone into a different --project directory.");

    /// <summary>The workspace a download would write over holds a workflow edit the environment has not seen.</summary>
    /// <param name="folderName">The workflow folder that would have been replaced.</param>
    /// <returns>The failure.</returns>
    public static ManagedAgentException WorkflowLocalChangesWouldBeLost(string folderName) => new(
        $"'{folderName}' holds changes the environment has not seen, and a pull would replace them.",
        ExitCodes.Usage,
        "workflow-local-changes-would-be-lost",
        "Run `msagent agent deploy` to push them, or pull again with --overwrite to discard them.");

    /// <summary>The supplied <c>--role</c> names no known access level.</summary>
    /// <param name="supplied">The value that was supplied.</param>
    /// <param name="accepted">The spellings that are accepted.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareRoleInvalid(string? supplied, IEnumerable<string> accepted) => new(
        string.IsNullOrWhiteSpace(supplied)
            ? $"--role is required, and must be one of: {string.Join(", ", accepted)}."
            : $"'{supplied.Trim()}' is not a share role. Valid roles are: {string.Join(", ", accepted)}.",
        ExitCodes.Usage,
        "share-role-invalid",
        "Re-run with `--role use` to grant chat access, or `--role edit` to grant collaborative authoring.");

    /// <summary>No user was supplied to share with or revoke from.</summary>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SharePrincipalSelectorRequired() => new(
        "--user is required.",
        ExitCodes.Usage,
        "share-principal-selector-required",
        "Supply `--user <sign-in-address-or-object-id>`. To change org-wide access alone, use `--share-with-org` with no user.");

    /// <summary>No agent was supplied to share, show, or revoke.</summary>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareAgentSelectorRequired() => new(
        "--agent-id or --agent-name is required.",
        ExitCodes.Usage,
        "share-agent-selector-required",
        "Supply `--agent-id <id>` or `--agent-name <name>`. `msagent agent list` prints both.");

    /// <summary>A role was supplied where no role applies.</summary>
    /// <param name="supplied">The value that was supplied.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareRoleNotApplicable(string? supplied) => new(
        $"--role '{supplied?.Trim()}' applies to --user. Org-wide sharing grants use access to everyone and takes no role.",
        ExitCodes.Usage,
        "share-role-not-applicable",
        "Drop --role, or name the user it applies to with `--user <sign-in-address-or-object-id>`.");

    /// <summary>The service did not record the access the grant asked for.</summary>
    /// <param name="displayName">The agent that was shared.</param>
    /// <param name="differences">How the state the service reports differs from the one that was asked for.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareNotApplied(string displayName, IReadOnlyList<string> differences) => new(
        $"The service accepted the request but reports a different state for {displayName}, so the share cannot be confirmed: {string.Join("; ", differences)}.",
        ExitCodes.Failure,
        "share-not-applied",
        "Sharing is written whole, so part of the request may have taken effect. Run `msagent agent share show` to see what access the agent actually has before running anything else.");

    /// <summary>The service did not remove the access the revoke asked for.</summary>
    /// <param name="principalName">The principal the revoke named.</param>
    /// <param name="displayName">The agent that was unshared.</param>
    /// <param name="differences">How the state the service reports differs from the one that was asked for.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareNotRemoved(string principalName, string displayName, IReadOnlyList<string> differences) => new(
        $"The service accepted the request to remove {principalName} from {displayName} but reports a different state, so the removal cannot be confirmed: {string.Join("; ", differences)}.",
        ExitCodes.Failure,
        "share-not-removed",
        "Sharing is written whole, so part of the request may have taken effect. Run `msagent agent share show` to see what access the agent actually has before running anything else.");

    /// <summary>The agent this record names is not in the environment its deployments point at.</summary>
    /// <param name="displayName">The record's name.</param>
    /// <param name="agentId">The record's identifier.</param>
    /// <param name="environmentId">The environment that was searched.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareTargetNotInEnvironment(string displayName, string agentId, string environmentId) => new(
        $"{displayName} ({agentId}) has no Copilot Studio agent in environment '{environmentId}', which is where its most recent deployment went.",
        ExitCodes.Usage,
        "share-target-not-in-environment",
        "Run `msagent agent deploy` to deploy it there, or `msagent deployment list` to see which environments it actually reached.");

    /// <summary>The user selector matched nobody in the tenant directory.</summary>
    /// <param name="selector">The value that was supplied.</param>
    /// <param name="tenantId">The tenant that was searched.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SharePrincipalNotFound(string selector, string tenantId) => new(
        $"No user with sign-in address or email address '{selector}' exists in tenant '{tenantId}'.",
        ExitCodes.Usage,
        "share-principal-not-found",
        "Check the address for typos, or supply the user's Entra object id.");

    /// <summary>The email address belongs to more than one user in the tenant directory, so it names nobody in particular.</summary>
    /// <param name="selector">The value that was supplied.</param>
    /// <param name="tenantId">The tenant that was searched.</param>
    /// <param name="signInAddresses">The sign-in addresses of users it matched. The search stops once it has found two.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SharePrincipalAmbiguous(string selector, string tenantId, IEnumerable<string> signInAddresses) => new(
        $"'{selector}' is the email address of more than one user in tenant '{tenantId}', including {string.Join(", ", signInAddresses)}.",
        ExitCodes.Usage,
        "share-principal-ambiguous",
        "Supply the sign-in address of the user you mean or their Entra object id.");

    /// <summary>The tenant directory could not answer which user an address names.</summary>
    /// <param name="selector">The value that was supplied.</param>
    /// <param name="detail">What the directory reported.</param>
    /// <param name="innerException">The directory failure, when there was one.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException SharePrincipalLookupFailed(string selector, string detail, Exception? innerException = null) => new(
        $"Microsoft Graph could not resolve '{selector}' to an Entra object id: {detail}",
        ExitCodes.Failure,
        "share-principal-lookup-failed",
        "Retry the command, or supply the Entra object id instead.",
        innerException);

    /// <summary>The owner's access was targeted, which sharing cannot change.</summary>
    /// <param name="principalName">The principal that owns the agent.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareOwnerNotChangeable(string principalName) => new(
        $"{principalName} owns this agent, and an owner's access cannot be granted or revoked by sharing.",
        ExitCodes.Usage,
        "share-owner-not-changeable",
        "Transfer ownership in Copilot Studio if the owner needs to change. Sharing only grants and revokes access for other principals.");

    /// <summary>The service reports one principal twice, so no change can name which entry it meant.</summary>
    /// <param name="displayName">The agent whose sharing was read.</param>
    /// <param name="duplicates">The principals listed more than once.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ShareStateAmbiguous(string displayName, IEnumerable<string> duplicates) => new(
        $"The service lists {string.Join(", ", duplicates)} more than once against {displayName}, so sharing cannot say which entry a change was meant for.",
        ExitCodes.Failure,
        "share-state-ambiguous",
        "Sharing is written whole, so changing one entry would rewrite the other. Resolve the duplicate access in Copilot Studio, then re-run.");

    /// <summary>The directory holds no MCS project to register.</summary>
    /// <param name="projectDirectory">The directory that was inspected.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ProjectNotFound(string projectDirectory) => new(
        $"{projectDirectory} holds no MCS project.",
        ExitCodes.Usage,
        "project-not-found",
        "`msagent agent init` registers a project that already exists; `msagent agent create` scaffolds a new project into an empty directory.");

    /// <summary>The project already carries a managed-agent record.</summary>
    /// <param name="displayName">The existing record's name.</param>
    /// <param name="agentId">The existing record's identifier.</param>
    /// <param name="configPath">Where the record lives.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException AlreadyRegistered(string displayName, string agentId, string configPath) => new(
        $"This project is already registered as {displayName} ({agentId}) in '{configPath}'.",
        ExitCodes.Usage,
        "already-registered",
        "Use the existing record, or delete it with `msagent agent delete` before registering again.");

    /// <summary>The clone source could not be found in the environment it was named in.</summary>
    /// <param name="selector">The schema name or identifier that was looked up.</param>
    /// <param name="environmentId">The environment that was searched.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException CloneSourceNotFound(string selector, string environmentId) => new(
        $"No agent matching '{selector}' exists in {environmentId}.",
        ExitCodes.Usage,
        "clone-source-not-found",
        $"Check the name with `msagent agent list --environment-id {environmentId}`, or clone by id with --mcs-clone-agent-id.");

    /// <summary>The agent the record expects in the target environment is not there.</summary>
    /// <param name="schemaName">The schema name that was looked for.</param>
    /// <param name="environmentId">The environment that was searched.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException TargetAgentMissing(string schemaName, string environmentId) => new(
        $"The agent {schemaName} is expected in {environmentId} but is not there.",
        ExitCodes.Usage,
        "target-agent-missing",
        "The agent has been deleted. Start again with `msagent agent create` or `msagent agent init`.");

    /// <summary>The agent exists nowhere remotely, so there is nothing to pull.</summary>
    /// <param name="displayName">The record's name.</param>
    /// <param name="agentId">The record's identifier.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException NotDeployedYet(string displayName, string agentId) => new(
        $"{displayName} ({agentId}) has not been deployed, so it does not exist in Copilot Studio yet.",
        ExitCodes.Usage,
        "not-deployed-yet",
        "Run `msagent deployment create` and then `msagent agent deploy` to create the agent, then pull.");

    /// <summary>The workspace still declares a reference the service would not remove.</summary>
    /// <param name="logicalName">The reference that survives.</param>
    /// <param name="workspaceDirectory">The workspace holding the declaration.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionReferenceNotRemoved(string logicalName, string workspaceDirectory) => new(
        $"Connection reference {logicalName} was not removed from {workspaceDirectory}.",
        ExitCodes.Failure,
        "connection-reference-not-removed",
        "Check that the workspace declares it and that its files are writable, then retry.");

    /// <summary>The workspace holds no connection reference under the name the request gave.</summary>
    /// <param name="logicalName">The name that matched nothing.</param>
    /// <param name="workspaceDirectory">The workspace that was searched.</param>
    /// <param name="nearMatches">The declared names close enough to be the one meant.</param>
    /// <param name="listCommand">The command that lists what the workspace declares.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionReferenceNotDeclared(string logicalName, string workspaceDirectory, IReadOnlyList<string> nearMatches, string listCommand) => new(
        $"Connection reference {logicalName} is not declared in {workspaceDirectory}.",
        ExitCodes.Usage,
        "connection-reference-not-declared",
        nearMatches is { Count: > 0 }
            ? $"The workspace declares {string.Join(", ", nearMatches)}. Re-run with the full name."
            : $"Run `{listCommand}` to see the references the workspace declares.");

    /// <summary>The reference was declared, but a later step failed before it could be bound.</summary>
    /// <param name="logicalName">The reference that now exists.</param>
    /// <param name="connectorId">The connector it targets.</param>
    /// <param name="failure">What went wrong after the declaration was made.</param>
    /// <param name="bindCommand">The command that would bind it.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionReferenceNotBound(string logicalName, string connectorId, Exception failure, string bindCommand) => new(
        $"Connection reference {logicalName} was declared for {connectorId}, but binding it failed: {failure.Message}",
        ExitCodes.Failure,
        "connection-reference-not-bound",
        $"The reference exists, so do not declare another. Bind it with `{bindCommand}`.",
        failure);

    /// <summary>
    /// The declaration failed, and the environment may already hold a reference the workspace does not.
    /// </summary>
    /// <param name="connectorId">The connector the declaration targeted.</param>
    /// <param name="environmentId">The environment the declaration was made in.</param>
    /// <param name="failure">What went wrong while declaring.</param>
    /// <param name="listCommand">The command that reports what the workspace declares.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionReferenceNotDeclared(string connectorId, string environmentId, Exception failure, string listCommand) => new(
        $"No connection reference was declared for {connectorId} in {environmentId}: {failure.Message}",
        ExitCodes.Failure,
        "connection-reference-not-declared",
        $"The reference is created in the environment before it is written to the workspace, so retrying may leave an unused reference behind in {environmentId}. Run `{listCommand}` to see what the workspace declares before retrying.",
        failure);

    /// <summary>The service refused to delete a connection.</summary>
    /// <param name="connectionId">The connection that survives.</param>
    /// <param name="environmentId">The environment holding it.</param>
    /// <param name="statusCode">What the service answered with.</param>
    /// <param name="detail">The service's own message, when it sent one.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionNotDeleted(string connectionId, string environmentId, int statusCode, string? detail) => new(
        $"Connection {connectionId} in {environmentId} was not deleted: the service answered {statusCode}.{Describe(detail)}",
        ExitCodes.Failure,
        "connection-not-deleted",
        "Check that you own the connection and that nothing outside this agent still holds it, then retry.");

    /// <summary>The reference was removed, and then the connection deletion did not succeed.</summary>
    /// <param name="connectionId">The connection that may survive.</param>
    /// <param name="logicalName">The reference this run already removed.</param>
    /// <param name="failure">What the deletion reported, or the timeout that left it unsettled.</param>
    /// <param name="retryCommand">The command that completes the deletion on its own.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionNotDeletedAfterReferenceRemoved(string connectionId, string logicalName, Exception failure, string retryCommand) => new(
        failure is TimeoutException
            ? $"Connection reference {logicalName} was removed, but the request to delete connection {connectionId} timed out, so whether the service deleted it is unknown: {failure.Message}"
            : $"Connection reference {logicalName} was removed, but connection {connectionId} was not deleted: {failure.Message}",
        ExitCodes.Failure,
        "connection-not-deleted",
        $"The reference is already gone, so do not pass --connection-reference again. Deleting an absent connection succeeds, so retry with `{retryCommand}`.",
        failure);

    /// <summary>A connection was created for a sign-in that failed, and could not be removed afterwards.</summary>
    /// <param name="connectionId">The connection that survives.</param>
    /// <param name="environmentId">The environment holding it.</param>
    /// <param name="detail">What stopped the removal.</param>
    /// <param name="reason">What ended the sign-in, already fit for an operator to read.</param>
    /// <param name="removeCommand">The command that deletes the surviving connection, built by the caller that knows its connector.</param>
    /// <param name="cause">The failure that ended the sign-in.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionNotRemoved(string connectionId, string environmentId, string detail, string reason, string removeCommand, Exception cause) => new(
        $"Signing in to the new connection did not complete: {reason} Connection {connectionId} in environment {environmentId} was then not removed: {detail}",
        ExitCodes.Failure,
        "connection-not-deleted",
        $"Delete it with `{removeCommand}` before retrying, so a half-made connection is not left behind.",
        cause);

    private static string Describe(string? detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail.Trim()}";

    /// <summary>The consent experience did not produce a connection.</summary>
    /// <param name="connectorId">The connector a connection was wanted for.</param>
    /// <param name="environmentId">The environment it would have been created in.</param>
    /// <param name="reason">What the consent experience reported.</param>
    /// <param name="bindCommand">The command that would bind one, once it exists.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConnectionNotCreated(string connectorId, string environmentId, string reason, string bindCommand) => new(
        $"No connection was created for connector {connectorId} in {environmentId}: {reason}",
        ExitCodes.Failure,
        "connection-not-created",
        $"Run the command again to sign in, or bind a connection you already have with `{bindCommand}`.");

    /// <summary>The directory holds files but is not connected to Copilot Studio.</summary>
    /// <param name="projectDirectory">The directory that was inspected.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceDisconnected(string projectDirectory) => new(
        $"{projectDirectory} holds a project that is not connected to Copilot Studio.",
        ExitCodes.Usage,
        "workspace-disconnected",
        "Run `msagent agent deploy` to create the agent and connect this directory, or pull into an empty directory instead.");

    /// <summary>The workspace is bound to a different agent than the selector resolved to.</summary>
    /// <param name="workspaceAgentId">The agent the workspace is bound to.</param>
    /// <param name="resolvedAgentId">The agent the selector resolved to.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceIdentityMismatch(string workspaceAgentId, string resolvedAgentId) => new(
        $"This directory is connected to agent {workspaceAgentId}, but the selector resolves to {resolvedAgentId}.",
        ExitCodes.Usage,
        "workspace-identity-mismatch",
        "Pull into the directory that matches the agent, or drop the selector to use this directory's own agent.");

    /// <summary>The workspace is bound to an agent that is not the one the workspace describes.</summary>
    /// <param name="expectedSchemaName">The schema name the workspace deploys under.</param>
    /// <param name="boundSchemaName">The schema name of the agent the workspace is bound to.</param>
    /// <param name="boundAgentId">The agent the workspace is bound to.</param>
    /// <param name="projectDirectory">The workspace directory.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceIdentityMismatch(string expectedSchemaName, string boundSchemaName, string boundAgentId, string projectDirectory) => new(
        $"{projectDirectory} deploys as '{expectedSchemaName}' but its .mcs binding points at '{boundSchemaName}' ({boundAgentId}), so a push would overwrite a different agent.",
        ExitCodes.Usage,
        "workspace-identity-mismatch",
        $"Drop --schema-name if you supplied one, or delete the workspace's .mcs directory and re-run the deploy to rebind it to '{expectedSchemaName}'.");

    /// <summary>The workspace does not record a schema name, so the agent it deploys as is unknown.</summary>
    /// <param name="workspaceDirectory">The workspace directory.</param>
    /// <param name="agentLocation">The workspace folder being registered.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceSchemaUnreadable(string workspaceDirectory, string agentLocation) => new(
        $"The project file in {workspaceDirectory} does not supply a readable schemaName, so '{agentLocation}' cannot be registered against the agent it deploys as.",
        ExitCodes.Usage,
        "workspace-schema-unreadable",
        "Restore the schemaName in the workspace's project file, then re-run `msagent agent init`.");

    /// <summary>The workspace's schema name could not be read, so its identity cannot be proven.</summary>
    /// <param name="workspaceDirectory">The workspace directory.</param>
    /// <param name="agentId">The record being acted on.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkspaceSchemaUnknown(string workspaceDirectory, string agentId) => new(
        $"{workspaceDirectory} does not record a schema name, so the cloud agent its .mcs directory points at cannot be confirmed to belong to {agentId}.",
        ExitCodes.Usage,
        "workspace-schema-unknown",
        "Restore the schemaName in the workspace's project file, then re-run. Delete the agent from the Copilot Studio portal first if the workspace cannot be repaired.");

    /// <summary>The cloud content was pushed, but the deployment record could not be updated.</summary>
    /// <param name="schemaName">The agent that was pushed.</param>
    /// <param name="environmentId">The environment it was pushed to.</param>
    /// <param name="targetAgentId">The cloud agent that now holds the content.</param>
    /// <param name="deploymentName">The deployment that was targeted.</param>
    /// <param name="innerException">The failure that stopped the record from being written.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException DeployRecordedNowhere(string schemaName, string environmentId, string targetAgentId, string deploymentName, Exception innerException) => new(
        $"Agent '{schemaName}' ({targetAgentId}) was pushed to {environmentId}, but deployment '{deploymentName}' could not be marked as deployed: {innerException.Message}",
        ExitCodes.Failure,
        "deploy-recorded-nowhere",
        $"The cloud content is already updated. Re-run `msagent agent deploy --deployment-name {deploymentName}` to record it; the push is idempotent, so nothing is sent twice that the service still holds.",
        innerException);

    /// <summary>The connections were bound, but the workflows depending on them stayed drafts.</summary>
    /// <param name="agentId">The cloud agent whose workflows could not be activated.</param>
    /// <param name="environmentId">The environment the agent lives in.</param>
    /// <param name="innerException">The failure that stopped the activation.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException WorkflowActivationFailed(string agentId, string environmentId, Exception innerException) => new(
        $"The connections for agent {agentId} in {environmentId} were bound, but the workflows depending on them could not be activated: {innerException.Message}",
        ExitCodes.Failure,
        "workflow-activation-failed",
        "The bindings are saved. Re-run `msagent deployment connection update` to activate the workflows once the cause is resolved; the agent is not ready until they are active.",
        innerException);

    /// <summary>The clone's agent was registered but its deployment could not be written.</summary>
    /// <param name="displayName">The agent that was rolled back.</param>
    /// <param name="innerException">The failure that stopped the write.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException CloneNotRegistered(string displayName, Exception innerException) => new(
        $"The clone of {displayName} could not be registered: {innerException.Message}",
        ExitCodes.Failure,
        "clone-not-registered",
        "The record was removed, so nothing is half-created. Re-run once the cause is resolved.",
        innerException);

    /// <summary>The clone source could not be written into the project directory.</summary>
    /// <param name="mcsAgentId">The source agent being cloned.</param>
    /// <param name="sourceEnvironmentId">The environment holding the source agent.</param>
    /// <param name="projectDirectory">The directory the content belongs in.</param>
    /// <param name="innerException">The failure that stopped the download.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException CloneNotMaterialised(string mcsAgentId, string sourceEnvironmentId, string projectDirectory, Exception innerException) => new(
        $"The clone of {mcsAgentId} in {sourceEnvironmentId} could not be written to {projectDirectory}: {innerException.Message}",
        ExitCodes.Failure,
        "clone-not-materialised",
        "Nothing was registered and nothing was sent to Copilot Studio. Re-run the same command once the cause is resolved.",
        innerException);

    /// <summary>The cloud agent was deleted but its records could not be removed.</summary>    /// <param name="agentId">The record that survived.</param>
    /// <param name="environmentId">The environment the cloud agent was deleted from.</param>
    /// <param name="reason">Why the record write failed.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException RecordsSurvivedCloudDelete(string agentId, string environmentId, string reason) => new(
        $"The cloud agent in {environmentId} was deleted, but the records for '{agentId}' could not be removed: {reason}",
        ExitCodes.Failure,
        "records-survived-cloud-delete",
        "Re-run `msagent agent delete`; deleting an already-absent cloud agent succeeds, so the records will be removed.");

    /// <summary>Another process holds the configuration write lock.</summary>
    /// <param name="path">The configuration file.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException ConfigLocked(string path) => new(
        $"Another AgentHost command is writing {path}.",
        ExitCodes.Failure,
        "config-locked",
        "Wait for the other command to finish, then run this one again.");

    /// <summary>Content was pushed but publishing it did not complete.</summary>
    /// <param name="schemaName">The agent that was pushed to.</param>
    /// <param name="environmentId">The environment it lives in.</param>
    /// <param name="deploymentName">The deployment that was targeted.</param>
    /// <param name="reason">Why publishing failed.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException PublishFailedAfterPush(string schemaName, string environmentId, string deploymentName, string reason) => new(
        $"The agent was deployed, but publishing it failed: {reason} Agent {schemaName} in {environmentId}, deployment {deploymentName}.",
        ExitCodes.Failure,
        "publish-failed-after-push",
        "The pushed content is saved as a draft. Re-run the same command to publish it, or publish from the Copilot Studio portal.");

    /// <summary>The push landed but did not fully land, so the requested publish was withheld.</summary>
    /// <param name="schemaName">The agent that was deployed.</param>
    /// <param name="environmentId">The environment it lives in.</param>
    /// <param name="deploymentName">The deployment that was targeted.</param>
    /// <param name="warnings">What the push did not land.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException PublishWithheldAfterIncompletePush(string schemaName, string environmentId, string deploymentName, IReadOnlyList<string> warnings) => new(
        $"The agent was deployed, but --publish was withheld because the push did not fully land: {string.Join(" ", warnings)} Agent {schemaName} in {environmentId}, deployment {deploymentName}.",
        ExitCodes.Failure,
        "publish-withheld-incomplete-push",
        "The pushed content is saved as a draft, so nothing live changed. Resolve the reported items — `msagent deployment connection update` binds refused flows — then re-run with --publish.");

    /// <summary>
    /// Content was pushed to a cloud agent that this deploy created or adopted, and a later stage
    /// failed. Names what already exists remotely, because the agent looks deployed.
    /// </summary>
    /// <param name="stage">The stage that failed.</param>
    /// <param name="schemaName">The agent that was being deployed.</param>
    /// <param name="environmentId">The environment it lives in.</param>
    /// <param name="targetDescription">How the deploy came by that agent.</param>
    /// <param name="innerException">The underlying failure.</param>
    /// <returns>The typed failure.</returns>
    public static ManagedAgentException DeployIncomplete(string stage, string schemaName, string environmentId, string targetDescription, Exception innerException) => new(
        $"The {stage} stage of the deploy failed. Agent {schemaName} {targetDescription} in {environmentId}. {DescribeChain(innerException)}",
        ExitCodes.Failure,
        "deploy-incomplete",
        "The deploy is not recorded, so the deployment still shows as never deployed. Re-running is safe: an existing agent is adopted rather than duplicated.",
        innerException);

    /// <summary>Renders an exception and its inner chain as one line.</summary>
    /// <param name="exception">The exception to describe.</param>
    /// <returns>The message, with each inner message appended.</returns>
    public static string DescribeChain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current is AgentHostingException ? current.Message : $"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", messages);
    }
}
