using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

public sealed class AuthCreateRequest
{
    public string? Name { get; init; }
    public string? Environment { get; init; }
    public PowerPlatformApiTargetConfig Target { get; init; } = PowerPlatformApiTargetConfig.Public;
    public string? Tenant { get; init; }
    public string? LoginHint { get; init; }
    public string? Resource { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public bool DeviceCode { get; init; }
    public string? Username { get; init; }
    [JsonIgnore] public string? Password { get; init; }
    public string? ApplicationId { get; init; }
    [JsonIgnore] public string? ClientSecret { get; init; }
    public string? CertificateDiskPath { get; init; }
    [JsonIgnore] public string? CertificatePassword { get; init; }
    public bool ManagedIdentity { get; init; }
    public bool GitHubFederated { get; init; }
    public bool AzureDevOpsFederated { get; init; }
    public bool NonInteractive { get; init; }
}
