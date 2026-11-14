using System.Globalization;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Input for <c>msagent agent create</c> on the MCS path.</summary>
public sealed record ManagedAgentCreateRequest
{
    /// <summary>Names the managed-agent record, and the scaffolded agent in scaffold mode.</summary>
    public string? Name { get; init; }

    /// <summary>Free-text description held on the record only.</summary>
    public string? Description { get; init; }

    /// <summary>An explicit schema name, overriding derivation from the name. Scaffold mode only.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The authoring shape to scaffold: <c>CLI</c> or <c>Classic</c>.</summary>
    public string? McsAgentType { get; init; }

    /// <summary>The scaffold template name, valid only under the classic shape.</summary>
    public string? InitTemplate { get; init; }

    /// <summary>Instructions scaffolded into the project.</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>Selects clone mode by the source agent's schema name.</summary>
    public string? CloneSchemaName { get; init; }

    /// <summary>Selects clone mode by the source agent's identifier.</summary>
    public string? CloneAgentId { get; init; }

    /// <summary>The environment holding the clone source. Required in clone mode.</summary>
    public string? CloneEnvironmentId { get; init; }

    /// <summary>Selects clone mode by the source workflow's identifier.</summary>
    public string? CloneWorkflowId { get; init; }

    /// <summary>The agent type the command creates.</summary>
    public string? AgentType { get; init; }

    /// <summary>
    /// The environment the record is registered in. The active environment when omitted.
    ///
    /// In clone mode this is the target: the source is looked up in the clone environment and the
    /// record is registered here, so the two may differ.
    /// </summary>
    public string? EnvironmentId { get; init; }

    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent agent create</c> on the MCS path.</summary>
public sealed record ManagedAgentCreateResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>agent-created</c>).</summary>
    public string Status { get; init; } = "agent-created";

    /// <summary>The managed-agent record's identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent type, always <c>MCSAgent</c> on this path.</summary>
    public required string AgentType { get; init; }

    /// <summary>The scaffold template that was written, or <see langword="null"/> in clone mode.</summary>
    public string? Template { get; init; }

    /// <summary>The agent's schema name, or <see langword="null"/> in clone mode.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The clone source's agent identifier, or <see langword="null"/> in scaffold mode.</summary>
    public string? McsAgentId { get; init; }

    /// <summary>The Dataverse workflow the record addresses, for a standalone workflow.</summary>
    public string? McsWorkflowId { get; init; }

    /// <summary>The workflow's Dataverse name, for a standalone workflow, while <see cref="DisplayName"/> stays the record's name.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>The absolute folder holding the workflow's files, for a standalone workflow.</summary>
    public string? WorkflowFolder { get; init; }

    /// <summary>Whether the workflow is activated or a draft, for a standalone workflow.</summary>
    public string? WorkflowState { get; init; }

    /// <summary>The tenant owning the record's environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The record's home environment.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The project directory, which holds no files yet in clone mode.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>Where the configuration was written.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>The scaffolded files, relative to the project directory.</summary>
    public IReadOnlyList<string> CreatedFiles { get; init; } = [];

    /// <summary>How the agent was created: <c>scaffold</c> or <c>clone</c>.</summary>
    public required string CreatedFrom { get; init; }

    /// <summary>The home deployment recorded alongside a clone, which clone mode always creates.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The home deployment's name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The home deployment's stage.</summary>
    public string? DeploymentType { get; init; }

    /// <summary>How many components the clone wrote into the project directory.</summary>
    public int? ClonedComponentCount { get; init; }

    /// <summary>The workspaces a clone wrote: the agent's, and one per component collection.</summary>
    public IReadOnlyList<CopilotClonedWorkspace> ClonedWorkspaces { get; init; } = [];

    /// <summary>The environment the clone source was read from, which may differ from the record's.</summary>
    public string? SourceEnvironmentId { get; init; }

    /// <summary>Advisories that did not stop the command, such as options ignored in clone mode.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
        AgentType == AgentTypes.McsWorkflow ? WorkflowLines() : CreatedFrom == "clone" ? CloneLines() : ScaffoldLines();

    private List<string> WorkflowLines()
    {
        var lines = new List<string>
        {
            CreatedFrom == "clone"
                ? $"Cloned workflow {TextGrid.DescribeWorkflow(WorkflowName, DisplayName)} from {McsWorkflowId} in {SourceEnvironmentId}."
                : $"Created workflow {TextGrid.DescribeWorkflow(WorkflowName, DisplayName)} in {ProjectDirectory}.",
            $"  agent id     {AgentId}",
            $"  workflow id  {McsWorkflowId}",
            $"  folder       {WorkflowFolder}",
            $"  state        {WorkflowState}",
            $"  environment  {EnvironmentId}",
            $"  config       {ConfigPath}",
        };

        if (CreatedFiles.Count > 0)
        {
            lines.Add($"  files        {CreatedFiles.Count} scaffolded");
        }

        if (DeploymentName is not null)
        {
            lines.Add($"  deployment   {DeploymentName} ({DeploymentType})");
        }

        lines.AddRange(Warnings.Select(warning => TextGrid.WarningLine("  ", warning)));

        return lines;
    }

    private List<string> CloneLines()
    {
        var lines = new List<string>
        {
            $"Cloned {SchemaName} ({DisplayName}) from {McsAgentId} in {EnvironmentId}.",
            $"  agent id    {AgentId}",
            $"  project     {ProjectDirectory}",
            $"  components  {ClonedComponentCount ?? 0} written",
            $"  deployment  {DeploymentName} ({DeploymentType})",
            $"  config      {ConfigPath}",
        };

        lines.AddRange(ClonedWorkspaces.Select(workspace => $"  {(workspace.Kind == "agent" ? "agent      " : "collection ")} {workspace.DisplayName} — {workspace.Path}"));
        lines.AddRange(Warnings.Select(warning => $"  warning     {warning}"));

        return lines;
    }

    private List<string> ScaffoldLines()
    {
        var lines = new List<string>
        {
            $"Created {SchemaName} ({DisplayName}) in {ProjectDirectory}.",
            $"  agent id  {AgentId}",
            $"  config    {ConfigPath}",
        };

        if (CreatedFiles.Count > 0)
        {
            lines.Add($"  files     {CreatedFiles.Count} scaffolded from the {Template} template");
        }

        lines.AddRange(ClonedWorkspaces.Select(workspace => $"  agent     {workspace.DisplayName} — {workspace.Path}"));
        lines.AddRange(Warnings.Select(warning => $"  warning   {warning}"));

        return lines;
    }
}

