namespace Microsoft.Agents.Hosting.Cli.Core.Operations;
/// <summary>The selectors every sharing verb accepts.</summary>
public abstract record ManagedAgentShareSelector
{
    /// <summary>The project directory holding the configuration. Defaults to the current directory.</summary>
    public string? Project { get; init; }

    /// <summary>The record to act on, by identifier. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record to act on, by name.</summary>
    public string? AgentName { get; init; }
}

/// <summary>Input for <c>msagent agent share</c>.</summary>
public sealed record ManagedAgentShareRequest : ManagedAgentShareSelector
{
    /// <summary>The user to grant access to, by sign-in address, email address, or Entra object id.</summary>
    public string? User { get; init; }

    /// <summary>The access level to grant.</summary>
    public string? Role { get; init; }

    /// <summary>Whether everyone in the organization may use the agent, left unchanged when absent.</summary>
    public bool? ShareWithOrg { get; init; }
}

/// <summary>Input for <c>msagent agent share show</c>.</summary>
public sealed record ManagedAgentShareShowRequest : ManagedAgentShareSelector;

/// <summary>Input for <c>msagent agent unshare</c>.</summary>
public sealed record ManagedAgentUnshareRequest : ManagedAgentShareSelector
{
    /// <summary>The user to revoke access from, by sign-in address, email address, or Entra object id.</summary>
    public string? User { get; init; }
}

/// <summary>One principal's access to an agent.</summary>
public sealed record ManagedAgentShareEntry
{
    /// <summary>The principal's Entra object id, which is what the service records.</summary>
    public required string ObjectId { get; init; }

    /// <summary>The principal's display name.</summary>
    public required string PrincipalName { get; init; }

    /// <summary>Whether the principal is a <c>User</c>, a <c>Group</c>, or <c>Unknown</c>.</summary>
    public required string PrincipalType { get; init; }

    /// <summary>The user's sign-in address, absent for a group.</summary>
    public string? SignInAddress { get; init; }

    /// <summary>The role held: <c>owner</c>, <c>edit</c>, <c>use</c>, <c>view</c>, or <c>none</c>. An access level this build does not recognise is reported as the service spells it.</summary>
    public required string Role { get; init; }

    /// <summary>
    /// Whether the directory named the principal. A principal the environment has never provisioned
    /// stands as its own object id, which is still a complete address for sharing.
    /// </summary>
    public bool IsNamed => !string.Equals(PrincipalName, ObjectId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The principal as a sentence reads it, naming the kind when no display name resolved.</summary>
    public string Describe() => IsNamed ? PrincipalName : $"{PrincipalType.ToLowerInvariant()} {ObjectId}";
}

/// <summary>Output for <c>msagent agent share</c>.</summary>
public sealed record ManagedAgentShareResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>shared</c> or <c>unchanged</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The record that was shared.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The Copilot Studio agent the access was applied to.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The environment the agent lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The principal that received access, absent when only org-wide access changed.</summary>
    public ManagedAgentShareEntry? Principal { get; init; }

    /// <summary>The role that was granted, absent when only org-wide access changed.</summary>
    public string? Role { get; init; }

    /// <summary>Whether everyone in the organization may use the agent.</summary>
    public bool ShareWithOrg { get; init; }

    /// <summary>Whether the command had to change anything.</summary>
    public bool Changed { get; init; }

    /// <summary>The agent's portal deep link, which carries no permission of its own.</summary>
    public string? PortalUrl { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        Principal is null ? DescribeOrgWideOnly() : DescribeGrant(),
        $"  agent        {AgentId} in environment {EnvironmentId}",
        $"  mcs agent    {McsAgentId}",
        .. Principal is null ? [] : new[]
        {
            Principal.IsNamed
                ? $"  principal    {Principal.PrincipalName} ({Principal.PrincipalType} {Principal.ObjectId})"
                : $"  principal    {Principal.ObjectId}  (name unavailable in this environment)",
        },
        $"  org-wide     {(ShareWithOrg ? "everyone in the organization can use this agent" : "off")}",
        .. DescribeLink(),
    ];

