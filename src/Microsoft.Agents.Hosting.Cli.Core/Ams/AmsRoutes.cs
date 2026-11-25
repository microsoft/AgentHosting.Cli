using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Builds every Agent Management Service route that code-agent commands call, so a route shape changes in one place.</summary>
public static class AmsRoutes
{
    /// <summary>The API version every route carries.</summary>
    public const string ApiVersion = "1";

    private const int ListPageSize = 100;

    /// <summary>Returns the agent collection, where agents are created.</summary>
    public static Uri Agents(AmsServiceTarget target) => Build(target, "agents");

    /// <summary>Returns the page of the agent list that a skip token names, with as many agents as a page can hold.</summary>
    public static Uri AgentListPage(AmsServiceTarget target, string? skipToken) => BuildListPage(target, "agents", skipToken);

    /// <summary>Returns one agent.</summary>
    public static Uri Agent(AmsServiceTarget target, string agentId) => Build(target, AgentPath(agentId));

    /// <summary>Returns the route that deletes one agent with its deployments and bots.</summary>
    public static Uri AgentCascadeDelete(AmsServiceTarget target, string agentId) => Build(target, $"{AgentPath(agentId)}/cascadeDelete");

    /// <summary>Returns the package collection of one agent, where packages are uploaded.</summary>
    public static Uri Packages(AmsServiceTarget target, string agentId) => Build(target, $"{AgentPath(agentId)}/packages");

    /// <summary>Returns the page of one agent's package list that a skip token names, with as many packages as a page can hold.</summary>
    public static Uri PackageListPage(AmsServiceTarget target, string agentId, string? skipToken) => BuildListPage(target, $"{AgentPath(agentId)}/packages", skipToken);

    /// <summary>Returns the deployment collection of one agent, where deployments are created.</summary>
    public static Uri Deployments(AmsServiceTarget target, string agentId) => Build(target, $"{AgentPath(agentId)}/deployments");

    /// <summary>Returns the page of one agent's deployment list that a skip token names, with as many deployments as a page can hold.</summary>
    public static Uri DeploymentListPage(AmsServiceTarget target, string agentId, string? skipToken) => BuildListPage(target, $"{AgentPath(agentId)}/deployments", skipToken);

    /// <summary>Returns one deployment of an agent.</summary>
    public static Uri Deployment(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, DeploymentPath(agentId, deploymentId));

    /// <summary>Returns the action that puts what a deployment record holds live.</summary>
    public static Uri DeploymentDeploy(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/deploy");

    /// <summary>Returns the channel types AMS can create and edit on a deployment.</summary>
    public static Uri ChannelTypes(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/channelTypes");

    /// <summary>Returns the channel collection of a deployment's bot, where channels are listed and created.</summary>
    public static Uri Channels(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/channels");

    /// <summary>Returns one channel of a deployment's bot.</summary>
    public static Uri Channel(AmsServiceTarget target, string agentId, string deploymentId, string channelType) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/channels/{Escape(channelType)}");

    /// <summary>Returns the route that issues Direct Line tokens for a deployment's bot.</summary>
    public static Uri DirectLineToken(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/channels/directline/token");

    /// <summary>Returns the route that hands off the webhook values of one channel of a deployment's bot.</summary>
    public static Uri ChannelWebhook(AmsServiceTarget target, string agentId, string deploymentId, string channelType) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/channels/{Escape(channelType)}/webhook");

    /// <summary>Returns the OAuth providers AMS can create connections for on a deployment.</summary>
    public static Uri OAuthProviders(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/oauthProviders");

    /// <summary>Returns the OAuth connection collection of a deployment's bot, where connections are listed and created.</summary>
    public static Uri OAuthConnections(AmsServiceTarget target, string agentId, string deploymentId) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/oauthConnections");

    /// <summary>Returns one OAuth connection of a deployment's bot.</summary>
    public static Uri OAuthConnection(AmsServiceTarget target, string agentId, string deploymentId, string connectionName) => Build(target, $"{DeploymentPath(agentId, deploymentId)}/oauthConnections/{Escape(connectionName)}");

    private static string AgentPath(string agentId) => $"agents/{Escape(agentId)}";

    private static string DeploymentPath(string agentId, string deploymentId) => $"{AgentPath(agentId)}/deployments/{Escape(deploymentId)}";

    private static Uri BuildListPage(AmsServiceTarget target, string relativePath, string? skipToken) => Build(target, relativePath, $"&$top={ListPageSize}{(skipToken is null ? string.Empty : $"&$skiptoken={Escape(skipToken)}")}");

    private static Uri Build(AmsServiceTarget target, string relativePath, string query = "")
    {
        ArgumentNullException.ThrowIfNull(target);

        var scope = target.RouteForm switch
        {
            AmsRouteForm.EnvironmentHost => string.Empty,
            AmsRouteForm.TenantHost => $"environments/{Escape(target.EnvironmentId)}/",
            AmsRouteForm.Direct => $"tenants/{Escape(target.TenantId)}/environments/{Escape(target.EnvironmentId)}/",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.RouteForm, "Unknown Agent Management Service route form."),
        };

        return new Uri(EnsureTrailingSlash(target.Endpoint), $"agenthosting/{scope}{relativePath}?api-version={ApiVersion}{query}");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static Uri EnsureTrailingSlash(Uri endpoint) => endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
}