/// <summary>Input for <c>msagent agent show</c>.</summary>
public sealed record ManagedAgentShowRequest
{
    /// <summary>The record to show. Wins over <c>AgentName</c>. Every record when omitted.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record to show, by name. Used when no identifier was supplied.</summary>
    public string? AgentName { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent agent show</c>.</summary>
public sealed record ManagedAgentShowResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The project whose configuration was read.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The configuration file the records came from.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>
    /// The matching managed-agent rows, exactly as the configuration holds them.
    ///
    /// The stored record is the payload rather than a projection of it: the command exists to show what
    /// the table contains, so reshaping the rows would answer a different question.
    /// </summary>
    public IReadOnlyList<ManagedAgentRecord> Agents { get; init; } = [];

    /// <summary>
    /// The deployment rows belonging to the matching agents, exactly as the configuration holds them.
    ///
    /// A sibling array rather than a list nested inside each agent, because that is how
    /// <c>agent.config.json</c> itself stores the two tables. Each row names its agent in
    /// <see cref="AgentDeploymentRecord.AgentId"/>.
    /// </summary>
    public IReadOnlyList<AgentDeploymentRecord> Deployments { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Agents.Count == 0)
        {
            return [$"No agents are registered in {ConfigPath}."];
        }

        var lines = new List<string> { $"{Agents.Count} agent(s) registered in {ConfigPath}." };

        foreach (var agent in Agents)
        {
            lines.Add(string.Empty);
            lines.Add($"  {agent.DisplayName} ({agent.AgentId})");
            lines.Add($"    agentId        {agent.AgentId}");
            lines.Add($"    tenantId       {agent.TenantId}");
            lines.Add($"    environmentId  {agent.EnvironmentId}");
            lines.Add($"    agentType      {agent.AgentType}");
            lines.Add($"    agentLocation  {agent.AgentLocation}");
            lines.Add($"    mcsAgentId     {TextGrid.DescribeOrDash(agent.McsAgentId)}");
            lines.Add($"    mcsSchemaName  {TextGrid.DescribeOrDash(agent.McsSchemaName)}");
            lines.Add($"    description    {TextGrid.DescribeOrDash(agent.Description)}");
            lines.Add($"    origin         {TextGrid.DescribeOrDash(agent.Origin)}");
            lines.Add($"    owner          {TextGrid.DescribePrincipal(agent.Owner)}");
            lines.Add($"    createdBy      {TextGrid.DescribePrincipal(agent.CreatedBy)}");
            lines.Add($"    lastDeployedBy {TextGrid.DescribePrincipal(agent.LastDeployedBy)}");
            lines.Add($"    createdTime    {agent.CreatedTime:yyyy-MM-dd HH:mm:ssZ}");
            lines.Add($"    changedTime    {agent.ChangedTime:yyyy-MM-dd HH:mm:ssZ}");
            lines.Add($"    agentState     {agent.AgentState}");
            lines.Add($"    entityTag      {agent.EntityTag}");
            lines.AddRange(DeploymentLines(agent.AgentId));
        }

        return lines;
    }

    private IEnumerable<string> DeploymentLines(string agentId)
    {
        var owned = Deployments.Where(deployment => string.Equals(deployment.AgentId, agentId, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (owned.Length == 0)
        {
            yield return "    deployments    —";
            yield break;
        }

        yield return $"    deployments    {owned.Length}";

        foreach (var deployment in owned)
        {
            yield return string.Empty;
            yield return $"      {deployment.DeploymentName} ({deployment.DeploymentId})";
            yield return $"        agentId           {deployment.AgentId}";
            yield return $"        deploymentType    {deployment.DeploymentType}";
            yield return $"        homeEnvironmentId {deployment.HomeEnvironmentId}";
            yield return $"        environmentId     {TextGrid.DescribeOrDash(deployment.EnvironmentId)}";
            yield return $"        createdBy         {TextGrid.DescribePrincipal(deployment.CreatedBy)}";
            yield return $"        lastDeployedBy    {TextGrid.DescribePrincipal(deployment.LastDeployedBy)}";
            yield return $"        lastDeployedTime  {TextGrid.DescribeTimestamp(deployment.LastDeployedTime)}";
            yield return $"        createdTime       {deployment.CreatedTime:yyyy-MM-dd HH:mm:ssZ}";
            yield return $"        changedTime       {deployment.ChangedTime:yyyy-MM-dd HH:mm:ssZ}";
            yield return $"        isEnabled         {deployment.IsEnabled}";
            yield return $"        isQuarantined     {deployment.IsQuarantined}";
            yield return $"        quarantinedReason {TextGrid.DescribeOrDash(deployment.QuarantinedReason)}";
            yield return $"        entityTag         {deployment.EntityTag}";
        }
    }
}

/// <summary>Input for <c>msagent agent init</c>.</summary>
public sealed record ManagedAgentInitRequest
{
    /// <summary>Names the managed-agent record.</summary>
    public string? Name { get; init; }

    /// <summary>The agent type; MCSAgent and MCSWorkflow are supported.</summary>
    public string? AgentType { get; init; }

    /// <summary>The directory holding the project. Defaults to the current directory.</summary>
    public string? McsAgentSource { get; init; }

    /// <summary>The directory holding the workflow. Defaults to the current directory.</summary>
    public string? McsWorkflowSource { get; init; }
}

/// <summary>Result of <c>msagent agent init</c>.</summary>
public sealed record ManagedAgentInitResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>agent-initialized</c>).</summary>
    public string Status { get; init; } = "agent-initialized";

    /// <summary>The managed-agent record's identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent type, always <c>MCSAgent</c>.</summary>
    public required string AgentType { get; init; }

    /// <summary>The tenant owning the record's environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The record's home environment, taken from the workspace when it has one.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>Whether the project's cloud baseline supplied the environment.</summary>
    public bool Connected { get; init; }

    /// <summary>The project directory that was registered.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>Where the configuration was written.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>The Dataverse workflow the record addresses, for a standalone workflow.</summary>
    public string? McsWorkflowId { get; init; }

    /// <summary>The workflow's Dataverse name, for a standalone workflow, while <see cref="DisplayName"/> stays the record's name.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>The absolute folder holding the workflow's files, for a standalone workflow.</summary>
    public string? WorkflowFolder { get; init; }

    /// <summary>Whether the workflow is activated or a draft, for a standalone workflow.</summary>
    public string? WorkflowState { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => AgentType == AgentTypes.McsWorkflow
        ?
        [
            $"Registered workflow {TextGrid.DescribeWorkflow(WorkflowName, DisplayName)} in {ProjectDirectory}.",
            $"  agent id     {AgentId}",
            $"  workflow id  {McsWorkflowId}",
            $"  folder       {WorkflowFolder}",
            $"  state        {WorkflowState}",
            $"  environment  {EnvironmentId}",
            $"  config       {ConfigPath}",
        ]
        :
        [
            $"Registered {DisplayName} in {ProjectDirectory}.",
            $"  agent id  {AgentId}",
            Connected
                ? $"  connected to Copilot Studio in {EnvironmentId}"
                : "  not yet in Copilot Studio",
        ];
}

/// <summary>Input for <c>msagent agent list</c>.</summary>
public sealed record ManagedAgentListRequest
{
    /// <summary>Lists this environment instead of the active one.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Lists every environment the operator can reach.</summary>
    public bool Tenant { get; init; }

    /// <summary>The maximum number of rows to return.</summary>
    public int? Limit { get; init; }

    /// <summary>The agent type to list; MCSAgent when omitted.</summary>
    public string? AgentType { get; init; }

    /// <summary>Returns only rows whose name contains this text. MCSWorkflow only.</summary>
    public string? Filter { get; init; }

    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>One agent in an <c>msagent agent list</c> result.</summary>
public sealed record ManagedAgentListItem
{
    /// <summary>The managed-agent record's identifier, or <see langword="null"/> when none matches.</summary>
    public string? AgentId { get; init; }

    /// <summary>The Copilot Studio agent's own identifier in its environment, for an MCS agent.</summary>
    public string? McsAgentId { get; init; }

    /// <summary>The Dataverse workflow's identifier, for a standalone workflow.</summary>
    public string? McsWorkflowId { get; init; }

    /// <summary>The tenant owning the environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The environment the agent lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent's schema name, for an MCS agent.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The agent type, one of <see cref="AgentTypes"/>.</summary>
    public required string AgentType { get; init; }