    /// <summary>
    /// The link to pass on, shown only when this command actually granted somebody access. 
    /// </summary>
    private IReadOnlyList<string> DescribeLink() =>
        Changed && Principal is not null && !string.IsNullOrWhiteSpace(PortalUrl)
            ? [string.Empty, $"  Send this link to {Principal.Describe()}:", $"  {PortalUrl}"]
            : [];

    private string DescribeGrant() =>
        Changed
            ? $"Shared {DisplayName} with {Principal!.Describe()} as '{Role}'."
            : $"{Principal!.Describe()} already had '{Role}' on {DisplayName}. Nothing changed.";

    private string DescribeOrgWideOnly() =>
        Changed
            ? $"{(ShareWithOrg ? "Enabled" : "Disabled")} org-wide use access on {DisplayName}."
            : $"Org-wide use access on {DisplayName} was already {(ShareWithOrg ? "on" : "off")}. Nothing changed.";
}

/// <summary>Output for <c>msagent agent share show</c>.</summary>
public sealed record ManagedAgentShareShowResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>shares-shown</c>).</summary>
    public string Status { get; init; } = "shares-shown";

    /// <summary>The record that was read.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The Copilot Studio agent whose access was read.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The environment the agent lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>Whether everyone in the organization may use the agent.</summary>
    public bool ShareWithOrg { get; init; }

    /// <summary>The principals holding access, owner first.</summary>
    public IReadOnlyList<ManagedAgentShareEntry> Shares { get; init; } = [];

    /// <summary>The agent's portal link.</summary>
    public string? PortalUrl { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var orgWide = ShareWithOrg ? ["Everyone in the organization can use this agent."] : Array.Empty<string>();
        var link = string.IsNullOrWhiteSpace(PortalUrl) ? [] : new[] { string.Empty, $"  {PortalUrl}" };

        if (Shares.Count == 0)
        {
            return
            [
                ShareWithOrg
                    ? $"{DisplayName} ({AgentId}) is not shared with anyone by name."
                    : $"{DisplayName} ({AgentId}) is not shared with anyone.",
                .. orgWide,
                .. link,
            ];
        }

        return
        [
            $"{Shares.Count} principal(s) with access to {DisplayName} ({AgentId}) in environment {EnvironmentId}.",
            .. orgWide,
            string.Empty,
            .. TextGrid.RenderRows(
                ["PRINCIPAL", "TYPE", "ROLE", "SIGN-IN ADDRESS"],
                [.. Shares.Select(share => new[] { share.PrincipalName, share.PrincipalType, share.Role, TextGrid.DescribeOrDash(share.SignInAddress) })]),
            .. link,
        ];
    }
}

/// <summary>Output for <c>msagent agent unshare</c>.</summary>
public sealed record ManagedAgentUnshareResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>unshared</c> or <c>unchanged</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The record that was unshared.</summary>
    public required string AgentId { get; init; }

    /// <summary>The record's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The Copilot Studio agent the access was removed from.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The environment the agent lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The principal that lost access.</summary>
    public required ManagedAgentShareEntry Principal { get; init; }

    /// <summary>Whether everyone in the organization may still use the agent.</summary>
    public bool ShareWithOrg { get; init; }

    /// <summary>Whether the command had to change anything.</summary>
    public bool Changed { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            Changed
                ? $"Removed the direct share of {DisplayName} ({AgentId}) with {Principal.Describe()}."
                : $"{Principal.Describe()} held no direct share of {DisplayName} ({AgentId}). Nothing changed.",
            DescribePrincipal(),
        };

        if (ShareWithOrg)
        {
            lines.Add($"  Everyone in the organization can still use this agent, so this did not end {Principal.Describe()}'s access. Turn that off with `msagent agent share --agent-id {AgentId} --share-with-org false`.");
        }

        return lines;
    }

    /// <summary>
    /// States the principal as its own line.
    /// </summary>
    private string DescribePrincipal() =>
        Principal.IsNamed
            ? $"  principal    {Principal.PrincipalName} ({Principal.PrincipalType} {Principal.ObjectId})"
            : $"  principal    {Principal.ObjectId}  (name unavailable in this environment)";
}

