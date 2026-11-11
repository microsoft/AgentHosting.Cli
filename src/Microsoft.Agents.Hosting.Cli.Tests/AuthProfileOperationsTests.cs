using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AuthProfileOperationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), ".test-auth-operations-" + Guid.NewGuid().ToString("N"));
    private readonly FileAuthProfileStore _profiles;
    private readonly Credentials _credentials = new();
    private readonly Selections _selections = new();
    private readonly Inventory _inventory = new();

    public AuthProfileOperationsTests()
    {
        Directory.CreateDirectory(_directory);
        _profiles = new(Path.Combine(_directory, "profiles.json"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AuthProfileOperations Service(bool allowed = true, IAuthProfileManagementStore? store = null,
        IAuthProfilePicker? picker = null) =>
        new(store ?? _profiles, _credentials,
            new EnvironmentSelectionService(_inventory, _selections, store ?? _profiles, new FixedClock(DateTimeOffset.UnixEpoch)),
            _selections, new StaticInteractionPolicy(allowed), new FixedClock(DateTimeOffset.UnixEpoch),
            _credentials, picker);

    private static AuthProfile Profile(string key = "home-a", string? name = null) => new()
    {
        AccountKey = key, HomeAccountId = key, Username = key + "@contoso.com", Name = name,
        TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "22222222-2222-2222-2222-222222222222",
        Authority = "https://login.microsoftonline.com/organizations", CredentialMethod = AuthCredentialMethod.Browser,
        FlowType = "interactive", CredentialReference = "private-" + key,
    };

    private static PowerPlatformEnvironment Environment(string id = "env-a") => new()
    {
        EnvironmentId = id, DisplayName = id, TenantId = Profile().TenantId!,
        ApiEndpoint = new Uri("https://env.api.powerplatform.com"), ApiAudience = PowerPlatformApiTargetConfig.Public.Audience,
        InstanceUrl = "https://org.crm.dynamics.com",
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ForbiddenCreateDoesNotStageOrConstructClient(bool nonInteractive, bool allowed)
    {
        var ex = await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            Service(allowed).CreateAsync(new() { NonInteractive = nonInteractive, Environment = "env-a" }, default));
        Assert.Equal(12007, ex.ErrorCode);
        Assert.Equal(3, ex.ExitCode);
        Assert.Equal(0, _credentials.Authentications);
        Assert.Equal(0, _inventory.Clients);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("                               ")]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("bad\nname")]
    public async Task InvalidNameDoesNotAuthenticate(string name)
    {
        await _profiles.CreateAsync(Profile(name: "Taken"), default);
        await Assert.ThrowsAsync<UsageException>(() => Service().CreateAsync(new() { Name = name }, default));
        Assert.Equal(0, _credentials.Authentications);
    }

    [Fact]
    public async Task NamedIdentityReplacementRetainsIdAppendsActivatesAndCleansOnlyOldStage()
    {
        var old = await _profiles.CreateAsync(Profile(name: "Existing") with { CredentialReference = "old-reference" }, default);
        var other = await _profiles.CreateAsync(Profile("other", "Other"), default);
        var before = await _profiles.GetProfilesAsync(default);

        var result = await Service().CreateAsync(new() { Name = "Existing" }, default);

        var after = await _profiles.GetProfilesAsync(default);
        Assert.Equal([other.ProfileId, old.ProfileId], after.Select(p => p.ProfileId));
        Assert.Equal(old.ProfileId, result.Profile.ProfileId);
        Assert.Equal("Existing", result.Profile.Name);
        Assert.True(result.Profile.IsActive);
        Assert.False(after[0].IsActive);
        Assert.Equal(before[0].Revision + 1, after[1].Revision);
        Assert.Equal("private-home-a", after[1].CredentialReference);
        Assert.Equal(after[1], _credentials.Completed);
        Assert.Equal("old-reference", Assert.Single(_credentials.Removed).CredentialReference);
        Assert.Equal(1, _credentials.Authentications);
        Assert.Equal(1, _credentials.Completions);
    }

    [Fact]
    public async Task DifferentIdentityDuplicateNameRollsBackOnlyNewStageBeforeMetadataCommit()
    {
        var old = await _profiles.CreateAsync(Profile("old", "Taken"), default);

        await Assert.ThrowsAsync<UsageException>(() => Service().CreateAsync(new() { Name = "Taken" }, default));

        Assert.Equal(old, Assert.Single(await _profiles.GetProfilesAsync(default)));
        Assert.Equal(1, _credentials.Authentications);
        Assert.Equal(0, _credentials.Completions);
        Assert.Equal("private-home-a", Assert.Single(_credentials.Removed).CredentialReference);
        Assert.False(_credentials.CleanupTokenCanceled);
    }

    [Fact]
    public async Task DifferentIdentityCreatePreservesCaseSensitiveNameUniqueness()
    {
        var old = await _profiles.CreateAsync(Profile("old", "Taken"), default);
        var result = await Service().CreateAsync(new() { Name = "taken" }, default);
        Assert.NotEqual(old.ProfileId, result.Profile.ProfileId);
        Assert.Equal(["Taken", "taken"], (await _profiles.GetProfilesAsync(default)).Select(p => p.Name));
        Assert.Equal(1, _credentials.Authentications);
        Assert.Empty(_credentials.Removed);
    }

    [Fact]
    public async Task DeniedPolicyReachesEligibleWorkloadProvider()
    {
        await Service(allowed: false).CreateAsync(new()
        {
            ManagedIdentity = true, Environment = null,
        }, default);
        Assert.True(_credentials.Request!.NonInteractive);
    }

    [Fact]
    public async Task FailedStagedEnvironmentValidationRollsBackOnlyStage()
    {
        var prior = await _profiles.CreateAsync(Profile("old"), default);
        _inventory.Available = [];
        var error = await Assert.ThrowsAsync<EnvironmentNotFoundException>(() =>
            Service().CreateAsync(new() { Environment = "missing" }, default));
        Assert.Equal(prior, await _profiles.GetActiveProfileAsync(default));
        Assert.Equal("private-home-a", Assert.Single(_credentials.Removed).CredentialReference);
        Assert.Equal("home-a", Assert.Single(_inventory.Contexts).Profile.AccountKey);
        Assert.Equal(0, _credentials.Completions);
        Assert.Equal(ExitCodes.Usage, error.ExitCode);
    }

    [Fact]
    public async Task CanceledPrecommitUsesIndependentCleanupToken()
    {
        using var cancel = new CancellationTokenSource();
        _inventory.BeforeList = () => { cancel.Cancel(); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<AuthOperationCanceledException>(() =>
            Service().CreateAsync(new() { Environment = "env-a" }, cancel.Token));
        Assert.False(error.MetadataCommitted);
        Assert.False(error.CleanupIncomplete);
        Assert.Null(error.Profile);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.DoesNotContain("cleanup is incomplete", error.Message);
        Assert.False(_credentials.CleanupTokenCanceled);
        Assert.Single(_credentials.Removed);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Fact]
    public async Task FailedPersistenceAndCleanupPreservePrimaryContract()
    {
        var primary = new CacheProtectionUnavailableException("Store unavailable.", "Repair the store.");
        _credentials.RemoveFailure = new AuthProfileStateException("Cleanup unavailable.");
        var error = await Assert.ThrowsAsync<AuthOperationException>(() =>
            Service(store: new Store(_profiles) { CreateFailure = primary }).CreateAsync(new(), default));
        Assert.False(error.MetadataCommitted);
        Assert.True(error.CleanupIncomplete);
        Assert.Equal(primary.ErrorCode, error.ErrorCode);
        Assert.Equal(primary.ExitCode, error.ExitCode);
        Assert.Equal(primary.Remediation, error.Remediation);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Fact]
    public async Task ReplacementFinalizesRetainedIdAndCleansOnlyObsoleteUnownedReference()
    {
        var old = await _profiles.CreateAsync(Profile() with { CredentialReference = "old-reference" }, default);
        var other = await _profiles.CreateAsync(Profile("other") with { CredentialReference = "old-reference" }, default);
        var result = await Service().CreateAsync(new() { Environment = "env-a" }, default);
        Assert.Equal(old.ProfileId, _credentials.Completed!.ProfileId);
        Assert.Equal(old.ProfileId, result.Profile.ProfileId);
        Assert.Equal("env-a", result.Profile.DefaultEnvironment!.EnvironmentId);
        Assert.Empty(_credentials.Removed);
        Assert.Contains(await _profiles.GetProfilesAsync(default), p => p.ProfileId == other.ProfileId);
    }

    [Fact]
    public async Task ReplacementCleansObsoleteReferenceAfterCommit()
    {
        var old = await _profiles.CreateAsync(Profile() with { CredentialReference = "old-reference" }, default);
        await Service().CreateAsync(new(), default);
        Assert.Equal(old.ProfileId, _credentials.Completed!.ProfileId);
        Assert.Equal("old-reference", Assert.Single(_credentials.Removed).CredentialReference);
        Assert.Equal("private-home-a", (await _profiles.GetActiveProfileAsync(default))!.CredentialReference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostcommitFinalizationFailureRetainsNewReferenceAndDefault(bool canceled)
    {
        using var cancel = new CancellationTokenSource();
        if (canceled) _credentials.AfterComplete = cancel.Cancel;
        _credentials.CompleteFailure = canceled
            ? new OperationCanceledException(cancel.Token)
            : new CacheProtectionUnavailableException("Finalization unavailable.", "Repair storage.");
        if (canceled)
        {
            _credentials.CompleteFailure.Data["msagent.metadata-committed"] = true;
            _credentials.CompleteFailure.Data["msagent.owned-cleanup"] = "The in-use lease is pending release.";
        }
        var error = await Record.ExceptionAsync(() => Service().CreateAsync(new() { Environment = "env-a" }, cancel.Token));
        var state = Assert.IsAssignableFrom<IAuthOperationPartialState>(error);
        Assert.True(state.MetadataCommitted);
        Assert.True(state.CleanupIncomplete);
        Assert.Equal("env-a", state.Profile!.DefaultEnvironment!.EnvironmentId);
        var active = await _profiles.GetActiveProfileAsync(default);
        Assert.Equal("private-home-a", active!.CredentialReference);
        Assert.Equal("env-a", active.DefaultEnvironment!.EnvironmentId);
        Assert.Empty(_credentials.Removed);
        if (canceled) Assert.Equal(cancel.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedAdapterCancellationDistinguishesReleasedStageFromPendingReplacementCleanup(bool replacing)
    {
        using var h = new ProfileCredentialTests.Harness();
        using var cancel = new CancellationTokenSource();
        var provider = new ProfileCredentialProvider(new() { ConfigDirectory = h.Directory, AllowUnprotectedCache = true },
            _profiles, h.Backend, h.Acquisition, FakePlatformInfo.Unknown());
        if (replacing)
        {
            var previous = await provider.AuthenticateAsync(new() { DeviceCode = true }, default);
            var committed = await _profiles.CreateAsync(previous, default);
            await provider.CompleteCreationAsync(committed, default);
        }
        var service = new AuthProfileOperations(new Store(_profiles) { AfterCreate = cancel.Cancel }, provider,
            new EnvironmentSelectionService(_inventory, _selections, _profiles, new FixedClock(DateTimeOffset.UnixEpoch)),
            _selections, new StaticInteractionPolicy(true), new FixedClock(DateTimeOffset.UnixEpoch), provider);
        var error = await Assert.ThrowsAsync<AuthOperationCanceledException>(() =>
            service.CreateAsync(new() { DeviceCode = true }, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(error.MetadataCommitted);
        Assert.Equal(replacing, error.CleanupIncomplete);
        Assert.Equal((await _profiles.GetActiveProfileAsync(default))!.ProfileId, error.Profile!.ProfileId);
        Assert.Equal(replacing ? 2 : 1, Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories).Length);
        await _profiles.ClearAsync(default);
        await provider.ClearOwnedCredentialsAsync(default);
        Assert.Empty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LifecycleUsesOrdinalNamesAndFirstPersistedFallbackWithoutAuthentication()
    {
        var a = await _profiles.CreateAsync(Profile("a", "Case"), default);
        await _profiles.CreateAsync(Profile("b", "case"), default);
        await Service(allowed: false).SelectAsync(new() { Selector = new(Name: "Case") }, default);
        await Service().NameAsync(new(Index: 1), "Renamed", default);
        await Assert.ThrowsAsync<UsageException>(() => Service().NameAsync(new(Index: 2), "Renamed", default));
        await Service().DeleteAsync(new(Index: 2), default);
        Assert.Equal(a.ProfileId, (await _profiles.GetActiveProfileAsync(default))!.ProfileId);
        await _profiles.CreateAsync(Profile("c"), default);
        await _profiles.CreateAsync(Profile("d"), default);
        await Service().DeleteAsync(new(Index: 3), default);
        Assert.Equal(a.ProfileId, (await _profiles.GetActiveProfileAsync(default))!.ProfileId);
        Assert.Equal(0, _credentials.Authentications);
        Assert.Equal(0, _inventory.Clients);
    }

    [Fact]
    public async Task SelectorErrorsAndForbiddenPickerDoNotAuthenticate()
    {
        await _profiles.CreateAsync(Profile(), default);
        await Assert.ThrowsAsync<UsageException>(() => Service().DeleteAsync(new(), default));
        await Assert.ThrowsAsync<UsageException>(() => Service().SelectAsync(new() { Selector = new(1, "x") }, default));
        await Assert.ThrowsAsync<InteractionRequiredException>(() => Service(allowed: false).SelectAsync(new(), default));
        Assert.Equal(0, _credentials.Authentications);
    }

    [Fact]
    public async Task NameRequiresAValueAsTypedUsageBeforeMutation()
    {
        var before = await _profiles.CreateAsync(Profile(), default);
        await Assert.ThrowsAsync<UsageException>(() => Service().NameAsync(new(Index: 1), null!, default));
        Assert.Equal(before, await _profiles.GetActiveProfileAsync(default));
    }

    [Fact]
    public async Task AllowedPickerSelectsCapturedIdNotReorderedIndex()
    {
        var a = await _profiles.CreateAsync(Profile("a"), default);
        await _profiles.CreateAsync(Profile("b"), default);
        var picker = new Picker(async profiles =>
        {
            Assert.Equal(a.ProfileId, profiles[0].ProfileId);
            await _profiles.DeleteAsync(new(Index: 1), default);
            return a.ProfileId;
        });
        await Assert.ThrowsAnyAsync<AgentHostingException>(() => Service(picker: picker).SelectAsync(new(), default));
        Assert.True(picker.Called);
        Assert.Equal("home-a", _credentials.Staged.AccountKey);
        Assert.Equal("b", (await _profiles.GetActiveProfileAsync(default))!.AccountKey);
    }

    [Fact]
    public async Task NamingInactiveProfileDoesNotConstructClientOrActivateIt()
    {
        var a = await _profiles.CreateAsync(Profile("a", "A"), default);
        var b = await _profiles.CreateAsync(Profile("b"), default);
        await Service().NameAsync(new(Index: 1), "Renamed", default);
        Assert.Equal("Renamed", (await _profiles.GetProfilesAsync(default))[0].Name);
        Assert.Equal(b.ProfileId, (await _profiles.GetActiveProfileAsync(default))!.ProfileId);
        Assert.Equal(a.ProfileId, (await _profiles.GetProfilesAsync(default))[0].ProfileId);
        Assert.Equal(0, _inventory.Clients);
        Assert.Equal(0, _credentials.Authentications);
    }

    [Fact]
    public async Task WhoUsesNewDefaultRatherThanStaleOverrideAndReturnsOnlySafeMetadata()
    {
        await _profiles.CreateAsync(Profile(name: "Safe"), default);
        var environments = new EnvironmentSelectionService(_inventory, _selections, _profiles, new FixedClock(DateTimeOffset.UnixEpoch));
        await environments.SelectAsync("env-a", default);
        var profile = (await _profiles.GetActiveProfileAsync(default))!;
        await _profiles.SetDefaultEnvironmentAsync(profile.ProfileId, profile.Revision,
            profile.DefaultEnvironment! with { EnvironmentId = "env-b", DisplayName = "env-b" }, default);
        var who = await Service().WhoAsync(default);
        Assert.Equal("env-b", who.Environment!.EnvironmentId);
        var listed = await Service().ListAsync(default);
        Assert.Equal(1, Assert.Single(listed.Profiles).Index);
        foreach (var json in new[] { JsonSerializer.Serialize(who), JsonSerializer.Serialize(listed) })
        {
            Assert.DoesNotContain("private-home-a", json);
            Assert.DoesNotContain("CredentialReference", json);
            Assert.DoesNotContain("CertificatePath", json);
            Assert.DoesNotContain("IdentityFingerprint", json);
        }
        Assert.Equal(0, _credentials.Authentications);
    }

    [Fact]
    public async Task DeleteCleanupFailureReportsCommittedMetadataAndKeepsFallback()
    {
        var a = await _profiles.CreateAsync(Profile("a"), default);
        await _profiles.CreateAsync(Profile("b"), default);
        _credentials.RemoveFailure = new CacheProtectionUnavailableException("Unavailable.", "Repair native storage.");
        var error = await Assert.ThrowsAsync<AuthOperationException>(() => Service().DeleteAsync(new(Index: 2), default));
        Assert.True(error.MetadataCommitted);
        Assert.False(error.Profile!.IsActive);
        Assert.True(error.ProfileDeleted);
        Assert.True(error.CleanupIncomplete);
        Assert.Equal(a.ProfileId, error.ActiveProfile!.ProfileId);
        Assert.True(error.ActiveProfile.IsActive);
        Assert.Equal(12006, error.ErrorCode);
        Assert.Equal(1, error.ExitCode);
        Assert.Same(_credentials.RemoveFailure, error.InnerException);
        Assert.Equal("Repair native storage.", error.Remediation);
        Assert.Equal(a.ProfileId, (await _profiles.GetActiveProfileAsync(default))!.ProfileId);
    }

    public static TheoryData<string, string, bool> PartialDeletionHostCases
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (string scenario in new[] { "active", "inactive", "last", "concurrent-selection" })
                foreach (string failure in new[] { "cleanup-failure", "canceled-cleanup", "late-cancellation" })
                    foreach (bool json in new[] { false, true })
                        cases.Add(scenario, failure, json);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(PartialDeletionHostCases))]
    public async Task PartialDeletionHostReportsNonactiveDeletedProfileAndAtomicFallback(
        string scenario, string failure, bool json)
    {
        const string canary = "FAKE_DELETION_PARTIAL_SECRET_CANARY";
        var first = await _profiles.CreateAsync(Profile("a", "First"), default);
        var deleted = first;
        AuthProfile? later = null;
        if (scenario != "last")
            deleted = await _profiles.CreateAsync(Profile("b", "Deleted"), default);
        if (scenario == "inactive")
            await _profiles.SelectAsync(new(Name: "First"), default);
        if (scenario == "concurrent-selection")
        {
            later = await _profiles.CreateAsync(Profile("c", "Later"), default);
            await _profiles.SelectAsync(new(Name: "Deleted"), default);
        }
        AuthProfileDeletion? snapshot = null;
        var store = new Store(_profiles)
        {
            AfterDelete = async deletion =>
            {
                snapshot = deletion;
                Assert.Equal(scenario != "inactive", deletion.Deleted.IsActive);
                if (later is not null) await _profiles.SelectAsync(new(Name: "Later"), default);
            },
        };
        using var cancellation = new CancellationTokenSource();
        bool canceled = failure != "cleanup-failure";
        bool cleanupIncomplete = failure != "late-cancellation";
        Exception primary = canceled ? new OperationCanceledException(canary, cancellation.Token)
            : new CacheProtectionUnavailableException(canary, "Repair protected storage.");
        primary.Data["untrusted"] = canary;
        if (canceled) _credentials.AfterRemove = cancellation.Cancel;
        if (cleanupIncomplete) _credentials.RemoveFailure = primary;

        var output = new StringWriter();
        var error = new StringWriter();
        var facade = new FakeOperations();
        string[] options = json ? ["--json"] : [];
        int exit = await new AgentHostCli(facade, new FacadeHandlerProvider(facade, Service(store: store))).InvokeAsync(
            ["auth", "delete", "--name", deleted.Name!, .. options, "--debug"], output, error, false, cancellation.Token);

        Assert.Equal(canceled ? 130 : 1, exit);
        Assert.NotNull(snapshot);
        Assert.Equal(scenario == "last" ? null : first.ProfileId, snapshot.Active?.ProfileId);
        Assert.Equal(scenario == "last" ? null : (later ?? first).ProfileId, (await _profiles.GetActiveProfileAsync(default))?.ProfileId);
        Assert.DoesNotContain(canary, output.ToString() + error);
        Assert.DoesNotContain(deleted.CredentialReference!, output.ToString() + error);
        Assert.DoesNotContain(first.CredentialReference!, output.ToString() + error);
        Assert.Equal(0, _credentials.Authentications);
        Assert.Equal(0, _inventory.Clients);
        if (json)
        {
            Assert.Empty(error.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            Assert.Equal(canceled ? 99001 : 12006, root.GetProperty("errorCode").GetInt32());
            Assert.Equal(exit, root.GetProperty("exitCode").GetInt32());
            var partial = root.GetProperty("partialResult");
            Assert.True(partial.GetProperty("metadataCommitted").GetBoolean());
            Assert.Equal(cleanupIncomplete, partial.GetProperty("cleanupIncomplete").GetBoolean());
            Assert.False(partial.GetProperty("profile").GetProperty("isActive").GetBoolean());
            Assert.Equal(deleted.ProfileId, partial.GetProperty("profile").GetProperty("profileId").GetString());
            Assert.True(partial.GetProperty("profileDeleted").GetBoolean());
            var active = partial.GetProperty("activeProfile");
            if (snapshot.Active is null) Assert.Equal(JsonValueKind.Null, active.ValueKind);
            else
            {
                Assert.True(active.GetProperty("isActive").GetBoolean());
                Assert.Equal(snapshot.Active.ProfileId, active.GetProperty("profileId").GetString());
            }
        }
        else
        {
            Assert.Empty(output.ToString());
            var lines = error.ToString().Split(System.Environment.NewLine);
            var deletedLine = Assert.Single(lines, line => line.StartsWith("Deleted profile:", StringComparison.Ordinal));
            Assert.Contains($"{deleted.Name} | {deleted.Username}", deletedLine);
            var activeLine = Assert.Single(lines, line => line.StartsWith("Active profile at commit:", StringComparison.Ordinal));
            Assert.Contains(snapshot.Active is null ? "none" : $"{snapshot.Active.Name} | {snapshot.Active.Username}", activeLine);
            Assert.Contains("was committed", error.ToString());
            Assert.DoesNotContain("12006", error.ToString());
        }
    }

    [Fact]
    public async Task SuccessfulDeleteReportsNonactiveDeletedProfileAndCommittedFallback()
    {
        var first = await _profiles.CreateAsync(Profile("a"), default);
        var deleted = await _profiles.CreateAsync(Profile("b"), default);
        var result = await Service().DeleteAsync(new(Index: 2), default);
        Assert.Equal(deleted.ProfileId, result.Profile.ProfileId);
        Assert.False(result.Profile.IsActive);
        Assert.Equal(first.ProfileId, result.Active!.ProfileId);
        Assert.True(result.Active.IsActive);
    }

    [Fact]
    public async Task ClearRepairsCorruptMetadataAndSelectionsWithoutReadingOrAuthenticating()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "profiles.json"), "{corrupt");
        _selections.Active = new()
        {
            EnvironmentId = "legacy", DisplayName = "legacy", TenantId = Profile().TenantId!,
            ApiEndpoint = Environment().ApiEndpoint, ApiAudience = Environment().ApiAudience,
        };
        Assert.Equal("signed-out", (await Service().ClearAsync(default)).Status);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
        Assert.Null(_selections.Active);
        Assert.Equal(1, _credentials.Clears);
        Assert.Equal(0, _credentials.Authentications);
    }

    [Fact]
    public async Task ClearFailurePreservesPartialStateAndPrimaryContract()
    {
        await _profiles.CreateAsync(Profile(), default);
        _credentials.ClearFailure = new CacheProtectionUnavailableException("Unavailable.", "Repair native storage.");
        var error = await Assert.ThrowsAsync<AuthOperationException>(() => Service().ClearAsync(default));
        Assert.True(error.MetadataCommitted);
        Assert.Equal(12006, error.ErrorCode);
        Assert.Equal("Repair native storage.", error.Remediation);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Fact]
    public async Task InvalidEnvironmentUrlAndConflictingScopesFailBeforeStaging()
    {
        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            Service().CreateAsync(new() { Environment = "https://untrusted.example.test" }, default));
        await Assert.ThrowsAsync<UsageException>(() =>
            Service().CreateAsync(new() { Resource = "https://example.test", Scopes = ["scope"] }, default));
        Assert.Equal(0, _credentials.Authentications);
        Assert.Equal(0, _inventory.Clients);
    }

    [Fact]
    public async Task FailedPersistenceWithSuccessfulRollbackRethrowsOriginalFailure()
    {
        var primary = new AuthProfileStateException("Cannot write.", "Repair profile storage.");
        var error = await Assert.ThrowsAsync<AuthProfileStateException>(() =>
            Service(store: new Store(_profiles) { CreateFailure = primary }).CreateAsync(new(), default));
        Assert.Same(primary, error);
        Assert.Single(_credentials.Removed);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Fact]
    public async Task CanceledPrecommitAndFailedCleanupExposeUncommittedPartialState()
    {
        using var cancel = new CancellationTokenSource();
        _inventory.BeforeList = () => { cancel.Cancel(); return Task.CompletedTask; };
        _credentials.RemoveFailure = new CacheProtectionUnavailableException("Unavailable.");
        var error = await Assert.ThrowsAsync<AuthOperationCanceledException>(() =>
            Service().CreateAsync(new() { Environment = "env-a" }, cancel.Token));
        Assert.False(error.MetadataCommitted);
        Assert.True(error.CleanupIncomplete);
        Assert.False(_credentials.CleanupTokenCanceled);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Fact]
    public async Task AtomicCreateCapturesReplacementChangedDuringStaging()
    {
        var original = await _profiles.CreateAsync(Profile() with { CredentialReference = "first-reference" }, default);
        _credentials.BeforeReturn = async () =>
        {
            await _profiles.CreateAsync(Profile() with { CredentialReference = "latest-reference" }, default);
        };
        await Service().CreateAsync(new(), default);
        Assert.Equal(original.ProfileId, _credentials.Completed!.ProfileId);
        Assert.Equal("latest-reference", Assert.Single(_credentials.Removed).CredentialReference);
    }

    [Fact]
    public async Task ForeignCredentialDeleteDoesNotCallOwnedProvider()
    {
        await _profiles.CreateAsync(Profile() with { CredentialReference = "shared-pac-reference" }, default);
        _credentials.IsOwned = false;
        await Service().DeleteAsync(new(Index: 1), default);
        Assert.Empty(_credentials.Removed);
        Assert.Empty(await _profiles.GetProfilesAsync(default));
    }

    [Fact]
    public async Task AllowedPickerSelectsWithoutAuthenticationOrInventory()
    {
        var a = await _profiles.CreateAsync(Profile("a"), default);
        await _profiles.CreateAsync(Profile("b"), default);
        var picker = new Picker(profiles => Task.FromResult<string?>(profiles[0].ProfileId));
        var selected = await Service(picker: picker).SelectAsync(new(), default);
        Assert.Equal(a.ProfileId, selected.Profile.ProfileId);
        Assert.Equal(a.ProfileId, (await _profiles.GetActiveProfileAsync(default))!.ProfileId);
        Assert.Equal(0, _inventory.Clients);
        Assert.Equal(0, _credentials.Authentications);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("clear")]
    public async Task LateCancellationReportsCompletedCleanupAccurately(string operation)
    {
        using var cancel = new CancellationTokenSource();
        Task Execute()
        {
            if (operation == "create")
            {
                _credentials.AfterComplete = cancel.Cancel;
                return Service().CreateAsync(new(), cancel.Token);
            }
            if (operation == "delete")
            {
                _credentials.AfterRemove = cancel.Cancel;
                return Service().DeleteAsync(new(Index: 1), cancel.Token);
            }
            _credentials.AfterClear = cancel.Cancel;
            return Service().ClearAsync(cancel.Token);
        }
        if (operation != "create") await _profiles.CreateAsync(Profile(), default);
        var error = await Assert.ThrowsAsync<AuthOperationCanceledException>(Execute);
        Assert.True(error.MetadataCommitted);
        Assert.False(error.CleanupIncomplete);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    private sealed class Credentials : IProfileCredentialProvider, IOwnedProfileCredentialCleanup
    {
        public bool IsOwned { get; set; } = true;
        public bool OwnsCredential(AuthProfile profile) => IsOwned && profile.CredentialReference is not null;
        public AuthProfile Staged { get; set; } = Profile() with { ProfileId = "stage-id" };
        public AuthCreateRequest? Request { get; private set; }
        public AuthProfile? Completed { get; private set; }
        public int Authentications { get; private set; }
        public int Completions { get; private set; }
        public int Clears { get; private set; }
        public bool CleanupTokenCanceled { get; private set; }
        public List<AuthProfile> Removed { get; } = [];
        public Exception? CompleteFailure { get; set; }
        public Exception? RemoveFailure { get; set; }
        public Exception? ClearFailure { get; set; }
        public Func<Task>? BeforeReturn { get; set; }
        public Action? AfterComplete { get; set; }
        public Action? AfterRemove { get; set; }
        public Action? AfterClear { get; set; }
        public async Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct)
        {
            Authentications++;
            Request = request;
            if (BeforeReturn is not null) await BeforeReturn();
            return Staged with { Name = request.Name };
        }
        public Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken ct) =>
            throw new InvalidOperationException("Metadata operations must not acquire.");
        public Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct)
        {
            Completions++;
            Completed = profile;
            AfterComplete?.Invoke();
            return CompleteFailure is null ? Task.CompletedTask : Task.FromException(CompleteFailure);
        }
        public Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct)
        {
            CleanupTokenCanceled = ct.IsCancellationRequested;
            Removed.Add(profile);
            AfterRemove?.Invoke();
            return RemoveFailure is null ? Task.CompletedTask : Task.FromException(RemoveFailure);
        }
        public Task ClearOwnedCredentialsAsync(CancellationToken ct)
        {
            Clears++;
            AfterClear?.Invoke();
            return ClearFailure is null ? Task.CompletedTask : Task.FromException(ClearFailure);
        }
    }

    private sealed class Inventory : IProfileEnvironmentClientFactory, IEnvironmentClient
    {
        public int Clients { get; private set; }
        public List<EnvironmentResolutionContext> Contexts { get; } = [];
        public IReadOnlyList<PowerPlatformEnvironment> Available { get; set; } = [Environment()];
        public Func<Task>? BeforeList { get; set; }
        public IEnvironmentClient CreateClient(EnvironmentResolutionContext context)
        {
            Clients++;
            Contexts.Add(context);
            return this;
        }
        public async Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken)
        {
            if (BeforeList is not null) await BeforeList();
            cancellationToken.ThrowIfCancellationRequested();
            return Available;
        }
    }

    private sealed class Selections : IEnvironmentSelectionStore
    {
        public ActiveEnvironment? Active { get; set; }
        public Task<ActiveEnvironment?> GetActiveAsync(CancellationToken ct) => Task.FromResult(Active);
        public Task SetActiveAsync(ActiveEnvironment environment, CancellationToken ct) { Active = environment; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct) { Active = null; return Task.CompletedTask; }
    }

    private sealed class Picker(Func<IReadOnlyList<AuthProfileInfo>, Task<string?>> pick) : IAuthProfilePicker
    {
        public bool Called { get; private set; }
        public Task<string?> PickAsync(IReadOnlyList<AuthProfileInfo> profiles, CancellationToken ct)
        {
            Called = true;
            return pick(profiles);
        }
    }

    private sealed class Store(IAuthProfileManagementStore inner) : IAuthProfileManagementStore, IAuthProfileCreationStore
    {
        public Exception? CreateFailure { get; set; }
        public Action? AfterCreate { get; set; }
        public Func<AuthProfileDeletion, Task>? AfterDelete { get; set; }
        public Task<AuthProfile> CreateAsync(AuthProfile profile, CancellationToken ct) =>
            CreateFailure is null ? inner.CreateAsync(profile, ct) : Task.FromException<AuthProfile>(CreateFailure);
        public async Task<AuthProfileCreation> CreateWithReplacementAsync(AuthProfile profile, CancellationToken ct)
        {
            if (CreateFailure is not null) throw CreateFailure;
            var creation = await ((IAuthProfileCreationStore)inner).CreateWithReplacementAsync(profile, ct);
            AfterCreate?.Invoke();
            return creation;
        }
        public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken ct) => inner.GetProfilesAsync(ct);
        public Task<AuthProfile?> GetActiveProfileAsync(CancellationToken ct) => inner.GetActiveProfileAsync(ct);
        public Task UpsertAsync(AuthProfile profile, CancellationToken ct) => inner.UpsertAsync(profile, ct);
        public Task SetActiveAsync(string key, CancellationToken ct) => inner.SetActiveAsync(key, ct);
        public Task ClearAsync(CancellationToken ct) => inner.ClearAsync(ct);
        public Task<AuthProfile> SelectAsync(AuthProfileSelector selector, CancellationToken ct) => inner.SelectAsync(selector, ct);
        public Task<AuthProfile> UpdateAsync(AuthProfileSelector selector, AuthProfileUpdate update, CancellationToken ct) => inner.UpdateAsync(selector, update, ct);
        public Task<AuthProfile> UpdateAsync(string id, long revision, AuthProfileUpdate update, CancellationToken ct) => inner.UpdateAsync(id, revision, update, ct);
        public async Task<AuthProfileDeletion> DeleteAsync(AuthProfileSelector selector, CancellationToken ct)
        {
            var deletion = await inner.DeleteAsync(selector, ct);
            if (AfterDelete is not null) await AfterDelete(deletion);
            return deletion;
        }
        public Task<AuthProfile> SetDefaultEnvironmentAsync(string id, long revision, ActiveEnvironment environment, CancellationToken ct) =>
            inner.SetDefaultEnvironmentAsync(id, revision, environment, ct);
    }
}
