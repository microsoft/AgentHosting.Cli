using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using System.Net;
using System.Text;
using System.Text.Json;
using System.CommandLine;
using Azure.Core;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class HostProfileCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), ".host-profile-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private IServiceCollection Configure(bool interactive, Func<string, string?>? environment = null, string? configDirectory = null,
        PowerPlatformApiTargetConfig? target = null)
    {
        var services = new ServiceCollection().AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
        Program.ConfigureProfileAuthentication(services, new AgentHostAuthOptions { ConfigDirectory = configDirectory ?? _directory, InteractionAllowed = interactive,
            Cloud = target?.Cloud ?? CloudInstance.Public },
            target ?? PowerPlatformApiTargetConfig.Public, environment ?? (_ => null),
            () => throw new InvalidOperationException("Native construction is forbidden."),
            () => throw new InvalidOperationException("Legacy construction is forbidden."));
        services.AddSingleton(sp => AgentCreateFixtures.PendingService(sp.GetRequiredService<EnvironmentSelectionService>()));
        services.AddSingleton(sp => AgentDeployFixtures.PendingUploadService(sp.GetRequiredService<EnvironmentSelectionService>()));
        services.AddSingleton(sp => AgentDeployFixtures.PendingService(sp.GetRequiredService<EnvironmentSelectionService>()));
        services.AddSingleton<Core.Deployments.IDeploymentStore>(new InMemoryDeploymentRecordStore());
        return services;
    }

    [Theory]
    [InlineData(CloudInstance.UsNat, "--help")]
    [InlineData(CloudInstance.UsSec, "--help")]
    [InlineData(CloudInstance.UsNat, "--version")]
    [InlineData(CloudInstance.UsSec, "--version")]
    [InlineData(CloudInstance.UsNat, "clear")]
    [InlineData(CloudInstance.UsSec, "clear")]
    [InlineData(CloudInstance.UsNat, "list")]
    [InlineData(CloudInstance.UsSec, "list")]
    public async Task NationalProfileBootstrapAndNonStudioHostActivationRemainUsable(CloudInstance cloud, string verb)
    {
        Directory.CreateDirectory(_directory);
        var store = new Core.Auth.FileAuthProfileStore(Path.Combine(_directory, "auth-profiles-csharp.json"));
        await store.UpsertAsync(new AuthProfile
        {
            AccountKey = "principal", Username = "", Cloud = cloud, TenantId = EnvironmentFixtures.ContosoTenant,
            ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId,
            PrincipalType = AuthPrincipalType.ServicePrincipal, CredentialMethod = AuthCredentialMethod.DefaultAzureCredential,
        }, default);
        await store.SetActiveAsync("principal", default);
        var target = await Program.ResolvePowerPlatformApiTargetAsync(_directory);
        Assert.Equal(cloud, target.Cloud);
        var services = Configure(false, target: target);
        services.AddSingleton<ILegacyMsalTokenCacheBackend>(new EmptyLegacyCache());
        var profile = (await store.GetActiveProfileAsync(default))!;
        var credentials = new CapturedCredentials(profile, target);
        services.AddSingleton<IProfileCredentialProvider>(credentials);
        services.AddKeyedSingleton<HttpClient>("profile-environments", (_, _) => new(new InventoryHandler(target, useGlobalEndpoint: true)));
        services.AddAgentHostCopilotStudio(TextWriter.Null, target);
        Program.ConfigureConnectionConsent(services, target, false);
        using var provider = services.BuildServiceProvider();
        var facade = provider.GetRequiredService<IAgentHostingOperations>();
        Assert.True((await facade.GetAuthStatusAsync(default)).SignedIn);
        Assert.NotNull(provider.GetRequiredService<IProfileResourceTokenProvider>());
        Assert.NotNull(provider.GetRequiredService<IProfileEnvironmentClientFactory>());
        Assert.NotNull(provider.GetRequiredService<Microsoft.CopilotStudio.Sync.IWorkspaceSynchronizer>());
        Assert.NotNull(provider.GetRequiredService<IConnectionConsentFlow>());
        Assert.Null(credentials.Acquired);
        var args = verb == "clear" ? new[] { "auth", verb }
            : verb == "list" ? new[] { "env", "list", "--json" } : new[] { verb };
        var error = new StringWriter();
        Assert.True(0 == await new Hosting.AgentHostCli(facade, services: new Hosting.FacadeHandlerProvider(
            facade, provider.GetRequiredService<IAuthProfileOperations>()))
            .InvokeAsync(args, new StringWriter(), error, false), error.ToString());
        if (verb == "list")
        {
            Assert.Equal(cloud, credentials.Acquired!.Cloud);
            Assert.Equal(profile.ProfileId, credentials.Acquired.ProfileId);
            Assert.Equal(profile.TenantId, credentials.Tenant);
        }
        else
            Assert.Null(credentials.Acquired);
    }

    [Theory]
    [InlineData(CloudInstance.UsNat, AuthPrincipalType.User)]
    [InlineData(CloudInstance.UsSec, AuthPrincipalType.User)]
    [InlineData(CloudInstance.UsNat, AuthPrincipalType.ServicePrincipal)]
    [InlineData(CloudInstance.UsSec, AuthPrincipalType.ServicePrincipal)]
    public async Task NationalConsentDefersItsDependenciesAndPreservesAppOnlyPreflight(CloudInstance cloud, AuthPrincipalType principal)
    {
        var target = PowerPlatformApiTargetSelection.TargetFor(cloud, PowerPlatformApiRing.Production);
        var services = new ServiceCollection();
        Program.ConfigureConnectionConsent(services, target, false);
        using var provider = services.BuildServiceProvider();
        var flow = provider.GetRequiredService<IConnectionConsentFlow>();
        var environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "national", TenantId = EnvironmentFixtures.ContosoTenant,
            DataverseOrganization = new Uri("https://unverified.invalid/"),
            Context = new(new AuthProfile
            {
                AccountKey = "principal", Username = "", Cloud = cloud, PrincipalType = principal,
            }, target),
        };
        if (principal == AuthPrincipalType.User)
            await Assert.ThrowsAsync<EndpointTrustException>(() => flow.CreateConnectionAsync(environment, "connector", false, default));
        else
            await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => flow.CreateConnectionAsync(environment, "connector", false, default));
    }

    private sealed class EmptyLegacyCache : ILegacyMsalTokenCacheBackend
    {
        public IDisposable AcquireLease(CancellationToken ct) => new Lease();
        public Task<byte[]?> ReadAsync(CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => throw new InvalidOperationException("Acquisition is forbidden.");
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    public static IEnumerable<object[]> ValidInteractionArguments()
    {
        yield return [new[] { "auth", "login" }, true];
        yield return [new[] { "auth", "login", "--json" }, true];
        foreach (var name in new[] { "--non-interactive", OptionCatalog.Alias("--non-interactive") })
        {
            foreach (string[] option in new string[][]
            {
                [name], [name + "=true"], [name + "=false"], [name + ":true"], [name + ":false"],
                [name, "true"], [name, "false"], [name + "=TRUE"], [name + "=False"],
                [name, "TRUE"], [name, "False"],
            })
            {
                string[] args = ["auth", "login", .. option];
                var (parsed, flag) = ParseInteractionReference(args);
                if (parsed.Errors.Count == 0)
                    yield return [args, !parsed.GetValue(flag)];
            }
        }
    }

    private static (ParseResult Parsed, Option<bool> Flag) ParseInteractionReference(string[] args)
    {
        var root = new RootCommand();
        var flag = new Option<bool>("--non-interactive", OptionCatalog.Alias("--non-interactive")) { Recursive = true };
        root.Add(flag);
        root.Add(new Option<bool>("--json", OptionCatalog.Alias("--json")) { Recursive = true });
        var auth = new Command("auth");
        auth.Add(new Command("login"));
        root.Add(auth);
        return (root.Parse(args), flag);
    }

    [Theory]
    [MemberData(nameof(ValidInteractionArguments))]
    public void BootstrapInteractionPolicyMatchesTheActualBooleanParser(string[] args, bool allowed)
    {
        Assert.Equal(allowed, Hosting.AgentHostCli.ResolveInteractionAllowed(args, new TestFeatureFlags(FeatureName.LegacyAuth)));
    }

    [Theory]
    [InlineData("--account=--non-interactive=true")]
    [InlineData("--account=-ni=true")]
    [InlineData("-acc=--non-interactive=true")]
    [InlineData("-acc=-ni=true")]
    public void OptionValuesAreNotGlobalInteractionFlags(string account)
    {
        Assert.True(Hosting.AgentHostCli.ResolveInteractionAllowed(["auth", "login", account], new TestFeatureFlags(FeatureName.LegacyAuth)));
    }

    public static IEnumerable<object[]> InvalidInteractionArguments() =>
    [
        [new[] { "auth", "login", "--non-interactive=not-a-boolean" }],
        [new[] { "auth", "login", "-ni=1" }],
        [new[] { "auth", "login", "--non-interactive=false", "--unknown=value" }],
        [new[] { "auth", "login", "--non-interactive", "false", "unmatched" }],
    ];

    [Theory]
    [MemberData(nameof(InvalidInteractionArguments))]
    public void InvalidInputsNeverGrantInteractionPermission(string[] args)
    {
        Assert.NotEmpty(ParseInteractionReference(args).Parsed.Errors);
        Assert.False(Hosting.AgentHostCli.ResolveInteractionAllowed(args, new TestFeatureFlags(FeatureName.LegacyAuth)));
    }

    [Theory]
    [MemberData(nameof(InvalidInteractionArguments))]
    public async Task InvalidInputsAreRejectedBeforeRegisteredAuthenticationOrSourceAcquisition(string[] args)
    {
        var source = new CountingDeveloperSource();
        var flags = new TestFeatureFlags(FeatureName.LegacyAuth);
        var services = Configure(Hosting.AgentHostCli.ResolveInteractionAllowed(args, flags));
        services.AddSingleton<IProfileAzureCredentialSourceFactory>(source);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(2, await new Hosting.AgentHostCli(provider.GetRequiredService<IAgentHostingOperations>(), featureFlags: flags)
            .InvokeAsync(args, new StringWriter(), new StringWriter(), false));
        Assert.Equal(0, source.Constructions);
        Assert.Equal(0, source.TokenCalls);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("--non-interactive=true")]
    [InlineData("-ni=true")]
    public async Task ExplicitNoninteractiveBooleansBlockCreateAndLoginBeforeAnyInteractiveDependency(string option)
    {
        var allowed = Hosting.AgentHostCli.ResolveInteractionAllowed(["auth", "login", option], new TestFeatureFlags(FeatureName.LegacyAuth));
        var services = Configure(allowed);
        services.AddSingleton(new AgentHostAuthOptions
        {
            ConfigDirectory = _directory, ClientId = "invalid-client-must-never-be-parsed",
            InteractionAllowed = allowed,
        });
        using var provider = services.BuildServiceProvider();
        var create = await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            provider.GetRequiredService<IAuthProfileOperations>().CreateAsync(new(), default));
        Assert.Equal(12007, create.ErrorCode);
        Assert.Equal(3, create.ExitCode);
        var login = await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            provider.GetRequiredService<IAuthenticationSession>().LoginWithDeviceCodeAsync(new(),
                _ => throw new InvalidOperationException("Device callback is forbidden."), default));
        Assert.Equal(12007, login.ErrorCode);
        Assert.Equal(3, login.ExitCode);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [MemberData(nameof(ValidInteractionArguments))]
    public async Task RegisteredDeveloperPinnedRenewalUsesParsedInvocationPolicyBeforeSourceConstruction(string[] args, bool allowed)
    {
        using var stage = new ProfileCredentialTests.Harness();
        stage.Acquisition.Next = ProfileCredentialTests.DacToken(ProfileAzureSourceId.AzureCli);
        var profile = await stage.Provider.AuthenticateAsync(new()
        {
            ManagedIdentity = true, Scopes = ["https://service.test/.default"],
        }, default);
        stage.Profiles.Items.Add(profile);
        await stage.Provider.CompleteCreationAsync(profile, default);
        try
        {
            var source = new CountingDeveloperSource();
            var services = Configure(Hosting.AgentHostCli.ResolveInteractionAllowed(args, new TestFeatureFlags(FeatureName.LegacyAuth)),
                configDirectory: stage.Directory);
            services.AddSingleton<IOwnedProtectedStorageBackend>(stage.Backend);
            services.AddSingleton<IAuthProfileStore>(stage.Profiles);
            services.AddSingleton<IProfileAzureCredentialSourceFactory>(source);
            using var provider = services.BuildServiceProvider();
            var credentials = provider.GetRequiredService<IProfileCredentialProvider>();
            var failure = await Record.ExceptionAsync(() =>
                credentials.AcquireAsync(profile, ["https://service.test/.default"], null, default));
            Assert.Equal(allowed ? 1 : 0, source.Constructions);
            Assert.Equal(allowed ? 1 : 0, source.TokenCalls);
            if (allowed) Assert.Null(failure);
            else Assert.IsType<InteractionRequiredException>(failure);
        }
        finally
        {
            stage.Profiles.Items.Clear();
            await stage.Provider.RemoveOwnedCredentialAsync(profile, default);
        }
    }

    private sealed class CountingDeveloperSource : IProfileAzureCredentialSourceFactory
    {
        public int Constructions { get; private set; }
        public int TokenCalls { get; private set; }
        public IReadOnlyList<ProfileAzureSourceId> GetInitialSources(ProfileCredentialRuntimeOptions runtime, AuthorityInfo authority) =>
            throw new InvalidOperationException("Renewal must not discover sources.");
        public TokenCredential Create(ProfileAzureSourceId source, ProfileCredentialRuntimeOptions runtime, AuthorityInfo authority)
        {
            Assert.Equal(ProfileAzureSourceId.AzureCli, source);
            Constructions++;
            return new Credential(this);
        }
        private sealed class Credential(CountingDeveloperSource owner) : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                owner.TokenCalls++;
                return new(ProfileCredentialTests.DacToken(ProfileAzureSourceId.AzureCli).AccessToken, DateTimeOffset.MaxValue);
            }
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    [Theory]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, AuthPrincipalType.User)]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, AuthPrincipalType.ServicePrincipal)]
    [InlineData(AuthCredentialMethod.ClientSecret, AuthPrincipalType.ServicePrincipal)]
    [InlineData(AuthCredentialMethod.Certificate, AuthPrincipalType.ServicePrincipal)]
    public async Task MetadataAndPrincipalStatusNeverInitializeAnyCredentialOrNativeCache(AuthCredentialMethod method, AuthPrincipalType principal)
    {
        using var provider = Configure(false, _ => "invalid-irrelevant-secret-value").BuildServiceProvider();
        var profile = await provider.GetRequiredService<IAuthProfileManagementStore>().CreateAsync(new()
        {
            AccountKey = "principal", Username = "", CredentialMethod = method, PrincipalType = principal,
            CredentialReference = "FAKE_PRIVATE_REFERENCE_CANARY",
        }, default);
        Assert.Single((await provider.GetRequiredService<IAuthProfileOperations>().ListAsync(default)).Profiles);
        await provider.GetRequiredService<IAuthProfileOperations>().SelectAsync(
            new() { Selector = new(Index: 1), NonInteractive = true }, default);
        Assert.Empty(await provider.GetRequiredService<IProfileCachedAccountReader>().GetAccountsAsync(profile, default));
        Assert.NotNull(provider.GetRequiredService<MsalPublicClientProvider>());
        Assert.NotNull(provider.GetRequiredService<EnvironmentSelectionService>());
        var facade = provider.GetRequiredService<IAgentHostingOperations>();
        Assert.True((await facade.GetAuthStatusAsync(default)).SignedIn);
        Assert.Equal(0, await new Hosting.AgentHostCli(facade).InvokeAsync(["--help"], new StringWriter(), new StringWriter(), false));
        var host = new Hosting.AgentHostCli(facade, services: new Hosting.FacadeHandlerProvider(
            facade, provider.GetRequiredService<IAuthProfileOperations>()));
        foreach (string[] args in new string[][]
        {
            ["auth", "list"], ["auth", "status"], ["auth", "select", "--index", "1"],
            ["auth", "name", "--index", "1", "--name", "Dev"],
            ["auth", "who"],
        })
        {
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, await host.InvokeAsync([.. args, "--json", "--non-interactive=true"], output, error, false));
            Assert.DoesNotContain("FAKE_PRIVATE_REFERENCE_CANARY", output.ToString() + error);
            Assert.DoesNotContain("invalid-irrelevant-secret-value", output.ToString() + error);
        }
    }

    [Fact]
    public async Task NoninteractiveCreateFailsBeforeNativeCachesOrCredentialAcquisition()
    {
        using var provider = Configure(false).BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            provider.GetRequiredService<IAuthProfileOperations>().CreateAsync(new(), default));
        Assert.Equal(3, error.ExitCode);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("--deviceCode", 12007, 3)]
    [InlineData("--applicationId", 99002, 2)]
    [InlineData("--certificatePassword", 99002, 2)]
    public async Task ActualHostRejectsCreationBeforeRegisteredSourcesOrStorage(string option, int code, int exit)
    {
        var services = Configure(false);
        var source = new CountingDeveloperSource();
        services.AddSingleton<IProfileAzureCredentialSourceFactory>(source);
        using var provider = services.BuildServiceProvider();
        var facade = provider.GetRequiredService<IAgentHostingOperations>();
        var host = new Hosting.AgentHostCli(facade, services: new Hosting.FacadeHandlerProvider(
            facade, provider.GetRequiredService<IAuthProfileOperations>()));
        string[] args = option == "--deviceCode"
            ? ["auth", "create", option, "--non-interactive=true", "--json"]
            : ["auth", "create", "-mi", option, "FAKE_INVALID_INPUT", "--non-interactive=true", "--json"];
        var output = new StringWriter();
        Assert.Equal(exit, await host.InvokeAsync(args, output, new StringWriter(), false));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(code, json.RootElement.GetProperty("errorCode").GetInt32());
        Assert.DoesNotContain("FAKE_INVALID_INPUT", output.ToString());
        Assert.Equal(0, source.Constructions);
        Assert.Equal(0, source.TokenCalls);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void HostCapturesApprovedRuntimeInputsWithoutEagerValidation()
    {
        var inputs = new Dictionary<string, string>
        {
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "github-url", ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"] = "github-secret",
            ["MSAGENT_ADO_ID_TOKEN_REQUEST_URL"] = "ado-url", ["MSAGENT_ADO_ID_TOKEN_REQUEST_TOKEN"] = "ado-secret",
            ["AZURE_TENANT_ID"] = "invalid-tenant", ["AZURE_CLIENT_ID"] = "invalid-client",
            ["AZURE_CLIENT_SECRET"] = "client-secret", ["AZURE_CLIENT_CERTIFICATE_PATH"] = "missing.pfx",
            ["AZURE_CLIENT_CERTIFICATE_PASSWORD"] = "password", ["AZURE_FEDERATED_TOKEN_FILE"] = "missing-token",
            ["AZURE_TOKEN_CREDENTIALS"] = "AzureCliCredential",
        };
        using var provider = Configure(false, name => inputs.GetValueOrDefault(name)).BuildServiceProvider();
        var runtime = provider.GetRequiredService<ProfileCredentialRuntimeOptions>();
        inputs.Clear();
        Assert.Equal("ado-url", runtime.AzureDevOpsRequestUrl);
        Assert.Equal("ado-secret", runtime.AzureDevOpsRequestToken);
        Assert.Equal("github-secret", runtime.GitHubRequestToken);
        Assert.Equal("client-secret", runtime.AzureClientSecret);
        Assert.Equal("password", runtime.AzureCertificatePassword);
        Assert.Equal("missing-token", runtime.AzureFederatedTokenFile);
        Assert.Equal("AzureCliCredential", runtime.AzureCredentialSelection);
    }

    [Fact]
    public void InstalledEnvironmentCompositionAcceptsCapturedSovereignAndStagedProfiles()
    {
        using var provider = Configure(false).BuildServiceProvider();
        var target = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.China);
        var profile = new AuthProfile { ProfileId = "staged", AccountKey = "principal", Username = "", Cloud = CloudInstance.China };
        Assert.False(provider.GetRequiredService<EnvironmentSelectionService>().IsRetargeted(profile));
        var factory = provider.GetRequiredService<IProfileEnvironmentClientFactory>();
        Assert.NotNull(factory.CreateClient(new(profile, target)));
        Assert.NotNull(provider.GetRequiredService<IProfileResourceTokenProvider>());
        Assert.Same(provider.GetRequiredService<IProfileCredentialProvider>(), provider.GetRequiredService<IProfileConsentTokenAcquisition>());
    }

    [Fact]
    public async Task ContainerDisposesOnlyInitializedOwnedStorage()
    {
        var backend = new DisposableBackend();
        var services = new ServiceCollection().AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
        Program.ConfigureProfileAuthentication(services, new() { ConfigDirectory = _directory },
            PowerPlatformApiTargetConfig.Public, _ => null, () => backend,
            () => throw new InvalidOperationException("Unused legacy cache must not initialize."));
        using (var provider = services.BuildServiceProvider())
        {
            Assert.Null(await provider.GetRequiredService<IOwnedProtectedStorageBackend>()
                .ReadAsync(new(OwnedProtectedStorageKind.TokenCache, Guid.NewGuid().ToString("N")), default));
            Assert.False(backend.Disposed);
        }
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task NoninteractiveLegacyLoginDoesNotConstructItsInvalidConfiguredClient()
    {
        using var provider = Configure(false).BuildServiceProvider();
        var session = provider.GetRequiredService<IAuthenticationSession>();
        await Assert.ThrowsAsync<InteractionRequiredException>(() => session.LoginWithDeviceCodeAsync(new(),
            _ => throw new InvalidOperationException("Device callback is forbidden."), default));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void UnrelatedInvalidConsentSettingsDoNotPreventMetadataComposition()
    {
        var services = new ServiceCollection().AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
        Program.ConfigureProfileAuthentication(services, new()
        {
            ConfigDirectory = _directory, GraphClientId = "invalid-secret-value", ArmClientId = "invalid-secret-value",
        }, PowerPlatformApiTargetConfig.Public, _ => null,
            () => throw new InvalidOperationException("Native construction is forbidden."),
            () => throw new InvalidOperationException("Legacy construction is forbidden."));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IAuthProfileOperations>());
    }

    [Fact]
    public async Task CapturedGeographyCompositionDoesNotUseGlobalSelectedAccountProvider()
    {
        var services = Configure(false);
        services.AddSingleton<ISelectedAccountTokenProvider>(new ForbiddenSelectedTokens());
        using var provider = services.BuildServiceProvider();
        Assert.Null(await provider.GetRequiredService<IEnvironmentGeographyClient>().GetGeographyAsync(new("env", "tenant"), default));
    }

    [Fact]
    public async Task GeographySkipsCapturedWorkloadWithoutConstructingAnySource()
    {
        using var provider = Configure(false).BuildServiceProvider();
        var profile = new AuthProfile
        {
            ProfileId = "inactive", AccountKey = "principal", Username = "",
            PrincipalType = AuthPrincipalType.ServicePrincipal, CredentialMethod = AuthCredentialMethod.ClientSecret,
            Cloud = CloudInstance.UsGov,
        };
        var factory = provider.GetRequiredService<IProfileEnvironmentClientFactory>();
        _ = factory.CreateClient(new(profile, PowerPlatformApiTargetSelection.TargetFor(profile.Cloud)));
        Assert.Null(await provider.GetRequiredService<IEnvironmentGeographyClient>().GetGeographyAsync(new("env", "tenant"), default));
    }

    [Fact]
    public async Task PlaintextOptInAppliesOnlyToTokenCachesAndNeverCredentials()
    {
        using var backend = new DeferredOwnedProtectedStorage(
            () => new FailingProtectedStorage(NativeStorageFailure.Unavailable), _directory);
        var token = new OwnedProtectedStorageReference(OwnedProtectedStorageKind.TokenCache, Guid.NewGuid().ToString("N"));
        await backend.WriteAsync(token, new byte[] { 1, 2, 3 }, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, await backend.ReadAsync(token, default));
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.WriteAsync(
            new(OwnedProtectedStorageKind.Credential, Guid.NewGuid().ToString("N")), new byte[] { 4, 5, 6 }, default));
    }

    [Fact]
    public async Task TokenPlaintextOptInNeverHidesMalformedNativeStorage()
    {
        using var backend = new DeferredOwnedProtectedStorage(
            () => new FailingProtectedStorage(NativeStorageFailure.Malformed), _directory);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(
            new(OwnedProtectedStorageKind.TokenCache, Guid.NewGuid().ToString("N")), default));
        Assert.False(Directory.Exists(_directory));
    }

    private sealed class FailingProtectedStorage(NativeStorageFailure failure) : OwnedProtectedStorageBackend("fake")
    {
        protected override Task<byte[]?> ReadCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct) =>
            throw new NativeStorageException(failure);
        protected override Task WriteCoreAsync(OwnedProtectedStorageReference reference, byte[] data, CancellationToken ct) =>
            throw new NativeStorageException(failure);
        protected override Task ClearCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct) =>
            throw new NativeStorageException(failure);
    }

    [Fact]
    public async Task StagedEnvironmentValidationRetainsItsCapturedSovereignProfile()
    {
        var services = Configure(false);
        var store = new FileAuthProfileStore(Path.Combine(_directory, AgentHostAuthOptions.ProfilesFileName));
        var target = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.China);
        var captured = new AuthProfile
        {
            ProfileId = Guid.NewGuid().ToString("N"), AccountKey = "china-app", Username = "",
            ClientId = "a19c2c4d-ef49-47ec-92b1-f761f33b3f16", TenantId = EnvironmentFixtures.ContosoTenant,
            Cloud = CloudInstance.China, Authority = target.Authority.AuthorityUrl.AbsoluteUri,
            PrincipalType = AuthPrincipalType.ServicePrincipal, CredentialMethod = AuthCredentialMethod.ClientSecret,
        };
        await store.CreateAsync(new() { AccountKey = "public-app", Username = "" }, default);
        var credentials = new CapturedCredentials(captured, target);
        services.AddSingleton<IProfileCredentialProvider>(credentials);
        using var http = new HttpClient(new InventoryHandler(target));
        services.AddKeyedSingleton<HttpClient>("profile-environments", http);
        using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<IAuthProfileOperations>();

        await operations.CreateAsync(new AuthCreateRequest
        {
            Target = target, Tenant = captured.TenantId, ApplicationId = captured.ClientId,
            ClientSecret = "synthetic-create-secret", Environment = "env-china", NonInteractive = true,
        }, default);
        Assert.Equal("env-china", credentials.Completed?.DefaultEnvironment?.EnvironmentId);

        Assert.Equal(captured, credentials.Acquired);
        Assert.Equal(captured.TenantId, credentials.Tenant);
        Assert.Equal([target.Audience.TrimEnd('/') + "/.default"], credentials.Scopes);
        var destination = new Hosting.CapturedProfileTelemetryDestinationResolver(
            provider.GetRequiredService<Hosting.CapturedProfileClients>(),
            provider.GetRequiredService<IEnvironmentGeographyClient>(), PowerPlatformApiTargetConfig.Public, true, null);
        Assert.False(destination.ActivationAvailable);
        Assert.Null(await destination.ResolveAsync(
            TelemetryEnvironmentTarget.Resolved([new("env-china", captured.TenantId!)]), default));
    }

    private sealed class InventoryHandler(PowerPlatformApiTargetConfig target, bool useGlobalEndpoint = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(target.GlobalEndpoint.Host, request.RequestUri?.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    value = new[] { new { name = "env-china", properties = new
                    { tenantId = EnvironmentFixtures.ContosoTenant, apiEndpoint = useGlobalEndpoint ? target.GlobalEndpoint.AbsoluteUri : new UriBuilder(target.GlobalEndpoint)
                        { Host = "advertised." + target.GlobalEndpoint.Host }.Uri.AbsoluteUri } } },
                })),
            });
        }
    }

    [Fact]
    public async Task TelemetryCannotSubstituteAnotherProfileForAnAmbiguousEnvironment()
        {
            var target = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.China);
            var profile = new AuthProfile
            {
                ProfileId = "profile-a", AccountKey = "app", Username = "", Cloud = CloudInstance.China,
                TenantId = EnvironmentFixtures.ContosoTenant, ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId,
                PrincipalType = AuthPrincipalType.ServicePrincipal, CredentialMethod = AuthCredentialMethod.ClientSecret,
            };
            using var http = new HttpClient(new InventoryHandler(target));
            var clients = new Hosting.CapturedProfileClients(http,
                new ProfileResourceTokenProvider(new CapturedCredentials(profile, target)), null);
            await clients.CreateClient(new(profile, target)).ListEnvironmentsAsync(default);
            await clients.CreateClient(new(profile with { ProfileId = "profile-b" }, target)).ListEnvironmentsAsync(default);
            Assert.Null(clients.For(new("env-china", profile.TenantId!)));
        }

    private sealed class CapturedCredentials(AuthProfile staged, PowerPlatformApiTargetConfig target) : IProfileCredentialProvider
    {
        public AuthProfile? Acquired { get; private set; }
        public AuthProfile? Completed { get; private set; }
        public string? Tenant { get; private set; }
        public IReadOnlyList<string>? Scopes { get; private set; }
        public Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct) => Task.FromResult(staged);
        public Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? tenant, CancellationToken ct)
        {
            Acquired = profile; Scopes = scopes; Tenant = tenant;
            var payload = JsonSerializer.Serialize(new { aud = target.Audience, appid = profile.ClientId,
                tid = profile.TenantId, idtyp = "app" });
            var token = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=')
                .Replace('+', '-').Replace('/', '_') + ".signature";
            return Task.FromResult(new TokenAcquisitionResult
            { AccessToken = token, TenantId = profile.TenantId, ExpiresOn = DateTimeOffset.MaxValue });
        }

        public Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct)
        { Completed = profile; return Task.CompletedTask; }
        public Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct) => throw new InvalidOperationException("Cleanup was not expected.");
    }

    [Fact]
    public async Task DefaultEnvironmentObservationRetainsCapturedProfileWithoutInventoryOrActiveLookup()
    {
        var services = Configure(false);
        var observer = new CapturedObserver();
        services.AddSingleton<IResolvedEnvironmentObserver>(sp => new Hosting.CapturedProfileEnvironmentObserver(
            sp.GetRequiredService<Hosting.CapturedProfileClients>(), observer));
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IAuthProfileManagementStore>();
        var profile = await store.CreateAsync(new()
        {
            AccountKey = "sovereign", Username = "", TenantId = EnvironmentFixtures.ContosoTenant,
            Cloud = CloudInstance.UsGov, DefaultEnvironment = new()
            {
                EnvironmentId = "env-gov", TenantId = EnvironmentFixtures.ContosoTenant, DisplayName = "Gov",
                ApiAudience = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.UsGov).Audience,
                ApiEndpoint = new Uri("https://advertised.api.gov.powerplatform.microsoft.us"),
            },
        }, default);
        var environment = await provider.GetRequiredService<EnvironmentSelectionService>().ResolveForCommandAsync(null, default);
        var context = provider.GetRequiredService<Hosting.CapturedProfileClients>().For(new("env-gov", profile.TenantId!));
        Assert.Equal(profile, context?.Profile);
        Assert.Equal(environment.Context, context);
        Assert.Equal(1, observer.Calls);
    }

    private sealed class CapturedObserver : IResolvedEnvironmentObserver
    {
        internal int Calls;
        public void Observe(ResolvedEnvironmentTarget target) => Calls++;
    }

    [Fact]
    public async Task TelemetryDoesNotCombineDifferentCapturedRevisionsOfOneProfile()
    {
        var target = PowerPlatformApiTargetConfig.Public;
        var profile = new AuthProfile
        {
            ProfileId = "profile-a", AccountKey = "account-a", Username = "user@example.test",
            Revision = 1, TenantId = EnvironmentFixtures.ContosoTenant,
        };
        using var http = new HttpClient(new InventoryHandler(target));
        var clients = new Hosting.CapturedProfileClients(http,
            new ProfileResourceTokenProvider(new CapturedCredentials(profile, target)), profile);
        ResolvedEnvironmentTarget first = new("env-first", profile.TenantId!);
        ResolvedEnvironmentTarget second = new("env-second", profile.TenantId!);
        clients.Capture(first, new(profile, target));
        clients.Capture(second, new(profile with { Revision = 2 }, target));
        var geography = new CountingGeography();
        var resolver = new Hosting.CapturedProfileTelemetryDestinationResolver(
            clients, geography, target, true, null);

        Assert.Null(await resolver.ResolveAsync(TelemetryEnvironmentTarget.Resolved([first, second]), default));
        Assert.Equal(0, geography.Calls);
    }

    private sealed class CountingGeography : IEnvironmentGeographyClient
    {
        public int Calls { get; private set; }
        public Task<string?> GetGeographyAsync(ResolvedEnvironmentTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task CachedLegacyUserStatusReadsOnlyTheInjectedDedicatedCache()
    {
        var services = Configure(false);
        var cache = new MetadataLegacyCache();
        services.AddSingleton<ILegacyMsalTokenCacheBackend>(cache);
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAuthProfileManagementStore>().CreateAsync(new()
        {
            AccountKey = "22222222-2222-2222-2222-222222222222." + EnvironmentFixtures.ContosoTenant, Username = "legacy@contoso.test",
            TenantId = EnvironmentFixtures.ContosoTenant, ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId,
            CredentialMethod = AuthCredentialMethod.DeviceCode, PrincipalType = AuthPrincipalType.User,
        }, default);
        var status = await provider.GetRequiredService<IAgentHostingOperations>().GetAuthStatusAsync(default);
        Assert.True(cache.Reads > 0);
        Assert.True(status.SignedIn);
        Assert.Equal("legacy@contoso.test", status.Account);
    }

    private sealed class MetadataLegacyCache : ILegacyMsalTokenCacheBackend
    {
        internal int Reads;
        public IDisposable AcquireLease(CancellationToken ct) => new EmptyLease();
        public Task<byte[]?> ReadAsync(CancellationToken ct)
        {
            Reads++;
            var account = new Dictionary<string, string>
            {
                ["home_account_id"] = "22222222-2222-2222-2222-222222222222." + EnvironmentFixtures.ContosoTenant,
                ["environment"] = "login.microsoftonline.com", ["realm"] = EnvironmentFixtures.ContosoTenant,
                ["local_account_id"] = "22222222-2222-2222-2222-222222222222", ["username"] = "legacy@contoso.test", ["authority_type"] = "MSSTS",
                ["client_info"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
                { uid = "22222222-2222-2222-2222-222222222222", utid = EnvironmentFixtures.ContosoTenant })),
            };
            var home = account["home_account_id"];
            var client = PowerPlatformApiPermissions.AgentHostCliApplicationId;
            return Task.FromResult<byte[]?>(JsonSerializer.SerializeToUtf8Bytes(new
            {
                Account = new Dictionary<string, object>
                { [home + "-login.microsoftonline.com-" + EnvironmentFixtures.ContosoTenant] = account },
                RefreshToken = new Dictionary<string, object>
                {
                    [home + "-login.microsoftonline.com-refreshtoken-" + client + "--"] = new Dictionary<string, string>
                    {
                        ["home_account_id"] = home, ["environment"] = "login.microsoftonline.com",
                        ["credential_type"] = "RefreshToken", ["client_id"] = client, ["secret"] = "synthetic-refresh-token",
                    },
                },
            }));
        }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => throw new InvalidOperationException("Metadata must not write a token.");
        public Task ClearAsync(CancellationToken ct) => throw new InvalidOperationException("Metadata must not clear a cache.");
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }

    private sealed class ForbiddenSelectedTokens : ISelectedAccountTokenProvider
    {
        public Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(string resource, string tenant, CancellationToken ct) =>
            throw new InvalidOperationException("Global active-profile acquisition is forbidden.");
    }

    private sealed class DisposableBackend : IOwnedProtectedStorageBackend, IDisposable
    {
        internal bool Disposed;
        public Task<byte[]?> ReadAsync(OwnedProtectedStorageReference reference, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(OwnedProtectedStorageReference reference, ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(OwnedProtectedStorageReference reference, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