    /// <summary>The workflow's state, for a standalone workflow.</summary>
    public string? WorkflowState { get; init; }

    /// <summary>The agent's provisioning state, drawn from the service.</summary>
    public string? LifecycleState { get; init; }

    /// <summary>When the agent last changed, as reported by the service.</summary>
    public DateTimeOffset? LastChangedOn { get; init; }

    /// <summary>How many deployments reference the matching record, or <see langword="null"/>.</summary>
    public int? DeploymentSlotCount { get; init; }
}

/// <summary>One environment that could not be read during a tenant-wide listing.</summary>
public sealed record ManagedAgentListDiagnostic
{
    /// <summary>The environment that failed.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>Why it failed.</summary>
    public required string Message { get; init; }
}

/// <summary>Result of <c>msagent agent list</c>.</summary>
public sealed record ManagedAgentListResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>agents-listed</c>).</summary>
    public string Status { get; init; } = "agents-listed";

    /// <summary>What was listed: <c>environment</c> or <c>tenant</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>The agent type that was listed.</summary>
    public string AgentType { get; init; } = AgentTypes.McsAgent;

    /// <summary>The environment listed, omitted under tenant scope.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The row limit that was applied.</summary>
    public int Limit { get; init; }

    /// <summary>Whether collection stopped at the limit.</summary>
    public bool Truncated { get; init; }

    /// <summary>The agents found, in environment order.</summary>
    public IReadOnlyList<ManagedAgentListItem> Agents { get; init; } = [];

    /// <summary>Per-environment failures, which do not fail the command.</summary>
    public IReadOnlyList<ManagedAgentListDiagnostic> Diagnostics { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var isWorkflow = AgentType == AgentTypes.McsWorkflow;
        var noun = isWorkflow ? "workflow" : "agent";

        if (Agents.Count == 0)
        {
            return
            [
                isWorkflow
                    ? "No workflows found. A workflow that has never been deployed is not listed."
                    : "No agents found. An agent that has never been deployed is not listed.",
                .. Diagnostics.Select(diagnostic => $"  {diagnostic.EnvironmentId}: {diagnostic.Message}"),
            ];
        }

        var isTenantScope = Scope == "tenant";
        var lines = new List<string> { $"{Agents.Count} {noun}(s){(Truncated ? $", truncated at --limit {Limit}" : string.Empty)}." };

        string[] headers = isWorkflow
            ? isTenantScope
                ? ["WORKFLOW ID", "NAME", "STATE", "ENVIRONMENT", "AGENT ID", "DEPLOYMENTS", "LAST CHANGED"]
                : ["WORKFLOW ID", "NAME", "STATE", "AGENT ID", "DEPLOYMENTS", "LAST CHANGED"]
            : isTenantScope
                ? ["MCS AGENT ID", "SCHEMA NAME", "DISPLAY NAME", "ENVIRONMENT", "TENANT", "STATE", "LAST CHANGED"]
                : ["MCS AGENT ID", "SCHEMA NAME", "DISPLAY NAME", "TENANT", "STATE", "LAST CHANGED"];

        lines.AddRange(TextGrid.RenderTable(
            isWorkflow ? "Workflows" : "Agents",
            headers,
            [.. Agents.Select(agent => isWorkflow ? WorkflowRow(agent, isTenantScope) : AgentRow(agent, isTenantScope))]));

        lines.AddRange(Diagnostics.Select(diagnostic => $"  {diagnostic.EnvironmentId}: {diagnostic.Message}"));

        return lines;
    }

    private static string[] WorkflowRow(ManagedAgentListItem workflow, bool isTenantScope) => isTenantScope
        ? [TextGrid.DescribeOrDash(workflow.McsWorkflowId), workflow.DisplayName, TextGrid.DescribeOrDash(workflow.WorkflowState), workflow.EnvironmentId, TextGrid.DescribeOrDash(workflow.AgentId), TextGrid.DescribeCount(workflow.DeploymentSlotCount), TextGrid.DescribeTimestamp(workflow.LastChangedOn)]
        : [TextGrid.DescribeOrDash(workflow.McsWorkflowId), workflow.DisplayName, TextGrid.DescribeOrDash(workflow.WorkflowState), TextGrid.DescribeOrDash(workflow.AgentId), TextGrid.DescribeCount(workflow.DeploymentSlotCount), TextGrid.DescribeTimestamp(workflow.LastChangedOn)];

    private static string[] AgentRow(ManagedAgentListItem agent, bool isTenantScope) => isTenantScope
        ? [TextGrid.DescribeOrDash(agent.McsAgentId), TextGrid.DescribeOrDash(agent.SchemaName), agent.DisplayName, agent.EnvironmentId, agent.TenantId, TextGrid.DescribeOrDash(agent.LifecycleState), TextGrid.DescribeTimestamp(agent.LastChangedOn)]
        : [TextGrid.DescribeOrDash(agent.McsAgentId), TextGrid.DescribeOrDash(agent.SchemaName), agent.DisplayName, agent.TenantId, TextGrid.DescribeOrDash(agent.LifecycleState), TextGrid.DescribeTimestamp(agent.LastChangedOn)];
}

/// <summary>Input for <c>msagent agent delete</c>.</summary>
public sealed record ManagedAgentDeleteRequest
{
    /// <summary>The record to delete, by identifier. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record to delete, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>Chooses which cloud agent is deleted, by deployment identifier.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>Chooses which cloud agent is deleted, by deployment name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Also deletes the flows, AI prompts, connection references, and custom connectors the agent holds.</summary>
    public bool AllResources { get; init; }

    /// <summary>Fails instead of prompting.</summary>
    public bool NonInteractive { get; init; }
    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>A cloud agent left running because its deployment's records were removed.</summary>
public sealed record ManagedAgentOrphanedTarget
{
    /// <summary>The deployment that referenced it.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The environment it lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent identifier, when it could be resolved.</summary>
    public string? McsAgentId { get; init; }

    /// <summary>The copy as one line of text.</summary>
    /// <returns>The environment, followed by what names it.</returns>
    public override string ToString() => DeploymentName == WorkflowDeleteService.HomeTargetName ? $"{EnvironmentId} (the agent's home environment, which no deployment names)" : $"{EnvironmentId} (deployment \"{DeploymentName}\")";
}

/// <summary>A flow, AI prompt, connection reference, or custom connector that <c>msagent agent delete --all-resources</c> removes with the agent.</summary>
public sealed record ManagedAgentResource
{
    /// <summary>What the resource is: <c>aiModel</c>, <c>workflow</c>, <c>connectionReference</c>, or <c>connector</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The resource's identifier, or a connection reference's logical name.</summary>
    public required string Identity { get; init; }

    /// <summary>The environment holding the resource.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The resource as one line of text.</summary>
    /// <returns>Its kind, identity, and environment.</returns>
    public override string ToString() => $"{Kind} {Identity} in {EnvironmentId}";
}

/// <summary>Result of <c>msagent agent delete</c>.</summary>
public sealed record ManagedAgentDeleteResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>agent-deleted</c>, <c>cancelled</c>, or <c>name-mismatch</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The record that was deleted.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The environment the deleted cloud agent lived in.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The cloud agent the delete targets in <see cref="Microsoft.Agents.Hosting.Cli.Core.Operations.ManagedAgentDeleteResult.EnvironmentId"/>, when one exists.</summary>
    public string? McsAgentId { get; init; }

