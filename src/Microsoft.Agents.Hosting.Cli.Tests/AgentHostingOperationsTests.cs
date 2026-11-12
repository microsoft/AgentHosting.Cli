using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Commands.Features;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline tests for the real <see cref="AgentHostingOperations"/> facade. They drive the auth command
/// surface against a spy <see cref="FakeAuthenticationSession"/> and a real, temp-backed
/// <see cref="FileAuthProfileStore"/> — no MSAL, browser, broker, or token acquisition. They assert the
/// login/status/switch/logout behavior <em>and</em> the negative guarantees: the non-interactive gate
/// opens no UI, and status/switch/logout never touch a login flow.
/// </summary>
public sealed class AgentHostingOperationsTests
{
    private const string MsitTenant = MicrosoftCorporateAccountPolicy.MicrosoftTenantId;
    private const string ContextClientId = "22222222-3333-4444-5555-666666666666";
    private const string BrokerFailureReason = "WAM_provider_error_3399614476, correlation ID 00000000-0000-0000-0000-000000000001";
    private const string BrokerFailedNotice = "Broker sign-in failed (WAM_provider_error_3399614476, correlation ID 00000000-0000-0000-0000-000000000001), so msagent will use a device code.";

    private static readonly AuthSessionContext Context = new()
    {
        ClientId = ContextClientId,
        Cloud = CloudInstance.Public,
        Authority = "https://login.microsoftonline.com/organizations",
    };

    [Theory]
    [InlineData(18)]
    [InlineData(19)]
    public void FacadePreservesExistingConstructorSignaturesForCompiledEmbedders(int parameterCount)
    {
        Assert.Contains(typeof(AgentHostingOperations).GetConstructors(),
            constructor => constructor.GetParameters().Length == parameterCount);
    }

