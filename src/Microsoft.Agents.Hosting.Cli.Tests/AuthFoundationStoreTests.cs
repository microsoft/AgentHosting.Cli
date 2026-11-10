using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Creates a unique temporary directory for a test and deletes it on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.GetFullPath("ah-cn3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}

public sealed class FileAuthProfileStoreTests
{
    private static AuthProfile Profile(
        string key,
        string user,
        string? tenant = null,
        Guid? authenticatedTenant = null,
        Guid? principalObjectId = null) => new()
    {
        AccountKey = key,
        Username = user,
        TenantId = tenant,
        ClientId = "11111111-2222-3333-4444-555555555555",
        Authority = "https://login.microsoftonline.com/organizations",
        Cloud = CloudInstance.Public,
        FlowType = "broker",
        AuthenticatedTenantId = authenticatedTenant?.ToString("D"),
        PrincipalObjectId = principalObjectId?.ToString("D"),
    };

    [Fact]
    public async Task SameAccountInDifferentTenants_RemainsTwoProfiles()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "one@contoso.com", "tenant-a"), default);
        await store.UpsertAsync(Profile("home-1", "one@contoso.com", "tenant-b"), default);
        Assert.Equal(2, (await store.GetProfilesAsync(default)).Count);
    }

    [Fact]
    public async Task AnUndeclaredRingOnDisk_IsRefusedRatherThanReadAsProduction()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(
            path,
            """
            {"activeAccountKey":"home-1","profiles":[{"accountKey":"home-1","username":"one@contoso.com","clientId":"11111111-2222-3333-4444-555555555555","authority":"https://login.microsoftonline.com/organizations","cloud":"Public","ring":987,"flowType":"broker"}]}
            """);

        var failure = await Assert.ThrowsAsync<AuthProfileStateException>(
            () => new FileAuthProfileStore(path).GetActiveProfileAsync(default));

        Assert.Contains("987", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent auth clear", failure.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProfileWrittenBeforeRingsWereSelectable_ReadsAsProduction()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(
            path,
            """
            {"activeAccountKey":"home-1","profiles":[{"accountKey":"home-1","username":"one@contoso.com","clientId":"11111111-2222-3333-4444-555555555555","authority":"https://login.microsoftonline.com/organizations","cloud":"Public","flowType":"broker"}]}
            """);

        var active = await new FileAuthProfileStore(path).GetActiveProfileAsync(default);

        Assert.Equal(PowerPlatformApiRing.Production, active!.Ring);
    }

    [Fact]
    public async Task ADeclaredRingOnDisk_IsReadBackUnchanged()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(
            path,
            """
            {"activeAccountKey":"home-1","profiles":[{"accountKey":"home-1","username":"one@contoso.com","clientId":"11111111-2222-3333-4444-555555555555","authority":"https://login.microsoftonline.com/organizations","cloud":"Public","ring":1,"flowType":"broker"}]}
            """);

        var active = await new FileAuthProfileStore(path).GetActiveProfileAsync(default);

        Assert.Equal(PowerPlatformApiRing.Preprod, active!.Ring);
    }

    [Fact]
    public async Task AnUndeclaredRing_IsRefusedRatherThanWritten()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));

        await Assert.ThrowsAsync<UsageException>(
            () => store.UpsertAsync(Profile("home-1", "one@contoso.com") with { Ring = (PowerPlatformApiRing)987 }, default));
    }

    [Fact]
    public async Task MissingFile_IsSignedOut_NotCorrupt()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));

        Assert.Empty(await store.GetProfilesAsync(default));
        Assert.Null(await store.GetActiveProfileAsync(default));
    }

    [Fact]
    public async Task Upsert_AddsProfile_AndMakesItActive()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var store = new FileAuthProfileStore(path);

        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);

        var profiles = await store.GetProfilesAsync(default);
        var single = Assert.Single(profiles);
        Assert.Equal("home-1", single.AccountKey);
        Assert.True(single.IsActive);

        var active = await store.GetActiveProfileAsync(default);
        Assert.NotNull(active);
        Assert.Equal("home-1", active!.AccountKey);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Upsert_ReplacesMatchingKey_DoesNotDuplicate()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));

        await store.UpsertAsync(Profile("home-1", "old@contoso.com"), default);
        await store.UpsertAsync(Profile("home-1", "new@contoso.com"), default);

        var single = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal("new@contoso.com", single.Username);
    }

    [Fact]
    public async Task Upsert_SecondProfile_BecomesActive()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));

        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);
        await store.UpsertAsync(Profile("home-2", "b@contoso.com"), default);

        var active = await store.GetActiveProfileAsync(default);
        Assert.Equal("home-2", active!.AccountKey);

        var profiles = await store.GetProfilesAsync(default);
        Assert.Equal(2, profiles.Count);
        Assert.False(profiles.Single(p => p.AccountKey == "home-1").IsActive);
    }

    [Fact]
    public async Task SetActive_ToKnownKey_SwitchesActive()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));

        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);
        await store.UpsertAsync(Profile("home-2", "b@contoso.com"), default);

        await store.SetActiveAsync("home-1", default);

        var active = await store.GetActiveProfileAsync(default);
        Assert.Equal("home-1", active!.AccountKey);
    }

    [Fact]
    public async Task SetActive_ToUnknownKey_IsUsageError()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);

        var ex = await Assert.ThrowsAsync<AuthenticationException>(
            () => store.SetActiveAsync("no-such-key", default));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public async Task Write_IsAtomic_NoTempFilesLeftBehind()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));

        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);

        var leftovers = Directory.GetFiles(dir.Path, "*.tmp-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Persisted_File_ContainsNoTokenShapedFields()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var store = new FileAuthProfileStore(path);
        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clear_DeletesStore()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var store = new FileAuthProfileStore(path);
        await store.UpsertAsync(Profile("home-1", "a@contoso.com"), default);

        await store.ClearAsync(default);

        Assert.False(File.Exists(path));
        Assert.Empty(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task OldProfileWithoutTelemetryMetadataRemainsReadableAndUnknown()
    {
        using var dir = new TempDir();
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(
            Profile("home-1", "a@contoso.com"),
            default);

        AuthProfile active =
            Assert.IsType<AuthProfile>(
                await store.GetActiveProfileAsync(default));

        Assert.Equal("home-1", active.AccountKey);
        Assert.Null(
            await store.GetActiveTelemetryContextAsync(default));
    }

    [Fact]
    public async Task SelectedProfileReturnsVerifiedPairedMetadata()
    {
        using var dir = new TempDir();
        Guid tenantId =
            new("11111111-1111-1111-1111-111111111111");
        Guid principalObjectId =
            new("22222222-2222-2222-2222-222222222222");
        var store = new FileAuthProfileStore(dir.File("profiles.json"));
        await store.UpsertAsync(
            Profile(
                "home-1",
                "a@contoso.com",
                authenticatedTenant: tenantId,
                principalObjectId: principalObjectId),
            default);

        AuthProfileTelemetryContext context =
            Assert.IsType<AuthProfileTelemetryContext>(
                await store.GetActiveTelemetryContextAsync(default));

        Assert.Equal("home-1", context.AccountKey);
        Assert.Equal(tenantId, context.TenantId);
        Assert.Equal(principalObjectId, context.PrincipalObjectId);
    }

    [Fact]
    public async Task MalformedOptionalMetadataIsUnknownWithoutBreakingProfileReads()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "activeAccountKey": "home-1",
              "profiles": [
                {
                  "accountKey": "home-1",
                  "username": "a@contoso.com",
                  "cloud": "Public",
                  "authenticatedTenantId": "not-a-guid",
                  "principalObjectId": "also-not-a-guid"
                }
              ]
            }
            """);
        var store = new FileAuthProfileStore(path);

        Assert.Equal(
            "home-1",
            (await store.GetActiveProfileAsync(default))?.AccountKey);
        Assert.Null(
            await store.GetActiveTelemetryContextAsync(default));
    }

    public static TheoryData<string, string> OptionalMetadataTokenShapes =>
        new()
        {
            { "authenticatedTenantId", "123" },
            { "authenticatedTenantId", "true" },
            { "authenticatedTenantId", """{ "value": "ignored" }""" },
            { "authenticatedTenantId", """{ "value": 1, "value": 2 }""" },
            { "authenticatedTenantId", """[{ "value": 1, "value": 2 }]""" },
            { "authenticatedTenantId", """["ignored"]""" },
            { "authenticatedTenantId", "null" },
            { "authenticatedTenantId", "\"not-a-guid\"" },
            { "authenticatedTenantId", "\"\\uD800\"" },
            { "authenticatedTenantId", "\"\\uDC00\"" },
            { "principalObjectId", "123" },
            { "principalObjectId", "true" },
            { "principalObjectId", """{ "value": "ignored" }""" },
            { "principalObjectId", """{ "value": 1, "value": 2 }""" },
            { "principalObjectId", """[{ "value": 1, "value": 2 }]""" },
            { "principalObjectId", """["ignored"]""" },
            { "principalObjectId", "null" },
            { "principalObjectId", "\"not-a-guid\"" },
            { "principalObjectId", "\"\\uD800\"" },
            { "principalObjectId", "\"\\uDC00\"" },
        };

    [Theory]
    [MemberData(nameof(OptionalMetadataTokenShapes))]
    public async Task OptionalMetadataTokenShapeDoesNotBreakProductProfile(
        string propertyName,
        string jsonValue)
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        string pairedProperty = propertyName == "authenticatedTenantId"
            ? """
              "principalObjectId": "22222222-2222-2222-2222-222222222222",
              """
            : """
              "authenticatedTenantId": "11111111-1111-1111-1111-111111111111",
              """;
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "activeAccountKey": "home-1",
              "profiles": [
                {
                  "accountKey": "home-1",
                  "username": "a@contoso.com",
                  "cloud": "Public",
                  {{pairedProperty}}
                  "{{propertyName}}": {{jsonValue}}
                }
              ]
            }
            """);
        var store = new FileAuthProfileStore(path);

        AuthProfile active = Assert.IsType<AuthProfile>(
            await store.GetActiveProfileAsync(default));
        Assert.Equal("home-1", active.AccountKey);
        Assert.Single(await store.GetProfilesAsync(default));
        AuthProfileTelemetryContext? context =
            await store.GetActiveTelemetryContextAsync(default);
        if (propertyName == "authenticatedTenantId")
        {
            Assert.Null(context);
        }
        else
        {
            AuthProfileTelemetryContext tenantOnly =
                Assert.IsType<AuthProfileTelemetryContext>(context);
            Assert.Equal(
                new Guid("11111111-1111-1111-1111-111111111111"),
                tenantOnly.TenantId);
            Assert.Null(tenantOnly.PrincipalObjectId);
        }

        await store.SetActiveAsync("home-1", default);
        AuthProfile roundTripped = Assert.IsType<AuthProfile>(
            await store.GetActiveProfileAsync(default));
        Assert.Equal("home-1", roundTripped.AccountKey);
        Assert.Single(await store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task Read_InvalidJson_IsTypedStateError()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");
        var store = new FileAuthProfileStore(path);

        var ex = await Assert.ThrowsAsync<AuthProfileStateException>(() => store.GetProfilesAsync(default));
        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
        Assert.Contains("msagent auth clear", ex.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_UnsupportedSchema_IsTypedStateError()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 99, "activeAccountKey": null, "profiles": [] }""");
        var store = new FileAuthProfileStore(path);

        await Assert.ThrowsAsync<AuthProfileStateException>(() => store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task Read_InconsistentActivePointer_IsTypedStateError()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "activeAccountKey": "ghost", "profiles": [] }""");
        var store = new FileAuthProfileStore(path);

        await Assert.ThrowsAsync<AuthProfileStateException>(() => store.GetActiveProfileAsync(default));
    }

    [Fact]
    public async Task Read_DuplicateAccountKeys_IsTypedStateError()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "activeAccountKey": "home-1",
              "profiles": [
                { "accountKey": "home-1", "username": "a@contoso.com", "cloud": "Public" },
                { "accountKey": "home-1", "username": "b@contoso.com", "cloud": "Public" }
              ]
            }
            """);
        var store = new FileAuthProfileStore(path);

        await Assert.ThrowsAsync<AuthProfileStateException>(() => store.GetProfilesAsync(default));
    }

    [Fact]
    public async Task RoundTrip_PreservesMetadata_AndWritesCloudAsString()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var store = new FileAuthProfileStore(path);
        await store.UpsertAsync(Profile("home-1", "a@contoso.com", tenant: "tenant-9"), default);

        var reread = Assert.Single(await store.GetProfilesAsync(default));
        Assert.Equal("tenant-9", reread.TenantId);
        Assert.Equal(CloudInstance.Public, reread.Cloud);
        Assert.Equal("broker", reread.FlowType);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Public\"", json);
    }
}

public sealed class AuthProfileSelectionTests
{
    private static AuthProfile P(string key, string user) => new() { AccountKey = key, Username = user };
    private static AuthAccount A(string homeId, string user) => new() { HomeAccountId = homeId, Username = user };

    [Fact]
    public void ResolveAccountKey_ExactHomeId_Wins()
    {
        var profiles = new[] { P("home-1", "a@contoso.com"), P("home-2", "b@contoso.com") };
        Assert.Equal("home-2", AuthProfileSelection.ResolveAccountKey(profiles, "home-2"));
    }

    [Fact]
    public void ResolveAccountKey_ByUsername_CaseInsensitive()
    {
        var profiles = new[] { P("home-1", "Alice@Contoso.com") };
        Assert.Equal("home-1", AuthProfileSelection.ResolveAccountKey(profiles, "alice@contoso.com"));
    }

    [Fact]
    public void ResolveAccountKey_AmbiguousUsername_IsUsageError()
    {
        var profiles = new[] { P("home-1", "dup@contoso.com"), P("home-2", "dup@contoso.com") };

        var ex = Assert.Throws<AuthenticationException>(
            () => AuthProfileSelection.ResolveAccountKey(profiles, "dup@contoso.com"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public void ResolveAccountKey_NoMatch_IsUsageError()
    {
        var profiles = new[] { P("home-1", "a@contoso.com") };

        var ex = Assert.Throws<AuthenticationException>(
            () => AuthProfileSelection.ResolveAccountKey(profiles, "missing@contoso.com"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Theory]
    [InlineData("home-1", true)]
    [InlineData("a@contoso.com", true)]
    [InlineData("A@CONTOSO.COM", true)]
    [InlineData("HOME-1", false)]
    [InlineData("b@contoso.com", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void NamesAccount_MatchesTheExactHomeId_OrTheUsernameInAnyCase(string? selector, bool expected) =>
        Assert.Equal(expected, AuthProfileSelection.NamesAccount(selector, "home-1", "a@contoso.com"));

    [Fact]
    public void NamesAccount_NeverMatchesAnAccountWithoutAnIdentity() =>
        Assert.False(AuthProfileSelection.NamesAccount("a@contoso.com", null, null));

    [Fact]
    public void Reconcile_NoActive_IsNone()
    {
        var result = AuthProfileSelection.Reconcile(null, Array.Empty<AuthAccount>());
        Assert.Equal(ActiveProfileState.None, result.State);
    }

    [Fact]
    public void Reconcile_ActivePresentInCache_IsActive()
    {
        var active = P("home-1", "a@contoso.com");
        var result = AuthProfileSelection.Reconcile(active, new[] { A("home-1", "a@contoso.com") });
        Assert.Equal(ActiveProfileState.Active, result.State);
    }

    [Fact]
    public void Reconcile_ActiveMissingFromCache_IsStale()
    {
        var active = P("home-1", "a@contoso.com");
        var result = AuthProfileSelection.Reconcile(active, new[] { A("home-2", "b@contoso.com") });
        Assert.Equal(ActiveProfileState.Stale, result.State);
        Assert.Same(active, result.Profile);
    }
}

public sealed class TokenCacheProtectionTests
{
    private static AgentHostCacheDescriptor Descriptor(string configDir) =>
        AgentHostCacheDescriptor.Create(configDir);

    [Fact]
    public void Descriptor_UsesIsolatedAgentHostPathAndNames()
    {
        using var dir = new TempDir();
        var descriptor = Descriptor(dir.Path);

        Assert.Equal(
            Path.Combine(dir.Path, "cache", "auth-csharp"),
            descriptor.CacheDirectory);
        Assert.Contains("agenthost", descriptor.CacheFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agenthost", descriptor.MacKeychainService, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agenthost", descriptor.LinuxKeyringSchema, StringComparison.OrdinalIgnoreCase);
        // Isolation from PAC: never PAC's service/schema names.
        Assert.DoesNotContain("powerplatform", descriptor.MacKeychainService, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powerapps", descriptor.LinuxKeyringSchema, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("windows", CacheProtectionKind.WindowsDpapi)]
    [InlineData("mac", CacheProtectionKind.MacKeychain)]
    [InlineData("linux", CacheProtectionKind.LinuxKeyring)]
    public void GetPlatformProtectionKind_PerPlatform(string platform, CacheProtectionKind expected)
    {
        var info = platform switch
        {
            "windows" => FakePlatformInfo.Windows(),
            "mac" => FakePlatformInfo.MacOs(),
            _ => FakePlatformInfo.Linux(),
        };
        var cache = new AgentHostTokenCache(info, new FakeSecureTokenCacheProbe(protectedAvailable: true));

        Assert.Equal(expected, cache.GetPlatformProtectionKind());
    }

    [Fact]
    public void GetPlatformProtectionKind_UnknownPlatform_Throws()
    {
        var cache = new AgentHostTokenCache(FakePlatformInfo.Unknown(), new FakeSecureTokenCacheProbe(true));
        Assert.Throws<CacheProtectionUnavailableException>(() => cache.GetPlatformProtectionKind());
    }

    [Fact]
    public async Task Resolve_ProtectedAvailable_IsProtected()
    {
        using var dir = new TempDir();
        var probe = new FakeSecureTokenCacheProbe(protectedAvailable: true);
        var cache = new AgentHostTokenCache(FakePlatformInfo.Linux(), probe);

        var resolved = await cache.ResolveProtectionAsync(Descriptor(dir.Path), allowUnprotectedCache: false, default);

        Assert.True(resolved.IsProtected);
        Assert.Equal(CacheProtectionKind.LinuxKeyring, resolved.Kind);
        Assert.Equal(1, probe.CallCount);
        Assert.NotNull(probe.LastDescriptor);
    }

    [Fact]
    public async Task Resolve_Unavailable_NoOptIn_ThrowsAndNeverFallsBack()
    {
        using var dir = new TempDir();
        var cache = new AgentHostTokenCache(FakePlatformInfo.Linux(), new FakeSecureTokenCacheProbe(protectedAvailable: false));

        var ex = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(
            () => cache.ResolveProtectionAsync(Descriptor(dir.Path), allowUnprotectedCache: false, default));

        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
        Assert.Contains(AgentHostTokenCache.AllowUnprotectedCacheSettingName, ex.Remediation);
    }

    [Fact]
    public async Task Resolve_Unavailable_WithOptIn_UsesUnprotectedFile()
    {
        using var dir = new TempDir();
        var cache = new AgentHostTokenCache(FakePlatformInfo.Linux(), new FakeSecureTokenCacheProbe(protectedAvailable: false));

        var resolved = await cache.ResolveProtectionAsync(Descriptor(dir.Path), allowUnprotectedCache: true, default);

        Assert.False(resolved.IsProtected);
        Assert.Equal(CacheProtectionKind.UnprotectedFile, resolved.Kind);
    }
}

public sealed class PublicClientConfiguratorTests
{
    private static readonly AgentHostClientAppConfiguration ClientApp =
        AgentHostClientAppConfiguration.Create("11111111-2222-3333-4444-555555555555", FakePlatformInfo.Windows());

    private static readonly AuthorityInfo Authority =
        AgentHostAuthorityResolver.Resolve(CloudInstance.Public);

    [Fact]
    public void Windows_WithBroker_UsesWamRedirect_DisablesInstanceDiscovery_AndNeedsParentWindow()
    {
        var configurator = new MsalPublicClientConfigurator(FakePlatformInfo.Windows());
        var config = configurator.Create(ClientApp, Authority, requestBroker: true);

        Assert.True(config.UseBroker);
        Assert.False(config.ListBrokerAccounts);
        Assert.True(config.DisableInstanceDiscovery);
        Assert.True(config.RequiresParentWindow);
        Assert.False(config.ValidateAuthority);
        Assert.Equal(ClientApp.ClientId, config.ClientId);
        Assert.Equal(
            new Uri($"ms-appx-web://microsoft.aad.brokerplugin/{ClientApp.ClientId:D}"),
            config.RedirectUri);
        Assert.Equal(Authority.AuthorityUrl, config.AuthorityUrl);
    }

    [Theory]
    [InlineData("macos", "msauth.com.msauth.unsignedapp://auth")]
    [InlineData("linux", "https://login.microsoftonline.com/common/oauth2/nativeclient")]
    public void MacOsAndLinux_WithBroker_UseTheirOwnReplyUrl_AndKeepInstanceDiscovery(string platform, string replyUrl)
    {
        var config = new MsalPublicClientConfigurator(FakePlatformInfo.Named(platform)).Create(ClientApp, Authority, requestBroker: true);

        Assert.True(config.UseBroker);
        Assert.False(config.DisableInstanceDiscovery);
        Assert.False(config.RequiresParentWindow);
        Assert.Equal(replyUrl, config.RedirectUri.OriginalString);
    }

    [Theory]
    [InlineData("windows", false, true)]
    [InlineData("macos", false, false)]
    [InlineData("linux", false, false)]
    [InlineData("unknown", true, false)]
    public void NoBrokerRequestedOrSupported_NeverUsesBroker(string platform, bool requestBroker, bool requiresParentWindow)
    {
        var config = new MsalPublicClientConfigurator(FakePlatformInfo.Named(platform)).Create(ClientApp, Authority, requestBroker);

        Assert.False(config.UseBroker);
        Assert.False(config.DisableInstanceDiscovery);
        Assert.Equal(requiresParentWindow, config.RequiresParentWindow);
        Assert.Equal(ClientApp.RedirectUri, config.RedirectUri);
    }

    [Theory]
    [InlineData("windows", true, true, true)]
    [InlineData("macos", true, true, true)]
    [InlineData("linux", true, true, true)]
    [InlineData("linux", true, false, false)]
    [InlineData("linux", false, true, false)]
    [InlineData("unknown", true, true, false)]
    public void ListsBrokerAccounts_OnlyWhenASupportedBrokerIsRequestedAndAskedToListThem(string platform, bool requestBroker, bool listBrokerAccounts, bool expected)
    {
        var config = new MsalPublicClientConfigurator(FakePlatformInfo.Named(platform)).Create(ClientApp, Authority, requestBroker, listBrokerAccounts);

        Assert.Equal(expected, config.ListBrokerAccounts);
    }
}

public sealed class SilentTokenTenantRoutingTests
{
    [Fact]
    public void BrokerTargetTenant_PinsClientAuthority_WithoutRequestOverride()
    {
        const string activeTenantId = "11111111-1111-1111-1111-111111111111";
        const string targetTenantId = "22222222-2222-2222-2222-222222222222";

        var routing = SilentTokenTenantRouting.Resolve(
            useBroker: true,
            activeTenantId,
            targetTenantId);

        Assert.Equal(Guid.Parse(targetTenantId), routing.ClientAuthorityTenantId);
        Assert.Null(routing.RequestTenantId);
    }

    [Fact]
    public void BrokerWithoutTargetTenant_PinsActiveProfileAuthority()
    {
        const string activeTenantId = "11111111-1111-1111-1111-111111111111";

        var routing = SilentTokenTenantRouting.Resolve(
            useBroker: true,
            activeTenantId,
            targetTenantId: null);

        Assert.Equal(Guid.Parse(activeTenantId), routing.ClientAuthorityTenantId);
        Assert.Null(routing.RequestTenantId);
    }

    [Fact]
    public void NonBrokerTargetTenant_UsesRequestOverride()
    {
        const string activeTenantId = "11111111-1111-1111-1111-111111111111";
        const string targetTenantId = "22222222-2222-2222-2222-222222222222";

        var routing = SilentTokenTenantRouting.Resolve(
            useBroker: false,
            activeTenantId,
            targetTenantId);

        Assert.Null(routing.ClientAuthorityTenantId);
        Assert.Equal(targetTenantId, routing.RequestTenantId);
    }
}

public sealed class ConsentAccountSelectionTests
{
    private sealed record CachedAccount(string AccountKey);

    [Fact]
    public void Select_ExactActiveAccount_Wins()
    {
        var accounts = new[]
        {
            new CachedAccount("home-1"),
            new CachedAccount("home-2"),
        };

        var selected = ConsentAccountSelection.Select(
            accounts,
            activeAccountKey: "home-2",
            account => account.AccountKey);

        Assert.Same(accounts[1], selected);
    }

    [Fact]
    public void Select_MissingActiveAccount_DoesNotUseAnotherCachedAccount()
    {
        var accounts = new[]
        {
            new CachedAccount("home-1"),
            new CachedAccount("home-2"),
        };

        var selected = ConsentAccountSelection.Select(
            accounts,
            activeAccountKey: "home-3",
            account => account.AccountKey);

        Assert.Null(selected);
    }

    [Fact]
    public void EnsureReturnedAccount_RejectsAccountDifferentFromActiveProfile()
    {
        var error = Assert.Throws<ActiveAccountMismatchException>(() =>
            ConsentAccountSelection.EnsureReturnedAccount(
                activeAccountKey: "home-1",
                returnedAccountKey: "home-2"));

        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        Assert.Contains("does not match the active AgentHost account", error.Message);
    }

    [Fact]
    public void EnsureReturnedAccount_AcceptsExactActiveProfileAccount()
    {
        ConsentAccountSelection.EnsureReturnedAccount(
            activeAccountKey: "home-1",
            returnedAccountKey: "home-1");
    }
}
