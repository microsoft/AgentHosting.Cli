using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileCredentialTests
{
    [Fact]
    public async Task RegisteredTargetsRemainValidAcrossCredentialAndProfilePersistence()
    {
        foreach (var token in PowerPlatformApiTargetSelection.AcceptedTokens)
        {
            using var h = new Harness();
            Assert.True(PowerPlatformApiTargetSelection.TryResolve(token, out var target, out _));
            var profile = await h.Provider.AuthenticateAsync(new() { Target = target, Tenant = Tenant }, default);
            try
            {
                var store = new FileAuthProfileStore(Path.Combine(h.Directory, "profiles.json"));
                var created = await store.CreateAsync(profile with { ConnectionUrl = null }, default);
                var persisted = Assert.Single(await new FileAuthProfileStore(Path.Combine(h.Directory, "profiles.json")).GetProfilesAsync(default));
                Assert.Equal(created.ProfileId, persisted.ProfileId);
                Assert.Equal(target.Cloud, persisted.Cloud);
                Assert.Equal(target.Ring, persisted.Ring);
                Assert.Equal(target.GlobalEndpoint.AbsoluteUri.TrimEnd('/'), persisted.ConnectionUrl);
                var request = Assert.Single(h.Acquisition.Requests);
                Assert.Equal(target.Cloud, request.Authority.Cloud);
                Assert.Equal(Guid.Parse(Tenant), request.Authority.TenantId);
                Assert.Equal([new PpapiScopeBuilder().DefaultScope(target.Audience)], request.Scopes);
            }
            finally { await h.Provider.RemoveOwnedCredentialAsync(profile, default); }
        }
    }

    [Theory]
    [InlineData(CloudInstance.UsGov, PowerPlatformApiRing.Preprod)]
    [InlineData(CloudInstance.China, PowerPlatformApiRing.Test)]
    [InlineData(CloudInstance.UsNat, PowerPlatformApiRing.Preprod)]
    [InlineData(CloudInstance.UsSec, PowerPlatformApiRing.Test)]
    [InlineData((CloudInstance)99, PowerPlatformApiRing.Production)]
    [InlineData(CloudInstance.Public, (PowerPlatformApiRing)99)]
    public async Task InvalidCloudTargetIsRejectedBeforeOwnedStorageOrAcquisition(CloudInstance cloud, PowerPlatformApiRing ring)
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<UsageException>(() => h.Provider.AuthenticateAsync(new()
        {
            Target = PowerPlatformApiTargetConfig.Public with { Cloud = cloud, Ring = ring },
        }, default));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Acquisition.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    [Fact]
    public async Task ForgedCloudCoordinatesAreRejectedBeforeOwnedStorageOrAcquisition()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<UsageException>(() => h.Provider.AuthenticateAsync(new()
        {
            Target = PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri("https://attacker.example") },
        }, default));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Acquisition.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    [Theory]
    [InlineData("https://user:synthetic@api.powerplatform.com/")]
    [InlineData("https://api.powerplatform.com/#fragment")]
    [InlineData("https://api.powerplatform.com/#")]
    public async Task EndpointComponentsAreRejectedBeforeOwnedStorageOrAcquisition(string endpoint)
    {
        using var h = new Harness();
        AuthProfile? unexpected = null;
        try
        {
            await Assert.ThrowsAsync<UsageException>(async () => unexpected = await h.Provider.AuthenticateAsync(new()
            {
                Target = PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri(endpoint) },
            }, default));
            Assert.Equal(0, h.Backend.Calls);
            Assert.Empty(h.Acquisition.Requests);
            Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
        }
        finally
        {
            if (unexpected is not null) await h.Provider.RemoveOwnedCredentialAsync(unexpected, default);
        }
    }

    public static IEnumerable<object[]> InvalidProfileTargetCases()
    {
        foreach (var consent in new[] { false, true })
        foreach (var edit in new[]
        {
            "authority-cloud", "authority-userinfo", "authority-fragment", "authority-query", "authority-port",
            "authority-relative", "connection-ring", "connection-userinfo", "connection-fragment",
            "connection-query", "connection-port", "connection-relative", "cloud", "ring",
            "undefined-cloud", "undefined-ring", "sovereign-ring",
        })
            yield return [consent, edit];
    }

    [Theory]
    [MemberData(nameof(InvalidProfileTargetCases))]
    public async Task ProfileTargetIsRejectedBeforeRenewalOrConsentStorageAndAcquisition(bool consent, string edit)
    {
        using var h = new Harness();
        var profile = PublicProfile();
        var edited = edit switch
        {
            "authority-cloud" => profile with { Authority = "https://login.partner.microsoftonline.cn/" + Tenant },
            "authority-userinfo" => profile with { Authority = "https://user:synthetic@login.microsoftonline.com/" + Tenant },
            "authority-fragment" => profile with { Authority = profile.Authority + "#fragment" },
            "authority-query" => profile with { Authority = profile.Authority + "?query=value" },
            "authority-port" => profile with { Authority = "https://login.microsoftonline.com:8443/" + Tenant },
            "authority-relative" => profile with { Authority = "relative" },
            "connection-ring" => profile with { ConnectionUrl = PowerPlatformApiTargetConfig.Preprod.GlobalEndpoint.AbsoluteUri },
            "connection-userinfo" => profile with { ConnectionUrl = "https://user:synthetic@api.powerplatform.com/" },
            "connection-fragment" => profile with { ConnectionUrl = profile.ConnectionUrl + "#fragment" },
            "connection-query" => profile with { ConnectionUrl = profile.ConnectionUrl + "?query=value" },
            "connection-port" => profile with { ConnectionUrl = "https://api.powerplatform.com:8443/" },
            "connection-relative" => profile with { ConnectionUrl = "relative" },
            "cloud" => profile with { Cloud = CloudInstance.China },
            "ring" => profile with { Ring = PowerPlatformApiRing.Test },
            "undefined-cloud" => profile with { Cloud = (CloudInstance)99 },
            "undefined-ring" => profile with { Ring = (PowerPlatformApiRing)99 },
            _ => profile with
            {
                Cloud = CloudInstance.UsNat, Ring = PowerPlatformApiRing.Test,
                Authority = AgentHostAuthorityResolver.Resolve(CloudInstance.UsNat).AuthorityUrl.AbsoluteUri,
                ConnectionUrl = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.UsNat).GlobalEndpoint.AbsoluteUri,
            },
        };
        await Assert.ThrowsAsync<AuthProfileStateException>(() => consent
            ? h.Provider.AcquireConsentAsync(edited, Guid.Parse(App), "http://localhost", ["delegated"], Tenant, default)
            : h.Provider.AcquireAsync(edited, ["delegated"], null, default));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Equal(0, h.Acquisition.Validations);
        Assert.Empty(h.Acquisition.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    public static IEnumerable<object[]> InvalidAuthorityPathCases()
    {
        foreach (var operation in new[] { "renewal", "consent", "reader" })
        foreach (var path in new[]
        {
            "/not/a/tenant", "/", "/organizations/extra", "//organizations", "/organizations//v2.0",
            "/organizations/v2.0/extra", "/not-a-tenant", "/contoso.example%2Fextra",
        })
            yield return [operation, path];
    }

    [Theory]
    [MemberData(nameof(InvalidAuthorityPathCases))]
    public async Task InvalidAuthorityPathIsRejectedBeforeAnyProfileIo(string operation, string path)
    {
        using var h = new Harness();
        var profile = PublicProfile() with { Authority = "https://login.microsoftonline.com" + path };
        h.Acquisition.Next = new() { AccessToken = "target-tenant-token", HomeAccountId = Home, TenantId = ObjectId };
        await Assert.ThrowsAsync<AuthProfileStateException>(() => operation switch
        {
            "renewal" => h.Provider.AcquireAsync(profile, ["delegated"], ObjectId, default),
            "consent" => h.Provider.AcquireConsentAsync(profile, Guid.Parse(App), "http://localhost", ["delegated"], ObjectId, default),
            _ => h.Provider.GetAccountsAsync(profile, default),
        });
        Assert.Equal(0, h.Backend.Calls);
        Assert.Equal(0, h.Acquisition.Validations);
        Assert.Empty(h.Acquisition.Requests);
        Assert.Empty(h.Acquisition.Reads);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, " ")]
    [InlineData(true, " ")]
    public async Task LegacyBlankTargetMetadataDoesNotPreventRenewalOrConsent(bool consent, string? metadata)
    {
        using var h = new Harness();
        var profile = PublicProfile() with { Authority = metadata, ConnectionUrl = metadata };
        var token = consent
            ? await h.Provider.AcquireConsentAsync(profile, Guid.Parse(App), "http://localhost", ["delegated"], Tenant, default)
            : await h.Provider.AcquireAsync(profile, ["delegated"], null, default);
        Assert.Equal(h.Acquisition.Next.AccessToken, token.AccessToken);
        Assert.Equal(Guid.Parse(Tenant), Assert.Single(h.Acquisition.Requests).Authority.TenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task LegacyBlankTargetMetadataDoesNotPreventCachedReading(string? metadata)
    {
        using var h = new Harness();
        var profile = PublicProfile() with { Authority = metadata, ConnectionUrl = metadata };
        Assert.Single(await h.Provider.GetAccountsAsync(profile, default));
        Assert.Equal(Guid.Parse(Tenant), Assert.Single(h.Acquisition.Reads).Authority.TenantId);
        Assert.Empty(h.Acquisition.Requests);
    }

    [Theory]
    [InlineData(false, "organizations")]
    [InlineData(true, "organizations")]
    [InlineData(false, Tenant)]
    [InlineData(true, Tenant)]
    [InlineData(false, "common")]
    [InlineData(true, "common")]
    [InlineData(false, "contoso.onmicrosoft.com")]
    [InlineData(true, "contoso.onmicrosoft.com")]
    [InlineData(false, "contoso.example/v2.0/")]
    [InlineData(true, "contoso.example/v2.0/")]
    [InlineData(false, "organizations/v2.0")]
    [InlineData(true, "organizations/v2.0")]
    [InlineData(false, Tenant + "/v2.0/")]
    [InlineData(true, Tenant + "/v2.0/")]
    public async Task StoredAuthorityIsValidatedIndependentlyOfTargetTenantOverride(bool consent, string authorityTenant)
    {
        using var h = new Harness();
        var profile = PublicProfile() with { Authority = "https://login.microsoftonline.com/" + authorityTenant };
        h.Acquisition.Next = new() { AccessToken = "target-tenant-token", HomeAccountId = Home, TenantId = ObjectId };
        var token = consent
            ? await h.Provider.AcquireConsentAsync(profile, Guid.Parse(App), "http://localhost", ["delegated"], ObjectId, default)
            : await h.Provider.AcquireAsync(profile, ["delegated"], ObjectId, default);
        var invocation = Assert.Single(h.Acquisition.Requests);
        Assert.Equal(CloudInstance.Public, invocation.Authority.Cloud);
        Assert.Equal(Guid.Parse(ObjectId), invocation.Authority.TenantId);
        Assert.Equal(ObjectId, token.TenantId);
        Assert.Equal("https://login.microsoftonline.com/" + authorityTenant, profile.Authority);
        Assert.Equal(Tenant, profile.TenantId);
    }

    [Fact]
    public async Task CachedAccountTargetIsRejectedBeforeStorage()
    {
        using var h = new Harness();
        var profile = PublicProfile() with { Authority = "https://login.partner.microsoftonline.cn/" + Tenant };
        await Assert.ThrowsAsync<AuthProfileStateException>(() => h.Provider.GetAccountsAsync(profile, default));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Acquisition.Reads);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredProfileTargetsCanRenewAndConsent(bool consent)
    {
        foreach (var token in PowerPlatformApiTargetSelection.AcceptedTokens)
        {
            using var h = new Harness();
            Assert.True(PowerPlatformApiTargetSelection.TryResolve(token, out var target, out _));
            var profile = PublicProfile() with
            {
                Cloud = target.Cloud, Ring = target.Ring, ConnectionUrl = target.GlobalEndpoint.AbsoluteUri,
                Authority = AgentHostAuthorityResolver.Resolve(target.Cloud, Guid.Parse(Tenant)).AuthorityUrl.AbsoluteUri,
            };
            if (consent)
                await h.Provider.AcquireConsentAsync(profile, Guid.Parse(App), "http://localhost", ["delegated"], Tenant, default);
            else
                await h.Provider.AcquireAsync(profile, ["delegated"], null, default);
            var invocation = Assert.Single(h.Acquisition.Requests);
            Assert.Equal(target.Cloud, invocation.Authority.Cloud);
            Assert.Equal(target.Ring, invocation.Ring);
            Assert.Equal(target.GlobalEndpoint.AbsoluteUri, invocation.ConnectionUrl);
        }
    }

    private static AuthProfile PublicProfile() => new()
    {
        AccountKey = Home, HomeAccountId = Home, Username = "alice", ClientId = App, TenantId = Tenant,
        CredentialMethod = AuthCredentialMethod.Browser, CredentialReference = OwnedProfileReferences.New(false).Value,
        Authority = "https://login.microsoftonline.com/" + Tenant,
        ConnectionUrl = PowerPlatformApiTargetConfig.Public.GlobalEndpoint.AbsoluteUri,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledBusyCompletionAutomaticallyReleasesLeaseWhenUseExits(bool acquisitionFails)
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        h.Profiles.Items.Add(stage);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Acquisition.BeforeAcquire = _ => { entered.TrySetResult(); return release.Task; };
        if (acquisitionFails) h.Acquisition.Failure = new AuthenticationException("synthetic-acquisition-failure");
        var inUse = h.Provider.AcquireAsync(stage, ["https://service.test/.default"], null, default);
        await entered.Task;
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        var other = h.NewProvider();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Provider.CompleteCreationAsync(stage, ct.Token));
            Assert.Equal(true, error.Data["msagent.metadata-committed"]);
            await Assert.ThrowsAnyAsync<AgentHostingException>(() => other.GetAccountsAsync(stage, default));
            release.TrySetResult();
            if (acquisitionFails) await Assert.ThrowsAsync<AuthenticationException>(() => inUse);
            else await inUse;
            Assert.Single(await other.GetAccountsAsync(stage, default));
        }
        finally
        {
            release.TrySetResult();
            try { await inUse; } catch (AuthenticationException) { }
            h.Profiles.Items.Clear();
            await h.Provider.RemoveOwnedCredentialAsync(stage, default);
        }
    }

    [Fact]
    public async Task AlreadyCanceledClearHonorsCancellationWithoutInventoryOrNativeAccess()
    {
        using var h = new Harness();
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Provider.ClearOwnedCredentialsAsync(ct.Token));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(Directory.GetFiles(h.Directory, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("read\0write")]
    [InlineData("read\twrite")]
    [InlineData("https://service.test/read write")]
    [InlineData("https://service.test/read\r\nwrite")]
    public async Task InvalidScopeMemberFailsBeforeAnyOwnedStorageOrAcquisition(string invalid)
    {
        using var h = new Harness();
        AuthProfile? stage = null;
        var error = await Record.ExceptionAsync(async () => stage = await h.Provider.AuthenticateAsync(
            new() { Scopes = ["https://service.test/read", invalid] }, default));
        if (stage is not null) await h.Provider.RemoveOwnedCredentialAsync(stage, default);
        Assert.IsType<UsageException>(error);
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Acquisition.Requests);
        Assert.Empty(Directory.GetFiles(h.Directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NativeCleanupFailurePreservesProtectionErrorFamilyAndReferenceInventory()
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        await h.Provider.CompleteCreationAsync(stage, default);
        h.Backend.FailClear = true;
        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => h.Provider.ClearOwnedCredentialsAsync(default));
        Assert.Equal(ErrorCodes.Authentication.CacheProtectionUnavailable, error.ErrorCode);
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        h.Backend.FailClear = false;
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
    }

    [Fact]
    public async Task IndeterminateClearCancellationRetainsRepairGuidanceAndMarkerUntilRetry()
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        await h.Provider.CompleteCreationAsync(stage, default);
        using var ct = new CancellationTokenSource();
        h.Backend.AfterClear = ct.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Provider.ClearOwnedCredentialsAsync(ct.Token));
        Assert.True(error.Data.Contains("msagent.owned-cleanup"));
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        h.Backend.AfterClear = null;
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
        Assert.Empty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnreadableOwnershipMetadataCannotCauseNativeDeletion()
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        await h.Provider.CompleteCreationAsync(stage, default);
        h.Profiles.ReadFailure = new IOException("synthetic-unreadable-ownership");
        var calls = h.Backend.Calls;
        await Assert.ThrowsAnyAsync<AgentHostingException>(() => h.Provider.ClearOwnedCredentialsAsync(default));
        Assert.Equal(calls, h.Backend.Calls);
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        h.Profiles.ReadFailure = null;
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOfBusyStageFinalizationIsBoundedAndLeavesRepairableLease(bool committed)
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Acquisition.BeforeAcquire = _ => { entered.TrySetResult(); return release.Task; };
        var usingStage = h.Provider.AcquireAsync(stage, ["https://service.test/.default"], null, default);
        await entered.Task;
        if (committed) h.Profiles.Items.Add(stage);
        using var ct = new CancellationTokenSource(); ct.Cancel();
        var finalize = committed ? h.Provider.CompleteCreationAsync(stage, ct.Token) : h.Provider.RemoveOwnedCredentialAsync(stage, ct.Token);
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalize.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error.Data.Contains(committed ? "msagent.metadata-committed" : "msagent.owned-cleanup"));
            Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        }
        finally
        {
            release.TrySetResult(); await usingStage;
            try { await finalize; } catch (OperationCanceledException) { }
            h.Profiles.Items.Clear();
            await h.Provider.RemoveOwnedCredentialAsync(stage, default);
            await h.NewProvider().ClearOwnedCredentialsAsync(default);
        }
    }

    [Fact]
    public async Task CrossProcessReferenceLeasePreventsCleanupUntilHolderReleases()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        await h.Provider.CompleteCreationAsync(stage, default);
        var reference = OwnedProfileReferences.Parse(stage.CredentialReference).Cache;
        var path = Path.Combine(h.Directory, "owned-profile-references-v1", $"token-cache-{reference.OpaqueId}.lease");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$s = [IO.File]::Open('" + path.Replace("'", "''") + "', 'Open', 'ReadWrite', 'None'); try { [Console]::WriteLine('READY'); [Console]::ReadLine() | Out-Null } finally { $s.Dispose() }" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await Assert.ThrowsAnyAsync<AgentHostingException>(() => h.NewProvider().ClearOwnedCredentialsAsync(default));
            Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
            await process.StandardInput.WriteLineAsync("release"); await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            await h.NewProvider().ClearOwnedCredentialsAsync(default);
            Assert.Empty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    internal const string Tenant = "33333333-3333-3333-3333-333333333333";
    internal const string App = "44444444-4444-4444-4444-444444444444";
    internal const string ObjectId = "55555555-5555-5555-5555-555555555555";
    internal const string Home = "user.home";

    [Theory]
    [InlineData("secret-cert")]
    [InlineData("ropc-device")]
    [InlineData("password-only")]
    [InlineData("username-only")]
    [InlineData("application-only")]
    [InlineData("certificate-password-only")]
    [InlineData("tenant-invalid")]
    [InlineData("resource-scopes")]
    [InlineData("empty-name")]
    [InlineData("long-name")]
    [InlineData("empty-scopes")]
    public async Task InvalidCreateFailsBeforeAnyCredentialStorageOrAcquisition(string mode)
    {
        using var h = new Harness();
        var request = mode switch
        {
            "secret-cert" => new AuthCreateRequest { ClientSecret = "secret", CertificateDiskPath = "cert.pfx" },
            "ropc-device" => new AuthCreateRequest { Username = "alice", Password = "password", DeviceCode = true },
            "password-only" => new AuthCreateRequest { Password = "password" },
            "username-only" => new AuthCreateRequest { Username = "alice" },
            "application-only" => new AuthCreateRequest { ApplicationId = App },
            "certificate-password-only" => new AuthCreateRequest { CertificatePassword = "password" },
            "tenant-invalid" => new AuthCreateRequest { Tenant = "not-a-guid" },
            "resource-scopes" => new AuthCreateRequest { Resource = "https://example.test", Scopes = ["delegated"] },
            "empty-name" => new AuthCreateRequest { Name = " " },
            "long-name" => new AuthCreateRequest { Name = new string('x', 31) },
            _ => new AuthCreateRequest { Scopes = [" "] },
        };
        await Assert.ThrowsAsync<UsageException>(() => h.Provider.AuthenticateAsync(request, default));
        Assert.Empty(h.Acquisition.Requests);
        Assert.Equal(0, h.Backend.Calls);
        Assert.Equal(0, h.Profiles.Writes);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task InteractiveCreateHeadlessRejectsBeforeClientNativeOrCallback(bool device, bool optionAllows)
    {
        using var h = new Harness(optionAllows);
        await Assert.ThrowsAsync<InteractionRequiredException>(() => h.Provider.AuthenticateAsync(
            new AuthCreateRequest { DeviceCode = device, NonInteractive = optionAllows }, default));
        Assert.Empty(h.Acquisition.Requests);
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(h.Directory));
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Browser)]
    [InlineData(AuthCredentialMethod.DeviceCode)]
    [InlineData(AuthCredentialMethod.Password)]
    public async Task UserCreateStagesWithoutStoreActivationAndPreservesExplicitScopes(AuthCredentialMethod method)
    {
        using var h = new Harness();
        var request = new AuthCreateRequest
        {
            Name = "named", Tenant = Tenant, LoginHint = "hint",
            Scopes = ["opaque.delegated", "opaque.delegated", " ", "Second.Scope"],
            DeviceCode = method == AuthCredentialMethod.DeviceCode,
            Username = method == AuthCredentialMethod.Password ? "alice" : null,
            Password = method == AuthCredentialMethod.Password ? "never-persist-password" : null,
        };
        var profile = await h.Provider.AuthenticateAsync(request, default);
        var invocation = Assert.Single(h.Acquisition.Requests);
        Assert.Equal(method, invocation.Method);
        Assert.Collection(invocation.Scopes, s => Assert.Equal("opaque.delegated", s), s => Assert.Equal("Second.Scope", s));
        Assert.Equal(Tenant, invocation.Authority.TenantId?.ToString());
        Assert.Equal(AgentHostClientAppConfiguration.DefaultClientId, invocation.ClientId);
        Assert.Equal(Home, profile.HomeAccountId);
        Assert.Equal(Home, profile.AccountKey);
        Assert.NotNull(profile.CredentialReference);
        Assert.False(profile.IsActive);
        Assert.Equal(0, h.Profiles.Writes);
        Assert.DoesNotContain("never-persist-password", string.Join("", h.Backend.Values.Values.Select(Encoding.UTF8.GetString)));
        await h.Provider.RemoveOwnedCredentialAsync(profile, default);
        Assert.Empty(h.Backend.Values);
    }

    [Fact]
    public async Task SecretStageIsDurableRenewalUsesSnapshotAndOwnerProtectsSharedReference()
    {
        using var h = new Harness(false);
        h.Acquisition.Next = WorkloadToken();
        var stage = await h.Provider.AuthenticateAsync(new AuthCreateRequest
        {
            Tenant = Tenant, ApplicationId = App, ClientSecret = "protected-only", NonInteractive = true,
            Scopes = ["https://service.test/.default"],
            Target = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.China),
        }, default);
        Assert.Equal(AuthPrincipalType.ServicePrincipal, stage.PrincipalType);
        Assert.Null(stage.HomeAccountId);
        Assert.Contains(ObjectId, stage.AccountKey);
        Assert.Equal(0, h.Profiles.Writes);
        var committed = stage with { ProfileId = "preserved-old-id", IsActive = true, Revision = 3 };
        h.Profiles.Items.Add(committed);
        await h.Provider.CompleteCreationAsync(committed, default);
        var replacementProvider = h.NewProvider();
        var token = await replacementProvider.AcquireAsync(committed, ["https://service.test/.default"], null, default);
        Assert.Equal(WorkloadToken().AccessToken, token.AccessToken);
        var renewal = h.Acquisition.Requests.Last();
        Assert.False(renewal.IsCreation);
        Assert.Equal("protected-only", renewal.ClientSecret);
        Assert.Equal(CloudInstance.China, renewal.Authority.Cloud);
        Assert.Equal(Guid.Parse(App), renewal.ClientId);
        await replacementProvider.RemoveOwnedCredentialAsync(committed, default);
        Assert.NotEmpty(h.Backend.Values);
        h.Profiles.Items.Clear();
        await replacementProvider.RemoveOwnedCredentialAsync(committed, default);
        Assert.Empty(h.Backend.Values);
    }

    [Fact]
    public async Task AcquisitionFailureCleansItsStageAndRetainsPrimaryTypedFailure()
    {
        using var h = new Harness();
        h.Acquisition.Failure = new InteractionRequiredException("safe-primary");
        await Assert.ThrowsAsync<InteractionRequiredException>(() => h.Provider.AuthenticateAsync(new AuthCreateRequest { DeviceCode = true }, default));
        Assert.Empty(h.Backend.Values);
        Assert.Empty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        Assert.Equal(0, h.Profiles.Writes);
    }

    [Fact]
    public async Task AcquisitionAndCleanupFailureExposePrimaryRemediationAndUncommittedState()
    {
        using var h = new Harness();
        var primary = new InteractionRequiredException("safe-primary", "Resolve the primary consent requirement.");
        h.Acquisition.Failure = primary;
        h.Backend.FailClear = true;
        var error = await Assert.ThrowsAsync<ProfileCredentialCleanupException>(() =>
            h.Provider.AuthenticateAsync(new() { DeviceCode = true }, default));
        Assert.Equal(primary.ErrorCode, error.ErrorCode);
        Assert.Equal(primary.ExitCode, error.ExitCode);
        Assert.Contains(primary.Remediation!, error.Remediation);
        Assert.Contains("Retry owned credential cleanup", error.Remediation);
        var state = Assert.IsAssignableFrom<IAuthOperationPartialState>(error);
        Assert.False(state.MetadataCommitted);
        Assert.True(state.CleanupIncomplete);
        Assert.Null(state.Profile);
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        Assert.Equal(0, h.Profiles.Writes);
        h.Backend.FailClear = false;
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
    }

    [Fact]
    public async Task RenewalRejectsDifferentHomeAccountWithoutReturningItsToken()
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new AuthCreateRequest(), default);
        h.Acquisition.Next = new ProfileTokenResult { TenantId = Tenant, HomeAccountId = "other.home", AccessToken = "foreign-token" };
        await Assert.ThrowsAsync<AuthenticationException>(() => h.Provider.AcquireAsync(stage, ["delegated"], null, default));
        await h.Provider.RemoveOwnedCredentialAsync(stage, default);
    }

    [Fact]
    public async Task PasswordRenewalNeverReplaysPasswordAndReaderDoesNotAcquire()
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new AuthCreateRequest { Username = "alice", Password = "once-only" }, default);
        h.Profiles.Items.Add(stage);
        await h.Provider.CompleteCreationAsync(stage, default);
        var provider = h.NewProvider();
        await provider.AcquireAsync(stage, ["delegated"], Tenant, default);
        Assert.Null(h.Acquisition.Requests.Last().Password);
        var acquisitions = h.Acquisition.Requests.Count;
        var accounts = await provider.GetAccountsAsync(stage, default);
        Assert.Single(accounts);
        Assert.Equal(acquisitions, h.Acquisition.Requests.Count);
        Assert.Equal(h.Acquisition.Requests[0].CacheReference, h.Acquisition.Reads.Single().CacheReference);
        Assert.Equal(AuthCredentialMethod.Password, h.Acquisition.Reads.Single().Method);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.ClientSecret)]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential)]
    public async Task WorkloadMetadataReaderDoesNotStartCredentialSources(AuthCredentialMethod method)
    {
        using var h = new Harness();
        var accounts = await h.Provider.GetAccountsAsync(new AuthProfile
        {
            AccountKey = "metadata", Username = "", CredentialMethod = method, PrincipalType = AuthPrincipalType.ServicePrincipal,
        }, default);
        Assert.Empty(accounts);
        Assert.Empty(h.Acquisition.Requests);
        Assert.Empty(h.Acquisition.Reads);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Fact]
    public async Task ClearCannotDeleteInFlightStageAcrossProviderInstances()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Acquisition.BeforeAcquire = async _ => { entered.SetResult(); await proceed.Task; };
        var creating = h.Provider.AuthenticateAsync(new AuthCreateRequest(), default);
        await entered.Task;
        await Assert.ThrowsAnyAsync<AuthenticationException>(() => h.NewProvider().ClearOwnedCredentialsAsync(default));
        proceed.SetResult();
        var stage = await creating;
        h.Profiles.Items.Add(stage);
        await h.Provider.CompleteCreationAsync(stage, default);
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReferenceTraversalIsRejectedBeforeNativeOrAcquisition()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<AuthProfileStateException>(() => h.Provider.AcquireAsync(new AuthProfile
        {
            AccountKey = Home, Username = "alice", CredentialReference = "msagent-owned-v1:../escape:-",
        }, ["delegated"], null, default));
        Assert.Empty(h.Acquisition.Requests);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Fact]
    public void RequestAndRuntimeSerializationAndToStringNeverDiscloseSecrets()
    {
        var create = new AuthCreateRequest { Password = "ropc-secret", ClientSecret = "app-secret", CertificatePassword = "pfx-secret" };
        var runtime = new ProfileCredentialRuntimeOptions
        {
            GitHubRequestToken = "github-secret", AzureDevOpsRequestToken = "ado-secret",
            AzureClientSecret = "azure-secret", AzureCertificatePassword = "azure-pfx-secret",
        };
        var output = JsonSerializer.Serialize(create) + create + JsonSerializer.Serialize(runtime) + runtime;
        foreach (var secret in new[] { "ropc-secret", "app-secret", "pfx-secret", "github-secret", "ado-secret", "azure-secret", "azure-pfx-secret" })
            Assert.DoesNotContain(secret, output);
    }

    internal static ProfileTokenResult WorkloadToken(string? objectId = null) => new()
    {
        TenantId = Tenant,
        AccessToken = Jwt(new { tid = Tenant, appid = App, oid = objectId ?? ObjectId, aud = "https://service.test", idtyp = "app" }),
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Fact]
    public async Task DacSourceAndMandatoryBindingSurviveAnotherProviderWithoutFreezingUnrelatedRuntimeSettings()
    {
        using var h = new Harness();
        h.Acquisition.Next = DacToken(ProfileAzureSourceId.EnvironmentClientSecret);
        var stage = await h.Provider.AuthenticateAsync(new() { ManagedIdentity = true, Scopes = ["https://service.test/.default"] }, default);
        try
        {
            h.Profiles.Items.Add(stage);
            await h.Provider.CompleteCreationAsync(stage, default);
            var renewed = h.NewProvider(new() { GitHubRequestToken = "rotated-unrelated", AzureClientSecret = "same-identity-rotation" });
            await renewed.AcquireAsync(stage, ["https://service.test/.default"], null, default);
            Assert.Equal(ProfileAzureSourceId.EnvironmentClientSecret, h.Acquisition.Requests.Last().PinnedAzureSource);
            Assert.Null(stage.HomeAccountId);
            Assert.Null(stage.AuthenticatedTenantId);
            Assert.Null(stage.PrincipalObjectId);
            h.Acquisition.Next = DacToken(ProfileAzureSourceId.ManagedIdentity);
            await Assert.ThrowsAsync<AuthenticationException>(() => renewed.AcquireAsync(stage, ["https://service.test/.default"], null, default));
        }
        finally { h.Profiles.Items.Clear(); await h.Provider.RemoveOwnedCredentialAsync(stage, default); }
    }

    [Theory]
    [InlineData("method")]
    [InlineData("cloud")]
    [InlineData("ring")]
    [InlineData("client")]
    [InlineData("tenant")]
    [InlineData("principal")]
    [InlineData("authority")]
    public async Task ProtectedCredentialBindingRejectsEditedSnapshotBeforeTokenAcquisition(string edit)
    {
        using var h = new Harness();
        h.Acquisition.Next = WorkloadToken();
        var stage = await h.Provider.AuthenticateAsync(new() { Tenant = Tenant, ApplicationId = App, ClientSecret = "owned", Scopes = ["https://service.test/.default"] }, default);
        try
        {
            var edited = edit switch
            {
                "method" => stage with { CredentialMethod = AuthCredentialMethod.GitHubFederated },
                "cloud" => stage with { Cloud = CloudInstance.China },
                "ring" => stage with { Ring = PowerPlatformApiRing.Test },
                "client" => stage with { ClientId = ObjectId },
                "tenant" => stage with { TenantId = ObjectId },
                "principal" => stage with { PrincipalType = AuthPrincipalType.User },
                _ => stage with { Authority = "https://unrelated.test/" + Tenant },
            };
            await Assert.ThrowsAnyAsync<AuthenticationException>(() => h.Provider.AcquireAsync(edited, ["https://service.test/.default"], null, default));
            Assert.Single(h.Acquisition.Requests);
        }
        finally { await h.Provider.RemoveOwnedCredentialAsync(stage, default); }
    }

    [Fact]
    public async Task DacDeveloperApplicationPrincipalHeadlessRejectsBeforeAcquisition()
    {
        using var h = new Harness();
        h.Acquisition.Next = DacToken(ProfileAzureSourceId.AzureCli);
        var stage = await h.Provider.AuthenticateAsync(new() { ManagedIdentity = true, Scopes = ["https://service.test/.default"] }, default);
        try
        {
            Assert.Equal(AuthPrincipalType.ServicePrincipal, stage.PrincipalType);
            h.Profiles.Items.Add(stage);
            await h.Provider.CompleteCreationAsync(stage, default);
            await Assert.ThrowsAsync<InteractionRequiredException>(() => h.NewProvider(interaction: false).AcquireAsync(stage, ["https://service.test/.default"], null, default));
            Assert.Single(h.Acquisition.Requests);
        }
        finally { h.Profiles.Items.Clear(); await h.Provider.RemoveOwnedCredentialAsync(stage, default); }
    }

    [Fact]
    public async Task IndeterminateWriteAndFailedCleanupPreservePrimaryErrorAndRepairInventoryForRetry()
    {
        using var h = new Harness();
        h.Backend.FailWriteAfterCommit = true; h.Backend.FailClear = true;
        var failure = await Assert.ThrowsAnyAsync<AuthenticationException>(() => h.Provider.AuthenticateAsync(
            new() { Tenant = Tenant, ApplicationId = App, ClientSecret = "protected-only", Scopes = ["https://service.test/.default"] }, default));
        Assert.Equal(ErrorCodes.Authentication.CacheProtectionUnavailable, failure.ErrorCode);
        Assert.NotEmpty(h.Backend.Values);
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        h.Backend.FailClear = false;
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
        Assert.Empty(h.Backend.Values);
        Assert.Empty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CanceledAcquisitionWithFailedCleanupRetainsCancellationAndRepairGuidance()
    {
        using var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Acquisition.BeforeAcquire = _ => { h.Backend.FailClear = true; cancellation.Cancel(); return Task.CompletedTask; };
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Provider.AuthenticateAsync(new(), cancellation.Token));
        Assert.True(failure.Data.Contains("msagent.owned-cleanup"));
        Assert.NotEmpty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
        h.Backend.FailClear = false;
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
    }

    [Fact]
    public async Task CommittedReplacementFinalizationCancellationReleasesReferenceAndReportsCommit()
    {
        using var h = new Harness();
        var stage = await h.Provider.AuthenticateAsync(new(), default);
        var committed = stage with { ProfileId = "preserved-old-id", Revision = 3 };
        h.Profiles.Items.Add(committed);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Provider.CompleteCreationAsync(committed, canceled.Token));
        Assert.Equal(true, failure.Data["msagent.metadata-committed"]);
        h.Profiles.Items.Clear();
        await h.NewProvider().ClearOwnedCredentialsAsync(default);
        Assert.Empty(Directory.GetFiles(h.Directory, "*.marker", SearchOption.AllDirectories));
    }

    internal static ProfileTokenResult DacToken(ProfileAzureSourceId source, string? oid = null) => new()
    {
        TenantId = Tenant, AccessToken = WorkloadToken(oid).AccessToken, AzureSource = source,
    };

    internal static string Jwt(object claims) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    internal sealed class Harness : IDisposable
    {
        internal string Directory { get; } = Path.Combine(System.Environment.CurrentDirectory, "credential-test-" + Guid.NewGuid().ToString("N"));
        internal Backend Backend { get; } = new();
        internal Acquisition Acquisition { get; } = new();
        internal Profiles Profiles { get; } = new();
        private readonly AgentHostAuthOptions _options;
        internal ProfileCredentialProvider Provider { get; }
        internal Harness(bool interaction = true)
        {
            System.IO.Directory.CreateDirectory(Directory);
            _options = new AgentHostAuthOptions { ConfigDirectory = Directory, InteractionAllowed = interaction, AllowUnprotectedCache = true };
            Provider = NewProvider();
        }
        internal ProfileCredentialProvider NewProvider(ProfileCredentialRuntimeOptions? runtime = null, bool? interaction = null) =>
            new(_options with { InteractionAllowed = interaction ?? _options.InteractionAllowed }, Profiles, Backend, Acquisition, FakePlatformInfo.Unknown(), runtime);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    internal sealed class Backend : IOwnedProtectedStorageBackend
    {
        internal Dictionary<string, byte[]> Values { get; } = [];
        internal int Calls;
        internal bool FailClear = false;
        internal Exception? ClearFailure;
        internal bool FailWriteAfterCommit = false;
        internal Action<OwnedProtectedStorageReference>? AfterWrite;
        internal Action? AfterClear;
        private static string Key(OwnedProtectedStorageReference r) => $"{r.Kind}:{r.OpaqueId}";
        public Task<byte[]?> ReadAsync(OwnedProtectedStorageReference r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(Values.TryGetValue(Key(r), out var value) ? value.ToArray() : null);
        }
        public Task WriteAsync(OwnedProtectedStorageReference r, ReadOnlyMemory<byte> value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; Values[Key(r)] = value.ToArray();
            AfterWrite?.Invoke(r);
            if (FailWriteAfterCommit) throw new CacheProtectionUnavailableException("safe-native");
            return Task.CompletedTask;
        }
        public Task ClearAsync(OwnedProtectedStorageReference r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (ClearFailure is not null) throw ClearFailure;
            if (FailClear) throw new CacheProtectionUnavailableException("safe-native");
            Values.Remove(Key(r)); AfterClear?.Invoke(); return Task.CompletedTask;
        }
    }

    internal sealed class Acquisition : IProfileTokenAcquisition
    {
        internal List<ProfileTokenRequest> Requests { get; } = [];
        internal List<ProfileTokenRequest> Reads { get; } = [];
        internal int Validations;
        internal ProfileTokenResult Next = new() { AccessToken = "opaque-user-token", HomeAccountId = Home, Username = "alice", TenantId = Tenant };
        internal Exception? Failure;
        internal Func<ProfileTokenRequest, Task>? BeforeAcquire;
        public void Validate(ProfileTokenRequest request) => Validations++;
        public async Task<ProfileTokenResult> AcquireAsync(ProfileTokenRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (BeforeAcquire is not null) await BeforeAcquire(request);
            ct.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Next;
        }
        public Task<IReadOnlyList<AuthAccount>> GetCachedAccountsAsync(ProfileTokenRequest request, CancellationToken ct)
        {
            Reads.Add(request);
            return Task.FromResult<IReadOnlyList<AuthAccount>>([new AuthAccount { HomeAccountId = Home, Username = "alice" }]);
        }
    }

    internal sealed class Profiles : IAuthProfileStore
    {
        internal List<AuthProfile> Items { get; } = [];
        internal int Writes;
        internal Exception? ReadFailure;
        public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken ct) =>
            ReadFailure is null ? Task.FromResult<IReadOnlyList<AuthProfile>>(Items.ToArray()) : Task.FromException<IReadOnlyList<AuthProfile>>(ReadFailure);
        public Task<AuthProfile?> GetActiveProfileAsync(CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(p => p.IsActive));
        public Task UpsertAsync(AuthProfile p, CancellationToken ct) { Writes++; Items.Add(p); return Task.CompletedTask; }
        public Task SetActiveAsync(string key, CancellationToken ct) { Writes++; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct) { Writes++; Items.Clear(); return Task.CompletedTask; }
    }
}
