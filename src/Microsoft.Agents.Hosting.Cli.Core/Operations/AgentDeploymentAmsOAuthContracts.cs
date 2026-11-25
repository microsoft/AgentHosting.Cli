namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Routes a deployment OAuth request to the AMS deployment that the agent id, deployment id and home environment address.</summary>
/// <typeparam name="TOAuthRequest">The OAuth request whose schema-free content rules AMS mode shares with local mode.</typeparam>
public sealed record AgentDeploymentAmsOAuthRequest<TOAuthRequest> : AgentDeploymentAmsRequest
    where TOAuthRequest : class
{
    /// <summary>The OAuth content, validated with the schema-free local-mode rules; its local deployment address is not used.</summary>
    public required TOAuthRequest OAuth { get; init; }
}
