namespace Microsoft.Agents.Hosting.Cli.Core;

/// <summary>
/// Identity metadata for the Microsoft AgentHosting CLI.
///
/// The type carries no console, authentication, or Azure dependency so it stays embeddable
/// by a future service host.
/// </summary>
public static class AgentHostingCliInfo
{
    /// <summary>
    /// Packaged dotnet-tool command name.
    /// </summary>
    public const string CommandName = "msagent";

    /// <summary>
    /// Human-facing product name.
    /// </summary>
    public const string ProductName = "Microsoft AgentHosting CLI";
}
