namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// The Azure coordinates the deployment module's own ARM adapter targets — the ARM audience/endpoint
/// and the <c>Microsoft.BotService</c> api-version it provisions against.
/// </summary>
public sealed record DeploymentTargetConfig
{
    /// <summary>The ARM token audience/resource (bare, the token seam appends <c>/.default</c>).</summary>
    public required string ArmResource { get; init; }

    /// <summary>The ARM REST endpoint host (no trailing slash).</summary>
    public required string ArmEndpoint { get; init; }

    /// <summary>The <c>Microsoft.BotService</c> api-version used for bot and channel resources.</summary>
    public required string BotServiceApiVersion { get; init; }

    /// <summary>
    /// The generic-ARM api-version for the subscription listing (<c>GET /subscriptions</c>). Not part of
    /// the <c>Microsoft.BotService</c> contract; kept here so every ARM api-version the module targets
    /// lives in one place and can be reused by future deployment features.
    /// </summary>
    public required string SubscriptionsApiVersion { get; init; }

    /// <summary>
    /// The generic-ARM api-version for the resource-group listing
    /// (<c>GET /subscriptions/{id}/resourcegroups</c>). Not part of the <c>Microsoft.BotService</c>
    /// contract; kept here alongside the other ARM api-versions for one-stop configuration and reuse.
    /// </summary>
    public required string ResourceGroupsApiVersion { get; init; }

    /// <summary>Azure public-cloud defaults for the deployment ARM target.</summary>
    public static DeploymentTargetConfig AzurePublic { get; } = new()
    {
        ArmResource = "https://management.azure.com",
        ArmEndpoint = "https://management.azure.com",
        BotServiceApiVersion = "2023-09-15-preview",
        SubscriptionsApiVersion = "2020-01-01",
        ResourceGroupsApiVersion = "2021-04-01",
    };
}
