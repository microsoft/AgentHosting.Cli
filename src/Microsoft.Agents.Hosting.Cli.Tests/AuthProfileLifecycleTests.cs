using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AuthProfileLifecycleTests
{
    private static AuthProfile Profile(string key, string? name = null) => new()
    {
        AccountKey = key,
        Username = key + "@contoso.com",
        TenantId = "tenant-a",
        ClientId = "client-a",
        Authority = "https://login.microsoftonline.com/organizations",
        Name = name,
    };

    private static ActiveEnvironment Environment(string id = "env-a") => new()
    {
        EnvironmentId = id,
        DisplayName = id,
        TenantId = "tenant-a",
        ApiEndpoint = new Uri("https://env.api.powerplatform.com"),
        ApiAudience = "https://api.powerplatform.com/",
    };

    private static IAuthProfileManagementStore Management(FileAuthProfileStore store) =>
        Assert.IsAssignableFrom<IAuthProfileManagementStore>(store);

    [Fact]
    public async Task LegacyMigration_IsStableReadOnly_AndNextMutationWritesV2()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        const string original = """
            {"schemaVersion":1,"activeAccountKey":"home-b","profiles":[
            {"accountKey":"home-a","username":"a@contoso.com","cloud":"Public","flowType":"device-code"},
            {"accountKey":"home-b","username":"b@contoso.com","cloud":"Public","ring":"Preprod"}]}
            """;
        await File.WriteAllTextAsync(path, original);
        var store = new FileAuthProfileStore(path);
        var first = await store.GetProfilesAsync(default);
        var second = await new FileAuthProfileStore(path).GetProfilesAsync(default);
        Assert.All(first, p => Assert.False(string.IsNullOrWhiteSpace(p.ProfileId)));
        Assert.Equal(first.Select(p => p.ProfileId), second.Select(p => p.ProfileId));
        Assert.Equal(2, first.Select(p => p.ProfileId).Distinct().Count());
        Assert.Equal("home-a", first[0].HomeAccountId);
        Assert.Equal(AuthCredentialMethod.DeviceCode, first[0].CredentialMethod);
        Assert.Equal(PowerPlatformApiRing.Preprod, first[1].Ring);
        Assert.Equal("https://api.preprod.powerplatform.com", first[1].ConnectionUrl);
        Assert.True(first[1].IsActive);
        Assert.Equal(original, await File.ReadAllTextAsync(path));

        await store.SetActiveAsync(first[0].ProfileId, default);
        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(2, written.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(first[0].ProfileId, written.RootElement.GetProperty("activeProfileId").GetString());
        Assert.False(written.RootElement.TryGetProperty("activeAccountKey", out _));
        Assert.Equal(first[1].ProfileId, (await store.GetProfilesAsync(default))[1].ProfileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyMigrationIdentity_IsStableAcrossCultures(bool customNegativeSign)
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        const string original = """
            {"schemaVersion":1,"activeAccountKey":"home-a","profiles":[
            {"accountKey":"home-a","username":"a@contoso.com"}]}
            """;
        await File.WriteAllTextAsync(path, original);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = await new FileAuthProfileStore(path).GetActiveProfileAsync(default);
            var alternate = new CultureInfo("sv-SE");
            if (customNegativeSign)
            {
                alternate.NumberFormat.NegativeSign = "~";
            }

            CultureInfo.CurrentCulture = alternate;
            var localized = await new FileAuthProfileStore(path).GetActiveProfileAsync(default);
            Assert.Equal(english?.ProfileId, localized?.ProfileId);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("device-code", AuthCredentialMethod.DeviceCode)]
    [InlineData("interactive", AuthCredentialMethod.Browser)]
    public async Task LegacyUpsert_ReusesMigratedFlowIdentity(string flow, AuthCredentialMethod expected)
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, $$"""
            {"schemaVersion":1,"activeAccountKey":"home-a","profiles":[
            {"accountKey":"home-a","username":"old@contoso.com","cloud":"Public","flowType":"{{flow}}"}]}
            """);
        var store = new FileAuthProfileStore(path);
        var migrated = await store.GetActiveProfileAsync(default);
        await store.UpsertAsync(new AuthProfile
        {
            AccountKey = "home-a",
            Username = "new@contoso.com",
            FlowType = flow,
        }, default);
        var replaced = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal(migrated?.ProfileId, replaced.ProfileId);
        Assert.Equal(expected, replaced.CredentialMethod);
        Assert.Equal("new@contoso.com", replaced.Username);
    }

    [Theory]
    [InlineData("authenticatedObjectId", false)]
    [InlineData("authenticatedClientId", false)]
    [InlineData("authenticatedContext", false)]
    [InlineData("unknownMetadata", false)]
    [InlineData("authenticatedContext", true)]
    [InlineData("unknownMetadata", true)]
    public async Task LegacyIgnoredPayloads_DoNotInvalidateMigration(string propertyName, bool atDocument)
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        string ignoredProperty = $$"""
            "{{propertyName}}":{"value":1,"value":2}
            """;
        string documentExtra = atDocument ? "," + ignoredProperty : string.Empty;
        string profileExtra = atDocument ? string.Empty : "," + ignoredProperty;
        string original = $$"""
            {"schemaVersion":1,"activeAccountKey":"home-a"{{documentExtra}},"profiles":[
            {"accountKey":"home-a","username":"a@contoso.com","cloud":"Public"{{profileExtra}}}]}
            """;
        await File.WriteAllTextAsync(path, original);
        var store = new FileAuthProfileStore(path);

        var migrated = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal("home-a", migrated.HomeAccountId);
        Assert.True(migrated.IsActive);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        await store.SetActiveAsync(migrated.ProfileId, default);
        Assert.Equal(migrated.ProfileId, (await store.GetActiveProfileAsync(default))?.ProfileId);
        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(2, written.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(written.RootElement.TryGetProperty(propertyName, out _));
        Assert.False(written.RootElement.GetProperty("profiles")[0].TryGetProperty(propertyName, out _));
    }

    [Fact]
    public async Task LegacyIgnoredDefaultEnvironmentPayload_DoesNotInvalidateTargetingMetadata()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":1,"activeAccountKey":"home-a","profiles":[
            {"accountKey":"home-a","username":"a@contoso.com","defaultEnvironment":{
            "environmentId":"env-a","displayName":"env-a","tenantId":"tenant-a",
            "apiEndpoint":"https://env.api.powerplatform.com","apiAudience":"https://api.powerplatform.com/",
            "unknownMetadata":{"value":1,"value":2}}}]}
            """);
        var store = new FileAuthProfileStore(path);
        var migrated = await store.GetActiveProfileAsync(default);
        Assert.Equal(Environment(), migrated?.DefaultEnvironment);
        await store.SetActiveAsync(migrated!.ProfileId, default);
        Assert.Equal(Environment(), (await store.GetActiveProfileAsync(default))?.DefaultEnvironment);
    }

    public static TheoryData<AuthProfile> DifferentIdentities => new()
    {
        Profile("home-a") with { TenantId = "tenant-b" },
        Profile("home-a") with { ClientId = "client-b" },
        Profile("home-a") with { Authority = "https://login.microsoftonline.com/tenant-b" },
        Profile("home-a") with { Cloud = CloudInstance.UsGov },
        Profile("home-a") with { Ring = PowerPlatformApiRing.Test },
        Profile("home-a") with { CredentialMethod = AuthCredentialMethod.Browser },
        Profile("home-a") with { PrincipalType = AuthPrincipalType.ServicePrincipal },
        Profile("home-a") with { CertificateThumbprint = "thumbprint-b" },
        Profile("home-a") with { CertificatePath = "certificate-b.pfx" },
        Profile("home-a") with { HomeAccountId = "other-home" },
    };

    [Theory]
    [MemberData(nameof(DifferentIdentities))]
    public async Task IdentityDimensions_DoNotConflateProfiles(AuthProfile different)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-a"), default);
        await store.UpsertAsync(different, default);
        Assert.Equal(2, (await store.GetProfilesAsync(default)).Count);
        await Assert.ThrowsAsync<UsageException>(() => store.SetActiveAsync("home-a", default));
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Broker)]
    [InlineData(AuthCredentialMethod.Browser)]
    [InlineData(AuthCredentialMethod.DeviceCode)]
    public async Task CapturedLegacyUpsert_RetargetsOnlyTheActiveProfileAndDropsItsOldRingDefault(AuthCredentialMethod method)
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var captured = await store.CreateAsync(Profile("home-a", "Named") with
        {
            CredentialMethod = method,
            DefaultEnvironment = Environment(),
        }, default);
        await store.UpsertAsync(captured with { Ring = PowerPlatformApiRing.Preprod }, default);

        var retargeted = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal(captured.ProfileId, retargeted.ProfileId);
        Assert.True(retargeted.Revision > captured.Revision);
        Assert.Equal("Named", retargeted.Name);
        Assert.True(retargeted.IsActive);
        Assert.Equal(PowerPlatformApiRing.Preprod, retargeted.Ring);
        Assert.Equal("https://api.preprod.powerplatform.com", retargeted.ConnectionUrl);
        Assert.Null(retargeted.DefaultEnvironment);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("switched")]
    [InlineData("deleted")]
    [InlineData("collision")]
    [InlineData("credential-bound")]
    [InlineData("other-identity")]
    public async Task CapturedLegacyRingUpsert_RejectsUnsafeRetargetingWithoutMutation(string scenario)
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var store = new FileAuthProfileStore(path);
        var captured = await store.CreateAsync(Profile("home-a") with
        {
            CredentialReference = scenario == "credential-bound" ? "protected-reference" : null,
        }, default);
        switch (scenario)
        {
            case "stale":
                await store.UpdateAsync(captured.ProfileId, captured.Revision, new(Name: "Changed"), default);
                break;
            case "switched":
                await store.CreateAsync(Profile("home-b"), default);
                break;
            case "deleted":
                await store.DeleteAsync(new(Index: 1), default);
                break;
            case "collision":
                await store.CreateAsync(Profile("home-a") with { Ring = PowerPlatformApiRing.Test }, default);
                await store.SetActiveAsync(captured.ProfileId, default);
                captured = (await store.GetActiveProfileAsync(default))!;
                break;
        }
        var requested = captured with
        {
            Ring = PowerPlatformApiRing.Test,
            TenantId = scenario == "other-identity" ? "tenant-b" : captured.TenantId,
        };
        var before = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<UsageException>(() => store.UpsertAsync(requested, default));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateWithCapturedIdAndAnotherRing_StillCreatesADistinctProfile()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        var original = await store.CreateAsync(Profile("home-a"), default);
        var other = await store.CreateAsync(original with { Ring = PowerPlatformApiRing.Test }, default);
        Assert.NotEqual(original.ProfileId, other.ProfileId);
        Assert.Equal(2, (await store.GetProfilesAsync(default)).Count);
    }

    [Fact]
    public async Task MatchingCreate_PreservesId_AppendsReplacement_IncrementsRevisionAndActivates()
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json")));
        var first = await store.CreateAsync(Profile("home-a", "First"), default);
        await store.CreateAsync(Profile("home-b", "Second"), default);
        var replaced = await store.CreateAsync(Profile("home-a", "Renamed") with { Username = "renamed@contoso.com" }, default);
        Assert.Equal(first.ProfileId, replaced.ProfileId);
        Assert.True(replaced.Revision > first.Revision);
        Assert.True(replaced.IsActive);
        var profiles = await store.GetProfilesAsync(default);
        Assert.Equal("Second", profiles[0].Name);
        Assert.Equal("Renamed", profiles[1].Name);
        Assert.Equal("renamed@contoso.com", replaced.Username);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementAppends_PreservingRemainingOrderAndDeletionFallback(bool legacyUpsert)
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json")));
        var first = await store.CreateAsync(Profile("home-a", "First"), default);
        var second = await store.CreateAsync(Profile("home-b", "Second"), default);
        var replacement = Profile("home-a", "Renamed") with { Username = "renamed@contoso.com" };
        if (legacyUpsert)
        {
            await store.UpsertAsync(replacement, default);
        }
        else
        {
            await store.CreateAsync(replacement, default);
        }

        var replaced = await store.GetActiveProfileAsync(default);
        Assert.Equal(first.ProfileId, replaced?.ProfileId);
        Assert.True(replaced?.Revision > first.Revision);
        Assert.Equal(second.ProfileId, (await store.GetProfilesAsync(default))[0].ProfileId);
        Assert.Equal(first.ProfileId, (await store.GetProfilesAsync(default))[1].ProfileId);
        Assert.Equal(second.ProfileId, (await store.SelectAsync(new(Index: 1), default)).ProfileId);

        await store.CreateAsync(Profile("home-c", "Third"), default);
        var deletion = await store.DeleteAsync(new(Name: "Third"), default);
        Assert.Equal(second.ProfileId, deletion.Active?.ProfileId);
    }

    [Fact]
    public async Task CreateNullName_IsUnnamed_WhileLegacyUpsertPreservesProfileOwnedMetadata()
    {
        using var dir = new TempDir();
        var fileStore = new FileAuthProfileStore(dir.File("profiles.json"));
        var store = Management(fileStore);
        var first = await store.CreateAsync(Profile("home-a", "Named") with { DefaultEnvironment = Environment() }, default);
        await fileStore.UpsertAsync(Profile("home-a") with { Username = "renamed@contoso.com" }, default);
        var signedIn = await store.GetActiveProfileAsync(default);
        Assert.Equal("Named", signedIn?.Name);
        Assert.Equal(first.DefaultEnvironment, signedIn?.DefaultEnvironment);
        Assert.Equal(first.ProfileId, signedIn?.ProfileId);
        var unnamed = await store.CreateAsync(Profile("home-a"), default);
        Assert.Null(unnamed.Name);
        Assert.Equal(first.ProfileId, unnamed.ProfileId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456789012345678901")]
    public async Task Create_InvalidName_DoesNotPersist(string name)
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json")));
        await Assert.ThrowsAsync<UsageException>(() => store.CreateAsync(Profile("home-a", name), default));
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task NamesAndSelectors_AreOrdinalAndUnambiguous()
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json")));
        var first = await store.CreateAsync(Profile("home-a", "Work"), default);
        var second = await store.CreateAsync(Profile("home-b", "work"), default);
        await Assert.ThrowsAsync<UsageException>(() => store.CreateAsync(Profile("home-c", "Work"), default));
        Assert.Equal(first.ProfileId, (await store.SelectAsync(new(Name: "Work"), default)).ProfileId);
        Assert.Equal(second.ProfileId, (await store.SelectAsync(new(Index: 2), default)).ProfileId);
        foreach (var invalid in new AuthProfileSelector[] { new(), new(Index: 0), new(Index: 3), new(Name: ""), new(Name: "WORK"), new(1, "Work") })
        {
            await Assert.ThrowsAsync<UsageException>(() => store.SelectAsync(invalid, default));
        }
    }

    [Fact]
    public async Task SetActive_ProfileIdAndLegacyKeyMatchingDifferentProfiles_IsAmbiguousWithoutMutation()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var observer = new RecordingObserver();
        var fileStore = new FileAuthProfileStore(path, observer);
        var store = Management(fileStore);
        var first = await store.CreateAsync(Profile("home-a", "First"), default);
        var second = await store.CreateAsync(Profile(first.ProfileId, "Second"), default);
        string before = await File.ReadAllTextAsync(path);
        long revision = observer.Mutation;

        await Assert.ThrowsAsync<UsageException>(() => fileStore.SetActiveAsync(second.AccountKey, default));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(second, await store.GetActiveProfileAsync(default));
        Assert.Equal(revision, observer.Mutation);
    }

    [Fact]
    public async Task SetActive_OneProfileMatchingBothIdAndLegacyKey_IsUnambiguous()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        const string id = "711be391a47f42e5b59c476d7c969491";
        await File.WriteAllTextAsync(path, $$"""
            {"schemaVersion":2,"activeProfileId":null,"profiles":[
            {"profileId":"{{id}}","accountKey":"{{id}}","username":"a@contoso.com","revision":1}]}
            """);
        var store = new FileAuthProfileStore(path);
        await store.SetActiveAsync(id, default);
        Assert.Equal(id, (await store.GetActiveProfileAsync(default))?.ProfileId);
        Assert.Single(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task InactiveUpdate_PreservesActivePointer_AndNullPreservesName_EmptyClears()
    {
        using var dir = new TempDir();
        var observer = new RecordingObserver();
        var revision = new AuthProfileMutationRevision();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json"), observer, revision));
        var first = await store.CreateAsync(Profile("home-a", "First"), default);
        var active = await store.CreateAsync(Profile("home-b", "Second"), default);
        var updated = await store.UpdateAsync(new(Name: "First"), new(DefaultEnvironment: Environment()), default);
        Assert.Equal("First", updated.Name);
        Assert.Equal(Environment(), updated.DefaultEnvironment! with { Owner = null, DefaultGeneration = null });
        Assert.Equal(updated.ProfileId, updated.DefaultEnvironment?.Owner?.ProfileId);
        Assert.False(updated.IsActive);
        Assert.True(updated.Revision > first.Revision);
        Assert.Equal(active.ProfileId, (await store.GetActiveProfileAsync(default))?.ProfileId);
        Assert.Equal(active.ProfileId, observer.Active?.ProfileId);
        Assert.Equal((await store.GetActiveProfileAsync(default))?.Revision, observer.Active?.Revision);
        Assert.Equal(revision.Current, observer.Mutation);
        var cleared = await store.UpdateAsync(new(Index: 1), new(Name: ""), default);
        Assert.Null(cleared.Name);
        await Assert.ThrowsAsync<UsageException>(() => store.UpdateAsync(new(Index: 1), new(Name: "Second"), default));
    }

    [Fact]
    public async Task DefaultEnvironmentCommit_RequiresRevisionAndActiveOwnership()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var store = Management(new FileAuthProfileStore(path));
        var otherStore = Management(new FileAuthProfileStore(path));
        var first = await store.CreateAsync(Profile("home-a", "First"), default);
        var updated = await store.SetDefaultEnvironmentAsync(first.ProfileId, first.Revision, Environment(), default);
        Assert.True(updated.Revision > first.Revision);
        await Assert.ThrowsAsync<UsageException>(() => store.SetDefaultEnvironmentAsync(first.ProfileId, first.Revision, Environment("stale"), default));
        await otherStore.CreateAsync(Profile("home-b"), default);
        await Assert.ThrowsAsync<UsageException>(() => store.SetDefaultEnvironmentAsync(updated.ProfileId, updated.Revision, Environment("wrong-owner"), default));
        await store.SelectAsync(new(Name: "First"), default);
        await Assert.ThrowsAsync<UsageException>(() => store.SetDefaultEnvironmentAsync(updated.ProfileId, updated.Revision, Environment("switched-back"), default));
        Assert.Equal("env-a", (await store.GetActiveProfileAsync(default))?.DefaultEnvironment?.EnvironmentId);
    }

    [Fact]
    public async Task DeleteInactive_PreservesActive_DeleteActive_SelectsFirstRemaining()
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json")));
        var first = await store.CreateAsync(Profile("home-a", "First"), default);
        var second = await store.CreateAsync(Profile("home-b", "Second"), default);
        var third = await store.CreateAsync(Profile("home-c", "Third"), default);
        var inactive = await store.DeleteAsync(new(Name: "Second"), default);
        Assert.Equal(second.ProfileId, inactive.Deleted.ProfileId);
        Assert.False(inactive.Deleted.IsActive);
        Assert.Equal(third.ProfileId, inactive.Active?.ProfileId);
        var active = await store.DeleteAsync(new(Name: "Third"), default);
        Assert.True(active.Deleted.IsActive);
        Assert.Equal(first.ProfileId, active.Active?.ProfileId);
        Assert.True(active.Active?.Revision > first.Revision);
        Assert.Null((await store.DeleteAsync(new(Index: 1), default)).Active);
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task DeleteActive_UniversalFallback_IgnoresPrincipalTypeAndCredentialMethod()
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json")));
        var service = await store.CreateAsync(Profile("service-a", "Service") with
        {
            PrincipalType = AuthPrincipalType.ServicePrincipal,
            CredentialMethod = AuthCredentialMethod.ClientSecret,
            CredentialReference = "protected-store-reference",
        }, default);
        var browser = await store.CreateAsync(Profile("home-a", "Browser") with
        {
            CredentialMethod = AuthCredentialMethod.Browser,
        }, default);
        await store.CreateAsync(Profile("home-b", "Device") with
        {
            CredentialMethod = AuthCredentialMethod.DeviceCode,
        }, default);

        var firstDeletion = await store.DeleteAsync(new(Name: "Device"), default);
        Assert.Equal(service.ProfileId, firstDeletion.Active?.ProfileId);
        Assert.Equal(AuthPrincipalType.ServicePrincipal, firstDeletion.Active?.PrincipalType);
        Assert.Equal(AuthCredentialMethod.ClientSecret, firstDeletion.Active?.CredentialMethod);
        var secondDeletion = await store.DeleteAsync(new(Name: "Service"), default);
        Assert.Equal(browser.ProfileId, secondDeletion.Active?.ProfileId);
        Assert.Equal(AuthPrincipalType.User, secondDeletion.Active?.PrincipalType);
        Assert.Equal(AuthCredentialMethod.Browser, secondDeletion.Active?.CredentialMethod);
    }

    [Fact]
    public async Task SeparateStores_ConcurrentMutations_DoNotLoseProfilesOrRevisions()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            new FileAuthProfileStore(path).UpsertAsync(Profile("home-" + i), default))));
        var store = Management(new FileAuthProfileStore(path));
        Assert.Equal(20, (await store.GetProfilesAsync(default)).Count);
        var first = await store.SelectAsync(new(Index: 1), default);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            Management(new FileAuthProfileStore(path)).UpdateAsync(new(Index: 1), new(DefaultEnvironment: Environment("env-" + i)), default))));
        Assert.Equal(first.Revision + 12, (await store.GetActiveProfileAsync(default))?.Revision);
    }

    [Fact]
    public async Task CancellationAtEntry_DoesNotReadOrMutate_AndLockWaitIsCancelable()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var store = Management(new FileAuthProfileStore(path));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetProfilesAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetActiveProfileAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync(Profile("home-a"), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(Profile("home-a"), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SelectAsync(new(Index: 1), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(new(Index: 1), new(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(new(Index: 1), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetDefaultEnvironmentAsync("id", 1, Environment(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetActiveAsync("home-a", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ClearAsync(canceled.Token));
        Assert.False(File.Exists(path));

        await store.CreateAsync(Profile("home-a"), default);
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var waiting = new CancellationTokenSource();
        var mutation = store.UpsertAsync(Profile("home-b"), waiting.Token);
        Assert.False(mutation.IsCompleted);
        waiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Single(await store.GetProfilesAsync(default));
    }

    [Theory]
    [InlineData(35, true, true)]
    [InlineData(35, false, false)]
    [InlineData(11, false, true)]
    [InlineData(32, false, true)]
    [InlineData(33, false, true)]
    [InlineData(unchecked((int)0x80070020), false, true)]
    [InlineData(unchecked((int)0x80070021), false, true)]
    [InlineData(13, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(unchecked((int)0x80070005), true, false)]
    [InlineData(unchecked((int)0x80070023), true, false)]
    public void LockContention_RecognizesDarwinErrnoWithoutRetryingUnrelatedErrors(
        int errorCode, bool isMacOS, bool retryExpected)
    {
        var error = new IOException("Injected file-lock error.", errorCode);
        Assert.Equal(retryExpected, FileAuthProfileStore.IsLockContention(error, isMacOS));
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"profiles":[{"profileId":"same","accountKey":"a"},{"profileId":"same","accountKey":"b"}]}""")]
    [InlineData("""{"schemaVersion":2,"activeProfileId":"ghost","profiles":[]}""")]
    [InlineData("""{"schemaVersion":2,"profiles":[{"accountKey":"a"}]}""")]
    [InlineData("""{"schemaVersion":2,"schemaVersion":1,"profiles":[]}""")]
    [InlineData("""{"schemaVersion":2,"profiles":[{"profileId":"id","accountKey":"a","name":"dup"},{"profileId":"id2","accountKey":"b","name":"dup"}]}""")]
    [InlineData("""{"schemaVersion":2,"profiles":[{"profileId":"id","accountKey":"a","credentialMethod":999}]}""")]
    [InlineData("""{"schemaVersion":1,"activeProfileId":"ghost","profiles":[]}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"home-a","profiles":[{"accountKey":"home-a","username":"a","authenticatedTenantId":{"value":1,"value":2},"authenticatedTenantId":null}]}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"home-a","activeAccountKey":"home-a","profiles":[{"accountKey":"home-a","username":"a"}]}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"home-a","profiles":[{"accountKey":"home-a","username":"a","ring":"Production","ring":"Test"}]}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"home-a","profiles":[{"accountKey":"home-a","username":"a","authority":"https://login.microsoftonline.com/common","authority":"https://login.microsoftonline.com/organizations"}]}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"home-a","profiles":[{"accountKey":"home-a","username":"a","defaultEnvironment":{"environmentId":"env-a","displayName":"env-a","tenantId":"tenant-a","tenantId":"tenant-b","apiEndpoint":"https://env.api.powerplatform.com","apiAudience":"https://api.powerplatform.com/"}}]}""")]
    public async Task Corruption_IsTyped_AndClearRecoversWithoutReading(string json)
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, json);
        var store = new FileAuthProfileStore(path);
        await Assert.ThrowsAsync<AuthProfileStateException>(() => store.GetProfilesAsync(default));
        await store.ClearAsync(default);
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task ClearFailure_IsObservable_AndDoesNotAdvanceObserver()
    {
        using var dir = new TempDir();
        var observer = new RecordingObserver();
        var store = new FileAuthProfileStore(dir.Path, observer);
        await Assert.ThrowsAsync<AuthProfileStateException>(() => store.ClearAsync(default));
        Assert.Equal(0, observer.Mutation);
    }

    [Fact]
    public async Task CredentialMetadata_RoundTripsWithoutOpeningCertificateOrCredentialStores()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var store = Management(new FileAuthProfileStore(path));
        var metadata = Profile("service-a", "Service") with
        {
            PrincipalType = AuthPrincipalType.ServicePrincipal,
            CredentialMethod = AuthCredentialMethod.Certificate,
            CertificatePath = dir.File("nonexistent-certificate.pfx"),
            CertificateThumbprint = "certificate-identity",
            CredentialReference = "protected-store-reference",
            ConnectionUrl = "https://api.powerplatform.com",
            DefaultEnvironment = Environment(),
        };
        var created = await store.CreateAsync(metadata, default);
        var reread = await new FileAuthProfileStore(path).GetActiveProfileAsync(default);
        Assert.Equal(created, reread);
        Assert.Null(reread?.HomeAccountId);
        Assert.False(File.Exists(metadata.CertificatePath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task ReplacedProfileFile_IsOwnerRestricted_WithoutInheritedPermissions()
    {
        using var dir = new TempDir();
        string path = dir.File("profiles.json");
        var store = new FileAuthProfileStore(path);
        await store.UpsertAsync(Profile("home-a"), default);
        await store.UpsertAsync(Profile("home-b"), default);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var access = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(identity.User, access.IdentityReference);
            Assert.Equal(AccessControlType.Allow, access.AccessControlType);
            Assert.False(access.IsInherited);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task ObserverOptOut_StillOrdersMutations_ButDoesNotObserveActiveMetadata()
    {
        using var dir = new TempDir();
        var observer = new RecordingObserver { IsObservationEnabled = false };
        var revision = new AuthProfileMutationRevision();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json"), observer, revision));
        await store.CreateAsync(Profile("home-a"), default);
        await store.UpdateAsync(new(Index: 1), new(Name: "Named"), default);
        await store.DeleteAsync(new(Index: 1), default);
        Assert.Equal(3, observer.Mutation);
        Assert.Equal(revision.Current, observer.Mutation);
        Assert.Equal(0, observer.ActiveObservations);
    }

    [Fact]
    public async Task ThrowingObserver_DoesNotFailCommittedMutation()
    {
        using var dir = new TempDir();
        var store = Management(new FileAuthProfileStore(dir.File("profiles.json"), new ThrowingObserver()));
        var created = await store.CreateAsync(Profile("home-a"), default);
        Assert.Equal(created, await store.GetActiveProfileAsync(default));
        await store.ClearAsync(default);
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    private sealed class RecordingObserver : IAuthProfileStateObserver
    {
        public bool IsObservationEnabled { get; init; } = true;
        public long Mutation { get; private set; }
        public AuthProfile? Active { get; private set; }
        public int ActiveObservations { get; private set; }
        public void ObserveProfileMutation(long revision) => Mutation = revision;
        public void ObserveActiveProfile(AuthProfile? profile, long revision)
        {
            Active = profile;
            ActiveObservations++;
        }
    }

    private sealed class ThrowingObserver : IAuthProfileStateObserver
    {
        public void ObserveProfileMutation(long revision) => throw new InvalidOperationException("Injected observer failure.");
        public void ObserveActiveProfile(AuthProfile? profile, long revision) => throw new InvalidOperationException("Injected observer failure.");
    }
}
