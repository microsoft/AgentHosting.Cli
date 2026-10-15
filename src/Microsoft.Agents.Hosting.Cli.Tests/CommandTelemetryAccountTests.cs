using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CommandTelemetryAccountTests
{
    private static readonly Guid TenantA =
        new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB =
        new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TenantC =
        new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid UserA =
        new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB =
        new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid UserC =
        new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string ClientId =
        "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task SelectedProfileEnrichesOnlyAttemptWithPairedFieldsAndDimensions()
    {
        var source = new RecordingProfileSource(
            new("home-user-a.home-tenant", TenantA, UserA));

        string payload = await InvokeAsync(
            source,
            new CommandTelemetryTenantObserver(),
            new FakeOperations());

        JsonElement[] events = Parse(payload);
        JsonElement attempt = Event(events, "CommandAttempt");
        JsonElement outcome = Event(events, "CommandOutcome");
        Assert.Equal(
            TenantA.ToString("D"),
            Data(attempt).GetProperty("tenantId").GetString());
        Assert.Equal(
            UserA.ToString("D"),
            Data(attempt).GetProperty("principalObjectId").GetString());
        Assert.Equal(
            UserA.ToString("D"),
            Dimensions(attempt).GetProperty("principalObjectId").GetString());
        Assert.False(Data(outcome).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(outcome).TryGetProperty("principalObjectId", out _));
        Assert.False(
            Dimensions(outcome).TryGetProperty("principalObjectId", out _));
        Assert.All(events, item => Assert.Equal(
            TenantA.ToString("D"),
            Dimensions(item).GetProperty("tenantId").GetString()));
        Assert.Equal(1, source.ReadCalls);

        Assert.DoesNotContain(
            "home-user-a.home-tenant",
            payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "example.invalid",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "access-token",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "puid",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "pptoolsDeviceId",
            payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "identityMode",
            payload,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StablePairsDoNotDependOnMachineAndSeparateUsersAndGuestTenants()
    {
        string firstMachine = await InvokeAsync(
            new RecordingProfileSource(new("machine-one-key", TenantA, UserA)),
            new CommandTelemetryTenantObserver(),
            new FakeOperations());
        string secondMachine = await InvokeAsync(
            new RecordingProfileSource(new("machine-two-key", TenantA, UserA)),
            new CommandTelemetryTenantObserver(),
            new FakeOperations());
        string differentUser = await InvokeAsync(
            new RecordingProfileSource(new("other-user-key", TenantA, UserB)),
            new CommandTelemetryTenantObserver(),
            new FakeOperations());
        string guestTenant = await InvokeAsync(
            new RecordingProfileSource(new("guest-key", TenantB, UserA)),
            new CommandTelemetryTenantObserver(),
            new FakeOperations());

        Assert.Equal(Subject(firstMachine), Subject(secondMachine));
        Assert.NotEqual(Subject(firstMachine), Subject(differentUser));
        Assert.NotEqual(Subject(firstMachine), Subject(guestTenant));
        Assert.DoesNotContain(
            "machine-one-key",
            firstMachine,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "machine-two-key",
            secondMachine,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshNormalAuthenticationReplacesSelectedFallbackWithoutPersisting()
    {
        var source = new RecordingProfileSource(
            new("old-selected", TenantA, UserA));
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccount account = AuthAccountMapper.FromResult(
                    Result(
                        TenantB,
                        UserB,
                        "new-selected.home-tenant"),
                    observer,
                    ClientId);
                Assert.Equal(UserB, account.PrincipalObjectId);
                return new AuthStatusReport
                {
                    Status = "signed-in",
                    SignedIn = true,
                };
            },
        };

        string payload = await InvokeAsync(source, observer, operations);

        Assert.Equal(
            (TenantB, UserB),
            Subject(payload));
        Assert.Equal(1, source.ReadCalls);
    }

    [Fact]
    public async Task ConflictingFreshUsersLoseSubjectWithoutLosingUsage()
    {
        var source = new RecordingProfileSource(
            new("old-selected", TenantA, UserA));
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(
                    Result(TenantA, UserA, "account-a"),
                    observer,
                    ClientId);
                AuthAccountMapper.FromResult(
                    Result(TenantA, UserB, "account-b"),
                    observer,
                    ClientId);
                return new AuthStatusReport
                {
                    Status = "signed-in",
                    SignedIn = true,
                };
            },
        };

        string payload = await InvokeAsync(source, observer, operations);

        JsonElement[] events = Parse(payload);
        Assert.Equal(2, events.Length);
        JsonElement attempt = Event(events, "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
        Assert.Equal(
            TenantA.ToString("D"),
            Dimensions(attempt).GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task PrincipalAmbiguityRemainsStickyWhileLaterTenantConflictClearsTenant()
    {
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(
                    Result(TenantA, UserA, "account-a"),
                    observer,
                    ClientId);
                AuthAccountMapper.FromResult(
                    Result(TenantA, UserB, "account-b"),
                    observer,
                    ClientId);
                AuthAccountMapper.FromResult(
                    Result(TenantB, UserA, "account-c"),
                    observer,
                    ClientId);
                return new AuthStatusReport
                {
                    Status = "signed-in",
                    SignedIn = true,
                };
            },
        };

        string payload = await InvokeAsync(
            new RecordingProfileSource(selected: null),
            observer,
            operations);

        JsonElement[] events = Parse(payload);
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
            Assert.False(
                Dimensions(item).TryGetProperty("tenantId", out _)));
        JsonElement attempt = Event(events, "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
    }

    [Fact]
    public async Task FreshNonUserContextSuppressesCachedUserButRetainsTenant()
    {
        var source = new RecordingProfileSource(
            new("selected", TenantA, UserA));
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(
                    Result(
                        TenantB,
                        principalObjectId: null,
                        accountKey: "selected",
                        identityType: "app"),
                    observer,
                    ClientId);
                return new AuthStatusReport
                {
                    Status = "signed-in",
                    SignedIn = true,
                };
            },
        };

        string payload = await InvokeAsync(source, observer, operations);

        JsonElement attempt = Event(Parse(payload), "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
        Assert.Equal(
            TenantB.ToString("D"),
            Dimensions(attempt).GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task ProfileSwitchAndLogoutMutationsReplaceAndClearFallback()
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA),
            default);
        await store.UpsertAsync(
            Profile("account-b", TenantB, UserB),
            default);
        await store.SetActiveAsync("account-a", default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());

        var switchTransport = new CapturingTransport();
        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            switchTransport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            session.RecordAttempt(Path());
            await store.SetActiveAsync("account-b", default);
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        Assert.Equal(
            (TenantB, UserB),
            Subject(Assert.IsType<string>(switchTransport.Payload)));

        var logoutTransport = new CapturingTransport();
        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            logoutTransport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            session.RecordAttempt(Path());
            await store.ClearAsync(default);
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        JsonElement logoutAttempt = Event(
            Parse(Assert.IsType<string>(logoutTransport.Payload)),
            "CommandAttempt");
        Assert.False(Data(logoutAttempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Dimensions(logoutAttempt).TryGetProperty("tenantId", out _));
    }

    [Fact]
    public async Task AuthenticationStartedBeforeLogoutIsIgnored()
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA),
            default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            AuthenticationResultObservation? delayed =
                AuthenticationResultTenantObserver.BeginObservation(observer);
            await store.ClearAsync(default);
            AuthAccountMapper.FromResult(
                Result(TenantA, UserA, "account-a"),
                delayed,
                ClientId);
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        JsonElement attempt = Event(
            Parse(Assert.IsType<string>(transport.Payload)),
            "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
        Assert.False(
            Dimensions(attempt).TryGetProperty("tenantId", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedMutationInvalidatesOuterAcquisitionWithoutSharingIdentity(
        bool enabledInnerScope)
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA),
            default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var outerTransport = new CapturingTransport();

        await using (CommandTelemetrySession outer = await Factory(
            source,
            observer,
            outerTransport).CreateAsync(false, default))
        {
            using IDisposable? outerScope = outer.EnterObservationScope();
            AuthenticationResultObservation? delayed =
                AuthenticationResultTenantObserver.BeginObservation(observer);

            if (enabledInnerScope)
            {
                await using CommandTelemetrySession inner = await Factory(
                    source,
                    observer,
                    new CapturingTransport()).CreateAsync(false, default);
                using IDisposable? innerScope =
                    inner.EnterObservationScope();
                await store.ClearAsync(default);
            }
            else
            {
                using IDisposable suppressed = observer.BeginSuppression();
                await store.ClearAsync(default);
            }

            AuthAccountMapper.FromResult(
                Result(TenantA, UserA, "account-a"),
                delayed,
                ClientId);
            outer.RecordAttempt(Path());
            outer.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        JsonElement[] events = Parse(
            Assert.IsType<string>(outerTransport.Payload));
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
            Assert.False(
                Dimensions(item).TryGetProperty("tenantId", out _)));
        JsonElement attempt = Event(events, "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
    }

    [Fact]
    public async Task PostSwitchAuthenticationIsAcceptedAndOlderCompletionIsIgnored()
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA),
            default);
        await store.UpsertAsync(
            Profile("account-b", TenantB, UserB),
            default);
        await store.SetActiveAsync("account-a", default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            AuthenticationResultObservation? delayed =
                AuthenticationResultTenantObserver.BeginObservation(observer);
            await store.SetActiveAsync("account-b", default);
            AuthenticationResultObservation? current =
                AuthenticationResultTenantObserver.BeginObservation(observer);
            AuthAccountMapper.FromResult(
                Result(TenantB, UserB, "account-b"),
                current,
                ClientId);
            AuthAccountMapper.FromResult(
                Result(TenantA, UserA, "account-a"),
                delayed,
                ClientId);
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        Assert.Equal(
            (TenantB, UserB),
            Subject(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public async Task LoginProfileActivationPreservesValidatedPair()
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            AuthenticationResultObservation? acquisition =
                AuthenticationResultTenantObserver.BeginObservation(observer);
            AuthAccount account = AuthAccountMapper.FromResult(
                Result(TenantA, UserA, "account-a"),
                acquisition,
                ClientId);
            await store.UpsertAsync(
                new AuthProfile
                {
                    AccountKey = account.HomeAccountId,
                    Username = account.Username,
                    TenantId = account.TenantId,
                    ClientId = ClientId,
                    Authority =
                        "https://login.microsoftonline.com/organizations",
                    Cloud = CloudInstance.Public,
                    FlowType = "interactive",
                    AuthenticatedTenantId =
                        account.AuthenticatedTenantId?.ToString("D"),
                    PrincipalObjectId =
                        account.PrincipalObjectId?.ToString("D"),
                },
                default);
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        Assert.Equal(
            (TenantA, UserA),
            Subject(Assert.IsType<string>(transport.Payload)));
        AuthProfile stored = Assert.IsType<AuthProfile>(
            await store.GetActiveProfileAsync(default));
        Assert.Equal(TenantA.ToString("D"), stored.AuthenticatedTenantId);
        Assert.Equal(UserA.ToString("D"), stored.PrincipalObjectId);
    }

    [Fact]
    public async Task ReorderedProfileCallbacksKeepNewestMutation()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var seed = new FileAuthProfileStore(path);
        await seed.UpsertAsync(Profile("account-a", TenantA, UserA), default);
        await seed.UpsertAsync(Profile("account-b", TenantB, UserB), default);

        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        using var reordered = new ReorderingProfileObserver(observer);
        var store = new FileAuthProfileStore(
            path,
            reordered,
            revision);
        await store.SetActiveAsync("account-b", default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            reordered.Arm();
            Task older = Task.Run(
                () => store.SetActiveAsync("account-a", default));
            try
            {
                await reordered.FirstCallback.WaitAsync(
                    TimeSpan.FromSeconds(2));
                Task newer = Task.Run(
                    () => store.SetActiveAsync("account-b", default));
                await newer.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                reordered.Release();
            }

            await older.WaitAsync(TimeSpan.FromSeconds(2));
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        Assert.Equal(
            (TenantB, UserB),
            Subject(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public async Task AcquisitionAfterCommittedSwitchSurvivesDelayedProfileCallback()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var seed = new FileAuthProfileStore(path);
        await seed.UpsertAsync(Profile("account-a", TenantA, UserA), default);
        await seed.UpsertAsync(Profile("account-b", TenantB, UserB), default);
        await seed.SetActiveAsync("account-a", default);

        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        using var delayed = new ReorderingProfileObserver(observer);
        var store = new FileAuthProfileStore(
            path,
            delayed,
            revision);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            delayed.Arm();
            Task switchAccount = Task.Run(
                () => store.SetActiveAsync("account-b", default));
            try
            {
                await delayed.FirstCallback.WaitAsync(
                    TimeSpan.FromSeconds(2));
                AuthenticationResultObservation? current =
                    AuthenticationResultTenantObserver.BeginObservation(
                        observer);
                delayed.Release();
                await switchAccount.WaitAsync(TimeSpan.FromSeconds(2));
                AuthAccountMapper.FromResult(
                    Result(
                        TenantC,
                        UserC,
                        "account-b"),
                    current,
                    ClientId);
            }
            finally
            {
                delayed.Release();
            }

            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        Assert.Equal(
            (TenantC, UserC),
            Subject(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public async Task ProfileActivationCannotResolveSameTenantFreshAmbiguity()
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA),
            default);
        await store.UpsertAsync(
            Profile("account-b", TenantA, UserB),
            default);
        await store.SetActiveAsync("account-a", default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            AuthAccountMapper.FromResult(
                Result(TenantA, UserA, "account-a"),
                observer,
                ClientId);
            AuthAccountMapper.FromResult(
                Result(TenantA, UserB, "account-b"),
                observer,
                ClientId);
            await store.SetActiveAsync("account-b", default);
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        JsonElement[] events = Parse(
            Assert.IsType<string>(transport.Payload));
        Assert.Equal(2, events.Length);
        Assert.All(events, item => Assert.Equal(
            TenantA.ToString("D"),
            Dimensions(item).GetProperty("tenantId").GetString()));
        JsonElement attempt = Event(events, "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
    }

    [Fact]
    public async Task ProfileActivationCannotResolveCrossTenantFreshAmbiguity()
    {
        using var dir = new TempDir();
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            dir.File("profiles.json"),
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA),
            default);
        await store.UpsertAsync(
            Profile("account-b", TenantB, UserB),
            default);
        await store.SetActiveAsync("account-a", default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            AuthAccountMapper.FromResult(
                Result(TenantA, UserA, "account-a"),
                observer,
                ClientId);
            AuthAccountMapper.FromResult(
                Result(TenantB, UserB, "account-b"),
                observer,
                ClientId);
            await store.SetActiveAsync("account-a", default);
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        JsonElement[] events = Parse(
            Assert.IsType<string>(transport.Payload));
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
            Assert.False(
                Dimensions(item).TryGetProperty("tenantId", out _)));
        JsonElement attempt = Event(events, "CommandAttempt");
        Assert.False(Data(attempt).TryGetProperty("tenantId", out _));
        Assert.False(
            Data(attempt).TryGetProperty("principalObjectId", out _));
    }

    [Fact]
    public async Task ObservedAuthenticationCountsInvocationButCannotRecreateProfile()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var revision = new AuthProfileMutationRevision();
        var observer = new CommandTelemetryTenantObserver(
            SessionContext(),
            revision);
        var store = new FileAuthProfileStore(
            path,
            observer,
            revision);
        await store.UpsertAsync(
            Profile("account-a", TenantA, UserA) with
            {
                AuthenticatedTenantId = null,
                PrincipalObjectId = null,
            },
            default);
        var source = new CommandTelemetryProfileContextSource(
            store,
            SessionContext());
        var transport = new CapturingTransport();

        await using (CommandTelemetrySession session = await Factory(
            source,
            observer,
            transport).CreateAsync(false, default))
        {
            using IDisposable? scope = session.EnterObservationScope();
            await store.ClearAsync(default);
            AuthAccountMapper.FromResult(
                Result(TenantB, UserB, "account-b"),
                observer,
                ClientId);
            session.RecordAttempt(Path());
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }

        Assert.Equal(
            (TenantB, UserB),
            Subject(Assert.IsType<string>(transport.Payload)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DelayedAuthenticationAfterScopeDisposalCannotMutateSession()
    {
        var source = new RecordingProfileSource(
            new("selected", TenantA, UserA));
        var observer = new CommandTelemetryTenantObserver();
        var transport = new CapturingTransport();
        await using CommandTelemetrySession session =
            await Factory(source, observer, transport).CreateAsync(false, default);
        IDisposable scope = session.EnterObservationScope()!;
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task delayed = Task.Run(async () =>
        {
            await release.Task;
            observer.Observe(
                new AuthenticatedTenantContext(
                    TenantB,
                    UserB,
                    "late-account",
                    ClientId));
        });

        scope.Dispose();
        release.TrySetResult();
        await delayed;
        session.RecordAttempt(Path());
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(
            (TenantA, UserA),
            Subject(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public async Task DisabledSuppressedFailedAndSlowProfileReadsNeverBlockUsage()
    {
        var disabledSource = new ThrowingProfileSource();
        await using (CommandTelemetrySession disabled =
            await Factory(
                disabledSource,
                new CommandTelemetryTenantObserver(),
                new CapturingTransport(),
                controls: DisabledControls()).CreateAsync(false, default))
        {
            disabled.RecordAttempt(Path());
        }
        Assert.Equal(0, disabledSource.ReadCalls);

        var suppressedSource = new ThrowingProfileSource();
        await using (CommandTelemetrySession suppressed =
            await Factory(
                suppressedSource,
                new CommandTelemetryTenantObserver(),
                new CapturingTransport()).CreateAsync(true, default))
        {
            suppressed.RecordAttempt(Path());
        }
        Assert.Equal(0, suppressedSource.ReadCalls);

        var failedTransport = new CapturingTransport();
        await using (CommandTelemetrySession failed =
            await Factory(
                new ThrowingProfileSource(),
                new CommandTelemetryTenantObserver(),
                failedTransport).CreateAsync(false, default))
        {
            failed.RecordAttempt(Path());
            failed.RecordOutcome(CommandTelemetryOutcome.Success);
        }
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            Parse(Assert.IsType<string>(failedTransport.Payload))
                .Select(item =>
                    Data(item).GetProperty("event_Name").GetString()));

        using var slowSource = new SlowProfileSource();
        var slowTransport = new CapturingTransport();
        var stopwatch = Stopwatch.StartNew();
        CommandTelemetrySession slow = await Factory(
            slowSource,
            new CommandTelemetryTenantObserver(),
            slowTransport,
            policyTimeout: TimeSpan.FromMilliseconds(50))
            .CreateAsync(false, default);
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Profile read exceeded startup bound: {stopwatch.Elapsed}.");
        slowSource.Release();
        slow.RecordAttempt(Path());
        slow.RecordOutcome(CommandTelemetryOutcome.Success);
        await slow.DisposeAsync();
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            Parse(Assert.IsType<string>(slowTransport.Payload))
                .Select(item =>
                    Data(item).GetProperty("event_Name").GetString()));
    }

    [Fact]
    public async Task ProductionProfileTelemetryCompositionResolvesWithoutCycle()
    {
        using var dir = new TempDir();
        var services = new ServiceCollection();
        services.AddSingleton<CommandTelemetryTenantObserver>();
        services.AddSingleton<IAuthenticationResultObserver>(
            provider =>
                provider.GetRequiredService<CommandTelemetryTenantObserver>());
        services.AddSingleton<IAuthProfileStateObserver>(
            provider =>
                provider.GetRequiredService<CommandTelemetryTenantObserver>());
        services.AddAgentHostAuthentication(
            new AgentHostAuthOptions
            {
                ConfigDirectory = dir.Path,
                ClientId = ClientId,
                Cloud = CloudInstance.Public,
                InteractionAllowed = false,
            });
        services.AddSingleton<ICommandTelemetryProfileContextSource>(
            provider => new CommandTelemetryProfileContextSource(
                provider.GetRequiredService<IAuthProfileTelemetryStore>(),
                provider.GetRequiredService<AuthSessionContext>()));
        await using ServiceProvider provider =
            services.BuildServiceProvider();

        ICommandTelemetryProfileContextSource source =
            provider.GetRequiredService<ICommandTelemetryProfileContextSource>();

        Assert.Null(await source.GetSelectedAsync(default));
        Assert.Same(
            provider.GetRequiredService<IAuthProfileStore>(),
            provider.GetRequiredService<IAuthProfileTelemetryStore>());
    }

    private static async Task<string> InvokeAsync(
        ICommandTelemetryProfileContextSource source,
        CommandTelemetryTenantObserver observer,
        FakeOperations operations)
    {
        var transport = new CapturingTransport();
        var host = new AgentHostCli(
            operations,
            telemetryFactory: Factory(source, observer, transport));

        int exitCode = await host.InvokeAsync(
            ["auth", "status"],
            new StringWriter(),
            new StringWriter(),
            color: false);

        Assert.Equal(0, exitCode);
        return Assert.IsType<string>(transport.Payload);
    }

    private static CommandTelemetrySessionFactory Factory(
        ICommandTelemetryProfileContextSource source,
        CommandTelemetryTenantObserver observer,
        CapturingTransport transport,
        TelemetryControlService? controls = null,
        TimeSpan? policyTimeout = null) =>
        new(
            controls ?? EnabledControls(),
            new FixedDestinationResolver(Destination()),
            new FixedClock(),
            typeof(AgentHostCli).Assembly,
            observer,
            profileSource: source,
            // Attribution assertions must not race production deadlines under CI coverage.
            policyTimeout: policyTimeout ?? TimeSpan.FromSeconds(10),
            flushTimeout: TimeSpan.FromSeconds(10),
            transport: transport);

    private static TelemetryControlService EnabledControls() =>
        new(
            new FixedPreferenceStore(TelemetryRememberedPreference.Enabled),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

    private static TelemetryControlService DisabledControls() =>
        new(
            new FixedPreferenceStore(TelemetryRememberedPreference.Disabled),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

    private static AuthProfile Profile(
        string accountKey,
        Guid tenantId,
        Guid principalObjectId) =>
        new()
        {
            AccountKey = accountKey,
            Username = accountKey + "@example.invalid",
            TenantId = tenantId.ToString("D"),
            ClientId = ClientId,
            Authority =
                "https://login.microsoftonline.com/organizations",
            Cloud = CloudInstance.Public,
            FlowType = "interactive",
            AuthenticatedTenantId = tenantId.ToString("D"),
            PrincipalObjectId = principalObjectId.ToString("D"),
        };

    private static AuthSessionContext SessionContext() =>
        new()
        {
            ClientId = ClientId,
            Authority =
                "https://login.microsoftonline.com/organizations",
            Cloud = CloudInstance.Public,
        };

    private static AuthenticationResult Result(
        Guid tenantId,
        Guid? principalObjectId,
        string accountKey,
        string? identityType = "user")
    {
        var claims = new List<Claim>
        {
            new("tid", tenantId.ToString("D")),
        };
        if (principalObjectId is { } objectId)
        {
            claims.Add(new("oid", objectId.ToString("D")));
        }

        if (identityType is not null)
        {
            claims.Add(new("idtyp", identityType));
        }

        return new AuthenticationResult(
            accessToken: "synthetic-access-token",
            isExtendedLifeTimeToken: false,
            uniqueId: "synthetic-unique-id-must-not-be-used",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(2),
            tenantId.ToString("D"),
            account: new SyntheticAccount(accountKey),
            idToken: "synthetic-id-token",
            scopes: ["synthetic.scope"],
            correlationId: Guid.NewGuid(),
            tokenType: "Bearer",
            authenticationResultMetadata: null!,
            claimsPrincipal: new ClaimsPrincipal(
                new ClaimsIdentity(claims, "synthetic")),
            spaAuthCode: null,
            additionalResponseParameters: null!);
    }

    private static (Guid TenantId, Guid PrincipalObjectId) Subject(
        string payload)
    {
        JsonElement attempt = Event(Parse(payload), "CommandAttempt");
        JsonElement data = Data(attempt);
        JsonElement dimensions = Dimensions(attempt);
        Assert.Equal(
            data.GetProperty("tenantId").GetString(),
            dimensions.GetProperty("tenantId").GetString());
        Assert.Equal(
            data.GetProperty("principalObjectId").GetString(),
            dimensions.GetProperty("principalObjectId").GetString());
        return (
            Guid.Parse(dimensions.GetProperty("tenantId").GetString()!),
            Guid.Parse(dimensions.GetProperty("principalObjectId").GetString()!));
    }

    private static JsonElement[] Parse(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static JsonElement Event(
        IEnumerable<JsonElement> events,
        string name) =>
        Assert.Single(
            events,
            item =>
                Data(item).GetProperty("event_Name").GetString() == name);

    private static JsonElement Data(JsonElement item) =>
        item.GetProperty("data");

    private static JsonElement Dimensions(JsonElement item) =>
        JsonDocument.Parse(
                Data(item).GetProperty("customDimensions").GetString()!)
            .RootElement.Clone();

    private static CommandTelemetryPath Path() =>
        CommandTelemetryPath.FromMetadata(["auth", "status"]);

    private static TelemetryDestination Destination() =>
        new(
            new Uri("http://127.0.0.1:1/OneCollector/1.0/"),
            string.Join(
                '-',
                new string('0', 32),
                new string('1', 8),
                new string('2', 4),
                new string('3', 4),
                new string('4', 4),
                new string('5', 12),
                new string('6', 4)),
            "powerappsclient",
            "prod",
            "prod",
            TelemetryLocation.Europe);

    private sealed class RecordingProfileSource(
        CommandTelemetryProfileContext? selected) :
        ICommandTelemetryProfileContextSource
    {
        public int ReadCalls { get; private set; }

        public ValueTask<CommandTelemetryProfileContext?> GetSelectedAsync(
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return ValueTask.FromResult(selected);
        }
    }

    private sealed class ThrowingProfileSource :
        ICommandTelemetryProfileContextSource
    {
        public int ReadCalls { get; private set; }

        public ValueTask<CommandTelemetryProfileContext?> GetSelectedAsync(
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return ValueTask.FromException<CommandTelemetryProfileContext?>(
                new IOException("synthetic inaccessible profile"));
        }

    }

    private sealed class SlowProfileSource :
        ICommandTelemetryProfileContextSource,
        IDisposable
    {
        private readonly ManualResetEventSlim _release = new();

        public ValueTask<CommandTelemetryProfileContext?> GetSelectedAsync(
            CancellationToken cancellationToken)
        {
            _release.Wait(CancellationToken.None);
            return ValueTask.FromResult<CommandTelemetryProfileContext?>(null);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class ReorderingProfileObserver(
        CommandTelemetryTenantObserver inner) :
        IAuthProfileStateObserver,
        IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _firstCallback =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _delayed;

        public bool IsObservationEnabled => inner.IsObservationEnabled;

        public Task FirstCallback => _firstCallback.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void ObserveProfileMutation(long revision) =>
            inner.ObserveProfileMutation(revision);

        public void ObserveActiveProfile(
            AuthProfile? profile,
            long revision)
        {
            if (Volatile.Read(ref _armed) != 0
                && Interlocked.Exchange(ref _delayed, 1) == 0)
            {
                _firstCallback.TrySetResult();
                _release.Wait();
            }

            inner.ObserveActiveProfile(profile, revision);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class FixedPreferenceStore(
        TelemetryRememberedPreference preference) :
        ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(preference);

        public ValueTask WriteAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedDestinationResolver(
        TelemetryDestination destination) :
        ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TelemetryDestination?>(destination);
    }

    private sealed class CapturingTransport : ICommandTelemetryTransport
    {
        public string? Payload { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            Payload = payload;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SyntheticAccount(string accountKey) : IAccount
    {
        public string Username => "synthetic@example.invalid";

        public string Environment => "login.microsoftonline.com";

        public AccountId HomeAccountId { get; } =
            new(accountKey, "home-object", "home-tenant");
    }
}
