using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public sealed class ProfileCredentialRuntimeOptions
{
    [JsonIgnore] public string? GitHubRequestUrl { get; init; }
    [JsonIgnore] public string? GitHubRequestToken { get; init; }
    [JsonIgnore] public string? AzureDevOpsRequestUrl { get; init; }
    [JsonIgnore] public string? AzureDevOpsRequestToken { get; init; }
    public string? AzureTenantId { get; init; }
    public string? AzureClientId { get; init; }
    [JsonIgnore] public string? AzureClientSecret { get; init; }
    public string? AzureCertificatePath { get; init; }
    [JsonIgnore] public string? AzureCertificatePassword { get; init; }
    public string? AzureFederatedTokenFile { get; init; }
    public string? ManagedIdentityClientId { get; init; }
    public string? AzureCredentialSelection { get; init; }
    public string? BrowserRedirectUri { get; init; }
    public bool ExcludeEnvironmentCredential { get; init; }
    public bool ExcludeWorkloadIdentityCredential { get; init; }
    public bool ExcludeManagedIdentityCredential { get; init; }
    public bool ExcludeDeveloperCredentials { get; init; }
    public TimeSpan CredentialProcessTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed class ProfileTokenRequest
{
    public required AuthCredentialMethod Method { get; init; }
    public required Guid ClientId { get; init; }
    public required AuthorityInfo Authority { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required string CacheReference { get; init; }
    public string? HomeAccountId { get; init; }
    public string? LoginHint { get; init; }
    public string? Username { get; init; }
    [JsonIgnore] public string? Password { get; init; }
    [JsonIgnore] public string? ClientSecret { get; init; }
    public string? CertificatePath { get; init; }
    [JsonIgnore] public string? CertificatePassword { get; init; }
    public bool IsCreation { get; init; }
    public bool ForceInteractive { get; init; }
    public string? RedirectUri { get; init; }
    public bool InteractionAllowed { get; init; }
    public ProfileAzureSourceId? PinnedAzureSource { get; init; }
    public PowerPlatformApiRing Ring { get; init; }
    public string? ConnectionUrl { get; init; }
}

public sealed class ProfileTokenResult
{
    [JsonIgnore] public required string AccessToken { get; init; }
    public required string TenantId { get; init; }
    public string? HomeAccountId { get; init; }
    public string? Username { get; init; }
    public DateTimeOffset ExpiresOn { get; init; }
    public string? CertificateThumbprint { get; init; }
    public ProfileAzureSourceId? AzureSource { get; init; }
    public AuthCredentialMethod? ActualMethod { get; init; }
    [JsonIgnore] public AuthenticationResult? MsalResult { get; init; }
}

public interface IProfileTokenAcquisition
{
    void Validate(ProfileTokenRequest request) { }
    Task<ProfileTokenResult> AcquireAsync(ProfileTokenRequest request, CancellationToken ct);
    Task<IReadOnlyList<AuthAccount>> GetCachedAccountsAsync(ProfileTokenRequest request, CancellationToken ct);
}