    /// <summary>The deployment that selected the target, when one did.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The Dataverse workflow that was deleted, for a standalone workflow.</summary>
    public string? McsWorkflowId { get; init; }

    /// <summary>Whether no cloud agent had to be deleted.</summary>
    public bool McsAgentAlreadyAbsent { get; init; }

    /// <summary>How many deployment rows were removed alongside the record.</summary>
    public int RemovedAgentDeployments { get; init; }

    /// <summary>Cloud agents left running by this delete.</summary>
    public IReadOnlyList<ManagedAgentOrphanedTarget> OrphanedTargets { get; init; } = [];

    /// <summary>Advisories about the delete, such as the Copilot Studio agents that lost the deleted workflow.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The flows, AI prompts, connection references, and custom connectors an <c>--all-resources</c> delete removed.</summary>
    public IReadOnlyList<ManagedAgentResource> Resources { get; init; } = [];

    /// <summary>What the delete removes, as its confirmation lists it.</summary>
    /// <returns>The lines.</returns>
    public IReadOnlyList<string> PlanLines() =>
    [
        McsWorkflowId is null ? $"  agent        {DisplayName} ({AgentId})" : $"  workflow     {DisplayName} ({AgentId})",
        McsWorkflowId is null ? ManagedAgentDeleteTargets.CloudAgentLine(McsAgentId is null ? null : $"{McsAgentId} in {EnvironmentId}", "exists") : $"  cloud flow   {McsWorkflowId} in {EnvironmentId}",
        $"  records      1 ManagedAgent + {RemovedAgentDeployments} AgentDeployment row(s)",
        .. Warnings.Select(warning => TextGrid.WarningLine("  ", warning)),
        .. ManagedAgentDeleteTargets.Section("resources", Resources),
        .. ManagedAgentDeleteTargets.Section("orphaned by this delete", OrphanedTargets),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Status == "name-mismatch")
        {
            return
            [
                $"That does not match the agent name, so nothing was deleted. Expected '{DisplayName}'.",
                "Type the name exactly as shown, without quotes.",
            ];
        }

        if (Status == "cancelled")
        {
            return ["Cancelled. Nothing was deleted."];
        }

        var lines = new List<string> { $"Deleted {DisplayName} ({AgentId})." };

        if (McsWorkflowId is null)
        {
            lines.Add(ManagedAgentDeleteTargets.CloudAgentLine(McsAgentAlreadyAbsent ? null : $"deleted from {EnvironmentId}", "existed"));
        }
        else
        {
            lines.Add(McsAgentAlreadyAbsent ? $"  cloud flow   none — this workflow existed nowhere in {EnvironmentId}" : $"  cloud flow   deleted from {EnvironmentId}");
        }

        lines.Add($"  records      1 ManagedAgent + {RemovedAgentDeployments} AgentDeployment row(s)");
        lines.AddRange(Warnings.Select(warning => TextGrid.WarningLine("  ", warning)));
        lines.AddRange(ManagedAgentDeleteTargets.Section("resources", Resources));
        lines.AddRange(ManagedAgentDeleteTargets.Section("orphaned by this delete", OrphanedTargets));
        lines.Add("Local files were not touched.");

        return lines;
    }
}

/// <summary>Input for <c>msagent agent pull</c>.</summary>
public sealed record ManagedAgentPullRequest
{
    /// <summary>The directory to pull into. Defaults to the current directory.</summary>
    public string? Project { get; init; }

    /// <summary>The record to pull, by identifier. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record to pull, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment whose environment is pulled from. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment whose environment is pulled from, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Whether a local workflow edit the environment has not seen may be replaced. MCSWorkflow only.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Result of <c>msagent agent pull</c>.</summary>
public sealed record ManagedAgentPullResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>agent-pulled</c> or <c>up-to-date</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The record that was pulled.</summary>
    public required string AgentId { get; init; }

    /// <summary>The environment the content came from.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The directory the content landed in.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>How the content arrived: <c>pull</c> or <c>clone</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>The changes that were applied.</summary>
    public IReadOnlyList<CopilotChange> Changes { get; init; } = [];

    /// <summary>How many changes were applied.</summary>
    public int ChangeCount { get; init; }

    /// <summary>How many components a clone wrote, in clone mode only.</summary>
    public int? ComponentCount { get; init; }

    /// <summary>The workflow's Dataverse name, for a standalone workflow.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>The absolute folder the workflow's files are in after the pull, for a standalone workflow.</summary>
    public string? WorkflowFolder { get; init; }

    /// <summary>The absolute folder a rename moved the workflow out of, or <see langword="null"/> when its folder kept its name.</summary>
    public string? PreviousFolder { get; init; }

    /// <summary>Advisories that did not stop the pull, such as files left with merge conflicts.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            (Status, WorkflowName) switch
            {
                ("up-to-date", null) => $"Already up to date with {EnvironmentId}.",
                ("up-to-date", _) => $"Workflow '{WorkflowName}' is already up to date with {EnvironmentId}.",
                (_, null) => $"Pulled {ChangeCount} change(s) from {EnvironmentId} into {ProjectDirectory}.",
                _ => $"Pulled {ChangeCount} change(s) to workflow '{WorkflowName}' from {EnvironmentId} into {ProjectDirectory}.",
            },
        };

        if (PreviousFolder is not null)
        {
            lines.Add($"  renamed    {PreviousFolder} -> {WorkflowFolder}");
        }

        lines.AddRange(Warnings.Select(warning => $"  warning    {warning}"));

        return lines;
    }
}

/// <summary>Input for <c>msagent agent deploy</c> on the MCS path.</summary>
public sealed record ManagedAgentDeployRequest
{
    /// <summary>The record to deploy, by identifier. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record to deploy, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment to target, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment to target, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Overrides the project's schema name for this deploy.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Publishes the agent after a successful push.</summary>
    public bool Publish { get; init; }

