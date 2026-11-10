using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Builds the commands that give an agent its deployments and deploy to them.</summary>
public static class DeploymentCommandHints
{
    /// <summary>The name the suggested Dev deployment gets.</summary>
    public const string DevDeploymentName = "development";

    /// <summary>The lines that create an agent's Dev deployment and then deploy to it, as commands that run as printed.</summary>
    /// <param name="displayName">The agent's name, which the first line names.</param>
    /// <param name="agentSelector">The options that select the agent in both commands.</param>
    /// <param name="deploymentSelector">The options that select the new deployment when deploying to it.</param>
    public static IReadOnlyList<string> CreateDevDeployment(string displayName, string agentSelector, string deploymentSelector) => [$"Create a deployment for '{displayName}':", $"  {DeploymentCreate(agentSelector, DevDeploymentName, "dev")}", "Then deploy:", $"  {AgentDeploy(agentSelector, deploymentSelector)}"];

    /// <summary>The command that creates a deployment of an agent.</summary>
    /// <param name="agentSelector">The options that select the agent.</param>
    /// <param name="deploymentName">The deployment name, or a placeholder.</param>
    /// <param name="deploymentType">dev, test or prod, or a placeholder.</param>
    public static string DeploymentCreate(string agentSelector, string deploymentName, string deploymentType) => $"msagent deployment create {agentSelector} --deployment-name {deploymentName} --deployment-type {deploymentType}";

    /// <summary>The command that deploys an agent's working folder to one of its deployments.</summary>
    /// <param name="agentSelector">The options that select the agent.</param>
    /// <param name="deploymentSelector">The options that select the deployment.</param>
    public static string AgentDeploy(string agentSelector, string deploymentSelector) => $"msagent agent deploy {agentSelector} {deploymentSelector}";

    /// <summary>The command that deploys a commit from GRS to one of an agent's deployments through AMS.</summary>
    /// <param name="deploymentSelector">The options that select the deployment, or a placeholder.</param>
    /// <param name="commit">The commit to deploy, or null for the one AMS takes for the stage.</param>
    public static string RemoteDeploy(string deploymentSelector, string? commit = null) => $"msagent agent deploy --remote {deploymentSelector}{(commit is null ? string.Empty : $" --commit-id {commit}")}";

    /// <summary>A value quoted so the shell passes it to msagent as one argument.</summary>
    /// <param name="value">The value.</param>
    public static string Quoted(string value) => ManagedAgentException.CommandArgument(value);

    /// <summary>The command that shows a deployment with its state.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="deploymentId">The deployment.</param>
    public static string DeploymentShow(string agentId, string deploymentId) => $"msagent deployment show --agent-id {agentId} --deployment-id {deploymentId}";
}