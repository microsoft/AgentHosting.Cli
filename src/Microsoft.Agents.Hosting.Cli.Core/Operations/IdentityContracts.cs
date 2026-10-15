using System.Text.Json.Serialization;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Requested credential mode for a provisioned agent identity.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdentityCredentialType>))]
public enum IdentityCredentialType
{
    FederatedCredentials,
    ClientSecret,

    /// <summary>
    /// User-assigned managed identity as the agent's runtime credential. Only valid for App
    /// Registration identities; the Blueprint flow rejects it.
    /// </summary>
    UserManagedIdentity,
}

/// <summary>Runtime-specific shape used for generated connection configuration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdentityConfigFormat>))]
public enum IdentityConfigFormat
{
    DotNet,
    Node,
}

/// <summary>Input for the end-to-end Agent Identity Blueprint create/reconcile operation.</summary>
public sealed record BlueprintIdentityRequest
{
    public required string TenantId { get; init; }
    public required string DisplayName { get; init; }
    public required IdentityCredentialType AuthType { get; init; }
    public required IdentityConfigFormat ConfigFormat { get; init; }
    public string? SubscriptionId { get; init; }
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? ManagedIdentityName { get; init; }
    public bool OAuth { get; init; }
    public string? RedirectUri { get; init; }
    public bool TeamsSso { get; init; }
    public bool Observability { get; init; }
    public bool McpDiscovery { get; init; }
    public bool DryRun { get; init; }
    public bool NonInteractive { get; init; }
}

/// <summary>One secret-free operation in a Blueprint provisioning plan or result.</summary>
public sealed record IdentityChange
{
    public required string Resource { get; init; }
    public required string Action { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// Secret-free generated runtime configuration. Credential values must be injected separately from
/// an approved secret store.
/// </summary>
public sealed record IdentityGeneratedConfig
{
    public required string Format { get; init; }
    public required string Content { get; init; }

    /// <summary>The human-readable view: the format header, then one entry per line of content.</summary>
    internal IEnumerable<string> ToTextLines() =>
        [$"Generated {Format} configuration:", .. Content.Replace("\r\n", "\n").Split('\n')];
}

/// <summary>Result of Blueprint identity planning or convergence.</summary>
public sealed record BlueprintIdentityReport : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required string TenantId { get; init; }
    public required string DisplayName { get; init; }
    public string? ApplicationId { get; init; }
    public string? ApplicationObjectId { get; init; }
    public string? ServicePrincipalObjectId { get; init; }
    public string? AdminConsentUrl { get; init; }

    /// <summary>
    /// One-time client secret returned only after creation. It is never included in generated
    /// configuration or partial reports.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OneTimeClientSecret { get; init; }

    public required IReadOnlyList<IdentityChange> Changes { get; init; }
    public required IReadOnlyList<string> NextActions { get; init; }
    public IdentityGeneratedConfig? Configuration { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string> { $"Blueprint identity {Status}: {DisplayName}" };

        if (ApplicationId is not null)
        {
            lines.Add($"Application ID: {ApplicationId}");
        }

        if (ServicePrincipalObjectId is not null)
        {
            lines.Add($"Service principal ID: {ServicePrincipalObjectId}");
        }

        if (OneTimeClientSecret is not null)
        {
            lines.Add($"One-time client secret (store securely; shown once): {OneTimeClientSecret}");
        }

        lines.AddRange(Changes.Select(change => $"{change.Action,-9} {change.Resource}: {change.Detail}"));

        if (Configuration is not null)
        {
            lines.AddRange(Configuration.ToTextLines());
        }

        lines.AddRange(NextActions.Select(action => $"Next: {action}"));

        return lines;
    }
}

/// <summary>
/// Input for the end-to-end App Registration identity create/reconcile operation. Unlike the
/// Blueprint request, <see cref="AuthType"/> may be <see cref="IdentityCredentialType.UserManagedIdentity"/>,
/// and an existing App Registration can be targeted directly via <see cref="ApplicationId"/> or
/// <see cref="ApplicationObjectId"/>.
/// </summary>
public sealed record AppRegistrationIdentityRequest
{
    public required string TenantId { get; init; }
    public required string DisplayName { get; init; }
    public required IdentityCredentialType AuthType { get; init; }
    public required IdentityConfigFormat ConfigFormat { get; init; }

    /// <summary>Application (client) id of an existing App Registration to target/disambiguate.</summary>
    public string? ApplicationId { get; init; }

    /// <summary>Application object id of an existing App Registration to target directly.</summary>
    public string? ApplicationObjectId { get; init; }

    public string? SubscriptionId { get; init; }
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? ManagedIdentityName { get; init; }
    public bool OAuth { get; init; }
    public string? RedirectUri { get; init; }
    public bool TeamsSso { get; init; }
    public bool Observability { get; init; }
    public bool McpDiscovery { get; init; }
    public bool DryRun { get; init; }
    public bool NonInteractive { get; init; }

}

/// <summary>
/// Result of App Registration identity planning or convergence. A newly-created client secret is
/// returned separately from the secret-free generated configuration and is never persisted or logged.
/// </summary>
public sealed record AppRegistrationIdentityReport : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required string TenantId { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Application (client) id of the OAuth identity app (the companion app for UMI).</summary>
    public string? ApplicationId { get; init; }
    public string? ApplicationObjectId { get; init; }
    public string? ServicePrincipalObjectId { get; init; }

    /// <summary>Client id of the user-assigned managed identity (UMI/FIC auth types).</summary>
    public string? ManagedIdentityClientId { get; init; }
    public string? ManagedIdentityPrincipalId { get; init; }
    public string? ManagedIdentityResourceId { get; init; }

    /// <summary>Authoritative expiration returned with a newly-created one-time client secret.</summary>
    public DateTimeOffset? ClientSecretExpiresOn { get; init; }

    /// <summary>
    /// One-time client secret returned only after creation. It is never included in generated
    /// configuration or partial reports.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OneTimeClientSecret { get; init; }

    public string? AdminConsentUrl { get; init; }
    public required IReadOnlyList<IdentityChange> Changes { get; init; }
    public required IReadOnlyList<string> NextActions { get; init; }
    public IdentityGeneratedConfig? Configuration { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string> { $"App Registration identity {Status}: {DisplayName}" };

        if (ApplicationId is not null)
        {
            lines.Add($"Application ID: {ApplicationId}");
        }

        if (ServicePrincipalObjectId is not null)
        {
            lines.Add($"Service principal ID: {ServicePrincipalObjectId}");
        }

        if (ManagedIdentityClientId is not null)
        {
            lines.Add($"Managed identity client ID: {ManagedIdentityClientId}");
        }

        if (OneTimeClientSecret is not null)
        {
            lines.Add($"One-time client secret (store securely; shown once): {OneTimeClientSecret}");
        }

        if (ClientSecretExpiresOn is not null)
        {
            lines.Add($"Client secret expires on: {ClientSecretExpiresOn:O}");
        }

        lines.AddRange(Changes.Select(change => $"{change.Action,-9} {change.Resource}: {change.Detail}"));

        if (Configuration is not null)
        {
            lines.AddRange(Configuration.ToTextLines());
        }

        lines.AddRange(NextActions.Select(action => $"Next: {action}"));

        return lines;
    }
}