    /// <summary>
    /// Overwrites cloud content that changed since the last sync. Only a dev deployment refuses
    /// without it; test and prod always overwrite.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent agent deploy</c> on the MCS path.</summary>
public sealed record ManagedAgentDeployResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>deployment-submitted</c>).</summary>
    public string Status { get; init; } = "deployment-submitted";

    /// <summary>The record that was deployed.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The deployment that was targeted.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deployment's stage.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>What the deploy did: <c>reattach</c>, <c>push</c>, or <c>retarget</c>.</summary>
    public required string Action { get; init; }

    /// <summary>The environment the content was pushed to.</summary>
    public required string TargetEnvironmentId { get; init; }

    /// <summary>The agent that was deployed to, which is never the clone source.</summary>
    public string? TargetMcsAgentId { get; init; }

    /// <summary>The schema name the deploy used, for an MCS agent.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The agent type that was deployed.</summary>
    public string? AgentType { get; init; }

    /// <summary>The Dataverse workflow that was deployed, for a standalone workflow.</summary>
    public string? McsWorkflowId { get; init; }

    /// <summary>The workflow's Dataverse name, for a standalone workflow, while <see cref="DisplayName"/> stays the record's name.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>Whether the workflow ended up activated or a draft, for a standalone workflow.</summary>
    public string? WorkflowState { get; init; }

    /// <summary>How many connection references the workflow still needs bound, for a standalone workflow.</summary>
    public int UnboundConnectionCount { get; init; }

    /// <summary>Whether a new cloud agent was created rather than an existing one updated.</summary>
    public bool TargetAgentCreated { get; init; }

    /// <summary>How many component changes the push sent and the service stored, excluding the agent's settings and icon.</summary>
    public int PushedComponentCount { get; init; }

    /// <summary>How many knowledge and attachment files the push sent.</summary>
    public int UploadedFileCount { get; init; }

    /// <summary>Workflows sent alongside the authored components.</summary>
    public int AuxiliaryChangeCount { get; init; }

    /// <summary>When the deploy completed.</summary>
    public DateTimeOffset DeployedOn { get; init; }

    /// <summary>Whether the pushed content was published, rather than left as a draft.</summary>
    public bool Published { get; init; }

    /// <summary>When the publish completed.</summary>
    public DateTimeOffset? PublishedOn { get; init; }

    /// <summary>Advisories that did not stop the deploy.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = AgentType == AgentTypes.McsWorkflow
            ? new List<string>
            {
                $"Deployed workflow {TextGrid.DescribeWorkflow(WorkflowName, DisplayName)} to {DeploymentName} ({DeploymentType}) in {TargetEnvironmentId}.",
                $"  action       {Action}{(TargetAgentCreated ? " — workflow created" : string.Empty)}",
                $"  workflow id  {McsWorkflowId}",
                $"  state        {WorkflowState}",
            }
            : new List<string>
            {
                $"Deployed {SchemaName} to {DeploymentName} ({DeploymentType}) in {TargetEnvironmentId}.",
                $"  action     {Action}{(TargetAgentCreated ? " — cloud agent created" : string.Empty)}",
                $"  agent      {TargetMcsAgentId}",
                $"  pushed     {PushedComponentCount + AuxiliaryChangeCount}{(UploadedFileCount > 0 ? $"; {UploadedFileCount} file(s) uploaded" : string.Empty)}",
                Published ? "  published  yes — the content is live" : "  published  no — the content is a draft. Re-run with --publish to make it live.",
            };

        lines.AddRange(Warnings.Select(warning => $"  warning    {warning}"));

        return lines;
    }
}

/// <summary>Input for <c>msagent deployment create</c>.</summary>
public sealed record AgentDeploymentCreateRequest
{
    /// <summary>The record the deployment belongs to.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record the deployment belongs to, by name, used when no identifier was supplied.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment's stage: <c>dev</c>, <c>test</c>, or <c>prod</c>.</summary>
    public string? DeploymentType { get; init; }

    /// <summary>The deployment's name, unique within the agent.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The target environment. Required for test and prod, rejected for dev.</summary>
    public string? EnvironmentId { get; init; }
    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>Input for <c>msagent deployment delete</c>.</summary>
public sealed record AgentDeploymentDeleteRequest
{
    /// <summary>The record that owns the deployment.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record that owns the deployment, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment to delete. Wins over <see cref="DeploymentName"/>.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment to delete, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Also delete the Copilot Studio agent the deployment targets.</summary>
    public bool DeleteDeployedMcsAgent { get; init; }

    /// <summary>Fails rather than prompting for confirmation.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>One deployment removed by <c>msagent deployment delete</c>.</summary>
public sealed record DeletedDeployment
{
    /// <summary>The deleted deployment's identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deleted deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deleted deployment's stage.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The environment the deployment targeted.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The Copilot Studio agent that was deleted with it, when one was.</summary>
    public string? DeletedMcsAgentId { get; init; }

    /// <summary>Whether the Copilot Studio agent was already gone when deletion was requested.</summary>
    public bool McsAgentAlreadyAbsent { get; init; }
}

/// <summary>Result of <c>msagent deployment delete</c>.</summary>
public sealed record AgentDeploymentDeleteResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>deployment-deleted</c>, <c>cancelled</c>, or <c>name-mismatch</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The record that owned the deployments.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The deployments that were removed, or would have been when the delete did not proceed.</summary>
    public IReadOnlyList<DeletedDeployment> Deployments { get; init; } = [];

    /// <summary>The configuration file the records were removed from.</summary>
    public string? ConfigPath { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Status is "cancelled" or "name-mismatch")
        {
            return
            [
                Status == "cancelled"
                    ? $"Cancelled. Nothing was deleted for {DisplayName}."
                    : $"The typed name did not match. Nothing was deleted for {DisplayName}.",
            ];
        }

        var lines = new List<string>
        {
            Deployments.Count == 1
                ? $"Deleted deployment {Deployments[0].DeploymentName} ({Deployments[0].DeploymentType}) of agent {DisplayName}."
                : $"Deleted {Deployments.Count} deployments of agent {DisplayName}.",
        };

        foreach (var deployment in Deployments)
        {
            lines.Add($"  {deployment.DeploymentName} ({deployment.DeploymentType}) in {deployment.EnvironmentId}");
            lines.Add(deployment.DeletedMcsAgentId is not null
                ? $"    cloud agent  {deployment.DeletedMcsAgentId} deleted"
                : deployment.McsAgentAlreadyAbsent
                    ? "    cloud agent  already absent"
                    : "    cloud agent  left in place");
        }

        lines.Add($"  config       {ConfigPath}");

        return lines;
    }
}

/// <summary>Input for <c>msagent deployment update</c>.</summary>
public sealed record AgentDeploymentUpdateRequest
{
    /// <summary>The record that owns the deployment.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record that owns the deployment, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment to update. Wins over <see cref="DeploymentName"/>.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment to update, by its current name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The deployment's new name.</summary>
    public string? NewName { get; init; }

    /// <summary>A custom endpoint, which no MCS deployment accepts.</summary>
    public string? EndpointUrl { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent deployment update</c>.</summary>
public sealed record AgentDeploymentUpdateResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>deployment-updated</c>).</summary>
    public string Status { get; init; } = "deployment-updated";

    /// <summary>The record that owns the deployment.</summary>
    public required string AgentId { get; init; }

    /// <summary>The updated deployment's identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The name the deployment had before the update.</summary>
    public required string PreviousDeploymentName { get; init; }

    /// <summary>The deployment's name after the update.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deployment's stage, which an update never changes.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The environment the deployment targets, which an update never changes.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The configuration file the record was written to.</summary>
    public required string ConfigPath { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        string.Equals(PreviousDeploymentName, DeploymentName, StringComparison.Ordinal)
            ? $"Deployment {DeploymentName} ({DeploymentType}) already had that name; nothing changed."
            : $"Renamed deployment {PreviousDeploymentName} to {DeploymentName} ({DeploymentType}).",
        $"  agent        {AgentId}",
        $"  deployment   {DeploymentId}",
        $"  environment  {EnvironmentId}",
        $"  config       {ConfigPath}",
    ];
}

/// <summary>Result of <c>msagent deployment create</c>.</summary>
public sealed record AgentDeploymentCreateResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>deployment-slot-created</c>).</summary>
    public string Status { get; init; } = "deployment-slot-created";

    /// <summary>The record the deployment belongs to.</summary>
    public required string AgentId { get; init; }

    /// <summary>The created deployment's identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deployment's stage.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The target environment, absent for a dev deployment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The environment the deployment resolves to, including a dev deployment's home.</summary>
    public required string TargetEnvironmentId { get; init; }

    /// <summary>The schema name the deployment will use, read live and not stored. Absent for a workflow.</summary>
    public string? SchemaName { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Created deployment {DeploymentName} ({DeploymentType}) for agent {AgentId}.",
        $"  environment  {TargetEnvironmentId}",
        .. SchemaName is null ? Array.Empty<string>() : [$"  schema name  {SchemaName}"],
    ];
}

