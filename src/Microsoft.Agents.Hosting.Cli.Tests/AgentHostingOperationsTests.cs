using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

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

    private static AgentHostingOperations Create(
        IAuthenticationSession session,
        IAuthProfileStore store,
        IPlatformInfo platform,
        bool interactionAllowed = true,
        RecordingMessagePresenter? presenter = null,
        EnvironmentSelectionService? environments = null,
        PowerPlatformApiTargetConfig? apiTarget = null)
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
            AgentDeployFixtures.PendingService(selection));
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

        Assert.Contains("--device-code", canceled.Remediation);
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
    public async Task Login_AsAnAccountInAnotherTenant_ClearsTheSelectedEnvironment()
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

        Assert.True(result.EnvironmentCleared);
        Assert.Null(selection.Active);
        Assert.Null(status.EnvironmentId);
        Assert.Null(status.EnvironmentTenantBound);
        Assert.Contains(
            result.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_AsAnotherAccountInTheSameTenant_ClearsTheSelectedEnvironment()
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

        Assert.True(result.EnvironmentCleared);
        Assert.Null(selection.Active);
        Assert.Null(status.EnvironmentId);
        Assert.Contains(
            result.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_AsTheSameAccountInTheSameCloud_KeepsTheSelectedEnvironment()
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
        Assert.True(status.EnvironmentTenantBound);
        Assert.Single(result.ToTextLines());
    }

    [Fact]
    public async Task Switch_ToAnAccountInAnotherTenant_ClearsTheSelectedEnvironment()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        await store.UpsertAsync(Profile("home-2", "op@fabrikam.com", EnvironmentFixtures.FabrikamTenant), default);
        await store.SetActiveAsync("home-1", default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync("op@fabrikam.com", null, default);

        Assert.True(summary.EnvironmentCleared);
        Assert.Null(selection.Active);
        Assert.Contains(
            summary.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switch_ToAnotherAccountInTheSameTenant_ClearsTheSelectedEnvironment()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        await store.UpsertAsync(Profile("home-2", "other@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        await store.SetActiveAsync("home-1", default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync("other@contoso.com", null, default);

        Assert.True(summary.EnvironmentCleared);
        Assert.Null(selection.Active);
        Assert.Contains(
            summary.ToTextLines(),
            line => line.Contains("Cleared the selected environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switch_ToAnotherCloud_ClearsTheSelectedEnvironment()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "op@contoso.com", EnvironmentFixtures.ContosoTenant), default);
        var (ops, selection) = await OpsWithSelectedEnvironmentAsync(new FakeAuthenticationSession(), store);

        var summary = await ops.SwitchAuthProfileAsync(string.Empty, PowerPlatformApiRing.Test, default);

        Assert.True(summary.EnvironmentCleared);
        Assert.Null(selection.Active);
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
