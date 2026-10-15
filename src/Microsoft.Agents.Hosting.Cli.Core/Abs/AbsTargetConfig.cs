namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// Azure public-cloud coordinates for the direct-ARM Azure Bot Service target — a faithful C# port
/// of the shipped TypeScript <c>AZURE_PUBLIC_ABS_TARGET</c> and <c>AbsTargetConfig</c>. This is the
/// single source of truth for the ARM audience/endpoint and the
/// <c>Microsoft.BotService</c> api-version, so <see cref="ArmAbsProvisioningAdapter"/> never repeats
/// an audience or version string and can be retargeted (sovereign clouds, tests) by swapping the
/// config.
///
/// The Graph coordinates are carried for shape-parity with the oracle even though the scoped C#
/// zero-Graph path never issues a Graph call.
/// </summary>
public sealed record AbsTargetConfig
{
    /// <summary>The ARM token audience/resource (bare, the token seam appends <c>/.default</c>).</summary>
    public required string ArmResource { get; init; }

    /// <summary>The ARM REST endpoint host (no trailing slash).</summary>
    public required string ArmEndpoint { get; init; }

    /// <summary>The <c>Microsoft.BotService</c> api-version.</summary>
    public required string ArmApiVersion { get; init; }

    /// <summary>The Microsoft Graph token audience/resource.</summary>
    public required string GraphResource { get; init; }

    /// <summary>The Microsoft Graph REST endpoint host.</summary>
    public required string GraphEndpoint { get; init; }

    /// <summary>The Microsoft Graph api-version segment (for example <c>v1.0</c>).</summary>
    public required string GraphApiVersion { get; init; }

    /// <summary>Azure public-cloud defaults, matching the shipped oracle byte-for-byte.</summary>
    public static AbsTargetConfig AzurePublic { get; } = new()
    {
        ArmResource = "https://management.azure.com",
        ArmEndpoint = "https://management.azure.com",
        ArmApiVersion = "2022-09-15",
        GraphResource = "https://graph.microsoft.com",
        GraphEndpoint = "https://graph.microsoft.com",
        GraphApiVersion = "v1.0",
    };
}
