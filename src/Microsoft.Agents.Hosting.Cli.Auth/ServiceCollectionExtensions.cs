using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The composition-root wiring for the AgentHost authentication surface. This is the ONLY entry point
/// the <c>Microsoft.Agents.Hosting.Cli</c> host calls into this project — it takes an <see cref="AgentHostAuthOptions"/>
/// of resolved primitives and registers the real MSAL adapters plus the MSAL-free
/// <see cref="AgentHostingOperations"/> facade. Because the host names no MSAL type (only this extension
/// and the options record), the host assembly records no direct Msal/Azure reference and its
/// offline-guarantee test stays green.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentHostProfileCredentials(this IServiceCollection services,
        OwnedStorage.IOwnedProtectedStorageBackend backend, ProfileCredentialRuntimeOptions runtime,
        ILegacyMsalTokenCacheBackend? legacyTokenCache = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(runtime);
        services.AddSingleton(backend);
        services.AddSingleton(runtime);
        if (legacyTokenCache is not null) services.AddSingleton(legacyTokenCache);
        services.AddSingleton<ProfileFederationAssertionProvider>();
        services.AddSingleton<IProfileMsalClientFactory, ProfileMsalClientFactory>();
        services.AddSingleton<IProfileAzureCredentialSourceFactory, ProfileAzureCredentialSourceFactory>();
        services.AddSingleton<IProfileTokenAcquisition, ProfileTokenAcquisition>();
        services.AddSingleton<ProfileCredentialProvider>();
        services.AddSingleton<IProfileCredentialProvider>(sp => sp.GetRequiredService<ProfileCredentialProvider>());
        services.AddSingleton<IOwnedProfileCredentialCleanup>(sp => sp.GetRequiredService<ProfileCredentialProvider>());
        services.AddSingleton<IProfileCachedAccountReader>(sp => sp.GetRequiredService<ProfileCredentialProvider>());
        services.AddSingleton<IProfileSelectedAccountTokenAcquisition>(sp => sp.GetRequiredService<ProfileCredentialProvider>());
        services.AddSingleton<IProfileConsentTokenAcquisition>(sp => sp.GetRequiredService<ProfileCredentialProvider>());
        services.AddSingleton<IAuthProfileOperations>(sp => new AuthProfileOperations(
            sp.GetRequiredService<IAuthProfileManagementStore>(),
            sp.GetRequiredService<IProfileCredentialProvider>(),
            sp.GetRequiredService<EnvironmentSelectionService>(),
            sp.GetRequiredService<IEnvironmentSelectionStore>(),
            sp.GetRequiredService<IInteractionPolicy>(),
            sp.GetRequiredService<IClock>(),
            sp.GetService<IOwnedProfileCredentialCleanup>(),
            sp.GetService<IAuthProfilePicker>(),
            sp.GetRequiredService<IAuthenticationSession>(),
            sp.GetService<EnvironmentSelectionScopes>()));
        return services;
    }

    /// <summary>
    /// Registers the AgentHost authentication services (profile store, MSAL session/token/probe
    /// adapters, interaction policy, message presenter, session context), the environment cloud map,
    /// selection store, and selection service, and the
    /// <see cref="IAgentHostingOperations"/> facade, using <paramref name="options"/> resolved at the
    /// composition root. <paramref name="powerPlatformApiTarget"/> lets that root select a fixed
    /// deployment ring and its paired host allowlist without exposing a command-line override.
    ///
    /// The caller must also register an <see cref="IEnvironmentClient"/>: the Power Platform API
    /// transport adapter is owned by the composition root, which decides the <see cref="HttpClient"/>
    /// and its redirect policy. The same applies to the AMS transports.
    /// </summary>
    public static IServiceCollection AddAgentHostAuthentication(
        this IServiceCollection services,
        AgentHostAuthOptions options,
        PowerPlatformApiTargetConfig? powerPlatformApiTarget = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (powerPlatformApiTarget is not null && powerPlatformApiTarget.Cloud != options.Cloud)
        {
            throw new ArgumentException(
                "The Power Platform API target cloud must match the authentication cloud.",
                nameof(powerPlatformApiTarget));
        }

        var ring = powerPlatformApiTarget?.Ring ?? PowerPlatformApiRing.Production;

        services.AddSingleton(options);
        services.AddSingleton<IPlatformInfo, RuntimePlatformInfo>();
        services.AddSingleton<ISecureTokenCacheProbe, MsalSecureTokenCacheProbe>();
        services.AddSingleton<MsalPublicClientProvider>();
        services.AddSingleton(new PpapiTokenClaimsValidator(options.ClientId));
        services.AddSingleton(GraphConsentClientConfiguration.ResolveDeferred(
            options.GraphClientId,
            options.GraphRedirectUri));
        services.AddSingleton(ArmConsentClientConfiguration.ResolveDeferred(
            options.ArmClientId,
            options.ArmRedirectUri));
        services.AddSingleton<AuthProfileMutationRevision>();

        services.AddSingleton(sp =>
            new FileAuthProfileStore(
                Path.Combine(
                    options.ConfigDirectory,
                    AgentHostAuthOptions.ProfilesFileName),
                sp.GetService<IAuthProfileStateObserver>(),
                sp.GetRequiredService<AuthProfileMutationRevision>()));
        services.AddSingleton<IAuthProfileStore>(
            sp => sp.GetRequiredService<FileAuthProfileStore>());
        services.AddSingleton<IAuthProfileManagementStore>(
            sp => sp.GetRequiredService<FileAuthProfileStore>());
        services.AddSingleton<IAuthProfileCreationStore>(
            sp => sp.GetRequiredService<FileAuthProfileStore>());
        services.AddSingleton<IAuthProfileTelemetryStore>(
            sp => sp.GetRequiredService<FileAuthProfileStore>());

        services.AddSingleton<IAuthenticationSession>(sp => new MsalAuthenticationSession(
            sp.GetRequiredService<MsalPublicClientProvider>(),
            sp.GetRequiredService<AgentHostAuthOptions>(),
            sp.GetService<IAuthenticationResultObserver>(),
            sp.GetService<IOwnedProfileCredentialCleanup>()));
        services.AddSingleton(sp => new MsalSilentTokenProvider(
            sp.GetRequiredService<MsalPublicClientProvider>(),
            sp.GetRequiredService<IAuthProfileStore>(),
            sp.GetService<IAuthenticationResultObserver>(),
            sp.GetService<IProfileCredentialProvider>(),
            sp.GetService<IProfileSelectedAccountTokenAcquisition>()));
        services.AddSingleton<ITokenProvider>(
            sp => sp.GetRequiredService<MsalSilentTokenProvider>());
        services.AddSingleton<ISelectedAccountTokenProvider>(
            sp => sp.GetRequiredService<MsalSilentTokenProvider>());
        services.AddSingleton(sp => new MsalConsentTokenAcquisition(
            sp.GetRequiredService<MsalPublicClientProvider>(),
            sp.GetRequiredService<IAuthProfileStore>(),
            sp.GetRequiredService<IInteractionPolicy>(),
            sp.GetRequiredService<IInteractiveMessagePresenter>(),
            sp.GetService<IAuthenticationResultObserver>(),
            sp.GetService<IProfileConsentTokenAcquisition>()));
        services.AddSingleton<MsalLiveTestTokenProvider>();
        services.AddSingleton<IGraphConsentTokenProvider, MsalGraphConsentTokenProvider>();
        services.AddSingleton<IArmConsentTokenProvider, MsalArmConsentTokenProvider>();
        services.AddSingleton<ArmScopedTokenProvider>();

        services.AddSingleton<IInteractionPolicy>(new StaticInteractionPolicy(options.InteractionAllowed));
        services.AddSingleton<IInteractiveMessagePresenter>(_ => new ConsoleInteractiveMessagePresenter(options.MessageOutput ?? Console.Error));

        services.AddSingleton(_ => new AuthSessionContext
        {
            ClientId = options.ClientId,
            Cloud = options.Cloud,
            Authority = AgentHostAuthorityResolver.Resolve(options.Cloud).AuthorityUrl.ToString(),
        });

        // Azure Bot Service scaffolding uses direct ARM. One adapter instance serves both the
        // read-only state reader and the single mutation seam; it speaks raw HTTP over an HttpClient and
        // the abstract ITokenProvider, so it needs no MSAL/Azure/Graph type and the host's
        // offline-guarantee test stays green. Connection metadata is persisted per-user under the
        // config directory.
        services.AddSingleton(sp =>
            new ArmAbsProvisioningAdapter(new HttpClient(TransportDefaults.CreateRedirectFollowingHandler()), sp.GetRequiredService<ArmScopedTokenProvider>()));
        services.AddSingleton<IAbsStateReader>(sp => sp.GetRequiredService<ArmAbsProvisioningAdapter>());
        services.AddSingleton<IAbsMutationPort>(sp => sp.GetRequiredService<ArmAbsProvisioningAdapter>());
        services.AddSingleton<IConnectionMetadataStore>(_ =>
            new FileConnectionMetadataStore(Path.Combine(
                options.ConfigDirectory,
                PowerPlatformApiRingSelection.RingScopedName("connections", ring))));
        services.AddSingleton<AbsProvisioningService>();
        services.AddSingleton<AbsApplyService>();

        services.AddSingleton(sp =>
            new IdentityRestClient(
                new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()),
                sp.GetRequiredService<ITokenProvider>(),
                graphConsentTokenProvider: null,
                armConsentTokenProvider: sp.GetRequiredService<IArmConsentTokenProvider>()));
        services.AddSingleton<IIdentityRestClient>(sp => sp.GetRequiredService<IdentityRestClient>());
        services.AddSingleton(sp => new BlueprintIdentityService(
            sp.GetRequiredService<IIdentityRestClient>(),
            armConsentClient: sp.GetRequiredService<ArmConsentClientConfiguration>()));

        // App Registration provisioning also acquires Microsoft Graph tokens through a consent-capable
        // client. It therefore gets its own REST client wired with both consent providers; Blueprint
        // keeps Graph on the AgentHost client while sharing the ARM consent path above.
        services.AddSingleton(sp =>
            new AppRegistrationIdentityService(
                new IdentityRestClient(
                    new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()),
                    sp.GetRequiredService<ITokenProvider>(),
                    sp.GetRequiredService<IGraphConsentTokenProvider>(),
                    sp.GetRequiredService<IArmConsentTokenProvider>()),
                graphConsentClient: sp.GetRequiredService<GraphConsentClientConfiguration>(),
                armConsentClient: sp.GetRequiredService<ArmConsentClientConfiguration>()));

        // Environment targeting. The cloud map, the file-backed selection, and the selection service
        // are MSAL-free Core types, so they are registered with the rest of the auth surface. The
        // concrete Power Platform API HTTP client is NOT registered here: it is a transport adapter and
        // belongs to the composition root, which owns the HttpClient and its redirect policy.
        services.AddSingleton(powerPlatformApiTarget ?? PowerPlatformApiRingSelection.Default);
        services.AddSingleton(powerPlatformApiTarget is null
            ? PowerPlatformCloudMap.ForCloud(options.Cloud)
            : PowerPlatformCloudMap.ForTarget(powerPlatformApiTarget));
        services.AddSingleton<IEnvironmentSelectionStore>(_ =>
            new FileEnvironmentSelectionStore(
                Path.Combine(
                    options.ConfigDirectory,
                    PowerPlatformApiRingSelection.RingScopedName(
                        AgentHostAuthOptions.EnvironmentSelectionFileName,
                        ring))));
        services.AddSingleton(_ => EnvironmentSelectionScopes.ForConfigDirectory(
            options.ConfigDirectory,
            AgentHostAuthOptions.EnvironmentSelectionFileName));
        services.AddSingleton<EnvironmentSelectionService>();

        // AMS operations. The application services are MSAL-free Core; the concrete HTTP transports
        // remain owned by the composition root.
        services.AddSingleton<AgentCreateService>();
        services.AddSingleton<AgentPackageUploadService>();
        services.AddSingleton<AgentDeployService>();

        // Deployments, OAuth, and channels. DeploymentService depends on one provider-neutral transport; the
        // current adapter implements it through direct ARM. OAuth keeps its own connection port and
        // reuses the deployment transport's subscription resolution for provider discovery. Channels
        // use their own adapter over the shared ARM client.
        services.AddSingleton(sp =>
            new DeploymentAdapter(new HttpClient(TransportDefaults.CreateRedirectFollowingHandler()), sp.GetRequiredService<ArmScopedTokenProvider>()));
        services.AddSingleton(sp =>
            new DeploymentOAuthAdapter(new HttpClient(TransportDefaults.CreateRedirectFollowingHandler()), sp.GetRequiredService<ArmScopedTokenProvider>()));
        services.AddSingleton(sp =>
            new DeploymentChannelAdapter(new HttpClient(TransportDefaults.CreateRedirectFollowingHandler()), sp.GetRequiredService<ArmScopedTokenProvider>()));
        services.AddSingleton<IDeploymentTransport>(sp => sp.GetRequiredService<DeploymentAdapter>());
        services.AddSingleton<IDeploymentOAuthTransport>(
            sp => sp.GetRequiredService<DeploymentOAuthAdapter>());
        services.AddSingleton<IDeploymentChannelTransport>(
            sp => sp.GetRequiredService<DeploymentChannelAdapter>());
        services.AddSingleton<DeploymentService>();
        services.AddSingleton<DeploymentOAuthService>();
        services.AddSingleton<DeploymentChannelService>();

        services.AddSingleton<IAgentHostingOperations>(sp => new AgentHostingOperations(
            sp.GetRequiredService<IAuthenticationSession>(),
            sp.GetRequiredService<IAuthProfileStore>(),
            sp.GetRequiredService<IInteractionPolicy>(),
            sp.GetRequiredService<IPlatformInfo>(),
            sp.GetRequiredService<IInteractiveMessagePresenter>(),
            sp.GetRequiredService<AuthSessionContext>(),
            sp.GetRequiredService<AbsProvisioningService>(),
            sp.GetRequiredService<AbsApplyService>(),
            sp.GetRequiredService<EnvironmentSelectionService>(),
            sp.GetRequiredService<AgentCreateService>(),
            sp.GetRequiredService<AgentPackageUploadService>(),
            sp.GetRequiredService<AgentDeployService>(),
            sp.GetService<IAuthProfileOperations>(),
            sp.GetService<IProfileCredentialProvider>(),
            sp.GetService<IProfileCachedAccountReader>(),
            sp.GetService<BlueprintIdentityService>(),
            sp.GetService<AppRegistrationIdentityService>(),
            sp.GetService<ManagedAgentServices>(),
            sp.GetService<DeploymentService>(),
            sp.GetService<DeploymentOAuthService>(),
            sp.GetService<DeploymentChannelService>(),
            sp.GetService<EvaluationServices>()));

        return services;
    }
}