    private static AgentHostingOperations Create(
        IAuthenticationSession session,
        IAuthProfileStore store,
        IPlatformInfo platform,
        bool interactionAllowed = true,
        RecordingMessagePresenter? presenter = null,
        EnvironmentSelectionService? environments = null,
        PowerPlatformApiTargetConfig? apiTarget = null,
        IAuthProfileOperations? authProfiles = null,
        IProfileCredentialProvider? credentials = null,
        IProfileCachedAccountReader? cachedAccounts = null)
    {
        var reader = new PendingAbsStateReader();
        var selection = environments ?? EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            new InMemoryEnvironmentSelectionStore(),
            store,
            apiTarget: apiTarget);
        return new(
            session,
            store,
            new StaticInteractionPolicy(interactionAllowed),
            platform,
            presenter ?? new RecordingMessagePresenter(),
            Context,
            new AbsProvisioningService(reader),
            new AbsApplyService(reader, new PendingAbsMutationPort()),
            selection,
            AgentCreateFixtures.PendingService(selection),
            AgentDeployFixtures.PendingUploadService(selection),
            AgentDeployFixtures.PendingService(selection),
            authProfiles: authProfiles, profileCredentials: credentials, profileCachedAccounts: cachedAccounts,
            blueprintIdentity: null, appRegistrationIdentity: null, managedAgents: null, deployments: null,
            deploymentOAuth: null, deploymentChannels: null, evaluations: null);
    }

    private static AuthAccount Account(
        string username,
        string homeAccountId,
        string? tenantId = null,
        Guid? authenticatedTenantId = null,
        Guid? principalObjectId = null,
        string? authenticatedClientId = null) => new()
    {
        Username = username,
        HomeAccountId = homeAccountId,
        TenantId = tenantId,
        AuthenticatedTenantId = authenticatedTenantId,
        PrincipalObjectId = principalObjectId,
        AuthenticatedClientId = authenticatedClientId,
    };

    [Fact]
    public async Task Status_CapturedProfileDoesNotMixWithConcurrentSelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var first = await store.CreateAsync(new()
        {
            AccountKey = "a", Username = "a@contoso.com", TenantId = "tenant-a", FlowType = "broker",
            DefaultEnvironment = new()
            {
                EnvironmentId = "env-a", DisplayName = "A", TenantId = "tenant-a",
                ApiEndpoint = new Uri("https://env.api.powerplatform.com"),
                ApiAudience = PowerPlatformApiTargetConfig.Public.Audience,
            },
        }, default);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = new CapturedStore(store, async () => { captured.SetResult(); await resume.Task; });
        var session = new FakeAuthenticationSession();
        var ops = Create(session, racing, FakePlatformInfo.Windows());
        var status = ops.GetAuthStatusAsync(default);
        await captured.Task;
        await store.CreateAsync(new()
        {
            AccountKey = "b", Username = "b@contoso.com", TenantId = "tenant-b",
            Ring = PowerPlatformApiRing.Preprod, FlowType = "broker",
        }, default);
        resume.SetResult();
        var report = await status;
        Assert.Equal(first.Username, report.Account);
        Assert.Equal("prod", report.Cloud);
        Assert.Equal("env-a", report.EnvironmentId);
        Assert.True(report.EnvironmentTenantBound);
        Assert.Equal(1, racing.Reads);
        Assert.Equal(0, session.GetAccountsCalls);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Broker)]
    [InlineData(AuthCredentialMethod.ClientSecret)]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential)]
    public async Task Status_BrokerAndWorkloadAreMetadataOnly(AuthCredentialMethod method)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.CreateAsync(new()
        {
            AccountKey = "a", Username = "a@contoso.com", CredentialMethod = method,
            CredentialReference = "owned-reference",
        }, default);
        var session = new FakeAuthenticationSession();
        var credentials = new ProfileCredentials();
        var report = await Create(session, store, FakePlatformInfo.Windows(), credentials: credentials, cachedAccounts: credentials)
            .GetAuthStatusAsync(default);
        Assert.Equal("signed-in", report.Status);
        Assert.Equal(0, session.GetAccountsCalls);
        Assert.Equal(0, credentials.Reads);
        Assert.Equal(0, credentials.Authentications);
    }

    [Fact]
    public async Task Status_OwnedPrivateCacheUsesExplicitCapturedSnapshotAndPropagatesErrors()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var a = await store.CreateAsync(new()
        {
            AccountKey = "a", Username = "a@contoso.com", FlowType = "interactive",
            CredentialMethod = AuthCredentialMethod.Browser, CredentialReference = "owned-reference",
        }, default);
        var credentials = new ProfileCredentials { Accounts = [Account("a@contoso.com", "a")] };
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows(), credentials: credentials, cachedAccounts: credentials);
        Assert.Equal("signed-in", (await ops.GetAuthStatusAsync(default)).Status);
        Assert.Equal(a, credentials.Captured);
        credentials.Accounts = [];
        Assert.Equal("stale", (await ops.GetAuthStatusAsync(default)).Status);
        credentials.ReadFailure = new CacheProtectionUnavailableException("Unavailable.");
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => ops.GetAuthStatusAsync(default));
        Assert.Equal(0, session.GetAccountsCalls);
    }

    [Fact]
    public async Task Login_ProfileServiceStagesRatherThanUsingLegacySessionAndPreservesHintAndScopes()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var credentials = new ProfileCredentials();
        var selections = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(new FakeEnvironmentClient(), selections, store);
        var service = new AuthProfileOperations(store, store, credentials, environments, selections,
            new StaticInteractionPolicy(true), new FixedClock(DateTimeOffset.UnixEpoch), credentials);
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Named("unknown"), environments: environments, authProfiles: service);
        var result = await ops.LoginAsync(new() { Account = "Hint@contoso.com", Scopes = ["scope.read"], Ring = PowerPlatformApiRing.Test }, default);
        Assert.Equal("test", result.Cloud);
        Assert.Equal("Hint@contoso.com", credentials.Request!.LoginHint);
        Assert.Null(credentials.Request.Name);
        Assert.Equal(["scope.read"], credentials.Request.Scopes);
        Assert.True(credentials.Request.DeviceCode);
        Assert.Equal(PowerPlatformApiTargetConfig.Test, credentials.Request.Target);
        Assert.Equal(0, session.TotalLoginCalls);
        Assert.NotNull((await store.GetActiveProfileAsync(default))!.CredentialReference);
    }

    public static TheoryData<string[]> BlankLoginCloudInputs => new()
    {
        { [] },
        { ["--cloud", ""] },
        { ["--cloud", " \t "] },
        { ["--cloud="] },
        { ["--cloud:"] },
        { ["--cloud= \t "] },
        { ["--cloud: \t "] },
        { ["-ci", ""] },
        { ["-ci", " \t "] },
        { ["-ci="] },
        { ["-ci:"] },
        { ["-ci= \t "] },
        { ["-ci: \t "] },
    };

    [Theory]
    [MemberData(nameof(BlankLoginCloudInputs))]
    public async Task Login_HostOmittedOrBlankCloudInheritsCapturedProfileTarget(string[] options)
    {
        var actual = await InvokeLoginHostWithPreprodAsync(options);
        Assert.Equal(0, actual.Exit);
        Assert.Equal(PowerPlatformApiTargetConfig.Preprod, actual.Target);
        Assert.Equal(1, actual.Authentications);
        Assert.Equal(0, actual.LegacyLogins);
        using var output = JsonDocument.Parse(actual.Output);
        Assert.Equal("preprod", output.RootElement.GetProperty("cloud").GetString());
    }

    [Theory]
    [InlineData("Public", CloudInstance.Public, PowerPlatformApiRing.Production)]
    [InlineData("Preprod", CloudInstance.Public, PowerPlatformApiRing.Preprod)]
    [InlineData("Test", CloudInstance.Public, PowerPlatformApiRing.Test)]
    [InlineData("prod", CloudInstance.Public, PowerPlatformApiRing.Production)]
    [InlineData("production", CloudInstance.Public, PowerPlatformApiRing.Production)]
    [InlineData("preprod", CloudInstance.Public, PowerPlatformApiRing.Preprod)]
    [InlineData("test", CloudInstance.Public, PowerPlatformApiRing.Test)]
    [InlineData("UsGov", CloudInstance.UsGov, PowerPlatformApiRing.Production)]
    [InlineData("UsGovHigh", CloudInstance.UsGovHigh, PowerPlatformApiRing.Production)]
    [InlineData("UsGovDod", CloudInstance.UsGovDod, PowerPlatformApiRing.Production)]
    [InlineData("China", CloudInstance.China, PowerPlatformApiRing.Production)]
    [InlineData("UsNat", CloudInstance.UsNat, PowerPlatformApiRing.Production)]
    [InlineData("UsSec", CloudInstance.UsSec, PowerPlatformApiRing.Production)]
    public async Task Login_HostExplicitCloudStillOverridesCapturedProfileTarget(
        string token, CloudInstance cloud, PowerPlatformApiRing ring)
    {
        var actual = await InvokeLoginHostWithPreprodAsync(["-ci=" + token]);
        Assert.Equal(0, actual.Exit);
        Assert.Equal(PowerPlatformApiTargetSelection.TargetFor(cloud, ring), actual.Target);
        Assert.Equal(1, actual.Authentications);
        Assert.Equal(0, actual.LegacyLogins);
    }

    [Theory]
    [InlineData(false, "unsupported", 2, 99002)]
    [InlineData(true, "", 3, 12007)]
    [InlineData(true, " \t ", 3, 12007)]
    [InlineData(true, "unsupported", 3, 12007)]
    public async Task Login_HostCloudRejectionsNeverStartCredentialOrLegacySources(
        bool nonInteractive, string cloud, int exit, int errorCode)
    {
        var actual = await InvokeLoginHostWithPreprodAsync(["--cloud=" + cloud], nonInteractive);
        Assert.Equal(exit, actual.Exit);
        Assert.Null(actual.Target);
        Assert.Equal(0, actual.Authentications);
        Assert.Equal(0, actual.LegacyLogins);
        using var output = JsonDocument.Parse(actual.Output);
        Assert.Equal(errorCode, output.RootElement.GetProperty("errorCode").GetInt32());
    }

    private static async Task<(int Exit, string Output, PowerPlatformApiTargetConfig? Target, int Authentications, int LegacyLogins)>
        InvokeLoginHostWithPreprodAsync(string[] options, bool nonInteractive = false)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.CreateAsync(new() { AccountKey = "prior", Username = "prior@contoso.com", Ring = PowerPlatformApiRing.Preprod }, default);
        var credentials = new ProfileCredentials();
        var selections = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(new FakeEnvironmentClient(), selections, store);
        var profiles = new AuthProfileOperations(store, store, credentials, environments, selections,
            new StaticInteractionPolicy(true), new FixedClock(DateTimeOffset.UnixEpoch), credentials);
        var session = new FakeAuthenticationSession();
        var core = Create(session, store, FakePlatformInfo.Named("unknown"), environments: environments, authProfiles: profiles);
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = await new AgentHostCli(core, featureFlags: new TestFeatureFlags(FeatureName.LegacyAuth)).InvokeAsync(
            ["auth", "login", .. options, "--json", $"--non-interactive={nonInteractive}"], output, error, false);
        Assert.Empty(error.ToString());
        return (exit, output.ToString(), credentials.Request?.Target, credentials.Authentications, session.TotalLoginCalls);
    }

    [Fact]
    public async Task Login_DistinctNamedTargetsAreAmbiguousBeforeAnySignIn()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.CreateAsync(new() { AccountKey = "same", Username = "same@contoso.com" }, default);
        await store.CreateAsync(new() { AccountKey = "same", Username = "same@contoso.com", Ring = PowerPlatformApiRing.Test }, default);
        var session = new FakeAuthenticationSession { BrokerResult = Account("same@contoso.com", "same") };
        await Assert.ThrowsAsync<UsageException>(() =>
            Create(session, store, FakePlatformInfo.Windows()).LoginAsync(new() { Account = "same@contoso.com" }, default));
        Assert.Equal(0, session.TotalLoginCalls);
    }

    [Fact]
    public async Task Login_ExplicitTargetRingConflictFailsBeforeProfileOrSessionAccess()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await File.WriteAllTextAsync(dir.File("profiles.json"), "{corrupt");
        var session = new FakeAuthenticationSession();
        await Assert.ThrowsAsync<UsageException>(() =>
            Create(session, store, FakePlatformInfo.Windows()).LoginAsync(new()
            {
                Target = PowerPlatformApiTargetConfig.Test, Ring = PowerPlatformApiRing.Preprod,
            }, default));
        Assert.Equal(0, session.TotalLoginCalls);
    }

    [Fact]
    public async Task RegisteredFacadeUsesProfileTransactionAndClearCallsOwnedCleanupExactlyOnce()
    {
        using var h = new ProfileCredentialTests.Harness();
        var credentials = new ProfileCredentials();
        var legacyCache = new LegacyCache();
        using var services = RegisteredProfileServices(h, legacyCache, credentials);
        Assert.IsType<AuthProfileOperations>(services.GetRequiredService<IAuthProfileOperations>());
        var facade = services.GetRequiredService<IAgentHostingOperations>();
        var result = await facade.LoginAsync(new() { DeviceCode = true }, default);
        Assert.Equal("signed-in", result.Status);
        Assert.Equal(1, credentials.Authentications);
        Assert.Equal("signed-out", (await facade.LogoutAsync(default)).Status);
        Assert.Equal(1, credentials.Clears);
        Assert.Equal(1, legacyCache.Clears);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredCleanupFailuresPreservePrimaryRemediationAndDurableState(bool committed)
    {
        using var h = new ProfileCredentialTests.Harness();
        var legacyCache = new LegacyCache();
        using var services = RegisteredProfileServices(h, legacyCache);
        var facade = services.GetRequiredService<IAgentHostingOperations>();
        AgentHostingException primary = committed
            ? new CacheProtectionUnavailableException("safe-primary", "Restore protected storage access.")
            : new InteractionRequiredException("safe-primary", "Resolve the primary consent requirement.");
        if (committed)
            await facade.LoginAsync(new() { DeviceCode = true }, default);
        else
            h.Acquisition.Failure = primary;
        h.Backend.ClearFailure = primary;
        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => committed
            ? facade.LogoutAsync(default)
            : facade.LoginAsync(new() { DeviceCode = true }, default));
        Assert.Equal(primary.ErrorCode, error.ErrorCode);
        Assert.Equal(primary.ExitCode, error.ExitCode);
        Assert.Contains(primary.Remediation!, error.Remediation);
        Assert.Contains("Retry owned credential cleanup", error.Remediation);
        var state = Assert.IsAssignableFrom<IAuthOperationPartialState>(error);
        Assert.Equal(committed, state.MetadataCommitted);
        Assert.True(state.CleanupIncomplete);
        Assert.Null(state.Profile);
        Assert.Empty(await services.GetRequiredService<IAuthProfileStore>().GetProfilesAsync(default));
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        Assert.Single(h.Acquisition.Requests);
        Assert.Equal(0, legacyCache.Clears);
        h.Backend.ClearFailure = null;
        await services.GetRequiredService<IOwnedProfileCredentialCleanup>().ClearOwnedCredentialsAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredAcquisitionCancellationCarriesCoreStateAndCallerToken(bool cleanupFails)
    {
        using var h = new ProfileCredentialTests.Harness();
        using var cancel = new CancellationTokenSource();
        var legacyCache = new LegacyCache();
        using var services = RegisteredProfileServices(h, legacyCache);
        h.Backend.FailClear = cleanupFails;
        h.Acquisition.BeforeAcquire = _ => { cancel.Cancel(); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<AuthOperationCanceledException>(() =>
            services.GetRequiredService<IAgentHostingOperations>().LoginAsync(new() { DeviceCode = true }, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.False(error.MetadataCommitted);
        Assert.Equal(cleanupFails, error.CleanupIncomplete);
        Assert.Null(error.Profile);
        Assert.Empty(await services.GetRequiredService<IAuthProfileStore>().GetProfilesAsync(default));
        Assert.Equal(cleanupFails, Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories).Length != 0);
        Assert.Equal(0, legacyCache.Clears);
        h.Backend.FailClear = false;
        await services.GetRequiredService<IOwnedProfileCredentialCleanup>().ClearOwnedCredentialsAsync(default);
    }

    [Fact]
    public async Task RegisteredLogoutCancellationCarriesCommittedStateAndCallerToken()
    {
        using var h = new ProfileCredentialTests.Harness();
        using var cancel = new CancellationTokenSource();
        var legacyCache = new LegacyCache();
        using var services = RegisteredProfileServices(h, legacyCache);
        var facade = services.GetRequiredService<IAgentHostingOperations>();
        await facade.LoginAsync(new() { DeviceCode = true }, default);
        h.Backend.AfterClear = cancel.Cancel;
        var error = await Assert.ThrowsAsync<AuthOperationCanceledException>(() => facade.LogoutAsync(cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(error.MetadataCommitted);
        Assert.True(error.CleanupIncomplete);
        Assert.Null(error.Profile);
        Assert.Empty(await services.GetRequiredService<IAuthProfileStore>().GetProfilesAsync(default));
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        Assert.Equal(0, legacyCache.Clears);
        h.Backend.AfterClear = null;
        await services.GetRequiredService<IOwnedProfileCredentialCleanup>().ClearOwnedCredentialsAsync(default);
    }

    private static ServiceProvider RegisteredProfileServices(ProfileCredentialTests.Harness h,
        LegacyCache legacyCache, ProfileCredentials? credentials = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddAgentHostAuthentication(new() { ConfigDirectory = h.Directory });
        registrations.AddAgentHostProfileCredentials(h.Backend, new(), legacyCache);
        registrations.AddSingleton<IProfileTokenAcquisition>(h.Acquisition);
        registrations.AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
        registrations.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient());
        if (credentials is not null)
        {
            registrations.AddSingleton<IProfileCredentialProvider>(credentials);
            registrations.AddSingleton<IProfileCachedAccountReader>(credentials);
            registrations.AddSingleton<IOwnedProfileCredentialCleanup>(credentials);
        }
        registrations.AddSingleton(sp => AgentCreateFixtures.PendingService(sp.GetRequiredService<EnvironmentSelectionService>()));
        registrations.AddSingleton(sp => AgentDeployFixtures.PendingUploadService(sp.GetRequiredService<EnvironmentSelectionService>()));
        registrations.AddSingleton(sp => AgentDeployFixtures.PendingService(sp.GetRequiredService<EnvironmentSelectionService>()));
        registrations.RemoveAll<Core.Deployments.DeploymentService>();
        registrations.RemoveAll<Core.Deployments.DeploymentOAuthService>();
        registrations.RemoveAll<Core.Deployments.DeploymentChannelService>();
        return registrations.BuildServiceProvider();
    }

    [Fact]
    public async Task Logout_ProfileServiceReportsCommittedCleanupFailureWithLegacyShapePreserved()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.CreateAsync(new() { AccountKey = "a", Username = "a" }, default);
        var credentials = new ProfileCredentials { ClearFailure = new CacheProtectionUnavailableException("Unavailable.", "Repair storage.") };
        var selections = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(new FakeEnvironmentClient(), selections, store);
        var service = new AuthProfileOperations(store, store, credentials, environments, selections,
            new StaticInteractionPolicy(true), new FixedClock(DateTimeOffset.UnixEpoch), credentials);
        var error = await Assert.ThrowsAsync<AuthOperationException>(() =>
            Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows(), authProfiles: service).LogoutAsync(default));
        Assert.True(error.MetadataCommitted);
        Assert.Equal(12006, error.ErrorCode);
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    private sealed class ProfileCredentials : IProfileCredentialProvider, IProfileCachedAccountReader, IOwnedProfileCredentialCleanup
    {
        public bool OwnsCredential(AuthProfile profile) => profile.CredentialReference is not null;
        public int Authentications { get; private set; }
        public int Reads { get; private set; }
        public int Clears { get; private set; }
        public AuthProfile? Captured { get; private set; }
        public AuthCreateRequest? Request { get; private set; }
        public IReadOnlyList<AuthAccount> Accounts { get; set; } = [];
        public Exception? ReadFailure { get; set; }
        public Exception? ClearFailure { get; set; }
        public Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct)
        {
            Authentications++;
            Request = request;
            return Task.FromResult(new AuthProfile
            {
                AccountKey = "a", Username = "a@contoso.com", CredentialReference = "owned-reference",
                Cloud = request.Target.Cloud, Ring = request.Target.Ring,
                FlowType = request.DeviceCode ? "device-code" : "broker",
                CredentialMethod = request.DeviceCode ? AuthCredentialMethod.DeviceCode : AuthCredentialMethod.Broker,
            });
        }
        public Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(AuthProfile profile, CancellationToken cancellationToken)
        {
            Reads++;
            Captured = profile;
            return ReadFailure is null ? Task.FromResult(Accounts) : Task.FromException<IReadOnlyList<AuthAccount>>(ReadFailure);
        }
        public Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? tenant, CancellationToken ct) =>
            throw new InvalidOperationException("No acquisition is expected.");
        public Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task ClearOwnedCredentialsAsync(CancellationToken ct)
        {
            Clears++;
            return ClearFailure is null ? Task.CompletedTask : Task.FromException(ClearFailure);
        }
    }

    private sealed class LegacyCache : ILegacyMsalTokenCacheBackend
    {
        public int Clears { get; private set; }
        public IDisposable AcquireLease(CancellationToken ct) => new Lease();
        public Task<byte[]?> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No cache reads are allowed.");
        public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct) => throw new InvalidOperationException("No cache writes are allowed.");
        public Task ClearAsync(CancellationToken ct) { Clears++; return Task.CompletedTask; }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class CapturedStore(IAuthProfileStore inner, Func<Task> afterCapture) : IAuthProfileStore
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken ct) => inner.GetProfilesAsync(ct);
        public async Task<AuthProfile?> GetActiveProfileAsync(CancellationToken ct)
        {
            var result = await inner.GetActiveProfileAsync(ct);
            if (++Reads == 1) await afterCapture();
            return result;
        }
        public Task ClearAsync(CancellationToken ct) => inner.ClearAsync(ct);
        public Task UpsertAsync(AuthProfile profile, CancellationToken ct) => inner.UpsertAsync(profile, ct);
        public Task SetActiveAsync(string key, CancellationToken ct) => inner.SetActiveAsync(key, ct);
    }

    // ---- Login: non-interactive gate ------------------------------------------------------------

    [Fact]
    public async Task Login_NonInteractiveRequest_FailsExit3_WithoutTouchingSession()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession { BrokerResult = Account("a@contoso.com", "home-1") };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.Windows(), presenter: presenter);

        var ex = await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            ops.LoginAsync(new AuthLoginRequest { Account = "a@contoso.com", NonInteractive = true }, default));

        Assert.Equal(ExitCodes.NotSignedIn, ex.ExitCode);
        Assert.Equal(0, session.TotalLoginCalls);
        Assert.Empty(presenter.Messages);
        Assert.Null(await store.GetActiveProfileAsync(default));
    }

    [Fact]
    public async Task Login_InteractionPolicyDisallows_FailsExit3_WithoutTouchingSession()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession { BrokerResult = Account("a@contoso.com", "home-1") };
        var ops = Create(session, store, FakePlatformInfo.Windows(), interactionAllowed: false);

        await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            ops.LoginAsync(new AuthLoginRequest(), default));

        Assert.Equal(0, session.TotalLoginCalls);
    }

    // ---- Login: flow selection + profile persistence --------------------------------------------

    [Fact]
    public async Task Login_OnWindows_UsesBroker_AndActivatesProfile()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        Guid issuerTenant =
            new("11111111-1111-1111-1111-111111111111");
        Guid principalObjectId =
            new("22222222-2222-2222-2222-222222222222");
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account(
                "a@contoso.com",
                "home-1",
                "tenant-x",
                issuerTenant,
                principalObjectId,
                ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal("signed-in", result.Status);
        Assert.Equal("a@contoso.com", result.Account);
        Assert.Equal(1, session.BrokerCalls);

        var active = await store.GetActiveProfileAsync(default);
        Assert.NotNull(active);
        Assert.Equal("home-1", active!.AccountKey);
        Assert.Equal(ContextClientId, active.ClientId);
        Assert.Equal("broker", active.FlowType);
        Assert.Equal(
            issuerTenant.ToString("D"),
            active.AuthenticatedTenantId);
        Assert.Equal(
            principalObjectId.ToString("D"),
            active.PrincipalObjectId);
        Assert.True(active.IsActive);
    }

    [Theory]
    [InlineData("windows", "--device-code")]
    [InlineData("macos", "--device-code")]
    [InlineData("linux", "--device-code")]
    [InlineData("unknown", "no broker on this OS")]
    public async Task Login_SignsInWithDeviceCode_WithoutTheBroker_WhenAskedOrWhenTheOsHasNoBroker(string platform, string reason)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1"),
            SilentBrokerResult = Account("a@contoso.com", "home-1"),
            DeviceCodeResult = Account("a@contoso.com", "home-1"),
        };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.Named(platform), presenter: presenter);

        var result = await ops.LoginAsync(new AuthLoginRequest { Account = "a@contoso.com", DeviceCode = reason == "--device-code" }, default);

        Assert.Equal("signed-in", result.Status);
        Assert.Equal(1, session.DeviceCodeCalls);
        Assert.Equal(0, session.BrokerCalls + session.SilentBrokerCalls);
        Assert.Contains("CODE-1234", Assert.Single(presenter.Messages));
        Assert.Equal("device-code", (await store.GetActiveProfileAsync(default))!.FlowType);
    }

    [Fact]
    public async Task Login_DoesNotBindAuthenticatedContextFromAnotherClient()
    {
        using var dir = new TempDir();
        Guid issuerTenant =
            new("11111111-1111-1111-1111-111111111111");
        Guid principalObjectId =
            new("22222222-2222-2222-2222-222222222222");
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account(
                "a@contoso.com",
                "home-1",
                "tenant-x",
                issuerTenant,
                principalObjectId,
                authenticatedClientId:
                    "99999999-9999-9999-9999-999999999999"),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        await ops.LoginAsync(new AuthLoginRequest(), default);

        AuthProfile active =
            Assert.IsType<AuthProfile>(
                await store.GetActiveProfileAsync(default));
        Assert.Null(active.AuthenticatedTenantId);
        Assert.Null(active.PrincipalObjectId);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public async Task Login_WhenTheBrokerFails_FallsBackToDeviceCode_NamingTheFailure(string platform)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerFailure = new BrokerUnavailableException(BrokerFailureReason),
            DeviceCodeResult = Account("a@contoso.com", "home-1"),
        };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.Named(platform), presenter: presenter);

        await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal(1, session.BrokerCalls);
        Assert.Equal(0, session.SilentBrokerCalls);
        Assert.Equal(1, session.DeviceCodeCalls);
        Assert.Equal("device-code", (await store.GetActiveProfileAsync(default))!.FlowType);
        Assert.Collection(
            presenter.Messages,
            fallback => Assert.Equal(BrokerFailedNotice, fallback),
            deviceCode => Assert.Contains("CODE-1234", deviceCode));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public async Task Login_WhenTheOperatorCancelsTheBroker_StopsWithoutDeviceCode(string platform)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerFailure = new SignInCanceledException("authentication_canceled: User canceled authentication."),
            DeviceCodeResult = Account("a@contoso.com", "home-1"),
        };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.Named(platform), presenter: presenter);

        var canceled = await Assert.ThrowsAsync<SignInCanceledException>(() => ops.LoginAsync(new AuthLoginRequest(), default));

        Assert.Contains("--deviceCode", canceled.Remediation);
        Assert.Equal(1, session.BrokerCalls);
        Assert.Equal(0, session.DeviceCodeCalls);
        Assert.Empty(presenter.Messages);
        Assert.Null(await store.GetActiveProfileAsync(default));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public async Task Login_WhenTheCommandIsCanceled_StopsWithoutTheBrokerPickerOrDeviceCode(string platform)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            SilentBrokerFailure = new OperationCanceledException(),
            BrokerResult = Account("a@contoso.com", "home-1"),
            DeviceCodeResult = Account("a@contoso.com", "home-1"),
        };
        var ops = Create(session, store, FakePlatformInfo.Named(platform));

        await Assert.ThrowsAsync<OperationCanceledException>(() => ops.LoginAsync(new AuthLoginRequest { Account = "a@contoso.com" }, default));

        Assert.Equal(1, session.SilentBrokerCalls);
        Assert.Equal(0, session.BrokerCalls);
        Assert.Equal(0, session.DeviceCodeCalls);
        Assert.Null(await store.GetActiveProfileAsync(default));
    }

    [Fact]
    public async Task Login_AFailureThatIsNotABrokerFailure_Propagates_WithoutDeviceCode()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerFailure = new InvalidOperationException("Unexpected."),
            DeviceCodeResult = Account("a@contoso.com", "home-1"),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.LoginAsync(new AuthLoginRequest(), default));

        Assert.Equal(1, session.BrokerCalls);
        Assert.Equal(0, session.DeviceCodeCalls);
        Assert.Null(await store.GetActiveProfileAsync(default));
    }

    [Theory]
    [InlineData("windows", "a@contoso.com")]
    [InlineData("macos", "A@CONTOSO.com")]
    [InlineData("linux", "home-1")]
    public async Task Login_WithAnAccountTheBrokerAlreadyHolds_SignsInSilently_WithoutThePicker(string platform, string requestedAccount)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession { SilentBrokerResult = Account("a@contoso.com", "home-1") };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.Named(platform), presenter: presenter);

        var result = await ops.LoginAsync(new AuthLoginRequest { Account = requestedAccount }, default);

        Assert.Equal("a@contoso.com", result.Account);
        Assert.Equal(1, session.SilentBrokerCalls);
        Assert.Equal(0, session.BrokerCalls);
        Assert.Equal(0, session.DeviceCodeCalls);
        Assert.Empty(presenter.Messages);
        Assert.Equal("broker", (await store.GetActiveProfileAsync(default))!.FlowType);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public async Task Login_WithAnAccountTheBrokerDoesNotHold_OpensThePickerForThatAccount(string platform)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession { BrokerResult = Account("bob@contoso.com", "home-bob") };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.Named(platform), presenter: presenter);

        await ops.LoginAsync(new AuthLoginRequest { Account = "bob@contoso.com" }, default);

        Assert.Equal(1, session.SilentBrokerCalls);
        Assert.Equal(1, session.BrokerCalls);
        Assert.Equal("bob@contoso.com", session.LastLoginRequest!.Account);
        Assert.Equal(0, session.DeviceCodeCalls);
        Assert.Empty(presenter.Messages);
        Assert.Equal("broker", (await store.GetActiveProfileAsync(default))!.FlowType);
    }

    [Fact]
    public async Task Login_WhenTheSilentBrokerReturnsAnotherAccount_OpensThePicker_AndPersistsOnlyTheRequestedAccount()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            SilentBrokerResult = Account("alice@contoso.com", "home-alice"),
            BrokerResult = Account("bob@contoso.com", "home-bob"),
        };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.MacOs(), presenter: presenter);

        var result = await ops.LoginAsync(new AuthLoginRequest { Account = "bob@contoso.com" }, default);

        Assert.Equal("bob@contoso.com", result.Account);
        Assert.Equal(1, session.BrokerCalls);
        Assert.Equal(0, session.DeviceCodeCalls);
        var profile = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal("home-bob", profile.AccountKey);
        Assert.Equal("broker", profile.FlowType);
        Assert.Empty(presenter.Messages);
    }

    [Theory]
    [InlineData("alice@contoso.com")]
    [InlineData(null)]
    public async Task Login_WhenTheSilentBrokerCannotSignInTheRequestedAccount_AndThePickerFails_TheNoticeNamesOnlyThePickerFailure(string? silentlySignedIn)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            SilentBrokerResult = silentlySignedIn is null ? null : Account(silentlySignedIn, "home-alice"),
            BrokerFailure = new BrokerUnavailableException(BrokerFailureReason),
            DeviceCodeResult = Account("bob@contoso.com", "home-bob"),
        };
        var presenter = new RecordingMessagePresenter();
        var ops = Create(session, store, FakePlatformInfo.MacOs(), presenter: presenter);

        await ops.LoginAsync(new AuthLoginRequest { Account = "bob@contoso.com" }, default);

        Assert.Equal(1, session.SilentBrokerCalls);
        Assert.Equal(1, session.BrokerCalls);
        Assert.Equal(1, session.DeviceCodeCalls);
        var profile = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal("home-bob", profile.AccountKey);
        Assert.Equal("device-code", profile.FlowType);
        Assert.Equal(BrokerFailedNotice, presenter.Messages[0]);
    }

    [Fact]
    public async Task Login_WithoutAnAccount_AlwaysShowsThePicker_NeverSigningInSilently()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            SilentBrokerResult = Account("alice@contoso.com", "home-alice"),
            BrokerResult = Account("bob@contoso.com", "home-bob"),
        };
        var ops = Create(session, store, FakePlatformInfo.Linux());

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal("bob@contoso.com", result.Account);
        Assert.Equal(0, session.SilentBrokerCalls);
        Assert.Equal(1, session.BrokerCalls);
    }

    // ---- Login: corporate-account protection ----------------------------------------------------

    [Fact]
    public async Task Login_Broker_CorporateAccount_IsAllowed()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession { BrokerResult = Account("alice@microsoft.com", "home-corp", MsitTenant) };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal("signed-in", result.Status);
        Assert.NotNull(await store.GetActiveProfileAsync(default));
    }

    // ---- Status ---------------------------------------------------------------------------------

    [Fact]
    public async Task Status_NoActiveProfile_IsSignedOut_WithoutTouchingSession()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var report = await ops.GetAuthStatusAsync(default);

        Assert.Equal("signed-out", report.Status);
        Assert.False(report.SignedIn);
        Assert.Equal(0, session.GetAccountsCalls);
    }

    [Fact]
    public async Task Status_ActiveProfilePresentInCache_IsSignedIn()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        // Browser (non-broker) profile: its account is in the enumerable MSAL cache, so status
        // reconciles it via enumeration.
        await store.UpsertAsync(Profile("home-1", "a@contoso.com", "tenant-x", flowType: "interactive"), default);

        var session = new FakeAuthenticationSession();
        session.CacheAccounts.Add(Account("a@contoso.com", "home-1", "tenant-x"));
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var report = await ops.GetAuthStatusAsync(default);

        Assert.Equal("signed-in", report.Status);
        Assert.True(report.SignedIn);
        Assert.Equal("a@contoso.com", report.Account);
        Assert.Equal(1, session.GetAccountsCalls);
    }

    [Fact]
    public async Task Status_ActiveProfileMissingFromCache_IsStale_ButNamesTheAccount()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        // Browser (non-broker) profile absent from the enumerable cache -> genuinely stale.
        await store.UpsertAsync(Profile("home-1", "a@contoso.com", flowType: "interactive"), default);

        var session = new FakeAuthenticationSession(); // empty cache -> stale pointer
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var report = await ops.GetAuthStatusAsync(default);

        Assert.Equal("stale", report.Status);
        Assert.False(report.SignedIn);
        Assert.Equal("a@contoso.com", report.Account);
        Assert.Equal(1, session.GetAccountsCalls);
    }

    [Fact]
    public async Task Status_BrokerProfileNotInEnumerableCache_IsSignedIn_WithoutEnumerating()
    {
        // A broker (OS/WAM) profile holds its account in the OS broker, not the enumerable MSAL file
        // cache. Status must trust the persisted broker profile (as PAC does for
        // OperatingSystem profiles) instead of reconciling it against an enumeration that cannot see the
        // WAM account -- otherwise it falsely reports `stale` after a successful broker login.
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "a@contoso.com", "tenant-x", flowType: "broker"), default);

        var session = new FakeAuthenticationSession(); // empty enumerable cache -- WAM account not listed
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var report = await ops.GetAuthStatusAsync(default);

        Assert.Equal("signed-in", report.Status);
        Assert.True(report.SignedIn);
        Assert.Equal("a@contoso.com", report.Account);
        Assert.Equal("tenant-x", report.TenantId);
        // PAC-faithful: broker profiles are never enumerated for status.
        Assert.Equal(0, session.GetAccountsCalls);
    }

    // ---- Switch ---------------------------------------------------------------------------------

    [Fact]
    public async Task Switch_NoProfiles_FailsExit3()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var ex = await Assert.ThrowsAsync<NotSignedInException>(() => ops.SwitchAuthProfileAsync("a@contoso.com", null, default));
        Assert.Equal(ExitCodes.NotSignedIn, ex.ExitCode);
    }

    [Fact]
    public async Task Switch_ByUsername_ActivatesProfile_WithoutTouchingSession()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        await store.UpsertAsync(Profile("home-2", "two@contoso.com"), default); // becomes active

        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var summary = await ops.SwitchAuthProfileAsync("one@contoso.com", null, default);

        Assert.Equal("switched", summary.Status);
        Assert.Equal("one@contoso.com", summary.Account);
        Assert.Equal(0, session.GetAccountsCalls);
        Assert.Equal(0, session.TotalLoginCalls);

        var active = await store.GetActiveProfileAsync(default);
        Assert.Equal("home-1", active!.AccountKey);
    }

    [Fact]
    public async Task Switch_EmptySelector_IsUsageError()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        var ex = await Assert.ThrowsAsync<UsageException>(() => ops.SwitchAuthProfileAsync("   ", null, default));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public async Task Login_WithARing_RecordsItOnTheProfile()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LoginAsync(
            new AuthLoginRequest { Ring = PowerPlatformApiRing.Preprod },
            default);

        Assert.Equal("preprod", result.Cloud);
        Assert.Equal(PowerPlatformApiRing.Preprod, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Login_WithoutARing_KeepsTheOneTheAccountAlreadyHas()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "a@contoso.com") with { Ring = PowerPlatformApiRing.Test }, default);
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal("test", result.Cloud);
        Assert.Equal(PowerPlatformApiRing.Test, session.LastLoginRequest!.Ring);
        Assert.Equal(PowerPlatformApiRing.Test, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Login_ForTheFirstAccount_DefaultsToProduction()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal("prod", result.Cloud);
        Assert.Equal(PowerPlatformApiRing.Production, session.LastLoginRequest!.Ring);
        Assert.Equal(PowerPlatformApiRing.Production, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Login_ForANewAccount_InheritsTheCloudTheSessionIsAlreadyOn()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-other", "b@contoso.com") with { Ring = PowerPlatformApiRing.Preprod }, default);
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal("preprod", result.Cloud);
        Assert.Equal(PowerPlatformApiRing.Preprod, session.LastLoginRequest!.Ring);
        Assert.Equal(PowerPlatformApiRing.Preprod, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Switch_ByRingAlone_RetargetsTheActiveAccountWithoutSigningIn()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var summary = await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Preprod, default);

        Assert.Equal("preprod", summary.Cloud);
        Assert.Equal("one@contoso.com", summary.Account);
        Assert.Equal(0, session.TotalLoginCalls);
        Assert.Equal(0, session.GetAccountsCalls);

        var active = await store.GetActiveProfileAsync(default);
        Assert.Equal("home-1", active!.AccountKey);
        Assert.Equal(PowerPlatformApiRing.Preprod, active.Ring);
    }

    [Fact]
    public async Task Switch_ByAccountAndRing_AppliesTheRingToTheAccountItActivates()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        await store.UpsertAsync(Profile("home-2", "two@contoso.com") with { Ring = PowerPlatformApiRing.Test }, default);
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        var summary = await ops.SwitchAuthProfileAsync("one@contoso.com", PowerPlatformApiRing.Preprod, default);

        Assert.Equal("one@contoso.com", summary.Account);
        Assert.Equal("preprod", summary.Cloud);

        var profiles = await store.GetProfilesAsync(default);
        Assert.Equal(PowerPlatformApiRing.Preprod, profiles.Single(p => p.AccountKey == "home-1").Ring);
        Assert.Equal(PowerPlatformApiRing.Test, profiles.Single(p => p.AccountKey == "home-2").Ring);
    }

    [Fact]
    public async Task Switch_ByAccountAlone_LeavesTheRingAlone()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com") with { Ring = PowerPlatformApiRing.Preprod }, default);
        await store.UpsertAsync(Profile("home-2", "two@contoso.com"), default);
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        var summary = await ops.SwitchAuthProfileAsync("one@contoso.com", null, default);

        Assert.Equal("preprod", summary.Cloud);
        Assert.Equal(PowerPlatformApiRing.Preprod, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Login_WithACloud_AsksForThatCloudsToken()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        await ops.LoginAsync(new AuthLoginRequest { Ring = PowerPlatformApiRing.Preprod }, default);

        Assert.Equal(PowerPlatformApiRing.Preprod, session.LastLoginRequest!.Ring);
    }

    [Fact]
    public async Task Login_ForANamedAccount_AsksForTheCloudThatAccountAlreadyTargets()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "a@contoso.com") with { Ring = PowerPlatformApiRing.Test }, default);
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        await ops.LoginAsync(new AuthLoginRequest { Account = "a@contoso.com" }, default);

        Assert.Equal(PowerPlatformApiRing.Test, session.LastLoginRequest!.Ring);
    }

    [Fact]
    public async Task Login_WithNothingNamed_AsksForProduction()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("a@contoso.com", "home-1", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows());

        await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.Equal(PowerPlatformApiRing.Production, session.LastLoginRequest!.Ring);
    }

    [Fact]
    public async Task Status_AfterARetarget_DoesNotReportTheCloudItLeftBehindAsTheTarget()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        var ops = Create(
            new FakeAuthenticationSession(),
            store,
            FakePlatformInfo.Windows(),
            apiTarget: PowerPlatformApiTargetConfig.Public);

        await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Preprod, default);
        var status = await ops.GetAuthStatusAsync(default);

        Assert.Equal("preprod", status.Cloud);
        Assert.Null(status.EnvironmentId);
    }

    [Fact]
    public async Task EnvironmentVerbs_AfterARetarget_RefuseRatherThanUseTheOldCloud()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        var ops = Create(
            new FakeAuthenticationSession(),
            store,
            FakePlatformInfo.Windows(),
            apiTarget: PowerPlatformApiTargetConfig.Public);

        await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Preprod, default);

        var listed = await Assert.ThrowsAsync<CloudRetargetedException>(() => ops.ListEnvironmentsAsync(default));
        var selected = await Assert.ThrowsAsync<CloudRetargetedException>(() => ops.SelectEnvironmentAsync("env-1", default));

        Assert.Contains("preprod", listed.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, selected.ExitCode);
    }

    [Fact]
    public async Task AgentCreate_AfterARetarget_RefusesRatherThanMutateInTheOldCloud()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        var ops = Create(
            new FakeAuthenticationSession(),
            store,
            FakePlatformInfo.Windows(),
            apiTarget: PowerPlatformApiTargetConfig.Public);

        await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Preprod, default);

        await Assert.ThrowsAsync<CloudRetargetedException>(
            () => ops.CreateAgentAsync(
                new AgentCreateRequest { DisplayName = "Agent 1", Language = "dotnet", LanguageVersion = "net8.0" },
                default));
    }

    [Fact]
    public async Task EnvironmentVerbs_AfterARetarget_RefuseEvenWhenTheHostComposedNoTarget()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Preprod, default);

        var listed = await Assert.ThrowsAsync<CloudRetargetedException>(() => ops.ListEnvironmentsAsync(default));

        Assert.Contains("preprod", listed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithAnAccountNameMatchingNothingStored_KeepsTheActiveAccountsCloud()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com") with { Ring = PowerPlatformApiRing.Preprod }, default);
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("stranger@contoso.com", "home-9", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows(),
            apiTarget: PowerPlatformApiRingSelection.TargetFor(PowerPlatformApiRing.Preprod));

        await ops.LoginAsync(new AuthLoginRequest { Account = "stranger@contoso.com" }, default);

        Assert.Equal(PowerPlatformApiRing.Preprod, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Login_WithAnAccountNameMatchingAStoredProfile_KeepsThatProfilesCloud()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com") with { Ring = PowerPlatformApiRing.Production }, default);
        await store.UpsertAsync(Profile("home-2", "two@contoso.com") with { Ring = PowerPlatformApiRing.Preprod }, default);
        await store.SetActiveAsync("home-1", default);
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("two@contoso.com", "home-2", "tenant-x", null, null, ContextClientId),
        };
        var ops = Create(session, store, FakePlatformInfo.Windows(),
            apiTarget: PowerPlatformApiRingSelection.TargetFor(PowerPlatformApiRing.Preprod));

        await ops.LoginAsync(new AuthLoginRequest { Account = "two@contoso.com" }, default);

        Assert.Equal(PowerPlatformApiRing.Preprod, (await store.GetActiveProfileAsync(default))!.Ring);
    }

    [Fact]
    public async Task Switch_ToAnUndeclaredCloud_IsRefusedBeforeTheProfileIsChanged()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        await store.UpsertAsync(Profile("home-2", "two@contoso.com"), default);
        await store.SetActiveAsync("home-1", default);
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        await Assert.ThrowsAsync<UsageException>(
            () => ops.SwitchAuthProfileAsync("two@contoso.com", (PowerPlatformApiRing)987, default));

        var active = await store.GetActiveProfileAsync(default);

        Assert.Equal("one@contoso.com", active!.Username);
        Assert.Equal(PowerPlatformApiRing.Production, active.Ring);
    }

    [Fact]
    public async Task EnvironmentVerbs_WithoutARetarget_StillRun()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com") with { Ring = PowerPlatformApiRing.Preprod }, default);
        var ops = Create(
            new FakeAuthenticationSession(),
            store,
            FakePlatformInfo.Windows(),
            apiTarget: PowerPlatformApiTargetConfig.Preprod);

        var status = await ops.GetAuthStatusAsync(default);

        Assert.Equal("preprod", status.Cloud);
    }

    [Fact]
    public async Task Switch_WithNeitherSelector_IsUsageError()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com"), default);
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        var ex = await Assert.ThrowsAsync<UsageException>(
            () => ops.SwitchAuthProfileAsync(string.Empty, null, default));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public async Task Status_ReportsTheActiveAccountsRing()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "a@contoso.com") with { Ring = PowerPlatformApiRing.Preprod }, default);
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Linux());

        var report = await ops.GetAuthStatusAsync(default);

        Assert.Equal("preprod", report.Cloud);
        Assert.Contains("Cloud: preprod.", report.ToTextLines());
    }

    [Fact]
    public async Task Status_WithNoProfiles_ReportsProduction()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Linux());

        var report = await ops.GetAuthStatusAsync(default);

        Assert.Equal("prod", report.Cloud);
    }

    [Fact]
    public async Task TheRingSurvivesAStoreRoundTrip()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await new FileAuthProfileStore(path).UpsertAsync(
            Profile("home-1", "a@contoso.com") with { Ring = PowerPlatformApiRing.Preprod },
            default);

        var reread = await new FileAuthProfileStore(path).GetActiveProfileAsync(default);

        Assert.Equal(PowerPlatformApiRing.Preprod, reread!.Ring);
        Assert.Equal(PowerPlatformApiRing.Preprod, await ActiveProfileRing.ReadAsync(path));
    }

    [Fact]
    public async Task Login_AsAnAccountInAnotherTenant_PreservesTheUnboundLegacySelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(
            new FakeAuthenticationSession
            {
                BrokerResult = Account(
                    "op@fabrikam.com",
                    "home-2",
                    EnvironmentFixtures.FabrikamTenant,
                    null,
                    null,
                    ContextClientId),
            },
            store);

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);
        var status = await ops.GetAuthStatusAsync(default);

        Assert.False(result.EnvironmentCleared);
        Assert.NotNull(selection.Active);
        Assert.Equal("env-1", status.EnvironmentId);
        Assert.False(status.EnvironmentTenantBound);
        Assert.DoesNotContain(
            result.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_AsAnotherAccountInTheSameTenant_PreservesTheUnboundLegacySelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(
            new FakeAuthenticationSession
            {
                BrokerResult = Account(
                    "other@contoso.com",
                    "home-2",
                    EnvironmentFixtures.ContosoTenant,
                    null,
                    null,
                    ContextClientId),
            },
            store);

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);
        var status = await ops.GetAuthStatusAsync(default);

        Assert.False(result.EnvironmentCleared);
        Assert.NotNull(selection.Active);
        Assert.Equal("env-1", status.EnvironmentId);
        Assert.False(status.EnvironmentTenantBound);
        Assert.DoesNotContain(
            result.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_AsTheSameAccountInTheSameCloud_DoesNotAdoptTheUnboundLegacySelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(
            new FakeAuthenticationSession
            {
                BrokerResult = Account(
                    "op@contoso.com",
                    "home-1",
                    EnvironmentFixtures.ContosoTenant,
                    null,
                    null,
                    ContextClientId),
            },
            store);

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);
        var status = await ops.GetAuthStatusAsync(default);

        Assert.False(result.EnvironmentCleared);
        Assert.Equal("env-1", selection.Active!.EnvironmentId);
        Assert.Equal("env-1", status.EnvironmentId);
        Assert.False(status.EnvironmentTenantBound);
        Assert.Single(result.ToTextLines());
    }

    [Fact]
    public async Task Switch_ToAnAccountInAnotherTenant_PreservesTheUnboundLegacySelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        await store.UpsertAsync(Profile("home-2", "op@fabrikam.com", EnvironmentFixtures.FabrikamTenant), default);
        await store.SetActiveAsync("home-1", default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync("op@fabrikam.com", null, default);

        Assert.False(summary.EnvironmentCleared);
        Assert.NotNull(selection.Active);
        Assert.DoesNotContain(
            summary.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switch_ToAnotherAccountInTheSameTenant_PreservesTheUnboundLegacySelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        await store.UpsertAsync(Profile("home-2", "other@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        await store.SetActiveAsync("home-1", default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync("other@contoso.com", null, default);

        Assert.False(summary.EnvironmentCleared);
        Assert.NotNull(selection.Active);
        Assert.DoesNotContain(
            summary.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switch_ToAnotherCloud_PreservesTheUnboundLegacySelection()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Test, default);

        Assert.False(summary.EnvironmentCleared);
        Assert.NotNull(selection.Active);
    }

    [Fact]
    public async Task Switch_ToTheCloudAlreadyActive_KeepsTheSelectedEnvironment()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Production, default);

        Assert.False(summary.EnvironmentCleared);
        Assert.Equal("env-1", selection.Active!.EnvironmentId);
        Assert.Single(summary.ToTextLines());
    }

    [Fact]
    public async Task Login_WithNoEnvironmentSelected_ReportsNothingCleared()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var session = new FakeAuthenticationSession
        {
            BrokerResult = Account("op@contoso.com", "home-1", EnvironmentFixtures.ContosoTenant, null, null, ContextClientId),
        };
        var ops = Create(
            session,
            store,
            FakePlatformInfo.Windows(),
            environments: EnvironmentFixtures.Service(new FakeEnvironmentClient(), selection, store));

        var result = await ops.LoginAsync(new AuthLoginRequest(), default);

        Assert.False(result.EnvironmentCleared);
    }

    /// <summary>A facade whose Contoso environment is already selected, sharing <paramref name="store"/>.</summary>
    private static async Task<(AgentHostingOperations Ops, InMemoryEnvironmentSelectionStore Selection)>
        OpsWithSelectedEnvironmentAsync(FakeAuthenticationSession session, IAuthProfileStore store)
    {
        var selection = new InMemoryEnvironmentSelectionStore();
        await selection.SetActiveAsync(EnvironmentFixtures.Selection(), default);

        var ops = Create(
            session,
            store,
            FakePlatformInfo.Windows(),
            environments: EnvironmentFixtures.Service(new FakeEnvironmentClient(), selection, store));

        return (ops, selection);
    }

    // ---- Logout ---------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_ClearsProfilesAndCache_WithoutTouchingLoginFlows()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);

        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LogoutAsync(default);

        Assert.Equal("signed-out", result.Status);
        Assert.Equal(1, session.ClearCacheCalls);
        Assert.Equal(0, session.TotalLoginCalls);
        Assert.Null(await store.GetActiveProfileAsync(default));
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task Logout_OnEmptyStore_StillClearsCache()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var session = new FakeAuthenticationSession();
        var ops = Create(session, store, FakePlatformInfo.Windows());

        var result = await ops.LogoutAsync(default);

        Assert.Equal("signed-out", result.Status);
        Assert.Equal(1, session.ClearCacheCalls);
    }

    // ---- ABS preview with an unavailable live read adapter -------------------------------------

    [Fact]
    public async Task AbsPreview_WithValidArgs_IsPendingAtReader_Exit1()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        // The facade wires the PendingAbsStateReader, so a fully-specified preview validates and then
        // fails pending at the first Azure read (exit 1) — input validation runs before the read seam.
        var ex = await Assert.ThrowsAsync<PendingImplementationException>(() =>
            ops.PreviewAzureBotAsync(ValidPreviewRequest(), default));
        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
    }

    [Fact]
    public async Task AbsPreview_MissingRequiredArgs_FailsMissingContext_Exit2()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            ops.PreviewAzureBotAsync(new AbsProvisioningRequest(), default));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Equal("missing-context", ex.ErrorKind);
    }

    [Fact]
    public async Task AbsApply_MissingRequiredArgs_FailsMissingContext_Exit2()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        // Missing context is caught before any reader/mutation seam is touched.
        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            ops.ApplyAzureBotAsync(new AbsProvisioningRequest(), default));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Equal("missing-context", ex.ErrorKind);
    }

    [Fact]
    public async Task AbsApply_WithValidArgs_IsPendingAtReader_Exit1()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var ops = Create(new FakeAuthenticationSession(), store, FakePlatformInfo.Windows());

        // Apply runs the shared read-only plan first, so a fully-specified request validates and then
        // fails pending at the first Azure read (exit 1) — before any mutation could occur.
        var ex = await Assert.ThrowsAsync<PendingImplementationException>(() =>
            ops.ApplyAzureBotAsync(ValidPreviewRequest(), default));
        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
    }

    private static AbsProvisioningRequest ValidPreviewRequest() => new()
    {
        Tenant = "tenant-contoso",
        Subscription = "sub-contoso",
        ResourceGroup = "rg-agenthost",
        Location = "westus",
        BotName = "agenthost-bot",
        Endpoint = "https://gateway.contoso.com/api/messages",
        MsaAppId = "11111111-1111-1111-1111-111111111111",
    };

    private static AuthProfile Profile(string key, string user, string? tenant = null, string flowType = "broker") => new()
    {
        AccountKey = key,
        Username = user,
        TenantId = tenant,
        ClientId = ContextClientId,
        Authority = "https://login.microsoftonline.com/organizations",
        Cloud = CloudInstance.Public,
        FlowType = flowType,
    };
}
