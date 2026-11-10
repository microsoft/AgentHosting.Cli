using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public enum ProfileAzureSourceId
{
    EnvironmentClientSecret,
    EnvironmentCertificate,
    WorkloadIdentity,
    ManagedIdentity,
    AzureCli,
    AzurePowerShell,
    AzureDeveloperCli,
}

public interface IProfileAzureCredentialSourceFactory
{
    IReadOnlyList<ProfileAzureSourceId> GetInitialSources(ProfileCredentialRuntimeOptions settings, AuthorityInfo authority);
    TokenCredential Create(ProfileAzureSourceId source, ProfileCredentialRuntimeOptions settings, AuthorityInfo authority);
}

public sealed class ProfileAzureCredentialSourceFactory(IInteractionPolicy interactionPolicy, HttpPipelineTransport? httpTransport = null) : IProfileAzureCredentialSourceFactory
{
    private readonly IInteractionPolicy _interactionPolicy = interactionPolicy ?? throw new ArgumentNullException(nameof(interactionPolicy));
    public static bool IsDeveloper(ProfileAzureSourceId source) =>
        source is ProfileAzureSourceId.AzureCli or ProfileAzureSourceId.AzurePowerShell or ProfileAzureSourceId.AzureDeveloperCli;

    public IReadOnlyList<ProfileAzureSourceId> GetInitialSources(ProfileCredentialRuntimeOptions settings, AuthorityInfo authority)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(authority);
        ValidateSettings(settings);
        var selection = settings.AzureCredentialSelection?.Trim().ToLowerInvariant();
        if (selection is not null and not ("" or "prod" or "dev"))
        {
            var source = selection switch
            {
                "environmentcredential" => EnvironmentSource(settings),
                "workloadidentitycredential" => ProfileAzureSourceId.WorkloadIdentity,
                "managedidentitycredential" => ProfileAzureSourceId.ManagedIdentity,
                "azureclicredential" => ProfileAzureSourceId.AzureCli,
                "azurepowershellcredential" => ProfileAzureSourceId.AzurePowerShell,
                "azuredeveloperclicredential" => ProfileAzureSourceId.AzureDeveloperCli,
                _ => throw new UsageException("The host-selected Azure credential source is not allowlisted."),
            };
            EnsurePolicy(source);
            return [source];
        }
        if (selection == "dev" && !_interactionPolicy.IsInteractionAllowed)
            throw new InteractionRequiredException("Developer Azure credentials require interaction permission in this invocation.");
        var candidates = new List<ProfileAzureSourceId>();
        if (selection != "dev")
        {
            if (!settings.ExcludeEnvironmentCredential) candidates.Add(EnvironmentSource(settings));
            if (!settings.ExcludeWorkloadIdentityCredential) candidates.Add(ProfileAzureSourceId.WorkloadIdentity);
            if (!settings.ExcludeManagedIdentityCredential) candidates.Add(ProfileAzureSourceId.ManagedIdentity);
        }
        if (selection != "prod" && _interactionPolicy.IsInteractionAllowed && !settings.ExcludeDeveloperCredentials)
            candidates.AddRange([ProfileAzureSourceId.AzureCli, ProfileAzureSourceId.AzurePowerShell, ProfileAzureSourceId.AzureDeveloperCli]);
        return candidates;
    }

    public TokenCredential Create(ProfileAzureSourceId source, ProfileCredentialRuntimeOptions settings, AuthorityInfo authority)
    {
        EnsurePolicy(source);
        ValidateSettings(settings);
        var host = new Uri(authority.AuthorityUrl.GetLeftPart(UriPartial.Authority));
        var tenant = authority.TenantId?.ToString("D") ?? settings.AzureTenantId;
        var client = settings.AzureClientId;
        return source switch
        {
            ProfileAzureSourceId.EnvironmentClientSecret when tenant is not null && client is not null && !string.IsNullOrEmpty(settings.AzureClientSecret) =>
                new ClientSecretCredential(tenant, client, settings.AzureClientSecret,
                    Configure(new ClientSecretCredentialOptions { AuthorityHost = host, DisableInstanceDiscovery = true })),
            ProfileAzureSourceId.EnvironmentCertificate when tenant is not null && client is not null && settings.AzureCertificatePath is not null =>
                CertificateCredential(tenant, client, settings, host),
            ProfileAzureSourceId.EnvironmentClientSecret or ProfileAzureSourceId.EnvironmentCertificate =>
                throw new CredentialUnavailableException("The pinned app-environment credential is not configured."),
            ProfileAzureSourceId.WorkloadIdentity when tenant is not null && client is not null && settings.AzureFederatedTokenFile is not null =>
                new WorkloadIdentityCredential(Configure(new WorkloadIdentityCredentialOptions
                {
                    TenantId = tenant, ClientId = client, TokenFilePath = settings.AzureFederatedTokenFile,
                    AuthorityHost = host, DisableInstanceDiscovery = true,
                })),
            ProfileAzureSourceId.WorkloadIdentity => throw new CredentialUnavailableException("The workload identity source is not configured."),
            ProfileAzureSourceId.ManagedIdentity =>
                new ManagedIdentityCredential(Configure(new ManagedIdentityCredentialOptions(
                    (settings.ManagedIdentityClientId ?? client) is { } id ? ManagedIdentityId.FromUserAssignedClientId(id) : ManagedIdentityId.SystemAssigned)
                    { AuthorityHost = host })),
            ProfileAzureSourceId.AzureCli => new AzureCliCredential(new AzureCliCredentialOptions
            { TenantId = tenant, ProcessTimeout = settings.CredentialProcessTimeout }),
            ProfileAzureSourceId.AzurePowerShell => new AzurePowerShellCredential(new AzurePowerShellCredentialOptions
            { TenantId = tenant, ProcessTimeout = settings.CredentialProcessTimeout }),
            ProfileAzureSourceId.AzureDeveloperCli => new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions
            { TenantId = tenant, ProcessTimeout = settings.CredentialProcessTimeout }),
            _ => throw new UsageException("The Azure credential source is not allowlisted."),
        };
    }

    private void EnsurePolicy(ProfileAzureSourceId source)
    {
        if (!Enum.IsDefined(source)) throw new UsageException("The Azure credential source is not allowlisted.");
        if (IsDeveloper(source) && !_interactionPolicy.IsInteractionAllowed)
            throw new InteractionRequiredException("The pinned developer Azure credential requires interaction permission in this invocation.");
    }

    private static ProfileAzureSourceId EnvironmentSource(ProfileCredentialRuntimeOptions settings) =>
        !string.IsNullOrEmpty(settings.AzureClientSecret) || settings.AzureCertificatePath is null
            ? ProfileAzureSourceId.EnvironmentClientSecret : ProfileAzureSourceId.EnvironmentCertificate;

    private static void ValidateSettings(ProfileCredentialRuntimeOptions s)
    {
        if (s.AzureTenantId is not null) ProfileCredentialValidation.ParseGuid(s.AzureTenantId, "Azure tenant");
        if (s.AzureClientId is not null) ProfileCredentialValidation.ParseGuid(s.AzureClientId, "Azure client");
        if (s.ManagedIdentityClientId is not null) ProfileCredentialValidation.ParseGuid(s.ManagedIdentityClientId, "managed identity client");
        if (s.CredentialProcessTimeout <= TimeSpan.Zero || s.CredentialProcessTimeout > TimeSpan.FromMinutes(2))
            throw new UsageException("Azure credential process timeout must be greater than zero and at most two minutes.");
    }

    private T Configure<T>(T options) where T : TokenCredentialOptions
    {
        if (httpTransport is not null) options.Transport = httpTransport;
        return options;
    }

    private CertificateCredentialOwner CertificateCredential(string tenant, string client, ProfileCredentialRuntimeOptions settings, Uri host)
    {
        var certificate = ProfileCertificateLoader.Load(settings.AzureCertificatePath!, settings.AzureCertificatePassword);
        try
        {
            return new CertificateCredentialOwner(certificate, new ClientCertificateCredential(tenant, client, certificate,
                Configure(new ClientCertificateCredentialOptions { AuthorityHost = host, DisableInstanceDiscovery = true })));
        }
        catch { certificate.Dispose(); throw; }
    }

    private sealed class CertificateCredentialOwner(X509Certificate2 certificate, TokenCredential inner) : TokenCredential, IDisposable
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => inner.GetToken(requestContext, cancellationToken);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => inner.GetTokenAsync(requestContext, cancellationToken);
        public void Dispose() => certificate.Dispose();
    }
}

internal static class ProfileCertificateLoader
{
    internal static X509Certificate2 Load(string path, string? password)
        => Load(path, password, new RuntimePlatformInfo(),
            (file, secret, flags) => X509CertificateLoader.LoadPkcs12FromFile(file, secret, flags));

    internal static X509Certificate2 Load(string path, string? password, IPlatformInfo platform,
        Func<string, string?, X509KeyStorageFlags, X509Certificate2> import)
    {
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new AuthenticationException("The certificate file exceeds its size limit.");
            // .NET macOS rejects EphemeralKeySet; without PersistKeySet it owns a temporary
            // Keychain whose deletion is attempted after the last certificate/key handle closes.
            var flags = platform.IsMacOs ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            var certificate = import(path, password, flags);
            if (certificate.HasPrivateKey) return certificate;
            certificate.Dispose();
            throw new AuthenticationException("The certificate does not contain a private key.");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new AuthenticationException("The certificate credential could not be loaded; check its path, password and private key."); }
    }
}