/// <summary>Input for <c>msagent deployment list</c>.</summary>
public sealed record AgentDeploymentListRequest
{
    /// <summary>The record whose deployments are listed.</summary>
    public string? AgentId { get; init; }
    /// <summary>The record whose deployments are listed, by name.</summary>
    public string? AgentName { get; init; }
    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>Input for <c>msagent deployment show</c> on an MCS agent.</summary>
public sealed record AgentDeploymentShowRequest
{
    /// <summary>The record that owns the deployment.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record that owns the deployment, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment's identifier, which wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent deployment show</c> on an MCS agent.</summary>
public sealed record AgentDeploymentShowResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>deployment-slot-retrieved</c>).</summary>
    public string Status { get; init; } = "deployment-slot-retrieved";

    /// <summary>The record that owns the deployment.</summary>
    public required string AgentId { get; init; }

    /// <summary>The owning record's display name.</summary>
    public required string AgentDisplayName { get; init; }

    /// <summary>The deployment's identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deployment's stage.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The target environment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The agent's home environment.</summary>
    public string? HomeEnvironmentId { get; init; }

    /// <summary>When the deployment last deployed successfully.</summary>
    public DateTimeOffset? LastDeployedOn { get; init; }

    /// <summary>Whether the deployment may process traffic.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Whether the gateway refuses traffic for this deployment.</summary>
    public bool IsQuarantined { get; init; }

    /// <summary>Why the deployment was quarantined.</summary>
    public string? QuarantinedReason { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string> { $"Deployment {DeploymentName} ({DeploymentType}) of agent {AgentDisplayName}." };

        lines.AddRange(TextGrid.RenderTable(
            "Deployment",
            ["FIELD", "VALUE"],
            [
                ["Deployment id", DeploymentId],
                ["Agent id", AgentId],
                ["Stage", DeploymentType],
                ["Environment", TextGrid.DescribeOrDash(EnvironmentId)],
                ["Home environment", TextGrid.DescribeOrDash(HomeEnvironmentId)],
                ["Last deployed", TextGrid.DescribeTimestamp(LastDeployedOn)],
                ["Enabled", IsEnabled ? "yes" : "no"],
                ["Quarantined", IsQuarantined ? QuarantinedReason ?? "yes" : "no"],
            ]));

        return lines;
    }
}

/// <summary>One deployment in an <c>msagent deployment list</c> result.</summary>
public sealed record AgentDeploymentListItem
{
    /// <summary>The deployment's identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The deployment's stage.</summary>
    public required string DeploymentType { get; init; }

    /// <summary>The target environment, absent for a dev deployment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>When the deployment last deployed successfully.</summary>
    public DateTimeOffset? LastDeployedOn { get; init; }

    /// <summary>The agent the deployment belongs to.</summary>
    public string? AgentId { get; init; }

    /// <summary>The agent's name, when the project still registers it.</summary>
    public string? AgentName { get; init; }
}

/// <summary>Result of <c>msagent deployment list</c>.</summary>
public sealed record AgentDeploymentListResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>deployment-slots-listed</c>).</summary>
    public string Status { get; init; } = "deployment-slots-listed";

    /// <summary>The record whose deployments were listed, or null when every agent was listed.</summary>
    public string? AgentId { get; init; }

    /// <summary>The deployments, in promotion order.</summary>
    public IReadOnlyList<AgentDeploymentListItem> Slots { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Slots.Count == 0)
        {
            return [AgentId is null ? "This project has no deployments." : $"Agent {AgentId} has no deployments."];
        }

        var scoped = AgentId is not null;
        var agents = Slots.Select(slot => slot.AgentId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var lines = new List<string>
        {
            scoped
                ? $"{Slots.Count} deployment(s) for agent {AgentId}."
                : $"{Slots.Count} deployment(s) across {agents} agent(s).",
        };

        lines.AddRange(TextGrid.RenderTable(
            "Deployments",
            [
                "NAME",
                "DEPLOYMENT ID",
                .. scoped ? (string[])[] : ["AGENT", "AGENT ID"],
                "STAGE",
                "ENVIRONMENT",
                "LAST DEPLOYED",
            ],
            [.. Slots.Select(slot => (string[])
            [
                slot.DeploymentName,
                slot.DeploymentId,
                .. scoped ? (string[])[] : [TextGrid.DescribeOrDash(slot.AgentName), TextGrid.DescribeOrDash(slot.AgentId)],
                slot.DeploymentType,
                TextGrid.DescribeOrDash(slot.EnvironmentId),
                TextGrid.DescribeTimestamp(slot.LastDeployedOn),
            ])]));

        return lines;
    }
}

/// <summary>Input for <c>msagent deployment update connection</c>.</summary>
public sealed record AgentDeploymentConnectionRequest : IAgentDeploymentConnectionSelector
{
    /// <summary>The record whose deployment is updated. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record whose deployment is updated, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment to update, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment to update, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Overrides the schema used for connection discovery and binding.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Narrows the run to one connection reference, by logical name.</summary>
    public string? ConnectionReference { get; init; }

    /// <summary>Narrows the run to the references that target one connector.</summary>
    public string? Connector { get; init; }

    /// <summary>
    /// Binds every reference on the selected connector to this connection. Requires the selectors to
    /// resolve to exactly one connector.
    /// </summary>
    public string? ConnectionId { get; init; }

    /// <summary>
    /// Reports readiness with consent links instead of opening a browser. Checked before any listener
    /// is constructed, so under this flag no socket is opened and no process is started.
    /// </summary>
    public bool NonInteractive { get; init; }

    /// <summary>Whether each reference is resolved on its own instead of a connector at a time.</summary>
    public bool ByReference { get; init; }

    /// <summary>
    /// The project directory the command acts on. The current directory when omitted.
    /// </summary>
    public string? Project { get; init; }
}

/// <summary>The selectors every connection verb shares.</summary>
public interface IAgentDeploymentConnectionSelector
{
    /// <summary>The project directory the command acts on.</summary>
    string? Project { get; }

    /// <summary>The record whose deployment is addressed.</summary>
    string? AgentId { get; }

    /// <summary>The record whose deployment is addressed, by name.</summary>
    string? AgentName { get; }

    /// <summary>The deployment addressed, by identifier.</summary>
    string? DeploymentId { get; }

    /// <summary>The deployment addressed, by name.</summary>
    string? DeploymentName { get; }

    /// <summary>Overrides the schema used for connection discovery.</summary>
    string? SchemaName { get; }
}

