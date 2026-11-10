using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Builds every Agent Management Service route the CLI calls, so a route shape changes in one place.</summary>
public static class AmsRoutes
{
    /// <summary>The API version every route carries.</summary>
    public const string ApiVersion = "1";

    private const string AgentHostingNamespace = "agenthosting";

    private const string McsAgentCollectionPath = "agents/mcs";

    /// <summary>One page of the MCS agent and standalone workflow records, optionally narrowed to one agent type or one repository.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="top">The page size.</param>
    /// <param name="agentType">MCSAgent or MCSWorkflow to list, or null for both.</param>
    /// <param name="repositoryId">The repository whose agents to list, or null for every repository.</param>
    /// <param name="skipToken">The token a previous page returned, or null for the first page.</param>
    public static Uri McsAgentRecords(AmsServiceTarget target, int top, string? agentType, string? repositoryId, string? skipToken) => Build(target, McsAgentCollectionPath, $"{Top(top)}{SkipToken(skipToken)}{Query("agentType", agentType)}{Query("repositoryId", repositoryId)}");

    /// <summary>One agent.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent.</param>
    public static Uri Agent(AmsServiceTarget target, string agentId) => Build(target, AgentPath(agentId));

    /// <summary>The MCS side of one agent, whose deletion removes the agent's AMS records and leaves its repository and Copilot Studio resources.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent.</param>
    public static Uri McsAgent(AmsServiceTarget target, string agentId) => Build(target, McsAgentPath(agentId));

    /// <summary>The MCS register action, which records an MCS agent or standalone workflow with its repository folder and deployments.</summary>
    /// <param name="target">The on-behalf-of capable data-plane endpoint.</param>
    public static Uri McsRegister(AmsServiceTarget target) => Build(target, McsAgentCollectionPath);

    /// <summary>One GRS repository and the agents AMS registered in it.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="agentFolder">The folder whose presence on the branch to report, or null.</param>
    /// <param name="includeDeployments">Whether each agent carries its deployments.</param>
    public static Uri McsRepository(AmsServiceTarget target, string repositoryId, string? agentFolder, bool includeDeployments) => Build(target, $"{McsAgentCollectionPath}/repositories/{Escape(repositoryId)}", $"{Query("agentFolder", agentFolder)}{(includeDeployments ? Query("includeDeployments", "true") : null)}");

    /// <summary>The AMS agents that hold one Copilot Studio agent or flow.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="botId">The Copilot Studio agent, or null for a flow.</param>
    /// <param name="workflowId">The flow, or null for a Copilot Studio agent.</param>
    public static Uri McsHolders(AmsServiceTarget target, string? botId, string? workflowId) => Build(target, $"{McsAgentCollectionPath}/holders", $"{Query("botId", botId)}{Query("workflowId", workflowId)}");

    /// <summary>The deploy records of one deployment, where msagent records a deploy it made from the working folder.</summary>
    /// <param name="target">The on-behalf-of capable data-plane endpoint.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="deploymentId">The deployment.</param>
    public static Uri McsDeployRecords(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{McsDeploymentPath(agentId, deploymentId)}/deploy-records");

    /// <summary>The MCS deploy action of one deployment.</summary>
    /// <param name="target">The on-behalf-of capable data-plane endpoint.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="deploymentId">The deployment.</param>
    public static Uri McsDeploy(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{McsDeploymentPath(agentId, deploymentId)}/deploy");

    /// <summary>The MCS capture action of one deployment.</summary>
    /// <param name="target">The on-behalf-of capable data-plane endpoint.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="deploymentId">The deployment whose Copilot Studio content is captured.</param>
    public static Uri McsCapture(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{McsDeploymentPath(agentId, deploymentId)}/capture");

    /// <summary>One MCS operation.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation.</param>
    public static Uri McsOperation(AmsServiceTarget target, string agentId, string operationId) => Build(target, $"{McsAgentPath(agentId)}/operations/{Escape(operationId)}");

    /// <summary>The deployment collection of one agent on the control plane, where deployments are created.</summary>
    /// <param name="target">The control-plane endpoint addressed through the agent's home environment.</param>
    /// <param name="agentId">The agent.</param>
    public static Uri Deployments(AmsServiceTarget target, string agentId) => Build(target, $"{AgentPath(agentId)}/deployments");

    /// <summary>One deployment on the control plane.</summary>
    /// <param name="target">The control-plane endpoint addressed through the agent's home environment.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="deploymentId">The deployment.</param>
    public static Uri Deployment(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{AgentPath(agentId)}/deployments/{Escape(deploymentId)}");

    private static string AgentPath(string agentId) => $"agents/{Escape(agentId)}";

    private static string McsAgentPath(string agentId) => $"{AgentPath(agentId)}/mcs";

    private static string McsDeploymentsPath(string agentId) => $"{McsAgentPath(agentId)}/deployments";

    private static string McsDeploymentPath(string agentId, string deploymentId) => $"{McsDeploymentsPath(agentId)}/{Escape(deploymentId)}";

    private static string Top(int top) => $"&$top={top.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string? SkipToken(string? skipToken) => Query("$skiptoken", skipToken);

    private static string? Query(string name, string? value) => value is null ? null : $"&{name}={Escape(value)}";

    private static Uri Build(AmsServiceTarget target, string relativePath, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var scope = target.RouteForm switch
        {
            AmsRouteForm.EnvironmentHost => string.Empty,
            AmsRouteForm.TenantHost => $"environments/{Escape(target.EnvironmentId)}/",
            AmsRouteForm.Direct => $"tenants/{Escape(target.TenantId)}/environments/{Escape(target.EnvironmentId)}/",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.RouteForm, "Unknown Agent Management Service route form."),
        };

        return new Uri(EnsureTrailingSlash(target.Endpoint), $"{AgentHostingNamespace}/{scope}{relativePath}?api-version={ApiVersion}{query}");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static Uri EnsureTrailingSlash(Uri endpoint) => endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
}