/// <summary>Input for <c>msagent deployment show connection</c>.</summary>
public sealed record AgentDeploymentConnectionShowRequest : IAgentDeploymentConnectionSelector
{
    /// <summary>The record whose deployment is reported. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record whose deployment is reported, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment to report, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment to report, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Overrides the schema used for connection discovery.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Whether the listing reports one row per reference instead of one per connector.</summary>
    public bool ByReference { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>One connection reference and how it resolved.</summary>
public sealed record AgentDeploymentConnectionItem
{
    /// <summary>The connection reference's logical name.</summary>
    public required string ConnectionReference { get; init; }

    /// <summary>The connector the reference targets.</summary>
    public string? ConnectorId { get; init; }

    /// <summary>The identity the bound connection runs as, when the catalog reports one.</summary>
    public string? ConnectionOwner { get; init; }

    /// <summary>
    /// What the connector as a whole is waiting on, which is the same for every reference it serves.
    /// </summary>
    public string? ConnectorStatus { get; init; }

    /// <summary>
    /// What happened: <c>bound</c>, <c>already-bound</c>, <c>created</c>, <c>unbound</c>,
    /// <c>no-candidate</c>, <c>unknown</c>, or <c>undeclared</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>The connection that was bound, when one was.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>The connections that could satisfy the reference.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];

    /// <summary>The link that creates a connection, when one has to be created.</summary>
    public string? ConsentLink { get; init; }
}

/// <summary>One workflow the agent owns, and whether it is running.</summary>
public sealed record AgentDeploymentWorkflowItem
{
    /// <summary>The workflow's identifier.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>The workflow's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The workspace-relative file defining the workflow.</summary>
    public string? FilePath { get; init; }

    /// <summary>The connection references the workflow needs.</summary>
    public IReadOnlyList<string> ConnectionReferences { get; init; } = [];

    /// <summary>The state last observed in this environment, rendered through the portal label for display.</summary>
    public required string State { get; init; }
}

/// <summary>Result of <c>msagent deployment update connection</c>.</summary>
public sealed record AgentDeploymentConnectionResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>connections-updated</c> or <c>connections-previewed</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The record whose deployment was updated.</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment that was updated.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>The deployment's name.</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The environment the connections live in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>Whether the agent is ready to run: every reference is bound.</summary>
    public bool Ready { get; init; }

    /// <summary>Whether every connection reference is bound.</summary>
    public bool ConnectionsReady { get; init; }

    /// <summary>
    /// Whether every workflow that has been observed in this environment is activated.
    ///
    /// Separate from <see cref="ConnectionsReady"/> because a workflow the service declines is not a
    /// connection problem, and reporting one as the other sent operators to the wrong place.
    /// </summary>
    public bool WorkflowsReady { get; init; }

    /// <summary>Every workflow the workspace defines, with its last observed state.</summary>
    public IReadOnlyList<AgentDeploymentWorkflowItem> Workflows { get; init; } = [];

    /// <summary>Every connection reference the workspace declares.</summary>
    public IReadOnlyList<AgentDeploymentConnectionItem> Connections { get; init; } = [];

    /// <summary>
    /// The workflows activated once every reference was bound.
    ///
    /// Empty while any reference is still unbound, because the drafts uploaded at attach time are
    /// correct until then.
    /// </summary>
    public IReadOnlyList<string> ActivatedWorkflows { get; init; } = [];

    /// <summary>
    /// The workflows the service declined to activate, and why.
    ///
    /// Reported as data rather than beside the listing: the <c>Workflows</c> table already shows each
    /// one as a draft, and while any connection is unbound the service's refusal only restates that.
    /// </summary>
    public IReadOnlyList<string> RefusedWorkflows { get; init; } = [];

    /// <summary>Consent flows that did not produce a connection, and why.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Whether the table leads with the connection reference instead of the connector.</summary>
    public bool ReferenceFirst { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>();

        if (Connections.Count > 0)
        {
            lines.AddRange(ReferenceFirst ? ReferenceRows() : ConnectorRows());
        }

        if (Workflows.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(TextGrid.RenderRows(
                ["WORKFLOW", "STATUS"],
                [.. Workflows.Select(workflow => new[] { workflow.DisplayName, WorkflowStateProjection.ToPortalLabel(workflow.State) })]));
        }

        if (Diagnostics.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(Diagnostics.Select(diagnostic => $"  {diagnostic}"));
        }

        return lines.Count == 0 ? [$"Deployment {DeploymentName} declares no connection references."] : TrimLeadingBlank(lines);
    }

    private IEnumerable<string> ConnectorRows() => TextGrid.RenderRows(
        ["CONNECTOR", "STATUS", "CONNECTION", "CONNECTION REFERENCES"],
        [.. Connections
            .GroupBy(connection => TextGrid.DescribeOrDash(connection.ConnectorId), StringComparer.Ordinal)
            .SelectMany((connector, position) => ConnectorBlock(connector, position > 0))])
        .Select(line => line.TrimEnd());

    private IEnumerable<string> ReferenceRows() => TextGrid.RenderRows(
        ["CONNECTION REFERENCE", "CONNECTOR", "STATUS", "CONNECTION"],
        [.. Connections
            .OrderBy(connection => connection.ConnectionReference, StringComparer.Ordinal)
            .Select(connection => new[]
            {
                connection.ConnectionReference,
                TextGrid.DescribeOrDash(connection.ConnectorId),
                ConnectionSelectionScope.DescribeState(connection.State),
                TextGrid.DescribeOrDash(connection.ConnectionId),
            })]);

    private static IEnumerable<string[]> ConnectorBlock(IGrouping<string, AgentDeploymentConnectionItem> connector, bool separated)
    {
        if (separated)
        {
            yield return [string.Empty, string.Empty, string.Empty, string.Empty];
        }

        var references = connector.Select(connection => connection.ConnectionReference).ToArray();

        yield return [connector.Key, connector.First().ConnectorStatus ?? string.Empty, DescribeConnections(connector), references[0]];

        foreach (var reference in references.Skip(1))
        {
            yield return [string.Empty, string.Empty, string.Empty, reference];
        }
    }

    /// <summary>
    /// Names the connections the operator could pass to <c>--connection-id</c>.
    ///
    /// Shows what the references use where they are bound, and what they could use where they are
    /// not, so the one command that reports readiness also reports what would change it.
    /// </summary>
    /// <param name="connector">The references one connector serves.</param>
    /// <returns>The connections in use, or the ones on offer.</returns>
    private static string DescribeConnections(IGrouping<string, AgentDeploymentConnectionItem> connector) =>
        TextGrid.DescribeOrDash(string.Join(", ", connector
            .Select(connection => connection.ConnectionId)
            .Where(connectionId => !string.IsNullOrWhiteSpace(connectionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// Drops the separator the first table opens with, which has nothing above it to separate from.
    /// </summary>
    /// <param name="lines">The rendered lines.</param>
    /// <returns>The lines without a leading blank.</returns>
    private static List<string> TrimLeadingBlank(List<string> lines) => lines[0].Length == 0 ? lines[1..] : lines;
}

/// <summary>Input for <c>msagent deployment connection connectors</c>.</summary>
public sealed record AgentDeploymentConnectorListRequest : IAgentDeploymentConnectionSelector
{
    /// <summary>The record whose deployment names the environment. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record whose deployment names the environment, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment naming the environment, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment naming the environment, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Never used by this verb: the catalog is read per environment, not per agent.</summary>
    public string? SchemaName => null;

    /// <summary>The environment to read, instead of resolving one from a deployment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>A substring matched against display name, internal id and publisher.</summary>
    public string? Filter { get; init; }

    /// <summary>One connector's internal id, matched exactly.</summary>
    public string? Connector { get; init; }

    /// <summary>The most rows to return.</summary>
    public int? Limit { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>One connector an environment serves.</summary>
public sealed record AgentDeploymentConnectorItem
{
    /// <summary>The connector's internal id, which is what <c>--connector</c> takes.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>The connector's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The publisher the catalog names, when it names one.</summary>
    public string? Publisher { get; init; }

    /// <summary>The licensing tier the catalog names, when it names one.</summary>
    public string? Tier { get; init; }

    /// <summary>
    /// Whether a connection reference the workspace declares already targets this connector.
    ///
    /// Absent when no workspace was resolved, which reads as "not known" rather than "not used".
    /// </summary>
    public bool? InUse { get; init; }
}

/// <summary>Result of <c>msagent deployment connection connectors</c>.</summary>
public sealed record AgentDeploymentConnectorListResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public required string Status { get; init; }

    /// <summary>The environment the catalog was read from.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The limit that was applied.</summary>
    public int Limit { get; init; }

    /// <summary>Whether more connectors matched than were returned.</summary>
    public bool Truncated { get; init; }

    /// <summary>The connectors the request asked for.</summary>
    public IReadOnlyList<AgentDeploymentConnectorItem> Connectors { get; init; } = [];

    /// <summary>What the operator could not act on from the listing alone.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Connectors.Count == 0)
        {
            return [$"No connector in {EnvironmentId} matches.", .. Diagnostics.Select(diagnostic => $"  {diagnostic}")];
        }

        var lines = new List<string>(TextGrid.RenderRows(
            ["CONNECTOR", "NAME", "PUBLISHER", "TIER", "IN USE"],
            [.. Connectors.Select(connector => new[]
            {
                connector.ConnectorId,
                connector.DisplayName,
                TextGrid.DescribeOrDash(connector.Publisher),
                TextGrid.DescribeOrDash(connector.Tier),
                DescribeInUse(connector.InUse),
            })]));

        if (Truncated)
        {
            lines.Add(string.Empty);
            lines.Add($"Showing {Connectors.Count} of more. Narrow with --filter, or raise --limit above {Limit}.");
        }

        lines.AddRange(Diagnostics.Select(diagnostic => $"  {diagnostic}"));

        return lines;
    }

    private static string DescribeInUse(bool? inUse) => inUse switch
    {
        true => "in use",
        false => string.Empty,
        _ => TextGrid.DescribeOrDash(null),
    };
}

/// <summary>Input for <c>msagent deployment connection add</c>.</summary>
public sealed record AgentDeploymentConnectionCreateRequest : IAgentDeploymentConnectionSelector
{
    /// <summary>The record whose deployment names the environment. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record whose deployment names the environment, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment naming the environment, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment naming the environment, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Overrides the schema used to resolve the deployed agent.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The environment to create in, instead of resolving one from a deployment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The connector to create a connection for.</summary>
    public string? Connector { get; init; }

    /// <summary>Whether the command may not prompt or open a browser.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent deployment connection add</c>.</summary>
public sealed record AgentDeploymentConnectionCreateResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public required string Status { get; init; }

    /// <summary>The environment the connection was created in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The connector the connection serves.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>The connector's display name.</summary>
    public string? ConnectorName { get; init; }

    /// <summary>The connection that was created.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>The connection's display name, when the consent experience reported one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The command that binds this connection, carrying the selectors the request supplied.
    ///
    /// Printed so it can be run as it stands: a suggestion that drops the deployment selector is
    /// refused by the very command it suggests.
    /// </summary>
    public required string BindCommand { get; init; }

    /// <summary>What the operator should know about the connections that already existed.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Created connection {ConnectionId} for {ConnectorId} in {EnvironmentId}.",
        $"  Bind it with: {BindCommand}",
        .. Diagnostics.Select(diagnostic => $"  {diagnostic}"),
    ];
}

/// <summary>Input for <c>msagent deployment connection add</c>.</summary>
public sealed record AgentDeploymentConnectionReferenceCreateRequest : IAgentDeploymentConnectionSelector
{
    /// <summary>The record whose deployment is addressed. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record whose deployment is addressed, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment addressed, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment addressed, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Overrides the schema used to resolve the deployed agent.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The connector the new reference targets.</summary>
    public string? Connector { get; init; }

    /// <summary>Bind the new reference to this connection instead of resolving one.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>Whether the command may not prompt or open a browser.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent deployment connection add</c>.</summary>
public sealed record AgentDeploymentConnectionReferenceCreateResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public required string Status { get; init; }

    /// <summary>The environment the reference was created in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The logical name the service assigned, which components use to name the reference.</summary>
    public required string ConnectionReference { get; init; }

    /// <summary>The connector the reference targets.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>The connection it was bound to, when one was.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>The identity the bound connection runs as, when the catalog reports one.</summary>
    public string? ConnectionOwner { get; init; }

    /// <summary>
    /// Whether the reference ended up bound.
    ///
    /// A declared but unbound reference is still a success: the declaration is what was asked for,
    /// and binding is a best-effort second step that a script reads from here rather than from the
    /// exit code.
    /// </summary>
    public bool Ready { get; init; }

    /// <summary>
    /// The command that binds this reference, carrying the selectors the request supplied.
    ///
    /// Printed only where the reference ended up unbound, and runnable as it stands.
    /// </summary>
    public required string BindCommand { get; init; }

    /// <summary>Why the reference was not bound, when it was not.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Declared connection reference {ConnectionReference} for {ConnectorId} in {EnvironmentId}.",
        Ready
            ? $"  Bound to {ConnectionId}{Owner()}."
            : $"  Not bound. Bind it with: {BindCommand}",
        $"  Use it in a component by naming: {ConnectionReference}",
        .. Diagnostics.Select(diagnostic => $"  {diagnostic}"),
    ];

    private string Owner() => string.IsNullOrWhiteSpace(ConnectionOwner) ? string.Empty : $" ({ConnectionOwner})";
}

/// <summary>Input for <c>msagent deployment connection remove</c>.</summary>
public sealed record AgentDeploymentConnectionRemoveRequest : IAgentDeploymentConnectionSelector
{
    /// <summary>The record whose deployment names the environment. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record whose deployment names the environment, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The deployment naming the environment, by identifier. Wins over the name.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment naming the environment, by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>Overrides the schema used to resolve the deployed agent.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The environment holding the connection, instead of resolving one from a deployment.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The connector the connection serves. Inferred from the workspace when omitted.</summary>
    public string? Connector { get; init; }

    /// <summary>The connection to delete.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>The connection reference to remove from the workspace.</summary>
    public string? ConnectionReference { get; init; }

    /// <summary>Remove or delete even while something still uses what was named.</summary>
    public bool Force { get; init; }

    /// <summary>Whether the command may not prompt.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>The project directory the command acts on. The current directory when omitted.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent deployment connection remove</c>.</summary>
public sealed record AgentDeploymentConnectionRemoveResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public required string Status { get; init; }

    /// <summary>The environment the command acted on.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The connector the deleted connection served, when one was deleted.</summary>
    public string? ConnectorId { get; init; }

    /// <summary>The connection that was deleted, when one was.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>The connection reference that was removed, when one was.</summary>
    public string? ConnectionReference { get; init; }

    /// <summary>Whether the environment's connectionreference record was deleted with the declaration.</summary>
    public bool ConnectionReferenceRecordDeleted { get; init; }

    /// <summary>The components that were still using the reference when it was removed.</summary>
    public IReadOnlyList<string> Usages { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        .. ConnectionReference is null
            ? []
            : new[]
            {
                $"Removed connection reference {ConnectionReference} from the workspace.",
                ConnectionReferenceRecordDeleted
                    ? "  Its connectionreference record was deleted from the environment, so a later deploy will not declare it again."
                    : "  The environment held no connectionreference record for it, so only the workspace declaration was removed.",
            },
        .. Usages.Count == 0 ? [] : new[] { $"  It was still used by: {string.Join(", ", Usages)}. Update those components so they stop naming it." },
        .. ConnectionId is null
            ? []
            : new[]
            {
                $"Deleted connection {ConnectionId} for {ConnectorId} from {EnvironmentId}.",
                "  Anything outside this agent that used it stops working.",
            },
    ];
}
